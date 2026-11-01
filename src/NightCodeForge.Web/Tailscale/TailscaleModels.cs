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
    public const string DefaultServiceName = "svc:ncf";

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
}

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

