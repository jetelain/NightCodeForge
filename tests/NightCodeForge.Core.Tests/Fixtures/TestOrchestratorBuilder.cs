namespace NightCodeForge.Core.Tests.Fixtures;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Core.Concurrency;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Streaming;
using NightCodeForge.Core.Workflow;

public class TestOrchestratorBuilder
{
    private ITaskDependencyStore _dependencyStore = new InMemoryTaskDependencyStore();
    private IDependencyResolutionEngine? _dependencyEngine;

    public ITaskRepository TaskRepository { get; set; } = new InMemoryTaskRepository();
    public IWorkspaceRepository WorkspaceRepository { get; set; } = new InMemoryWorkspaceRepository();
    public ITaskDependencyStore DependencyStore
    {
        get => _dependencyStore;
        set
        {
            _dependencyStore = value;
            _dependencyEngine = new DependencyResolutionEngine(value);
        }
    }
    public IDependencyResolutionEngine DependencyEngine
    {
        get => _dependencyEngine ??= new DependencyResolutionEngine(_dependencyStore);
        set => _dependencyEngine = value;
    }
    public IWorkspaceLockProvider LockProvider { get; set; } = new WorkspaceLockProvider();
    public IGitWorkspaceManager GitWorkspaceManager { get; set; } = CreateDefaultGitWorkspaceManager();
    public Mock<IExecutionEnvironmentProvider> EnvironmentProviderMock { get; } = new();
    public IWorkflowPipelineProvider PipelineProvider { get; set; } = CreateDefaultPipelineProvider();

    private static IWorkflowPipelineProvider CreateDefaultPipelineProvider()
    {
        var stages = new IWorkflowStage[]
        {
            new TestWorkflowStage(StandardStageIds.TaskReview, WorkflowStageKind.Agentic) { QueueTier = StageQueueTier.TaskReview },
            new TestWorkflowStage(StandardStageIds.PreImplementationBaseline, WorkflowStageKind.Deterministic) { QueueTier = StageQueueTier.Implementation },
            new TestWorkflowStage(StandardStageIds.Implementation, WorkflowStageKind.Agentic) { QueueTier = StageQueueTier.Implementation },
            new TestWorkflowStage(StandardStageIds.PostImplementationGates, WorkflowStageKind.Deterministic) { QueueTier = StageQueueTier.Implementation },
            new TestWorkflowStage(StandardStageIds.CodeReviewAndFix, WorkflowStageKind.Agentic) { QueueTier = StageQueueTier.CodeReview },
            new TestWorkflowStage(StandardStageIds.PostReviewVerification, WorkflowStageKind.Deterministic) { QueueTier = StageQueueTier.CodeReview }
        };
        return new DefaultWorkflowPipelineProvider(new WorkflowPipeline(stages));
    }

    private static IGitWorkspaceManager CreateDefaultGitWorkspaceManager()
    {
        var mock = new Mock<IGitWorkspaceManager>();
        mock.Setup(m => m.CheckMergeableAsync(It.IsAny<Workspace>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MergeabilityCheckResult(IsMergeable: true, ConflictedFiles: Array.Empty<string>(), BaseBranchSha: "base-sha", FeatureBranchSha: "feat-sha"));
        mock.Setup(m => m.SyncFromBaseBranchAsync(It.IsAny<Workspace>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MergeSyncResult(Success: true, HasConflict: false, ConflictedFiles: Array.Empty<string>(), MergeCommitSha: "base-sha"));
        mock.Setup(m => m.GetBranchShaAsync(It.IsAny<Workspace>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("base-sha");
        return mock.Object;
    }
    public ITaskJobQueue JobQueue { get; set; } = new FakeTaskJobQueue();
    public IHarnessRegistry HarnessRegistry { get; set; } = new HarnessRegistry();
    public IGitMirrorSyncService GitMirrorSyncService { get; set; } = new Mock<IGitMirrorSyncService>().Object;
    public ITaskMarkdownSyncService MarkdownSyncService { get; set; } = CreateDefaultMarkdownSync();

    private static ITaskMarkdownSyncService CreateDefaultMarkdownSync()
    {
        var mock = new Mock<ITaskMarkdownSyncService>();
        mock.Setup(m => m.SyncAllTasksToDiskAsync(
            It.IsAny<Workspace>(),
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SyncAllToDiskResult(new Dictionary<int, string>(), new Dictionary<int, TaskBaselineEntry>()));
        mock.Setup(m => m.SyncAllTasksFromDiskAsync(
            It.IsAny<Workspace>(),
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<IReadOnlyDictionary<int, TaskBaselineEntry>?>(),
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ForgeTask>());
        return mock.Object;
    }

    private static IWorkspacePathResolver CreateDefaultPathResolver()
    {
        var mock = new Mock<IWorkspacePathResolver>();
        mock.Setup(m => m.ToHostPath(It.IsAny<string>())).Returns<string>(p => p);
        return mock.Object;
    }

    public IWorkspacePathResolver PathResolver { get; set; } = CreateDefaultPathResolver();
    public IAgentActivityStreamer Streamer { get; set; } = new InMemoryAgentActivityStreamer();
    public ITaskCancellationManager CancellationManager { get; set; } = new TaskCancellationManager(NullLogger<TaskCancellationManager>.Instance);
    public IConflictResolutionVerifier ConflictResolutionVerifier { get; set; } = new Mock<IConflictResolutionVerifier>().Object;
    public ILogger<TaskOrchestrator> Logger { get; set; } = NullLogger<TaskOrchestrator>.Instance;

    public TaskOrchestrator Build() => new(
        TaskRepository,
        WorkspaceRepository,
        DependencyEngine,
        LockProvider,
        GitWorkspaceManager,
        EnvironmentProviderMock.Object,
        PipelineProvider,
        JobQueue,
        DependencyStore,
        HarnessRegistry,
        GitMirrorSyncService,
        MarkdownSyncService,
        PathResolver,
        Streamer,
        CancellationManager,
        ConflictResolutionVerifier,
        Logger);
}
