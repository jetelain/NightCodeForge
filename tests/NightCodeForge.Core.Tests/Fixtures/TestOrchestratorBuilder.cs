namespace NightCodeForge.Core.Tests.Fixtures;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Core.Concurrency;
using NightCodeForge.Core.Dependencies;
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
    public ITaskMarkdownSyncService MarkdownSyncService { get; set; } = new Mock<ITaskMarkdownSyncService>().Object;
    public IWorkspacePathResolver PathResolver { get; set; } = new Mock<IWorkspacePathResolver>().Object;
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
