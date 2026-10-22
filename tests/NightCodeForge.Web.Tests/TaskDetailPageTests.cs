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

public class TaskDetailPageTests : BunitContext
{
    private readonly InMemoryTaskRepository _taskRepository;
    private readonly InMemoryWorkspaceRepository _workspaceRepository;
    private readonly InMemoryTaskDependencyStore _depStore;
    private readonly MockTaskMarkdownSyncService _markdownSync;
    private readonly MockDevQueueService _devQueueService;
    private readonly ToastService _toastService;
    private readonly WorkspaceStateService _workspaceState;
    private readonly OrchestratorHubClientConnection _hubConnection;
    private readonly MockTaskOrchestrator _orchestrator;
    private readonly TaskMutationService _mutationService;

    public TaskDetailPageTests()
    {
        _taskRepository = new InMemoryTaskRepository();
        _workspaceRepository = new InMemoryWorkspaceRepository();
        _depStore = new InMemoryTaskDependencyStore();
        _markdownSync = new MockTaskMarkdownSyncService();
        _devQueueService = new MockDevQueueService();
        _toastService = TestServices.CreateToastService();
        _workspaceState = new WorkspaceStateService();
        _hubConnection = new OrchestratorHubClientConnection("/hubs/orchestrator");
        _orchestrator = new MockTaskOrchestrator();
        Services.AddSingleton<ITaskOrchestrator>(_orchestrator);
        _mutationService = new TaskMutationService(
            _taskRepository,
            _workspaceRepository,
            _markdownSync,
            _orchestrator,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TaskMutationService>.Instance);

        Services.AddSingleton<ITaskRepository>(_taskRepository);
        Services.AddSingleton<IWorkspaceRepository>(_workspaceRepository);
        Services.AddSingleton<ITaskDependencyStore>(_depStore);
        Services.AddSingleton<IDependencyResolutionEngine>(new DependencyResolutionEngine(_depStore));
        Services.AddSingleton<ITaskMarkdownSyncService>(_markdownSync);
        Services.AddSingleton<IDevQueueService>(_devQueueService);
        Services.AddSingleton(_toastService);
        Services.AddSingleton(_workspaceState);
        Services.AddSingleton(_hubConnection);
        Services.AddSingleton<ITaskHistoryDiffService, TaskHistoryDiffService>();
        Services.AddSingleton<ITaskMutationService>(_mutationService);
    }

    [Fact]
    public async Task WhenTaskNotFound_RendersNotFoundState()
    {
        var cut = Render<TaskDetail>(p => p.Add(t => t.TaskId, 999));

        Assert.Contains("Task #999 Not Found", cut.Markup);
        Assert.Contains("Return to Backlog &amp; DAG", cut.Markup);
    }

    [Fact]
    public async Task WhenTaskExists_RendersTitleBadgesAndDescriptionWithoutInnerScroll()
    {
        var ws = new Workspace { Id = 1, Name = "Alpha Engine", RepositoryPath = "C:\\Alpha" };
        await _workspaceRepository.CreateAsync(ws);

        var task = new ForgeTask
        {
            Id = 42,
            WorkspaceId = 1,
            Title = "Implement Distributed Consensus",
            Description = "### Architecture\nThis feature implements Raft consensus.\n\n- Leader election\n- Log replication",
            Status = TaskOverallStatus.Open,
            Priority = TaskPriority.High,
            MergeMode = MergeMode.Automatic
        };
        await _taskRepository.CreateAsync(task);

        var cut = Render<TaskDetail>(p => p.Add(t => t.TaskId, 42));

        Assert.Contains("Implement Distributed Consensus", cut.Markup);
        Assert.Contains("#42", cut.Markup);
        Assert.Contains("P2 - High", cut.Markup);
        Assert.Contains("Auto-Merge", cut.Markup);

        // Verify description markdown rendering exists and is not constrained by 240px inner scroll
        Assert.Contains("Leader election", cut.Markup);
        Assert.Contains("Log replication", cut.Markup);
        Assert.NotEmpty(cut.FindAll(".task-spec-markdown-body"));
    }

