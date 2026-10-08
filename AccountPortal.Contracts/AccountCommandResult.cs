namespace AccountPortal.Contracts;

public sealed record AccountCommandResult(
    Guid Id,
    AccountCommandStatus Status,
    string Code,
    string Email,
    DateTimeOffset CompletedAtUtc);
