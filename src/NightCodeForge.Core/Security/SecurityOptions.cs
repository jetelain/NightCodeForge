namespace NightCodeForge.Core.Security;

/// <summary>
/// Configuration options for system security, Content Security Policy (CSP), and air-gapped offline operation.
/// </summary>
public class SecurityOptions
{
    public const string SectionName = "Security";
    public const string AirGappedEnvironmentVariable = "NCF_AIR_GAPPED";

    private bool _airGappedMode;

    /// <summary>
    /// Establishes the centralized platform configuration option for air-gapped operations.
    /// When true, external network egress (such as web search or external telemetry) is blocked or deactivated.
    /// Can be set via configuration ("Security:AirGappedMode") or the NCF_AIR_GAPPED environment variable.
    /// Default: false.
    /// </summary>
    public bool AirGappedMode
    {
        get => _airGappedMode || IsAirGappedEnvironmentVariableSet();
        set => _airGappedMode = value;
    }

    /// <summary>
    /// Controls whether the web application emits strict Content Security Policy (CSP) headers on HTTP responses.
    /// Enabled by default for defense-in-depth web hardening.
    /// Default: true.
    /// </summary>
    public bool EnableContentSecurityPolicy { get; set; } = true;

    private static bool IsAirGappedEnvironmentVariableSet()
    {
        var env = Environment.GetEnvironmentVariable(AirGappedEnvironmentVariable);
        return string.Equals(env, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(env, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(env, "yes", StringComparison.OrdinalIgnoreCase);
    }
}

