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
    public const int DefaultLlmMaxTimeoutRetries = 1;

    public LlmRetryPolicyOptions()
    {
        MaxRetries = DefaultLlmMaxRetries;
        BaseDelay = TimeSpan.FromSeconds(DefaultLlmBaseDelaySeconds);
        MaxDelay = TimeSpan.FromSeconds(DefaultLlmMaxDelaySeconds);
        MaxRetryDuration = TimeSpan.FromSeconds(DefaultLlmMaxRetryDurationSeconds);
        MaxTimeoutRetries = DefaultLlmMaxTimeoutRetries;
    }
}

