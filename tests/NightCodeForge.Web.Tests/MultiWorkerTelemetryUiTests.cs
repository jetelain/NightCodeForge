using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NightCodeForge.Agents.Inference;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Infrastructure.Configuration;
using NightCodeForge.Web.Components.Monitoring;
using NightCodeForge.Web.Components.Settings;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class MultiWorkerTelemetryUiTests : BunitContext
{
    [Fact]
    public void OperationsTelemetryUiService_MultiLlmCallTracking_UpdatesIndependentlyAndPreservesSiblingCall()
    {
        var telemetryMock = new Mock<IOperationsTelemetryService>();
        var hub = TestHubConnectionHelper.Create();
        var uiService = new OperationsTelemetryUiService(hub, telemetryMock.Object);

        var call1 = new LlmCallRecord(
            CallId: "call-1",
            Model: "qwen2.5-coder:32b",
            TargetBackend: "Node-A-RTX4090",
            StartTime: DateTime.UtcNow.AddSeconds(-2),
            DurationMs: 500,
            PromptTokens: 2000,
            CompletionTokens: 10,
            TokensPerSecond: 20.0,
            PromptPreview: "Implement Step A",
            CompletionPreview: null,
            Status: LlmCallStatus.Running,
            ErrorMessage: null,
            Endpoint: "http://node-a:11434");

        var call2 = new LlmCallRecord(
            CallId: "call-2",
            Model: "deepseek-r1:70b",
            TargetBackend: "Node-B-Dual4090",
            StartTime: DateTime.UtcNow.AddSeconds(-1),
            DurationMs: 1000,
            PromptTokens: 5000,
            CompletionTokens: 25,
            TokensPerSecond: 25.0,
            PromptPreview: "Review architecture plan",
            CompletionPreview: null,
            Status: LlmCallStatus.Running,
            ErrorMessage: null,
            Endpoint: "http://node-b:11434");

        // Start call-1 then call-2
        hub.TriggerLlmCallStarted(call1);
        hub.TriggerLlmCallStarted(call2);

        Assert.Equal(2, uiService.ActiveLlmCalls.Count);
        Assert.Contains(uiService.ActiveLlmCalls, c => c.CallId == "call-1");
        Assert.Contains(uiService.ActiveLlmCalls, c => c.CallId == "call-2");
        Assert.Equal("call-2", uiService.ActiveLlmCall?.CallId);

        // Update call-1 progress independently
        hub.TriggerLlmCallProgress("call-1", 100, 3000, 33.3, 0);
        var active1 = uiService.ActiveLlmCalls.First(c => c.CallId == "call-1");
        Assert.Equal(100, active1.CompletionTokens);
        Assert.Equal(33.3, active1.TokensPerSecond);

        // Update call-2 progress independently
        hub.TriggerLlmCallProgress("call-2", 250, 5000, 50.0, 120);
        var active2 = uiService.ActiveLlmCalls.First(c => c.CallId == "call-2");
        Assert.Equal(250, active2.CompletionTokens);
        Assert.Equal(50.0, active2.TokensPerSecond);
        Assert.Equal(120, active2.ReasoningTokens);
        Assert.Equal(250, uiService.ActiveLlmCall?.CompletionTokens);

        // Complete call-2, leaving call-1 running
        var completedCall2 = call2 with { Status = LlmCallStatus.Completed, CompletionTokens = 300, DurationMs = 6000 };
        hub.TriggerLlmCallCompleted(completedCall2);

        Assert.Single(uiService.ActiveLlmCalls);
        Assert.Equal("call-1", uiService.ActiveLlmCalls[0].CallId);
        Assert.Equal("call-1", uiService.ActiveLlmCall?.CallId);
        Assert.Equal(100, uiService.ActiveLlmCall?.CompletionTokens);
    }

    [Fact]
    public void OperationsTelemetryUiService_MultiShellCommandTracking_UpdatesIndependentlyAndPreservesSibling()
    {
        var telemetryMock = new Mock<IOperationsTelemetryService>();
        var hub = TestHubConnectionHelper.Create();
        var uiService = new OperationsTelemetryUiService(hub, telemetryMock.Object);

        var cmd1 = new ShellCommandRecord(
            CommandId: "cmd-1",
            CommandLine: "dotnet test --filter Category!=E2E",
            WorkingDirectory: "C:/repo/worker1",
            StartTime: DateTime.UtcNow.AddSeconds(-5),
            DurationMs: 2000,
            ExitCode: null,
            Status: ShellCommandStatus.Running,
            OutputPreview: "Running tests...",
            ErrorPreview: null);

        var cmd2 = new ShellCommandRecord(
            CommandId: "cmd-2",
            CommandLine: "git status --porcelain",
            WorkingDirectory: "C:/repo/worker2",
            StartTime: DateTime.UtcNow.AddSeconds(-2),
            DurationMs: 500,
            ExitCode: null,
            Status: ShellCommandStatus.Running,
            OutputPreview: null,
            ErrorPreview: null);

        hub.TriggerShellCommandStarted(cmd1);
        hub.TriggerShellCommandStarted(cmd2);

        Assert.Equal(2, uiService.ActiveShellCommands.Count);
        Assert.Equal("cmd-2", uiService.ActiveShellCommand?.CommandId);

        hub.TriggerShellCommandOutput("cmd-1", "Passed 42 tests", false);
        var active1 = uiService.ActiveShellCommands.First(c => c.CommandId == "cmd-1");
        Assert.Contains("Passed 42 tests", active1.OutputPreview);

        hub.TriggerShellCommandCompleted(cmd2 with { Status = ShellCommandStatus.Completed, ExitCode = 0 });

        Assert.Single(uiService.ActiveShellCommands);
        Assert.Equal("cmd-1", uiService.ActiveShellCommands[0].CommandId);
        Assert.Equal("cmd-1", uiService.ActiveShellCommand?.CommandId);
    }

    [Fact]
    public void ActiveOperationsHero_WhenConcurrentOperations_RendersAllCallsCommandsAndTasks()
    {
        var telemetryMock = new Mock<IOperationsTelemetryService>();
        var opsControlMock = new Mock<IOperationsControlService>();
        var gatewayMock = new Mock<ILlmGatewayService>();
        gatewayMock.Setup(g => g.ProviderType).Returns(LlmProviderType.OllamaRouter);
        opsControlMock.Setup(o => o.IsPaused).Returns(false);
        opsControlMock.Setup(o => o.GetDeferredTaskCountAsync(default)).ReturnsAsync(0);

        var call1 = new LlmCallRecord(
            CallId: "call-1",
            Model: "qwen2.5-coder:32b",
            TargetBackend: "Node-A",
            StartTime: DateTime.UtcNow,
            DurationMs: 1000,
            PromptTokens: 1000,
            CompletionTokens: 50,
            TokensPerSecond: 50.0,
            PromptPreview: "Prompt 1",
            CompletionPreview: null,
            Status: LlmCallStatus.Running,
            ErrorMessage: null,
            Endpoint: "http://node-a:11434");

        var call2 = new LlmCallRecord(
            CallId: "call-2",
            Model: "deepseek-r1:70b",
            TargetBackend: "Node-B",
            StartTime: DateTime.UtcNow,
            DurationMs: 2000,
            PromptTokens: 2000,
            CompletionTokens: 80,
            TokensPerSecond: 40.0,
            PromptPreview: "Prompt 2",
            CompletionPreview: null,
            Status: LlmCallStatus.Running,
            ErrorMessage: null,
            Endpoint: "http://node-b:11434");

        var cmd1 = new ShellCommandRecord("cmd-1", "dotnet build", "C:/repo/1", DateTime.UtcNow, 1000, null, ShellCommandStatus.Running, "Compiling...", null);
        var cmd2 = new ShellCommandRecord("cmd-2", "npm test", "C:/repo/2", DateTime.UtcNow, 1500, null, ShellCommandStatus.Running, "Jest running...", null);

        var task1 = new ActiveTaskSummary(101, "Autonomous Task Alpha", "Implementation", 1, "default", DateTime.UtcNow, "Node-A");
        var task2 = new ActiveTaskSummary(102, "Autonomous Task Beta", "CodeReview", 2, "default", DateTime.UtcNow, "Node-B");

        telemetryMock.Setup(t => t.GetActiveLlmCalls()).Returns(new[] { call1, call2 });
        telemetryMock.Setup(t => t.GetActiveShellCommands()).Returns(new[] { cmd1, cmd2 });
        telemetryMock.Setup(t => t.GetActiveTasks()).Returns(new[] { task1, task2 });
        telemetryMock.Setup(t => t.GetActiveLlmCall()).Returns(call2);
        telemetryMock.Setup(t => t.GetActiveShellCommand()).Returns(cmd2);
        telemetryMock.Setup(t => t.GetActiveTask(null)).Returns(task2);

        var workerState = WorkerStateTestHelper.Create(telemetryMock.Object, 2);
        var hubConnection = TestHubConnectionHelper.Create(workerStateService: workerState);
        var uiService = new OperationsTelemetryUiService(hubConnection, telemetryMock.Object);

        Services.AddSingleton(workerState);
        Services.AddSingleton(uiService);
        Services.AddSingleton(opsControlMock.Object);
        Services.AddSingleton(gatewayMock.Object);
        Services.AddSingleton(TestServices.CreateToastService());
        Services.AddSingleton(NullLogger<ActiveOperationsHero>.Instance);

        var cut = Render<ActiveOperationsHero>();

        // Verify multi-call hero header and content
        var llmCard = cut.Find("#active-llm-card");
        Assert.Contains("2 Inferring", llmCard.TextContent);
        Assert.Contains("qwen2.5-coder:32b", llmCard.TextContent);
        Assert.Contains("deepseek-r1:70b", llmCard.TextContent);
        Assert.Contains("Node-A", llmCard.TextContent);
        Assert.Contains("Node-B", llmCard.TextContent);

        // Verify multi-shell hero header and content
        var shellCard = cut.Find("#active-shell-card");
        Assert.Contains("2 Executing", shellCard.TextContent);
        Assert.Contains("dotnet build", shellCard.TextContent);
        Assert.Contains("npm test", shellCard.TextContent);

        // Verify multi-task hero header and content
        var taskCard = cut.Find("#active-task-card");
        Assert.Contains("Running (2)", taskCard.TextContent);
        Assert.Contains("#101", taskCard.TextContent);
        Assert.Contains("Autonomous Task Alpha", taskCard.TextContent);
        Assert.Contains("#102", taskCard.TextContent);
        Assert.Contains("Autonomous Task Beta", taskCard.TextContent);
    }

    [Fact]
    public void WorkerStateService_WithConcurrentTasks_PreservesRunningUntilAllComplete()
    {
        var telemetryMock = new Mock<IOperationsTelemetryService>();
        var task1 = new ActiveTaskSummary(101, "Task 101", "stage-1", 1, "default", DateTime.UtcNow.AddMinutes(-5), "Node-A");
        var task2 = new ActiveTaskSummary(102, "Task 102", "stage-2", 1, "default", DateTime.UtcNow.AddMinutes(-2), "Node-B");

        var activeList = new List<ActiveTaskSummary> { task1, task2 };
        telemetryMock.Setup(t => t.GetActiveTasks()).Returns(activeList);
        telemetryMock.Setup(t => t.GetActiveTask(null)).Returns(task2);

        var workerState = WorkerStateTestHelper.Create(telemetryMock.Object, 2);

        Assert.Equal(WorkerExecutionStatus.Running, workerState.Current.Status);
        Assert.Equal(2, workerState.ActiveTaskCount);
        Assert.Equal(2, workerState.ConfiguredWorkerCount);
        Assert.Equal(2, workerState.ActiveTaskIds.Count);
        Assert.Contains(101, workerState.ActiveTaskIds);
        Assert.Contains(102, workerState.ActiveTaskIds);

        // Task 101 transitions to Idle/Completed while Task 102 is still executing
        activeList.Remove(task1);
        workerState.HandleWorkerStatus(WorkerStatusConstants.Idle, 101, null, "Node-A");

        // Worker overall status must stay Running because Task 102 is still active
        Assert.Equal(WorkerExecutionStatus.Running, workerState.Current.Status);
        Assert.Equal(102, workerState.ActiveTaskId);
        Assert.Single(workerState.Current.ActiveTasks);

        // Task 102 completes
        activeList.Remove(task2);
        workerState.HandleWorkerStatus(WorkerStatusConstants.Idle, 102, null, "Node-B");

        // Now that 0 tasks remain, overall status becomes Idle
        Assert.Equal(WorkerExecutionStatus.Idle, workerState.Current.Status);
        Assert.Null(workerState.ActiveTaskId);
        Assert.Empty(workerState.Current.ActiveTasks);
    }

    [Fact]
    public void WorkerStatusCard_RendersWorkerUtilization_And_ListsActiveTasks()
    {
        var telemetryMock = new Mock<IOperationsTelemetryService>();
        var task1 = new ActiveTaskSummary(201, "Refactor Core Parser", "Agentic", 1, "default", DateTime.UtcNow.AddMinutes(-10), "Node-A");
        var task2 = new ActiveTaskSummary(202, "Run Acceptance Suite", "Agentic", 1, "default", DateTime.UtcNow.AddMinutes(-3), "Node-B");

        telemetryMock.Setup(t => t.GetActiveTasks()).Returns(new[] { task1, task2 });
        telemetryMock.Setup(t => t.GetActiveTask(null)).Returns(task2);

        var workerState = WorkerStateTestHelper.Create(telemetryMock.Object, 2);
        var workspaceState = new WorkspaceStateService();

        Services.AddSingleton(workerState);
        Services.AddSingleton(workspaceState);

        var cut = Render<WorkerStatusCard>();

        // Utilization pill
        var statusPill = cut.Find(".worker-status-pill");
        Assert.Contains("RUNNING (2 / 2)", statusPill.TextContent);

        // Active task & scope section
        var card = cut.Find("#worker-telemetry-card");
        Assert.Contains("2 / 2 Workers Active", card.TextContent);
        Assert.Contains("#201", card.TextContent);
        Assert.Contains("Refactor Core Parser", card.TextContent);
        Assert.Contains("#202", card.TextContent);
        Assert.Contains("Run Acceptance Suite", card.TextContent);
    }

    [Fact]
    public void OllamaRouterGatewayCard_MaxConcurrentTasks_SavesAndShowsRestartNotice()
    {
        var configWriterMock = new Mock<ILlmConfigurationWriter>();
        LlmProviderOptions? savedOptions = null;
        configWriterMock
            .Setup(c => c.SaveProviderConfigurationAsync(It.IsAny<Action<LlmProviderOptions>>(), It.IsAny<CancellationToken>()))
            .Callback<Action<LlmProviderOptions>, CancellationToken>((action, ct) =>
            {
                var opts = new LlmProviderOptions();
                action(opts);
                savedOptions = opts;
            })
            .Returns(Task.CompletedTask);

        var gatewayMock = new Mock<ILlmGatewayService>();
        gatewayMock.Setup(g => g.ProviderType).Returns(LlmProviderType.OllamaRouter);
        gatewayMock.Setup(g => g.GetGatewayStatusAsync(default)).ReturnsAsync(new GatewayStatusReport(
            IsConnected: true,
            ProviderType: LlmProviderType.OllamaRouter,
            BaseUrl: "http://localhost:11434",
            Version: "0.5.0",
            FleetTargets: new List<FleetTargetStatus>()));
        gatewayMock.Setup(g => g.GetResidentModelsAsync(default)).ReturnsAsync(new List<ResidentModelInfo>());

        var catalogMock = new Mock<ILlmModelCatalogService>();
        catalogMock.Setup(c => c.GetAvailableModelsAsync(default)).ReturnsAsync(new List<AvailableModelInfo>());

        var initialOptions = new LlmProviderOptions
        {
            ProviderType = LlmProviderType.OllamaRouter,
            MaxConcurrentInferenceTasks = 1
        };

        Services.AddSingleton(gatewayMock.Object);
        Services.AddSingleton(catalogMock.Object);
        Services.AddSingleton(configWriterMock.Object);
        Services.AddSingleton<IOptions<LlmProviderOptions>>(Options.Create(initialOptions));
        Services.AddSingleton(TestServices.CreateToastService());

        var cut = Render<OllamaRouterGatewayCard>();

        // Concurrency input exists and has default value 1
        var concurrencyInput = cut.Find("#input-max-concurrent-tasks");
        Assert.Equal("1", concurrencyInput.GetAttribute("value"));

        // Change concurrency to 4
        concurrencyInput.Change(new ChangeEventArgs { Value = "4" });

        // Notice that restart warning badge appears
        Assert.Contains("Restart required after saving changes", cut.Markup);

        // Click save
        var saveButton = cut.Find("#btn-save-settings-ollama");
        saveButton.Click();

        // Verify ConfigurationWriter saved 4 workers
        Assert.NotNull(savedOptions);
        Assert.Equal(4, savedOptions.MaxConcurrentInferenceTasks);

        // Out-of-range value is clamped in both saved options and UI input
        concurrencyInput = cut.Find("#input-max-concurrent-tasks");
        concurrencyInput.Change(new ChangeEventArgs { Value = "99" });
        saveButton = cut.Find("#btn-save-settings-ollama");
        saveButton.Click();

        Assert.Equal(16, savedOptions.MaxConcurrentInferenceTasks);
        Assert.Equal("16", cut.Find("#input-max-concurrent-tasks").GetAttribute("value"));
    }

    [Fact]
    public void WorkerStateService_WhenHandleWorkerStatusCalledWithoutTelemetryTasks_PreservesActiveTasksInCurrent()
    {
        var telemetryMock = new Mock<IOperationsTelemetryService>();
        var workerState = WorkerStateTestHelper.Create(telemetryMock.Object, 2);

        workerState.HandleWorkerStatus(WorkerStatusConstants.Running, 55, "stage-impl", "Node-A", "Task Fifty-Five", "task/55");

        var snapshot = workerState.Current;
        Assert.Equal(WorkerExecutionStatus.Running, snapshot.Status);
        Assert.Equal(55, snapshot.ActiveTaskId);
        Assert.Single(snapshot.ActiveTasks);
        Assert.Equal(1, workerState.ActiveTaskCount);
        Assert.Equal("RUNNING (1 / 2)", WorkerStateService.FormatRunningStatusLabel(snapshot));
    }

    [Fact]
    public void WorkerStateService_WhenConcurrentTasksAlternateStageUpdates_PreservesExistingTitleAndStartTime()
    {
        var telemetryMock = new Mock<IOperationsTelemetryService>();
        var t1Start = DateTime.UtcNow.AddMinutes(-15);
        var t2Start = DateTime.UtcNow.AddMinutes(-5);
        var task1 = new ActiveTaskSummary(101, "Alpha Feature", "stage-1", 2, "llm-inference", t1Start, "Node-A");
        var task2 = new ActiveTaskSummary(102, "Beta Bugfix", "stage-1", 3, "llm-inference", t2Start, "Node-B");

        var activeList = new List<ActiveTaskSummary> { task1, task2 };
        telemetryMock.Setup(t => t.GetActiveTasks()).Returns(activeList);

        var workerState = WorkerStateTestHelper.Create(telemetryMock.Object, 2);
        Assert.Equal(102, workerState.Current.ActiveTaskId);

        // Task 101 emits stage transition via ReceiveWorkerStatus (which does not pass taskTitle)
        workerState.HandleWorkerStatus(WorkerStatusConstants.Running, 101, "stage-2", "Node-A");

        var current = workerState.Current;
        Assert.Equal(101, current.ActiveTaskId);
        Assert.Equal("Alpha Feature", current.ActiveTaskTitle);
        Assert.Equal("stage-2", current.StageId);
        Assert.Equal(t1Start, current.StartedAt);

        var updatedTask1 = current.ActiveTasks.First(t => t.TaskId == 101);
        Assert.Equal(2, updatedTask1.Priority);
        Assert.Equal("llm-inference", updatedTask1.QueueType);
        Assert.Equal(t1Start, updatedTask1.StartedAt);
    }

    [Fact]
    public void WorkerStateService_WhenGlobalIdleReceived_DoesNotResurrectStaleTaskFromGetActiveTasks()
    {
        var telemetryMock = new Mock<IOperationsTelemetryService>();
        var staleTask = new ActiveTaskSummary(5, "Stale Task", "stage-1", 1, "default", DateTime.UtcNow.AddMinutes(-3));
        telemetryMock.Setup(t => t.GetActiveTasks()).Returns(new[] { staleTask });
        telemetryMock.Setup(t => t.GetActiveTask(null)).Returns(staleTask);

        var workerState = WorkerStateTestHelper.Create(telemetryMock.Object, 2);
        Assert.Equal(WorkerExecutionStatus.Running, workerState.Current.Status);

        workerState.HandleWorkerStatus(WorkerStatusConstants.Idle, null, null, "Local GPU");

        Assert.Equal(WorkerExecutionStatus.Idle, workerState.Current.Status);
        Assert.Null(workerState.Current.ActiveTaskId);
        Assert.Empty(workerState.Current.ActiveTasks);
    }

    [Fact]
    public void OperationsTelemetryUiService_WhenSiblingTaskClearedInTelemetry_SynchronizesActiveTasksOnRemainingTaskSignal()
    {
        var telemetryMock = new Mock<IOperationsTelemetryService>();
        var task1 = new ActiveTaskSummary(101, "Task 101", "stage-1", 1, "default", DateTime.UtcNow.AddMinutes(-5), "Node-A");
        var task2 = new ActiveTaskSummary(102, "Task 102", "stage-2", 1, "default", DateTime.UtcNow.AddMinutes(-2), "Node-B");

        var activeList = new List<ActiveTaskSummary> { task1, task2 };
        telemetryMock.Setup(t => t.GetActiveTasks()).Returns(() => activeList.ToList());
        telemetryMock.Setup(t => t.GetActiveTask(null)).Returns(() => activeList.LastOrDefault());

        var hub = TestHubConnectionHelper.Create();
        var uiService = new OperationsTelemetryUiService(hub, telemetryMock.Object);

        Assert.Equal(2, uiService.ActiveTasks.Count);

        // Task 102 finishes in telemetry service, leaving only Task 101, and SignalR broadcasts remaining Task 101
        activeList.Remove(task2);
        hub.TriggerActiveTask(task1);

        Assert.Single(uiService.ActiveTasks);
        Assert.Equal(101, uiService.ActiveTasks[0].TaskId);
        Assert.Equal(101, uiService.ActiveTask?.TaskId);
    }

    [Fact]
    public void WorkerStateService_WhenConcurrentTasksArriveViaHandleWorkerStatusWithoutTelemetry_TracksBothAndStaysRunningWhenOneWaitsForHuman()
    {
        var telemetryMock = new Mock<IOperationsTelemetryService>();
        var workerState = WorkerStateTestHelper.Create(telemetryMock.Object, 2);

        workerState.HandleWorkerStatus(WorkerStatusConstants.Running, 101, "stage-1", "Node-A", "Task 101", "feature/task-101");
        workerState.HandleWorkerStatus(WorkerStatusConstants.Running, 102, "stage-1", "Node-B", "Task 102", "feature/task-102");

        Assert.Equal(WorkerExecutionStatus.Running, workerState.Current.Status);
        Assert.Equal(2, workerState.ActiveTaskCount);
        Assert.Contains(101, workerState.ActiveTaskIds);
        Assert.Contains(102, workerState.ActiveTaskIds);

        // Task 101 stage update with empty hardwareTarget while Task 102 is active: must preserve Task 101's "Node-A"
        workerState.HandleWorkerStatus(WorkerStatusConstants.Running, 101, "stage-2", "");
        Assert.Equal("Node-A", workerState.Current.HardwareTarget);

        // Task 102 transitions to WaitingForHuman while Task 101 continues running
        workerState.HandleWorkerStatus(WorkerStatusConstants.WaitingForHuman, 102, "stage-1", "Node-B");

        Assert.Equal(WorkerExecutionStatus.Running, workerState.Current.Status);
        Assert.Equal(101, workerState.ActiveTaskId);
        Assert.Single(workerState.Current.ActiveTasks);
        Assert.Equal("Task 101", workerState.Current.ActiveTasks[0].TaskTitle);
    }

    [Fact]
    public void OperationsTelemetryUiService_WhenHandleWorkerStatusReceivedWithEmptyHardwareTarget_PreservesExistingHardwareTarget()
    {
        var telemetryMock = new Mock<IOperationsTelemetryService>();
        var hub = TestHubConnectionHelper.Create();
        var uiService = new OperationsTelemetryUiService(hub, telemetryMock.Object);

        hub.TriggerWorkerStatus(WorkerStatusConstants.Running, 101, "stage-1", "Node-A-RTX4090");
        Assert.Single(uiService.ActiveTasks);
        Assert.Equal("Node-A-RTX4090", uiService.ActiveTasks[0].HardwareTarget);

        // Stage update arrives with empty hardwareTarget while telemetry GetActiveTasks() is empty
        hub.TriggerWorkerStatus(WorkerStatusConstants.Running, 101, "stage-2", "");

        Assert.Single(uiService.ActiveTasks);
        Assert.Equal("stage-2", uiService.ActiveTasks[0].CurrentStageId);
        Assert.Equal("Node-A-RTX4090", uiService.ActiveTasks[0].HardwareTarget);
        Assert.Equal("Node-A-RTX4090", uiService.ActiveTask?.HardwareTarget);
    }
}
