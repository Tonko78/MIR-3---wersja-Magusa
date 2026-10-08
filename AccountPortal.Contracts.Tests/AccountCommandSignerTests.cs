using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AccountPortal.Contracts;

namespace AccountPortal.Contracts.Tests;

public sealed class AccountCommandSignerTests
{
    private const string FixedSignature = "EmoZnUiDqK+F/dxtWHSfOLaREE9aMN7F0IfC1sQkt9Q=";

    private static readonly string FixedKeyBase64 = Convert.ToBase64String(
        Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());

    [Fact]
    public void SignAndVerify_AcceptsUntamperedCommand()
    {
        var command = CreateFixedCommand();

        AccountCommandSigner.Sign(command, FixedKeyBase64);

        Assert.True(AccountCommandSigner.Verify(command, FixedKeyBase64));
    }

    [Fact]
    public void Verify_RejectsChangedEmailAfterSigning()
    {
        var command = AccountCommand.Create(
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            AccountCommandType.ActivateAccount,
            "first@example.com",
            null,
            DateTimeOffset.Parse("2026-09-22T12:00:00Z"),
            "nonce");
        AccountCommandSigner.Sign(command, FixedKeyBase64);

        command.Email = "other@example.com";

        Assert.False(AccountCommandSigner.Verify(command, FixedKeyBase64));
    }

    [Fact]
    public void Create_NormalizesTrimmedEmailToLowercase()
    {
        var command = AccountCommand.Create(
            Guid.NewGuid(),
            AccountCommandType.ActivateAccount,
            "  PLAYER@Example.COM ",
            null,
            DateTimeOffset.UtcNow,
            "nonce");

        Assert.Equal("player@example.com", command.Email);
    }

    [Fact]
    public void Validate_CreateAccountWithWrongPasswordHashLength_ThrowsInvalidDataException()
    {
        var command = AccountCommand.Create(
            Guid.NewGuid(),
            AccountCommandType.CreateAccount,
            "player@example.com",
            Convert.ToBase64String(new byte[35]),
            DateTimeOffset.UtcNow,
            "nonce");

        Assert.Throws<InvalidDataException>(command.Validate);
    }

    [Fact]
    public void Validate_CreateAccountWithMalformedPasswordHash_ThrowsInvalidDataException()
    {
        var command = AccountCommand.Create(
            Guid.NewGuid(),
            AccountCommandType.CreateAccount,
            "player@example.com",
            "not-base64!",
            DateTimeOffset.UtcNow,
            "nonce");

        var exception = Record.Exception(command.Validate);

        Assert.IsType<InvalidDataException>(exception);
    }

    [Theory]
    [InlineData(AccountCommandType.ActivateAccount)]
    [InlineData(AccountCommandType.DeactivateAccount)]
    public void Validate_NonCreateCommandDoesNotRequirePasswordHash(AccountCommandType type)
    {
        var command = AccountCommand.Create(
            Guid.NewGuid(),
            type,
            "player@example.com",
            null,
            DateTimeOffset.UtcNow,
            "nonce");

        command.Validate();
    }

    [Fact]
    public void Validate_WithUndefinedType_ThrowsInvalidDataException()
    {
        var command = CreateCommandWithUndefinedType();

        Assert.Throws<InvalidDataException>(command.Validate);
    }

    [Fact]
    public void Sign_WithUndefinedType_ThrowsInvalidDataException()
    {
        var command = CreateCommandWithUndefinedType();

        Assert.Throws<InvalidDataException>(() => AccountCommandSigner.Sign(command, FixedKeyBase64));
    }

