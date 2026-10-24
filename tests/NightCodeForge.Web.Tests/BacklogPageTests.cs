using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Web.Components.Pages;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class BacklogPageTests : BunitContext
{
    private readonly WorkspaceStateService _workspaceState;
    private readonly InMemoryTaskRepository _taskRepository;
    private readonly InMemoryTaskDependencyStore _depStore;
    private readonly DependencyResolutionEngine _dependencyEngine;
    private readonly MockTaskMarkdownSyncService _markdownSync;
    private readonly MockTaskOrchestrator _orchestrator;
    private readonly OrchestratorHubClientConnection _hubConnection;
    private readonly ToastService _toastService;

    private readonly MockTaskDiskSyncCoordinator _diskSyncCoordinator;

    public BacklogPageTests()
    {
        _workspaceState = new WorkspaceStateService();
        _taskRepository = new InMemoryTaskRepository();
        _depStore = new InMemoryTaskDependencyStore();
        _dependencyEngine = new DependencyResolutionEngine(_depStore);
        _markdownSync = new MockTaskMarkdownSyncService();
        _orchestrator = new MockTaskOrchestrator();
        _hubConnection = TestHubConnectionHelper.Create();
        _toastService = TestServices.CreateToastService();
        _diskSyncCoordinator = new MockTaskDiskSyncCoordinator();

        Services.AddSingleton(_workspaceState);
        Services.AddSingleton<ITaskRepository>(_taskRepository);
        Services.AddSingleton<IWorkflowRepository>(new InMemoryWorkflowRepository());
        Services.AddSingleton<ITaskDependencyStore>(_depStore);
        Services.AddSingleton<IDependencyResolutionEngine>(_dependencyEngine);
        Services.AddSingleton<ITaskMarkdownSyncService>(_markdownSync);
        Services.AddSingleton<ITaskOrchestrator>(_orchestrator);
        Services.AddSingleton(_hubConnection);
        Services.AddSingleton(_toastService);
        Services.AddSingleton<IDevQueueService>(new MockDevQueueService());
        Services.AddSingleton<ITaskHistoryDiffService, TaskHistoryDiffService>();
        Services.AddSingleton<ITaskDiskSyncCoordinator>(_diskSyncCoordinator);
        Services.AddSingleton<IDagLayoutEngine, DagLayoutEngine>();
        var telemetryMock = new Moq.Mock<NightCodeForge.Core.Interfaces.IOperationsTelemetryService>();
        var workerStateService = new WorkerStateService(telemetryMock.Object);
        Services.AddSingleton(workerStateService);
        Services.AddSingleton(TestServices.CreateOperationsControlService());
        Services.AddSingleton<ITaskMutationService>(new TaskMutationService(
            _taskRepository,
            new InMemoryWorkspaceRepository(),
            _markdownSync,
            _orchestrator,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TaskMutationService>.Instance));
    }

    [Fact]
    public void WhenNoActiveWorkspace_RendersEmptyHero()
    {
        var cut = Render<Backlog>();

        Assert.NotEmpty(cut.FindAll(".workspace-empty-hero"));
        Assert.Contains("Welcome to NightCodeForge", cut.Find(".workspace-empty-hero").TextContent);
    }

    [Fact]
    public async Task WhenWorkspaceActive_RendersHeaderAndTasks()
    {
        var workspace = new Workspace
        {
            Id = 1,
            Name = "Forge Engine",
            RepositoryPath = "C:\\Projects\\ForgeEngine",
            DefaultBaseBranch = "main"
        };
        _workspaceState.SetActiveWorkspace(workspace);

        var task1 = new ForgeTask { Id = 1, WorkspaceId = 1, Title = "Setup Roslyn compiler", Status = TaskOverallStatus.Open };
        var task2 = new ForgeTask { Id = 2, WorkspaceId = 1, Title = "Add DAG visualizer", Status = TaskOverallStatus.Suggested };
        await _taskRepository.CreateAsync(task1);
        await _taskRepository.CreateAsync(task2);

        var cut = Render<Backlog>();

        Assert.Contains("Forge Engine", cut.Find(".backlog-header").TextContent);
        Assert.Contains("Setup Roslyn compiler", cut.Markup);
        Assert.Contains("Add DAG visualizer", cut.Markup);

        // Stats chips
        Assert.Contains("2", cut.Find(".stat-chip.active .stat-chip-val").TextContent);
    }

    [Fact]
    public async Task ViewToggle_SwitchesBetweenKanbanAndDag()
    {
        var workspace = new Workspace { Id = 2, Name = "Telemetry Proj", RepositoryPath = "C:\\Test" };
        _workspaceState.SetActiveWorkspace(workspace);

        var task = new ForgeTask { Id = 10, WorkspaceId = 2, Title = "Telemetry Aggregator", Status = TaskOverallStatus.Open };
        await _taskRepository.CreateAsync(task);

        var cut = Render<Backlog>();

        // Defaults to Kanban Board
        Assert.NotEmpty(cut.FindAll(".kanban-board-container"));
        Assert.Empty(cut.FindAll(".dag-visualizer-container"));

        // Toggle to Visual DAG
        var dagToggleBtn = cut.FindAll(".view-toggle-btn").First(b => b.TextContent.Contains("Visual DAG"));
        dagToggleBtn.Click();

        Assert.Empty(cut.FindAll(".kanban-board-container"));
        Assert.NotEmpty(cut.FindAll(".dag-visualizer-container"));
    }

    [Fact]
    public async Task FilterBySearchInput_FiltersDisplayedTasks()
    {
        var workspace = new Workspace { Id = 3, Name = "Search Test", RepositoryPath = "C:\\Test" };
        _workspaceState.SetActiveWorkspace(workspace);

        var task1 = new ForgeTask { Id = 1, WorkspaceId = 3, Title = "Alpha Service", Status = TaskOverallStatus.Open };
        var task2 = new ForgeTask { Id = 2, WorkspaceId = 3, Title = "Beta Component", Status = TaskOverallStatus.Open };
        await _taskRepository.CreateAsync(task1);
        await _taskRepository.CreateAsync(task2);

        var cut = Render<Backlog>();

        Assert.Contains("Alpha Service", cut.Markup);
        Assert.Contains("Beta Component", cut.Markup);

        // Search for "Beta"
        var searchInput = cut.Find(".backlog-search-input");
        searchInput.Input("Beta");

        Assert.DoesNotContain("Alpha Service", cut.Markup);
        Assert.Contains("Beta Component", cut.Markup);
    }

    [Fact]
    public async Task SignalR_StageTransition_ReloadsBacklogReactivity()
    {
        var workspace = new Workspace { Id = 4, Name = "Live Pipeline", RepositoryPath = "C:\\Live" };
        _workspaceState.SetActiveWorkspace(workspace);

        var task = new ForgeTask { Id = 50, WorkspaceId = 4, Title = "Live Task", Status = TaskOverallStatus.Open };
        await _taskRepository.CreateAsync(task);

        var cut = Render<Backlog>();
        Assert.Contains("Open (Backlog)", cut.Markup);

        // Simulate background execution: task status changed to Reviewed
        task.Status = TaskOverallStatus.Reviewed;
        task.CurrentStageId = "task-review";
        await _taskRepository.UpdateAsync(task);

        // SignalR broadcasts StageTransition event
        _hubConnection.TriggerStageTransition(50, "task-review", "Agentic", "Completed");

        // Wait for reactive reload in UI
        cut.WaitForAssertion(() =>
        {
            var inFlightCol = cut.Find(".column-inflight");
            Assert.Contains("Live Task", inFlightCol.TextContent);
        });
    }

    [Fact]
    public void ImmediateDocsTasksSyncButton_RendersInHeaderWhenWorkspaceActive()
    {
        var workspace = new Workspace { Id = 5, Name = "DocsSync Proj", RepositoryPath = "C:\\Projects\\DocsSync" };
        _workspaceState.SetActiveWorkspace(workspace);

        var cut = Render<Backlog>();

        var syncBtn = cut.Find("#btn-sync-docs-tasks");
        Assert.NotNull(syncBtn);
        Assert.Contains("Sync docs/tasks", syncBtn.TextContent);
    }

    [Fact]
    public async Task ImmediateDocsTasksSyncButton_OnClick_InvokesCoordinatorAndShowsSuccessToast()
    {
        var workspace = new Workspace { Id = 6, Name = "DocsSync Action", RepositoryPath = "C:\\Projects\\DocsSyncAction" };
        _workspaceState.SetActiveWorkspace(workspace);

        _diskSyncCoordinator.ResultToReturn = new TaskDiskSyncResult(
            TasksIngestedOrUpdatedFromDisk: 2,
            TasksWrittenToDisk: 4,
            RefreshedTasks: Array.Empty<ForgeTask>(),
            WrittenPaths: new Dictionary<int, string>());

        var cut = Render<Backlog>();
        var syncBtn = cut.Find("#btn-sync-docs-tasks");

        await cut.InvokeAsync(() => syncBtn.Click());

        Assert.Equal(1, _diskSyncCoordinator.SyncCount);
        Assert.Equal(6, _diskSyncCoordinator.LastSyncedWorkspace?.Id);

        // Verify toast level and text
        Assert.Contains(_toastService.Toasts, t => t.Title == "docs/tasks Synchronized" && t.Level == ToastLevel.Success);
    }

    [Fact]
    public async Task ImmediateDocsTasksSyncButton_WhenSyncFails_ShowsErrorToastAndDoesNotCrash()
    {
        var workspace = new Workspace { Id = 7, Name = "DocsSync Error", RepositoryPath = "C:\\Projects\\DocsSyncError" };
        _workspaceState.SetActiveWorkspace(workspace);

        _diskSyncCoordinator.ExceptionToThrow = new TaskMarkdownSyncException("Syntax error in markdown YAML frontmatter", "task-1.md");

        var cut = Render<Backlog>();
        var syncBtn = cut.Find("#btn-sync-docs-tasks");

        await cut.InvokeAsync(() => syncBtn.Click());

        Assert.Equal(1, _diskSyncCoordinator.SyncCount);
        Assert.Contains(_toastService.Toasts, t => t.Title == "docs/tasks Sync Error" && t.Level == ToastLevel.Error);
    }
}
