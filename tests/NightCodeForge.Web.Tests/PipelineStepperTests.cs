using Bunit;
using NightCodeForge.Core.Domain;
using NightCodeForge.Web.Components.Monitoring;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class PipelineStepperTests : BunitContext
{
    private static List<StageDefinition> CreateTestStages() => new()
    {
        new() { SequenceOrder = 1, StageId = StandardStageIds.TaskReview, DisplayName = "ForgeTask Review", Kind = WorkflowStageKind.Agentic, Icon = "🔍" },
        new() { SequenceOrder = 2, StageId = StandardStageIds.PreImplementationBaseline, DisplayName = "Pre-Impl Baseline", Kind = WorkflowStageKind.Deterministic, Icon = "⚙️" },
        new() { SequenceOrder = 3, StageId = StandardStageIds.Implementation, DisplayName = "Implementation", Kind = WorkflowStageKind.Agentic, Icon = "💻" },
        new() { SequenceOrder = 4, StageId = StandardStageIds.PostImplementationGates, DisplayName = "Post-Impl Gates", Kind = WorkflowStageKind.Deterministic, Icon = "🧪" },
        new() { SequenceOrder = 5, StageId = StandardStageIds.CodeReviewAndFix, DisplayName = "Code Review", Kind = WorkflowStageKind.Agentic, Icon = "🧐" },
        new() { SequenceOrder = 6, StageId = StandardStageIds.PostReviewVerification, DisplayName = "Post-Review Verification", Kind = WorkflowStageKind.Deterministic, Icon = "🛡️" },
        new() { SequenceOrder = 7, StageId = StandardStageIds.Merge, DisplayName = "Merge Resolution", Kind = WorkflowStageKind.Deterministic, Icon = "🔀" }
    };

    [Fact]
    public void PipelineStepper_WhenNoStagesProvided_RendersEmpty()
    {
        var cut = Render<PipelineStepper>();
        var steps = cut.FindAll(".step-item");
        Assert.Empty(steps);
    }

    [Fact]
    public void PipelineStepper_RendersConfiguredStagesWithBadges()
    {
        var cut = Render<PipelineStepper>(p => p.Add(s => s.WorkflowStages, CreateTestStages()));

        var steps = cut.FindAll(".step-item");
        Assert.Equal(7, steps.Count);

        // Stages 1, 3, 5 are Agentic GPU
        Assert.Contains("⚡ GPU", steps[0].TextContent);
        Assert.Contains("ForgeTask Review", steps[0].TextContent);

        // Stage 2 is Deterministic CPU
        Assert.Contains("⚙️ CPU", steps[1].TextContent);
        Assert.Contains("Pre-Impl Baseline", steps[1].TextContent);

        // Stage 3 is Agentic GPU
        Assert.Contains("⚡ GPU", steps[2].TextContent);
        Assert.Contains("Implementation", steps[2].TextContent);

        // Stage 4 is Deterministic CPU
        Assert.Contains("⚙️ CPU", steps[3].TextContent);
        Assert.Contains("Post-Impl Gates", steps[3].TextContent);

        // Stage 5 is Agentic GPU
        Assert.Contains("⚡ GPU", steps[4].TextContent);
        Assert.Contains("Code Review", steps[4].TextContent);

        // Stage 6 is Deterministic CPU
        Assert.Contains("⚙️ CPU", steps[5].TextContent);
        Assert.Contains("Post-Review Verification", steps[5].TextContent);

        // Stage 7 is Deterministic CPU
        Assert.Contains("⚙️ CPU", steps[6].TextContent);
        Assert.Contains("Merge Resolution", steps[6].TextContent);
    }

    [Fact]
    public void PipelineStepper_WhenStageRunning_DisplaysActiveClassAndPulsingSpinner()
    {
        var cut = Render<PipelineStepper>(p => p
            .Add(c => c.WorkflowStages, CreateTestStages())
            .Add(c => c.CurrentStageId, "implementation")
            .Add(c => c.CurrentStageStatus, StageExecutionStatus.Running));

        var steps = cut.FindAll(".step-item");
        var stage3 = steps[2];

        Assert.Contains("step-active", stage3.ClassName);
        Assert.NotEmpty(stage3.QuerySelectorAll(".spinner-pulse"));

        // Stages 1 and 2 should be marked completed
        Assert.Contains("step-completed", steps[0].ClassName);
        Assert.Contains("step-completed", steps[1].ClassName);

        // Connector between stage 2 and stage 3 should be active
        var connectors = cut.FindAll(".step-connector");
        Assert.Contains("connector-completed", connectors[1].ClassName);
    }

    [Fact]
    public void PipelineStepper_WhenDeterministicGateResultReceived_DisplaysMetricsBadge()
    {
        var cut = Render<PipelineStepper>(p => p.Add(c => c.WorkflowStages, CreateTestStages()));

        cut.InvokeAsync(() =>
        {
            cut.Instance.ApplyGateResult("deterministic-post-impl", true, "+1.4% Coverage");
        });

        var steps = cut.FindAll(".step-item");
        var stage4 = steps[3];

        Assert.Contains("step-completed", stage4.ClassName);
        Assert.Contains("✓", stage4.TextContent);
        Assert.Contains("+1.4% Coverage", stage4.TextContent);
        Assert.NotEmpty(stage4.QuerySelectorAll(".step-metrics-badge"));
    }

    [Fact]
    public void PipelineStepper_WhenWaitingForHuman_RendersWaitingIconAndClass()
    {
        var cut = Render<PipelineStepper>(p => p.Add(c => c.WorkflowStages, CreateTestStages()));

        cut.InvokeAsync(() =>
        {
            cut.Instance.ApplyStageTransition(StandardStageIds.Merge, StageExecutionStatus.WaitingForHuman);
        });

        var steps = cut.FindAll(".step-item");
        var stage7 = steps[6];

        Assert.Contains("step-waiting", stage7.ClassName);
        Assert.Contains("✋", stage7.TextContent);
    }

    [Fact]
    public void PipelineStepper_WhenStageIdRunning_CorrectlyActivatesStep()
    {
        var cut = Render<PipelineStepper>(p => p
            .Add(c => c.WorkflowStages, CreateTestStages())
            .Add(c => c.CurrentStageId, "task-review")
            .Add(c => c.CurrentStageStatus, StageExecutionStatus.Running));

        var steps = cut.FindAll(".step-item");
        var stage1 = steps[0];

        Assert.Contains("step-active", stage1.ClassName);
        Assert.NotEmpty(stage1.QuerySelectorAll(".spinner-pulse"));
    }

    [Fact]
    public void PipelineStepper_WhenCurrentStageStatusNullOrPending_RendersPendingWithoutSpinners()
    {
        var cut = Render<PipelineStepper>(p => p
            .Add(c => c.WorkflowStages, CreateTestStages())
            .Add(c => c.CurrentStageId, "task-review")
            .Add(c => c.CurrentStageStatus, null));

        var steps = cut.FindAll(".step-item");
        var stage1 = steps[0];

        // Should be step-pending, not step-active
        Assert.Contains("step-pending", stage1.ClassName);
        Assert.DoesNotContain("step-active", stage1.ClassName);
        Assert.Empty(cut.FindAll(".spinner-pulse"));
    }

    [Fact]
    public void PipelineStepper_WhenIsWaitingForHumanFlagIsTrue_RendersWaitingIconInsteadOfSpinner()
    {
        var cut = Render<PipelineStepper>(p => p
            .Add(c => c.WorkflowStages, CreateTestStages())
            .Add(c => c.CurrentStageId, "task-review")
            .Add(c => c.CurrentStageStatus, StageExecutionStatus.Running)
            .Add(c => c.IsWaitingForHuman, true));

        var steps = cut.FindAll(".step-item");
        var stage1 = steps[0];

        // Honors IsWaitingForHuman override over Running
        Assert.Contains("step-waiting", stage1.ClassName);
        Assert.DoesNotContain("step-active", stage1.ClassName);
        Assert.Contains("✋", stage1.TextContent);
        Assert.Empty(cut.FindAll(".spinner-pulse"));
    }

    [Fact]
    public void PipelineStepper_WhenStageHasFailedRunThenSuccessfulRun_ReflectsLastRunStatusCompleted()
    {
        var conv1 = new Conversation
        {
            Id = 1,
            TaskId = 42,
            StageId = StandardStageIds.TaskReview,
            AttemptIndex = 1,
            Status = StageExecutionStatus.Failed,
            StartedAt = DateTime.UtcNow.AddMinutes(-10)
        };
        var conv2 = new Conversation
        {
            Id = 2,
            TaskId = 42,
            StageId = StandardStageIds.TaskReview,
            AttemptIndex = 2,
            Status = StageExecutionStatus.Completed,
            StartedAt = DateTime.UtcNow.AddMinutes(-5)
        };

        var cut = Render<PipelineStepper>(p => p
            .Add(c => c.WorkflowStages, CreateTestStages())
            .Add(c => c.Conversations, new List<Conversation> { conv1, conv2 }));

        var steps = cut.FindAll(".step-item");
        var stage1 = steps[0];

        // Should reflect the latest run (Attempt 2: Completed), NOT the older failed run
        Assert.Contains("step-completed", stage1.ClassName);
        Assert.DoesNotContain("step-failed", stage1.ClassName);
        Assert.Contains("✓", stage1.TextContent);
    }

    [Fact]
    public void PipelineStepper_WhenStageHasSuccessfulRunThenFailedRun_ReflectsLastRunStatusFailed()
    {
        var conv1 = new Conversation
        {
            Id = 1,
            TaskId = 42,
            StageId = StandardStageIds.TaskReview,
            AttemptIndex = 1,
            Status = StageExecutionStatus.Completed,
            StartedAt = DateTime.UtcNow.AddMinutes(-10)
        };
        var conv2 = new Conversation
        {
            Id = 2,
            TaskId = 42,
            StageId = StandardStageIds.TaskReview,
            AttemptIndex = 2,
            Status = StageExecutionStatus.Failed,
            StartedAt = DateTime.UtcNow.AddMinutes(-5)
        };

        var cut = Render<PipelineStepper>(p => p
            .Add(c => c.WorkflowStages, CreateTestStages())
            .Add(c => c.Conversations, new List<Conversation> { conv1, conv2 }));

        var steps = cut.FindAll(".step-item");
        var stage1 = steps[0];

        // Should reflect the latest run (Attempt 2: Failed)
        Assert.Contains("step-failed", stage1.ClassName);
        Assert.DoesNotContain("step-completed", stage1.ClassName);
        Assert.Contains("✕", stage1.TextContent);
        Assert.NotEmpty(stage1.QuerySelectorAll(".status-overlay-failed"));
    }

    [Fact]
    public void PipelineStepper_WhenPipelineAdvancesToLaterStage_PreviousStageTransitionsToCompleted()
    {
        var cut = Render<PipelineStepper>(p => p
            .Add(c => c.WorkflowStages, CreateTestStages())
            .Add(c => c.CurrentStageId, StandardStageIds.TaskReview)
            .Add(c => c.CurrentStageStatus, StageExecutionStatus.Failed));

        var steps = cut.FindAll(".step-item");
        Assert.Contains("step-failed", steps[0].ClassName);

        // Advance pipeline to stage 2
        cut.InvokeAsync(() =>
        {
            cut.Instance.ApplyStageTransition(StandardStageIds.PreImplementationBaseline, StageExecutionStatus.Running);
        });

        steps = cut.FindAll(".step-item");
        // Stage 1 must now be completed since pipeline progressed to stage 2
        Assert.Contains("step-completed", steps[0].ClassName);
        Assert.DoesNotContain("step-failed", steps[0].ClassName);
        Assert.Contains("step-active", steps[1].ClassName);
    }

    [Fact]
    public void PipelineStepper_WhenTaskFailsAtLaterStage_PrecedingPendingStagesTransitionToCompleted()
    {
        // Stage 1 (Agentic) succeeded and has conversation.
        // Stage 2 (Deterministic) succeeded (no conversation in Conversations).
        // Stage 3 (Agentic) failed and task OverallStatus is Failed.
        var conv1 = new Conversation
        {
            Id = 1,
            TaskId = 42,
            StageId = StandardStageIds.TaskReview,
            AttemptIndex = 1,
            Status = StageExecutionStatus.Completed,
            StartedAt = DateTime.UtcNow.AddMinutes(-10)
        };

        var cut = Render<PipelineStepper>(p => p
            .Add(c => c.WorkflowStages, CreateTestStages())
            .Add(c => c.Conversations, new List<Conversation> { conv1 })
            .Add(c => c.CurrentStageId, StandardStageIds.Implementation)
            .Add(c => c.OverallStatus, TaskOverallStatus.Failed));

        var steps = cut.FindAll(".step-item");

        // Stage 1 should be completed (from conversation)
        Assert.Contains("step-completed", steps[0].ClassName);

        // Stage 2 (preceding deterministic stage with no conversation) must be marked completed
        Assert.Contains("step-completed", steps[1].ClassName);
        Assert.DoesNotContain("step-pending", steps[1].ClassName);

        // Stage 3 (current failing stage) should be failed
        Assert.Contains("step-failed", steps[2].ClassName);

        // Stage 4 (subsequent stage) should remain pending
        Assert.Contains("step-pending", steps[3].ClassName);
    }
}
