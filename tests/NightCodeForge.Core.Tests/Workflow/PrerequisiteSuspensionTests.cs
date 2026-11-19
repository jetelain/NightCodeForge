using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Core.Concurrency;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Execution;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Streaming;
using NightCodeForge.Core.Tests.Fixtures;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Core.Workflow.Stages;

namespace NightCodeForge.Core.Tests.Workflow;

public class PrerequisiteSuspensionTests : IDisposable
{
    private readonly string _tempWorkspaceDir;
    private readonly string _tempWorktreeDir;

    public PrerequisiteSuspensionTests()
    {
        _tempWorkspaceDir = Path.Combine(Path.GetTempPath(), "ncf_susp_wks_" + Guid.NewGuid().ToString("N"));
        _tempWorktreeDir = Path.Combine(Path.GetTempPath(), "ncf_susp_wkt_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempWorkspaceDir);
        Directory.CreateDirectory(_tempWorktreeDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempWorkspaceDir)) Directory.Delete(_tempWorkspaceDir, recursive: true);
            if (Directory.Exists(_tempWorktreeDir)) Directory.Delete(_tempWorktreeDir, recursive: true);
        }
        catch
        {
            // Ignore temp cleanup errors on Windows file locks in tests
        }
    }

    private sealed class ActionAgentHarness : IAgentHarness
    {
        private readonly Func<AgentSessionContext, Task<AgentRunResult>> _handler;
        public string HarnessId => StandardHarnessIds.Crucible;

        public ActionAgentHarness(Func<AgentSessionContext, Task<AgentRunResult>> handler)
        {
            _handler = handler;
        }

        public Task<AgentRunResult> RunStageAsync(AgentSessionContext context, CancellationToken cancellationToken = default)
            => _handler(context);
    }

    private sealed class SingleHarnessRegistry : IHarnessRegistry
    {
        private readonly IAgentHarness _harness;
        public SingleHarnessRegistry(IAgentHarness harness) => _harness = harness;
        public IAgentHarness GetHarness(string harnessId) => _harness;
        public void RegisterHarness(IAgentHarness harness) { }
        public bool TryGetHarness(string harnessId, out IAgentHarness? harness)
        {
            harness = _harness;
            return true;
        }
        public IEnumerable<IAgentHarness> GetAllHarnesses() => new[] { _harness };
    }

    private sealed class TrackingGitWorkspaceManager : IGitWorkspaceManager
    {
        private readonly FakeGitWorkspaceManager _inner = new();
        private readonly string _worktreePath;
        private readonly List<string> _cleanupEvents;
        private readonly Func<Task>? _onDeleteTaskBranch;

        public TrackingGitWorkspaceManager(
            string worktreePath,
            List<string> cleanupEvents,
            Func<Task>? onDeleteTaskBranch = null)
        {
            _worktreePath = worktreePath;
            _cleanupEvents = cleanupEvents;
            _onDeleteTaskBranch = onDeleteTaskBranch;
        }

        public List<int> DeletedBranches => _inner.DeletedTaskBranches;

        public Task EnsureRepositoryValidAsync(string repoPath, CancellationToken cancellationToken = default) =>
            _inner.EnsureRepositoryValidAsync(repoPath, cancellationToken);
        public Task CheckoutTaskBranchAsync(Workspace workspace, int taskId, string baseBranch, CancellationToken cancellationToken = default) =>
            _inner.CheckoutTaskBranchAsync(workspace, taskId, baseBranch, cancellationToken);
        public Task<string> CreateWipCheckpointAsync(Workspace workspace, int taskId, string stageName, string summary, CancellationToken cancellationToken = default) =>
            _inner.CreateWipCheckpointAsync(workspace, taskId, stageName, summary, cancellationToken);
        public Task<string> GenerateDiffAsync(Workspace workspace, int taskId, string baseBranch, CancellationToken cancellationToken = default) =>
            _inner.GenerateDiffAsync(workspace, taskId, baseBranch, cancellationToken);
        public Task<string> GenerateDiffStatAsync(Workspace workspace, int taskId, string baseBranch, CancellationToken cancellationToken = default) =>
            _inner.GenerateDiffStatAsync(workspace, taskId, baseBranch, cancellationToken);
        public Task<string> GenerateLiveDiffStatAsync(Workspace workspace, int taskId, string baseBranch = "main", CancellationToken cancellationToken = default) =>
            _inner.GenerateLiveDiffStatAsync(workspace, taskId, baseBranch, cancellationToken);
        public Task<string> GenerateLiveDiffAsync(Workspace workspace, int taskId, string baseBranch = "main", CancellationToken cancellationToken = default) =>
            _inner.GenerateLiveDiffAsync(workspace, taskId, baseBranch, cancellationToken);
        public Task<string> GetCommitDiffAsync(Workspace workspace, string commitSha, CancellationToken cancellationToken = default) =>
            _inner.GetCommitDiffAsync(workspace, commitSha, cancellationToken);
        public Task<string> SquashAndMergeAsync(Workspace workspace, int taskId, string baseBranch, string commitMessage, CancellationToken cancellationToken = default) =>
            _inner.SquashAndMergeAsync(workspace, taskId, baseBranch, commitMessage, cancellationToken);
        public Task<string> SquashAndMergeAsync(Workspace workspace, int taskId, string baseBranch, string commitMessage, bool allowCreateBaseBranch, CancellationToken cancellationToken = default) =>
            _inner.SquashAndMergeAsync(workspace, taskId, baseBranch, commitMessage, allowCreateBaseBranch, cancellationToken);
        public Task<string> EnsureWorktreeAsync(Workspace workspace, int taskId, string baseBranch, CancellationToken cancellationToken = default)
        {
            _inner.CheckedOutBranches.Add((taskId, baseBranch));
            return Task.FromResult(_worktreePath);
        }
        public Task<string> ResolveEffectiveBaseBranchAsync(Workspace workspace, int taskId, string baseBranch, CancellationToken cancellationToken = default) =>
            _inner.ResolveEffectiveBaseBranchAsync(workspace, taskId, baseBranch, cancellationToken);
        public Task EnsureWorktreeRelativePathsAsync(Workspace workspace, int taskId, CancellationToken cancellationToken = default) =>
            _inner.EnsureWorktreeRelativePathsAsync(workspace, taskId, cancellationToken);
        public Task RemoveWorktreeAsync(Workspace workspace, int taskId, CancellationToken cancellationToken = default)
        {
            _cleanupEvents.Add("remove-worktree");
            return _inner.RemoveWorktreeAsync(workspace, taskId, cancellationToken);
        }
        public string GetWorktreePath(Workspace workspace, int taskId) => _worktreePath;
        public Task RevertOrCleanAsync(Workspace workspace, int? taskId, CancellationToken cancellationToken = default) =>
            _inner.RevertOrCleanAsync(workspace, taskId, cancellationToken);
        public Task RevertOrCleanAsync(Workspace workspace, CancellationToken cancellationToken = default) =>
            _inner.RevertOrCleanAsync(workspace, cancellationToken);
        public Task ValidateGitVersionAsync(CancellationToken cancellationToken = default) =>
            _inner.ValidateGitVersionAsync(cancellationToken);
        public Task<MergeSyncResult> SyncFromBaseBranchAsync(Workspace workspace, int taskId, string baseBranch, CancellationToken cancellationToken = default) =>
            _inner.SyncFromBaseBranchAsync(workspace, taskId, baseBranch, cancellationToken);
        public Task<MergeabilityCheckResult> CheckMergeableAsync(Workspace workspace, int taskId, string baseBranch, CancellationToken cancellationToken = default) =>
            _inner.CheckMergeableAsync(workspace, taskId, baseBranch, cancellationToken);
        public Task AbortMergeAsync(Workspace workspace, int taskId, CancellationToken cancellationToken = default) =>
            _inner.AbortMergeAsync(workspace, taskId, cancellationToken);
        public Task<string> GetBranchShaAsync(Workspace workspace, string branchName, CancellationToken cancellationToken = default) =>
            _inner.GetBranchShaAsync(workspace, branchName, cancellationToken);
        public Task ResetTaskBranchToBaseAsync(Workspace workspace, int taskId, string baseBranch, CancellationToken cancellationToken = default) =>
            _inner.ResetTaskBranchToBaseAsync(workspace, taskId, baseBranch, cancellationToken);
        public Task ResetCanonicalBranchAsync(Workspace workspace, string branchName, string targetSha, CancellationToken cancellationToken = default) =>
            _inner.ResetCanonicalBranchAsync(workspace, branchName, targetSha, cancellationToken);
        public async Task DeleteTaskBranchAsync(Workspace workspace, int taskId, CancellationToken cancellationToken = default)
        {
            _cleanupEvents.Add("delete-branch");
            await _inner.DeleteTaskBranchAsync(workspace, taskId, cancellationToken);
            if (_onDeleteTaskBranch != null)
            {
                await _onDeleteTaskBranch();
            }
        }
    }

    private sealed class RecordingMirrorSyncService : IGitMirrorSyncService
    {
        public int PushToHostCount { get; private set; }
        public int PushBaseBranchCount { get; private set; }

        public Task ConfigureHostRepositoryAsync(string hostRepoPath, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string> InitializeMirrorAsync(Workspace workspace, string mirrorDirectory, CancellationToken cancellationToken = default) => Task.FromResult(mirrorDirectory);
        public Task PullFromHostAsync(string mirrorRepoPath, string baseBranch, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<GitSyncResult> PushToHostAsync(string mirrorRepoPath, int taskId, CancellationToken cancellationToken = default)
        {
            PushToHostCount++;
            return Task.FromResult(new GitSyncResult(true, false, $"feature/task-{taskId}", null));
        }
        public Task<GitSyncResult> PushBaseBranchToHostAsync(string mirrorRepoPath, string baseBranch, CancellationToken cancellationToken = default)
        {
            PushBaseBranchCount++;
            return Task.FromResult(new GitSyncResult(true, false, baseBranch, null));
        }
    }

    private static async Task AddDependencyToMarkdownFileAsync(string fullPath, int dependsOnId)
    {
        var content = (await File.ReadAllTextAsync(fullPath)).Replace("\r\n", "\n");
        var closingIdx = content.IndexOf("\n---", startIndex: 3, StringComparison.Ordinal);
        if (closingIdx < 0)
        {
            throw new InvalidOperationException($"Missing YAML frontmatter closing delimiter in '{fullPath}'.");
        }

        var frontmatter = content[..closingIdx];
        var rest = content[closingIdx..];
        frontmatter = frontmatter.Contains("depends_on: []", StringComparison.Ordinal)
            ? frontmatter.Replace("depends_on: []", $"depends_on:\n  - {dependsOnId}", StringComparison.Ordinal)
            : $"{frontmatter}\ndepends_on:\n  - {dependsOnId}";

        await File.WriteAllTextAsync(fullPath, frontmatter + rest);
    }

    private static TaskMarkdownSyncService CreateMarkdownSyncService(
        InMemoryTaskRepository taskRepo,
        InMemoryTaskDependencyStore depStore,
        DependencyResolutionEngine depEngine)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITaskRepository>(taskRepo);
        services.AddSingleton<ITaskDependencyStore>(depStore);
        services.AddSingleton<IDependencyResolutionEngine>(depEngine);
        var sp = services.BuildServiceProvider();
        return new TaskMarkdownSyncService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            new Mock<IWorkspacePathResolver>().Object,
            NullLogger<TaskMarkdownSyncService>.Instance);
    }

    [Fact]
    public async Task ProcessTaskAsync_Stage1AddsUnclosedDependencyOnDisk_SuspendsResetsStateClearsArtifactsAndCleansUpInOrder()
    {
        var taskRepo = new InMemoryTaskRepository();
        var workspaceRepo = new InMemoryWorkspaceRepository();
        workspaceRepo.SeedGlobalProfiles(AgentProfile.CreateStandardGlobalProfiles("test-model"));
        var depStore = new InMemoryTaskDependencyStore();
        var depEngine = new DependencyResolutionEngine(depStore);

        var workspace = await workspaceRepo.CreateAsync(new Workspace
        {
            Id = 1,
            Name = "Suspension WS",
            RepositoryPath = _tempWorkspaceDir,
            DefaultBaseBranch = "main",
            ExecutionMode = ExecutionMode.ContainerSandbox
        });

        var prereq = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 1,
            WorkspaceId = 1,
            Title = "Prerequisite API",
            Status = TaskOverallStatus.Open,
            BaseBranch = "main"
        });
        depStore.AddTask(prereq.Id, 1, TaskOverallStatus.Open);

        var initialBag = new PipelineDataBag();
        initialBag.Set(PipelineDataBagKeys.BuildCommand, "dotnet build --no-restore");
        initialBag.Set(PipelineDataBagKeys.TestCommand, "dotnet test --filter Category!=E2E");
        initialBag.Set(PipelineDataBagKeys.ApprovedPlan, "Pre-existing stale plan");
        initialBag.Set(PipelineDataBagKeys.ReviewerFeedback, "Pre-existing feedback");

        var pendingQuestion = new Question
        {
            TaskId = 2,
            StageId = StandardStageIds.TaskReview,
            Prompt = "Stale question from earlier run?",
            Status = QuestionStatus.Pending,
            BatchId = "batch-1"
        };

        var task = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 2,
            WorkspaceId = 1,
            Title = "Dependent Feature",
            Description = "Needs prereq #1",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.TaskReview,
            BaseBranch = "main",
            LastHangfireJobId = "job-123",
            MaxExecutedStageOrder = 1,
            ExternalSource = GitHubSyncConstants.ExternalSourceGitHub,
            ExternalReferenceId = "42",
            PipelineDataJson = initialBag.ToJson(),
            Version = 1,
            CreatedAt = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc),
            Questions = new List<Question> { pendingQuestion }
        });
        depStore.AddTask(task.Id, 1, TaskOverallStatus.Open);

        await taskRepo.AddCheckpointAsync(new StageCheckpoint
        {
            TaskId = 2,
            StageId = StandardStageIds.TaskReview,
            SerializedMemory = "{\"turns\":[\"old-memory\"]}",
            GitCommitHash = "abc123"
        });
        var conv = await taskRepo.StartConversationAsync(
            2,
            StandardStageIds.TaskReview,
            WorkflowStageKind.Agentic,
            attemptIndex: 1,
            agentProfileId: StandardStageIds.TaskReview,
            harnessId: StandardHarnessIds.Crucible,
            modelName: "test-model");

        var cleanupOrder = new List<string>();
        var gitManager = new TrackingGitWorkspaceManager(_tempWorktreeDir, cleanupOrder);
        var syncService = CreateMarkdownSyncService(taskRepo, depStore, depEngine);
        var mirrorSync = new RecordingMirrorSyncService();
        var jobQueue = new FakeTaskJobQueue();
        var broadcastEvents = new List<AgentActivityEvent>();
        var streamerMock = new Mock<IAgentActivityStreamer>();
        streamerMock
            .Setup(s => s.BroadcastAsync(It.IsAny<AgentActivityEvent>(), It.IsAny<CancellationToken>()))
            .Callback<AgentActivityEvent, CancellationToken>((evt, _) => broadcastEvents.Add(evt))
            .Returns(Task.CompletedTask);

        var harness = new ActionAgentHarness(async ctx =>
        {
            var relPath = ctx.PipelineContext!.DataBag.Get<string>(PipelineDataBagKeys.TaskMarkdownPath)!;
            var fullPath = Path.Combine(ctx.PipelineContext.WorkingDirectory!, relPath);
            await AddDependencyToMarkdownFileAsync(fullPath, 1);
            return AgentRunResult.Completed(
                new AgentRunMetrics(25, 50, 25, 75),
                summary: "Discovered prerequisite #1");
        });

        var stage2Executed = false;
        var pipeline = new WorkflowPipeline(new IWorkflowStage[]
        {
            TestWorkflowPipelines.CreateReviewStage(),
            new TestWorkflowStage(StandardStageIds.Implementation, WorkflowStageKind.Deterministic)
            {
                QueueTier = StageQueueTier.Implementation,
                Handler = (_, _) =>
                {
                    stage2Executed = true;
                    return Task.FromResult(StageExecutionResult.Success());
                }
            }
        });

        var builder = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = workspaceRepo,
            DependencyStore = depStore,
            DependencyEngine = depEngine,
            GitWorkspaceManager = gitManager,
            GitMirrorSyncService = mirrorSync,
            MarkdownSyncService = syncService,
            JobQueue = jobQueue,
            Streamer = streamerMock.Object,
            HarnessRegistry = new SingleHarnessRegistry(harness),
            PipelineProvider = new DefaultWorkflowPipelineProvider(pipeline)
        };
        builder.GitHubTaskMirrorMock
            .Setup(m => m.TryClaimIssueForExecutionAsync(It.IsAny<ForgeTask>(), It.IsAny<Workspace>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        builder.EnvironmentProviderMock
            .Setup(e => e.CleanupTaskContainerAsync(It.IsAny<Workspace>(), 2, It.IsAny<CancellationToken>()))
            .Callback(() => cleanupOrder.Add("cleanup-container"))
            .Returns(Task.CompletedTask);

        var orchestrator = builder.Build();

        // Act
        await orchestrator.ProcessTaskAsync(2);

        // Assert task reset
        var reloaded = await taskRepo.GetByIdAsync(2);
        Assert.NotNull(reloaded);
        Assert.Equal(TaskOverallStatus.Open, reloaded.Status);
        Assert.Equal(string.Empty, reloaded.CurrentStageId);
        Assert.False(reloaded.HasExecutionRun);
        Assert.Equal(0, reloaded.MaxExecutedStageOrder);
        Assert.Null(reloaded.LastHangfireJobId);
        Assert.False(reloaded.IsWaitingForHuman);
        Assert.Null(reloaded.InterruptedStageId);
        Assert.Null(reloaded.InterruptedAgentProfileId);

        // Assert DataBag preserves config overrides while clearing Stage 1 artifacts
        var resultingBag = PipelineDataBag.FromJson(reloaded.PipelineDataJson);
        Assert.Equal("dotnet build --no-restore", resultingBag.Get<string>(PipelineDataBagKeys.BuildCommand));
        Assert.Equal("dotnet test --filter Category!=E2E", resultingBag.Get<string>(PipelineDataBagKeys.TestCommand));
        Assert.Null(resultingBag.Get<string>(PipelineDataBagKeys.ApprovedPlan));
        Assert.Null(resultingBag.Get<string>(PipelineDataBagKeys.ReviewerFeedback));
        Assert.Null(resultingBag.Get<string>(PipelineDataBagKeys.TaskMarkdownPath));

        // Assert dangling questions, checkpoints, conversations, and store state
        var question = Assert.Single(reloaded.Questions);
        Assert.Equal(QuestionStatus.Skipped, question.Status);
        Assert.Null(question.Answer);

        var checkpoint = Assert.Single(reloaded.Checkpoints);
        Assert.Equal(string.Empty, checkpoint.SerializedMemory);

        var conversations = await taskRepo.GetConversationsAsync(2);
        var updatedConv = conversations.Single(c => c.Id == conv.Id);
        Assert.Equal(StageExecutionStatus.Cancelled, updatedConv.Status);

        var prereqsInStore = await depStore.GetPrerequisiteTaskIdsAsync(2);
        Assert.Equal(new[] { 1 }, prereqsInStore);
        Assert.Equal(TaskOverallStatus.Open, await depStore.GetTaskStatusAsync(2));
        Assert.False(await depStore.IsTaskWaitingForHumanAsync(2));

        // Assert history, GitHub claim reconciliation, SignalR Notice, and teardown ordering
        var history = await taskRepo.GetHistoryAsync(2);
        Assert.Contains(history, h =>
            h.ChangedBy == AuditActors.TaskOrchestrator &&
            h.ChangeReason != null &&
            h.ChangeReason.StartsWith(AgentActivityConstants.SuspendedAuditReasonPrefix, StringComparison.Ordinal));

        builder.GitHubTaskMirrorMock.Verify(
            m => m.MirrorTaskOpenedAsync(It.Is<ForgeTask>(t => t.Id == 2), workspace, It.IsAny<CancellationToken>()),
            Times.Once);

        Assert.Equal(new[] { "cleanup-container", "remove-worktree", "delete-branch" }, cleanupOrder);
        Assert.Equal(new[] { 2 }, gitManager.DeletedBranches);
        Assert.Equal(0, mirrorSync.PushToHostCount);

        var hostTaskFiles = Directory.GetFiles(Path.Combine(_tempWorkspaceDir, "docs", "tasks"), "*.md");
        var hostMarkdown = hostTaskFiles
            .Select(File.ReadAllText)
            .FirstOrDefault(content => content.Contains("id: 2", StringComparison.Ordinal));
        Assert.NotNull(hostMarkdown);
        Assert.Contains("depends_on", hostMarkdown);
        Assert.Contains("- 1", hostMarkdown);

        Assert.False(stage2Executed);
        Assert.Empty(jobQueue.EnqueuedStages);

        var suspendedNotice = Assert.Single(broadcastEvents, e =>
            e.Kind == AgentActivityKind.Notice &&
            e.Metadata != null &&
            e.Metadata.TryGetValue(AgentActivityMetadataKeys.ActionType, out var action) &&
            Equals(action, AgentActivityConstants.ActionTypeTaskSuspended));
        Assert.Equal(2, suspendedNotice.TaskId);
    }

    [Fact]
    public async Task ProcessTaskAsync_Stage1PendingProposalsAndClarificationWithoutDiskEdit_SuspendsBeforeMissingDocAndQuestionCreation()
    {
        var taskRepo = new InMemoryTaskRepository();
        var workspaceRepo = new InMemoryWorkspaceRepository();
        workspaceRepo.SeedGlobalProfiles(AgentProfile.CreateStandardGlobalProfiles("test-model"));
        var depStore = new InMemoryTaskDependencyStore();
        var depEngine = new DependencyResolutionEngine(depStore);

        await workspaceRepo.CreateAsync(new Workspace
        {
            Id = 1,
            Name = "NoDiskSync WS",
            RepositoryPath = Path.Combine(_tempWorkspaceDir, "missing-repo"),
            DefaultBaseBranch = "main",
            ExecutionMode = ExecutionMode.BareHost
        });

        await taskRepo.CreateAsync(new ForgeTask { Id = 1, WorkspaceId = 1, Title = "Open Blocker", Status = TaskOverallStatus.Open });
        depStore.AddTask(1, 1, TaskOverallStatus.Open);

        var target = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 2,
            WorkspaceId = 1,
            Title = "Blocked Without Disk Edit",
            Description = "Original description",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.TaskReview,
            BaseBranch = "main"
        });
        depStore.AddTask(2, 1, TaskOverallStatus.Open);
        depStore.AddDependency(1, taskId: 2, dependsOnTaskId: 1);

        var harness = new ActionAgentHarness(async ctx =>
        {
            await depStore.SetTaskDependenciesAsync(1, 2, new[] { 1 });

            ctx.PipelineContext!.DataBag.Set(PipelineDataBagKeys.PendingProposals, new List<TaskProposal>
            {
                new("Proposed Upstream Prep", "Should be registered in dependency store")
            });

            return AgentRunResult.WaitingForHuman(
                new[]
                {
                    new Question
                    {
                        StageId = StandardStageIds.TaskReview,
                        Prompt = "This new clarification must not be persisted because task suspends first"
                    }
                },
                new AgentRunMetrics(10, 20, 10, 30),
                updatedState: "{\"checkpoint\":\"must-not-be-saved\"}",
                summary: "Clarification requested");
        });

        var pipeline = new WorkflowPipeline(new IWorkflowStage[]
        {
            TestWorkflowPipelines.CreateReviewStage(),
            TestWorkflowPipelines.CreateImplementationStage()
        });

        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = workspaceRepo,
            DependencyStore = depStore,
            DependencyEngine = depEngine,
            MarkdownSyncService = CreateMarkdownSyncService(taskRepo, depStore, depEngine),
            HarnessRegistry = new SingleHarnessRegistry(harness),
            PipelineProvider = new DefaultWorkflowPipelineProvider(pipeline)
        }.Build();

        // Start cleanly without blocking edges in store, then let Stage 1 harness introduce the dependency edge
        await depStore.SetTaskDependenciesAsync(1, 2, Array.Empty<int>());

        await orchestrator.ProcessTaskAsync(2);

        // Proposed task was persisted and registered in ITaskDependencyStore
        var allTasks = await taskRepo.GetByWorkspaceIdAsync(1);
        var proposedTask = Assert.Single(allTasks, t => t.Title == "Proposed Upstream Prep");
        Assert.Equal(TaskOverallStatus.Suggested, proposedTask.Status);
        Assert.Equal(TaskOverallStatus.Suggested, await depStore.GetTaskStatusAsync(proposedTask.Id));

        // Suspended task did not fail on missing doc and did not persist WaitingForHuman questions or Stage 1 checkpoint
        var reloaded = await taskRepo.GetByIdAsync(2);
        Assert.NotNull(reloaded);
        Assert.Equal(TaskOverallStatus.Open, reloaded.Status);
        Assert.False(reloaded.IsWaitingForHuman);
        Assert.Empty(reloaded.Questions);
        Assert.Empty(reloaded.Checkpoints);
    }

    [Fact]
    public async Task GetUnclosedPrerequisiteIdsAsync_WhenDependsOnTaskNavPropIsNullButRepoTaskIsClosed_ReturnsEmpty()
    {
        var taskRepo = new InMemoryTaskRepository();
        var depStore = new InMemoryTaskDependencyStore();
        await taskRepo.CreateAsync(new ForgeTask { Id = 1, WorkspaceId = 1, Title = "Closed Prereq", Status = TaskOverallStatus.Closed });
        await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 2,
            WorkspaceId = 1,
            Title = "Open Dependent",
            Status = TaskOverallStatus.Open,
            Dependencies = new List<TaskDependency>
            {
                new() { TaskId = 2, DependsOnTaskId = 1, DependsOnTask = null }
            }
        });

        var coordinator = new TaskMergeAndCascadeCoordinator(
            taskRepo,
            new Mock<ITaskWorkspacePreparer>().Object,
            new DependencyResolutionEngine(depStore),
            depStore,
            new FakeTaskJobQueue(),
            new DefaultWorkflowPipelineProvider(),
            new Mock<IConflictResolutionVerifier>().Object,
            new Mock<IAgentActivityStreamer>().Object,
            new Mock<IOperationsTelemetryService>().Object,
            NullLogger<TaskMergeAndCascadeCoordinator>.Instance);

        var unclosedWhenClosed = await coordinator.GetUnclosedPrerequisiteIdsAsync(2, new[] { 1 });
        Assert.Empty(unclosedWhenClosed);

        await taskRepo.CreateAsync(new ForgeTask { Id = 3, WorkspaceId = 1, Title = "Open Prereq", Status = TaskOverallStatus.Open });
        var unclosedWhenOpen = await coordinator.GetUnclosedPrerequisiteIdsAsync(2, new[] { 1, 3 });
        Assert.Equal(new[] { 3 }, unclosedWhenOpen);
    }

    [Fact]
    public async Task ProcessTaskAsync_AntiLostWakeup_WhenPrerequisiteClosesDuringCleanup_ReEnqueuesTask()
    {
        var taskRepo = new InMemoryTaskRepository();
        var workspaceRepo = new InMemoryWorkspaceRepository();
        workspaceRepo.SeedGlobalProfiles(AgentProfile.CreateStandardGlobalProfiles("test-model"));
        var depStore = new InMemoryTaskDependencyStore();
        var depEngine = new DependencyResolutionEngine(depStore);

        await workspaceRepo.CreateAsync(new Workspace
        {
            Id = 1,
            Name = "AntiLostWakeup WS",
            RepositoryPath = _tempWorkspaceDir,
            DefaultBaseBranch = "main",
            ExecutionMode = ExecutionMode.BareHost
        });

        var prereq = await taskRepo.CreateAsync(new ForgeTask { Id = 1, WorkspaceId = 1, Title = "Prereq", Status = TaskOverallStatus.Open });
        depStore.AddTask(1, 1, TaskOverallStatus.Open);

        await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 2,
            WorkspaceId = 1,
            Title = "Dependent",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.TaskReview,
            BaseBranch = "main"
        });
        depStore.AddTask(2, 1, TaskOverallStatus.Open);

        var syncService = CreateMarkdownSyncService(taskRepo, depStore, depEngine);
        var jobQueue = new FakeTaskJobQueue();
        var cleanupOrder = new List<string>();
        var gitManager = new TrackingGitWorkspaceManager(
            _tempWorktreeDir,
            cleanupOrder,
            onDeleteTaskBranch: async () =>
            {
                // Simulate prerequisite #1 finishing and closing mid-cleanup
                prereq.Status = TaskOverallStatus.Closed;
                await taskRepo.UpdateAsync(prereq);
                await depStore.UpdateTaskStatusAsync(1, TaskOverallStatus.Closed);
            });

        var harness = new ActionAgentHarness(async ctx =>
        {
            var relPath = ctx.PipelineContext!.DataBag.Get<string>(PipelineDataBagKeys.TaskMarkdownPath)!;
            var fullPath = Path.Combine(ctx.PipelineContext.WorkingDirectory!, relPath);
            await AddDependencyToMarkdownFileAsync(fullPath, 1);
            return AgentRunResult.Completed(new AgentRunMetrics(10, 10, 10, 20), summary: "Added dep on #1");
        });

        var pipeline = new WorkflowPipeline(new IWorkflowStage[]
        {
            TestWorkflowPipelines.CreateReviewStage(),
            TestWorkflowPipelines.CreateImplementationStage()
        });

        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = workspaceRepo,
            DependencyStore = depStore,
            DependencyEngine = depEngine,
            GitWorkspaceManager = gitManager,
            MarkdownSyncService = syncService,
            JobQueue = jobQueue,
            HarnessRegistry = new SingleHarnessRegistry(harness),
            PipelineProvider = new DefaultWorkflowPipelineProvider(pipeline)
        }.Build();

        await orchestrator.ProcessTaskAsync(2);

        var enqueued = Assert.Single(jobQueue.EnqueuedStages);
        Assert.Equal(2, enqueued.TaskId);
        Assert.Equal(StageQueueTier.TaskReview, enqueued.Tier);

        var reloaded = await taskRepo.GetByIdAsync(2);
        Assert.NotNull(reloaded);
        Assert.False(string.IsNullOrWhiteSpace(reloaded.LastHangfireJobId));
        Assert.Equal(string.Empty, reloaded.CurrentStageId);
    }

    private class RecordingTaskRepoProxy : System.Reflection.DispatchProxy
    {
        private ITaskRepository _inner = null!;
        private List<(string Method, int? TaskId)> _calls = null!;

        public static ITaskRepository Create(ITaskRepository inner, List<(string Method, int? TaskId)> calls)
        {
            var proxy = Create<ITaskRepository, RecordingTaskRepoProxy>() as RecordingTaskRepoProxy
                ?? throw new InvalidOperationException("Failed to create repository proxy.");
            proxy._inner = inner;
            proxy._calls = calls;
            return (ITaskRepository)(object)proxy;
        }

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            int? taskId = args?.FirstOrDefault() switch
            {
                int id => id,
                ForgeTask ft => ft.Id,
                _ => null
            };
            _calls.Add((targetMethod.Name, taskId));
            return targetMethod.Invoke(_inner, args);
        }
    }

    [Fact]
    public async Task CascadeUnblockDependentTasksAsync_UsesTargetedSchedulingUpdate_WithoutFullEntityOverwrite()
    {
        var innerRepo = new InMemoryTaskRepository();
        await innerRepo.CreateAsync(new ForgeTask { Id = 1, WorkspaceId = 1, Title = "Prereq", Status = TaskOverallStatus.Ready, BaseBranch = "main" });
        await innerRepo.CreateAsync(new ForgeTask
        {
            Id = 2,
            WorkspaceId = 1,
            Title = "Suspended Dependent",
            Status = TaskOverallStatus.Open,
            CurrentStageId = string.Empty,
            HasExecutionRun = false,
            LastHangfireJobId = "stale-job",
            IsDeferredByPause = true
        });

        var depStore = new InMemoryTaskDependencyStore();
        depStore.AddTask(1, 1, TaskOverallStatus.Ready);
        depStore.AddTask(2, 1, TaskOverallStatus.Open);
        depStore.AddDependency(1, 2, 1);

        var recordedCalls = new List<(string Method, int? TaskId)>();
        var proxyRepo = RecordingTaskRepoProxy.Create(innerRepo, recordedCalls);

        var preparerMock = new Mock<ITaskWorkspacePreparer>();
        preparerMock.Setup(p => p.AcquireWorkspaceLockAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());
        preparerMock.Setup(p => p.GetWorkspaceAsync(It.IsAny<ForgeTask>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Workspace { Id = 1, Name = "WS", RepositoryPath = _tempWorkspaceDir, DefaultBaseBranch = "main" });
        preparerMock.Setup(p => p.GetBranchShaAsync(It.IsAny<Workspace>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("sha-1");
        preparerMock.Setup(p => p.SquashAndMergeAsync(It.IsAny<Workspace>(), 1, "main", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("merge-sha-1");

        var jobQueue = new FakeTaskJobQueue();
        jobQueue.CompletedJobs.Add("stale-job");
        var pipeline = new WorkflowPipeline(new IWorkflowStage[]
        {
            new TestWorkflowStage(StandardStageIds.TaskReview, WorkflowStageKind.Agentic) { QueueTier = StageQueueTier.TaskReview }
        });

        var coordinator = new TaskMergeAndCascadeCoordinator(
            proxyRepo,
            preparerMock.Object,
            new DependencyResolutionEngine(depStore),
            depStore,
            jobQueue,
            new DefaultWorkflowPipelineProvider(pipeline),
            new Mock<IConflictResolutionVerifier>().Object,
            new Mock<IAgentActivityStreamer>().Object,
            new Mock<IOperationsTelemetryService>().Object,
            NullLogger<TaskMergeAndCascadeCoordinator>.Instance);

        await coordinator.CompleteTaskAndCascadeAsync(1, "feat: merge prereq");

        Assert.Contains(recordedCalls, c => c.Method == nameof(ITaskRepository.UpdateSchedulingStateAsync) && c.TaskId == 2);
        Assert.DoesNotContain(recordedCalls, c => c.Method == nameof(ITaskRepository.UpdateAsync) && c.TaskId == 2);
        Assert.DoesNotContain(recordedCalls, c => c.Method == nameof(ITaskRepository.UpdateWithHistoryAsync) && c.TaskId == 2);

        var reloadedDependent = await innerRepo.GetByIdAsync(2);
        Assert.NotNull(reloadedDependent);
        Assert.False(reloadedDependent.IsDeferredByPause);
        Assert.False(string.IsNullOrWhiteSpace(reloadedDependent.LastHangfireJobId));
        Assert.Equal("Suspended Dependent", reloadedDependent.Title);
    }

    [Fact]
    public async Task StartupRecoveryService_ReconcileWorkspacesAsync_SkipsBlockedOpenTasksAndClearsStaleJobId()
    {
        var taskRepo = new InMemoryTaskRepository();
        var workspaceRepo = new InMemoryWorkspaceRepository();
        await workspaceRepo.CreateAsync(new Workspace { Id = 1, Name = "Recovery WS", RepositoryPath = _tempWorkspaceDir });

        var depStore = new InMemoryTaskDependencyStore();
        await taskRepo.CreateAsync(new ForgeTask { Id = 1, WorkspaceId = 1, Title = "Unclosed Blocker", Status = TaskOverallStatus.Open });
        await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 2,
            WorkspaceId = 1,
            Title = "Blocked Open Task",
            Status = TaskOverallStatus.Open,
            CurrentStageId = string.Empty,
            LastHangfireJobId = "stale-99",
            Dependencies = new List<TaskDependency>
            {
                new() { TaskId = 2, DependsOnTaskId = 1, DependsOnTask = null }
            }
        });
        await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 3,
            WorkspaceId = 1,
            Title = "Ready Open Task",
            Status = TaskOverallStatus.Open,
            CurrentStageId = string.Empty
        });

        var jobQueue = new FakeTaskJobQueue();
        var opsMock = new Mock<IOperationsControlService>();
        opsMock.Setup(o => o.IsPaused).Returns(false);

        var pipeline = new WorkflowPipeline(new IWorkflowStage[]
        {
            new TestWorkflowStage(StandardStageIds.TaskReview, WorkflowStageKind.Agentic) { QueueTier = StageQueueTier.TaskReview }
        });

        var gitRecovery = new WorkspaceGitRecoveryService(
            new FakeGitCliRunner(),
            new FakeGitWorkspaceManager(),
            new Mock<IWorkspacePathResolver>().Object,
            new Mock<IProcessRunner>().Object,
            taskRepo,
            NullLogger<WorkspaceGitRecoveryService>.Instance);

        var recovery = new StartupRecoveryService(
            workspaceRepo,
            taskRepo,
            gitRecovery,
            new DefaultWorkflowPipelineProvider(pipeline),
            jobQueue,
            new SingleExecutionEnvironmentProvider(new Mock<IExecutionEnvironment>().Object),
            opsMock.Object,
            NullLogger<StartupRecoveryService>.Instance);

        await recovery.ReconcileWorkspacesAsync();

        var blocked = await taskRepo.GetByIdAsync(2);
        Assert.NotNull(blocked);
        Assert.Null(blocked.LastHangfireJobId);
        Assert.Equal(string.Empty, blocked.CurrentStageId);
        Assert.Contains("stale-99", jobQueue.DeletedJobs);
        Assert.DoesNotContain(jobQueue.EnqueuedStages, s => s.TaskId == 2);

        // Compare behavior by removing unclosed dep and verifying unblocked task behavior independently:
        // Note: blocked task #1 had no deps so both #1 and #3 are enqueued cleanly while #2 is skipped
        Assert.Contains(jobQueue.EnqueuedStages, s => s.TaskId == 3);
    }

    [Fact]
    public async Task CascadeUnblockDependentTasksAsync_SkipsAlreadyInFlightOrActiveJobDependentTasks()
    {
        var taskRepo = new InMemoryTaskRepository();
        await taskRepo.CreateAsync(new ForgeTask { Id = 1, WorkspaceId = 1, Title = "Prereq", Status = TaskOverallStatus.Ready, BaseBranch = "main" });
        await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 2,
            WorkspaceId = 1,
            Title = "In-Flight Dependent",
            Status = TaskOverallStatus.Open,
            HasExecutionRun = true,
            CurrentStageId = StandardStageIds.TaskReview
        });
        await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 3,
            WorkspaceId = 1,
            Title = "Active-Job Dependent",
            Status = TaskOverallStatus.Open,
            HasExecutionRun = false,
            LastHangfireJobId = "active-job-3"
        });
        await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 4,
            WorkspaceId = 1,
            Title = "Ready Suspended Dependent",
            Status = TaskOverallStatus.Open,
            HasExecutionRun = false,
            LastHangfireJobId = "completed-job-4"
        });

        var depStore = new InMemoryTaskDependencyStore();
        depStore.AddTask(1, 1, TaskOverallStatus.Ready);
        depStore.AddTask(2, 1, TaskOverallStatus.Open);
        depStore.AddTask(3, 1, TaskOverallStatus.Open);
        depStore.AddTask(4, 1, TaskOverallStatus.Open);
        depStore.AddDependency(1, 2, 1);
        depStore.AddDependency(1, 3, 1);
        depStore.AddDependency(1, 4, 1);

        var preparerMock = new Mock<ITaskWorkspacePreparer>();
        preparerMock.Setup(p => p.AcquireWorkspaceLockAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());
        preparerMock.Setup(p => p.GetWorkspaceAsync(It.IsAny<ForgeTask>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Workspace { Id = 1, Name = "WS", RepositoryPath = _tempWorkspaceDir, DefaultBaseBranch = "main" });
        preparerMock.Setup(p => p.GetBranchShaAsync(It.IsAny<Workspace>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("sha-1");
        preparerMock.Setup(p => p.SquashAndMergeAsync(It.IsAny<Workspace>(), 1, "main", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("merge-sha-1");

        var jobQueue = new FakeTaskJobQueue();
        jobQueue.CompletedJobs.Add("completed-job-4");
        var pipeline = new WorkflowPipeline(new IWorkflowStage[]
        {
            new TestWorkflowStage(StandardStageIds.TaskReview, WorkflowStageKind.Agentic) { QueueTier = StageQueueTier.TaskReview }
        });

        var coordinator = new TaskMergeAndCascadeCoordinator(
            taskRepo,
            preparerMock.Object,
            new DependencyResolutionEngine(depStore),
            depStore,
            jobQueue,
            new DefaultWorkflowPipelineProvider(pipeline),
            new Mock<IConflictResolutionVerifier>().Object,
            new Mock<IAgentActivityStreamer>().Object,
            new Mock<IOperationsTelemetryService>().Object,
            NullLogger<TaskMergeAndCascadeCoordinator>.Instance);

        await coordinator.CompleteTaskAndCascadeAsync(1, "feat: merge prereq");

        Assert.DoesNotContain(jobQueue.EnqueuedStages, s => s.TaskId == 2);
        Assert.DoesNotContain(jobQueue.EnqueuedStages, s => s.TaskId == 3);
        var enqueued = Assert.Single(jobQueue.EnqueuedStages);
        Assert.Equal(4, enqueued.TaskId);

        var activeTask = await taskRepo.GetByIdAsync(3);
        Assert.Equal("active-job-3", activeTask!.LastHangfireJobId);
    }

    [Theory]
    [InlineData(TaskOverallStatus.Cancelled, false)]
    [InlineData(TaskOverallStatus.Rejected, false)]
    [InlineData(TaskOverallStatus.Open, true)]
    public async Task ProcessTaskAsync_Stage1WithUnclosedDependency_WhenCancelledOrRejectedOutOfBand_DoesNotResurrectToOpen(
        TaskOverallStatus outOfBandStatus,
        bool explicitCancelRequest)
    {
        var taskRepo = new InMemoryTaskRepository();
        var workspaceRepo = new InMemoryWorkspaceRepository();
        workspaceRepo.SeedGlobalProfiles(AgentProfile.CreateStandardGlobalProfiles("test-model"));
        var depStore = new InMemoryTaskDependencyStore();
        var depEngine = new DependencyResolutionEngine(depStore);

        await workspaceRepo.CreateAsync(new Workspace
        {
            Id = 1,
            Name = "OutOfBandTerminal WS",
            RepositoryPath = _tempWorkspaceDir,
            DefaultBaseBranch = "main",
            ExecutionMode = ExecutionMode.ContainerSandbox
        });

        await taskRepo.CreateAsync(new ForgeTask { Id = 1, WorkspaceId = 1, Title = "Prereq", Status = TaskOverallStatus.Open });
        depStore.AddTask(1, 1, TaskOverallStatus.Open);

        var target = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 2,
            WorkspaceId = 1,
            Title = "Target Task",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.TaskReview,
            BaseBranch = "main"
        });
        depStore.AddTask(2, 1, TaskOverallStatus.Open);

        var cleanupOrder = new List<string>();
        var gitManager = new TrackingGitWorkspaceManager(_tempWorktreeDir, cleanupOrder);
        var syncService = CreateMarkdownSyncService(taskRepo, depStore, depEngine);
        var cancellationManager = new TaskCancellationManager(NullLogger<TaskCancellationManager>.Instance);

        var harness = new ActionAgentHarness(async ctx =>
        {
            var relPath = ctx.PipelineContext!.DataBag.Get<string>(PipelineDataBagKeys.TaskMarkdownPath)!;
            var fullPath = Path.Combine(ctx.PipelineContext.WorkingDirectory!, relPath);
            await AddDependencyToMarkdownFileAsync(fullPath, 1);

            if (explicitCancelRequest)
            {
                cancellationManager.Cancel(2);
            }
            else
            {
                target.Status = outOfBandStatus;
                await taskRepo.UpdateWithHistoryAsync(target, null, AuditActors.User, $"Out-of-band {outOfBandStatus}");
            }

            return AgentRunResult.Completed(new AgentRunMetrics(10, 10, 10, 20), summary: "Added dep on #1");
        });

        var pipeline = new WorkflowPipeline(new IWorkflowStage[]
        {
            TestWorkflowPipelines.CreateReviewStage(),
            TestWorkflowPipelines.CreateImplementationStage()
        });

        var builder = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = workspaceRepo,
            DependencyStore = depStore,
            DependencyEngine = depEngine,
            GitWorkspaceManager = gitManager,
            MarkdownSyncService = syncService,
            CancellationManager = cancellationManager,
            HarnessRegistry = new SingleHarnessRegistry(harness),
            PipelineProvider = new DefaultWorkflowPipelineProvider(pipeline)
        };

        await builder.Build().ProcessTaskAsync(2);

        var reloaded = await taskRepo.GetByIdAsync(2);
        Assert.NotNull(reloaded);
        var expectedStatus = explicitCancelRequest ? TaskOverallStatus.Cancelled : outOfBandStatus;
        Assert.Equal(expectedStatus, reloaded.Status);
        Assert.Empty(gitManager.DeletedBranches);
        Assert.Empty(cleanupOrder);

        var history = await taskRepo.GetHistoryAsync(2);
        Assert.DoesNotContain(history, h =>
            h.ChangeReason != null &&
            h.ChangeReason.StartsWith(AgentActivityConstants.SuspendedAuditReasonPrefix, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProcessTaskAsync_Stage1WithUnclosedDependency_WhenPausedConcurrentlyDuringCleanup_CompletesAtomicCleanupWithoutCorruptingReset()
    {
        var taskRepo = new InMemoryTaskRepository();
        var workspaceRepo = new InMemoryWorkspaceRepository();
        workspaceRepo.SeedGlobalProfiles(AgentProfile.CreateStandardGlobalProfiles("test-model"));
        var depStore = new InMemoryTaskDependencyStore();
        var depEngine = new DependencyResolutionEngine(depStore);

        await workspaceRepo.CreateAsync(new Workspace
        {
            Id = 1,
            Name = "ConcurrentPause WS",
            RepositoryPath = _tempWorkspaceDir,
            DefaultBaseBranch = "main",
            ExecutionMode = ExecutionMode.ContainerSandbox
        });

        await taskRepo.CreateAsync(new ForgeTask { Id = 1, WorkspaceId = 1, Title = "Prereq", Status = TaskOverallStatus.Open });
        depStore.AddTask(1, 1, TaskOverallStatus.Open);

        await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 2,
            WorkspaceId = 1,
            Title = "Dependent Task",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.TaskReview,
            BaseBranch = "main"
        });
        depStore.AddTask(2, 1, TaskOverallStatus.Open);

        var cleanupOrder = new List<string>();
        var gitManager = new TrackingGitWorkspaceManager(_tempWorktreeDir, cleanupOrder);
        var syncService = CreateMarkdownSyncService(taskRepo, depStore, depEngine);
        var cancellationManager = new TaskCancellationManager(NullLogger<TaskCancellationManager>.Instance);
        var isPaused = false;
        var opsMock = new Mock<IOperationsControlService>();
        opsMock.Setup(o => o.IsPaused).Returns(() => isPaused);

        var harness = new ActionAgentHarness(async ctx =>
        {
            var relPath = ctx.PipelineContext!.DataBag.Get<string>(PipelineDataBagKeys.TaskMarkdownPath)!;
            var fullPath = Path.Combine(ctx.PipelineContext.WorkingDirectory!, relPath);
            await AddDependencyToMarkdownFileAsync(fullPath, 1);
            return AgentRunResult.Completed(new AgentRunMetrics(10, 10, 10, 20), summary: "Added dep on #1");
        });

        var pipeline = new WorkflowPipeline(new IWorkflowStage[]
        {
            TestWorkflowPipelines.CreateReviewStage(),
            TestWorkflowPipelines.CreateImplementationStage()
        });

        var builder = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = workspaceRepo,
            DependencyStore = depStore,
            DependencyEngine = depEngine,
            GitWorkspaceManager = gitManager,
            MarkdownSyncService = syncService,
            CancellationManager = cancellationManager,
            OperationsControlService = opsMock.Object,
            HarnessRegistry = new SingleHarnessRegistry(harness),
            PipelineProvider = new DefaultWorkflowPipelineProvider(pipeline)
        };
        builder.EnvironmentProviderMock
            .Setup(e => e.CleanupTaskContainerAsync(It.IsAny<Workspace>(), 2, It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                cleanupOrder.Add("cleanup-container");
                isPaused = true;
                cancellationManager.RequestPause(2);
            })
            .Returns(Task.CompletedTask);

        await builder.Build().ProcessTaskAsync(2);

        Assert.Equal(new[] { "cleanup-container", "remove-worktree", "delete-branch" }, cleanupOrder);
        var reloaded = await taskRepo.GetByIdAsync(2);
        Assert.NotNull(reloaded);
        Assert.Equal(TaskOverallStatus.Open, reloaded.Status);
        Assert.Equal(string.Empty, reloaded.CurrentStageId);
        Assert.False(reloaded.HasExecutionRun);
        Assert.Null(reloaded.InterruptedStageId);
        Assert.False(reloaded.IsDeferredByPause);
    }

    [Fact]
    public async Task ProcessTaskAsync_AfterSuspensionAndUnblock_MissingDocCheckDoesNotReusePreSuspensionStage1History()
    {
        var taskRepo = new InMemoryTaskRepository();
        var workspaceRepo = new InMemoryWorkspaceRepository();
        workspaceRepo.SeedGlobalProfiles(AgentProfile.CreateStandardGlobalProfiles("test-model"));
        var depStore = new InMemoryTaskDependencyStore();
        var depEngine = new DependencyResolutionEngine(depStore);

        await workspaceRepo.CreateAsync(new Workspace
        {
            Id = 1,
            Name = "PostSuspendMissingDoc WS",
            RepositoryPath = _tempWorkspaceDir,
            DefaultBaseBranch = "main",
            ExecutionMode = ExecutionMode.BareHost
        });

        var prereq = await taskRepo.CreateAsync(new ForgeTask { Id = 1, WorkspaceId = 1, Title = "Prereq", Status = TaskOverallStatus.Open });
        depStore.AddTask(1, 1, TaskOverallStatus.Open);

        var initialBag = new PipelineDataBag();
        initialBag.Set(PipelineDataBagKeys.MaxRetries, 1);

        var dependent = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 2,
            WorkspaceId = 1,
            Title = "Dependent Task",
            Description = "Needs prereq #1",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.TaskReview,
            BaseBranch = "main",
            PipelineDataJson = initialBag.ToJson()
        });
        depStore.AddTask(2, 1, TaskOverallStatus.Open);

        var syncService = CreateMarkdownSyncService(taskRepo, depStore, depEngine);
        var cleanupOrder = new List<string>();
        var gitManager = new TrackingGitWorkspaceManager(_tempWorktreeDir, cleanupOrder);

        var harnessCalls = 0;
        var harness = new ActionAgentHarness(async ctx =>
        {
            harnessCalls++;
            if (harnessCalls == 1)
            {
                // Run 1 (pre-suspension): edit markdown on disk to add dependency on unclosed #1 -> creates Agent:task-review history then suspends
                var relPath = ctx.PipelineContext!.DataBag.Get<string>(PipelineDataBagKeys.TaskMarkdownPath)!;
                var fullPath = Path.Combine(ctx.PipelineContext.WorkingDirectory!, relPath);
                await AddDependencyToMarkdownFileAsync(fullPath, 1);
                return AgentRunResult.Completed(new AgentRunMetrics(10, 10, 10, 20), summary: "Blocked on #1");
            }

            if (harnessCalls == 2)
            {
                // Run 2 (post-unblock): asks clarification WITHOUT updating markdown
                return AgentRunResult.WaitingForHuman(
                    new[] { new Question { Prompt = "Clarify API contract?" } },
                    new AgentRunMetrics(10, 10, 10, 20),
                    updatedState: "{\"turns\":[\"asked\"]}",
                    summary: "Need clarification");
            }

            // Run 3+ (resumed post-unblock and remediation attempt): returns Completed WITHOUT editing markdown
            return AgentRunResult.Completed(new AgentRunMetrics(10, 10, 10, 20), summary: "Completed without doc edit");
        });

        var stage2Executed = false;
        var pipeline = new WorkflowPipeline(new IWorkflowStage[]
        {
            TestWorkflowPipelines.CreateReviewStage(),
            new TestWorkflowStage(StandardStageIds.Implementation, WorkflowStageKind.Deterministic)
            {
                QueueTier = StageQueueTier.Implementation,
                Handler = (_, _) =>
                {
                    stage2Executed = true;
                    return Task.FromResult(StageExecutionResult.Success());
                }
            }
        });

        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = workspaceRepo,
            DependencyStore = depStore,
            DependencyEngine = depEngine,
            GitWorkspaceManager = gitManager,
            MarkdownSyncService = syncService,
            HarnessRegistry = new SingleHarnessRegistry(harness),
            PipelineProvider = new DefaultWorkflowPipelineProvider(pipeline)
        }.Build();

        // 1. Pre-suspension run -> suspends on unclosed #1
        await orchestrator.ProcessTaskAsync(2);
        var suspended = await taskRepo.GetByIdAsync(2);
        Assert.Equal(TaskOverallStatus.Open, suspended!.Status);
        Assert.Equal(string.Empty, suspended.CurrentStageId);

        // 2. Unblock prerequisite #1
        prereq.Status = TaskOverallStatus.Closed;
        await taskRepo.UpdateAsync(prereq);
        await depStore.UpdateTaskStatusAsync(1, TaskOverallStatus.Closed);

        // 3. Post-unblock run -> asks clarification without modifying spec
        await orchestrator.ProcessTaskAsync(2);
        var waiting = await taskRepo.GetByIdAsync(2);
        Assert.True(waiting!.IsWaitingForHuman);
        var question = Assert.Single(waiting.Questions);

        // 4. Answer clarification and resume -> must trigger missing-doc remediation (must NOT reuse pre-suspension Agent:task-review history)
        await orchestrator.SubmitHumanAnswerAsync(question.Id, "Proceed with v2 API");
        await orchestrator.ProcessTaskAsync(2);

        Assert.False(stage2Executed);
        Assert.Equal(4, harnessCalls);
        var finalTask = await taskRepo.GetByIdAsync(2);
        Assert.Equal(TaskOverallStatus.Failed, finalTask!.Status);
    }

    [Fact]
    public async Task StartupRecoveryService_ReconcileWorkspacesAsync_ClearsDeferredByPauseAndInterruptsRunningConversationsOnBlockedOpenTask()
    {
        var taskRepo = new InMemoryTaskRepository();
        var workspaceRepo = new InMemoryWorkspaceRepository();
        await workspaceRepo.CreateAsync(new Workspace { Id = 1, Name = "BlockedPause WS", RepositoryPath = _tempWorkspaceDir });

        await taskRepo.CreateAsync(new ForgeTask { Id = 1, WorkspaceId = 1, Title = "Unclosed Blocker", Status = TaskOverallStatus.Open });
        await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 2,
            WorkspaceId = 1,
            Title = "Blocked Deferred Task",
            Status = TaskOverallStatus.Open,
            CurrentStageId = string.Empty,
            LastHangfireJobId = "stale-deferred-2",
            IsDeferredByPause = true,
            Dependencies = new List<TaskDependency>
            {
                new() { TaskId = 2, DependsOnTaskId = 1, DependsOnTask = null }
            }
        });

        var runningConv = await taskRepo.StartConversationAsync(
            2,
            StandardStageIds.TaskReview,
            WorkflowStageKind.Agentic,
            attemptIndex: 1,
            agentProfileId: StandardStageIds.TaskReview,
            harnessId: StandardHarnessIds.Crucible,
            modelName: "test-model");

        var jobQueue = new FakeTaskJobQueue();
        var opsMock = new Mock<IOperationsControlService>();
        opsMock.Setup(o => o.IsPaused).Returns(false);

        var gitRecovery = new WorkspaceGitRecoveryService(
            new FakeGitCliRunner(),
            new FakeGitWorkspaceManager(),
            new Mock<IWorkspacePathResolver>().Object,
            new Mock<IProcessRunner>().Object,
            taskRepo,
            NullLogger<WorkspaceGitRecoveryService>.Instance);

        var recovery = new StartupRecoveryService(
            workspaceRepo,
            taskRepo,
            gitRecovery,
            new DefaultWorkflowPipelineProvider(new WorkflowPipeline(new IWorkflowStage[]
            {
                new TestWorkflowStage(StandardStageIds.TaskReview, WorkflowStageKind.Agentic) { QueueTier = StageQueueTier.TaskReview }
            })),
            jobQueue,
            new SingleExecutionEnvironmentProvider(new Mock<IExecutionEnvironment>().Object),
            opsMock.Object,
            NullLogger<StartupRecoveryService>.Instance);

        await recovery.ReconcileWorkspacesAsync();

        var blocked = await taskRepo.GetByIdAsync(2);
        Assert.NotNull(blocked);
        Assert.Null(blocked.LastHangfireJobId);
        Assert.False(blocked.IsDeferredByPause);
        Assert.Contains("stale-deferred-2", jobQueue.DeletedJobs);
        Assert.DoesNotContain(jobQueue.EnqueuedStages, s => s.TaskId == 2);

        var convs = await taskRepo.GetConversationsAsync(2);
        var updatedConv = Assert.Single(convs, c => c.Id == runningConv.Id);
        Assert.Equal(StageExecutionStatus.Interrupted, updatedConv.Status);
    }
}
