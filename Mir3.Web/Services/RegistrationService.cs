using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Mir3.Web.Data;
using Mir3.Web.Domain;
using Mir3.Web.Localization;
using Mir3.Web.Security;

namespace Mir3.Web.Services;

public static class RegistrationMessages
{
    public const string Created = "Registration request received. If the address can be registered, a verification e-mail will be sent. Check your inbox and follow the instructions in the message. If nothing arrives, please try again later.";
    public const string VerificationInvalid = "This verification link is invalid, expired, or has already been used.";
    public const string VerificationSucceeded = "Your e-mail address has been verified.";
    public const string StatusUnavailable = "Registration status is unavailable.";
    public const string ResendAccepted = "If this registration can receive another message, a verification message will be sent.";
}

public enum RegistrationCreateOutcome
{
    Created,
    Conflict
}

public sealed record RegistrationCreateResult(
    RegistrationCreateOutcome Outcome,
    string UserMessage,
    Guid? RegistrationReference = null);

public enum RegistrationVerificationOutcome
{
    Verified,
    Invalid
}

public sealed record RegistrationVerificationResult(
    RegistrationVerificationOutcome Outcome,
    string UserMessage,
    Guid? RegistrationReference = null,
    bool CanResend = false);

public enum RegistrationStatusOutcome
{
    Found,
    NotFound
}

public sealed record RegistrationStatusResult(
    RegistrationStatusOutcome Outcome,
    string PublicStatus,
    string UserMessage,
    string? Email = null);

public interface IRegistrationService
{
    Task<RegistrationCreateResult> RegisterAsync(
        string email,
        string password,
        IPAddress sourceIp,
        CancellationToken cancellationToken = default);

    Task<RegistrationVerificationResult> VerifyAsync(
        string? token,
        CancellationToken cancellationToken = default);

    Task<RegistrationStatusResult> GetStatusAsync(
        Guid registrationReference,
        CancellationToken cancellationToken = default);

    Task ResendVerificationAsync(
        Guid registrationReference,
        CancellationToken cancellationToken = default);
}

public sealed class RegistrationService : IRegistrationService
{
    private static readonly byte[] IpHashDomain = Encoding.UTF8.GetBytes("mir3-registration-source-ip\0");

    private readonly IDbContextFactory<PortalDbContext> _dbContextFactory;
    private readonly GamePasswordHasher _passwordHasher;
    private readonly EmailVerificationService _emailVerification;
    private readonly RegistrationStateMachine _stateMachine;
    private readonly AccountQueueClient _queueClient;
    private readonly RegistrationOutboxProcessor _outbox;
    private readonly ILogger<RegistrationService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly RegistrationServiceOperations _operations;

    public RegistrationService(
        IDbContextFactory<PortalDbContext> dbContextFactory,
        GamePasswordHasher passwordHasher,
        EmailVerificationService emailVerification,
        RegistrationStateMachine stateMachine,
        AccountQueueClient queueClient,
        RegistrationOutboxProcessor outbox,
        ILogger<RegistrationService> logger,
        TimeProvider? timeProvider = null)
        : this(
            dbContextFactory,
            passwordHasher,
            emailVerification,
            stateMachine,
            queueClient,
            outbox,
            logger,
            timeProvider,
            null)
    {
    }

