using System.Net;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mir3.Web.Data;
using Mir3.Web.Domain;
using Mir3.Web.Options;
using Mir3.Web.Security;

namespace Mir3.Web.Services;

public enum EmailVerificationSendOutcome
{
    Sent,
    NotFound,
    NotPending,
    Failed
}

public sealed record EmailVerificationSendResult(
    EmailVerificationSendOutcome Outcome,
    string? FailureCode = null);

public sealed class EmailVerificationService
{

    private static readonly TimeSpan DeliveryAttemptLease = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DeliveryAttemptPollDelay = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan DatabaseRetryDelay = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan DatabaseOperationDeadline = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan TransportLockDeadline = TimeSpan.FromSeconds(2);
    private const int DatabaseCommandTimeoutSeconds = 1;
    private const int DatabaseBusyTimeoutMilliseconds = 100;
    private const int MaximumDeliveryAttemptPolls = 6_000;
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(3)
    ];

    private readonly IDbContextFactory<PortalDbContext> _dbContextFactory;
    private readonly VerificationTransportLockManager _transportLocks;
    private readonly VerificationTokenService _tokenService;
    private readonly IEmailSender _emailSender;
    private readonly SmtpOptions _smtpOptions;
    private readonly ILogger<EmailVerificationService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly EmailVerificationServiceOperations _operations;

    public EmailVerificationService(
        IDbContextFactory<PortalDbContext> dbContextFactory,
        VerificationTransportLockManager transportLocks,
        VerificationTokenService tokenService,
        IEmailSender emailSender,
        IOptions<SmtpOptions> smtpOptions,
        ILogger<EmailVerificationService> logger,
        TimeProvider? timeProvider = null)
        : this(dbContextFactory, transportLocks, tokenService, emailSender, smtpOptions, logger, timeProvider, Task.Delay)
    {
    }

    internal EmailVerificationService(
        IDbContextFactory<PortalDbContext> dbContextFactory,
        VerificationTransportLockManager transportLocks,
        VerificationTokenService tokenService,
        IEmailSender emailSender,
        IOptions<SmtpOptions> smtpOptions,
        ILogger<EmailVerificationService> logger,
        TimeProvider? timeProvider,
        Func<TimeSpan, CancellationToken, Task> delay,
        EmailVerificationServiceOperations? operations = null)
    {
        _dbContextFactory = dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));
        _transportLocks = transportLocks ?? throw new ArgumentNullException(nameof(transportLocks));
        _tokenService = tokenService ?? throw new ArgumentNullException(nameof(tokenService));
        _emailSender = emailSender ?? throw new ArgumentNullException(nameof(emailSender));
        _smtpOptions = Validate(smtpOptions);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _delay = delay ?? throw new ArgumentNullException(nameof(delay));
        _operations = operations ?? EmailVerificationServiceOperations.Default;
    }

    public async Task<EmailVerificationSendResult> SendAsync(
        Guid registrationId,
        CancellationToken cancellationToken = default)
    {
        if (registrationId == Guid.Empty)
            throw new ArgumentException("The registration ID is required.", nameof(registrationId));

        cancellationToken.ThrowIfCancellationRequested();
        while (true)
        {
            DeliveryPreparation preparation;
            try
            {
                preparation = await ExecuteDatabaseOperationAsync(
                    token => TryAcquireAndComposeOnceAsync(registrationId, token),
                    cancellationToken);
            }
            catch (DatabaseOperationDeadlineException)
            {
                return DatabaseFailureResult;
            }

            if (preparation.Result is not null)
                return preparation.Result;
            if (preparation.Message is null)
            {
                var coalesced = await WaitForDeliveryAttemptAsync(
                    registrationId,
                    preparation.AttemptId,
                    cancellationToken);
                if (coalesced is not null)
                    return coalesced;
                continue;
            }

            var releaseOwnership = true;
            var deliveryAttempted = false;
            string? releaseFailureCode = null;
            try
            {
                await using var transportLock = preparation.TransportLock ??
                    await AcquireTransportLockAsync(registrationId, cancellationToken);
                bool beganTransport;
                try
                {
                    beganTransport = await ExecuteDatabaseOperationAsync(
                        token => BeginDeliveryTransportOnceAsync(registrationId, preparation.AttemptId, token),
                        cancellationToken);
                }
                catch (DatabaseOperationDeadlineException)
                {
                    return DatabaseFailureResult;
                }

                if (!beganTransport)
                {
                    releaseOwnership = false;
                    var coalesced = await WaitForDeliveryAttemptAsync(
                        registrationId,
                        preparation.AttemptId,
                        cancellationToken);
                    if (coalesced is not null)
                        return coalesced;
                    continue;
                }

                var sendResult = await SendWithRetryAsync(preparation.Message, registrationId, cancellationToken);
                deliveryAttempted = true;
                try
                {
                    if (await ExecuteDatabaseOperationAsync(
                            token => CompleteDeliveryAttemptOnceAsync(
                                registrationId,
                                preparation.AttemptId,
                                sendResult.FailureCode,
                                token),
                            CancellationToken.None))
                    {
                        releaseOwnership = false;
                        return sendResult;
                    }
                }
                catch (DatabaseOperationDeadlineException)
                {
                    releaseOwnership = false;
                    return sendResult;
                }

                releaseOwnership = false;
                var currentResult = await WaitForDeliveryAttemptAsync(
                    registrationId,
                    preparation.AttemptId,
                    cancellationToken);
                if (currentResult is not null)
                    return currentResult;
            }
            catch (TransportLockDeadlineException)
            {
                releaseFailureCode = TransportCoordinationFailureResult.FailureCode;
                return TransportCoordinationFailureResult;
            }
            finally
            {
                if (releaseOwnership && !deliveryAttempted)
                    await ReleaseDeliveryAttemptBestEffortAsync(
                        registrationId,
                        preparation.AttemptId,
                        releaseFailureCode);
            }
        }
    }

    private async Task<DeliveryPreparation> TryAcquireAndComposeOnceAsync(
        Guid registrationId,
        CancellationToken cancellationToken)
    {
        var attemptId = Guid.NewGuid();
        var now = UtcNow;
        var staleBefore = now - DeliveryAttemptLease;
        await using var database = await CreateDatabaseContextAsync(cancellationToken);
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var existing = await database.Registrations
            .AsNoTracking()
            .Where(registration => registration.Id == registrationId)
            .Select(registration => new
            {
                registration.Status,
                registration.EmailDeliveryAttemptId,
                registration.EmailDeliveryAttemptAcquiredUtc
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (existing is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return DeliveryPreparation.Completed(EmailVerificationSendOutcome.NotFound);
        }

        if (existing.Status != RegistrationStatus.PendingEmail)
        {
            await transaction.RollbackAsync(cancellationToken);
            return DeliveryPreparation.Completed(EmailVerificationSendOutcome.NotPending);
        }

        if (existing.EmailDeliveryAttemptAcquiredUtc > staleBefore)
        {
            await transaction.RollbackAsync(cancellationToken);
            return existing.EmailDeliveryAttemptId is { } currentAttemptId
                ? DeliveryPreparation.Waiting(currentAttemptId)
                : DeliveryPreparation.Retry;
        }

        VerificationTransportLock? transportLock = null;
        if (existing.EmailDeliveryAttemptAcquiredUtc is not null)
        {
            transportLock = _transportLocks.TryAcquire(registrationId);
            if (transportLock is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return existing.EmailDeliveryAttemptId is { } currentAttemptId
                    ? DeliveryPreparation.Waiting(currentAttemptId)
                    : DeliveryPreparation.Retry;
            }
        }

        try
        {
            var claimed = await database.Registrations
                .Where(registration =>
                    registration.Id == registrationId &&
                    registration.Status == RegistrationStatus.PendingEmail &&
                    registration.EmailDeliveryAttemptId == existing.EmailDeliveryAttemptId &&
                    registration.EmailDeliveryAttemptAcquiredUtc == existing.EmailDeliveryAttemptAcquiredUtc)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(registration => registration.EmailDeliveryAttemptId, attemptId)
                        .SetProperty(registration => registration.EmailDeliveryAttemptAcquiredUtc, now)
                        .SetProperty(registration => registration.EmailLastErrorCode, (string?)null)
                        .SetProperty(registration => registration.UpdatedUtc, now),
                    cancellationToken);

            if (claimed == 0)
            {
                if (transportLock is not null)
                {
                    await transportLock.DisposeAsync();
                    transportLock = null;
                }
                await transaction.RollbackAsync(cancellationToken);
                var current = await database.Registrations
                    .AsNoTracking()
                    .Where(registration => registration.Id == registrationId)
                    .Select(registration => new
                    {
                        registration.Status,
                        registration.EmailDeliveryAttemptId,
                        registration.EmailDeliveryAttemptAcquiredUtc
                    })
                    .SingleOrDefaultAsync(cancellationToken);
                if (current is null)
                    return DeliveryPreparation.Completed(EmailVerificationSendOutcome.NotFound);
                if (current.Status != RegistrationStatus.PendingEmail)
                    return DeliveryPreparation.Completed(EmailVerificationSendOutcome.NotPending);
                if (current.EmailDeliveryAttemptId is { } ownerId &&
                    current.EmailDeliveryAttemptAcquiredUtc is not null)
                {
                    return DeliveryPreparation.Waiting(ownerId);
                }

                return DeliveryPreparation.Retry;
            }

            var registration = await database.Registrations.SingleAsync(
                item => item.Id == registrationId,
                cancellationToken);
            var token = _tokenService.Issue(registration);
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            var link = QueryHelpers.AddQueryString(
                _smtpOptions.PublicBaseUrl!.TrimEnd('/') + "/verify",
                "token",
                token);
            var statusLink = QueryHelpers.AddQueryString(
                _smtpOptions.PublicBaseUrl!.TrimEnd('/') + "/Status",
                "registration",
                registration.Id.ToString("D"));
            var encodedLink = WebUtility.HtmlEncode(link);
            var encodedStatusLink = WebUtility.HtmlEncode(statusLink);
            var message = registration.PreferredLanguage == "pl"
                ? new EmailMessage(
                    new EmailAddress(_smtpOptions.FromAddress, _smtpOptions.FromName),
                    new EmailAddress(registration.NormalizedEmail),
                    "Zweryfikuj adres e-mail — Mir3",
                    $"Zweryfikuj rejestrację Mir3 za pomocą tego linku:\n{link}\n\nSprawdź status rejestracji po weryfikacji:\n{statusLink}\n",
                    $"<p>Zweryfikuj rejestrację Mir3:</p><p><a href=\"{encodedLink}\">Zweryfikuj adres e-mail</a></p><p>Sprawdź status rejestracji: <a href=\"{encodedStatusLink}\">Otwórz stronę statusu</a></p>")
                : new EmailMessage(
                    new EmailAddress(_smtpOptions.FromAddress, _smtpOptions.FromName),
                    new EmailAddress(registration.NormalizedEmail),
                    "Mir3 e-mail verification",
                    $"Verify your Mir3 registration using this link:\n{link}\n\nCheck registration status here after verification:\n{statusLink}\n",
                    $"<p>Verify your Mir3 registration:</p><p><a href=\"{encodedLink}\">Verify e-mail address</a></p><p>Check registration status: <a href=\"{encodedStatusLink}\">Open registration status page</a></p>");
            var preparation = DeliveryPreparation.Owned(attemptId, message, transportLock);
            transportLock = null;
            return preparation;
        }
        catch
        {
            if (transportLock is not null)
                await transportLock.DisposeAsync();
            throw;
        }
    }

    private async Task<VerificationTransportLock> AcquireTransportLockAsync(
        Guid registrationId,
        CancellationToken cancellationToken)
    {
        var started = _timeProvider.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var transportLock = _transportLocks.TryAcquire(registrationId);
            if (transportLock is not null)
                return transportLock;
            if (_timeProvider.GetElapsedTime(started) >= TransportLockDeadline)
                throw new TransportLockDeadlineException();

            await _delay(DeliveryAttemptPollDelay, cancellationToken);
        }
    }


    private async Task<bool> BeginDeliveryTransportOnceAsync(
        Guid registrationId,
        Guid attemptId,
        CancellationToken cancellationToken)
    {
        await using var database = await CreateDatabaseContextAsync(cancellationToken);
        await _operations.BeforeBeginDeliveryTransport(cancellationToken);
        var now = UtcNow;
        var updated = await database.Registrations
            .Where(registration =>
                registration.Id == registrationId &&
                registration.Status == RegistrationStatus.PendingEmail &&
                registration.EmailDeliveryAttemptId == attemptId &&
                registration.EmailDeliveryAttemptAcquiredUtc != null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(registration => registration.EmailDeliveryAttemptAcquiredUtc, now)
                    .SetProperty(registration => registration.UpdatedUtc, now),
                cancellationToken);
        return updated == 1;
    }

    private async Task<EmailVerificationSendResult?> WaitForDeliveryAttemptAsync(
        Guid registrationId,
        Guid attemptId,
        CancellationToken cancellationToken)
    {
        for (var poll = 0; poll < MaximumDeliveryAttemptPolls; poll++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeliveryAttemptSnapshot? delivery;
            try
            {
                delivery = await ExecuteDatabaseOperationAsync(
                    token => ReadDeliveryAttemptOnceAsync(registrationId, token),
                    cancellationToken);
            }
            catch (DatabaseOperationDeadlineException)
            {
                return DatabaseFailureResult;
            }
            if (delivery is null)
                return new EmailVerificationSendResult(EmailVerificationSendOutcome.NotFound);
            if (delivery.Status != RegistrationStatus.PendingEmail)
                return new EmailVerificationSendResult(EmailVerificationSendOutcome.NotPending);

            if (delivery.EmailDeliveryAttemptId == attemptId)
            {
                if (delivery.EmailDeliveryAttemptAcquiredUtc is null)
                {
                    return delivery.EmailLastErrorCode is null
                        ? new EmailVerificationSendResult(EmailVerificationSendOutcome.Sent)
                        : null;
                }

                if (delivery.EmailDeliveryAttemptAcquiredUtc <= UtcNow - DeliveryAttemptLease)
                {
                    var transportLock = _transportLocks.TryAcquire(registrationId);
                    if (transportLock is not null)
                    {
                        await transportLock.DisposeAsync();
                        return null;
                    }
                }
            }
            else if (delivery.EmailDeliveryAttemptAcquiredUtc is null)
            {
                if (delivery.EmailDeliveryAttemptId is null)
                    return null;

                return delivery.EmailLastErrorCode is null
                    ? new EmailVerificationSendResult(EmailVerificationSendOutcome.Sent)
                    : null;
            }
            else
            {
                attemptId = delivery.EmailDeliveryAttemptId!.Value;
            }

            await _delay(DeliveryAttemptPollDelay, cancellationToken);
        }

        return new EmailVerificationSendResult(EmailVerificationSendOutcome.Failed, "smtp-send");
    }

    private async Task<DeliveryAttemptSnapshot?> ReadDeliveryAttemptOnceAsync(
        Guid registrationId,
        CancellationToken cancellationToken)
    {
        await using var database = await CreateDatabaseContextAsync(cancellationToken);
        return await database.Registrations
            .AsNoTracking()
            .Where(registration => registration.Id == registrationId)
            .Select(registration => new DeliveryAttemptSnapshot(
                registration.Status,
                registration.EmailDeliveryAttemptId,
                registration.EmailDeliveryAttemptAcquiredUtc,
                registration.EmailLastErrorCode))
            .SingleOrDefaultAsync(cancellationToken);
    }

    private async Task<EmailVerificationSendResult> SendWithRetryAsync(
        EmailMessage message,
        Guid registrationId,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await _emailSender.SendAsync(message, cancellationToken);
                _logger.LogInformation(
                    "Verification e-mail delivery completed for registration {RegistrationId}.",
                    registrationId);
                return new EmailVerificationSendResult(EmailVerificationSendOutcome.Sent);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (SmtpEmailException exception)
            {
                var finalAttempt = !exception.IsTransient || attempt == 2;
                _logger.LogWarning(
                    "Verification e-mail delivery attempt {Attempt} failed for registration {RegistrationId} with {FailureCode}.",
                    attempt + 1,
                    registrationId,
                    exception.SafeCode);
                if (finalAttempt)
                {
                    return new EmailVerificationSendResult(
                        EmailVerificationSendOutcome.Failed,
                        exception.SafeCode);
                }
            }
            catch
            {
                const string safeCode = "smtp-send";
                _logger.LogWarning(
                    "Verification e-mail delivery attempt {Attempt} failed for registration {RegistrationId} with {FailureCode}.",
                    attempt + 1,
                    registrationId,
                    safeCode);
                return new EmailVerificationSendResult(
                    EmailVerificationSendOutcome.Failed,
                    safeCode);
            }

            await _delay(RetryDelays[attempt], cancellationToken);
        }

        throw new InvalidOperationException("The bounded SMTP retry loop did not complete.");
    }

    private async Task<bool> CompleteDeliveryAttemptOnceAsync(
        Guid registrationId,
        Guid attemptId,
        string? failureCode,
        CancellationToken cancellationToken)
    {
        await using var database = await CreateDatabaseContextAsync(cancellationToken);
        var updated = await database.Registrations
            .Where(registration =>
                registration.Id == registrationId &&
                registration.EmailDeliveryAttemptId == attemptId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(registration => registration.EmailLastErrorCode, failureCode)
                    .SetProperty(registration => registration.EmailDeliveryAttemptAcquiredUtc, (DateTime?)null)
                    .SetProperty(registration => registration.UpdatedUtc, UtcNow),
                cancellationToken);
        return updated == 1;
    }

    private async Task ReleaseDeliveryAttemptOnceAsync(
        Guid registrationId,
        Guid attemptId,
        string? failureCode,
        CancellationToken cancellationToken)
    {
        await using var database = await CreateDatabaseContextAsync(cancellationToken);
        await database.Registrations
            .Where(registration =>
                registration.Id == registrationId &&
                registration.EmailDeliveryAttemptId == attemptId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(registration => registration.EmailDeliveryAttemptId, (Guid?)null)
                    .SetProperty(registration => registration.EmailDeliveryAttemptAcquiredUtc, (DateTime?)null)
                    .SetProperty(registration => registration.EmailLastErrorCode, failureCode)
                    .SetProperty(registration => registration.UpdatedUtc, UtcNow),
                cancellationToken);
    }

    private async Task ReleaseDeliveryAttemptBestEffortAsync(
        Guid registrationId,
        Guid attemptId,
        string? failureCode)
    {
        try
        {
            await ExecuteDatabaseOperationAsync(
                async token =>
                {
                    await ReleaseDeliveryAttemptOnceAsync(registrationId, attemptId, failureCode, token);
                    return true;
                },
                CancellationToken.None);
        }
        catch (Exception)
        {
            _logger.LogWarning(
                "Verification e-mail delivery ownership cleanup could not be persisted for registration {RegistrationId}.",
                registrationId);
        }
    }

    private async Task<T> ExecuteDatabaseOperationAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(DatabaseOperationDeadline);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await operation(linked.Token);
            }
            catch (Exception exception) when (IsContention(exception))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (deadline.IsCancellationRequested)
                    throw new DatabaseOperationDeadlineException();
            }
            catch (OperationCanceledException) when (
                deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new DatabaseOperationDeadlineException();
            }

            try
            {
                await Task.Delay(DatabaseRetryDelay, linked.Token);
            }
            catch (OperationCanceledException) when (
                deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new DatabaseOperationDeadlineException();
            }
        }
    }

    private async Task<PortalDbContext> CreateDatabaseContextAsync(CancellationToken cancellationToken)
    {
        var database = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        try
        {
            database.Database.SetCommandTimeout(DatabaseCommandTimeoutSeconds);
            if (database.Database.GetDbConnection() is SqliteConnection connection)
                connection.DefaultTimeout = DatabaseCommandTimeoutSeconds;
            await database.Database.OpenConnectionAsync(cancellationToken);
            await database.Database.ExecuteSqlRawAsync(
                $"PRAGMA busy_timeout = {DatabaseBusyTimeoutMilliseconds}",
                cancellationToken);
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    private static bool IsContention(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqliteException { SqliteErrorCode: 5 or 6 }) return true;
        }

        return false;
    }

    private static EmailVerificationSendResult DatabaseFailureResult { get; } =
        new(EmailVerificationSendOutcome.Failed, "smtp-send");

    private static EmailVerificationSendResult TransportCoordinationFailureResult { get; } =
        new(EmailVerificationSendOutcome.Failed, "smtp-send");

    private static SmtpOptions Validate(IOptions<SmtpOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var value = options.Value;
        var validation = new SmtpOptionsValidator().Validate(Microsoft.Extensions.Options.Options.DefaultName, value);
        if (validation.Failed)
        {
            throw new OptionsValidationException(
                Microsoft.Extensions.Options.Options.DefaultName,
                typeof(SmtpOptions),
                validation.Failures);
        }

        return value;
    }

    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    private sealed class DatabaseOperationDeadlineException : Exception;

    private sealed class TransportLockDeadlineException : Exception;

    private sealed record DeliveryAttemptSnapshot(
        RegistrationStatus Status,
        Guid? EmailDeliveryAttemptId,
        DateTime? EmailDeliveryAttemptAcquiredUtc,
        string? EmailLastErrorCode);

    private sealed record DeliveryPreparation(
        Guid AttemptId,
        EmailVerificationSendResult? Result,
        EmailMessage? Message,
        VerificationTransportLock? TransportLock)
    {
        public static DeliveryPreparation Retry { get; } = new(Guid.Empty, null, null, null);

        public static DeliveryPreparation Completed(EmailVerificationSendOutcome outcome) =>
            new(Guid.Empty, new EmailVerificationSendResult(outcome), null, null);

        public static DeliveryPreparation Waiting(Guid attemptId) => new(attemptId, null, null, null);

        public static DeliveryPreparation Owned(
            Guid attemptId,
            EmailMessage message,
            VerificationTransportLock? transportLock) =>
            new(attemptId, null, message, transportLock);
    }
}

internal sealed class EmailVerificationServiceOperations
{
    internal static EmailVerificationServiceOperations Default { get; } = new();

    internal Func<CancellationToken, Task> BeforeBeginDeliveryTransport { get; init; } =
        static _ => Task.CompletedTask;
}
