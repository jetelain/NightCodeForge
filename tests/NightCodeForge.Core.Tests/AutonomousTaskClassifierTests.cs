using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Streaming;
using NightCodeForge.Core.Workflow;
using Xunit;

namespace NightCodeForge.Core.Tests;

public class AutonomousTaskClassifierTests
{
    [Theory]
    [InlineData(1, "implementation", true)]
    [InlineData(42, "code-review-fix", true)]
    [InlineData(100, null, true)]
    [InlineData(0, "implementation", false)]
    [InlineData(-1, "implementation", false)]
    [InlineData(null, "implementation", false)]
    [InlineData(0, StandardStageIds.Ideation, false)]
    [InlineData(42, StandardStageIds.Ideation, false)]
    [InlineData(42, "IDEATION", false)]
    public void IsAutonomousTask_TaskIdAndStageId_ClassifiesCorrectly(int? taskId, string? stageId, bool expected)
    {
        var actual = AutonomousTaskClassifier.IsAutonomousTask(taskId, stageId);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void IsAutonomousTask_AgentActivityEvent_ClassifiesCorrectly()
    {
        Assert.False(AutonomousTaskClassifier.IsAutonomousTask((AgentActivityEvent)null!));

        var ideationEvent = new AgentActivityEvent(
            "sess-1",
            0,
            StandardStageIds.Ideation,
            AgentActivityKind.StageStarted,
            "Ideation starting",
            DateTime.UtcNow);
        Assert.False(AutonomousTaskClassifier.IsAutonomousTask(ideationEvent));

        var autonomousEvent = new AgentActivityEvent(
            "sess-2",
            42,
            StandardStageIds.Implementation,
            AgentActivityKind.StageStarted,
            "Impl starting",
            DateTime.UtcNow);
        Assert.True(AutonomousTaskClassifier.IsAutonomousTask(autonomousEvent));
    }

    [Fact]
    public void IsAutonomousTask_ActiveTaskSummary_ClassifiesCorrectly()
    {
        Assert.False(AutonomousTaskClassifier.IsAutonomousTask((ActiveTaskSummary)null!));

        var ideationSummary = new ActiveTaskSummary(
            0,
            "Task #0",
            StandardStageIds.Ideation,
            1,
            "default",
            DateTime.UtcNow);
        Assert.False(AutonomousTaskClassifier.IsAutonomousTask(ideationSummary));

        var autonomousSummary = new ActiveTaskSummary(
            42,
            "Refactor Parser",
            StandardStageIds.Implementation,
            1,
            "default",
            DateTime.UtcNow);
        Assert.True(AutonomousTaskClassifier.IsAutonomousTask(autonomousSummary));
    }
}
