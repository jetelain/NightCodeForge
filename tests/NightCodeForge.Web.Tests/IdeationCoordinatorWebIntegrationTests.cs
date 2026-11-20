using System.Text;
using Bunit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NightCodeForge.Agents;
using NightCodeForge.Agents.Inference;
using NightCodeForge.Core.Concurrency;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Execution;
using NightCodeForge.Core.Inference;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Resilience;
using NightCodeForge.Core.Security;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Infrastructure;
using NightCodeForge.Infrastructure.BackgroundJobs;
using NightCodeForge.Infrastructure.Data;
using NightCodeForge.Web.Components.Monitoring;
using NightCodeForge.Web.Components.Pages;
using NightCodeForge.Web.Services;
using Pmad.Markitdown;

namespace NightCodeForge.Web.Tests;

public class IdeationCoordinatorWebIntegrationTests
{
    private static void RegisterSharedWebServices(
        BunitServiceProvider services,
        MockIdeationConversationCoordinator coordinator,
        InMemoryTaskRepository taskRepo,
        InMemoryWorkflowRepository workflowRepo,
        InMemoryWorkspaceRepository workspaceRepo,
        WorkspaceStateService wsState,
        ToastService toastService,
        IdeationSessionState sessionState)
    {
        services.AddSingleton(wsState);
        services.AddSingleton<ITaskRepository>(taskRepo);
        services.AddSingleton<IWorkflowRepository>(workflowRepo);
        services.AddSingleton<IWorkspaceRepository>(workspaceRepo);
        services.AddSingleton<ITaskMarkdownSyncService>(new MockTaskMarkdownSyncService());
        services.AddSingleton<IIdeationConversationCoordinator>(coordinator);
        services.AddSingleton(toastService);
        services.AddSingleton(sessionState);
        services.AddSingleton(new IdeationRefinementService(taskRepo));
        services.AddSingleton<ICompanionTriageDraftStore>(new CompanionTriageDraftStore(TimeProvider.System));
        TestServices.AddDefaultWebUiServices(services);
    }

    [Fact]
    public async Task IdeationPage_WhenDisposedMidTurn_SurvivesAndReattachesWithAskCardAndCompletion()
    {
        var taskRepo = new InMemoryTaskRepository();
        var workflowRepo = new InMemoryWorkflowRepository();
        var workspaceRepo = new InMemoryWorkspaceRepository();
        workspaceRepo.SeedGlobalProfiles(AgentProfile.CreateStandardGlobalProfiles("test-model"));
        var ws = new Workspace { Id = 801, Name = "Survival Repo", DefaultBaseBranch = "main" };
        await workspaceRepo.CreateAsync(ws);

        var coordinator = new MockIdeationConversationCoordinator(taskRepo);
        var question = new IdeationQuestion("Which auth mode?", "Pick one", ["JWT", "Cookie"]);
        coordinator.StreamUpdatesToYield =
        [
            new(IdeationStreamUpdateKind.ThinkingDelta, TextDelta: "Inspecting architecture..."),
            new(IdeationStreamUpdateKind.MessageDelta, TextDelta: "Checking options..."),
            new(IdeationStreamUpdateKind.QuestionAsked, Question: question, Questions: [question]),
            new(IdeationStreamUpdateKind.MessageDelta, TextDelta: " Proceeding with answer!"),
            new(IdeationStreamUpdateKind.Completed, FinalResult: new IdeationResult("Checking options... Proceeding with answer!", []))
        ];

        int activeConvId;
        // Circuit 1: Send message, pause at live Ask card, then dispose circuit (simulates browser refresh)
        using (var ctx1 = new BunitContext())
        {
            ctx1.JSInterop.Mode = JSRuntimeMode.Loose;
            var wsState1 = new WorkspaceStateService();
            wsState1.SetActiveWorkspace(ws);
            var session1 = TestServices.CreateIdeationSessionState(coordinator: coordinator, taskRepository: taskRepo, workflowRepository: workflowRepo);
            RegisterSharedWebServices(ctx1.Services, coordinator, taskRepo, workflowRepo, workspaceRepo, wsState1, TestServices.CreateToastService(), session1);

            var cut1 = ctx1.Render<Ideation>();
            await cut1.InvokeAsync(() => cut1.Find("textarea.chat-textarea").Input("Design auth"));
            await cut1.InvokeAsync(() => cut1.Find("button.send-btn").Click());

            cut1.WaitForAssertion(() =>
            {
                Assert.True(session1.IsGenerating);
                Assert.Contains("Inspecting architecture...", cut1.Markup);
                Assert.Contains("Which auth mode?", cut1.Markup);
                Assert.NotNull(cut1.FindComponent<AgentQuestionTurnCard>());
            });

            activeConvId = session1.ActiveConversationId!.Value;
        }

        // Disposing circuit 1 MUST NOT cancel turn or release session affinity
        Assert.Empty(coordinator.CancelledConversationIds);
        Assert.Empty(coordinator.ReleasedSessions);
        Assert.True(coordinator.IsConversationGenerating(activeConvId));
        Assert.False(question.AnswerTask.IsCompleted);

        // Circuit 2: Remount fresh circuit + fresh IdeationSessionState; must re-attach seamlessly
        using (var ctx2 = new BunitContext())
        {
            ctx2.JSInterop.Mode = JSRuntimeMode.Loose;
            var wsState2 = new WorkspaceStateService();
            wsState2.SetActiveWorkspace(ws);
            var session2 = TestServices.CreateIdeationSessionState(coordinator: coordinator, taskRepository: taskRepo, workflowRepository: workflowRepo);
            RegisterSharedWebServices(ctx2.Services, coordinator, taskRepo, workflowRepo, workspaceRepo, wsState2, TestServices.CreateToastService(), session2);

            var cut2 = ctx2.Render<Ideation>();
            cut2.WaitForAssertion(() =>
            {
                Assert.True(session2.IsGenerating);
                Assert.Equal(activeConvId, session2.ActiveConversationId);
                Assert.Contains("Design auth", cut2.Markup);
                Assert.Contains("Inspecting architecture...", cut2.Markup);
                Assert.Contains("Checking options...", cut2.Markup);
                Assert.Contains("Which auth mode?", cut2.Markup);
            });

            await cut2.InvokeAsync(() =>
            {
                cut2.FindAll("button.option-btn")[0].Click();
                cut2.Find("button.submit-answer-btn").Click();
            });

            Assert.Equal("JWT", await question.AnswerTask);

            cut2.WaitForAssertion(() =>
            {
                Assert.False(session2.IsGenerating);
                Assert.Contains("Proceeding with answer!", cut2.Markup);
                Assert.Equal(2, session2.Messages.Count);
            });
        }
    }

