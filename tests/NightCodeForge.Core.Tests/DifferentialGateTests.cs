using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Core.Workflow.Stages;

namespace NightCodeForge.Core.Tests;

public class DifferentialGateTests
{
    [Fact]
    public async Task CoverageGate_Fails_WhenCoverageDrops()
    {
        var gate = new DeterministicCoverageGate();
        var task = new ForgeTask { Id = 1, WorkspaceId = 1, CurrentStageId = "coverage-gate" };
        var workspace = new Workspace { Id = 1, Name = "WS" };
        var context = new PipelineContext(task, workspace);

        // Baseline: 85%, Current: 80% (drop of 5%)
        context.DataBag.Set("tests.baseline.total", 20);
        context.DataBag.Set("tests.current.total", 20);
        context.DataBag.Set("coverage.baseline.line_rate", 0.85);
        context.DataBag.Set("coverage.current.line_rate", 0.80);

        var result = await gate.ExecuteAsync(context);

        Assert.False(result.IsSuccess);
        Assert.Equal("implementation", result.TargetRoutingStageId);
        Assert.Contains("Coverage regression detected", result.ErrorMessage);
    }

    [Fact]
    public async Task CoverageGate_FailsImmediately_WhenTestCountDecreases_AntiCheating()
    {
        var gate = new DeterministicCoverageGate();
        var task = new ForgeTask { Id = 1, WorkspaceId = 1, CurrentStageId = "coverage-gate" };
        var workspace = new Workspace { Id = 1, Name = "WS" };
        var context = new PipelineContext(task, workspace);

        // Baseline: 42 tests, Current: 40 tests (2 tests deleted/commented out)
        context.DataBag.Set("tests.baseline.total", 42);
        context.DataBag.Set("tests.current.total", 40);
        context.DataBag.Set("coverage.baseline.line_rate", 0.90);
        context.DataBag.Set("coverage.current.line_rate", 0.92);

        var result = await gate.ExecuteAsync(context);

        Assert.False(result.IsSuccess);
        Assert.Contains("Anti-cheating violation", result.ErrorMessage);
        Assert.Equal("implementation", result.TargetRoutingStageId);
    }

    [Fact]
    public async Task CoverageGate_Passes_WhenCoverageMaintainedOrIncreased()
    {
        var gate = new DeterministicCoverageGate();
        var task = new ForgeTask { Id = 1, WorkspaceId = 1, CurrentStageId = "coverage-gate" };
        var workspace = new Workspace { Id = 1, Name = "WS" };
        var context = new PipelineContext(task, workspace);

        context.DataBag.Set("tests.baseline.total", 50);
        context.DataBag.Set("tests.current.total", 55);
        context.DataBag.Set("coverage.baseline.line_rate", 0.82);
        context.DataBag.Set("coverage.current.line_rate", 0.85);

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
        context.DataBag.Set("warnings.baseline.total", 10);
        context.DataBag.Set("warnings.current.total", 12);
        context.DataBag.Set("warnings.new", new List<string> { "CS0168: Variable 'x' is declared but never used" });

        var result = await ratchet.ExecuteAsync(context);

        Assert.False(result.IsSuccess);
        Assert.Equal("implementation", result.TargetRoutingStageId);
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

        context.DataBag.Set("warnings.baseline.total", 10);
        context.DataBag.Set("warnings.current.total", 8);

        var result = await ratchet.ExecuteAsync(context);

        Assert.True(result.IsSuccess);
    }
}
