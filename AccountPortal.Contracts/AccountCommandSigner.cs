using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AccountPortal.Contracts;

public static class AccountCommandSigner
{
    private const int KeyLength = 32;

    public static void Sign(AccountCommand command, string keyBase64)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Validate();

        var key = DecodeKey(keyBase64);
        try
        {
            var signature = HMACSHA256.HashData(key, GetCanonicalBytes(command));
            command.SignatureBase64 = Convert.ToBase64String(signature);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public static bool Verify(AccountCommand? command, string? keyBase64)
    {
        if (command is null)
        {
            return false;
        }

        byte[] key;
        byte[] suppliedSignature;

        try
        {
            command.Validate();
            suppliedSignature = Convert.FromBase64String(command.SignatureBase64);
            key = DecodeKey(keyBase64);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or FormatException or ArgumentNullException)
        {
            return false;
        }

        try
        {
            var expectedSignature = HMACSHA256.HashData(key, GetCanonicalBytes(command));
            return CryptographicOperations.FixedTimeEquals(expectedSignature, suppliedSignature);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static byte[] DecodeKey(string? keyBase64)
    {
        byte[] key;

        try
        {
            key = Convert.FromBase64String(keyBase64!);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentNullException)
        {
            throw new InvalidDataException("The account command HMAC key must be valid Base64.", exception);
        }

        if (key.Length != KeyLength)
        {
            CryptographicOperations.ZeroMemory(key);
            throw new InvalidDataException("The account command HMAC key must decode to exactly 32 bytes.");
        }

        return key;
    }

    private static byte[] GetCanonicalBytes(AccountCommand command)
    {
        var canonical = string.Join(
            "\n",
            command.Version.ToString(CultureInfo.InvariantCulture),
            command.Id.ToString("D"),
            command.Type.ToString(),
            command.Email,
            command.PasswordHashBase64 ?? string.Empty,
            command.IssuedAtUnixSeconds.ToString(CultureInfo.InvariantCulture),
            command.Nonce);

        return Encoding.UTF8.GetBytes(canonical);
    }
}