    [Fact]
    public async Task IdeationPage_ConfigChanges_InvokeUpdateSessionOptionsAsync()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var taskRepo = new InMemoryTaskRepository();
        var workflowRepo = new InMemoryWorkflowRepository();
        var workspaceRepo = new InMemoryWorkspaceRepository();
        workspaceRepo.SeedGlobalProfiles(AgentProfile.CreateStandardGlobalProfiles("test-model"));
        var ws = new Workspace { Id = 802, Name = "Config WS", DefaultBaseBranch = "main" };
        await workspaceRepo.CreateAsync(ws);

        var coordinator = new MockIdeationConversationCoordinator(taskRepo);
        var wsState = new WorkspaceStateService();
        wsState.SetActiveWorkspace(ws);
        var session = TestServices.CreateIdeationSessionState(coordinator: coordinator, taskRepository: taskRepo, workflowRepository: workflowRepo);
        RegisterSharedWebServices(ctx.Services, coordinator, taskRepo, workflowRepo, workspaceRepo, wsState, TestServices.CreateToastService(), session);

        var cut = ctx.Render<Ideation>();
        cut.WaitForAssertion(() => Assert.NotNull(session.ActiveConversationId));

        await cut.InvokeAsync(() => cut.Find("button.prompt-config-toggle-btn").Click());

        await cut.InvokeAsync(() => cut.Find("textarea.system-prompt-textarea").Change("Prefer Clean Architecture."));
        await cut.InvokeAsync(() => cut.Find(".system-prompt-card select.forge-input").Change(OllamaRoutingTarget.Cloud.ToString()));
        await cut.InvokeAsync(() => cut.Find("#ideation-model-override-input").Change("qwen3-coder:480b"));

