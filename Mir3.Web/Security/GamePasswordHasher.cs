using System.Security.Cryptography;

namespace Mir3.Web.Security;

public sealed class GamePasswordHasher
{
    // Kept in parity with LibraryCore.Globals at the pinned Zircon source revision.
    public const int MinimumPasswordLength = 5;
    public const int MaximumPasswordLength = 15;

    private const int Iterations = 1354;
    private const int SaltSize = 16;
    private const int HashSize = 20;
    private const int PayloadSize = SaltSize + HashSize;

    public byte[] Hash(string password)
    {
        ValidatePassword(password);

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var derivedHash = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            HashSize);

        try
        {
            var payload = new byte[PayloadSize];
            salt.CopyTo(payload, 0);
            derivedHash.CopyTo(payload, SaltSize);
            return payload;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derivedHash);
        }
    }

    public static bool Verify(string? password, byte[]? payload)
    {
        if (!IsValidPassword(password) || payload is null || payload.Length != PayloadSize)
        {
            return false;
        }

        var derivedHash = Rfc2898DeriveBytes.Pbkdf2(
            password!,
            payload.AsSpan(0, SaltSize),
            Iterations,
            HashAlgorithmName.SHA256,
            HashSize);

        try
        {
            return CryptographicOperations.FixedTimeEquals(
                payload.AsSpan(SaltSize, HashSize),
                derivedHash);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derivedHash);
        }
    }

    private static void ValidatePassword(string? password)
    {
        ArgumentNullException.ThrowIfNull(password);

        if (!IsValidPassword(password))
        {
            throw new ArgumentException(
                $"Password must contain no whitespace and be between {MinimumPasswordLength} and {MaximumPasswordLength} characters.",
                nameof(password));
        }
    }

    private static bool IsValidPassword(string? password) =>
        password is not null &&
        password.Length is >= MinimumPasswordLength and <= MaximumPasswordLength &&
        !password.Any(char.IsWhiteSpace);
}
