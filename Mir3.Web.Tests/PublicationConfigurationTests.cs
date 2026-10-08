using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Mir3.Web.Options;
namespace Mir3.Web.Tests;
public class PublicationConfigurationTests
{
    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("http://portal.example.invalid")]
    [InlineData("https://user:secret@portal.example.invalid")]
    [InlineData("https://portal.example.invalid/path")]
    [InlineData("https://portal.example.invalid/?x=1")]
    [InlineData("https://portal.example.invalid/#fragment")]
    public void Invalid_public_origin_fails_validation(string? origin)
    {
        var options = Configure(origin);
        var result = new SmtpOptionsValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("MIR3_PUBLIC_BASE_URL"));
    }
    [Fact]
    public void Environment_public_origin_is_bound_and_validated()
    {
        var options = Configure("https://portal.example.invalid:8443");
        var property = typeof(SmtpOptions).GetProperty("PublicBaseUrl");
        Assert.NotNull(property);
        Assert.Equal("https://portal.example.invalid:8443", property.GetValue(options));
        Assert.True(new SmtpOptionsValidator().Validate(null, options).Succeeded);
    }
    [Theory]
    [InlineData("smtp.example.invalid", 2525, 45, true)]
    [InlineData("", 587, 20, false)]
    [InlineData("https://smtp.example.invalid", 587, 20, false)]
    [InlineData("smtp.example.invalid", 0, 20, false)]
    [InlineData("smtp.example.invalid", 65536, 20, false)]
    [InlineData("smtp.example.invalid", 587, 0, false)]
    [InlineData("smtp.example.invalid", 587, 301, false)]
    public void Smtp_transport_has_configurable_bounded_values(string host, int port, int timeout, bool valid)
    {
        var options = Configure("https://portal.example.invalid");
        options.Host = host; options.Port = port; options.TimeoutSeconds = timeout;
        Assert.Equal(valid, new SmtpOptionsValidator().Validate(null, options).Succeeded);
    }
    [Fact]
    public void Proxy_allowlist_defaults_to_loopback_and_rejects_invalid_entries()
    {
        var type = typeof(SmtpOptions).Assembly.GetType("Mir3.Web.Options.TrustedProxyConfiguration");
        Assert.NotNull(type);
        var method = type.GetMethod("Apply");
        Assert.NotNull(method);
        var options = new Microsoft.AspNetCore.Builder.ForwardedHeadersOptions();
        method.Invoke(null, new object?[] { options, null });
        Assert.Contains(System.Net.IPAddress.Loopback, options.KnownProxies);
        Assert.Contains(System.Net.IPAddress.IPv6Loopback, options.KnownProxies);
        Assert.DoesNotContain(System.Net.IPAddress.Parse("192.168.0.64"), options.KnownProxies);
        method.Invoke(null, new object?[] { options, "10.0.0.8,::1" });
        Assert.Contains(System.Net.IPAddress.Parse("10.0.0.8"), options.KnownProxies);
        Assert.DoesNotContain(System.Net.IPAddress.Loopback, options.KnownProxies);
        Assert.Throws<System.Reflection.TargetInvocationException>(() => method.Invoke(null, new object?[] { options, "*" }));
        Assert.Throws<System.Reflection.TargetInvocationException>(() => method.Invoke(null, new object?[] { options, "" }));
    }
    private static SmtpOptions Configure(string? origin)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
        { ["MIR3_PUBLIC_BASE_URL"] = origin }).Build();
        var options = new SmtpOptions();
        new SmtpOptionsSetup(config, key => key == "MIR3_PUBLIC_BASE_URL" ? origin : "test-secret").Configure(options);
        return options;
    }
}
