using System.Text.Json;

namespace NightCodeForge.Web.Tailscale;

/// <summary>Resolves the local Kestrel endpoint to proxy to from configured/bound addresses.</summary>
public static class TailscaleTargetResolver
{
    private static readonly string[] WildcardHosts = ["+", "*", "[::]", "0.0.0.0"];

    /// <summary>
    /// Picks the endpoint to proxy to. Plain HTTP is preferred (no certificate hassle); HTTPS is used only when no HTTP endpoint exists.
    /// </summary>
    /// <exception cref="TailscaleServeException">No usable address.</exception>
    public static TailscaleTarget Resolve(IEnumerable<string> addresses)
    {
        var targets = new List<TailscaleTarget>();
        foreach (var raw in addresses.SelectMany(a => a.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        {
            if (TryParse(raw, out var target))
            {
                targets.Add(target);
            }
        }

        var chosen = targets.FirstOrDefault(t => t.Scheme == Uri.UriSchemeHttp) ?? targets.FirstOrDefault();
        return chosen ?? throw new TailscaleServeException(
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

