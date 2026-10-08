namespace AccountPortal.Contracts;

public enum AccountCommandStatus
{
    Success,
    Conflict,
    NotFound,
    Invalid,
    Rejected,
    Failed
}
