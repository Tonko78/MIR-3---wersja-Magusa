using AccountPortal.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mir3.Web.Data;
using Mir3.Web.Domain;
using Mir3.Web.Options;

namespace Mir3.Web.Services;

public sealed class RegistrationOutboxProcessor(
    IDbContextFactory<PortalDbContext> dbContextFactory,
    EmailVerificationService emailVerification,
    AccountQueueClient queueClient,
    ILogger<RegistrationOutboxProcessor> logger)
{
    private const int BatchSize = 50;

    public async Task ProcessOnceAsync(CancellationToken cancellationToken = default)
    {
        await ProcessQueueOnceAsync(cancellationToken);
        await ProcessEmailOnceAsync(cancellationToken);
    }

    public async Task ProcessQueueOnceAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var queueIds = await context.Registrations
            .AsNoTracking()
            .Where(registration =>
                registration.Status == RegistrationStatus.QueuePending &&
                (registration.QueuePublicationState == QueuePublicationState.Pending ||
                 registration.QueuePublicationState == QueuePublicationState.Uncertain))
            .OrderBy(registration => registration.UpdatedUtc)
            .Select(registration => registration.Id)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        foreach (var registrationId in queueIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await PublishQueueAsync(registrationId, cancellationToken);
        }
    }

    public async Task ProcessEmailOnceAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var emailIds = await context.Registrations
            .AsNoTracking()
            .Where(registration =>
                registration.Status == RegistrationStatus.PendingEmail &&
                registration.InitialEmailDeliveryPending)
            .OrderBy(registration => registration.CreatedUtc)
            .Select(registration => registration.Id)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        foreach (var registrationId in emailIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await DeliverInitialEmailAsync(registrationId, cancellationToken);
        }
    }

    public async Task PublishQueueAsync(Guid registrationId, CancellationToken cancellationToken = default)
    {
        QueueIntent? intent;
        await using (var context = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            intent = await context.Registrations
                .AsNoTracking()
                .Where(registration =>
                    registration.Id == registrationId &&
                    registration.Status == RegistrationStatus.QueuePending &&
                    (registration.QueuePublicationState == QueuePublicationState.Pending ||
                     registration.QueuePublicationState == QueuePublicationState.Uncertain) &&
                    registration.QueueRequestId != null &&
                    registration.QueueCommandType != null)
                .Select(registration => new QueueIntent(
                    registration.QueueRequestId!.Value,
                    registration.QueueCommandType!.Value,
                    registration.NormalizedEmail,
                    registration.QueueCommandType == AccountCommandType.ResetPassword
                        ? registration.PendingPasswordHash!
                        : registration.PasswordHash,
                    registration.QueuePublicationState!.Value))
                .SingleOrDefaultAsync(cancellationToken);
        }

        if (intent is null) return;
        if (intent.PublicationState == QueuePublicationState.Uncertain &&
            !queueClient.IsRequestDefinitelyAbsent(intent.RequestId))
        {
            return;
        }

        AccountQueuePublishResult publication;
        try
        {
            publication = await queueClient.WriteCommandAsync(
                intent.RequestId,
                intent.CommandType,
                intent.NormalizedEmail,
                intent.CommandType is AccountCommandType.CreateAccount or AccountCommandType.ResetPassword
                    ? intent.PasswordHash
                    : null,
                cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "Account queue publication {RequestId} was interrupted before durable publication.",
                intent.RequestId);
            return;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Account queue publication {RequestId} failed before durable publication.",
                intent.RequestId);
            return;
        }

        await using var updateContext = await dbContextFactory.CreateDbContextAsync(CancellationToken.None);
        await updateContext.Registrations
            .Where(registration =>
                registration.Id == registrationId &&
                registration.Status == RegistrationStatus.QueuePending &&
                registration.QueueRequestId == intent.RequestId &&
                registration.QueuePublicationState == intent.PublicationState)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(
                    registration => registration.QueuePublicationState,
                    publication.Outcome == AccountQueuePublishOutcome.Published
                        ? QueuePublicationState.Published
                        : QueuePublicationState.Uncertain),
                CancellationToken.None);
    }

    private async Task DeliverInitialEmailAsync(Guid registrationId, CancellationToken cancellationToken)
    {
        try
        {
            await emailVerification.SendAsync(registrationId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Initial verification delivery processing failed for registration {RegistrationId}.", registrationId);
            return;
        }

        await using var context = await dbContextFactory.CreateDbContextAsync(CancellationToken.None);
        await context.Registrations
            .Where(registration => registration.Id == registrationId && registration.InitialEmailDeliveryPending)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(registration => registration.InitialEmailDeliveryPending, false),
                CancellationToken.None);
    }

    private sealed record QueueIntent(
        Guid RequestId,
        AccountCommandType CommandType,
        string NormalizedEmail,
        byte[] PasswordHash,
        QueuePublicationState PublicationState);
}

public sealed class RegistrationOutboxWorker(
    RegistrationOutboxProcessor processor,
    IOptions<QueueOptions> options,
    ILogger<RegistrationOutboxWorker> logger,
    TimeProvider? timeProvider = null) : BackgroundService
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(options.Value.ResultPollSeconds);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var queuePublisher = RunLoopAsync(
            processor.ProcessQueueOnceAsync,
            "Account command outbox publication pass failed.",
            stoppingToken);
        var emailSender = RunLoopAsync(
            processor.ProcessEmailOnceAsync,
            "Registration email outbox delivery pass failed.",
            stoppingToken);

        await Task.WhenAll(queuePublisher, emailSender);
    }

    private async Task RunLoopAsync(
        Func<CancellationToken, Task> processOnce,
        string failureMessage,
        CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await processOnce(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, failureMessage);
            }

            try
            {
                await Task.Delay(_pollInterval, _timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
