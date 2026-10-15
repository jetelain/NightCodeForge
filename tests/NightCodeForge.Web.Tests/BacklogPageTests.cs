using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
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

    public BacklogPageTests()
    {
        _workspaceState = new WorkspaceStateService();
        _taskRepository = new InMemoryTaskRepository();
        _depStore = new InMemoryTaskDependencyStore();
        _dependencyEngine = new DependencyResolutionEngine(_depStore);
        _markdownSync = new MockTaskMarkdownSyncService();
        _orchestrator = new MockTaskOrchestrator();
        _hubConnection = new OrchestratorHubClientConnection("/hubs/orchestrator");
        _toastService = new ToastService();

        Services.AddSingleton(_workspaceState);
        Services.AddSingleton<ITaskRepository>(_taskRepository);
        Services.AddSingleton<ITaskDependencyStore>(_depStore);
        Services.AddSingleton<IDependencyResolutionEngine>(_dependencyEngine);
        Services.AddSingleton<ITaskMarkdownSyncService>(_markdownSync);
        Services.AddSingleton<ITaskOrchestrator>(_orchestrator);
        Services.AddSingleton(_hubConnection);
        Services.AddSingleton(_toastService);
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
}
