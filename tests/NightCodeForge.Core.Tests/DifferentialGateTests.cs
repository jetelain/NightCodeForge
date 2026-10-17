using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Core.Workflow.Stages;

namespace NightCodeForge.Core.Tests;

public class DifferentialGateTests
{
    [Fact]
    public async Task CoverageGate_Fails_WhenCoverageDrops()
    {
        var gate = new DeterministicCoverageGate(new Moq.Mock<IExecutionEnvironmentProvider>().Object);
        var task = new ForgeTask { Id = 1, WorkspaceId = 1, CurrentStageId = "coverage-gate" };
        var workspace = new Workspace { Id = 1, Name = "WS" };
        var context = new PipelineContext(task, workspace);

        // Baseline: 85%, Current: 80% (drop of 5%)
        context.DataBag.Set(PipelineDataBagKeys.TestsBaselineTotal, 20);
        context.DataBag.Set(PipelineDataBagKeys.TestsCurrentTotal, 20);
        context.DataBag.Set(PipelineDataBagKeys.CoverageBaselineLineRate, 0.85);
        context.DataBag.Set(PipelineDataBagKeys.CoverageCurrentLineRate, 0.80);

        var result = await gate.ExecuteAsync(context);

        Assert.False(result.IsSuccess);
        Assert.Equal(StandardStageIds.Implementation, result.TargetRoutingStageId);
        Assert.Contains("Coverage regression detected", result.ErrorMessage);
        Assert.Equal(result.ErrorMessage, context.DataBag.Get<string>(PipelineDataBagKeys.CoverageFailureReason));
    }

    [Fact]
    public async Task CoverageGate_FailsImmediately_WhenTestCountDecreases_AntiCheating()
    {
        var gate = new DeterministicCoverageGate(new Moq.Mock<IExecutionEnvironmentProvider>().Object);
        var task = new ForgeTask { Id = 1, WorkspaceId = 1, CurrentStageId = "coverage-gate" };
        var workspace = new Workspace { Id = 1, Name = "WS" };
        var context = new PipelineContext(task, workspace);

        // Baseline: 42 tests, Current: 40 tests (2 tests deleted/commented out)
        context.DataBag.Set(PipelineDataBagKeys.TestsBaselineTotal, 42);
        context.DataBag.Set(PipelineDataBagKeys.TestsCurrentTotal, 40);
        context.DataBag.Set(PipelineDataBagKeys.CoverageBaselineLineRate, 0.90);
        context.DataBag.Set(PipelineDataBagKeys.CoverageCurrentLineRate, 0.92);

        var result = await gate.ExecuteAsync(context);

        Assert.False(result.IsSuccess);
        Assert.Contains("Anti-cheating violation", result.ErrorMessage);
        Assert.Equal(StandardStageIds.Implementation, result.TargetRoutingStageId);
        Assert.Equal(result.ErrorMessage, context.DataBag.Get<string>(PipelineDataBagKeys.CoverageFailureReason));
    }

    [Fact]
    public async Task CoverageGate_Passes_WhenCoverageMaintainedOrIncreased()
    {
        var gate = new DeterministicCoverageGate(new Moq.Mock<IExecutionEnvironmentProvider>().Object);
        var task = new ForgeTask { Id = 1, WorkspaceId = 1, CurrentStageId = "coverage-gate" };
        var workspace = new Workspace { Id = 1, Name = "WS" };
        var context = new PipelineContext(task, workspace);

        context.DataBag.Set(PipelineDataBagKeys.TestsBaselineTotal, 50);
        context.DataBag.Set(PipelineDataBagKeys.TestsCurrentTotal, 55);
        context.DataBag.Set(PipelineDataBagKeys.CoverageBaselineLineRate, 0.82);
        context.DataBag.Set(PipelineDataBagKeys.CoverageCurrentLineRate, 0.85);

        var result = await gate.ExecuteAsync(context);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task WarningRatchet_Fails_WhenNewWarningsIntroduced()
    {
        var ratchet = new DeterministicWarningRatchetStage();
        var task = new ForgeTask { Id = 1, WorkspaceId = 1, CurrentStageId = "warning-ratchet" };
        var workspace = new Workspace { Id = 1, Name = "WS" };
        var context = new PipelineContext(task, workspace);

        // Baseline: 10, Current: 12
        context.DataBag.Set(PipelineDataBagKeys.WarningsBaselineTotal, 10);
        context.DataBag.Set(PipelineDataBagKeys.WarningsCurrentTotal, 12);
        context.DataBag.Set(PipelineDataBagKeys.WarningsNew, new List<string> { "CS0168: Variable 'x' is declared but never used" });

        var result = await ratchet.ExecuteAsync(context);

        Assert.False(result.IsSuccess);
        Assert.Equal(StandardStageIds.Implementation, result.TargetRoutingStageId);
        Assert.Contains("Warning ratchet violation", result.ErrorMessage);
        Assert.Contains("CS0168", result.ErrorMessage);
    }

    [Fact]
    public async Task WarningRatchet_Passes_WhenWarningCountDecreasedOrEqual()
    {
        var ratchet = new DeterministicWarningRatchetStage();
        var task = new ForgeTask { Id = 1, WorkspaceId = 1, CurrentStageId = "warning-ratchet" };
        var workspace = new Workspace { Id = 1, Name = "WS" };
        var context = new PipelineContext(task, workspace);

        context.DataBag.Set(PipelineDataBagKeys.WarningsBaselineTotal, 10);
        context.DataBag.Set(PipelineDataBagKeys.WarningsCurrentTotal, 8);

        var result = await ratchet.ExecuteAsync(context);

        Assert.True(result.IsSuccess);
    }
}
