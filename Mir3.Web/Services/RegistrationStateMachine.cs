using AccountPortal.Contracts;
using Mir3.Web.Domain;

namespace Mir3.Web.Services;

public sealed class RegistrationStateMachine
{
    private static readonly HashSet<string> SafeErrorCodes = new(StringComparer.Ordinal)
    {
        "account-not-found",
        "execution-failed",
        "expired-request",
        "future-request",
        "id-mismatch",
        "invalid-command",
        "invalid-signature",
        "malformed-request",
        "queue-failed"
    };

    public AccountCommandType? Verify(
        Registration registration,
        bool autoActivate,
        Guid? queueRequestId,
        DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(registration);
        EnsureStatus(registration, RegistrationStatus.PendingEmail, "verify");
        EnsureUtc(utcNow);
        if (autoActivate) EnsureRequestId(queueRequestId);
        else if (queueRequestId is not null) Throw(registration, "verify without automatic activation cannot have a queue request");

        registration.EmailVerifiedUtc = utcNow;
        registration.VerificationUsedUtc = utcNow;
        registration.QueueLastErrorCode = null;
        registration.UpdatedUtc = utcNow;

        if (!autoActivate)
        {
            registration.Status = RegistrationStatus.AwaitingAdmin;
            registration.QueueRequestId = null;
            registration.QueueCommandType = null;
            registration.QueuePublicationState = null;
            return null;
        }

        Queue(registration, queueRequestId!.Value, AccountCommandType.CreateAccount);
        return AccountCommandType.CreateAccount;
    }

    public AccountCommandType AdminActivate(
        Registration registration,
        Guid queueRequestId,
        DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(registration);
        EnsureUtc(utcNow);
        EnsureRequestId(queueRequestId);

        var commandType = registration.Status switch
        {
            RegistrationStatus.AwaitingAdmin => AccountCommandType.CreateAccount,
            RegistrationStatus.Disabled => AccountCommandType.ActivateAccount,
            _ => throw Exception(registration, "admin activation")
        };

        if (registration.EmailVerifiedUtc is null)
        {
            throw Exception(registration, "admin activation requires verified e-mail");
        }

        registration.UpdatedUtc = utcNow;
        Queue(registration, queueRequestId, commandType);
        return commandType;
    }

    public AccountCommandType AdminDeactivate(
        Registration registration,
        Guid queueRequestId,
        DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(registration);
        EnsureStatus(registration, RegistrationStatus.Active, "admin deactivation");
        EnsureUtc(utcNow);
        EnsureRequestId(queueRequestId);

        registration.UpdatedUtc = utcNow;
        Queue(registration, queueRequestId, AccountCommandType.DeactivateAccount);
        return AccountCommandType.DeactivateAccount;
    }

    public AccountCommandType AdminResetPassword(
        Registration registration,
        byte[] replacementHash,
        Guid queueRequestId,
        DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(replacementHash);
        EnsureStatus(registration, RegistrationStatus.Active, "admin password reset");
        EnsureUtc(utcNow);
        EnsureRequestId(queueRequestId);
        if (replacementHash.Length != 36)
        {
            throw new RegistrationTransitionException("A Zircon password hash must be 36 bytes.");
        }

        registration.PendingPasswordHash = replacementHash.ToArray();
        registration.UpdatedUtc = utcNow;
        Queue(registration, queueRequestId, AccountCommandType.ResetPassword);
        return AccountCommandType.ResetPassword;
    }