        Assert.NotEmpty(coordinator.UpdatedOptions);
        var last = coordinator.UpdatedOptions[^1].Options;
        Assert.Equal(OllamaRoutingTarget.Cloud, last.RoutingTarget);
        Assert.Equal("qwen3-coder:480b", last.ModelOverride);
        Assert.Equal("Prefer Clean Architecture.", last.CustomSystemPrompt);
    }

    [Fact]
    public async Task IdeationPage_ErrorAndOptimizationEventsAndTurns_RenderCleanly()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var taskRepo = new InMemoryTaskRepository();
        var workflowRepo = new InMemoryWorkflowRepository();
        var workspaceRepo = new InMemoryWorkspaceRepository();
        workspaceRepo.SeedGlobalProfiles(AgentProfile.CreateStandardGlobalProfiles("test-model"));
        var ws = new Workspace { Id = 803, Name = "Events WS", DefaultBaseBranch = "main" };
        await workspaceRepo.CreateAsync(ws);

        // Seed conversation with persisted Optimization checkpoint turn
        var conv = await taskRepo.StartIdeationConversationAsync(ws.Id, "Compaction History");
        await taskRepo.AddConversationTurnAsync(conv.Id, StageTurnRole.User, content: "Analyze big repo");
        await taskRepo.AddConversationTurnAsync(conv.Id, StageTurnRole.Optimization, content: "Compacted 18 messages into summary.");
        await taskRepo.AddConversationTurnAsync(conv.Id, StageTurnRole.Assistant, content: "Continued after compaction.");

        var coordinator = new MockIdeationConversationCoordinator(taskRepo)
        {
            StreamUpdatesToYield =
            [
                new(IdeationStreamUpdateKind.OptimizationNotice, TextDelta: "Live compaction triggered", PreCompactionTokens: 45_000, PostCompactionTokens: 8_000, DurationMs: 50),
                new(IdeationStreamUpdateKind.Error, ErrorMessage: "Upstream router timeout")
            ]
        };

        var wsState = new WorkspaceStateService();
        wsState.SetActiveWorkspace(ws);
        var toastService = TestServices.CreateToastService();

        var session = TestServices.CreateIdeationSessionState(coordinator: coordinator, taskRepository: taskRepo, workflowRepository: workflowRepo);
        RegisterSharedWebServices(ctx.Services, coordinator, taskRepo, workflowRepo, workspaceRepo, wsState, toastService, session);

        var cut = ctx.Render<Ideation>();
        cut.WaitForAssertion(() => Assert.Contains("Compacted 18 messages into summary.", cut.Markup));
        Assert.NotEmpty(cut.FindAll(".compaction-notice-part"));

        await cut.InvokeAsync(() => cut.Find("textarea.chat-textarea").Input("Trigger error"));
        await cut.InvokeAsync(() => cut.Find("button.send-btn").Click());

        cut.WaitForAssertion(() =>
        {
            Assert.False(session.IsGenerating);
            Assert.Contains(toastService.Toasts, t => t.Level == ToastLevel.Error && t.Message.Contains("Upstream router timeout"));
        });
    }

    [Fact]
    public async Task TaskMutationService_And_AttachmentUiService_Mutations_MarkWorkspaceContextDirty()
    {
        var taskRepo = new InMemoryTaskRepository();
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var depStore = new InMemoryTaskDependencyStore();
        var markdownSync = new MockTaskMarkdownSyncService();
        var orchestrator = new MockTaskOrchestrator();
        var coordinatorMock = new Mock<IIdeationConversationCoordinator>();

        var ws = await workspaceRepo.CreateAsync(new Workspace { Id = 901, Name = "DirtyWS", RepositoryPath = Directory.GetCurrentDirectory() });
        var task = await taskRepo.CreateWithBaselineAsync(
            new ForgeTask { WorkspaceId = ws.Id, Title = "Initial", Description = "Desc", Status = TaskOverallStatus.Open },
            null,
            "user",
            "Created");

        var mutationService = new TaskMutationService(
            taskRepo,
            workspaceRepo,
            depStore,
            markdownSync,
            orchestrator,
            Mock.Of<IOperationsTelemetryService>(),
            coordinatorMock.Object,
            NullLogger<TaskMutationService>.Instance);

        await mutationService.UpdateTitleAsync(task, "Updated Title");
        await mutationService.RejectTaskAsync(task, "No longer needed");

        coordinatorMock.Verify(c => c.MarkWorkspaceContextDirty(ws.Id), Times.AtLeast(2));

        // AttachmentUiService mutations
        coordinatorMock.Invocations.Clear();
        var attachmentRepo = new InMemoryTaskAttachmentRepository(taskRepo);
        var attachment = new TaskAttachment
        {
            Id = 55,
            WorkspaceId = ws.Id,
            Title = "notes.md",
            SourceRef = "notes.md",
            Sha256 = new string('b', 64),
            ContentKind = TaskAttachmentContentKind.PlainText
        };
        var intakeMock = new Mock<IAttachmentIntakeService>();
        intakeMock.Setup(i => i.IngestFileDropAsync(ws.Id, "notes.md", It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(attachment);
        intakeMock.Setup(i => i.DeleteAttachmentAsync(attachment.Id, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        await attachmentRepo.CreateAsync(attachment);

        var attachmentUi = new AttachmentUiService(
            intakeMock.Object,
            attachmentRepo,
            taskRepo,
            workspaceRepo,
            Mock.Of<IWorkspacePathResolver>(),
            coordinatorMock.Object,
            Mock.Of<IVisionCapabilityUiService>(),
            Mock.Of<ITaskMarkdownSyncService>(),
            new WorkspaceLockProvider(),
            NullLogger<AttachmentUiService>.Instance);

        await using var ms = new MemoryStream(Encoding.UTF8.GetBytes("# Notes"));
        await attachmentUi.UploadFileAsync(ws.Id, "notes.md", ms);
        await attachmentUi.DeleteAttachmentAsync(attachment.Id);

        coordinatorMock.Verify(c => c.MarkWorkspaceContextDirty(ws.Id), Times.Exactly(2));
    }

    [Fact]
    public async Task ProductionDiContainer_WithValidateScopesAndValidateOnBuild_BuildsWithoutCaptiveScopedDependencies()
    {
        var tempDb = Path.Combine(Path.GetTempPath(), $"ncf-di-validate-{Guid.NewGuid():N}.db");
        var tempHangfireDb = Path.Combine(Path.GetTempPath(), $"ncf-di-hangfire-{Guid.NewGuid():N}.db");
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
            var webEnvMock = new Mock<IWebHostEnvironment>();
            webEnvMock.SetupGet(e => e.EnvironmentName).Returns(Environments.Development);
            webEnvMock.SetupGet(e => e.ApplicationName).Returns("NightCodeForge.Web");
            services.AddSingleton(webEnvMock.Object);
            services.AddSingleton<IHostEnvironment>(webEnvMock.Object);
            services.AddSingleton(Mock.Of<IHostApplicationLifetime>());
            services.AddRazorComponents().AddInteractiveServerComponents();

            services.AddScoped<WorkspaceStateService>();
            services.AddSingleton<WorkerStateService>();
            services.AddScoped<ToastService>();
            services.AddScoped<IIdeationProposalService, IdeationProposalService>();
            services.AddScoped<IdeationSessionState>();
            services.AddScoped<IdeationRefinementService>();
            services.AddScoped<IWorkspaceProvisioningService, WorkspaceProvisioningService>();
            services.AddScoped<ReviewCommentStateService>();
            services.AddScoped<ITaskReviewService, TaskReviewService>();
            services.AddSingleton<ITranscriptAnalysisService, TranscriptAnalysisService>();
            services.AddScoped<ITranscriptBrowserService, TranscriptBrowserService>();
            services.AddScoped<IGitSyncService, GitSyncService>();
            services.AddSingleton<IDevQueueSkippedJobRegistry, DevQueueSkippedJobRegistry>();
            services.AddScoped<IDevQueueService, DevQueueService>();
            services.AddSingleton<ITaskHistoryDiffService, TaskHistoryDiffService>();
            services.AddScoped<ITaskMutationService, TaskMutationService>();
            services.AddScoped<IVisionCapabilityUiService, VisionCapabilityUiService>();
            services.AddScoped<IAttachmentUiService, AttachmentUiService>();
            services.AddScoped<ITaskCreationUiService, TaskCreationUiService>();
            services.AddScoped<ITaskDiskSyncCoordinator, TaskDiskSyncCoordinator>();
            services.AddSingleton<IDagLayoutEngine, DagLayoutEngine>();
            services.AddScoped<OperationsTelemetryUiService>();
            services.AddSingleton<ISyntaxHighlightingService, SimpleSyntaxHighlighter>();
            services.AddScoped<ITaskDiffService, TaskDiffService>();
            services.AddScoped<IGlobalSkillsManagerService, GlobalSkillsManagerService>();
            services.AddScoped<GitHubSettingsUiService>();

            services.Configure<SecurityOptions>(_ => { });
            services.Configure<OperationsControlOptions>(_ => { });
            services.AddNightCodeForgeCoreAndInfrastructure($"Data Source={tempDb}");
            services.Configure<LlmProviderOptions>(_ => { });
            services.Configure<NightCodeForge.Infrastructure.Inference.LlmEndpointOptions>(_ => { });
            services.Configure<RetryPolicyOptions>(_ => { });
            services.Configure<LlmRetryPolicyOptions>(_ => { });
            services.AddNightCodeForgeInference(LlmProviderType.Ollama);
            services.AddNightCodeForgeAgents(LlmProviderType.Ollama);
            services.AddNightCodeForgeHangfire($"Data Source={tempHangfireDb}");
            services.AddSignalR();
            services.AddNightCodeForgeSignalRBridge();
            services.AddScoped<OrchestratorHubClientConnection>();
            services.AddScoped<IWebNotificationService, WebNotificationService>();
            services.AddScoped<WebNotificationService>(sp => (WebNotificationService)sp.GetRequiredService<IWebNotificationService>());

            await using var sp = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateScopes = true,
                ValidateOnBuild = true
            });

            await using (var initScope = sp.CreateAsyncScope())
            {
                var db = initScope.ServiceProvider.GetRequiredService<AppDbContext>();
                await db.Database.EnsureCreatedAsync();
            }

            var coord = sp.GetRequiredService<IIdeationConversationCoordinator>();
            Assert.NotNull(coord);

            await using var scope = sp.CreateAsyncScope();
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<IIdeationProposalService>());
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<IdeationSessionState>());
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<ITaskMutationService>());
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<IAttachmentUiService>());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var dbFile in new[] { tempDb, tempHangfireDb })
            {
                if (File.Exists(dbFile))
                {
                    try { File.Delete(dbFile); } catch (IOException) { }
                }
            }
        }
    }

    [Fact]
    public async Task IdeationPage_BrowserReload_PreservesSessionOptionsAndTranscript_MidTurnAndAfterCompletion()
    {
        var taskRepo = new InMemoryTaskRepository();
        var workflowRepo = new InMemoryWorkflowRepository();
        var workspaceRepo = new InMemoryWorkspaceRepository();
        workspaceRepo.SeedGlobalProfiles(AgentProfile.CreateStandardGlobalProfiles("test-model"));
        var ws = new Workspace { Id = 804, Name = "Reload Options WS", DefaultBaseBranch = "main" };
        await workspaceRepo.CreateAsync(ws);

        var coordinator = new MockIdeationConversationCoordinator(taskRepo);
        var question = new IdeationQuestion("Confirm target?", null, ["Yes", "No"]);
        coordinator.StreamUpdatesToYield =
        [
            new(IdeationStreamUpdateKind.QuestionAsked, Question: question, Questions: [question]),
            new(IdeationStreamUpdateKind.MessageDelta, TextDelta: "Confirmed."),
            new(IdeationStreamUpdateKind.Completed, FinalResult: new IdeationResult(
                "Confirmed.",
                [],
                TranscriptPath: "/tmp/ideation-conv.md",
                FullTranscriptMarkdown: "# Transcript\nConfirmed."))
        ];

        // Circuit 1: configure options and send prompt
        using (var ctx1 = new BunitContext())
        {
            ctx1.JSInterop.Mode = JSRuntimeMode.Loose;
            var wsState1 = new WorkspaceStateService();
            wsState1.SetActiveWorkspace(ws);
            var session1 = TestServices.CreateIdeationSessionState(coordinator: coordinator, taskRepository: taskRepo, workflowRepository: workflowRepo);
            RegisterSharedWebServices(ctx1.Services, coordinator, taskRepo, workflowRepo, workspaceRepo, wsState1, TestServices.CreateToastService(), session1);

            var cut1 = ctx1.Render<Ideation>();
            cut1.WaitForAssertion(() => Assert.NotNull(session1.ActiveConversationId));

            await cut1.InvokeAsync(() => cut1.Find("button.prompt-config-toggle-btn").Click());
            await cut1.InvokeAsync(() => cut1.Find("textarea.system-prompt-textarea").Change("Prefer DDD."));
            await cut1.InvokeAsync(() => cut1.Find(".system-prompt-card select.forge-input").Change(OllamaRoutingTarget.Cloud.ToString()));
            await cut1.InvokeAsync(() => cut1.Find("#ideation-model-override-input").Change("qwen3:32b"));

            await cut1.InvokeAsync(() => cut1.Find("textarea.chat-textarea").Input("Start review"));
            await cut1.InvokeAsync(() => cut1.Find("button.send-btn").Click());
            cut1.WaitForAssertion(() => Assert.NotNull(cut1.FindComponent<AgentQuestionTurnCard>()));
        }

        // Circuit 2: browser reload mid-turn preserves options, answers question, completes with transcript
        using (var ctx2 = new BunitContext())
        {
            ctx2.JSInterop.Mode = JSRuntimeMode.Loose;
            var wsState2 = new WorkspaceStateService();
            wsState2.SetActiveWorkspace(ws);
            var session2 = TestServices.CreateIdeationSessionState(coordinator: coordinator, taskRepository: taskRepo, workflowRepository: workflowRepo);
            RegisterSharedWebServices(ctx2.Services, coordinator, taskRepo, workflowRepo, workspaceRepo, wsState2, TestServices.CreateToastService(), session2);

            var cut2 = ctx2.Render<Ideation>();
            cut2.WaitForAssertion(() =>
            {
                Assert.True(session2.IsGenerating);
                Assert.Equal(OllamaRoutingTarget.Cloud, session2.SelectedRoutingTarget);
                Assert.Equal("qwen3:32b", session2.SelectedModelOverride);
                Assert.Equal("Prefer DDD.", session2.CustomSystemPrompt);
            });

            await cut2.InvokeAsync(() =>
            {
                cut2.FindAll("button.option-btn")[0].Click();
                cut2.Find("button.submit-answer-btn").Click();
            });

            cut2.WaitForAssertion(() => Assert.False(session2.IsGenerating));
            Assert.Equal("/tmp/ideation-conv.md", session2.LatestTranscriptPath);
            Assert.Equal("# Transcript\nConfirmed.", session2.LatestFullTranscriptMarkdown);
        }

        // Circuit 3: browser reload after completion still preserves options and transcript
        using (var ctx3 = new BunitContext())
        {
            ctx3.JSInterop.Mode = JSRuntimeMode.Loose;
            var wsState3 = new WorkspaceStateService();
            wsState3.SetActiveWorkspace(ws);
            var session3 = TestServices.CreateIdeationSessionState(coordinator: coordinator, taskRepository: taskRepo, workflowRepository: workflowRepo);
            RegisterSharedWebServices(ctx3.Services, coordinator, taskRepo, workflowRepo, workspaceRepo, wsState3, TestServices.CreateToastService(), session3);

            var cut3 = ctx3.Render<Ideation>();
            cut3.WaitForAssertion(() =>
            {
                Assert.False(session3.IsGenerating);
                Assert.Equal(OllamaRoutingTarget.Cloud, session3.SelectedRoutingTarget);
                Assert.Equal("qwen3:32b", session3.SelectedModelOverride);
                Assert.Equal("Prefer DDD.", session3.CustomSystemPrompt);
                Assert.Equal("/tmp/ideation-conv.md", session3.LatestTranscriptPath);
                Assert.Equal("# Transcript\nConfirmed.", session3.LatestFullTranscriptMarkdown);
            });
        }
    }

    [Fact]
    public async Task SendMessageAsync_OnSynchronousFailureOrConcurrentReject_RollsBackOptimisticUserMessageAndPreservesChips()
    {
        var taskRepo = new InMemoryTaskRepository();
        var coordinator = new MockIdeationConversationCoordinator(taskRepo);
        var session = TestServices.CreateIdeationSessionState(coordinator: coordinator, taskRepository: taskRepo);

        var notifications = new System.Collections.Concurrent.ConcurrentQueue<(string Title, string Message, ToastLevel Level)>();
        session.OnNotification += (t, m, l) => notifications.Enqueue((t, m, l));

        var ws = new Workspace { Id = 805, Name = "Rollback WS", DefaultBaseBranch = "main" };
        await session.ReloadWorkspaceContextAsync(ws, null, null);

        var chip = new ConversationAttachmentChipViewModel
        {
            AttachmentId = 42,
            Title = "spec.md",
            SourceRef = "spec.md",
            Status = AttachmentIntakeStatus.Stored
        };

        // 1. Synchronous start failure (e.g. OperationsPausedException)
        session.PendingComposerChips.Add(chip);
        coordinator.SynchronousStartException = new OperationsPausedException("Global operations are paused.");

        var sent1 = await session.SendMessageAsync(ws, "Will fail synchronously");
        Assert.False(sent1);
        Assert.Empty(session.Messages);
        Assert.Single(session.PendingComposerChips);
        Assert.Contains(notifications, n => n.Level == ToastLevel.Error && n.Message.Contains("paused", StringComparison.OrdinalIgnoreCase));

        // 2. Concurrent active turn rejection before this circuit observes IsGenerating == true
        coordinator.SynchronousStartException = null;
        while (notifications.TryDequeue(out _)) { }
        var allowYieldQuestion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.OnBeforeYieldUpdateAsync = async _ => await allowYieldQuestion.Task;
        var pendingQ = new IdeationQuestion("Waiting...", null, ["Continue"]);
        coordinator.StreamUpdatesToYield = [new(IdeationStreamUpdateKind.QuestionAsked, Question: pendingQ, Questions: [pendingQ])];
        Assert.True(await coordinator.StartTurnAsync(new IdeationSendRequest(ws.Id, session.ActiveConversationId!.Value, "Tab 1 message")));

        var sent2 = await session.SendMessageAsync(ws, "Tab 2 concurrent message");
        Assert.False(sent2);
        Assert.DoesNotContain(session.Messages, m => m.Content == "Tab 2 concurrent message");
        Assert.Single(session.PendingComposerChips);
        Assert.Contains(notifications, n => n.Title == "Turn Already Active" && n.Level == ToastLevel.Warning);

        allowYieldQuestion.TrySetResult();
        pendingQ.Cancel();
        await coordinator.WaitForActiveTurnAsync(session.ActiveConversationId.Value);
    }

    [Fact]
    public async Task WorkspaceSwitch_ClearsPendingComposerChips_And_InvalidTaskIdToastFiresOnce()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var taskRepo = new InMemoryTaskRepository();
        var workflowRepo = new InMemoryWorkflowRepository();
        var workspaceRepo = new InMemoryWorkspaceRepository();
        workspaceRepo.SeedGlobalProfiles(AgentProfile.CreateStandardGlobalProfiles("test-model"));
        var ws1 = new Workspace { Id = 806, Name = "WS One", DefaultBaseBranch = "main" };
        var ws2 = new Workspace { Id = 807, Name = "WS Two", DefaultBaseBranch = "main" };
        await workspaceRepo.CreateAsync(ws1);
        await workspaceRepo.CreateAsync(ws2);

        var coordinator = new MockIdeationConversationCoordinator(taskRepo);
        var wsState = new WorkspaceStateService();
        wsState.SetActiveWorkspace(ws1);
        var toastService = TestServices.CreateToastService();
        var session = TestServices.CreateIdeationSessionState(coordinator: coordinator, taskRepository: taskRepo, workflowRepository: workflowRepo);
        RegisterSharedWebServices(ctx.Services, coordinator, taskRepo, workflowRepo, workspaceRepo, wsState, toastService, session);

        session.PendingComposerChips.Add(new ConversationAttachmentChipViewModel
        {
            AttachmentId = 10,
            Title = "ws1-chip.md",
            SourceRef = "ws1-chip.md",
            Status = AttachmentIntakeStatus.Stored
        });

        var nav = ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        nav.NavigateTo("/ideation?taskId=9999");
        var cut = ctx.Render<Ideation>();

        cut.WaitForAssertion(() =>
        {
            Assert.Single(toastService.Toasts, t => t.Title == "Cannot Refine Task");
        });

        // Verify workspace switch clears stale composer chips from ws1
        Assert.Single(session.PendingComposerChips);
        wsState.SetActiveWorkspace(ws2);

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(ws2.Id, session.BoundWorkspaceId);
            Assert.Empty(session.PendingComposerChips);
        });
    }

    [Fact]
    public async Task SessionSwitching_DetachesSubscriptionBeforeReleasingAffinity_AcrossSelectRefinementAndCompanionDraft()
    {
        var taskRepo = new InMemoryTaskRepository();
        var workflowRepo = new InMemoryWorkflowRepository();
        var draftStore = new CompanionTriageDraftStore(TimeProvider.System);
        var coordinator = new MockIdeationConversationCoordinator(taskRepo);
        var session = TestServices.CreateIdeationSessionState(
            coordinator: coordinator,
            taskRepository: taskRepo,
            workflowRepository: workflowRepo,
            companionDraftStore: draftStore);

        var ws = new Workspace { Id = 808, Name = "Switch WS", DefaultBaseBranch = "main" };
        var conv1 = await taskRepo.StartIdeationConversationAsync(ws.Id, "Session 1");
        var conv2 = await taskRepo.StartIdeationConversationAsync(ws.Id, "Session 2");
        var draftTask = await taskRepo.CreateWithBaselineAsync(
            new ForgeTask { WorkspaceId = ws.Id, Title = "Draft To Refine", Description = "Scope", Status = TaskOverallStatus.Draft },
            null,
            AuditActors.User,
            "Init");

        await session.ReloadWorkspaceContextAsync(ws, null, null);
        var initialConvId = session.ActiveConversationId!.Value;
        var nextConvId = initialConvId == conv1.Id ? conv2.Id : conv1.Id;

        await session.SelectSessionAsync(ws.Id, nextConvId);
        Assert.Equal(nextConvId, session.ActiveConversationId);
        var release1 = Assert.Single(coordinator.ReleaseEvents);
        Assert.Equal((ws.Id, initialConvId, 0), release1);

        var refinement = await session.TryInitializeTaskRefinementAsync(ws.Id, draftTask.Id);
        Assert.True(refinement.Handled);
        var refineConvId = session.ActiveConversationId!.Value;
        Assert.NotEqual(nextConvId, refineConvId);
        Assert.Equal(2, coordinator.ReleaseEvents.Count);
        Assert.Equal((ws.Id, nextConvId, 0), coordinator.ReleaseEvents[1]);

        var companionDraft = draftStore.CreateDraft(ws.Id, "Companion Finding", "# Findings\nCheck X", "main");
        var draftInit = await session.TryInitializeCompanionDraftAsync(ws.Id, companionDraft.DraftId);
        Assert.True(draftInit.Handled);
        Assert.Equal(3, coordinator.ReleaseEvents.Count);
        Assert.Equal((ws.Id, refineConvId, 0), coordinator.ReleaseEvents[2]);
    }

    [Fact]
    public async Task StreamError_ZeroCompletedToolsRollsBackOptimisticUserAndChips_CompletedToolsAppendsErrorNotice()
    {
        var taskRepo = new InMemoryTaskRepository();
        var coordinator = new MockIdeationConversationCoordinator(taskRepo);
        var session = TestServices.CreateIdeationSessionState(coordinator: coordinator, taskRepository: taskRepo);

        var notifications = new System.Collections.Concurrent.ConcurrentQueue<(string Title, string Message, ToastLevel Level)>();
        session.OnNotification += (t, m, l) => notifications.Enqueue((t, m, l));

        var ws = new Workspace { Id = 809, Name = "Error Consistency WS", DefaultBaseBranch = "main" };
        await session.ReloadWorkspaceContextAsync(ws, null, null);

        // Case 1: Error with zero completed tools rolls back optimistic user message and restores composer chips
        var chip = new ConversationAttachmentChipViewModel
        {
            AttachmentId = 77,
            Title = "context.md",
            SourceRef = "context.md",
            Status = AttachmentIntakeStatus.Stored
        };
        session.PendingComposerChips.Add(chip);
        coordinator.StreamUpdatesToYield =
        [
            new(IdeationStreamUpdateKind.Error, ErrorMessage: "Zero-tool failure")
        ];

        Assert.True(await session.SendMessageAsync(ws, "Will fail before tools"));
        SpinWait.SpinUntil(() => !session.IsGenerating, TimeSpan.FromSeconds(5));

        Assert.False(session.IsGenerating);
        Assert.Empty(session.Messages);
        var restoredChip = Assert.Single(session.PendingComposerChips);
        Assert.Equal(77, restoredChip.AttachmentId);
        Assert.Contains(notifications, n => n.Level == ToastLevel.Error && n.Message.Contains("Zero-tool failure"));

        // Case 2: Error AFTER a completed tool keeps user message and appends ErrorAfterToolsAssistantNotice
        while (notifications.TryDequeue(out _)) { }
        var readCall = new IdeationToolCallInfo("read-err-1", CanonicalToolNames.Read, "{\"path\":\"Program.cs\"}");
        var readDone = readCall with { Result = "Console.WriteLine();", DurationMs = 4 };
        coordinator.StreamUpdatesToYield =
        [
            new(IdeationStreamUpdateKind.ToolCallStarted, ToolCall: readCall),
            new(IdeationStreamUpdateKind.ToolCallCompleted, ToolCall: readDone),
            new(IdeationStreamUpdateKind.MessageDelta, TextDelta: "Partial findings."),
            new(IdeationStreamUpdateKind.Error, ErrorMessage: "Crash after tool")
        ];

        Assert.True(await session.SendMessageAsync(ws, "Inspect then fail"));
        SpinWait.SpinUntil(() => !session.IsGenerating, TimeSpan.FromSeconds(5));

        Assert.False(session.IsGenerating);
        Assert.Equal(2, session.Messages.Count);
        Assert.Equal("user", session.Messages[0].Role);
        Assert.Equal("Inspect then fail", session.Messages[0].Content);

        var assistantMsg = session.Messages[1];
        Assert.Equal("assistant", assistantMsg.Role);
        Assert.Contains("Partial findings.", assistantMsg.Content);
        Assert.Contains(IIdeationConversationCoordinator.ErrorAfterToolsAssistantNotice, assistantMsg.Content);
        Assert.NotNull(assistantMsg.Parts);
        Assert.Contains(assistantMsg.Parts, p => p is ToolCallContentPart tc && tc.ToolCall.ToolCallId == "read-err-1" && tc.ToolCall.IsCompleted);
        Assert.Contains(assistantMsg.Parts, p => p is TextContentPart tp && tp.Text.Contains(IIdeationConversationCoordinator.ErrorAfterToolsAssistantNotice));
        Assert.Contains(notifications, n => n.Level == ToastLevel.Error && n.Message.Contains("Crash after tool"));
    }

    [Fact]
    public async Task CancelledTurn_AfterThinkingWithEmptyTextBuffer_RendersStoppedNoticeInPartsAndMarkup()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var taskRepo = new InMemoryTaskRepository();
        var workflowRepo = new InMemoryWorkflowRepository();
        var workspaceRepo = new InMemoryWorkspaceRepository();
        workspaceRepo.SeedGlobalProfiles(AgentProfile.CreateStandardGlobalProfiles("test-model"));
        var ws = new Workspace { Id = 810, Name = "Cancel Markup WS", DefaultBaseBranch = "main" };
        await workspaceRepo.CreateAsync(ws);

        var coordinator = new MockIdeationConversationCoordinator(taskRepo)
        {
            StreamUpdatesToYield =
            [
                new(IdeationStreamUpdateKind.ThinkingDelta, TextDelta: "Initial reasoning before stop..."),
                new(IdeationStreamUpdateKind.Completed, FinalResult: new IdeationResult(
                    IIdeationConversationCoordinator.CancelledAssistantNotice.TrimStart(),
                    [],
                    IsCancelled: true))
            ]
        };

        var wsState = new WorkspaceStateService();
        wsState.SetActiveWorkspace(ws);
        var session = TestServices.CreateIdeationSessionState(coordinator: coordinator, taskRepository: taskRepo, workflowRepository: workflowRepo);
        RegisterSharedWebServices(ctx.Services, coordinator, taskRepo, workflowRepo, workspaceRepo, wsState, TestServices.CreateToastService(), session);

        var cut = ctx.Render<Ideation>();
        cut.WaitForAssertion(() => Assert.NotNull(session.ActiveConversationId));

        await cut.InvokeAsync(() => cut.Find("textarea.chat-textarea").Input("Stop mid-thinking"));
        await cut.InvokeAsync(() => cut.Find("button.send-btn").Click());

        cut.WaitForAssertion(() =>
        {
            Assert.False(session.IsGenerating);
            Assert.Equal(2, session.Messages.Count);
            var assistantMsg = session.Messages[1];
            Assert.NotNull(assistantMsg.Parts);
            Assert.Contains(assistantMsg.Parts, p => p is ThinkingContentPart);
            Assert.Contains(assistantMsg.Parts, p => p is TextContentPart tp && tp.Text.Contains("Generation stopped by user"));
            Assert.Contains("Generation stopped by user", cut.Markup);
        });
    }

    [Fact]
    public async Task MidTurnReattach_BetweenSubscribeAndLoad_HandlesCompletedAndNewTurnStreamsWithoutDuplicates()
    {
        var taskRepo = new InMemoryTaskRepository();
        var coordinator = new MockIdeationConversationCoordinator(taskRepo);
        var ws = new Workspace { Id = 811, Name = "Race Guard WS", DefaultBaseBranch = "main" };

        // Part A: Active turn completes BETWEEN Subscribe and LoadConversationAsync -> cost must not double-count
        var convA = await taskRepo.StartIdeationConversationAsync(ws.Id, "Finish Between Subscribe And Load");
        var gatePart1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var usageDelta = new SessionUsageDelta(120, 45, 0, 0.05, 0.05, 0.0, 0.0, "USD");

        coordinator.OnBeforeYieldUpdateAsync = async u =>
        {
            if (u.Kind == IdeationStreamUpdateKind.MessageDelta && u.TextDelta == "Part 1")
            {
                return;
            }
            if (u.Kind == IdeationStreamUpdateKind.Completed)
            {
                gatePart1.TrySetResult();
                await releaseFinish.Task;
            }
        };
        coordinator.StreamUpdatesToYield =
        [
            new(IdeationStreamUpdateKind.MessageDelta, TextDelta: "Part 1"),
            new(IdeationStreamUpdateKind.Completed, FinalResult: new IdeationResult("Part 1", [], UsageDelta: usageDelta))
        ];

        Assert.True(await coordinator.StartTurnAsync(new IdeationSendRequest(ws.Id, convA.Id, "Prompt A")));
        await gatePart1.Task.WaitAsync(TimeSpan.FromSeconds(5));

        coordinator.OnBeforeLoadConversationAsync = async (_, targetId, _, _) =>
        {
            if (targetId == convA.Id)
            {
                releaseFinish.TrySetResult();
                await coordinator.WaitForActiveTurnAsync(convA.Id);
                await taskRepo.UpdateConversationUsageAsync(convA.Id, usageDelta);
            }
        };

        var sessionA = TestServices.CreateIdeationSessionState(coordinator: coordinator, taskRepository: taskRepo);
        await sessionA.SelectSessionAsync(ws.Id, convA.Id);
        SpinWait.SpinUntil(() => !sessionA.IsGenerating, TimeSpan.FromSeconds(5));

        Assert.Equal(2, sessionA.Messages.Count);
        Assert.Equal("Part 1", sessionA.Messages[1].Content);
        Assert.Equal(0.05, sessionA.ActiveSessionCost.TotalCost, 6);

        // Part B: New turn starts and buffers updates BETWEEN Subscribe and LoadConversationAsync -> deltas not doubled
        var convB = await taskRepo.StartIdeationConversationAsync(ws.Id, "Start Between Subscribe And Load");
        var pendingQ = new IdeationQuestion("Hold stream", null, ["OK"]);
        var questionEmitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        coordinator.OnBeforeYieldUpdateAsync = u =>
        {
            if (u.Kind == IdeationStreamUpdateKind.QuestionAsked)
            {
                questionEmitted.TrySetResult();
            }
            return Task.CompletedTask;
        };
        coordinator.StreamUpdatesToYield =
        [
            new(IdeationStreamUpdateKind.MessageDelta, TextDelta: "Fresh token"),
            new(IdeationStreamUpdateKind.QuestionAsked, Question: pendingQ, Questions: [pendingQ])
        ];
        coordinator.OnBeforeLoadConversationAsync = async (_, targetId, _, _) =>
        {
            if (targetId == convB.Id)
            {
                coordinator.OnBeforeLoadConversationAsync = null;
                Assert.True(await coordinator.StartTurnAsync(new IdeationSendRequest(ws.Id, convB.Id, "Concurrent start")));
                await questionEmitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
        };

        var sessionB = TestServices.CreateIdeationSessionState(coordinator: coordinator, taskRepository: taskRepo);
        await sessionB.SelectSessionAsync(ws.Id, convB.Id);
        SpinWait.SpinUntil(() => sessionB.PendingQuestions.Count > 0, TimeSpan.FromSeconds(5));

        Assert.Equal("Fresh token", sessionB.CurrentMessageBuffer);

        pendingQ.Submit("OK");
        SpinWait.SpinUntil(() => !sessionB.IsGenerating, TimeSpan.FromSeconds(5));
        Assert.Equal("Fresh token", sessionB.Messages.Last().Content);
    }

    [Fact]
    public async Task ApplyConfigChangeAsync_WhenCoordinatorThrows_SurfacesErrorNotificationWithoutThrowing()
    {
        var taskRepo = new InMemoryTaskRepository();
        var coordinator = new MockIdeationConversationCoordinator(taskRepo);
        var session = TestServices.CreateIdeationSessionState(coordinator: coordinator, taskRepository: taskRepo);

        var notifications = new List<(string Title, string Message, ToastLevel Level)>();
        session.OnNotification += (t, m, l) => notifications.Add((t, m, l));

        var ws = new Workspace { Id = 812, Name = "Config Error WS", DefaultBaseBranch = "main" };
        await session.ReloadWorkspaceContextAsync(ws, null, null);

        coordinator.UpdateOptionsException = new InvalidOperationException("Coordinator config failure");
        session.SelectedModelOverride = "qwen3:32b";

        await session.ApplyConfigChangeAsync();

        var err = Assert.Single(notifications, n => n.Level == ToastLevel.Error);
        Assert.Equal("Configuration Error", err.Title);
        Assert.Contains("Coordinator config failure", err.Message);
    }
}
