namespace NightCodeForge.Core.Tests;

using NightCodeForge.Core.Resilience;
using Xunit;

public class RetryPolicyOptionsTests
{
    [Fact]
    public void RetryPolicyOptions_DefaultValues_AreExpected()
    {
        var options = new RetryPolicyOptions();

        Assert.Equal(3, options.MaxRetries);
        Assert.Equal(TimeSpan.FromSeconds(1.0), options.BaseDelay);
        Assert.Equal(1.0, options.BaseDelaySeconds);
        Assert.Equal(TimeSpan.FromSeconds(15.0), options.MaxDelay);
        Assert.Equal(15.0, options.MaxDelaySeconds);
        Assert.Equal(TimeSpan.FromSeconds(30.0), options.MaxRetryDuration);
        Assert.Equal(30.0, options.MaxRetryDurationSeconds);
        Assert.Equal(0, options.MaxTimeoutRetries);
        Assert.Null(options.PerAttemptTimeout);
        Assert.Null(options.PerAttemptTimeoutSeconds);
    }

    [Fact]
    public void LlmRetryPolicyOptions_DefaultValues_AreExpected()
    {
        var options = new LlmRetryPolicyOptions();

        Assert.Equal(20, options.MaxRetries);
        Assert.Equal(TimeSpan.FromSeconds(1.0), options.BaseDelay);
        Assert.Equal(1.0, options.BaseDelaySeconds);
        Assert.Equal(TimeSpan.FromSeconds(30.0), options.MaxDelay);
        Assert.Equal(30.0, options.MaxDelaySeconds);
        Assert.Equal(TimeSpan.FromSeconds(1800.0), options.MaxRetryDuration);
        Assert.Equal(1800.0, options.MaxRetryDurationSeconds);
        Assert.Equal(3, options.MaxTimeoutRetries);
    }

    [Fact]
    public void HelperProperties_UpdateUnderlyingTimeSpans()
    {
        var options = new RetryPolicyOptions
        {
            BaseDelaySeconds = 5.5,
            MaxDelaySeconds = 40.0,
            MaxRetryDurationSeconds = 120.0,
            PerAttemptTimeoutSeconds = 15.0
        };

        Assert.Equal(TimeSpan.FromSeconds(5.5), options.BaseDelay);
        Assert.Equal(TimeSpan.FromSeconds(40.0), options.MaxDelay);
        Assert.Equal(TimeSpan.FromSeconds(120.0), options.MaxRetryDuration);
        Assert.Equal(TimeSpan.FromSeconds(15.0), options.PerAttemptTimeout);

        options.PerAttemptTimeoutSeconds = null;
        Assert.Null(options.PerAttemptTimeout);
    }

    [Fact]
    public void HelperProperties_IgnoreInvalidOrNegativeValues_AndClearNonPositivePerAttemptTimeout()
    {
        var options = new RetryPolicyOptions
        {
            BaseDelaySeconds = 4.0,
            MaxDelaySeconds = 20.0,
            MaxRetryDurationSeconds = 60.0,
            PerAttemptTimeoutSeconds = 10.0
        };

        options.BaseDelaySeconds = -1.0;
        options.MaxDelaySeconds = double.NaN;
        options.MaxRetryDurationSeconds = double.PositiveInfinity;
        Assert.Equal(TimeSpan.FromSeconds(4.0), options.BaseDelay);
        Assert.Equal(TimeSpan.FromSeconds(20.0), options.MaxDelay);
        Assert.Equal(TimeSpan.FromSeconds(60.0), options.MaxRetryDuration);

        options.PerAttemptTimeoutSeconds = 0.0;
        Assert.Null(options.PerAttemptTimeout);

        options.PerAttemptTimeoutSeconds = 10.0;
        options.PerAttemptTimeoutSeconds = -5.0;
        Assert.Null(options.PerAttemptTimeout);
    }
}

