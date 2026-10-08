namespace AccountPortal.Contracts;

public sealed class AccountCommand
{
    public int Version { get; init; } = 1;
    public Guid Id { get; init; }
    public AccountCommandType Type { get; init; }
    public string Email { get; set; } = string.Empty;
    public string? PasswordHashBase64 { get; init; }
    public long IssuedAtUnixSeconds { get; init; }
    public string Nonce { get; init; } = string.Empty;
    public string SignatureBase64 { get; set; } = string.Empty;

    public static AccountCommand Create(
        Guid id,
        AccountCommandType type,
        string email,
        string? passwordHashBase64,
        DateTimeOffset issuedAt,
        string nonce)
    {
        return new AccountCommand
        {
            Id = id,
            Type = type,
            Email = email.Trim().ToLowerInvariant(),
            PasswordHashBase64 = passwordHashBase64,
            IssuedAtUnixSeconds = issuedAt.ToUnixTimeSeconds(),
            Nonce = nonce
        };
    }

    public void Validate()
    {
        if (Version != 1 ||
            Id == Guid.Empty ||
            !Enum.IsDefined(Type) ||
            string.IsNullOrWhiteSpace(Email) ||
            string.IsNullOrWhiteSpace(Nonce))
        {
            throw new InvalidDataException("Malformed account command.");
        }

        if (Type is not (AccountCommandType.CreateAccount or AccountCommandType.ResetPassword))
        {
            return;
        }

        if (PasswordHashBase64 is null)
        {
            throw new InvalidDataException($"{Type} requires a 36-byte Zircon password hash.");
        }

        try
        {
            if (Convert.FromBase64String(PasswordHashBase64).Length != 36)
            {
                throw new InvalidDataException($"{Type} requires a 36-byte Zircon password hash.");
            }
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException(
                $"{Type} requires a valid Base64-encoded 36-byte Zircon password hash.",
                exception);
        }
    }
}
