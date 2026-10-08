namespace Mir3.Web.Domain;

public enum RegistrationStatus
{
    PendingEmail,
    AwaitingAdmin,
    QueuePending,
    Active,
    Disabled,
    Failed
}
