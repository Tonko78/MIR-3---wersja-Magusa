using Mir3.Web.Security;

namespace Mir3.Web.Tests;

public sealed class GamePasswordHasherTests
{
    [Fact]
    public void Hash_ProducesZirconCompatible36BytePayload()
    {
        var payload = new GamePasswordHasher().Hash("ValidPass123!");

        Assert.Equal(36, payload.Length);
        Assert.True(GamePasswordHasher.Verify("ValidPass123!", payload));
        Assert.False(GamePasswordHasher.Verify("WrongPass123!", payload));
    }

    [Fact]
    public void PasswordLengthConstants_MatchPinnedZirconGlobals()
    {
        // Pinned source parity: LibraryCore/Globals.cs, Globals.MinPasswordLength and
        // Globals.MaxPasswordLength (lines 66-67 at Task 6's pinned commit).
        Assert.Equal(5, GamePasswordHasher.MinimumPasswordLength);
        Assert.Equal(15, GamePasswordHasher.MaximumPasswordLength);
    }

    [Theory]
    [InlineData("Pass word1!")]
    [InlineData("Pass\tword1!")]
    [InlineData("Pass\nword1!")]
    public void Hash_RejectsWhitespace(string password)
    {
        Assert.Throws<ArgumentException>(() => new GamePasswordHasher().Hash(password));
    }

    [Fact]
    public void Hash_RejectsTooShortPassword()
    {
        var password = new string('a', GamePasswordHasher.MinimumPasswordLength - 1);

        Assert.Throws<ArgumentException>(() => new GamePasswordHasher().Hash(password));
    }

    [Fact]
    public void Hash_RejectsTooLongPassword()
    {
        var password = new string('a', GamePasswordHasher.MaximumPasswordLength + 1);

        Assert.Throws<ArgumentException>(() => new GamePasswordHasher().Hash(password));
    }

    [Fact]
    public void Hash_RejectsNullPassword()
    {
        Assert.Throws<ArgumentNullException>(() => new GamePasswordHasher().Hash(null!));
    }

    [Fact]
    public void Verify_AcceptsPinnedZirconCompatibilityVectorWithSaltFirst()
    {
        // ServerLibrary/Envir/SEnvir.cs CreateHash/PasswordMatch uses:
        // PBKDF2-SHA256, 1354 iterations, salt[16] followed by hash[20].
        var payload = Convert.FromHexString(
            "000102030405060708090A0B0C0D0E0F" +
            "960846F3C5B3F8E0E383D71CCDE01C0DE4446347");

        Assert.True(GamePasswordHasher.Verify("ValidPass123!", payload));
        Assert.False(GamePasswordHasher.Verify("WrongPass123!", payload));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(35)]
    [InlineData(37)]
    public void Verify_RejectsNullOrMalformedPayload(int? length)
    {
        var payload = length is null ? null : new byte[length.Value];

        Assert.False(GamePasswordHasher.Verify("ValidPass123!", payload));
    }

    [Fact]
    public void Verify_RejectsInvalidPasswordWithoutThrowing()
    {
        var payload = new byte[36];

        Assert.False(GamePasswordHasher.Verify(null, payload));
        Assert.False(GamePasswordHasher.Verify("bad password", payload));
    }
}
