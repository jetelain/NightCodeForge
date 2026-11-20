using Bunit;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using Moq;
using NightCodeForge.Agents.Inference;
using NightCodeForge.Agents.Tools;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Security;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Infrastructure.Configuration;
using NightCodeForge.Infrastructure.Data;
using NightCodeForge.Infrastructure.Data.Repositories;
using NightCodeForge.Infrastructure.Inference;
using NightCodeForge.Web.Components.Backlog;
using NightCodeForge.Web.Components.Monitoring;
using NightCodeForge.Web.Components.Pages;
using NightCodeForge.Web.Components.Settings;
using NightCodeForge.Web.Components.Triage;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class Step125BDifficultyUiTests : BunitContext
{
    [Fact]
    public void TaskHistoryDiffService_EmitsDifficultyFieldChange_WhenDifficultyChanges()
    {
        var diffService = new TaskHistoryDiffService();
        var histories = new List<ForgeTaskHistory>
        {
            new()
            {
                Id = 1,
                TaskId = 5,
                Version = 1,
                Title = "Task",
                Description = "Desc",
                Status = TaskOverallStatus.Open,
                Priority = TaskPriority.Normal,
                Difficulty = TaskDifficulty.Standard,
                ChangedAt = DateTime.UtcNow.AddMinutes(-5)
            },
            new()
            {
                Id = 2,
                TaskId = 5,
                Version = 2,
                Title = "Task",
                Description = "Desc",
                Status = TaskOverallStatus.Open,
                Priority = TaskPriority.Normal,
                Difficulty = TaskDifficulty.Expert,
                ChangedAt = DateTime.UtcNow
            }
        };

        var timeline = diffService.BuildTimeline(histories);
        Assert.Equal(2, timeline.Count);
        var latest = timeline[0];
        Assert.Equal(TaskDifficulty.Expert, latest.Difficulty);
        var diffChange = Assert.Single(latest.FieldChanges);
        Assert.Equal("Difficulty", diffChange.FieldName);
        Assert.Equal("Standard", diffChange.OldValue);
        Assert.Equal("Expert", diffChange.NewValue);
        Assert.Equal("badge-purple", diffChange.BadgeClass);
    }

    [Fact]
    public async Task TaskMutationService_UpdateDifficultyAsync_UpdatesTaskAndRecordsHistory()
    {
        var taskRepo = new InMemoryTaskRepository();
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var syncMock = new MockTaskMarkdownSyncService();
        var orchestrator = new MockTaskOrchestrator();
        var telemetryMock = new Mock<IOperationsTelemetryService>();
        var mutationService = new TaskMutationService(
            taskRepo,
            workspaceRepo,
            new InMemoryTaskDependencyStore(),
            syncMock,
            orchestrator,
            telemetryMock.Object,
            Mock.Of<NightCodeForge.Core.Interfaces.IIdeationConversationCoordinator>(),
            NullLogger<TaskMutationService>.Instance);

        var ws = await workspaceRepo.CreateAsync(new Workspace { Name = "MutWS", RepositoryPath = "/mut" });
        var task = await taskRepo.CreateWithBaselineAsync(new ForgeTask
        {
            WorkspaceId = ws.Id,
            Title = "Mutate Difficulty",
            Difficulty = TaskDifficulty.Standard
        }, null, AuditActors.User, "Init");

        await mutationService.UpdateDifficultyAsync(task, TaskDifficulty.Expert);

        var updated = await taskRepo.GetByIdAsync(task.Id);
        Assert.Equal(TaskDifficulty.Expert, updated!.Difficulty);

        var history = await taskRepo.GetHistoryAsync(task.Id);
        Assert.Contains(history, h => h.Difficulty == TaskDifficulty.Expert && (h.ChangeReason?.Contains("Standard to Expert") ?? false));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            mutationService.UpdateDifficultyAsync(task, (TaskDifficulty)999));
    }

    [Fact]
    public void AgentProfileDifficultyMatrixHelper_MapsRolesAndCopiesArchitectToCoderCleanly()
    {
        var profiles = AgentProfile.CreateStandardGlobalProfiles("qwen3.5:35b").ToList();
        var acpCell = new DifficultyMatrixCellModel
        {
            Difficulty = TaskDifficulty.Expert,
            Role = EscalationMatrixRole.ArchitectureAndReview,
            HarnessId = StandardHarnessIds.Acp,
            ModelName = "claude-opus-5-5",
            ThinkingEffort = ThinkingEffort.High,
            RoutingTarget = OllamaRoutingTarget.Local
        };

        AgentProfileDifficultyMatrixHelper.ApplyCellToProfiles(
            profiles,
            acpCell,
            LlmProviderType.OllamaRouter,
            new Dictionary<string, int>(),
            acpStatus: null);

        var review = profiles.Single(p => p.Id == StandardStageIds.TaskReview);
        var codeReview = profiles.Single(p => p.Id == StandardStageIds.CodeReviewAndFix);
        var impl = profiles.Single(p => p.Id == StandardStageIds.Implementation);

        Assert.Equal(StandardHarnessIds.Acp, review.DifficultyOverrides![TaskDifficulty.Expert].HarnessId);
        Assert.Equal("claude-opus-5-5", review.DifficultyOverrides[TaskDifficulty.Expert].ModelName);
        Assert.Equal(OllamaRoutingTarget.Auto, review.DifficultyOverrides[TaskDifficulty.Expert].RoutingTarget);
        Assert.Equal("claude-opus-5-5", codeReview.DifficultyOverrides![TaskDifficulty.Expert].ModelName);

        Assert.False(AgentProfileDifficultyMatrixHelper.IsCoderSameAsArchitect(profiles, TaskDifficulty.Expert));

        AgentProfileDifficultyMatrixHelper.CopyArchitectToCoder(
            profiles,
            TaskDifficulty.Expert,
            LlmProviderType.OllamaRouter,
            new Dictionary<string, int>(),
            acpStatus: null);

        Assert.True(AgentProfileDifficultyMatrixHelper.IsCoderSameAsArchitect(profiles, TaskDifficulty.Expert));
        Assert.Equal(StandardHarnessIds.Acp, impl.DifficultyOverrides![TaskDifficulty.Expert].HarnessId);
        Assert.Equal("claude-opus-5-5", impl.DifficultyOverrides[TaskDifficulty.Expert].ModelName);
    }

    [Fact]
    public async Task AgentProfilesTab_RendersDifficultyEscalationMatrix_AndAppliesHarnessChangesWithoutBreakingSelectors()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        var dbOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).Options;
        await using (var initDb = new AppDbContext(dbOptions))
        {
            await initDb.Database.EnsureCreatedAsync();
            await initDb.EnsureDefaultGlobalAgentProfilesAsync("qwen3.5:35b");
        }

        Services.AddDbContextFactory<AppDbContext>(opts => opts.UseSqlite(conn));
        Services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());
        Services.AddScoped<IWorkspaceRepository, EfCoreWorkspaceRepository>();
        Services.AddScoped<IWorkflowRepository, EfCoreWorkflowRepository>();
        Services.AddSingleton(new WorkspaceStateService());
        Services.AddSingleton(TestServices.CreateToastService());
        Services.AddSingleton<IGlobalOptimizationSettingsService>(new Mock<IGlobalOptimizationSettingsService>().Object);
        Services.AddSingleton<IWebSearchSettingsService>(new Mock<IWebSearchSettingsService>().Object);
        Services.AddSingleton<IWebSearchService>(new MockWebSearchService());

        var modelCatalog = new Mock<ILlmModelCatalogService>();
        modelCatalog.Setup(m => m.GetAvailableModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new AvailableModelInfo("qwen3.5:35b", "qwen3.5:35b"), new AvailableModelInfo("qwen3.5:122b", "qwen3.5:122b")]);
        Services.AddSingleton(modelCatalog.Object);

        var gateway = new Mock<ILlmGatewayService>();
        gateway.SetupGet(g => g.ProviderType).Returns(LlmProviderType.OllamaRouter);
        Services.AddSingleton(gateway.Object);
        Services.AddSingleton<IWslMetadataService>(new Mock<IWslMetadataService>().Object);
        Services.AddSingleton<IGitMirrorSyncService>(new Mock<IGitMirrorSyncService>().Object);
        Services.AddMemoryCache();
        Services.AddSingleton<ILlmContextWindowCache, LlmContextWindowCache>();
        Services.AddSingleton<ILlmConfigurationWriter>(new Mock<ILlmConfigurationWriter>().Object);
        Services.AddSingleton(Options.Create(new LlmProviderOptions { DefaultModelId = "qwen3.5:35b" }));
        Services.AddSingleton(Options.Create(new SecurityOptions()));
        Services.AddSingleton(new Mock<IAcpHarnessCatalogService>().Object);

        var cut = Render<AgentProfilesTab>(p => p.Add(x => x.Scope, AgentProfilesTab.ProfileScope.Global));

        var matrix = cut.Find("#difficulty-escalation-matrix");
        Assert.NotNull(matrix);
        Assert.Contains("Role-Based Task Difficulty Escalation Matrix", matrix.TextContent);

        var tierRows = matrix.QuerySelectorAll(".matrix-tier-row");
        Assert.Equal(3, tierRows.Length);

        var expertArchCell = matrix.QuerySelector(".matrix-role-cell[data-difficulty='Expert'][data-matrix-role='ArchitectureAndReview']");
        Assert.NotNull(expertArchCell);

        var expertHarnessSelect = expertArchCell!.QuerySelector("select.matrix-harness-select");
        Assert.NotNull(expertHarnessSelect);
        expertHarnessSelect!.Change(StandardHarnessIds.Acp);

        var refreshedExpertArchCell = cut.Find(".matrix-role-cell[data-difficulty='Expert'][data-matrix-role='ArchitectureAndReview']");
        var expertModelSelect = refreshedExpertArchCell.QuerySelector("select.matrix-model-select");
        Assert.NotNull(expertModelSelect);
        Assert.DoesNotContain("font-mono", expertModelSelect!.ClassList);

        var copyBtn = cut.Find(".matrix-role-cell[data-difficulty='Expert'][data-matrix-role='CoderImplementation'] button.matrix-copy-arch-btn");
        copyBtn.Click();

        await cut.WaitForAssertionAsync(() =>
        {
            var refreshedBtn = cut.Find(".matrix-role-cell[data-difficulty='Expert'][data-matrix-role='CoderImplementation'] button.matrix-copy-arch-btn");
            Assert.Contains("Same as Architecture & Review", refreshedBtn.TextContent);
        });
    }

    [Fact]
    public async Task CreateTask_And_TaskDetail_And_StageConversationViewer_ExposeDifficultyControlsAndEscalationBadges()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var wsState = new WorkspaceStateService();
        var taskRepo = new InMemoryTaskRepository();
        var wsRepo = new InMemoryWorkspaceRepository();
        var workflowRepo = new InMemoryWorkflowRepository();
        var depStore = new InMemoryTaskDependencyStore();
        var orchestrator = new MockTaskOrchestrator();
        var mutationMock = new Mock<ITaskMutationService>();

        var ws = await wsRepo.CreateAsync(new Workspace { Id = 1, Name = "UI WS", RepositoryPath = "/ui", DefaultBaseBranch = "main" });
        wsState.SetActiveWorkspace(ws);

        Services.AddSingleton(wsState);
        Services.AddSingleton<ITaskRepository>(taskRepo);
        Services.AddSingleton<IWorkspaceRepository>(wsRepo);
        Services.AddSingleton<IWorkflowRepository>(workflowRepo);
        Services.AddSingleton<ITaskDependencyStore>(depStore);
        Services.AddSingleton<IDependencyResolutionEngine>(new DependencyResolutionEngine(depStore));
        Services.AddSingleton<ITaskJobQueue>(new Mock<ITaskJobQueue>().Object);
        Services.AddSingleton<IDevQueueService>(new MockDevQueueService());
        Services.AddSingleton(TestServices.CreateToastService());
        Services.AddSingleton<ITaskOrchestrator>(orchestrator);
        Services.AddSingleton<ITaskMutationService>(mutationMock.Object);
        Services.AddSingleton<ITaskHistoryDiffService, TaskHistoryDiffService>();
        Services.AddSingleton<ITaskDiffService>(new Mock<ITaskDiffService>().Object);
        Services.AddSingleton<ISyntaxHighlightingService>(new SimpleSyntaxHighlighter());
        Services.AddSingleton(new ReviewCommentStateService());
        Services.AddSingleton(TestHubConnectionHelper.Create());
        TestServices.AddDefaultWebUiServices(Services);

        // 1. CreateTask selects Expert difficulty chip and submits ForgeTask with Difficulty = Expert
        var createCut = Render<CreateTask>();
        var diffChips = createCut.FindAll(".difficulty-chip-btn");
        Assert.Equal(3, diffChips.Count);
        diffChips[2].Click(); // Expert
        createCut.Find("#task-title-input").Input("Expert UI Task");
        await createCut.InvokeAsync(() => createCut.Find("#btn-save-as-open").Click());

        var createdTasks = await taskRepo.GetByWorkspaceIdAsync(ws.Id);
        var created = Assert.Single(createdTasks);
        Assert.Equal(TaskDifficulty.Expert, created.Difficulty);

        // 2. TaskDetail displays Difficulty badge and fires UpdateDifficultyAsync when clicking Difficult chip
        var detailCut = Render<TaskDetail>(p => p.Add(t => t.TaskId, created.Id));
        Assert.Contains("Expert", detailCut.Markup);
        var detailChips = detailCut.FindAll(".difficulty-chip-btn");
        detailChips[1].Click(); // Difficult
        mutationMock.Verify(
            m => m.UpdateDifficultyAsync(
                It.Is<ForgeTask>(t => t.Id == created.Id),
                TaskDifficulty.Difficult,
                It.IsAny<IEnumerable<int>?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);

        // 3. StageConversationViewer renders Escalated badge on Agentic escalated stages
        var conv = new Conversation
        {
            Id = 50,
            TaskId = created.Id,
            StageId = StandardStageIds.TaskReview,
            StageKind = WorkflowStageKind.Agentic,
            HarnessId = StandardHarnessIds.Acp,
            ModelName = "gemini-4-argon",
            Status = StageExecutionStatus.Completed,
            Turns = []
        };
        var viewerCut = Render<StageConversationViewer>(p => p
            .Add(x => x.Task, created)
            .Add(x => x.Conversation, conv));

        var escBadge = viewerCut.Find(".escalation-badge");
        Assert.Contains("Escalated (Expert: Antigravity ACP / gemini-4-argon)", escBadge.TextContent);
    }

    [Fact]
    public void AgentProfileDifficultyMatrixHelper_StripsPreTaskOverrides_AndCollectsCrucibleModelsAcrossOverrides()
    {
        var triage = new AgentProfile
        {
            Id = StandardStageIds.Triage,
            HarnessId = StandardHarnessIds.Crucible,
            ModelName = "triage-model",
            DifficultyOverrides = new Dictionary<TaskDifficulty, DifficultyStageOverride>
            {
                [TaskDifficulty.Expert] = new() { HarnessId = StandardHarnessIds.Acp, ModelName = "gemini-4-argon" }
            }
        };
        var wsIdeation = new AgentProfile
        {
            Id = $"ws-3:{StandardStageIds.Ideation}",
            WorkspaceId = 3,
            HarnessId = StandardHarnessIds.Crucible,
            ModelName = "ideation-model",
            DifficultyOverrides = new Dictionary<TaskDifficulty, DifficultyStageOverride>
            {
                [TaskDifficulty.Difficult] = new() { HarnessId = StandardHarnessIds.Crucible, ModelName = "arch-122b" }
            }
        };
        var impl = new AgentProfile
        {
            Id = StandardStageIds.Implementation,
            HarnessId = StandardHarnessIds.Crucible,
            ModelName = "coder-32b",
            DifficultyOverrides = new Dictionary<TaskDifficulty, DifficultyStageOverride>
            {
                [TaskDifficulty.Difficult] = new() { HarnessId = StandardHarnessIds.Crucible, ModelName = "arch-122b" },
                [TaskDifficulty.Expert] = new() { HarnessId = StandardHarnessIds.Acp, ModelName = "claude-opus-5-5" }
            }
        };

        AgentProfileDifficultyMatrixHelper.NormalizeDifficultyOverridesForSave(
            triage,
            LlmProviderType.OllamaRouter,
            new Dictionary<string, int>(),
            acpStatus: null);
        AgentProfileDifficultyMatrixHelper.NormalizeDifficultyOverridesForSave(
            wsIdeation,
            LlmProviderType.OllamaRouter,
            new Dictionary<string, int>(),
            acpStatus: null);
        AgentProfileDifficultyMatrixHelper.NormalizeDifficultyOverridesForSave(
            impl,
            LlmProviderType.OllamaRouter,
            new Dictionary<string, int>(),
            acpStatus: null);

        Assert.Null(triage.DifficultyOverrides);
        Assert.Null(wsIdeation.DifficultyOverrides);
        Assert.NotNull(impl.DifficultyOverrides);

        var distinctCrucibleModels = AgentProfileDifficultyMatrixHelper.GetDistinctCrucibleModelNames([triage, wsIdeation, impl]);
        Assert.Contains("triage-model", distinctCrucibleModels);
        Assert.Contains("ideation-model", distinctCrucibleModels);
        Assert.Contains("coder-32b", distinctCrucibleModels);
        Assert.Contains("arch-122b", distinctCrucibleModels);
        Assert.DoesNotContain("claude-opus-5-5", distinctCrucibleModels);
        Assert.DoesNotContain("gemini-4-argon", distinctCrucibleModels);
    }

    [Fact]
    public void CloneToDedicatedProfile_DeepClonesPermittedToolsAndStageDirectives()
    {
        var globalTools = new List<string> { "read", "edit" };
        var globalDirectives = new Dictionary<string, string> { [StandardStageIds.Implementation] = "Implement carefully" };
        var globalProfile = new AgentProfile
        {
            Id = StandardStageIds.Implementation,
            Name = "Coder",
            HarnessId = StandardHarnessIds.Crucible,
            ModelName = "coder-32b",
            PermittedTools = globalTools,
            StageDirectives = globalDirectives
        };

        var cloned = AgentProfileAcpPresentationHelper.CloneToDedicatedProfile(globalProfile, 7, "Repo7");
        globalTools.Add("bash");
        cloned.StageDirectives![StandardStageIds.Implementation] = "Mutated directive";

        Assert.NotSame(globalTools, cloned.PermittedTools);
        Assert.Equal(["read", "edit"], cloned.PermittedTools);
        Assert.Equal("Implement carefully", globalDirectives[StandardStageIds.Implementation]);
    }

    [Fact]
    public async Task TaskCreationModal_ResetsDifficultyToStandardAfterSubmission()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var uiMock = new Mock<ITaskCreationUiService>();
        var createdRequests = new List<TaskCreationRequest>();
        uiMock.Setup(u => u.IsPullRequestDeliveryAvailableAsync(It.IsAny<int>(), It.IsAny<Workspace?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        uiMock.Setup(u => u.LoadWorkflowsAsync(It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new WorkflowDefinition { Id = 1, Name = "Standard", IsDefault = true, Stages = [] }]);
        uiMock.Setup(u => u.ResolveDefaultWorkflowId(It.IsAny<IReadOnlyList<WorkflowDefinition>>(), It.IsAny<int?>(), It.IsAny<int?>()))
            .Returns(1);
        uiMock.Setup(u => u.CreateTaskAsync(It.IsAny<TaskCreationRequest>(), It.IsAny<CancellationToken>()))
            .Callback<TaskCreationRequest, CancellationToken>((req, _) => createdRequests.Add(req))
            .ReturnsAsync((TaskCreationRequest req, CancellationToken _) => new TaskCreationResult(
                new ForgeTask { Id = createdRequests.Count, WorkspaceId = req.WorkspaceId, Title = req.Title, Difficulty = req.Difficulty },
                false));

        Services.AddSingleton(uiMock.Object);
        Services.AddSingleton(TestServices.CreateToastService());

        var cut = Render<TaskCreationModal>(p => p
            .Add(x => x.IsOpen, true)
            .Add(x => x.WorkspaceId, 1));

        cut.Find("#task-title-input").Change("First Expert Task");
        cut.Find("#task-difficulty-select").Change(TaskDifficulty.Expert);
        await cut.InvokeAsync(() => cut.Find("#btn-confirm-create-task").Click());

        cut.Render(p => p
            .Add(x => x.IsOpen, true)
            .Add(x => x.WorkspaceId, 1));

        Assert.Equal(TaskDifficulty.Standard.ToString(), cut.Find("#task-difficulty-select").GetAttribute("value"));
        cut.Find("#task-title-input").Change("Second Default Task");
        await cut.InvokeAsync(() => cut.Find("#btn-confirm-create-task").Click());

        Assert.Equal(2, createdRequests.Count);
        Assert.Equal(TaskDifficulty.Expert, createdRequests[0].Difficulty);
        Assert.Equal(TaskDifficulty.Standard, createdRequests[1].Difficulty);
    }

    [Fact]
    public void AcpAndMatrixHelpers_ClearStaleContextWindowTokens_PreserveNullConfigJson_AndCopyAllowNoTests()
    {
        var profile = new AgentProfile
        {
            Id = StandardStageIds.Implementation,
            Name = "Coder",
            HarnessId = StandardHarnessIds.Crucible,
            ModelName = "qwen3.5:35b",
            ContextWindowTokens = 65536,
            AllowNoTests = true
        };

        var cloned = AgentProfileAcpPresentationHelper.CloneToDedicatedProfile(profile, 4, "WS4");
        Assert.True(cloned.AllowNoTests);

        // Switching to ACP clears stale Crucible ContextWindowTokens
        AgentProfileAcpPresentationHelper.ApplyHarnessChange(profile, StandardHarnessIds.Acp, acpStatus: null, fallbackCrucibleModel: "qwen3.5:35b");
        Assert.Null(profile.ContextWindowTokens);

        // Matrix Standard cell preserves null HarnessConfigurationJson and clears ContextWindowTokens for ACP
        var profiles = AgentProfile.CreateStandardGlobalProfiles("qwen3.5:35b").ToList();
        var stdCell = new DifficultyMatrixCellModel
        {
            Difficulty = TaskDifficulty.Standard,
            Role = EscalationMatrixRole.CoderImplementation,
            HarnessId = StandardHarnessIds.Crucible,
            ModelName = "qwen3.5:35b",
            ContextWindowTokens = 65536,
            HarnessConfigurationJson = null
        };
        AgentProfileDifficultyMatrixHelper.ApplyCellToProfiles(profiles, stdCell, LlmProviderType.OllamaRouter, new Dictionary<string, int>(), null);
        Assert.Null(profiles.Single(p => p.Id == StandardStageIds.Implementation).HarnessConfigurationJson);

        AgentProfileDifficultyMatrixHelper.ApplyHarnessSwitch(stdCell, StandardHarnessIds.Acp, "qwen3.5:35b", null);
        Assert.Null(stdCell.ContextWindowTokens);
    }

    [Fact]
    public async Task AgentProfilesTab_ClearsStaleContextWindowWhenSwitchingToUncachedModelOrAcp()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        var dbOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).Options;
        await using (var initDb = new AppDbContext(dbOptions))
        {
            await initDb.Database.EnsureCreatedAsync();
            await initDb.EnsureDefaultGlobalAgentProfilesAsync("qwen3.5:35b");
            var impl = await initDb.AgentProfiles.FindAsync(StandardStageIds.Implementation);
            impl!.ContextWindowTokens = 65536;
            await initDb.SaveChangesAsync();
        }

        Services.AddDbContextFactory<AppDbContext>(opts => opts.UseSqlite(conn));
        Services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());
        Services.AddScoped<IWorkspaceRepository, EfCoreWorkspaceRepository>();
        Services.AddScoped<IWorkflowRepository, EfCoreWorkflowRepository>();
        Services.AddSingleton(new WorkspaceStateService());
        Services.AddSingleton(TestServices.CreateToastService());
        Services.AddSingleton<IGlobalOptimizationSettingsService>(new Mock<IGlobalOptimizationSettingsService>().Object);
        Services.AddSingleton<IWebSearchSettingsService>(new Mock<IWebSearchSettingsService>().Object);
        Services.AddSingleton<IWebSearchService>(new MockWebSearchService());

        var modelCatalog = new Mock<ILlmModelCatalogService>();
        modelCatalog.Setup(m => m.GetAvailableModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new AvailableModelInfo("qwen3.5:35b", "qwen3.5:35b", ContextWindowTokens: 65536),
                new AvailableModelInfo("unprobed-custom:70b", "unprobed-custom:70b", ContextWindowTokens: null)
            ]);
        Services.AddSingleton(modelCatalog.Object);

        var gateway = new Mock<ILlmGatewayService>();
        gateway.SetupGet(g => g.ProviderType).Returns(LlmProviderType.OllamaRouter);
        gateway.Setup(g => g.ResolveContextWindowAsync("unprobed-custom:70b", It.IsAny<CancellationToken>())).ReturnsAsync(0);
        Services.AddSingleton(gateway.Object);
        Services.AddSingleton<IWslMetadataService>(new Mock<IWslMetadataService>().Object);
        Services.AddSingleton<IGitMirrorSyncService>(new Mock<IGitMirrorSyncService>().Object);
        Services.AddMemoryCache();
        Services.AddSingleton<ILlmContextWindowCache, LlmContextWindowCache>();
        Services.AddSingleton<ILlmConfigurationWriter>(new Mock<ILlmConfigurationWriter>().Object);
        Services.AddSingleton(Options.Create(new LlmProviderOptions { DefaultModelId = "qwen3.5:35b" }));
        Services.AddSingleton(Options.Create(new SecurityOptions()));
        Services.AddSingleton(new Mock<IAcpHarnessCatalogService>().Object);

        var cut = Render<AgentProfilesTab>(p => p.Add(x => x.Scope, AgentProfilesTab.ProfileScope.Global));

        var stdCoderCell = cut.Find(".matrix-role-cell[data-difficulty='Standard'][data-matrix-role='CoderImplementation']");
        var modelSelect = stdCoderCell.QuerySelector("select.matrix-model-select");
        Assert.NotNull(modelSelect);
        await cut.InvokeAsync(() => modelSelect!.Change("unprobed-custom:70b"));

        var saveBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Save Profiles", StringComparison.Ordinal));
        await cut.InvokeAsync(() => saveBtn.Click());

        await using var verifyDb = new AppDbContext(dbOptions);
        var savedImpl = await verifyDb.AgentProfiles.FindAsync(StandardStageIds.Implementation);
        Assert.NotNull(savedImpl);
        Assert.Equal("unprobed-custom:70b", savedImpl!.ModelName);
        Assert.Null(savedImpl.ContextWindowTokens);
    }

    [Fact]
    public void StageConversationViewer_And_TriageTaskProposalCard_RenderAcpComputeBadgeAndProposalDifficultyBadge()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<ITaskOrchestrator>(new MockTaskOrchestrator());
        Services.AddSingleton(TestServices.CreateToastService());
        TestServices.AddDefaultWebUiServices(Services);

        var acpConv = new Conversation
        {
            Id = 51,
            TaskId = 9,
            StageId = StandardStageIds.Implementation,
            StageKind = WorkflowStageKind.Agentic,
            HarnessId = StandardHarnessIds.Acp,
            ModelName = "gemini-4-argon",
            Status = StageExecutionStatus.Completed,
            Turns = []
        };
        var acpCut = Render<StageConversationViewer>(p => p.Add(x => x.Conversation, acpConv));
        Assert.Contains(TaskDifficultyPresentationHelper.AntigravityAcpComputeLabel, acpCut.Markup);
        Assert.DoesNotContain(TaskDifficultyPresentationHelper.AgenticGpuComputeLabel, acpCut.Markup);

        var crucibleConv = new Conversation
        {
            Id = 52,
            TaskId = 9,
            StageId = StandardStageIds.Implementation,
            StageKind = WorkflowStageKind.Agentic,
            HarnessId = StandardHarnessIds.Crucible,
            ModelName = "qwen3.5:35b",
            Status = StageExecutionStatus.Completed,
            Turns = []
        };
        var crucibleCut = Render<StageConversationViewer>(p => p.Add(x => x.Conversation, crucibleConv));
        Assert.Contains(TaskDifficultyPresentationHelper.AgenticGpuComputeLabel, crucibleCut.Markup);

        var expertProposal = new ForgeTask
        {
            Id = 101,
            WorkspaceId = 1,
            Title = "Expert Proposal",
            Difficulty = TaskDifficulty.Expert,
            Priority = TaskPriority.High
        };
        var expertProposalCut = Render<TriageTaskProposalCard>(p => p.Add(x => x.Task, expertProposal));
        var diffBadge = expertProposalCut.Find(".difficulty-badge");
        Assert.Contains("Expert", diffBadge.TextContent);

        var standardProposal = new ForgeTask
        {
            Id = 102,
            WorkspaceId = 1,
            Title = "Standard Proposal",
            Difficulty = TaskDifficulty.Standard,
            Priority = TaskPriority.Normal
        };
        var standardProposalCut = Render<TriageTaskProposalCard>(p => p.Add(x => x.Task, standardProposal));
        Assert.Empty(standardProposalCut.FindAll(".difficulty-badge"));
    }
}
