using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Web.Components.Monitoring;
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
    private readonly Mock<IOperationsTelemetryService> _mockTelemetryService;
    private readonly Mock<IOllamaRouterGatewayService> _mockGatewayService;
    private readonly Mock<IOperationsControlService> _mockOperationsControlService = new();
    private readonly OperationsTelemetryUiService _uiService;

    public MonitoringPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;

        _mockTelemetryService = new Mock<IOperationsTelemetryService>();
        _mockGatewayService = new Mock<IOllamaRouterGatewayService>();
        _workerState = new WorkerStateService(_mockTelemetryService.Object);
        _workspaceState = new WorkspaceStateService();
        _taskRepository = new InMemoryTaskRepository();
        _hubConnection = TestHubConnectionHelper.Create(workerStateService: _workerState);

        _mockTelemetryService.Setup(t => t.GetRecentLlmCalls(It.IsAny<int>())).Returns(new List<LlmCallRecord>());
        _mockTelemetryService.Setup(t => t.GetRecentShellCommands(It.IsAny<int>())).Returns(new List<ShellCommandRecord>());

        _mockGatewayService.Setup(g => g.GetGatewayStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OllamaRouterStatus(
                IsConnected: true,
                IsRouterProxy: true,
                BaseUrl: "http://localhost:11434",
                Targets: new List<TargetBackendStatus>
                {
                    new("Local GPU Target", TargetBackendState.Active, "Workstation GPU", LatencyMs: 12),
                    new("Remote Server Target", TargetBackendState.Available, "LAN GPU", LatencyMs: 24),
                    new("Cloud Overflow Target", TargetBackendState.Active, "Cloud fallback", LatencyMs: 80)
                }));

        _mockGatewayService.Setup(g => g.GetResidentModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ResidentModelInfo>
            {
                new("qwen2.5-coder:32b", "qwen2.5-coder:32b", 20_000_000_000, 19_000_000_000, DateTimeOffset.UtcNow.AddMinutes(5))
            });

        _mockGatewayService.Setup(g => g.ReclaimVramAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _uiService = new OperationsTelemetryUiService(_hubConnection, _mockTelemetryService.Object);

        Services.AddSingleton(_workerState);
        Services.AddSingleton(_workspaceState);
        Services.AddSingleton<ITaskRepository>(_taskRepository);
        Services.AddSingleton<IWorkflowRepository>(new InMemoryWorkflowRepository(new[] { TestWorkflowFixtures.CreateStandardWorkflow() }));
        _mockOperationsControlService.Setup(m => m.IsPaused).Returns(false);
        _mockOperationsControlService.Setup(m => m.ReclaimStatus).Returns(NightCodeForge.Core.Domain.OperationsReclaimStatus.Idle);
        _mockOperationsControlService.Setup(m => m.GetDeferredTaskCountAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);

        Services.AddSingleton(_hubConnection);
        Services.AddSingleton<IOperationsTelemetryService>(_mockTelemetryService.Object);
        Services.AddSingleton<IOllamaRouterGatewayService>(_mockGatewayService.Object);
        Services.AddSingleton(_uiService);
        Services.AddSingleton(TestServices.CreateToastService());
        Services.AddSingleton<IOperationsControlService>(_mockOperationsControlService.Object);
    }

    [Fact]
    public void MonitoringPage_RendersHeaderAndAllTelemetryComponents()
    {
        var cut = Render<Monitoring>();

        // Header and title
        Assert.Contains("Live Developer Operations & Hardware Telemetry", cut.Find("h1").TextContent);
        Assert.NotNull(cut.Find("#signalr-connection-badge"));

        // Live Hero & cluster monitor
        Assert.NotNull(cut.Find("#active-operations-hero"));
        Assert.NotNull(cut.Find("#active-llm-card"));
        Assert.NotNull(cut.Find("#active-shell-card"));
        Assert.NotNull(cut.Find("#ollamarouter-cluster-monitor"));

        // Default tab: LLM stream
        Assert.NotNull(cut.Find("#llm-calls-history"));
    }

    [Fact]
    public void MonitoringPage_WhenSignalRLlmCallStarted_UpdatesActiveHeroAndHistoryTable()
    {
        var cut = Render<Monitoring>();

        var call = new LlmCallRecord(
            CallId: "call-99",
            Model: "deepseek-coder:33b",
            TargetBackend: "Remote",
            StartTime: DateTime.UtcNow,
            DurationMs: 0,
            PromptTokens: 250,
            CompletionTokens: 0,
            TokensPerSecond: 0,
            PromptPreview: "Write an algorithm",
            CompletionPreview: null,
            Status: LlmCallStatus.Running,
            ErrorMessage: null);

        _hubConnection.TriggerLlmCallStarted(call);

        cut.WaitForState(() => cut.Find("#active-llm-card").TextContent.Contains("deepseek-coder:33b"));
        Assert.Contains("Inferring", cut.Find("#active-llm-card").TextContent);

        var completedCall = call with
        {
            DurationMs = 1500,
            CompletionTokens = 85,
            TokensPerSecond = 34.5,
            CompletionPreview = "Done!",
            Status = LlmCallStatus.Completed
        };
        _hubConnection.TriggerLlmCallCompleted(completedCall);

        cut.WaitForState(() => cut.Find("#active-llm-card").TextContent.Contains("Idle"));
        Assert.Contains("deepseek-coder:33b", cut.Find("#llm-calls-history").TextContent);
    }

    [Fact]
    public void MonitoringPage_WhenSignalRShellCommandStarted_UpdatesActiveShellCardAndHistory()
    {
        var cut = Render<Monitoring>();

        var cmd = new ShellCommandRecord(
            CommandId: "cmd-123",
            CommandLine: "dotnet build --no-restore",
            WorkingDirectory: "/src/NightCodeForge",
            StartTime: DateTime.UtcNow,
            DurationMs: 0,
            ExitCode: null,
            Status: ShellCommandStatus.Running,
            OutputPreview: "Building projects...",
            ErrorPreview: null);

        _hubConnection.TriggerShellCommandStarted(cmd);

        cut.WaitForState(() => cut.Find("#active-shell-card").TextContent.Contains("dotnet build"));
        Assert.Contains("Executing", cut.Find("#active-shell-card").TextContent);

        var completedCmd = cmd with
        {
            DurationMs = 2400,
            ExitCode = 0,
            Status = ShellCommandStatus.Completed
        };
        _hubConnection.TriggerShellCommandCompleted(completedCmd);

        cut.WaitForState(() => cut.Find("#active-shell-card").TextContent.Contains("Idle"));
    }

    [Fact]
    public void MonitoringPage_TabSwitching_SwitchesBetweenLlmAndShellStreams()
    {
        var cut = Render<Monitoring>();

        // Default tab is LLM
        Assert.NotNull(cut.Find("#llm-calls-history"));
        Assert.Empty(cut.FindAll("#shell-commands-history"));

        // Switch to Shell
        cut.Find("#tab-shell-ops").Click();
        Assert.NotNull(cut.Find("#shell-commands-history"));
        Assert.Empty(cut.FindAll("#llm-calls-history"));

        // Switch back to LLM
        cut.Find("#tab-llm-calls").Click();
        Assert.NotNull(cut.Find("#llm-calls-history"));
    }

    [Fact]
    public void MonitoringPage_ReclaimVramButton_TriggersGatewayCall()
    {
        var cut = Render<Monitoring>();

        var reclaimBtn = cut.Find("#reclaim-vram-btn");
        reclaimBtn.Click();

        _mockGatewayService.Verify(g => g.ReclaimVramAsync(It.IsAny<CancellationToken>()), Times.Once);
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

    [Fact]
    public void MonitoringPage_WhenTargetIsDisabled_RendersDisabledBadgeAndGrayStyle()
    {
        _mockGatewayService.Setup(g => g.GetGatewayStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OllamaRouterStatus(
                IsConnected: true,
                IsRouterProxy: true,
                BaseUrl: "http://localhost:11434",
                Targets: new List<TargetBackendStatus>
                {
                    new("Local GPU Target", TargetBackendState.Disabled, "Local GPU is disabled"),
                    new("Remote Server Target", TargetBackendState.Available, "LAN GPU", LatencyMs: 20)
                }));

        var cut = Render<Monitoring>();

        var monitor = cut.Find("#ollamarouter-cluster-monitor");
        Assert.Contains("Disabled", monitor.TextContent);
        Assert.Contains("badge-gray", monitor.InnerHtml);
    }

    [Fact]
    public void ShellCommandOutputModal_RendersWithBackdropAndModalCard()
    {
        var cmd = new ShellCommandRecord(
            CommandId: "cmd-test",
            CommandLine: "dotnet test --filter Category!=E2E",
            WorkingDirectory: "c:\\repo",
            StartTime: DateTime.UtcNow,
            DurationMs: 1200,
            ExitCode: 0,
            Status: ShellCommandStatus.Completed,
            OutputPreview: "All 500 tests passed!",
            ErrorPreview: null);

        var cut = Render<ShellCommandOutputModal>(p => p
            .Add(x => x.Command, cmd));

        var backdrop = cut.Find(".forge-modal-backdrop");
        Assert.NotNull(backdrop);
        Assert.Equal("dialog", backdrop.GetAttribute("role"));
        Assert.Equal("true", backdrop.GetAttribute("aria-modal"));

        var card = cut.Find(".forge-modal");
        Assert.NotNull(card);
        Assert.Contains("forge-modal-content", card.ClassList);
        Assert.Contains("card-glass", card.ClassList);
        Assert.Contains("All 500 tests passed!", card.TextContent);
    }

    [Fact]
    public void InferenceInspectionModal_RendersWithBackdropAndModalCard()
    {
        var call = new LlmCallRecord(
            CallId: "call-test",
            Model: "qwen2.5-coder:32b",
            TargetBackend: "Remote",
            StartTime: DateTime.UtcNow,
            DurationMs: 3400,
            PromptTokens: 1200,
            CompletionTokens: 300,
            TokensPerSecond: 38.5,
            PromptPreview: "Write a unit test",
            CompletionPreview: "public class Test { }",
            Status: LlmCallStatus.Completed,
            ErrorMessage: null);

        var cut = Render<InferenceInspectionModal>(p => p
            .Add(x => x.Call, call));

        var backdrop = cut.Find(".forge-modal-backdrop");
        Assert.NotNull(backdrop);
        Assert.Equal("dialog", backdrop.GetAttribute("role"));
        Assert.Equal("true", backdrop.GetAttribute("aria-modal"));

        var card = cut.Find(".forge-modal");
        Assert.NotNull(card);
        Assert.Contains("forge-modal-content", card.ClassList);
        Assert.Contains("card-glass", card.ClassList);
        Assert.Contains("qwen2.5-coder:32b", card.TextContent);
    }

    [Theory]
    [InlineData(TargetBackendState.Active, "badge-teal")]
    [InlineData(TargetBackendState.Available, "badge-indigo")]
    [InlineData(TargetBackendState.Busy, "badge-amber")]
    [InlineData(TargetBackendState.Disabled, "badge-gray")]
    [InlineData(TargetBackendState.Offline, "badge-crimson")]
    public void OperationsTelemetryUiService_GetTargetBackendBadgeClass_ReturnsExpectedClass(TargetBackendState state, string expectedClass)
    {
        Assert.Equal(expectedClass, OperationsTelemetryUiService.GetTargetBackendBadgeClass(state));
    }

    [Fact]
    public async Task OperationsTelemetryUiService_ConcurrentEnumerationAndMutation_DoesNotThrowCollectionModified()
    {
        // Seed initial items
        for (int i = 0; i < 10; i++)
        {
            _hubConnection.TriggerLlmCallStarted(new LlmCallRecord($"call-{i}", "model", "Local", DateTime.UtcNow, 0, 10, 0, 0, "p", null, LlmCallStatus.Running, null));
            _hubConnection.TriggerShellCommandStarted(new ShellCommandRecord($"cmd-{i}", "cmd", "cwd", DateTime.UtcNow, 0, null, ShellCommandStatus.Running, null, null));
        }

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        var readerTask = Task.Run(() =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    foreach (var call in _uiService.RecentLlmCalls)
                    {
                        _ = call.Model;
                    }
                    foreach (var cmd in _uiService.RecentShellCommands)
                    {
                        _ = cmd.CommandLine;
                    }
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            }
        });

        var writerLlmTask = Task.Run(() =>
        {
            int counter = 100;
            while (!cts.Token.IsCancellationRequested && counter < 200)
            {
                _hubConnection.TriggerLlmCallStarted(new LlmCallRecord($"call-{counter}", "model", "Local", DateTime.UtcNow, 0, 10, 0, 0, "p", null, LlmCallStatus.Running, null));
                _hubConnection.TriggerLlmCallProgress($"call-{counter}", 50, 100, 25.0);
                counter++;
            }
        });

        var writerShellTask = Task.Run(() =>
        {
            int counter = 100;
            while (!cts.Token.IsCancellationRequested && counter < 200)
            {
                _hubConnection.TriggerShellCommandStarted(new ShellCommandRecord($"cmd-{counter}", "cmd", "cwd", DateTime.UtcNow, 0, null, ShellCommandStatus.Running, null, null));
                _hubConnection.TriggerShellCommandOutput($"cmd-{counter}", "output chunk", false);
                counter++;
            }
        });

        await Task.WhenAll(writerLlmTask, writerShellTask);
        cts.Cancel();
        await readerTask;

        Assert.Empty(exceptions);
    }

    [Fact]
    public void MonitoringPage_WhenReasoningTokensPresent_RendersReasoningBadgesInHeroTableAndModal()
    {
        var cut = Render<Monitoring>();

        var runningCallWithReasoning = new LlmCallRecord(
            CallId: "call-r1",
            Model: "deepseek-r1:14b",
            TargetBackend: "Local",
            StartTime: DateTime.UtcNow,
            DurationMs: 800,
            PromptTokens: 150,
            CompletionTokens: 60,
            TokensPerSecond: 45.0,
            PromptPreview: "Solve with deep reasoning",
            CompletionPreview: null,
            Status: LlmCallStatus.Running,
            ErrorMessage: null,
            ReasoningTokens: 40);

        _hubConnection.TriggerLlmCallStarted(runningCallWithReasoning);

        cut.WaitForState(() => cut.Find("#active-llm-card").TextContent.Contains("deepseek-r1:14b"));
        var heroText = cut.Find("#active-llm-card").TextContent;
        Assert.Contains("Reasoning:", heroText);
        Assert.Contains("40", heroText);

        var completedCallWithReasoning = runningCallWithReasoning with
        {
            DurationMs = 2000,
            CompletionTokens = 120,
            ReasoningTokens = 80,
            TokensPerSecond = 60.0,
            CompletionPreview = "[Reasoning]\nChecking constraints...\n\n[Response]\nDone.",
            Status = LlmCallStatus.Completed
        };

        _hubConnection.TriggerLlmCallCompleted(completedCallWithReasoning);

        cut.WaitForState(() => cut.Find("#llm-calls-history").TextContent.Contains("deepseek-r1:14b"));
        var tableText = cut.Find("#llm-calls-history").TextContent;
        Assert.Contains("(🧠 80)", tableText);

        // Test Inspection Modal
        var modalCut = Render<InferenceInspectionModal>(p => p.Add(x => x.Call, completedCallWithReasoning));
        var modalMarkup = modalCut.Markup;
        Assert.Contains("Reasoning: <strong style=\"color: var(--accent-amber);\">🧠 80</strong>", modalMarkup);
        Assert.Contains("Model Completion / Output (with Reasoning 🧠)", modalMarkup);
        Assert.Contains("[Reasoning]", modalMarkup);
        Assert.Contains("[Response]", modalMarkup);
    }

    [Fact]
    public void OperationsTelemetryUiService_WhenMultipleLlmCallsRunning_AndOneCompletes_KeepsOtherCallActive()
    {
        var call1 = new LlmCallRecord("c-1", "model-1", "Local", DateTime.UtcNow, 0, 10, 0, 0, "p1", null, LlmCallStatus.Running, null);
        var call2 = new LlmCallRecord("c-2", "model-2", "Remote", DateTime.UtcNow, 0, 20, 0, 0, "p2", null, LlmCallStatus.Running, null);

        _hubConnection.TriggerLlmCallStarted(call1);
        _hubConnection.TriggerLlmCallStarted(call2);

        Assert.Equal("c-2", _uiService.ActiveLlmCall?.CallId);

        // Complete call2
        _hubConnection.TriggerLlmCallCompleted(call2 with { Status = LlmCallStatus.Completed });

        // call1 must still be active
        Assert.NotNull(_uiService.ActiveLlmCall);
        Assert.Equal("c-1", _uiService.ActiveLlmCall.CallId);
        Assert.Equal(LlmCallStatus.Running, _uiService.ActiveLlmCall.Status);
    }

    [Fact]
    public void OperationsTelemetryUiService_WhenProgressArrivesForSelectedCall_UpdatesSelectedCall()
    {
        var call = new LlmCallRecord("c-sel", "model-sel", "Local", DateTime.UtcNow, 0, 10, 0, 0, "p", null, LlmCallStatus.Running, null);
        _hubConnection.TriggerLlmCallStarted(call);

        _uiService.SelectedLlmCall = call;

        _hubConnection.TriggerLlmCallProgress("c-sel", 120, 1500, 80.0, 40);

        Assert.NotNull(_uiService.SelectedLlmCall);
        Assert.Equal(120, _uiService.SelectedLlmCall.CompletionTokens);
        Assert.Equal(40, _uiService.SelectedLlmCall.ReasoningTokens);
        Assert.Equal(80.0, _uiService.SelectedLlmCall.TokensPerSecond);
    }

    [Fact]
    public void OperationsTelemetryUiService_WhenOutputArrivesForSelectedCommand_UpdatesSelectedCommandOutput()
    {
        var cmd = new ShellCommandRecord("cmd-sel", "dotnet build", "/repo", DateTime.UtcNow, 0, null, ShellCommandStatus.Running, "initial", null);
        _hubConnection.TriggerShellCommandStarted(cmd);

        _uiService.SelectedShellCommand = cmd;

        _hubConnection.TriggerShellCommandOutput("cmd-sel", " more output", false);

        Assert.NotNull(_uiService.SelectedShellCommand);
        Assert.Contains("initial more output", _uiService.SelectedShellCommand.OutputPreview);
    }

    [Fact]
    public void ActiveOperationsHero_RendersContextTokensAndCleanStaticBadges()
    {
        var cut = Render<Monitoring>();

        var call = new LlmCallRecord(
            CallId: "call-context-test",
            Model: "qwen2.5-coder:32b",
            TargetBackend: "Local",
            StartTime: DateTime.UtcNow,
            DurationMs: 0,
            PromptTokens: 14200,
            CompletionTokens: 0,
            TokensPerSecond: 0,
            PromptPreview: "Inspect code",
            CompletionPreview: null,
            Status: LlmCallStatus.Running,
            ErrorMessage: null);

        var cmd = new ShellCommandRecord(
            CommandId: "cmd-test",
            CommandLine: "dotnet test",
            WorkingDirectory: "/workspace",
            StartTime: DateTime.UtcNow,
            DurationMs: 0,
            ExitCode: null,
            Status: ShellCommandStatus.Running,
            OutputPreview: null,
            ErrorPreview: null);

        _hubConnection.TriggerLlmCallStarted(call);
        _hubConnection.TriggerShellCommandStarted(cmd);

        cut.WaitForState(() => cut.Find("#active-llm-card").TextContent.Contains("14200"));
        var heroMarkup = cut.Find("#active-operations-hero").InnerHtml;

        // Context size displayed
        Assert.Contains("Context:", heroMarkup);
        Assert.Contains("14200 tokens", heroMarkup);

        // Clean static badges without pulse animation
        Assert.DoesNotContain("animation: pulse", heroMarkup);
    }

    [Fact]
    public void OperationsTelemetryUiService_FormatLiveDuration_ComputesLiveElapsedForRunningStatus()
    {
        var startTime = DateTime.UtcNow.AddSeconds(-5);
        var runningCall = new LlmCallRecord("c1", "m", "Local", startTime, 1000, 10, 0, 0, "p", null, LlmCallStatus.Running, null);
        var completedCall = runningCall with { Status = LlmCallStatus.Completed, DurationMs = 2500 };

        var liveElapsed = OperationsTelemetryUiService.FormatLiveLlmDuration(runningCall);
        var staticElapsed = OperationsTelemetryUiService.FormatLiveLlmDuration(completedCall);

        // Running call elapsed duration should be at least ~4-5s (dynamic from startTime)
        Assert.EndsWith("s", liveElapsed);
        Assert.NotEqual("1000ms", liveElapsed);
        Assert.NotEqual("1.00s", liveElapsed);

        // Completed call elapsed duration should match DurationMs (2.50s)
        Assert.Equal("2.50s", staticElapsed);

        var runningCmd = new ShellCommandRecord("cmd1", "dotnet test", "/repo", startTime, 500, null, ShellCommandStatus.Running, null, null);
        var completedCmd = runningCmd with { Status = ShellCommandStatus.Completed, DurationMs = 1200 };

        var liveCmdElapsed = OperationsTelemetryUiService.FormatLiveShellDuration(runningCmd);
        var staticCmdElapsed = OperationsTelemetryUiService.FormatLiveShellDuration(completedCmd);

        Assert.EndsWith("s", liveCmdElapsed);
        Assert.NotEqual("500ms", liveCmdElapsed);
        Assert.Equal("1.20s", staticCmdElapsed);
    }

    [Fact]
    public void LlmCallsHistoryTable_RendersContextOutputColumnHeader()
    {
        var cut = Render<Monitoring>();
        var table = cut.Find("#llm-calls-history");
        Assert.Contains("Tokens (Context / Output)", table.TextContent);
    }

    [Fact]
    public void ActiveOperationsHero_RendersActiveTaskCard_WhenIdleAndWhenRunning()
    {
        // 1. Initial idle state
        var cut = Render<ActiveOperationsHero>();
        var card = cut.Find("#active-task-card");
        Assert.NotNull(card);
        Assert.Contains("In-Flight Autonomous Task", card.TextContent);
        Assert.Contains("Idle", card.TextContent);

        // 2. Active running task set
        _uiService.SetActiveTask(new ActiveTaskSummary(
            42,
            "Refactor Telemetry Pipeline",
            "implementation",
            1,
            "InferenceQueue",
            DateTime.UtcNow));

        cut.WaitForState(() => cut.Find("#badge-active-task-running") != null);
        Assert.Contains("#42", cut.Find("#active-task-card").TextContent);
        Assert.Contains("Refactor Telemetry Pipeline", cut.Find("#active-task-card").TextContent);
        Assert.Contains("implementation", cut.Find("#active-task-card").TextContent);
        Assert.NotNull(cut.Find("#badge-active-task-running"));
    }

    [Fact]
    public async Task ActiveOperationsHero_WhenRunningWithDeferredTasks_RendersRetryCard()
    {
        _mockOperationsControlService.Setup(m => m.IsPaused).Returns(false);
        _mockOperationsControlService.Setup(m => m.ReclaimStatus).Returns(NightCodeForge.Core.Domain.OperationsReclaimStatus.Idle);
        _mockOperationsControlService.Setup(m => m.GetDeferredTaskCountAsync(It.IsAny<CancellationToken>())).ReturnsAsync(2);
        _mockOperationsControlService.Setup(m => m.ResumeOperationsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var cut = Render<ActiveOperationsHero>();

        cut.WaitForState(() => cut.Find("#deferred-tasks-retry-card") != null);
        var retryCard = cut.Find("#deferred-tasks-retry-card");
        Assert.NotNull(retryCard);
        Assert.Contains("2 Deferred Task(s) Pending Dispatch", retryCard.TextContent);
        Assert.Contains("Retry Needed", retryCard.TextContent);

        var retryBtn = cut.Find("#btn-hero-retry-deferred");
        Assert.NotNull(retryBtn);
        Assert.Contains("Retry Dispatch (2)", retryBtn.TextContent);

        await retryBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        _mockOperationsControlService.Verify(m => m.ResumeOperationsAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void ActiveOperationsHero_WhenSignalRActiveTaskReceived_UpdatesLiveHeroCard()
    {
        var cut = Render<ActiveOperationsHero>();
        Assert.Contains("Idle", cut.Find("#active-task-card").TextContent);

        _hubConnection.TriggerActiveTask(new ActiveTaskSummary(
            7,
            "Autonomous Optimization Flow",
            "impl",
            1,
            "default",
            DateTime.UtcNow));

        cut.WaitForState(() => cut.Find("#badge-active-task-running") != null);
        Assert.Contains("#7", cut.Find("#active-task-card").TextContent);
        Assert.Contains("Autonomous Optimization Flow", cut.Find("#active-task-card").TextContent);
        Assert.Contains("impl", cut.Find("#active-task-card").TextContent);

        _hubConnection.TriggerActiveTask(null);
        cut.WaitForState(() => cut.Find("#active-task-card").TextContent.Contains("Idle"));
    }

    [Fact]
    public void ActiveOperationsHero_WhenTelemetryOnActiveTaskChangedFired_UpdatesLiveHeroCard()
    {
        var cut = Render<ActiveOperationsHero>();
        Assert.Contains("Idle", cut.Find("#active-task-card").TextContent);

        _mockTelemetryService.Raise(t => t.OnActiveTaskChanged += null, new ActiveTaskSummary(
            7,
            "Live Telemetry Task",
            "review",
            1,
            "default",
            DateTime.UtcNow));

        cut.WaitForState(() => cut.Find("#badge-active-task-running") != null);
        Assert.Contains("#7", cut.Find("#active-task-card").TextContent);
        Assert.Contains("Live Telemetry Task", cut.Find("#active-task-card").TextContent);
    }

    [Fact]
    public void ActiveOperationsHero_WhenWorkerStatusWaitingForHumanReceived_ClearsActiveHeroCard()
    {
        var cut = Render<ActiveOperationsHero>();

        _hubConnection.TriggerActiveTask(new ActiveTaskSummary(
            7, "Autonomous Flow", "impl", 1, "default", DateTime.UtcNow));
        cut.WaitForState(() => cut.Find("#badge-active-task-running") != null);

        _hubConnection.TriggerWorkerStatus("WaitingForHuman", 7, "impl", "Local GPU");
        cut.WaitForState(() => cut.Find("#active-task-card").TextContent.Contains("Idle"));
        Assert.Null(cut.FindAll("#badge-active-task-running").FirstOrDefault());
    }
}