    [Fact]
    public void Verify_WithUndefinedType_ReturnsFalse()
    {
        var command = CreateCommandWithUndefinedType();
        var canonical = string.Join(
            "\n",
            command.Version.ToString(CultureInfo.InvariantCulture),
            command.Id.ToString("D"),
            command.Type.ToString(),
            command.Email,
            command.PasswordHashBase64 ?? string.Empty,
            command.IssuedAtUnixSeconds.ToString(CultureInfo.InvariantCulture),
            command.Nonce);
        command.SignatureBase64 = Convert.ToBase64String(HMACSHA256.HashData(
            Convert.FromBase64String(FixedKeyBase64),
            Encoding.UTF8.GetBytes(canonical)));

        Assert.False(AccountCommandSigner.Verify(command, FixedKeyBase64));
    }

    [Fact]
    public void Sign_UsesExactDeterministicCanonicalForm()
    {
        var command = CreateFixedCommand();

        AccountCommandSigner.Sign(command, FixedKeyBase64);

        Assert.Equal(FixedSignature, command.SignatureBase64);
    }

    [Fact]
    public void Sign_WithInvalidBase64Key_ThrowsInvalidDataException()
    {
        var command = CreateFixedCommand();

        Assert.Throws<InvalidDataException>(() => AccountCommandSigner.Sign(command, "not-base64!"));
    }

    [Fact]
    public void Sign_WithNon32ByteKey_ThrowsInvalidDataException()
    {
        var command = CreateFixedCommand();
        var shortKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(31));

        Assert.Throws<InvalidDataException>(() => AccountCommandSigner.Sign(command, shortKey));
    }

    [Fact]
    public void Verify_WithInvalidBase64Key_ReturnsFalse()
    {
        var command = CreateFixedCommand();
        AccountCommandSigner.Sign(command, FixedKeyBase64);

        var result = AccountCommandSigner.Verify(command, "not-base64!");

        Assert.False(result);
    }

    [Fact]
    public void Verify_WithNon32ByteKey_ReturnsFalse()
    {
        var command = CreateFixedCommand();
        AccountCommandSigner.Sign(command, FixedKeyBase64);
        var shortKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(31));

        var result = AccountCommandSigner.Verify(command, shortKey);

        Assert.False(result);
    }

    [Fact]
    public void Verify_WithMalformedSignature_ReturnsFalse()
    {
        var command = CreateFixedCommand();
        command.SignatureBase64 = "not-base64!";

        var result = AccountCommandSigner.Verify(command, FixedKeyBase64);

        Assert.False(result);
    }

    [Fact]
    public void Verify_WithMalformedCommand_ReturnsFalse()
    {
        var command = AccountCommand.Create(
            Guid.NewGuid(),
            AccountCommandType.CreateAccount,
            "player@example.com",
            Convert.ToBase64String(new byte[35]),
            DateTimeOffset.UtcNow,
            "nonce");
        command.SignatureBase64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        var result = AccountCommandSigner.Verify(command, FixedKeyBase64);

        Assert.False(result);
    }

    [Fact]
    public void Validate_RequiresVersionIdEmailAndNonce()
    {
        var commands = new[]
        {
            new AccountCommand { Version = 2, Id = Guid.NewGuid(), Email = "player@example.com", Nonce = "nonce" },
            new AccountCommand { Id = Guid.Empty, Email = "player@example.com", Nonce = "nonce" },
            new AccountCommand { Id = Guid.NewGuid(), Email = " ", Nonce = "nonce" },
            new AccountCommand { Id = Guid.NewGuid(), Email = "player@example.com", Nonce = " " }
        };

        Assert.All(commands, command => Assert.Throws<InvalidDataException>(command.Validate));
    }

    private static AccountCommand CreateCommandWithUndefinedType()
    {
        return AccountCommand.Create(
            Guid.NewGuid(),
            (AccountCommandType)int.MaxValue,
            "player@example.com",
            null,
            DateTimeOffset.UtcNow,
            "nonce");
    }

    private static AccountCommand CreateFixedCommand()
    {
        return AccountCommand.Create(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            AccountCommandType.CreateAccount,
            "Player@Example.com",
            Convert.ToBase64String(new byte[36]),
            DateTimeOffset.Parse("2026-09-22T12:00:00Z"),
            "fixed-nonce");
    }
}
