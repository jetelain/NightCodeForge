using System.Text.Json.Serialization;

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
    /// Initial base delay in seconds. Optional helper for JSON / config binding (e.g. appsettings.json / config.json).
    /// When set to a finite non-negative value, updates <see cref="BaseDelay"/>.
    /// </summary>
    public double? BaseDelaySeconds
    {
        get => BaseDelay.TotalSeconds;
        set
        {
            if (value is >= 0 && double.IsFinite(value.Value))
            {
                BaseDelay = TimeSpan.FromSeconds(value.Value);
            }
        }
    }

    /// <summary>
    /// Initial base delay before applying exponential backoff and random jitter.
    /// </summary>
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromSeconds(DefaultBaseDelaySeconds);

    /// <summary>
    /// Maximum ceiling for the exponential delay in seconds. Optional helper for JSON / config binding (e.g. appsettings.json / config.json).
    /// When set to a finite non-negative value, updates <see cref="MaxDelay"/>.
    /// </summary>
    public double? MaxDelaySeconds
    {
        get => MaxDelay.TotalSeconds;
        set
        {
            if (value is >= 0 && double.IsFinite(value.Value))
            {
                MaxDelay = TimeSpan.FromSeconds(value.Value);
            }
        }
    }

    /// <summary>
    /// Maximum ceiling for the exponential delay before adding jitter.
    /// </summary>
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(DefaultMaxDelaySeconds);

    /// <summary>
    /// Maximum total duration in seconds spent retrying transient errors before exhausting the retry policy.
    /// Optional helper for JSON / config binding (e.g. appsettings.json / config.json).
    /// When set to a finite non-negative value, updates <see cref="MaxRetryDuration"/>.
    /// </summary>
    public double? MaxRetryDurationSeconds
    {
        get => MaxRetryDuration.TotalSeconds;
        set
        {
            if (value is >= 0 && double.IsFinite(value.Value))
            {
                MaxRetryDuration = TimeSpan.FromSeconds(value.Value);
            }
        }
    }

    /// <summary>
    /// Maximum total duration spent retrying transient errors before exhausting the retry policy.
    /// </summary>
    public TimeSpan MaxRetryDuration { get; set; } = TimeSpan.FromSeconds(DefaultMaxRetryDurationSeconds);

    /// <summary>
    /// Maximum number of retry attempts for HTTP request timeouts before failing fast.
    /// Defaults to 0 so non-LLM clients fail fast on timeout.
    /// </summary>
    public int MaxTimeoutRetries { get; set; } = 0;

    /// <summary>
    /// Optional per-attempt timeout in seconds. Optional helper for JSON / config binding (e.g. appsettings.json / config.json).
    /// When set to a finite positive value, updates <see cref="PerAttemptTimeout"/>; otherwise clears it.
    /// </summary>
    public double? PerAttemptTimeoutSeconds
    {
        get => PerAttemptTimeout?.TotalSeconds;
        set
        {
            if (value is > 0 && double.IsFinite(value.Value))
            {
                PerAttemptTimeout = TimeSpan.FromSeconds(value.Value);
            }
            else
            {
                PerAttemptTimeout = null;
            }
        }
    }

    /// <summary>
    /// Optional per-attempt timeout enforced within the retry handler.
    /// When set, each attempt is cancelled individually after this duration without aborting the outer HttpClient.
    /// </summary>
    public TimeSpan? PerAttemptTimeout { get; set; }

    /// <summary>
    /// Optional synchronous notification callback invoked on each retry attempt.
    /// </summary>
    [JsonIgnore]
    public Action<RetryNotificationContext>? OnRetry { get; set; }

    /// <summary>
    /// Optional asynchronous notification callback invoked on each retry attempt.
    /// </summary>
    [JsonIgnore]
    public Func<RetryNotificationContext, CancellationToken, Task>? OnRetryAsync { get; set; }
}
