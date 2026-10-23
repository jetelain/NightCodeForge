namespace NightCodeForge.Core.Resilience;

/// <summary>
/// Constants used across resilience policies, jitter calculations, and telemetry.
/// </summary>
public static class ResilienceConstants
{
    /// <summary>
    /// Lower bound multiplier for jitter calculation (50% of the calculated exponential delay).
    /// </summary>
    public const double JitterLowerBoundFactor = 0.5;

    /// <summary>
    /// Exception Data key indicating that an exception has already been retried by an inner handler.
    /// </summary>
    public const string RetriedKey = "NightCodeForge.TransientRetried";

    /// <summary>
    /// Log level string constants for telemetry and activity logs.
    /// </summary>
    public const string LogLevelWarning = "Warning";
    public const string LogLevelError = "Error";
    public const string LogLevelInfo = "Info";
}
