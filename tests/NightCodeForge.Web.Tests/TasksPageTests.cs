using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Web.Components.Pages;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class TasksPageTests : BunitContext
{
    private readonly InMemoryWorkspaceRepository _workspaceRepo;
    private readonly InMemoryTaskRepository _taskRepo;
    private readonly WorkspaceStateService _workspaceState;
    private readonly WorkerStateService _workerState;

    public TasksPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        _workspaceRepo = new InMemoryWorkspaceRepository();
        _taskRepo = new InMemoryTaskRepository();
        _workspaceState = new WorkspaceStateService();
        _workerState = new WorkerStateService(new Mock<IOperationsTelemetryService>().Object);

        Services.AddSingleton<IWorkspaceRepository>(_workspaceRepo);
        Services.AddSingleton<ITaskRepository>(_taskRepo);
        Services.AddSingleton(_workspaceState);
        Services.AddSingleton(_workerState);
    }

    [Fact]
    public async Task TasksPage_DisplaysAllTasks_IncludingTasksClosedOlderThan7Days()
    {
        var ws = await _workspaceRepo.CreateAsync(new Workspace
        {
            Name = "Alpha Repo",
            RepositoryPath = "/repos/alpha",
            CreatedAt = DateTime.UtcNow.AddDays(-30)
        });
        _workspaceState.TryActivateWorkspaceById(ws.Id);

        var now = DateTime.UtcNow;

        var openTask = await _taskRepo.CreateAsync(new ForgeTask
        {
            WorkspaceId = ws.Id,
            Title = "Active Open Task",
            Status = TaskOverallStatus.Open,
            Priority = TaskPriority.Critical,
            UpdatedAt = now
        });

        // Task closed 15 days ago (excluded from Backlog, but must be in All Tasks page)
        var oldClosedTask = await _taskRepo.CreateAsync(new ForgeTask
        {
            WorkspaceId = ws.Id,
            Title = "Historical Ancient Task",
            Status = TaskOverallStatus.Closed,
            Priority = TaskPriority.Normal,
            UpdatedAt = now.AddDays(-15)
        });

        var cut = Render<Tasks>(parameters => parameters.Add(p => p.WorkspaceId, ws.Id));

        cut.WaitForState(() => cut.Markup.Contains("Active Open Task"));

        Assert.Contains("Active Open Task", cut.Markup);
        Assert.Contains("Historical Ancient Task", cut.Markup);

        var rows = cut.FindAll("tbody tr");
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public async Task TasksPage_SearchFilter_FiltersRows()
    {
        var ws = await _workspaceRepo.CreateAsync(new Workspace
        {
            Name = "Beta Repo",
            RepositoryPath = "/repos/beta"
        });
        _workspaceState.TryActivateWorkspaceById(ws.Id);

        await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 10,
            WorkspaceId = ws.Id,
            Title = "Feature Authentication",
            Description = "JWT and OAuth2",
            Status = TaskOverallStatus.Open
        });

        await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 20,
            WorkspaceId = ws.Id,
            Title = "Fix Database Deadlock",
            Description = "Concurrency locks",
            Status = TaskOverallStatus.Open
        });

        var cut = Render<Tasks>(parameters => parameters.Add(p => p.WorkspaceId, ws.Id));
        cut.WaitForState(() => cut.FindAll("tbody tr").Count == 2);

        var searchInput = cut.Find("#tasks-search-input");
        searchInput.Input("Authentication");

        cut.WaitForState(() => cut.FindAll("tbody tr").Count == 1);
        Assert.Contains("Feature Authentication", cut.Markup);
        Assert.DoesNotContain("Fix Database Deadlock", cut.Markup);
    }

    [Fact]
    public async Task TasksPage_StatusFilter_FiltersByStatus()
    {
        var ws = await _workspaceRepo.CreateAsync(new Workspace
        {
            Name = "Gamma Repo",
            RepositoryPath = "/repos/gamma"
        });
        _workspaceState.TryActivateWorkspaceById(ws.Id);

        await _taskRepo.CreateAsync(new ForgeTask
        {
            WorkspaceId = ws.Id,
            Title = "Failed Task 1",
            Status = TaskOverallStatus.Failed
        });

        await _taskRepo.CreateAsync(new ForgeTask
        {
            WorkspaceId = ws.Id,
            Title = "Closed Task 2",
            Status = TaskOverallStatus.Closed
        });

        var cut = Render<Tasks>(parameters => parameters.Add(p => p.WorkspaceId, ws.Id));
        cut.WaitForState(() => cut.FindAll("tbody tr").Count == 2);

        var statusSelect = cut.Find("#filter-status-select");
        statusSelect.Change(TaskStatusFilter.Failed.ToString());

        cut.WaitForState(() => cut.FindAll("tbody tr").Count == 1);
        Assert.Contains("Failed Task 1", cut.Markup);
        Assert.DoesNotContain("Closed Task 2", cut.Markup);
    }

    [Fact]
    public async Task TasksPage_ColumnSorting_TogglesSortOrder()
    {
        var ws = await _workspaceRepo.CreateAsync(new Workspace
        {
            Name = "Delta Repo",
            RepositoryPath = "/repos/delta"
        });
        _workspaceState.TryActivateWorkspaceById(ws.Id);

        await _taskRepo.CreateAsync(new ForgeTask { Id = 5, WorkspaceId = ws.Id, Title = "Task 5", Priority = 3 });
        await _taskRepo.CreateAsync(new ForgeTask { Id = 1, WorkspaceId = ws.Id, Title = "Task 1", Priority = 1 });
        await _taskRepo.CreateAsync(new ForgeTask { Id = 9, WorkspaceId = ws.Id, Title = "Task 9", Priority = 2 });

        var cut = Render<Tasks>(parameters => parameters.Add(p => p.WorkspaceId, ws.Id));
        cut.WaitForState(() => cut.FindAll("tbody tr").Count == 3);

        // Default sort is Id DESC -> 9, 5, 1
        var rowsDesc = cut.FindAll("tbody tr");
        Assert.Equal("task-row-9", rowsDesc[0].Id);
        Assert.Equal("task-row-5", rowsDesc[1].Id);
        Assert.Equal("task-row-1", rowsDesc[2].Id);

        // Click #ID header to toggle ASC -> 1, 5, 9
        var idHeader = cut.Find("#sort-col-id");
        idHeader.Click();

        var rowsAsc = cut.FindAll("tbody tr");
        Assert.Equal("task-row-1", rowsAsc[0].Id);
        Assert.Equal("task-row-5", rowsAsc[1].Id);
        Assert.Equal("task-row-9", rowsAsc[2].Id);

        // Click Priority header -> P1 (1), P2 (9), P3 (5)
        var priorityHeader = cut.Find("#sort-col-priority");
        priorityHeader.Click();

        var rowsPriority = cut.FindAll("tbody tr");
        Assert.Equal("task-row-1", rowsPriority[0].Id);
        Assert.Equal("task-row-9", rowsPriority[1].Id);
        Assert.Equal("task-row-5", rowsPriority[2].Id);

        // Click Created header
        var createdHeader = cut.Find("#sort-col-created");
        createdHeader.Click();
        Assert.Contains("Created", cut.Markup);
    }

    [Fact]
    public async Task TasksPage_ScopesToRouteWorkspace_AndCanToggleAllWorkspaces()
    {
        var ws1 = await _workspaceRepo.CreateAsync(new Workspace
        {
            Name = "Workspace One",
            RepositoryPath = "/repos/one"
        });
        var ws2 = await _workspaceRepo.CreateAsync(new Workspace
        {
            Name = "Workspace Two",
            RepositoryPath = "/repos/two"
        });

        await _taskRepo.CreateAsync(new ForgeTask { Id = 101, WorkspaceId = ws1.Id, Title = "Ws1 Task", Status = TaskOverallStatus.Open });
        await _taskRepo.CreateAsync(new ForgeTask { Id = 202, WorkspaceId = ws2.Id, Title = "Ws2 Task", Status = TaskOverallStatus.Open });

        // Render scoped to ws1 even when WorkspaceState had no prior KnownWorkspaces
        var cut = Render<Tasks>(parameters => parameters.Add(p => p.WorkspaceId, ws1.Id));
        cut.WaitForState(() => cut.Markup.Contains("Ws1 Task"));

        Assert.Single(cut.FindAll("tbody tr"));
        Assert.Contains("Ws1 Task", cut.Markup);
        Assert.DoesNotContain("Ws2 Task", cut.Markup);

        // Toggle to All Workspaces
        cut.Find("#toggle-scope-all").Click();
        cut.WaitForState(() => cut.FindAll("tbody tr").Count == 2);

        Assert.Contains("Ws1 Task", cut.Markup);
        Assert.Contains("Ws2 Task", cut.Markup);
    }

    [Fact]
    public async Task TasksPage_RendersActionButtonsForInspectorReviewAndDetails()
    {
        var ws = await _workspaceRepo.CreateAsync(new Workspace
        {
            Name = "Epsilon Repo",
            RepositoryPath = "/repos/epsilon"
        });
        _workspaceState.TryActivateWorkspaceById(ws.Id);

        var readyTask = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 55,
            WorkspaceId = ws.Id,
            Title = "Ready for Human Review Task",
            Status = TaskOverallStatus.Ready,
            HasExecutionRun = true
        });

        var cut = Render<Tasks>(parameters => parameters.Add(p => p.WorkspaceId, ws.Id));
        cut.WaitForState(() => cut.FindAll("#task-row-55").Count > 0);

        Assert.NotNull(cut.Find("#task-btn-review-55"));
        Assert.NotNull(cut.Find("#task-btn-inspector-55"));
        Assert.NotNull(cut.Find("#task-btn-details-55"));
    }
}

