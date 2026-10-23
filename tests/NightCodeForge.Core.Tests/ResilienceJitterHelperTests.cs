using NightCodeForge.Core.Resilience;
using Xunit;

namespace NightCodeForge.Core.Tests;

public class ResilienceJitterHelperTests
{
    [Fact]
    public void CalculateJitteredDelay_StaysWithinExpectedBounds()
    {
        var baseDelay = TimeSpan.FromSeconds(1);
        var maxDelay = TimeSpan.FromSeconds(15);

        for (int attempt = 1; attempt <= 10; attempt++)
        {
            var factor = Math.Pow(2.0, attempt - 1);
            var expectedCap = Math.Min(maxDelay.TotalMilliseconds, baseDelay.TotalMilliseconds * factor);
            var expectedMin = expectedCap * 0.5;

            for (int trial = 0; trial < 20; trial++)
            {
                var delay = ResilienceJitterHelper.CalculateJitteredDelay(attempt, baseDelay, maxDelay);
                Assert.True(delay.TotalMilliseconds >= expectedMin, $"Delay {delay.TotalMilliseconds} should be >= min {expectedMin}");
                Assert.True(delay.TotalMilliseconds <= expectedCap, $"Delay {delay.TotalMilliseconds} should be <= cap {expectedCap}");
            }
        }
    }

    [Fact]
    public void CalculateJitteredDelay_RespectsMaxDelayCeiling()
    {
        var baseDelay = TimeSpan.FromSeconds(1);
        var maxDelay = TimeSpan.FromSeconds(5);

        for (int attempt = 5; attempt <= 20; attempt++)
        {
            var delay = ResilienceJitterHelper.CalculateJitteredDelay(attempt, baseDelay, maxDelay);
            Assert.True(delay <= maxDelay, $"Delay {delay.TotalMilliseconds}ms exceeded max delay {maxDelay.TotalMilliseconds}ms");
            Assert.True(delay.TotalMilliseconds >= maxDelay.TotalMilliseconds * 0.5);
        }
    }

    [Fact]
    public void CalculateJitteredDelay_Deterministic_WithPredictableRandom()
    {
        var baseDelay = TimeSpan.FromSeconds(2);
        var maxDelay = TimeSpan.FromSeconds(30);

        // Random initialized with seed 42 will produce exact same sequence
        var rng1 = new Random(42);
        var rng2 = new Random(42);

        var delay1 = ResilienceJitterHelper.CalculateJitteredDelay(2, baseDelay, maxDelay, rng1);
        var delay2 = ResilienceJitterHelper.CalculateJitteredDelay(2, baseDelay, maxDelay, rng2);

        Assert.Equal(delay1, delay2);
    }
}
