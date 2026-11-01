using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using NightCodeForge.Web.Tailscale;

namespace NightCodeForge.Web.Tests;

/// <summary>
/// Verifies the forwarded-headers trust policy: only the local Tailscale daemon
/// (loopback) is treated as a proxy, so a direct client cannot spoof X-Forwarded-Proto / X-Forwarded-For.
/// </summary>
public class ForwardedHeadersConfigurationTests
{
    private static bool IsKnownProxy(ForwardedHeadersOptions options, IPAddress address)
    {
        foreach (var known in options.KnownProxies)
        {
            if (known.Equals(address))
            {
                return true;
            }
        }

        return false;
    }

    [Fact]
    public void ApplyLoopbackTrust_HonorsForwardedHeaders()
    {
        var options = new ForwardedHeadersOptions();

        ForwardedHeadersConfiguration.ApplyLoopbackTrust(options);

        Assert.Equal(ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto, options.ForwardedHeaders);
    }

    [Fact]
    public void ApplyLoopbackTrust_TreatsLoopbackAsKnownProxy()
    {
        var options = new ForwardedHeadersOptions();

        ForwardedHeadersConfiguration.ApplyLoopbackTrust(options);

        // The local Tailscale daemon proxies on loopback; its forwarded values must be honored.
        Assert.True(IsKnownProxy(options, IPAddress.Loopback));
        Assert.True(IsKnownProxy(options, IPAddress.IPv6Loopback));
    }

    [Theory]
    [InlineData("100.64.0.10")]  // Tailnet client behind no local proxy
    [InlineData("192.168.1.50")] // LAN client
    [InlineData("8.8.8.8")]      // Internet client
    [InlineData("2001:4860:4860::8888")] // IPv6 internet client
    public void ApplyLoopbackTrust_DoesNotTrustNonLoopbackPeers(string address)
    {
        var options = new ForwardedHeadersOptions();

        ForwardedHeadersConfiguration.ApplyLoopbackTrust(options);

        var peer = IPAddress.Parse(address);
        Assert.False(IsKnownProxy(options, peer));
    }
}
