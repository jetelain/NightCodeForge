using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Web.Components.Backlog;
using NightCodeForge.Web.Components.Pages;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class BacklogPageRejectionAndFilterTests : BunitContext
{
    private readonly InMemoryTaskRepository _taskRepository;
    private readonly InMemoryWorkspaceRepository _workspaceRepository;
    private readonly InMemoryTaskDependencyStore _depStore;
    private readonly DependencyResolutionEngine _dependencyEngine;
    private readonly MockTaskMarkdownSyncService _markdownSync;
    private readonly MockTaskOrchestrator _orchestrator;
    private readonly WorkspaceStateService _workspaceState;
    private readonly ToastService _toastService;
    private readonly MockDevQueueService _devQueue;
    private readonly TaskMutationService _mutationService;

    public BacklogPageRejectionAndFilterTests()
    {
        _taskRepository = new InMemoryTaskRepository();
        _workspaceRepository = new InMemoryWorkspaceRepository();
        _depStore = new InMemoryTaskDependencyStore();
        _dependencyEngine = new DependencyResolutionEngine(_depStore);
        _markdownSync = new MockTaskMarkdownSyncService();
        _orchestrator = new MockTaskOrchestrator();
        _workspaceState = new WorkspaceStateService();
        _toastService = TestServices.CreateToastService();
        _devQueue = new MockDevQueueService();

        _mutationService = new TaskMutationService(
            _taskRepository,
            _workspaceRepository,
            _markdownSync,
            _orchestrator,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TaskMutationService>.Instance);

        Services.AddSingleton(_workspaceState);
        Services.AddSingleton<ITaskRepository>(_taskRepository);
        Services.AddSingleton<IWorkspaceRepository>(_workspaceRepository);
        Services.AddSingleton<IWorkflowRepository>(new InMemoryWorkflowRepository());
        Services.AddSingleton<ITaskDependencyStore>(_depStore);
        Services.AddSingleton<IDependencyResolutionEngine>(_dependencyEngine);
        Services.AddSingleton<ITaskMarkdownSyncService>(_markdownSync);
        Services.AddSingleton<ITaskOrchestrator>(_orchestrator);
        Services.AddSingleton<ITaskMutationService>(_mutationService);
        Services.AddSingleton<IDevQueueService>(_devQueue);
        Services.AddSingleton(_toastService);
        Services.AddSingleton<ITaskHistoryDiffService, TaskHistoryDiffService>();
        Services.AddSingleton<ITaskDiskSyncCoordinator>(new MockTaskDiskSyncCoordinator());
        Services.AddSingleton<IDagLayoutEngine, DagLayoutEngine>();
        Services.AddSingleton(new OrchestratorHubClientConnection("/hubs/orchestrator"));
    }

    [Fact]
    public void BacklogPage_PriorityFilterButtons_HavePriorityFilterBtnAndStatChipClasses()
    {
        var ws = new Workspace { Id = 1, Name = "Test Project", RepositoryPath = "C:\\Repo" };
        _workspaceState.SetActiveWorkspace(ws);

        var cut = Render<Backlog>();

        var priorityButtons = cut.FindAll("button.priority-filter-btn");
        Assert.Equal(5, priorityButtons.Count);

        foreach (var btn in priorityButtons)
        {
            Assert.Contains("stat-chip", btn.ClassName);
            Assert.False(string.IsNullOrWhiteSpace(btn.TextContent));
        }
    }

    [Fact]
    public void TaskDetailDrawer_RendersRejectTaskButton_ForOpenTasks()
    {
        var task = new ForgeTask
        {
            Id = 42,
            WorkspaceId = 1,
            Title = "Open Task in Drawer",
            Status = TaskOverallStatus.Open
        };

        var cut = Render<TaskDetailDrawer>(p => p
            .Add(d => d.IsOpen, true)
            .Add(d => d.Task, task));

        var rejectBtn = cut.Find("button#btn-drawer-reject-task");
        Assert.NotNull(rejectBtn);
        Assert.Contains("Reject Task", rejectBtn.TextContent);
    }

    [Fact]
    public async Task TaskRejectionModal_WhenConfirmed_CallsMutationServiceRejectTask()
    {
        var task = new ForgeTask
        {
            Id = 99,
            WorkspaceId = 1,
            Title = "Task to be rejected via modal",
            Status = TaskOverallStatus.Open
        };

        bool closed = false;
        ForgeTask? rejectedTask = null;

        var cut = Render<TaskRejectionModal>(p => p
            .Add(m => m.IsOpen, true)
            .Add(m => m.TargetTask, task)
            .Add(m => m.OnClosed, () => { closed = true; })
            .Add(m => m.OnRejected, (t) => { rejectedTask = t; }));

        var textarea = cut.Find("textarea#rejection-reason-input");
        textarea.Change("Not aligning with product goals");

        var confirmBtn = cut.Find("button#btn-confirm-reject");
        await confirmBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.True(closed);
        Assert.NotNull(rejectedTask);
        Assert.Equal(99, rejectedTask.Id);
        Assert.NotNull(_orchestrator.LastRejectCall);
        Assert.Equal(99, _orchestrator.LastRejectCall.Value.TaskId);
        Assert.Equal("Not aligning with product goals", _orchestrator.LastRejectCall.Value.Reason);
    }

    [Fact]
    public void KanbanBoard_PromotedTaskWithPendingPrerequisites_PlacesInOpenColumn_NotFlight()
    {
        // Simulates Promoted Task 4: Open, but HasExecutionRun = false, CurrentStageId = "" (or preset "task-review")
        var task4 = new ForgeTask
        {
            Id = 4,
            Title = "Promoted Task 4 with Pending Prerequisites",
            Status = TaskOverallStatus.Open,
            HasExecutionRun = false,
            CurrentStageId = "task-review"
        };

        var tasks = new List<ForgeTask> { task4 };

        var cut = Render<KanbanBoard>(p => p
            .Add(b => b.Tasks, tasks));

        var columns = cut.FindAll(".kanban-column");
        // Column 1: Suggested = 0
        Assert.Equal("0", columns[0].QuerySelector(".column-count-badge")?.TextContent);
        // Column 2: Open (Backlog) = 1
        Assert.Equal("1", columns[1].QuerySelector(".column-count-badge")?.TextContent);
        // Column 3: In-Flight = 0
        Assert.Equal("0", columns[2].QuerySelector(".column-count-badge")?.TextContent);
    }

    [Fact]
    public async Task KanbanCard_ClickingReject_InvokesOnReject()
    {
        var task = new ForgeTask
        {
            Id = 77,
            Title = "Task with Reject Action",
            Status = TaskOverallStatus.Open
        };

        ForgeTask? rejected = null;
        var cut = Render<KanbanCard>(p => p
            .Add(c => c.Task, task)
            .Add(c => c.OnReject, (t) => { rejected = t; }));

        var rejectBtn = cut.Find("button.reject-btn");
        await rejectBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.NotNull(rejected);
        Assert.Equal(77, rejected.Id);
    }

    [Fact]
    public async Task TaskDetailDrawer_ClickingRejectButton_OpensRejectionModal_AndSubmitsRejection()
    {
        var task = new ForgeTask
        {
            Id = 55,
            WorkspaceId = 1,
            Title = "Task in Drawer to reject",
            Status = TaskOverallStatus.Open
        };

        ForgeTask? updatedTask = null;
        var cut = Render<TaskDetailDrawer>(p => p
            .Add(d => d.IsOpen, true)
            .Add(d => d.Task, task)
            .Add(d => d.OnTaskUpdated, (t) => { updatedTask = t; }));

        var rejectBtn = cut.Find("button#btn-drawer-reject-task");
        await rejectBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        var textarea = cut.Find("textarea#rejection-reason-input");
        textarea.Change("Obsolete requirement from drawer");

        var confirmBtn = cut.Find("button#btn-confirm-reject");
        await confirmBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.NotNull(updatedTask);
        Assert.Equal(55, updatedTask.Id);
        Assert.NotNull(_orchestrator.LastRejectCall);
        Assert.Equal(55, _orchestrator.LastRejectCall.Value.TaskId);
        Assert.Equal("Obsolete requirement from drawer", _orchestrator.LastRejectCall.Value.Reason);
    }

    [Fact]
    public void TaskDetailDrawer_OmitsRejectButton_WhenTaskIsClosedOrRejected()
    {
        var closedTask = new ForgeTask { Id = 56, WorkspaceId = 1, Title = "Closed", Status = TaskOverallStatus.Closed };
        var rejectedTask = new ForgeTask { Id = 57, WorkspaceId = 1, Title = "Rejected", Status = TaskOverallStatus.Rejected };

        var cutClosed = Render<TaskDetailDrawer>(p => p.Add(d => d.IsOpen, true).Add(d => d.Task, closedTask));
        Assert.Empty(cutClosed.FindAll("button#btn-drawer-reject-task"));

        var cutRejected = Render<TaskDetailDrawer>(p => p.Add(d => d.IsOpen, true).Add(d => d.Task, rejectedTask));
        Assert.Empty(cutRejected.FindAll("button#btn-drawer-reject-task"));
    }

    [Fact]
    public async Task BacklogPage_ClickingInFlightStatChip_ActivatesStatChip()
    {
        var ws = new Workspace { Id = 1, Name = "Test Project", RepositoryPath = "C:\\Repo" };
        _workspaceState.SetActiveWorkspace(ws);

        var cut = Render<Backlog>();

        var chips = cut.FindAll(".backlog-stats-bar .stat-chip");
        var inFlightChip = chips.First(c => c.TextContent.Contains("In-Flight"));

        await inFlightChip.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        var updatedInFlightChip = cut.FindAll(".backlog-stats-bar .stat-chip").First(c => c.TextContent.Contains("In-Flight"));
        Assert.Contains("active", updatedInFlightChip.ClassName);
    }

    [Fact]
    public void BacklogPage_RendersRejectedStatChip_AndDropdownHasCancelledAndFailed()
    {
        var ws = new Workspace { Id = 1, Name = "Test Project", RepositoryPath = "C:\\Repo" };
        _workspaceState.SetActiveWorkspace(ws);

        var cut = Render<Backlog>();

        var rejectedChip = cut.FindAll(".backlog-stats-bar .stat-chip")
            .FirstOrDefault(c => c.TextContent.Contains("Rejected"));
        Assert.NotNull(rejectedChip);

        var select = cut.Find("select.toolbar-filter-select");
        var options = select.QuerySelectorAll("option");
        var optionValues = options.Select(o => o.GetAttribute("value")).ToList();

        Assert.Contains(TaskOverallStatus.Cancelled.ToString(), optionValues);
        Assert.Contains(TaskOverallStatus.Failed.ToString(), optionValues);
        Assert.Contains(TaskOverallStatus.Rejected.ToString(), optionValues);
    }

    [Fact]
    public async Task BacklogPage_ClickingCardRejectButton_OpensModal_AndSubmitsRejection()
    {
        var ws = new Workspace { Id = 1, Name = "Test Project", RepositoryPath = "C:\\Repo" };
        _workspaceState.SetActiveWorkspace(ws);

        var task = new ForgeTask
        {
            Id = 88,
            WorkspaceId = 1,
            Title = "Backlog Card To Reject",
            Status = TaskOverallStatus.Open
        };
        await _taskRepository.CreateAsync(task);

        var cut = Render<Backlog>();

        var rejectBtn = cut.Find("button.reject-btn");
        await rejectBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        var textarea = cut.Find("textarea#rejection-reason-input");
        textarea.Change("Not aligned with v1 scope");

        var confirmBtn = cut.Find("button#btn-confirm-reject");
        await confirmBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.NotNull(_orchestrator.LastRejectCall);
        Assert.Equal(88, _orchestrator.LastRejectCall.Value.TaskId);
        Assert.Equal("Not aligned with v1 scope", _orchestrator.LastRejectCall.Value.Reason);
    }

    [Fact]
    public async Task BacklogPage_ClickingRejectedStatChip_FiltersOnlyRejectedTasks()
    {
        var ws = new Workspace { Id = 1, Name = "Test Project", RepositoryPath = "C:\\Repo" };
        _workspaceState.SetActiveWorkspace(ws);

        var openTask = new ForgeTask { Id = 10, WorkspaceId = 1, Title = "Still Open Task", Status = TaskOverallStatus.Open };
        var rejectedTask = new ForgeTask { Id = 11, WorkspaceId = 1, Title = "Archived Rejected Task", Status = TaskOverallStatus.Rejected };

        await _taskRepository.CreateAsync(openTask);
        await _taskRepository.CreateAsync(rejectedTask);

        var cut = Render<Backlog>();

        var rejectedChip = cut.FindAll(".backlog-stats-bar .stat-chip")
            .First(c => c.TextContent.Contains("Rejected"));

        await rejectedChip.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Contains("Archived Rejected Task", cut.Markup);
        Assert.DoesNotContain("Still Open Task", cut.Markup);
    }
}

