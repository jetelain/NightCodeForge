using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Core.Concurrency;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Execution;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Streaming;
using NightCodeForge.Core.Tests.Fixtures;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Core.Workflow.Stages;

namespace NightCodeForge.Core.Tests;

public class Step125BTaskDifficultyTests : IDisposable
{
    private readonly string _tempWorkspaceDir;
    private readonly string _tempWorktreeDir;

    public Step125BTaskDifficultyTests()
    {
        _tempWorkspaceDir = Path.Combine(Path.GetTempPath(), "ncf_125b_wks_" + Guid.NewGuid().ToString("N"));
        _tempWorktreeDir = Path.Combine(Path.GetTempPath(), "ncf_125b_wkt_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempWorkspaceDir);
        Directory.CreateDirectory(_tempWorktreeDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempWorkspaceDir)) Directory.Delete(_tempWorkspaceDir, true);
            if (Directory.Exists(_tempWorktreeDir)) Directory.Delete(_tempWorktreeDir, true);
        }
        catch (Exception ex)
        {
            _ = ex.Message;
        }
    }

    private sealed class SynchronousBackgroundTaskRunner : IBackgroundTaskRunner
    {
        public void RunTracked(Func<CancellationToken, Task> work, string description, CancellationToken cancellationToken = default) =>
            work(cancellationToken).GetAwaiter().GetResult();

        public void RunTracked(Func<Task> work, string description) =>
            work().GetAwaiter().GetResult();
    }

    private sealed class CapturingPreloadService : ILlmModelPreloadService
    {
        public List<(string ModelName, ModelPreloadOptions? Options)> Calls { get; } = new();

        public Task<ModelPreloadResult> PreloadModelAsync(
            string modelName,
            ModelPreloadOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((modelName, options));
            return Task.FromResult(new ModelPreloadResult(true, modelName, LoadAccepted: true));
        }
    }

    private sealed class CapturingActivityStreamer : IAgentActivityStreamer
    {
        public List<AgentActivityEvent> Events { get; } = new();

        public Task BroadcastAsync(AgentActivityEvent activityEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(activityEvent);
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<AgentActivityEvent> SubscribeAsync(
            int? taskId = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class CapturingHarness : IAgentHarness
    {
        public CapturingHarness(string harnessId) => HarnessId = harnessId;

        public string HarnessId { get; }
        public List<AgentProfile> CapturedProfiles { get; } = new();
        public Func<AgentSessionContext, CancellationToken, Task>? OnRunAsync { get; set; }

        public async Task<AgentRunResult> RunStageAsync(
            AgentSessionContext context,
            CancellationToken cancellationToken = default)
        {
            CapturedProfiles.Add(context.Profile);
            if (OnRunAsync != null)
            {
                await OnRunAsync(context, cancellationToken);
            }

            return AgentRunResult.Completed(
                new AgentRunMetrics(100, 50, 50, 100),
                summary: $"Executed on {HarnessId} ({context.Profile.ModelName})");
        }
    }

    private sealed class FakeExecutionEnvironment : IExecutionEnvironment, IExecutionEnvironmentProvider
    {
        public ExecutionTier Tier => ExecutionTier.BareHost;
        public Task<IExecutionEnvironment> GetEnvironmentAsync(SandboxScopeDescriptor scope, CancellationToken cancellationToken = default) => Task.FromResult<IExecutionEnvironment>(this);
        public Task<IExecutionEnvironment> GetEnvironmentAsync(Workspace workspace, SandboxSecurityTier securityTier = SandboxSecurityTier.Standard, int? taskId = null, CancellationToken cancellationToken = default) => Task.FromResult<IExecutionEnvironment>(this);
        public Task<IExecutionEnvironment> RecreateEnvironmentContainerAsync(SandboxScopeDescriptor scope, CancellationToken cancellationToken = default) => Task.FromResult<IExecutionEnvironment>(this);
        public Task<IExecutionEnvironment> RecreateEnvironmentContainerAsync(Workspace workspace, SandboxSecurityTier securityTier = SandboxSecurityTier.Standard, int? taskId = null, CancellationToken cancellationToken = default) => Task.FromResult<IExecutionEnvironment>(this);
        public Task CleanupTaskContainerAsync(Workspace workspace, int taskId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CleanupWorkspaceContainersAsync(Workspace workspace, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<int>> ListTaskContainerIdsAsync(Workspace workspace, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<int>>([]);
        public Task<int> EvictIdleWorkspaceContainersAsync(Workspace workspace, TimeSpan idleTimeout, CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task<CommandResult> ExecuteAsync(string command, string? workingDirectory = null, int? timeoutSeconds = null, IReadOnlyDictionary<string, string>? environmentVariables = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new CommandResult(0, "Build succeeded.", string.Empty, TimeSpan.FromMilliseconds(5)));
    }

    private sealed class WorktreeOverrideGitManager : FakeGitWorkspaceManager, IGitWorkspaceManager
    {
        private readonly string _worktreePath;
        public WorktreeOverrideGitManager(string worktreePath) => _worktreePath = worktreePath;
        string IGitWorkspaceManager.GetWorktreePath(Workspace workspace, int taskId) => _worktreePath;
    }

    private static TaskMarkdownSyncService CreateSyncService(ITaskRepository taskRepo)
    {
        var services = new ServiceCollection();
        services.AddSingleton(taskRepo);
        services.AddSingleton<IDependencyResolutionEngine>(new DependencyResolutionEngine(new InMemoryTaskDependencyStore()));
        var sp = services.BuildServiceProvider();
        return new TaskMarkdownSyncService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            new Mock<IWorkspacePathResolver>().Object,
            NullLogger<TaskMarkdownSyncService>.Instance);
    }

    [Fact]
    public void CreateStandardGlobalProfiles_SeedsDifficultyOverridesOnlyOnPipelineStages()
    {
        var options = new DefaultLlmProviderOptions(
            DefaultModelId: "qwen3.5:35b-a3b",
            CoderModelId: "qwen3.5:35b-a3b",
            ArchitectModelId: "qwen3.5:122b-a10b",
            ExpertHarnessId: StandardHarnessIds.Acp,
            ExpertModelId: "claude-opus-5-5",
            ExpertThinkingEffort: ThinkingEffort.High);

        var profiles = AgentProfile.CreateStandardGlobalProfiles(options);

        var triage = profiles.Single(p => p.Id == StandardStageIds.Triage);
        var ideation = profiles.Single(p => p.Id == StandardStageIds.Ideation);
        var taskReview = profiles.Single(p => p.Id == StandardStageIds.TaskReview);
        var impl = profiles.Single(p => p.Id == StandardStageIds.Implementation);
        var codeReview = profiles.Single(p => p.Id == StandardStageIds.CodeReviewAndFix);

        Assert.Null(triage.DifficultyOverrides);
        Assert.Null(ideation.DifficultyOverrides);

        Assert.NotNull(taskReview.DifficultyOverrides);
        Assert.Equal("qwen3.5:122b-a10b", taskReview.DifficultyOverrides![TaskDifficulty.Difficult].ModelName);
        Assert.Equal(StandardHarnessIds.Crucible, taskReview.DifficultyOverrides[TaskDifficulty.Difficult].HarnessId);
        Assert.Equal(ThinkingEffort.High, taskReview.DifficultyOverrides[TaskDifficulty.Difficult].ThinkingEffort);
        Assert.Equal(StandardHarnessIds.Acp, taskReview.DifficultyOverrides[TaskDifficulty.Expert].HarnessId);
        Assert.Equal("claude-opus-5-5", taskReview.DifficultyOverrides[TaskDifficulty.Expert].ModelName);

        Assert.NotNull(impl.DifficultyOverrides);
        Assert.Equal("qwen3.5:122b-a10b", impl.DifficultyOverrides![TaskDifficulty.Difficult].ModelName);
        Assert.Equal(StandardHarnessIds.Acp, impl.DifficultyOverrides[TaskDifficulty.Expert].HarnessId);

        Assert.NotNull(codeReview.DifficultyOverrides);
        Assert.Equal(StandardHarnessIds.Acp, codeReview.DifficultyOverrides![TaskDifficulty.Expert].HarnessId);
    }

    [Fact]
    public void CreateStandardGlobalProfiles_WhenExpertOptionsUnset_ExpertDefaultsSafelyToDifficultTier()
    {
        var options = new DefaultLlmProviderOptions(
            DefaultModelId: "coder-32b",
            ArchitectModelId: "architect-122b");

        var profiles = AgentProfile.CreateStandardGlobalProfiles(options);
        var impl = profiles.Single(p => p.Id == StandardStageIds.Implementation);

        Assert.NotNull(impl.DifficultyOverrides);
        Assert.Equal(StandardHarnessIds.Crucible, impl.DifficultyOverrides![TaskDifficulty.Expert].HarnessId);
        Assert.Equal("architect-122b", impl.DifficultyOverrides[TaskDifficulty.Expert].ModelName);
        Assert.Equal(ThinkingEffort.High, impl.DifficultyOverrides[TaskDifficulty.Expert].ThinkingEffort);
    }

    [Fact]
    public void ResolveForDifficulty_ReturnsBaseForStandard_AndAppliesOverrideOrArchitectFallbackForHigherTiers()
    {
        var archProfile = new AgentProfile
        {
            Id = StandardStageIds.TaskReview,
            HarnessId = StandardHarnessIds.Crucible,
            ModelName = "arch-large",
            ThinkingEffort = ThinkingEffort.High,
            RoutingTarget = OllamaRoutingTarget.Remote
        };

        var implProfile = new AgentProfile
        {
            Id = StandardStageIds.Implementation,
            HarnessId = StandardHarnessIds.Crucible,
            ModelName = "coder-fast",
            ThinkingEffort = ThinkingEffort.Medium,
            RoutingTarget = OllamaRoutingTarget.Local,
            DifficultyOverrides = new Dictionary<TaskDifficulty, DifficultyStageOverride>
            {
                [TaskDifficulty.Expert] = new()
                {
                    HarnessId = StandardHarnessIds.Acp,
                    ModelName = "gemini-4-argon",
                    ThinkingEffort = ThinkingEffort.High,
                    RoutingTarget = OllamaRoutingTarget.Auto,
                    ContextWindowTokens = 200_000,
                    MaxOutputTokens = 16_384
                }
            }
        };

        var stdResolved = implProfile.ResolveForDifficulty(TaskDifficulty.Standard, archProfile);
        Assert.Equal("coder-fast", stdResolved.ModelName);
        Assert.Equal(StandardHarnessIds.Crucible, stdResolved.HarnessId);
        Assert.Equal(OllamaRoutingTarget.Local, stdResolved.RoutingTarget);

        // Difficult has no explicit override on implProfile -> falls back to archProfile
        Assert.True(implProfile.RequiresArchitectFallbackForDifficulty(TaskDifficulty.Difficult));
        var diffResolved = implProfile.ResolveForDifficulty(TaskDifficulty.Difficult, archProfile);
        Assert.Equal("arch-large", diffResolved.ModelName);
        Assert.Equal(ThinkingEffort.High, diffResolved.ThinkingEffort);
        Assert.Equal(OllamaRoutingTarget.Remote, diffResolved.RoutingTarget);

        // Expert has explicit ACP override -> applies ACP without modifying base implProfile
        Assert.False(implProfile.RequiresArchitectFallbackForDifficulty(TaskDifficulty.Expert));
        var expResolved = implProfile.ResolveForDifficulty(TaskDifficulty.Expert, archProfile);
        Assert.Equal(StandardHarnessIds.Acp, expResolved.HarnessId);
        Assert.Equal("gemini-4-argon", expResolved.ModelName);
        Assert.Equal(ThinkingEffort.High, expResolved.ThinkingEffort);
        Assert.Equal(200_000, expResolved.ContextWindowTokens);
        Assert.Equal(16_384, expResolved.MaxOutputTokens);
        Assert.Equal("coder-fast", implProfile.ModelName);
    }

    [Fact]
    public async Task TaskMarkdownSyncService_RoundTripsAndMergesDifficultyFrontmatter_AndRejectsInvalidValues()
    {
        var taskRepo = new InMemoryTaskRepository();
        var sync = CreateSyncService(taskRepo);

        var workspace = new Workspace
        {
            Id = 1,
            Name = "SyncDiffWS",
            RepositoryPath = _tempWorkspaceDir
        };

        var task = await taskRepo.CreateWithBaselineAsync(new ForgeTask
        {
            WorkspaceId = workspace.Id,
            Title = "Escalation Sync Task",
            Description = "Verify frontmatter",
            Priority = TaskPriority.High,
            Difficulty = TaskDifficulty.Difficult,
            MergeMode = MergeMode.ManualReviewRequired
        }, null, AuditActors.User, "Init");

        var stdHash = TaskMarkdownSyncService.ComputeTaskContentHash(new TaskFrontmatter
        {
            Title = task.Title,
            Priority = task.Priority,
            Difficulty = "standard",
            MergeMode = "ManualReviewRequired"
        }, task.Description);
        var diffHash = TaskMarkdownSyncService.ComputeTaskContentHash(new TaskFrontmatter
        {
            Title = task.Title,
            Priority = task.Priority,
            Difficulty = "difficult",
            MergeMode = "ManualReviewRequired"
        }, task.Description);
        Assert.NotEqual(stdHash, diffHash);

        var diskResult = await sync.SyncAllTasksToDiskAsync(workspace, _tempWorkspaceDir);
        var mdPath = diskResult.WrittenPaths[task.Id];
        var mdContent = await File.ReadAllTextAsync(mdPath);
        Assert.Contains("difficulty: difficult", mdContent);

        // Simulate maintainer upgrading difficulty to expert in markdown on disk
        var updatedMd = mdContent.Replace("difficulty: difficult", "difficulty: expert");
        await File.WriteAllTextAsync(mdPath, updatedMd);

        await sync.SyncAllTasksFromDiskAsync(
            workspace,
            _tempWorkspaceDir,
            hostRepositoryPath: null,
            baselineSnapshot: diskResult.Baseline,
            changeActor: AuditActors.User,
            changeReason: "Maintainer upgrade",
            currentTaskId: task.Id);

        var syncedTask = await taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(syncedTask);
        Assert.Equal(TaskDifficulty.Expert, syncedTask!.Difficulty);

        // Invalid difficulty value in markdown frontmatter must throw TaskMarkdownSyncException
        Assert.Throws<TaskMarkdownSyncException>(() => TaskMarkdownSyncService.ParseDifficulty("impossible"));
    }

    [Fact]
    public async Task DeterministicBuildStage_LookaheadPreload_PreloadsEscalatedCrucibleModelAndSkipsAcp()
    {
        var env = new FakeExecutionEnvironment();
        var preload = new CapturingPreloadService();
        var buildStage = new DeterministicBuildStage(
            env,
            preload,
            new SynchronousBackgroundTaskRunner(),
            NullLogger<DeterministicBuildStage>.Instance);

        var implProfile = new AgentProfile
        {
            Id = StandardStageIds.Implementation,
            HarnessId = StandardHarnessIds.Crucible,
            ModelName = "coder-8b",
            DifficultyOverrides = new Dictionary<TaskDifficulty, DifficultyStageOverride>
            {
                [TaskDifficulty.Difficult] = new()
                {
                    HarnessId = StandardHarnessIds.Crucible,
                    ModelName = "architect-122b",
                    ThinkingEffort = ThinkingEffort.High
                },
                [TaskDifficulty.Expert] = new()
                {
                    HarnessId = StandardHarnessIds.Acp,
                    ModelName = "claude-opus-5-5",
                    ThinkingEffort = ThinkingEffort.High
                }
            }
        };

        var workspace = new Workspace
        {
            Id = 1,
            Name = "PreloadWS",
            RepositoryPath = _tempWorkspaceDir,
            AgentProfiles = [implProfile]
        };

        var pipeline = new WorkflowPipeline(
        [
            buildStage,
            TestWorkflowPipelines.CreateImplementationStage()
        ]);

        // Case 1: Difficult task -> preloads escalated Crucible model "architect-122b"
        var diffTask = new ForgeTask
        {
            Id = 10,
            WorkspaceId = 1,
            Difficulty = TaskDifficulty.Difficult,
            CurrentStageId = buildStage.StageId
        };
        await buildStage.ExecuteAsync(new PipelineContext(diffTask, workspace) { Pipeline = pipeline });
        Assert.Single(preload.Calls);
        Assert.Equal("architect-122b", preload.Calls[0].ModelName);

        // Case 2: Expert task -> next stage uses Antigravity ACP -> local Ollama preload is skipped
        preload.Calls.Clear();
        var expertTask = new ForgeTask
        {
            Id = 11,
            WorkspaceId = 1,
            Difficulty = TaskDifficulty.Expert,
            CurrentStageId = buildStage.StageId
        };
        await buildStage.ExecuteAsync(new PipelineContext(expertTask, workspace) { Pipeline = pipeline });
        Assert.Empty(preload.Calls);
    }

    [Fact]
    public async Task TaskOrchestrator_ProcessTaskAsync_RoutesEscalatedStageToAcpWithTelemetryAndLogs()
    {
        var taskRepo = new InMemoryTaskRepository();
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var depStore = new InMemoryTaskDependencyStore();

        var acpHarness = new CapturingHarness(StandardHarnessIds.Acp);
        var crucibleHarness = new CapturingHarness(StandardHarnessIds.Crucible);
        var harnessRegistry = new HarnessRegistry();
        harnessRegistry.RegisterHarness(acpHarness);
        harnessRegistry.RegisterHarness(crucibleHarness);

        var profiles = AgentProfile.CreateStandardGlobalProfiles(new DefaultLlmProviderOptions(
            DefaultModelId: "qwen3.5:35b",
            ArchitectModelId: "qwen3.5:122b",
            ExpertHarnessId: StandardHarnessIds.Acp,
            ExpertModelId: "gemini-4-argon",
            ExpertThinkingEffort: ThinkingEffort.High));
        workspaceRepo.SeedGlobalProfiles(profiles);

        var workspace = new Workspace
        {
            Id = 1,
            Name = "AcpEscalationWS",
            RepositoryPath = _tempWorkspaceDir,
            ExecutionMode = ExecutionMode.BareHost
        };
        await workspaceRepo.CreateAsync(workspace);

        var task = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 77,
            WorkspaceId = 1,
            Title = "Complex Architecture Task",
            Status = TaskOverallStatus.Open,
            Difficulty = TaskDifficulty.Expert,
            CurrentStageId = StandardStageIds.TaskReview,
            CreatedAt = DateTime.UtcNow
        });
        depStore.AddTask(77, 1, TaskOverallStatus.Open);

        var recordedSummaries = new List<ActiveTaskSummary>();
        var telemetryMock = new Mock<IOperationsTelemetryService>();
        telemetryMock
            .Setup(t => t.RecordActiveTask(It.IsAny<ActiveTaskSummary>()))
            .Callback<ActiveTaskSummary>(s => recordedSummaries.Add(s));

        var streamer = new CapturingActivityStreamer();
        var pipeline = new WorkflowPipeline(
        [
            TestWorkflowPipelines.CreateReviewStage(),
            TestWorkflowPipelines.CreateImplementationStage()
        ]);

        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = workspaceRepo,
            DependencyStore = depStore,
            GitWorkspaceManager = new FakeGitWorkspaceManager(),
            PipelineProvider = new DefaultWorkflowPipelineProvider(pipeline),
            HarnessRegistry = harnessRegistry,
            Streamer = streamer,
            TelemetryService = telemetryMock.Object
        }.Build();

        await orchestrator.ProcessTaskAsync(task.Id);

        Assert.NotEmpty(acpHarness.CapturedProfiles);
        Assert.Empty(crucibleHarness.CapturedProfiles);
        Assert.All(acpHarness.CapturedProfiles, p =>
        {
            Assert.Equal("gemini-4-argon", p.ModelName);
            Assert.Equal(StandardHarnessIds.Acp, p.HarnessId);
        });

        Assert.Contains(recordedSummaries, s => s.HardwareTarget == "Antigravity ACP" && s.TaskId == 77);

        var startedEvt = streamer.Events.FirstOrDefault(e => e.Kind == AgentActivityKind.StageStarted && e.TaskId == 77);
        Assert.NotNull(startedEvt);
        Assert.NotNull(startedEvt!.Metadata);
        Assert.Equal("Expert", startedEvt.Metadata![AgentActivityMetadataKeys.Difficulty]?.ToString());
        Assert.Equal(StandardHarnessIds.Acp, startedEvt.Metadata[AgentActivityMetadataKeys.HarnessId]?.ToString());
        Assert.Equal("gemini-4-argon", startedEvt.Metadata[AgentActivityMetadataKeys.ModelName]?.ToString());

        var logs = taskRepo.GetAllLogs();
        Assert.Contains(logs, l => l.TaskId == 77 && l.Message.Contains("[Expert]: running stage 'task-review' with harness 'acp', model 'gemini-4-argon'", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TaskOrchestrator_ProcessTaskAsync_TaskReviewMidPipelineDifficultyUpgrade_UpdatesTaskOnDiskReadBack()
    {
        var taskRepo = new InMemoryTaskRepository();
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var depStore = new InMemoryTaskDependencyStore();

        workspaceRepo.SeedGlobalProfiles(AgentProfile.CreateStandardGlobalProfiles("local-32b"));

        var workspace = new Workspace
        {
            Id = 1,
            Name = "MidPipelineUpgradeWS",
            RepositoryPath = _tempWorkspaceDir,
            ExecutionMode = ExecutionMode.BareHost
        };
        await workspaceRepo.CreateAsync(workspace);

        var task = await taskRepo.CreateWithBaselineAsync(new ForgeTask
        {
            Id = 88,
            WorkspaceId = 1,
            Title = "Hidden Complexity Task",
            Description = "Initial scope",
            Status = TaskOverallStatus.Open,
            Difficulty = TaskDifficulty.Standard,
            CurrentStageId = StandardStageIds.TaskReview,
            CreatedAt = DateTime.UtcNow
        }, null, AuditActors.User, "Init");
        depStore.AddTask(88, 1, TaskOverallStatus.Open);

        var syncService = CreateSyncService(taskRepo);
        var gitManager = new WorktreeOverrideGitManager(_tempWorktreeDir);
        var crucibleHarness = new CapturingHarness(StandardHarnessIds.Crucible)
        {
            OnRunAsync = async (ctx, ct) =>
            {
                var relPath = ctx.PipelineContext?.DataBag.Get<string>(PipelineDataBagKeys.TaskMarkdownPath);
                var workDir = ctx.PipelineContext?.WorkingDirectory;
                Assert.False(string.IsNullOrWhiteSpace(relPath));
                Assert.False(string.IsNullOrWhiteSpace(workDir));

                var fullPath = Path.Combine(workDir!, relPath!);
                var content = await File.ReadAllTextAsync(fullPath, ct);
                var updated = content.Replace("difficulty: standard", "difficulty: difficult");
                await File.WriteAllTextAsync(fullPath, updated, ct);
            }
        };

        var harnessRegistry = new HarnessRegistry();
        harnessRegistry.RegisterHarness(crucibleHarness);

        var pipeline = new WorkflowPipeline(
        [
            TestWorkflowPipelines.CreateReviewStage(),
            TestWorkflowPipelines.CreateImplementationStage()
        ]);

        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = workspaceRepo,
            DependencyStore = depStore,
            GitWorkspaceManager = gitManager,
            PipelineProvider = new DefaultWorkflowPipelineProvider(pipeline),
            HarnessRegistry = harnessRegistry,
            MarkdownSyncService = syncService
        }.Build();

        await orchestrator.ProcessTaskAsync(task.Id);

        var updatedTask = await taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(updatedTask);
        Assert.Equal(TaskDifficulty.Difficult, updatedTask!.Difficulty);
        Assert.Equal(StandardStageIds.Implementation, updatedTask.CurrentStageId);

        var history = await taskRepo.GetHistoryAsync(task.Id);
        Assert.Contains(history, h => h.Difficulty == TaskDifficulty.Difficult);
    }

    [Fact]
    public void ResolveForDifficulty_ArchitectFallback_AppliesArchitectOverrideForRequestedDifficulty()
    {
        var archProfile = new AgentProfile
        {
            Id = StandardStageIds.TaskReview,
            HarnessId = StandardHarnessIds.Crucible,
            ModelName = "arch-standard",
            ThinkingEffort = ThinkingEffort.High,
            RoutingTarget = OllamaRoutingTarget.Local,
            DifficultyOverrides = new Dictionary<TaskDifficulty, DifficultyStageOverride>
            {
                [TaskDifficulty.Expert] = new()
                {
                    HarnessId = StandardHarnessIds.Acp,
                    ModelName = "gemini-4-argon",
                    ThinkingEffort = ThinkingEffort.High,
                    RoutingTarget = OllamaRoutingTarget.Auto,
                    ContextWindowTokens = 1_000_000
                }
            }
        };

        var implProfile = new AgentProfile
        {
            Id = StandardStageIds.Implementation,
            HarnessId = StandardHarnessIds.Crucible,
            ModelName = "coder-fast",
            ThinkingEffort = ThinkingEffort.Medium,
            RoutingTarget = OllamaRoutingTarget.Local
        };

        Assert.True(implProfile.RequiresArchitectFallbackForDifficulty(TaskDifficulty.Expert));
        var resolved = implProfile.ResolveForDifficulty(TaskDifficulty.Expert, archProfile);

        Assert.Equal(StandardHarnessIds.Acp, resolved.HarnessId);
        Assert.Equal("gemini-4-argon", resolved.ModelName);
        Assert.Equal(ThinkingEffort.High, resolved.ThinkingEffort);
        Assert.Equal(OllamaRoutingTarget.Auto, resolved.RoutingTarget);
        Assert.Equal(1_000_000, resolved.ContextWindowTokens);
    }

    [Fact]
    public async Task WorkspaceScopedProfiles_ResolveCorrectlyInInMemoryRepoAndDeterministicBuildStage()
    {
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var archDedicated = new AgentProfile
        {
            Id = $"ws-1:{StandardStageIds.TaskReview}",
            WorkspaceId = 1,
            Name = "Dedicated Architect",
            HarnessId = StandardHarnessIds.Crucible,
            ModelName = "arch-base",
            ThinkingEffort = ThinkingEffort.High,
            DifficultyOverrides = new Dictionary<TaskDifficulty, DifficultyStageOverride>
            {
                [TaskDifficulty.Difficult] = new()
                {
                    HarnessId = StandardHarnessIds.Crucible,
                    ModelName = "arch-122b",
                    ThinkingEffort = ThinkingEffort.High
                }
            }
        };
        var implDedicated = new AgentProfile
        {
            Id = $"ws-1:{StandardStageIds.Implementation}",
            WorkspaceId = 1,
            Name = "Dedicated Coder",
            HarnessId = StandardHarnessIds.Crucible,
            ModelName = "coder-8b",
            ThinkingEffort = ThinkingEffort.Medium
        };

        var workspace = new Workspace
        {
            Id = 1,
            Name = "ScopedWS",
            RepositoryPath = _tempWorkspaceDir,
            AgentProfiles = [archDedicated, implDedicated]
        };
        await workspaceRepo.CreateAsync(workspace);

        var effectiveImpl = await workspaceRepo.GetEffectiveAgentProfileAsync(1, StandardStageIds.Implementation, TaskDifficulty.Difficult);
        Assert.NotNull(effectiveImpl);
        Assert.Equal("arch-122b", effectiveImpl!.ModelName);

        var preload = new CapturingPreloadService();
        var buildStage = new DeterministicBuildStage(
            new FakeExecutionEnvironment(),
            preload,
            new SynchronousBackgroundTaskRunner(),
            NullLogger<DeterministicBuildStage>.Instance);

        var pipeline = new WorkflowPipeline(
        [
            buildStage,
            TestWorkflowPipelines.CreateImplementationStage()
        ]);

        var task = new ForgeTask
        {
            Id = 55,
            WorkspaceId = 1,
            Difficulty = TaskDifficulty.Difficult,
            CurrentStageId = buildStage.StageId
        };

        await buildStage.ExecuteAsync(new PipelineContext(task, workspace) { Pipeline = pipeline });

        Assert.Single(preload.Calls);
        Assert.Equal("arch-122b", preload.Calls[0].ModelName);
    }

    [Fact]
    public async Task TaskOrchestrator_ProcessTaskAsync_PendingProposals_ParsesValidAndInvalidDifficultyWithoutThrowing()
    {
        var taskRepo = new InMemoryTaskRepository();
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var depStore = new InMemoryTaskDependencyStore();

        workspaceRepo.SeedGlobalProfiles(AgentProfile.CreateStandardGlobalProfiles("local-32b"));

        var workspace = new Workspace
        {
            Id = 1,
            Name = "ProposalsWS",
            RepositoryPath = _tempWorkspaceDir,
            ExecutionMode = ExecutionMode.BareHost
        };
        await workspaceRepo.CreateAsync(workspace);

        var task = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 99,
            WorkspaceId = 1,
            Title = "Parent Discovery Task",
            Status = TaskOverallStatus.Open,
            Difficulty = TaskDifficulty.Standard,
            CurrentStageId = StandardStageIds.Implementation,
            CreatedAt = DateTime.UtcNow
        });
        depStore.AddTask(99, 1, TaskOverallStatus.Open);

        var proposalHarness = new CapturingHarness(StandardHarnessIds.Crucible)
        {
            OnRunAsync = (ctx, _) =>
            {
                ctx.PipelineContext?.DataBag.Set(PipelineDataBagKeys.PendingProposals, new List<TaskProposal>
                {
                    new("Valid Expert Followup", "Details", difficulty: "expert"),
                    new("Unrecognized Difficulty Followup", "Details", difficulty: "invalid-tier")
                });
                return Task.CompletedTask;
            }
        };

        var harnessRegistry = new HarnessRegistry();
        harnessRegistry.RegisterHarness(proposalHarness);

        var pipeline = new WorkflowPipeline([TestWorkflowPipelines.CreateImplementationStage()]);

        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = workspaceRepo,
            DependencyStore = depStore,
            GitWorkspaceManager = new FakeGitWorkspaceManager(),
            PipelineProvider = new DefaultWorkflowPipelineProvider(pipeline),
            HarnessRegistry = harnessRegistry
        }.Build();

        await orchestrator.ProcessTaskAsync(task.Id);

        var allTasks = await taskRepo.GetByWorkspaceIdAsync(1);
        var validFollowup = Assert.Single(allTasks, t => t.Title == "Valid Expert Followup");
        var fallbackFollowup = Assert.Single(allTasks, t => t.Title == "Unrecognized Difficulty Followup");
        Assert.Equal(TaskDifficulty.Expert, validFollowup.Difficulty);
        Assert.Equal(TaskDifficulty.Standard, fallbackFollowup.Difficulty);
    }
}
