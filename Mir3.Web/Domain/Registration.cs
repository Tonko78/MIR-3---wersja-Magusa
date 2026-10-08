using AccountPortal.Contracts;

namespace Mir3.Web.Domain;

public sealed class Registration
{
    public Guid Id { get; set; }

    public string Email { get; set; } = string.Empty;

    public string NormalizedEmail { get; set; } = string.Empty;
    public string PreferredLanguage { get; set; } = "en";

    public byte[] PasswordHash { get; set; } = [];

    // Holds a replacement hash only while a ResetPassword queue command is pending.
    public byte[]? PendingPasswordHash { get; set; }

    public RegistrationStatus Status { get; set; }

    public byte[] VerificationTokenHash { get; set; } = [];

    public DateTime VerificationExpiresUtc { get; set; }

    public DateTime? VerificationUsedUtc { get; set; }

    public DateTime? EmailVerifiedUtc { get; set; }

    public string? EmailLastErrorCode { get; set; }

    public bool InitialEmailDeliveryPending { get; set; }

    // Correlates token rotation and completion so concurrent or stale senders cannot invalidate newer mail.
    public Guid? EmailDeliveryAttemptId { get; set; }

    public DateTime? EmailDeliveryAttemptAcquiredUtc { get; set; }

    public DateTime? GameActivatedUtc { get; set; }

    public DateTime? AdminDisabledUtc { get; set; }

    public Guid? QueueRequestId { get; set; }

    public QueuePublicationState? QueuePublicationState { get; set; }

    public AccountCommandType? QueueCommandType { get; set; }

    public string? QueueLastErrorCode { get; set; }

    public DateTime? QueueLastCheckedUtc { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }

    public byte[] SourceIpHash { get; set; } = [];
}
