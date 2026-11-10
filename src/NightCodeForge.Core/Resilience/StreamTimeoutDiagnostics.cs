namespace NightCodeForge.Core.Resilience;

/// <summary>
/// Execution phase in which a streaming LLM inference timeout or premature connection closure occurred.
/// </summary>
public enum StreamTimeoutPhase
{
    /// <summary>
    /// Stalled before HTTP 200 OK headers arrived (e.g. cold KV cache prefill or server queue saturation).
    /// </summary>
    PreHeaderPrefill,

    /// <summary>
    /// Stalled after HTTP 200 OK headers arrived but before the very first token chunk was emitted.
    /// </summary>
    FirstTokenPrefill,

    /// <summary>
    /// The stream successfully yielded tokens, but then paused/stalled for longer than the sliding idle watchdog.
    /// </summary>
    MidStreamIdleStall,

    /// <summary>
    /// The stream was actively generating tokens when the upstream connection was unexpectedly reset or closed.
    /// </summary>
    MidStreamConnectionClosed
}

/// <summary>
/// Structured diagnostics captured when a streaming LLM inference call times out or is aborted mid-stream.
/// </summary>
public sealed record StreamTimeoutDiagnostics
{
    public const string DataKey = "NightCodeForge.StreamTimeoutDiagnostics";

    public StreamTimeoutPhase Phase { get; init; }
    public double ElapsedSeconds { get; init; }
    public double? ConfiguredTimeoutSeconds { get; init; }
    public int GeneratedTokens { get; init; }
    public double TokensPerSecond { get; init; }
}

/// <summary>
/// Specialized HTTP exception thrown when a streaming LLM inference call fails due to a prefill timeout,
/// sliding inter-token idle timeout, or mid-stream connection closure.
/// Inherits <see cref="HttpRequestException"/> so existing transient error and timeout policies recognize it.
/// </summary>
public class LlmStreamTimeoutException : HttpRequestException
{
    public StreamTimeoutDiagnostics Diagnostics { get; }

    public LlmStreamTimeoutException(
        string message,
        Exception? innerException,
        StreamTimeoutPhase phase,
        double elapsedSeconds,
        double? configuredTimeoutSeconds,
        int generatedTokens,
        double tokensPerSecond)
        : base(message, innerException)
    {
        Diagnostics = new StreamTimeoutDiagnostics
        {
            Phase = phase,
            ElapsedSeconds = elapsedSeconds,
            ConfiguredTimeoutSeconds = configuredTimeoutSeconds,
            GeneratedTokens = generatedTokens,
            TokensPerSecond = tokensPerSecond
        };
        Data[StreamTimeoutDiagnostics.DataKey] = Diagnostics;
    }

    public LlmStreamTimeoutException(
        string message,
        Exception? innerException,
        StreamTimeoutDiagnostics diagnostics)
        : base(message, innerException)
    {
        Diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        Data[StreamTimeoutDiagnostics.DataKey] = Diagnostics;
    }
}

