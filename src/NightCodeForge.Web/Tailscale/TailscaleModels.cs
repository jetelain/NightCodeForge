namespace NightCodeForge.Web.Tailscale;

/// <summary>
/// Options bound from the <c>Tailscale</c> configuration section (appsettings.json, config/config.json, CLI flags).
/// </summary>
public class TailscaleOptions
{
    public const string SectionName = "Tailscale";

    public const string HttpsProtocol = "https";
    public const string HttpProtocol = "http";
    public const int DefaultListenPort = 443;
    public const int MinListenPort = 1;
    public const int MaxListenPort = 65535;
    public const string DefaultServiceName = "svc:ncf";
    public const string AdminConsoleServicesUrl = "https://console.tailscale.com/admin/services?refreshed=true";
    public const string TailscaleServicesDocsUrl = "https://tailscale.com/docs/features/tailscale-services";

    /// <summary>Whether the app configures <c>tailscale serve</c> by itself on startup.</summary>
    public bool AutoServe { get; set; }

    /// <summary>Port exposed on the tailnet.</summary>
    public int ListenPort { get; set; } = DefaultListenPort;

    /// <summary><c>https</c> or <c>http</c>.</summary>
    public string Protocol { get; set; } = HttpsProtocol;

    /// <summary>Tailscale service name (e.g. <c>svc:ncf</c>). Empty/null selects Device Mode.</summary>
    public string? ServiceName { get; set; } = DefaultServiceName;

    /// <summary>Whether the service is advertised after being configured (Service Mode only).</summary>
    public bool Advertise { get; set; } = true;

    /// <summary>Validates that a listen port falls within the legal TCP port range (1–65535).</summary>
    public static bool IsValidListenPort(int port) => port is >= MinListenPort and <= MaxListenPort;

    /// <summary>Returns the trimmed service name, or <c>null</c> when empty/whitespace (Device Mode).</summary>
    public string? GetNormalizedServiceName() =>
        string.IsNullOrWhiteSpace(ServiceName) ? null : ServiceName.Trim();

    /// <summary>Returns the normalized lowercase protocol (<c>http</c> or <c>https</c>).</summary>
    public string GetNormalizedProtocol() =>
        string.Equals(Protocol?.Trim(), HttpProtocol, StringComparison.OrdinalIgnoreCase)
            ? HttpProtocol
            : HttpsProtocol;

    /// <summary>Determines whether two <see cref="TailscaleOptions"/> instances represent the same effective configuration.</summary>
    public bool ConfigurationEquals(TailscaleOptions? other)
    {
        if (other is null)
        {
            return false;
        }

        return AutoServe == other.AutoServe
            && ListenPort == other.ListenPort
            && string.Equals(GetNormalizedProtocol(), other.GetNormalizedProtocol(), StringComparison.Ordinal)
            && string.Equals(GetNormalizedServiceName(), other.GetNormalizedServiceName(), StringComparison.Ordinal)
            && Advertise == other.Advertise;
    }

    /// <summary>Creates a memberwise clone of this options instance.</summary>
    public TailscaleOptions Clone() => new()
    {
        AutoServe = AutoServe,
        ListenPort = ListenPort,
        Protocol = Protocol,
        ServiceName = ServiceName,
        Advertise = Advertise
    };
}

/// <summary>Live operational state of the Tailscale daemon and serve route.</summary>
public enum TailscaleDaemonState
{
    Inactive,
    Applying,
    Active,
    DaemonOfflineOrUnauthenticated
}

/// <summary>Real-time telemetry and diagnostics snapshot of the local Tailscale node and serve route.</summary>
public record TailscaleStatusSnapshot(
    TailscaleDaemonState State,
    string? CliVersion,
    string? NodeDnsName,
    string? TailnetDomain,
    string? TailscaleIpv4,
    string? LoopbackTarget,
    string? MagicDnsUrl,
    string? LastErrorMessage = null);

/// <summary>Local Kestrel endpoint that <c>tailscale serve</c> proxies to.</summary>
/// <param name="Scheme">Kestrel scheme (<c>http</c> or <c>https</c>).</param>
/// <param name="Port">Kestrel port.</param>
public record TailscaleTarget(string Scheme, int Port)
{
    public const string LoopbackAddress = "127.0.0.1";
    public const string InsecureHttpsScheme = "https+insecure";

    /// <summary>Proxy target argument for <c>tailscale serve</c>.</summary>
    public string ToServeTarget() =>
        string.Equals(Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            ? $"{InsecureHttpsScheme}://{LoopbackAddress}:{Port}"
            : $"{LoopbackAddress}:{Port}";
}

/// <summary>Result of a Tailscale CLI invocation.</summary>
public record TailscaleCliResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>Raised when the Tailscale serve configuration cannot be resolved or applied.</summary>
public class TailscaleServeException : Exception
{
    public TailscaleServeException(string message) : base(message)
    {
    }

    public TailscaleServeException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