    internal RegistrationService(
        IDbContextFactory<PortalDbContext> dbContextFactory,
        GamePasswordHasher passwordHasher,
        EmailVerificationService emailVerification,
        RegistrationStateMachine stateMachine,
        AccountQueueClient queueClient,
        RegistrationOutboxProcessor outbox,
        ILogger<RegistrationService> logger,
        TimeProvider? timeProvider,
        RegistrationServiceOperations? operations)
    {
        _dbContextFactory = dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));
        _passwordHasher = passwordHasher ?? throw new ArgumentNullException(nameof(passwordHasher));
        _emailVerification = emailVerification ?? throw new ArgumentNullException(nameof(emailVerification));
        _stateMachine = stateMachine ?? throw new ArgumentNullException(nameof(stateMachine));
        _queueClient = queueClient ?? throw new ArgumentNullException(nameof(queueClient));
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _operations = operations ?? RegistrationServiceOperations.Default;
    }

    public async Task<RegistrationCreateResult> RegisterAsync(
        string email,
        string password,
        IPAddress sourceIp,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(sourceIp);

        var normalizedEmail = NormalizeEmail(email);
        var preferredLanguage = PortalText.Pick("en", "pl");
        var now = UtcNow;
        var passwordHash = _passwordHasher.Hash(password);
        var sourceIpHash = HashSourceIp(sourceIp);
        var invalidatedTokenHash = RandomNumberGenerator.GetBytes(VerificationTokenService.TokenByteCount);
        var registration = new Registration
        {
            Id = Guid.NewGuid(),
            Email = normalizedEmail,
            NormalizedEmail = normalizedEmail,
            PreferredLanguage = preferredLanguage,
            PasswordHash = passwordHash,
            Status = RegistrationStatus.PendingEmail,
            VerificationTokenHash = invalidatedTokenHash,
            VerificationExpiresUtc = now.Add(VerificationTokenService.TokenLifetime),
            CreatedUtc = now,
            UpdatedUtc = now,
            SourceIpHash = sourceIpHash,
            InitialEmailDeliveryPending = true
        };

        await using var database = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var replaced = await database.Registrations
            .Where(item =>
                item.NormalizedEmail == normalizedEmail &&
                item.Status == RegistrationStatus.PendingEmail &&
                item.EmailVerifiedUtc == null &&
                item.VerificationUsedUtc == null &&
                item.VerificationExpiresUtc <= now)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(item => item.PasswordHash, passwordHash)
                    .SetProperty(item => item.PreferredLanguage, preferredLanguage)
                    .SetProperty(item => item.VerificationTokenHash, invalidatedTokenHash)
                    .SetProperty(item => item.VerificationExpiresUtc, now.Add(VerificationTokenService.TokenLifetime))
                    .SetProperty(item => item.VerificationUsedUtc, (DateTime?)null)
                    .SetProperty(item => item.EmailVerifiedUtc, (DateTime?)null)
                    .SetProperty(item => item.EmailLastErrorCode, (string?)null)
                    .SetProperty(item => item.InitialEmailDeliveryPending, true)
                    .SetProperty(item => item.EmailDeliveryAttemptId, (Guid?)null)
                    .SetProperty(item => item.EmailDeliveryAttemptAcquiredUtc, (DateTime?)null)
                    .SetProperty(item => item.SourceIpHash, sourceIpHash)
                    .SetProperty(item => item.UpdatedUtc, now),
                cancellationToken);
        if (replaced == 1)
        {
            return Created();
        }

        await database.Registrations
            .Where(item =>
                item.NormalizedEmail == normalizedEmail &&
                item.Status == RegistrationStatus.PendingEmail &&
                item.EmailVerifiedUtc == null &&
                item.VerificationUsedUtc == null &&
                item.EmailLastErrorCode != null &&
                item.EmailDeliveryAttemptAcquiredUtc == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(item => item.InitialEmailDeliveryPending, true)
                    .SetProperty(item => item.PreferredLanguage, preferredLanguage)
                    .SetProperty(item => item.EmailDeliveryAttemptId, (Guid?)null)
                    .SetProperty(item => item.UpdatedUtc, now),
                cancellationToken);

        if (await database.Registrations.AnyAsync(
                item => item.NormalizedEmail == normalizedEmail,
                cancellationToken))
        {
            return Conflict();
        }

        database.Registrations.Add(registration);
        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueConstraint(exception))
        {
            return Conflict();
        }

        return Created();
    }

    public async Task<RegistrationVerificationResult> VerifyAsync(
        string? token,
        CancellationToken cancellationToken = default)
    {
        if (!TryHashToken(token, out var tokenHash))
        {
            return InvalidVerification;
        }

        try
        {
            await using var database = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            var now = UtcNow;
            var registration = await database.Registrations.SingleOrDefaultAsync(
                item => item.VerificationTokenHash == tokenHash,
                cancellationToken);
            if (registration is null ||
                !CryptographicOperations.FixedTimeEquals(registration.VerificationTokenHash, tokenHash))
            {
                await transaction.RollbackAsync(cancellationToken);
                return InvalidVerification;
            }

            if (registration.Status != RegistrationStatus.PendingEmail ||
                registration.VerificationUsedUtc is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return InvalidVerification;
            }

            if (registration.VerificationExpiresUtc <= now)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new RegistrationVerificationResult(
                    RegistrationVerificationOutcome.Invalid,
                    RegistrationMessages.VerificationInvalid,
                    registration.Id,
                    CanResend: true);
            }

            var autoActivate = await database.PortalSettings
                .Where(setting => setting.Id == PortalSetting.SingletonId)
                .Select(setting => setting.AutoActivateAfterEmailVerification)
                .SingleAsync(cancellationToken);
            var queueRequestId = autoActivate ? Guid.NewGuid() : (Guid?)null;
            _stateMachine.Verify(registration, autoActivate, queueRequestId, now);
            registration.QueuePublicationState = autoActivate ? QueuePublicationState.Pending : null;
            await database.SaveChangesAsync(cancellationToken);
            await _operations.BeforeVerificationCommit(cancellationToken);
            await transaction.CommitAsync(CancellationToken.None);

            if (autoActivate)
            {
                try
                {
                    await _operations.AfterVerificationCommit(CancellationToken.None);
                    await _outbox.PublishQueueAsync(registration.Id, CancellationToken.None);
                }
                catch (Exception)
                {
                    _logger.LogWarning(
                        "Post-commit account queue publication kick failed for registration {RegistrationId}; durable outbox recovery remains pending.",
                        registration.Id);
                }
            }

            return new RegistrationVerificationResult(
                RegistrationVerificationOutcome.Verified,
                RegistrationMessages.VerificationSucceeded,
                registration.Id);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(tokenHash);
        }
    }

    public async Task<RegistrationStatusResult> GetStatusAsync(
        Guid registrationReference,
        CancellationToken cancellationToken = default)
    {
        if (registrationReference == Guid.Empty)
        {
            return MissingStatus;
        }

        await using var database = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var registration = await database.Registrations
            .AsNoTracking()
            .Where(registration => registration.Id == registrationReference)
            .Select(registration => new
            {
                registration.Status,
                registration.NormalizedEmail
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (registration is null)
        {
            return MissingStatus;
        }

        var publicStatus = registration.Status switch
        {
            RegistrationStatus.PendingEmail => "Waiting for e-mail verification",
            RegistrationStatus.AwaitingAdmin => "Waiting for approval",
            RegistrationStatus.QueuePending => "Processing",
            RegistrationStatus.Active => "Active",
            RegistrationStatus.Disabled => "Unavailable",
            RegistrationStatus.Failed => "Processing delayed",
            _ => "Unavailable"
        };
        return new RegistrationStatusResult(
            RegistrationStatusOutcome.Found,
            publicStatus,
            $"Registration status: {publicStatus}.",
            registration.NormalizedEmail);
    }

    public async Task ResendVerificationAsync(
        Guid registrationReference,
        CancellationToken cancellationToken = default)
    {
        if (registrationReference == Guid.Empty)
        {
            return;
        }

        await _emailVerification.SendAsync(registrationReference, cancellationToken);
    }

    public static string NormalizeEmail(string email)
    {
        var normalized = email.Trim().ToLowerInvariant();
        if (normalized.Length is 0 or > 320)
        {
            throw new ArgumentException("A valid e-mail address is required.", nameof(email));
        }

        return normalized;
    }

    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    private static RegistrationCreateResult Conflict() => new(
        RegistrationCreateOutcome.Conflict,
        RegistrationMessages.Created);

    private static RegistrationCreateResult Created() => new(
        RegistrationCreateOutcome.Created,
        RegistrationMessages.Created);

    private static RegistrationVerificationResult InvalidVerification => new(
        RegistrationVerificationOutcome.Invalid,
        RegistrationMessages.VerificationInvalid);

    private static RegistrationStatusResult MissingStatus => new(
        RegistrationStatusOutcome.NotFound,
        "Unavailable",
        RegistrationMessages.StatusUnavailable);

    private static byte[] HashSourceIp(IPAddress sourceIp)
    {
        var canonical = sourceIp.IsIPv4MappedToIPv6 ? sourceIp.MapToIPv4() : sourceIp;
        var address = Encoding.UTF8.GetBytes(canonical.ToString());
        try
        {
            var input = new byte[IpHashDomain.Length + address.Length];
            IpHashDomain.CopyTo(input, 0);
            address.CopyTo(input, IpHashDomain.Length);
            try
            {
                return SHA256.HashData(input);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(input);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(address);
        }
    }

    private static bool TryHashToken(string? token, out byte[] hash)
    {
        hash = [];
        if (string.IsNullOrEmpty(token) || token.Length != 43)
        {
            return false;
        }

        byte[] raw;
        try
        {
            raw = WebEncoders.Base64UrlDecode(token);
        }
        catch (FormatException)
        {
            return false;
        }

        try
        {
            if (raw.Length != VerificationTokenService.TokenByteCount)
            {
                return false;
            }

            hash = SHA256.HashData(raw);
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(raw);
        }
    }

    private static bool IsUniqueConstraint(DbUpdateException exception) =>
        exception.InnerException is SqliteException { SqliteErrorCode: 19 };
}

internal sealed class RegistrationServiceOperations
{
    internal static RegistrationServiceOperations Default { get; } = new();

    internal Func<CancellationToken, Task> BeforeVerificationCommit { get; init; } =
        _ => Task.CompletedTask;

    internal Func<CancellationToken, Task> AfterVerificationCommit { get; init; } =
        _ => Task.CompletedTask;
}
