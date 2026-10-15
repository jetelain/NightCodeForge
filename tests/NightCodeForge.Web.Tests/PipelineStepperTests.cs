using Bunit;
using NightCodeForge.Web.Components.Monitoring;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class PipelineStepperTests : BunitContext
{
    [Fact]
    public void PipelineStepper_RendersAllSevenStagesWithBadges()
    {
        var cut = Render<PipelineStepper>();

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

        // Stage 7 is Merge Gate
        Assert.Contains("🔀 Gate", steps[6].TextContent);
        Assert.Contains("Merge Resolution", steps[6].TextContent);
    }

    [Fact]
    public void PipelineStepper_WhenStageRunning_DisplaysActiveClassAndPulsingSpinner()
    {
        var cut = Render<PipelineStepper>(p => p
            .Add(c => c.CurrentStageId, "ImplementationStage")
            .Add(c => c.CurrentStageStatus, "Running"));

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
        var cut = Render<PipelineStepper>();

        cut.InvokeAsync(() =>
        {
            cut.Instance.ApplyGateResult("CoverageGate", true, "+1.4% Coverage");
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
        var cut = Render<PipelineStepper>();

        cut.InvokeAsync(() =>
        {
            cut.Instance.ApplyStageTransition("MergeGate", "WaitingForHuman");
        });

        var steps = cut.FindAll(".step-item");
        var stage7 = steps[6];

        Assert.Contains("step-waiting", stage7.ClassName);
        Assert.Contains("✋", stage7.TextContent);
    }

    [Fact]
    public void PipelineStepper_WhenHyphenatedStageIdRunning_CorrectlyActivatesStep()
    {
        var cut = Render<PipelineStepper>(p => p
            .Add(c => c.CurrentStageId, "task-review")
            .Add(c => c.CurrentStageStatus, "Running"));

        var steps = cut.FindAll(".step-item");
        var stage1 = steps[0];

        Assert.Contains("step-active", stage1.ClassName);
        Assert.NotEmpty(stage1.QuerySelectorAll(".spinner-pulse"));
    }
}