    public void ApplyResult(
        Registration registration,
        AccountCommandResult result,
        DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(result);
        EnsureStatus(registration, RegistrationStatus.QueuePending, "queue result");
        EnsureUtc(utcNow);

        var requestId = registration.QueueRequestId;
        var pendingCommandType = registration.QueueCommandType;
        if (requestId is null || requestId == Guid.Empty || pendingCommandType is null)
        {
            throw Exception(registration, "queue result requires pending command correlation");
        }

        if (result.Id != requestId.Value)
        {
            Throw(registration, "queue result identity does not match the pending request");
        }

        var normalizedResultEmail = result.Email?.Trim().ToLowerInvariant() ?? string.Empty;
        if (result.Status != AccountCommandStatus.Rejected && normalizedResultEmail.Length == 0)
        {
            throw Exception(registration, "queue result identity does not match the registration");
        }

        if (result.Status != AccountCommandStatus.Rejected &&
            !string.Equals(normalizedResultEmail, registration.NormalizedEmail, StringComparison.Ordinal))
        {
            Throw(registration, "queue result identity does not match the registration");
        }

        var commandType = pendingCommandType.Value;
        var destination = ResolveDestination(registration, result, commandType);
        var errorCode = commandType == AccountCommandType.ResetPassword &&
                        result.Status is AccountCommandStatus.Failed or AccountCommandStatus.Rejected
            ? SafeErrorCode(result.Code)
            : destination == RegistrationStatus.Failed ? SafeErrorCode(result.Code) : null;

        if (commandType == AccountCommandType.ResetPassword &&
            result.Status == AccountCommandStatus.Success)
        {
            if (registration.PendingPasswordHash is not { Length: 36 })
            {
                throw Exception(registration, "successful password reset has no pending hash");
            }

            registration.PasswordHash = registration.PendingPasswordHash;
            registration.PendingPasswordHash = null;
        }
        else if (commandType == AccountCommandType.ResetPassword)
        {
            registration.PendingPasswordHash = null;
        }

        registration.Status = destination;
        registration.QueueLastErrorCode = errorCode;
        registration.QueueRequestId = null;
        registration.QueueCommandType = null;
        registration.QueuePublicationState = null;
        registration.QueueLastCheckedUtc = null;
        registration.UpdatedUtc = utcNow;

        if (destination == RegistrationStatus.Active)
        {
            registration.GameActivatedUtc = utcNow;
            registration.AdminDisabledUtc = null;
        }
        else if (destination == RegistrationStatus.Disabled)
        {
            registration.AdminDisabledUtc = utcNow;
        }
    }

    private static RegistrationStatus ResolveDestination(
        Registration registration,
        AccountCommandResult result,
        AccountCommandType commandType)
    {
        if (result.Status == AccountCommandStatus.Success)
        {
            return (commandType, result.Code) switch
            {
                (AccountCommandType.CreateAccount, "created") => RegistrationStatus.Active,
                (AccountCommandType.ActivateAccount, "activated") => RegistrationStatus.Active,
                (AccountCommandType.DeactivateAccount, "deactivated") => RegistrationStatus.Disabled,
                (AccountCommandType.ResetPassword, "password-reset") => RegistrationStatus.Active,
                _ => throw Exception(registration, "queue success result does not match the pending command")
            };
        }

        if (result.Status == AccountCommandStatus.Conflict &&
            commandType == AccountCommandType.CreateAccount &&
            string.Equals(result.Code, "already-exists", StringComparison.Ordinal))
        {
            return RegistrationStatus.Active;
        }

        if (result.Status is AccountCommandStatus.Failed or AccountCommandStatus.Rejected)
        {
            return commandType == AccountCommandType.ResetPassword
                ? RegistrationStatus.Active
                : RegistrationStatus.Failed;
        }

        if (result.Status == AccountCommandStatus.NotFound &&
            commandType is (AccountCommandType.ActivateAccount or AccountCommandType.DeactivateAccount or AccountCommandType.ResetPassword) &&
            string.Equals(result.Code, "account-not-found", StringComparison.Ordinal))
        {
            return RegistrationStatus.Failed;
        }

        throw Exception(registration, "queue result is not valid for the pending command");
    }

    private static void Queue(
        Registration registration,
        Guid requestId,
        AccountCommandType commandType)
    {
        registration.Status = RegistrationStatus.QueuePending;
        registration.QueueRequestId = requestId;
        registration.QueueCommandType = commandType;
        registration.QueuePublicationState = QueuePublicationState.Pending;
        registration.QueueLastErrorCode = null;
    }

    private static string SafeErrorCode(string? code) =>
        code is not null && SafeErrorCodes.Contains(code) ? code : "queue-failed";

    private static void EnsureStatus(
        Registration registration,
        RegistrationStatus expected,
        string action)
    {
        if (registration.Status != expected)
        {
            throw Exception(registration, action);
        }
    }

    private static void EnsureRequestId(Guid? requestId)
    {
        if (requestId is null || requestId == Guid.Empty)
        {
            throw new RegistrationTransitionException("A non-empty queue request ID is required.");
        }
    }

    private static void EnsureUtc(DateTime value)
    {
        if (value.Kind != DateTimeKind.Utc)
        {
            throw new RegistrationTransitionException("Registration transition timestamps must be UTC.");
        }
    }

    private static RegistrationTransitionException Exception(Registration registration, string action) =>
        new($"Registration in status {registration.Status} cannot apply {action}.");

    private static void Throw(Registration registration, string action) =>
        throw Exception(registration, action);
}

public sealed class RegistrationTransitionException(string message) : InvalidOperationException(message);
