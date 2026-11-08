using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace NightCodeForge.Web.Tailscale;

/// <summary>Resolves the local Kestrel endpoint to proxy to from configured/bound addresses.</summary>
public static class TailscaleTargetResolver
{
    private static readonly string[] WildcardHosts = ["+", "*", "[::]", "0.0.0.0"];

    /// <summary>
    /// Attempts to pick the endpoint to proxy to without throwing. Plain HTTP is preferred; HTTPS is used only when no HTTP endpoint exists.
    /// </summary>
    public static bool TryResolve(IEnumerable<string> addresses, out TailscaleTarget? target)
    {
        var targets = new List<TailscaleTarget>();
        foreach (var raw in addresses.SelectMany(a => a.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        {
            if (TryParse(raw, out var parsed))
            {
                targets.Add(parsed);
            }
        }

        target = targets.FirstOrDefault(t => t.Scheme == Uri.UriSchemeHttp) ?? targets.FirstOrDefault();
        return target is not null;
    }

    /// <summary>
    /// Picks the endpoint to proxy to. Plain HTTP is preferred (no certificate hassle); HTTPS is used only when no HTTP endpoint exists.
    /// </summary>
    /// <exception cref="TailscaleServeException">No usable address.</exception>
    public static TailscaleTarget Resolve(IEnumerable<string> addresses)
    {
        if (TryResolve(addresses, out var target) && target is not null)
        {
            return target;
        }

        throw new TailscaleServeException(
            "Unable to determine the local Kestrel port (no usable http/https address bound or configured in 'Urls').");
    }

    private static bool TryParse(string address, out TailscaleTarget target)
    {
        target = null!;
        var normalized = address;
        foreach (var wildcard in WildcardHosts)
        {
            normalized = normalized.Replace($"://{wildcard}:", "://localhost:", StringComparison.Ordinal);
        }

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || uri.Port <= 0)
        {
            return false;
        }

        target = new TailscaleTarget(uri.Scheme, uri.Port);
        return true;
    }
}

/// <summary>Parses <c>tailscale status --json</c>.</summary>
public static class TailscaleStatusParser
{
    public const string RunningBackendState = "Running";
    private const string ServicePrefix = "svc:";

    public static bool IsAuthenticated(string statusJson)
    {
        if (string.IsNullOrWhiteSpace(statusJson))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(statusJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("BackendState", out var state)
                && state.ValueKind == JsonValueKind.String
                && state.GetString() == RunningBackendState;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Returns this node's MagicDNS name without trailing dot, or null if unknown.</summary>
    public static string? GetNodeDnsName(string statusJson)
    {
        if (string.IsNullOrWhiteSpace(statusJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(statusJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("Self", out var self)
                && self.ValueKind == JsonValueKind.Object
                && self.TryGetProperty("DNSName", out var dns)
                && dns.ValueKind == JsonValueKind.String
                && dns.GetString() is { Length: > 0 } name)
            {
                return name.TrimEnd('.');
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    /// <summary>
    /// Builds the public hostname: the node name in Device Mode, or <c>&lt;service&gt;.&lt;tailnet&gt;</c> in Service Mode.
    /// </summary>
    public static string? GetServeHostName(string statusJson, string? serviceName)
    {
        var node = GetNodeDnsName(statusJson);
        if (node is null || string.IsNullOrWhiteSpace(serviceName))
        {
            return node;
        }

        var separator = node.IndexOf('.');
        var tailnetSuffix = separator >= 0 ? node[(separator + 1)..] : string.Empty;
        var shortName = serviceName.StartsWith(ServicePrefix, StringComparison.OrdinalIgnoreCase)
            ? serviceName[ServicePrefix.Length..]
            : serviceName;
        return tailnetSuffix.Length > 0 ? $"{shortName}.{tailnetSuffix}" : shortName;
    }

    /// <summary>Returns this node's primary IPv4 WireGuard address (100.x.y.z), or null if absent.</summary>
    public static string? GetTailscaleIpv4(string statusJson)
    {
        if (string.IsNullOrWhiteSpace(statusJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(statusJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("Self", out var self)
                && self.ValueKind == JsonValueKind.Object
                && self.TryGetProperty("TailscaleIPs", out var ips)
                && ips.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in ips.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String
                        && IPAddress.TryParse(item.GetString(), out var address)
                        && address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        return address.ToString();
                    }
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    /// <summary>Returns the tailnet domain suffix (e.g. <c>tail1234.ts.net</c>), or null if unknown.</summary>
    public static string? GetTailnetDomain(string statusJson)
    {
        if (string.IsNullOrWhiteSpace(statusJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(statusJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("CurrentTailnet", out var ct)
                && ct.ValueKind == JsonValueKind.Object
                && ct.TryGetProperty("Name", out var name)
                && name.ValueKind == JsonValueKind.String
                && name.GetString() is { Length: > 0 } tailnetName)
            {
                return tailnetName.TrimEnd('.');
            }
        }
        catch (JsonException)
        {
            // Fall back to node suffix parsing below
        }

        var node = GetNodeDnsName(statusJson);
        if (string.IsNullOrWhiteSpace(node))
        {
            return null;
        }

        var separator = node.IndexOf('.');
        return separator >= 0 && separator < node.Length - 1
            ? node[(separator + 1)..]
            : null;
    }
}

/// <summary>Maps <c>--tailscale*</c> CLI flags to configuration keys.</summary>
public static class TailscaleCommandLine
{
    public const string EnableFlag = "--tailscale";
    public const string ServiceFlag = "--tailscale-service";
    public const string PortFlag = "--tailscale-port";

    public static IReadOnlyDictionary<string, string?> ToConfiguration(IReadOnlyList<string> args)
    {
        var values = new Dictionary<string, string?>();
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case EnableFlag:
                    values[$"{TailscaleOptions.SectionName}:{nameof(TailscaleOptions.AutoServe)}"] = bool.TrueString;
                    break;
                case ServiceFlag when i + 1 < args.Count:
                    values[$"{TailscaleOptions.SectionName}:{nameof(TailscaleOptions.ServiceName)}"] = args[++i];
                    values[$"{TailscaleOptions.SectionName}:{nameof(TailscaleOptions.AutoServe)}"] = bool.TrueString;
                    break;
                case PortFlag when i + 1 < args.Count:
                    values[$"{TailscaleOptions.SectionName}:{nameof(TailscaleOptions.ListenPort)}"] = args[++i];
                    break;
            }
        }

        return values;
    }
}

