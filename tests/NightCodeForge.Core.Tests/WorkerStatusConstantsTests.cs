using NightCodeForge.Core.Domain;
using Xunit;

namespace NightCodeForge.Core.Tests;

public class WorkerStatusConstantsTests
{
    [Theory]
    [InlineData("Idle", true)]
    [InlineData("idle", true)]
    [InlineData("IDLE", true)]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("Running", false)]
    public void IsIdle_RecognizesIdleAndEmptyStrings(string? input, bool expected)
    {
        Assert.Equal(expected, WorkerStatusConstants.IsIdle(input));
    }

    [Theory]
    [InlineData("Running", true)]
    [InlineData("running", true)]
    [InlineData("RUNNING", true)]
    [InlineData("Idle", false)]
    [InlineData(null, false)]
    public void IsRunning_RecognizesRunning(string? input, bool expected)
    {
        Assert.Equal(expected, WorkerStatusConstants.IsRunning(input));
    }

    [Theory]
    [InlineData("WaitingForHuman", true)]
    [InlineData("waitingforhuman", true)]
    [InlineData("waiting_for_human", true)]
    [InlineData("waiting", true)]
    [InlineData("WAITING", true)]
    [InlineData("Running", false)]
    [InlineData(null, false)]
    public void IsWaitingForHuman_RecognizesAllVariants(string? input, bool expected)
    {
        Assert.Equal(expected, WorkerStatusConstants.IsWaitingForHuman(input));
    }

    [Theory]
    [InlineData("Blocked", true)]
    [InlineData("blocked", true)]
    [InlineData("BLOCKED", true)]
    [InlineData("Running", false)]
    [InlineData(null, false)]
    public void IsBlocked_RecognizesBlocked(string? input, bool expected)
    {
        Assert.Equal(expected, WorkerStatusConstants.IsBlocked(input));
    }

    [Fact]
    public void CanonicalConstants_HaveExpectedValues()
    {
        Assert.Equal("Idle", WorkerStatusConstants.Idle);
        Assert.Equal("Running", WorkerStatusConstants.Running);
        Assert.Equal("WaitingForHuman", WorkerStatusConstants.WaitingForHuman);
        Assert.Equal("Blocked", WorkerStatusConstants.Blocked);
    }
}
