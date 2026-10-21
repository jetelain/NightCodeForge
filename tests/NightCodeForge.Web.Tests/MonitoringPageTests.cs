using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Web.Components.Pages;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class MonitoringPageTests : BunitContext
{
    private readonly WorkerStateService _workerState;
    private readonly WorkspaceStateService _workspaceState;
    private readonly InMemoryTaskRepository _taskRepository;
    private readonly OrchestratorHubClientConnection _hubConnection;

    public MonitoringPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;

        _workerState = new WorkerStateService();
        _workspaceState = new WorkspaceStateService();
        _taskRepository = new InMemoryTaskRepository();
        _hubConnection = new OrchestratorHubClientConnection("/hubs/orchestrator");

        Services.AddSingleton(_workerState);
        Services.AddSingleton(_workspaceState);
        Services.AddSingleton<ITaskRepository>(_taskRepository);
        Services.AddSingleton<IWorkflowRepository>(new InMemoryWorkflowRepository(new[] { TestWorkflowFixtures.CreateStandardWorkflow() }));
        Services.AddSingleton(_hubConnection);
    }

    [Fact]
    public void MonitoringPage_RendersHeaderAndAllTelemetryComponents()
    {
        var cut = Render<Monitoring>();

        // Header and title
        Assert.Contains("Live Telemetry & Monitoring Console", cut.Find("h1").TextContent);
        Assert.NotNull(cut.Find("#signalr-connection-badge"));

        // All 5 core components
        Assert.NotNull(cut.Find("#worker-telemetry-card"));
        Assert.NotNull(cut.Find(".pipeline-stepper-container"));
        Assert.NotNull(cut.Find("#tool-activity-strip"));
        Assert.NotNull(cut.Find("#thinking-accordion"));
        Assert.NotNull(cut.Find("#terminal-console-container"));
    }

    [Fact]
    public void MonitoringPage_WhenSignalRStageTransitionTriggered_UpdatesStepperAndLogs()
    {
        var cut = Render<Monitoring>();

        // Simulate SignalR event with exact stage ID
        _hubConnection.TriggerStageTransition(15, "implementation", "Agentic", "Running");

        // Stepper reflects running stage 3
        cut.WaitForState(() => cut.FindAll("#stage-step-3.step-active").Any());
        var stage3 = cut.Find("#stage-step-3");
        Assert.Contains("step-active", stage3.ClassName);

        // Terminal console has log
        var terminalLogs = cut.FindAll(".log-line");
        Assert.Contains(terminalLogs, l => l.TextContent.Contains("Stage transition: implementation"));
    }

    [Fact]
    public void MonitoringPage_WhenSignalRDeterministicGateResultTriggered_UpdatesStepperWithMetrics()
    {
        var cut = Render<Monitoring>();

        _hubConnection.TriggerDeterministicGateResult(15, "deterministic-post-impl", true, "+1.4% Coverage");

        var stage4 = cut.Find("#stage-step-4");
        Assert.Contains("step-completed", stage4.ClassName);
        Assert.Contains("+1.4% Coverage", stage4.TextContent);
    }

    [Fact]
    public void MonitoringPage_WhenSignalRToolExecutionTriggered_UpdatesToolActivityStripAndTerminal()
    {
        var cut = Render<Monitoring>();

        _hubConnection.TriggerToolExecution(15, "bash", "dotnet build", "Running", null);

        var toolPills = cut.FindAll(".tool-call-pill");
        Assert.Single(toolPills);
        Assert.Contains("bash", toolPills[0].TextContent);

        _hubConnection.TriggerToolExecution(15, "bash", "dotnet build", "Completed", 1200);

        toolPills = cut.FindAll(".tool-call-pill");
        Assert.Contains("1.20s", toolPills[0].TextContent);
    }

    [Fact]
    public void MonitoringPage_WhenSignalRThinkingDeltaTriggered_StreamsReasoningTokens()
    {
        var cut = Render<Monitoring>();

        _hubConnection.TriggerTokenDelta(15, "Formulating ", true);
        _hubConnection.TriggerTokenDelta(15, "hypothesis...", true);

        var thinkContent = cut.Find("#think-accordion-content-body");
        Assert.Contains("Formulating hypothesis...", thinkContent.TextContent);
    }

    [Fact]
    public void MonitoringPage_WhenSignalRExecutionLogTriggered_AppendsLogToTerminal()
    {
        var cut = Render<Monitoring>();

        _hubConnection.TriggerExecutionLog(15, "Implementation", "Warning", "High memory usage detected", DateTime.UtcNow);

        var terminalLogs = cut.FindAll(".log-line");
        Assert.Contains(terminalLogs, l => l.TextContent.Contains("High memory usage detected"));
    }

    [Fact]
    public async Task MonitoringPage_WhenOpenTaskExistsOnInitialize_DoesNotSynthesizeWorkerRunningState()
    {
        await _taskRepository.CreateAsync(new ForgeTask
        {
            Id = 42,
            WorkspaceId = 1,
            Title = "Active Migration Task",
            Status = TaskOverallStatus.Open,
            CurrentStageId = "task-review"
        });

        var cut = Render<Monitoring>();

        Assert.Equal(WorkerExecutionStatus.Idle, _workerState.Current.Status);
        Assert.Null(_workerState.Current.ActiveTaskId);
        Assert.Contains("IDLE", cut.Find("#worker-telemetry-card").TextContent);
    }

    [Fact]
    public void MonitoringPage_WhenOptimizationActionReceived_LogsWithTokensUnit()
    {
        var cut = Render<Monitoring>();

        _hubConnection.TriggerOptimizationAction(15, "implementation", "ToolResultCompaction", "Pruned 3 outputs", 1500, DateTime.UtcNow);

        var terminalLogs = cut.FindAll(".log-line");
        Assert.Contains(terminalLogs, l => l.TextContent.Contains("(Saved 1500 tokens)"));
    }

    [Fact]
    public void MonitoringPage_RendersLinkToTaskInspector()
    {
        var cut = Render<Monitoring>();

        var links = cut.FindAll("a");
        Assert.Contains(links, a => a.GetAttribute("href") == "/inspector" || a.GetAttribute("href")!.StartsWith("/inspector/"));
    }

    [Fact]
    public void MonitoringPage_WhenRouteTaskIdProvided_RedirectsToDedicatedInspector()
    {
        var navMan = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();

        Render<Monitoring>(p => p.Add(x => x.TaskId, 99));

        Assert.Contains("/inspector/99", navMan.Uri);
    }
}
