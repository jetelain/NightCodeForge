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

public class BacklogPrioritySortingAndFilteringTests : BunitContext
{
    private readonly WorkspaceStateService _workspaceState;
    private readonly InMemoryTaskRepository _taskRepository;
    private readonly InMemoryTaskDependencyStore _depStore;
    private readonly DependencyResolutionEngine _dependencyEngine;
    private readonly MockTaskMarkdownSyncService _markdownSync;
    private readonly MockTaskOrchestrator _orchestrator;
    private readonly OrchestratorHubClientConnection _hubConnection;
    private readonly ToastService _toastService;

    public BacklogPrioritySortingAndFilteringTests()
    {
        _workspaceState = new WorkspaceStateService();
        _taskRepository = new InMemoryTaskRepository();
        _depStore = new InMemoryTaskDependencyStore();
        _dependencyEngine = new DependencyResolutionEngine(_depStore);
        _markdownSync = new MockTaskMarkdownSyncService();
        _orchestrator = new MockTaskOrchestrator();
        _hubConnection = new OrchestratorHubClientConnection("/hubs/orchestrator");
        _toastService = TestServices.CreateToastService();

        Services.AddSingleton(_workspaceState);
        Services.AddSingleton<ITaskRepository>(_taskRepository);
        Services.AddSingleton<IWorkflowRepository>(new InMemoryWorkflowRepository());
        Services.AddSingleton<ITaskDependencyStore>(_depStore);
        Services.AddSingleton<IDependencyResolutionEngine>(_dependencyEngine);
        Services.AddSingleton<ITaskMarkdownSyncService>(_markdownSync);
        Services.AddSingleton<ITaskOrchestrator>(_orchestrator);
        Services.AddSingleton<IDevQueueService>(new MockDevQueueService());
        Services.AddSingleton(_hubConnection);
        Services.AddSingleton(_toastService);
    }

    [Fact]
    public async Task Backlog_DefaultOrdering_SortsByPriorityAscendingThenCreatedAt()
    {
        var ws = new Workspace { Id = 1, Name = "Sorting Project", RepositoryPath = "C:\\Repo" };
        _workspaceState.SetActiveWorkspace(ws);

        var baseTime = DateTime.UtcNow;

        var tLow = new ForgeTask
        {
            Id = 1,
            WorkspaceId = 1,
            Title = "Low Priority Task",
            Priority = TaskPriority.Low, // 4
            Status = TaskOverallStatus.Open,
            CreatedAt = baseTime.AddHours(-3)
        };
        var tCritLate = new ForgeTask
        {
            Id = 2,
            WorkspaceId = 1,
            Title = "Critical Task Late",
            Priority = TaskPriority.Critical, // 1
            Status = TaskOverallStatus.Open,
            CreatedAt = baseTime.AddHours(-1)
        };
        var tNorm = new ForgeTask
        {
            Id = 3,
            WorkspaceId = 1,
            Title = "Normal Priority Task",
            Priority = TaskPriority.Normal, // 3
            Status = TaskOverallStatus.Open,
            CreatedAt = baseTime.AddHours(-2)
        };
        var tCritEarly = new ForgeTask
        {
            Id = 4,
            WorkspaceId = 1,
            Title = "Critical Task Early",
            Priority = TaskPriority.Critical, // 1
            Status = TaskOverallStatus.Open,
            CreatedAt = baseTime.AddHours(-4)
        };

        await _taskRepository.CreateAsync(tLow);
        await _taskRepository.CreateAsync(tCritLate);
        await _taskRepository.CreateAsync(tNorm);
        await _taskRepository.CreateAsync(tCritEarly);

        var cut = Render<Backlog>();

        var cards = cut.FindAll(".column-open .kanban-card");
        Assert.Equal(4, cards.Count);

        // Expected order: P1 Early (#4), P1 Late (#2), P3 (#3), P4 (#1)
        Assert.Contains("#4", cards[0].QuerySelector(".card-id")?.TextContent);
        Assert.Contains("#2", cards[1].QuerySelector(".card-id")?.TextContent);
        Assert.Contains("#3", cards[2].QuerySelector(".card-id")?.TextContent);
        Assert.Contains("#1", cards[3].QuerySelector(".card-id")?.TextContent);
    }

    [Fact]
    public async Task Backlog_PriorityFilterChips_FilterTasksAccurately()
    {
        var ws = new Workspace { Id = 2, Name = "Filter Project", RepositoryPath = "C:\\Repo" };
        _workspaceState.SetActiveWorkspace(ws);

        var t1 = new ForgeTask { Id = 10, WorkspaceId = 2, Title = "P1 Item", Priority = TaskPriority.Critical, Status = TaskOverallStatus.Open };
        var t2 = new ForgeTask { Id = 20, WorkspaceId = 2, Title = "P2 Item", Priority = TaskPriority.High, Status = TaskOverallStatus.Open };
        var t3 = new ForgeTask { Id = 30, WorkspaceId = 2, Title = "P3 Item", Priority = TaskPriority.Normal, Status = TaskOverallStatus.Open };
        var t5 = new ForgeTask { Id = 50, WorkspaceId = 2, Title = "P5 Item", Priority = TaskPriority.Trivial, Status = TaskOverallStatus.Open };

        await _taskRepository.CreateAsync(t1);
        await _taskRepository.CreateAsync(t2);
        await _taskRepository.CreateAsync(t3);
        await _taskRepository.CreateAsync(t5);

        var cut = Render<Backlog>();

        // Filter P1 Critical
        var p1FilterBtn = cut.FindAll(".priority-filter-btn").First(b => b.TextContent.Contains("P1 Critical"));
        p1FilterBtn.Click();

        var cardsP1 = cut.FindAll(".kanban-card");
        Assert.Single(cardsP1);
        Assert.Contains("P1 Item", cardsP1[0].TextContent);

        // Filter P1-P2 High+
        var highPlusFilterBtn = cut.FindAll(".priority-filter-btn").First(b => b.TextContent.Contains("P1–P2 High+"));
        highPlusFilterBtn.Click();

        var cardsP1P2 = cut.FindAll(".kanban-card");
        Assert.Equal(2, cardsP1P2.Count);

        // Filter P3 Normal
        var p3FilterBtn = cut.FindAll(".priority-filter-btn").First(b => b.TextContent.Contains("P3 Normal"));
        p3FilterBtn.Click();

        var cardsP3 = cut.FindAll(".kanban-card");
        Assert.Single(cardsP3);
        Assert.Contains("P3 Item", cardsP3[0].TextContent);

        // Filter P4-P5 Low
        var lowFilterBtn = cut.FindAll(".priority-filter-btn").First(b => b.TextContent.Contains("P4–P5 Low"));
        lowFilterBtn.Click();

        var cardsLow = cut.FindAll(".kanban-card");
        Assert.Single(cardsLow);
        Assert.Contains("P5 Item", cardsLow[0].TextContent);

        // Filter All
        var allFilterBtn = cut.FindAll(".priority-filter-btn").First(b => b.TextContent.Trim() == "All");
        allFilterBtn.Click();

        var cardsAll = cut.FindAll(".kanban-card");
        Assert.Equal(4, cardsAll.Count);
    }
}
