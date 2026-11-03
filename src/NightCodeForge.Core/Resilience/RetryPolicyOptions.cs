namespace NightCodeForge.Core.Resilience;

/// <summary>
/// Configuration options governing transient network and HTTP error retries with exponential backoff and jitter.
/// </summary>
public class RetryPolicyOptions
{
    public const string SectionName = "RetryPolicy";

    public const int DefaultMaxRetries = 3;
    public const double DefaultBaseDelaySeconds = 1.0;
    public const double DefaultMaxDelaySeconds = 15.0;
    public const double DefaultMaxRetryDurationSeconds = 30.0;

    /// <summary>
    /// Maximum number of retry attempts for transient errors before failing fast.
    /// </summary>
    public int MaxRetries { get; set; } = DefaultMaxRetries;

    /// <summary>
    /// Initial base delay before applying exponential backoff and random jitter.
    /// </summary>
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromSeconds(DefaultBaseDelaySeconds);

    /// <summary>
    /// Maximum ceiling for the exponential delay before adding jitter.
    /// </summary>
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(DefaultMaxDelaySeconds);

    /// <summary>
    /// Maximum total duration spent retrying transient errors before exhausting the retry policy.
    /// </summary>
    public TimeSpan MaxRetryDuration { get; set; } = TimeSpan.FromSeconds(DefaultMaxRetryDurationSeconds);

    /// <summary>
    /// Optional synchronous notification callback invoked on each retry attempt.
    /// </summary>
    public Action<RetryNotificationContext>? OnRetry { get; set; }

    /// <summary>
    /// Optional asynchronous notification callback invoked on each retry attempt.
    /// </summary>
    public Func<RetryNotificationContext, CancellationToken, Task>? OnRetryAsync { get; set; }
}
