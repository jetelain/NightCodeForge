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
    public IGitWorkspaceManager GitWorkspaceManager { get; set; } = new Mock<IGitWorkspaceManager>().Object;
    public IWorkflowPipelineProvider PipelineProvider { get; set; } = new DefaultWorkflowPipelineProvider();
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
    public ILogger<TaskOrchestrator> Logger { get; set; } = NullLogger<TaskOrchestrator>.Instance;

    public TaskOrchestrator Build() => new(
        TaskRepository,
        WorkspaceRepository,
        DependencyEngine,
        LockProvider,
        GitWorkspaceManager,
        PipelineProvider,
        JobQueue,
        DependencyStore,
        HarnessRegistry,
        GitMirrorSyncService,
        MarkdownSyncService,
        PathResolver,
        Streamer,
        CancellationManager,
        Logger);
}
