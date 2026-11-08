using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Security;

namespace NightCodeForge.Core.Tests.Security;

public class AirGappedSecurityTests
{
    [Fact]
    public void AirGappedOperationException_Constructors_SetExpectedProperties()
    {
        var defaultEx = new AirGappedOperationException();
        Assert.NotNull(defaultEx.Message);
        Assert.Contains("Air-Gapped", defaultEx.Message);
        Assert.IsAssignableFrom<InvalidOperationException>(defaultEx);

        var customEx = new AirGappedOperationException("Custom air-gapped message");
        Assert.Equal("Custom air-gapped message", customEx.Message);

        var inner = new Exception("Inner root cause");
        var wrappedEx = new AirGappedOperationException("Wrapped air-gap failure", inner);
        Assert.Equal("Wrapped air-gap failure", wrappedEx.Message);
        Assert.Same(inner, wrappedEx.InnerException);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("1", true)]
    [InlineData("yes", true)]
    [InlineData("YES", true)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("no", false)]
    [InlineData(null, false)]
    public void SecurityOptions_AirGappedMode_HonorsEnvironmentVariable(string? envValue, bool expected)
    {
        var originalEnv = Environment.GetEnvironmentVariable(SecurityOptions.AirGappedEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(SecurityOptions.AirGappedEnvironmentVariable, envValue);

            var options = new SecurityOptions { AirGappedMode = false };
            Assert.Equal(expected, options.AirGappedMode);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SecurityOptions.AirGappedEnvironmentVariable, originalEnv);
        }
    }

    [Fact]
    public void SecurityOptions_AirGappedMode_ExplicitTrue_OverridesEvenIfEnvNotSet()
    {
        var originalEnv = Environment.GetEnvironmentVariable(SecurityOptions.AirGappedEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(SecurityOptions.AirGappedEnvironmentVariable, null);

            var options = new SecurityOptions { AirGappedMode = true };
            Assert.True(options.AirGappedMode);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SecurityOptions.AirGappedEnvironmentVariable, originalEnv);
        }
    }
}

