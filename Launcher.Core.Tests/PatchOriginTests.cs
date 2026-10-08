namespace Launcher.Core.Tests;

public sealed class PatchOriginTests
{
    [Theory]
    [InlineData("https://portal.example.invalid/patch/")]
    [InlineData("https://portal.example.invalid/patch")]
    [InlineData("https://portal.example.invalid/")]
    public void Https_hosts_are_accepted_and_normalized(string host)
    {
        var origin = PatchOrigin.Parse(host);

        Assert.Equal(Uri.UriSchemeHttps, origin.BaseUri.Scheme);
        Assert.EndsWith("/", origin.BaseUri.AbsoluteUri, StringComparison.Ordinal);
        Assert.Empty(origin.BaseUri.UserInfo);
    }

    [Theory]
    [InlineData("http://portal.example.invalid/patch/")]
    [InlineData("ftp://portal.example.invalid/patch/")]
    [InlineData("portal.example.invalid/patch/")]
    [InlineData("/patch/")]
    [InlineData("")]
    [InlineData("   ")]
    public void Non_https_or_relative_hosts_are_rejected(string host)
    {
        Assert.Throws<ArgumentException>(() => PatchOrigin.Parse(host));
    }

    [Theory]
    [InlineData("https://user:pass@portal.example.invalid/patch/")]
    [InlineData("https://user@portal.example.invalid/patch/")]
    public void Embedded_credentials_are_rejected(string host)
    {
        var exception = Assert.Throws<ArgumentException>(() => PatchOrigin.Parse(host));

        Assert.Contains("credentials", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("https://portal.example.invalid/patch/?token=abc")]
    [InlineData("https://portal.example.invalid/patch/#fragment")]
    public void Query_and_fragment_are_rejected(string host)
    {
        Assert.Throws<ArgumentException>(() => PatchOrigin.Parse(host));
    }

    [Fact]
    public void Manifest_and_payload_uris_resolve_under_the_base()
    {
        var origin = PatchOrigin.Parse("https://portal.example.invalid/patch");

        Assert.Equal("https://portal.example.invalid/patch/PList.Bin", origin.ResolveManifestUri().AbsoluteUri);
        Assert.Equal(
            "https://portal.example.invalid/patch/Data-System.db.gz",
            origin.ResolveFileUri("Data\\System.db").AbsoluteUri);
    }
}
