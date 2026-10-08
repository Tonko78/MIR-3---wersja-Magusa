using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mir3.Web.Data;
using Mir3.Web.Domain;
using Mir3.Web.Options;

namespace Mir3.Web.Services;

public sealed class AccountQueueResultWorker(
    IDbContextFactory<PortalDbContext> dbContextFactory,
    AccountQueueClient queueClient,
    RegistrationStateMachine stateMachine,
    IOptions<QueueOptions> options,
    ILogger<AccountQueueResultWorker> logger,
    TimeProvider? timeProvider = null) : BackgroundService
{
    private const int BatchSize = 50;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(options.Value.ResultPollSeconds);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Account queue result reconciliation pass failed.");
            }

            await Task.Delay(_pollInterval, _timeProvider, stoppingToken);
        }
    }

    public async Task ProcessOnceAsync(CancellationToken cancellationToken = default)
    {
        var pending = await ClaimPendingBatchAsync(cancellationToken);

        foreach (var candidate in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AccountQueueReadResult read;
            try
            {
                read = await queueClient.ReadResultAsync(
                    candidate.RequestId,
                    candidate.NormalizedEmail,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Reading account queue result {RequestId} failed.",
                    candidate.RequestId);
                continue;
            }

            if (read.Status != AccountQueueReadStatus.Ready || read.Result is null) continue;
            await ApplyReadyResultAsync(candidate, read.Result, cancellationToken);
        }
    }

    private async Task<List<PendingRegistration>> ClaimPendingBatchAsync(CancellationToken cancellationToken)
    {
        await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var pendingQuery = context.Registrations.Where(registration =>
            registration.Status == RegistrationStatus.QueuePending &&
            registration.QueueRequestId != null &&
            registration.QueueCommandType != null);
        var latestCheckedUtc = await pendingQuery
            .MaxAsync(registration => registration.QueueLastCheckedUtc, cancellationToken);
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var checkedUtc = latestCheckedUtc is null || latestCheckedUtc < now
            ? now
            : latestCheckedUtc.Value.AddTicks(1);
        var candidates = await pendingQuery
            .AsNoTracking()
            .OrderBy(registration => registration.QueueLastCheckedUtc)
            .ThenBy(registration => registration.UpdatedUtc)
            .ThenBy(registration => registration.Id)
            .Take(BatchSize)
            .Select(registration => new PendingRegistration(
                registration.Id,
                registration.QueueRequestId!.Value,
                registration.NormalizedEmail,
                registration.QueueLastCheckedUtc))
            .ToListAsync(cancellationToken);

        var claimed = new List<PendingRegistration>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var updated = await context.Registrations
                .Where(registration =>
                    registration.Id == candidate.RegistrationId &&
                    registration.Status == RegistrationStatus.QueuePending &&
                    registration.QueueRequestId == candidate.RequestId &&
                    registration.QueueLastCheckedUtc == candidate.LastCheckedUtc)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(
                        registration => registration.QueueLastCheckedUtc,
                        checkedUtc),
                    cancellationToken);
            if (updated == 1) claimed.Add(candidate);
        }

        return claimed;
    }

    private async Task ApplyReadyResultAsync(
        PendingRegistration candidate,
        AccountPortal.Contracts.AccountCommandResult result,
        CancellationToken cancellationToken)
    {
        await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var claimed = await context.Registrations
            .Where(registration =>
                registration.Id == candidate.RegistrationId &&
                registration.Status == RegistrationStatus.QueuePending &&
                registration.QueueRequestId == candidate.RequestId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(
                    registration => registration.UpdatedUtc,
                    registration => registration.UpdatedUtc),
                cancellationToken);
        if (claimed == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return;
        }

        var registration = await context.Registrations.SingleAsync(
            item => item.Id == candidate.RegistrationId,
            cancellationToken);
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        try
        {
            stateMachine.ApplyResult(registration, result, now);
        }
        catch (RegistrationTransitionException exception)
        {
            await transaction.RollbackAsync(cancellationToken);
            logger.LogWarning(
                "Account queue result {RequestId} was not applicable: {Reason}",
                candidate.RequestId,
                exception.Message);
            return;
        }

        context.AuditEntries.Add(new AuditEntry
        {
            Actor = "account-queue",
            Action = "registration.queue-result",
            Target = $"registration:{registration.Id:D}",
            DetailsJson = JsonSerializer.Serialize(new
            {
                RequestId = candidate.RequestId,
                Status = result.Status.ToString(),
                Code = registration.QueueLastErrorCode ?? result.Code
            }),
            CreatedUtc = now
        });

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private sealed record PendingRegistration(
        Guid RegistrationId,
        Guid RequestId,
        string NormalizedEmail,
        DateTime? LastCheckedUtc);
}
