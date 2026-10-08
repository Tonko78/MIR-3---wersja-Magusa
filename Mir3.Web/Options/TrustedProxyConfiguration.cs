using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
namespace Mir3.Web.Options;

public static class TrustedProxyConfiguration
{
    // Comma-separated individual IP addresses, never wildcard networks.
    public static void Apply(ForwardedHeadersOptions options, string? configured)
    {
        var entries = configured is null ? new[] { "127.0.0.1", "::1" } : configured.Split(',');
        var addresses = new List<IPAddress>();
        foreach (var entry in entries)
        {
            if (!IPAddress.TryParse(entry.Trim(), out var address) ||
                address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
                throw new InvalidOperationException("MIR3_TRUSTED_PROXIES must be a nonempty comma-separated IP allowlist.");
            addresses.Add(address);
        }
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
#pragma warning disable ASPDEPR005
        options.KnownNetworks.Clear();
#pragma warning restore ASPDEPR005
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        foreach (var address in addresses) options.KnownProxies.Add(address);
        options.ForwardLimit = 1;
    }
}