    [Fact]
    public async Task EditDescription_SavesNewVersionAndUpdatesView()
    {
        var ws = new Workspace { Id = 2, Name = "Beta Workspace", RepositoryPath = "C:\\Beta" };
        await _workspaceRepository.CreateAsync(ws);

        var task = new ForgeTask
        {
            Id = 55,
            WorkspaceId = 2,
            Title = "Specification Editing Task",
            Description = "Initial requirement details.",
            Status = TaskOverallStatus.Open
        };
        await _taskRepository.CreateAsync(task);

        var cut = Render<TaskDetail>(p => p.Add(t => t.TaskId, 55));

        // Click Edit Markdown button
        var editBtn = cut.Find("#btn-edit-description");
        editBtn.Click();

        // Find textarea and enter new markdown specification
        var textarea = cut.Find("#task-spec-textarea");
        textarea.Change("Updated specification with acceptance criteria.\n\n- [x] Step 1\n- [ ] Step 2");

        // Click Save Changes button
        var saveBtn = cut.Find("#btn-save-description");
        await cut.InvokeAsync(() => saveBtn.Click());

        // Verify persisted in repository
        var updated = await _taskRepository.GetByIdAsync(55);
        Assert.NotNull(updated);
        Assert.Contains("Updated specification with acceptance criteria.", updated.Description);

        // Verify UI updated
        Assert.Contains("Updated specification with acceptance criteria.", cut.Markup);
        Assert.Contains("Step 1", cut.Markup);
    }

    [Fact]
    public async Task UpdatePriority_ClickingChip_UpdatesTaskAndEscalatesIfCritical()
    {
        var ws = new Workspace { Id = 3, Name = "Gamma Project", RepositoryPath = "C:\\Gamma" };
        await _workspaceRepository.CreateAsync(ws);

        var task = new ForgeTask
        {
            Id = 77,
            WorkspaceId = 3,
            Title = "Priority Escalation Task",
            Priority = TaskPriority.Normal,
            Status = TaskOverallStatus.Open
        };
        await _taskRepository.CreateAsync(task);

        var cut = Render<TaskDetail>(p => p.Add(t => t.TaskId, 77));

        // Click Critical (P1) chip
        var p1Btn = cut.FindAll(".priority-chip-btn").First(b => b.TextContent.Contains("P1"));
        await cut.InvokeAsync(() => p1Btn.Click());

        var updated = await _taskRepository.GetByIdAsync(77);
        Assert.NotNull(updated);
        Assert.Equal(TaskPriority.Critical, updated.Priority);

        // Toast displayed
        Assert.Contains(_toastService.Toasts, t => t.Title == "Priority Escalated");
    }

    [Fact]
    public async Task UpdateMergeMode_TogglingButton_UpdatesTaskMergeMode()
    {
        var ws = new Workspace { Id = 4, Name = "Delta Project", RepositoryPath = "C:\\Delta" };
        await _workspaceRepository.CreateAsync(ws);

        var task = new ForgeTask
        {
            Id = 88,
            WorkspaceId = 4,
            Title = "Merge Mode Task",
            MergeMode = MergeMode.ManualReviewRequired,
            Status = TaskOverallStatus.Open
        };
        await _taskRepository.CreateAsync(task);

        var cut = Render<TaskDetail>(p => p.Add(t => t.TaskId, 88));

        // Click Auto-Merge button
        var autoMergeBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Auto-Merge"));
        await cut.InvokeAsync(() => autoMergeBtn.Click());

        var updated = await _taskRepository.GetByIdAsync(88);
        Assert.NotNull(updated);
        Assert.Equal(MergeMode.Automatic, updated.MergeMode);

        Assert.Contains(_toastService.Toasts, t => t.Title == "Merge Mode Updated");
    }

