using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace NightCodeForge.Web.Tailscale;

/// <summary>
/// Forwarded-headers trust policy for requests arriving through the local Tailscale
/// <c>tailscale serve</c> proxy. The only proxy in the MVP is the local daemon on
/// loopback, so forwarded-header values are honored exclusively for loopback peers.
/// </summary>
public static class ForwardedHeadersConfiguration
{
    /// <summary>
    /// Honors <c>X-Forwarded-Proto</c> / <c>X-Forwarded-For</c> only for requests whose remote
    /// endpoint is loopback (the local Tailscale daemon). Any other direct client is not
    /// treated as a proxy, so its forwarded-header values are ignored — this prevents
    /// scheme/for-spoofing by a client that reaches Kestrel directly.
    /// </summary>
    public static void ApplyLoopbackTrust(ForwardedHeadersOptions options)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        options.KnownProxies.Add(IPAddress.Loopback);
        options.KnownProxies.Add(IPAddress.IPv6Loopback);
    }
}
