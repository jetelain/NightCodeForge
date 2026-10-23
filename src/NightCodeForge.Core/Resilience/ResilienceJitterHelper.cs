namespace NightCodeForge.Core.Resilience;

/// <summary>
/// Helper for calculating exponential backoff delays with random jitter to prevent synchronized retry storms.
/// </summary>
public static class ResilienceJitterHelper
{
    /// <summary>
    /// Calculates a jittered exponential backoff delay for the specified retry attempt.
    /// </summary>
    /// <param name="attempt">The 1-based attempt index (1 for the first retry, 2 for the second, etc.).</param>
    /// <param name="baseDelay">The baseline initial delay.</param>
    /// <param name="maxDelay">The maximum ceiling delay.</param>
    /// <param name="random">Optional custom random instance for deterministic unit testing.</param>
    /// <returns>A TimeSpan representing the jittered backoff delay.</returns>
    public static TimeSpan CalculateJitteredDelay(
        int attempt,
        TimeSpan baseDelay,
        TimeSpan maxDelay,
        Random? random = null)
    {
        var baseMs = Math.Max(1.0, baseDelay.TotalMilliseconds);
        var maxMs = Math.Max(baseMs, maxDelay.TotalMilliseconds);
        var factor = Math.Pow(2.0, Math.Max(0, attempt - 1));
        var exponentialMs = Math.Min(maxMs, baseMs * factor);

        // Spread delay between lower bound factor and 100% of the calculated exponential ceiling
        var minMs = exponentialMs * ResilienceConstants.JitterLowerBoundFactor;
        var rng = random ?? Random.Shared;
        var delayMs = minMs + (rng.NextDouble() * (exponentialMs - minMs));

        return TimeSpan.FromMilliseconds(delayMs);
    }
}
