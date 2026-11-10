namespace NightCodeForge.Core.Resilience;

/// <summary>
/// Dedicated retry policy options for LLM inference endpoints surviving multi-minute outages (e.g. server restarts, cold weight reloads).
/// </summary>
public class LlmRetryPolicyOptions : RetryPolicyOptions
{
    public new const string SectionName = "LlmRetryPolicy";

    public const int DefaultLlmMaxRetries = 20;
    public const double DefaultLlmBaseDelaySeconds = 1.0;
    public const double DefaultLlmMaxDelaySeconds = 30.0;
    public const double DefaultLlmMaxRetryDurationSeconds = 1800.0; // 30 minutes
    public const int DefaultLlmMaxTimeoutRetries = 3;
    public const double DefaultStreamIdleTimeoutSeconds = 300.0; // 5 minutes

    private TimeSpan? _streamIdleTimeout;

    /// <summary>
    /// Gets whether <see cref="StreamIdleTimeout"/> was explicitly configured rather than using the default value.
    /// </summary>
    public bool IsStreamIdleTimeoutExplicitlySet => _streamIdleTimeout.HasValue;

    /// <summary>
    /// Maximum silence interval between consecutive streamed chunks once generation has started.
    /// Defaults to 5 minutes (300 seconds).
    /// </summary>
    public TimeSpan StreamIdleTimeout
    {
        get => _streamIdleTimeout ?? TimeSpan.FromSeconds(DefaultStreamIdleTimeoutSeconds);
        set => _streamIdleTimeout = value;
    }

    /// <summary>
    /// Helper for configuration binding in seconds.
    /// </summary>
    public double? StreamIdleTimeoutSeconds
    {
        get => StreamIdleTimeout.TotalSeconds;
        set
        {
            if (value is > 0 && double.IsFinite(value.Value))
            {
                StreamIdleTimeout = TimeSpan.FromSeconds(value.Value);
            }
        }
    }

    public LlmRetryPolicyOptions()
    {
        MaxRetries = DefaultLlmMaxRetries;
        BaseDelay = TimeSpan.FromSeconds(DefaultLlmBaseDelaySeconds);
        MaxDelay = TimeSpan.FromSeconds(DefaultLlmMaxDelaySeconds);
        MaxRetryDuration = TimeSpan.FromSeconds(DefaultLlmMaxRetryDurationSeconds);
        MaxTimeoutRetries = DefaultLlmMaxTimeoutRetries;
    }
}

