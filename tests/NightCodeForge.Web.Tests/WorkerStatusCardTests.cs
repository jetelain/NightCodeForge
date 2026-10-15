using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Core.Domain;
using NightCodeForge.Web.Components.Monitoring;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class WorkerStatusCardTests : BunitContext
{
    private readonly WorkerStateService _workerState;
    private readonly WorkspaceStateService _workspaceState;

    public WorkerStatusCardTests()
    {
        _workerState = new WorkerStateService();
        _workspaceState = new WorkspaceStateService();

        Services.AddSingleton(_workerState);
        Services.AddSingleton(_workspaceState);
    }

    [Fact]
    public void WorkerStatusCard_WhenIdle_RendersIdleStateAndStandbyGauge()
    {
        var cut = Render<WorkerStatusCard>();

        Assert.Contains("● IDLE", cut.Find(".worker-status-pill").TextContent);
        Assert.Contains("No active task running", cut.Markup);
        Assert.Contains("Engine Standby", cut.Markup);
    }

    [Fact]
    public void WorkerStatusCard_WhenRunning_RendersTaskDetailsAndRoutingGauge()
    {
        var snapshot = new WorkerStatusSnapshot(
            WorkerExecutionStatus.Running,
            42,
            "Implement SignalR Telemetry Hub",
            "ImplementationStage",
            "Agentic",
            "Fast Local GPU",
            0,
            "feature/task-42-telemetry",
            "Crucible",
            "qwen2.5-coder:32b",
            DateTime.UtcNow.AddMinutes(-3));

        _workerState.UpdateStatus(snapshot);

        var cut = Render<WorkerStatusCard>();

        Assert.Contains("RUNNING", cut.Find(".worker-status-pill").TextContent);
        Assert.Contains("#42", cut.Markup);
        Assert.Contains("Implement SignalR Telemetry Hub", cut.Markup);
        Assert.Contains("feature/task-42-telemetry", cut.Markup);
        Assert.Contains("Fast Local GPU", cut.Markup);
        Assert.Contains("Crucible", cut.Markup);
        Assert.Contains("qwen2.5-coder:32b", cut.Markup);
        Assert.Contains("GPU Inference Active", cut.Markup);
    }

    [Fact]
    public void WorkerStatusCard_WhenDeterministicGateActive_RendersDeterministicGauge()
    {
        var snapshot = new WorkerStatusSnapshot(
            WorkerExecutionStatus.Running,
            42,
            "Run Verification Gates",
            "PostImplementation",
            "Deterministic",
            "Local CPU",
            0,
            "feature/task-42",
            "Crucible",
            "qwen2.5-coder:32b",
            DateTime.UtcNow.AddSeconds(-30));

        _workerState.UpdateStatus(snapshot);

        var cut = Render<WorkerStatusCard>();

        Assert.Contains("Deterministic CPU (0 GPU)", cut.Markup);
        Assert.NotEmpty(cut.FindAll(".hardware-gauge-fill.cpu"));
    }

    [Fact]
    public void WorkerStatusCard_WhenWaitingForHuman_RendersAlertBanner()
    {
        var snapshot = new WorkerStatusSnapshot(
            WorkerExecutionStatus.WaitingForHuman,
            99,
            "Schema Migration Discrepancy",
            "TaskReview",
            "Agentic",
            "Local GPU",
            2,
            "feature/task-99");

        _workerState.UpdateStatus(snapshot);

        var cut = Render<WorkerStatusCard>();

        Assert.Contains("WAITING FOR HUMAN", cut.Find(".worker-status-pill").TextContent);
        Assert.Contains("Worker paused: 2 pending questions awaiting developer input.", cut.Markup);
        Assert.NotEmpty(cut.FindAll("a[href='/inbox']"));
    }

    [Fact]
    public void WorkerStatusCard_WhenWorkspaceSet_RendersWorkspaceName()
    {
        _workspaceState.SetActiveWorkspace(new Workspace { Id = 1, Name = "NightCodeForge Core" });

        var cut = Render<WorkerStatusCard>();

        Assert.Contains("NightCodeForge Core", cut.Markup);
    }
}