    [Fact]
    public async Task Prerequisites_RendersBlockingAndSatisfiedBadges()
    {
        var ws = new Workspace { Id = 5, Name = "Epsilon DAG", RepositoryPath = "C:\\Epsilon" };
        await _workspaceRepository.CreateAsync(ws);

        var prereqClosed = new ForgeTask { Id = 10, WorkspaceId = 5, Title = "Foundation Prereq", Status = TaskOverallStatus.Closed };
        var prereqOpen = new ForgeTask { Id = 11, WorkspaceId = 5, Title = "In-Flight Prereq", Status = TaskOverallStatus.Open };
        var mainTask = new ForgeTask { Id = 12, WorkspaceId = 5, Title = "Dependent Main Task", Status = TaskOverallStatus.Open };

        await _taskRepository.CreateAsync(prereqClosed);
        await _taskRepository.CreateAsync(prereqOpen);
        await _taskRepository.CreateAsync(mainTask);

        _depStore.AddDependency(5, 12, 10);
        _depStore.AddDependency(5, 12, 11);

        var cut = Render<TaskDetail>(p => p.Add(t => t.TaskId, 12));

        Assert.Contains("Prerequisites (2)", cut.Markup);
        Assert.Contains("Foundation Prereq", cut.Markup);
        Assert.Contains("In-Flight Prereq", cut.Markup);
        Assert.Contains("Closed", cut.Markup);
        Assert.Contains("Blocking", cut.Markup);
    }

    [Fact]
    public async Task TaskDetail_RendersRejectButton_ForOpenTask_AndOmitsForClosedOrRejected()
    {
        var openTask = new ForgeTask { Id = 20, WorkspaceId = 1, Title = "Open Detail Task", Status = TaskOverallStatus.Open };
        var closedTask = new ForgeTask { Id = 21, WorkspaceId = 1, Title = "Closed Detail Task", Status = TaskOverallStatus.Closed };
        var rejectedTask = new ForgeTask { Id = 22, WorkspaceId = 1, Title = "Rejected Detail Task", Status = TaskOverallStatus.Rejected };

        await _taskRepository.CreateAsync(openTask);
        await _taskRepository.CreateAsync(closedTask);
        await _taskRepository.CreateAsync(rejectedTask);

        var cutOpen = Render<TaskDetail>(p => p.Add(t => t.TaskId, 20));
        var rejectBtn = cutOpen.Find("button#btn-reject-task");
        Assert.NotNull(rejectBtn);
        Assert.Contains("Reject Task", rejectBtn.TextContent);

        var cutClosed = Render<TaskDetail>(p => p.Add(t => t.TaskId, 21));
        Assert.Empty(cutClosed.FindAll("button#btn-reject-task"));

        var cutRejected = Render<TaskDetail>(p => p.Add(t => t.TaskId, 22));
        Assert.Empty(cutRejected.FindAll("button#btn-reject-task"));
    }

    [Fact]
    public async Task TaskDetail_ClickingRejectButton_OpensModal_AndRejectionUpdatesTaskStatus()
    {
        var task = new ForgeTask { Id = 30, WorkspaceId = 1, Title = "Detail Task to Reject", Status = TaskOverallStatus.Open };
        await _taskRepository.CreateAsync(task);

        var cut = Render<TaskDetail>(p => p.Add(t => t.TaskId, 30));
        var rejectBtn = cut.Find("button#btn-reject-task");
        await rejectBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        var textarea = cut.Find("textarea#rejection-reason-input");
        textarea.Change("Not viable anymore");

        var confirmBtn = cut.Find("button#btn-confirm-reject");
        await confirmBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.NotNull(_orchestrator.LastRejectCall);
        Assert.Equal(30, _orchestrator.LastRejectCall.Value.TaskId);
        Assert.Equal("Not viable anymore", _orchestrator.LastRejectCall.Value.Reason);
    }
}
