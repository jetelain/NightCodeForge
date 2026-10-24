using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Web.Components.Pages;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class TaskInspectorPageTests : BunitContext
{
    private readonly InMemoryTaskRepository _taskRepository;
    private readonly InMemoryWorkflowRepository _workflowRepository;
    private readonly WorkerStateService _workerState;
    private readonly WorkspaceStateService _workspaceState;
    private readonly OrchestratorHubClientConnection _hubConnection;
    private readonly FakeDevQueueService _devQueueService;
    private readonly FakeTaskReviewService _taskReviewService;

    public TaskInspectorPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;

        _taskRepository = new InMemoryTaskRepository();
        _workflowRepository = new InMemoryWorkflowRepository(new[] { TestWorkflowFixtures.CreateStandardWorkflow() });
        _workerState = new WorkerStateService(new Moq.Mock<NightCodeForge.Core.Interfaces.IOperationsTelemetryService>().Object);
        _workspaceState = new WorkspaceStateService();
        _hubConnection = TestHubConnectionHelper.Create(workerStateService: _workerState);
        _devQueueService = new FakeDevQueueService();
        _taskReviewService = new FakeTaskReviewService();

        Services.AddSingleton<ITaskRepository>(_taskRepository);
        Services.AddSingleton<ITaskOrchestrator>(new MockTaskOrchestrator());
        Services.AddSingleton<IWorkflowRepository>(_workflowRepository);
        Services.AddSingleton(_workerState);
        Services.AddSingleton(_workspaceState);
        Services.AddSingleton(_hubConnection);
        Services.AddSingleton<IDevQueueService>(_devQueueService);
        Services.AddSingleton<ITaskReviewService>(_taskReviewService);
        Services.AddSingleton<ReviewCommentStateService>();
        Services.AddSingleton<ISyntaxHighlightingService>(new SimpleSyntaxHighlighter());
    }

    [Fact]
    public void TaskInspector_WhenNoTasks_RendersEmptyState()
    {
        var cut = Render<TaskInspector>();

        Assert.Contains("Task Execution Inspector", cut.Find("h1").TextContent);
        Assert.NotNull(cut.Find("#task-inspector-empty"));
        Assert.Contains("No Executed Tasks Found", cut.Markup);
    }

    [Fact]
    public async Task TaskInspector_WhenTasksExist_RendersTaskSelectorAndInspectorWith1To7Stepper()
    {
        await _taskRepository.CreateAsync(new ForgeTask
        {
            Id = 101,
            WorkspaceId = 1,
            Title = "Implement OAuth2 Flow",
            Description = "Full OAuth2 authorization code flow with PKCE.",
            Status = TaskOverallStatus.Open,
            HasExecutionRun = true,
            CurrentStageId = "implementation"
        });

        var cut = Render<TaskInspector>();

        cut.WaitForState(() => cut.Markup.Contains("Implement OAuth2 Flow"));

        Assert.Contains("Task Execution Inspector", cut.Find("h1").TextContent);
        Assert.NotNull(cut.Find("#task-inspector-toolbar"));
        Assert.NotNull(cut.Find("#task-selector-dropdown"));

        // Verifies TaskExecutionInspector and 1-7 phases pipeline stepper are rendered
        Assert.NotNull(cut.Find("#task-execution-inspector"));
        Assert.NotNull(cut.Find(".pipeline-stepper-container"));

        // Stepper contains 7 steps
        for (int i = 1; i <= 7; i++)
        {
            Assert.NotNull(cut.Find($"#stage-step-{i}"));
        }
    }

    [Fact]
    public async Task TaskInspector_WhenRouteTaskIdProvided_LoadsSpecifiedTask()
    {
        await _taskRepository.CreateAsync(new ForgeTask
        {
            Id = 105,
            WorkspaceId = 1,
            Title = "Database Migration",
            Status = TaskOverallStatus.Reviewed,
            HasExecutionRun = true
        });
        await _taskRepository.CreateAsync(new ForgeTask
        {
            Id = 106,
            WorkspaceId = 1,
            Title = "Specified Route Task",
            Status = TaskOverallStatus.Implemented,
            HasExecutionRun = true
        });

        var cut = Render<TaskInspector>(p => p.Add(x => x.TaskId, 106));

        cut.WaitForState(() => cut.Markup.Contains("Specified Route Task"));

        Assert.Contains("Specified Route Task", cut.Markup);
        Assert.Contains("Task #106", cut.Markup);
    }

    [Fact]
    public async Task TaskInspector_WhenWorkerRunningDifferentTask_ShowsSwitchButton()
    {
        await _taskRepository.CreateAsync(new ForgeTask
        {
            Id = 201,
            WorkspaceId = 1,
            Title = "Selected Finished Task",
            Status = TaskOverallStatus.Closed,
            HasExecutionRun = true
        });

        _workerState.HandleWorkerStatus("Running", 202, "implementation", "Local GPU", "Active Running Task", "feature/task-202");

        var cut = Render<TaskInspector>(p => p.Add(x => x.TaskId, 201));

        cut.WaitForState(() => cut.Find("#switch-to-active-task-btn") != null);

        var switchBtn = cut.Find("#switch-to-active-task-btn");
        Assert.NotNull(switchBtn);
        Assert.Contains("#202", switchBtn.TextContent);
    }

    [Fact]
    public async Task TaskInspector_ReusesPipelineStepper_WithStageStatusMapping()
    {
        var task = new ForgeTask
        {
            Id = 301,
            WorkspaceId = 1,
            Title = "Closed Completed Pipeline Task",
            Status = TaskOverallStatus.Closed,
            HasExecutionRun = true
        };

        await _taskRepository.CreateAsync(task);

        var cut = Render<TaskInspector>(p => p.Add(x => x.TaskId, 301));

        cut.WaitForState(() => cut.Markup.Contains("Closed Completed Pipeline Task"));

        // For a closed task, all 7 stages on the reused pipeline stepper should be completed
        for (int i = 1; i <= 7; i++)
        {
            var step = cut.Find($"#stage-step-{i}");
            Assert.Contains("step-completed", step.ClassName);
        }
    }

    [Fact]
    public async Task TaskInspector_WhenStageTransitionReceived_UpdatesDropdownTaskStatus()
    {
        var task = new ForgeTask
        {
            Id = 401,
            WorkspaceId = 1,
            Title = "Live Stepper Task",
            Status = TaskOverallStatus.Open,
            HasExecutionRun = true
        };
        await _taskRepository.CreateAsync(task);

        var cut = Render<TaskInspector>(p => p.Add(x => x.TaskId, 401));
        cut.WaitForState(() => cut.Markup.Contains("Live Stepper Task"));

        Assert.Contains("#401 - Live Stepper Task (Open)", cut.Markup);

        // Transition task in repository
        task.Status = TaskOverallStatus.Implemented;
        await _taskRepository.UpdateAsync(task);

        // Fire SignalR stage transition event
        _hubConnection.TriggerStageTransition(401, "task-review", "Deterministic", "Completed");

        cut.WaitForState(() => cut.Markup.Contains("#401 - Live Stepper Task (Implemented)"));
        Assert.Contains("#401 - Live Stepper Task (Implemented)", cut.Markup);
    }

    [Fact]
    public async Task TaskInspector_Dropdown_ExcludesUnstartedTasks_AndIncludesExecutedAndRolledBackTasks()
    {
        // 1. Unstarted Open task (HasExecutionRun == false)
        await _taskRepository.CreateAsync(new ForgeTask
        {
            Id = 501,
            WorkspaceId = 1,
            Title = "Unstarted Fresh Task",
            Status = TaskOverallStatus.Open,
            HasExecutionRun = false
        });

        // 2. Executed Open task that was rolled back (HasExecutionRun == true, Status == Open)
        await _taskRepository.CreateAsync(new ForgeTask
        {
            Id = 502,
            WorkspaceId = 1,
            Title = "Rolled Back Stage Task",
            Status = TaskOverallStatus.Open,
            HasExecutionRun = true,
            MaxExecutedStageOrder = 2
        });

        // 3. Implemented task (HasExecutionRun == true, Status == Implemented)
        await _taskRepository.CreateAsync(new ForgeTask
        {
            Id = 503,
            WorkspaceId = 1,
            Title = "Completed Feature Task",
            Status = TaskOverallStatus.Implemented,
            HasExecutionRun = true,
            MaxExecutedStageOrder = 4
        });

        var cut = Render<TaskInspector>();

        cut.WaitForState(() => cut.Markup.Contains("Rolled Back Stage Task"));

        var dropdown = cut.Find("#task-selector-dropdown");
        var options = dropdown.QuerySelectorAll("option").Select(o => o.TextContent).ToList();

        // Rolled back and executed tasks are selectable
        Assert.Contains(options, o => o.Contains("#502") && o.Contains("Rolled Back Stage Task (Open)"));
        Assert.Contains(options, o => o.Contains("#503") && o.Contains("Completed Feature Task (Implemented)"));

        // Unstarted task is omitted
        Assert.DoesNotContain(options, o => o.Contains("#501"));
        Assert.DoesNotContain(options, o => o.Contains("Unstarted Fresh Task"));
    }

    [Fact]
    public async Task TaskInspector_WhenWorkspaceChanged_UpdatesInspectedTaskToNewWorkspace()
    {
        var ws1 = new Workspace { Id = 1, Name = "Workspace Alpha", RepositoryPath = "/path/alpha" };
        var ws2 = new Workspace { Id = 2, Name = "Workspace Beta", RepositoryPath = "/path/beta" };

        await _taskRepository.CreateAsync(new ForgeTask
        {
            Id = 601,
            WorkspaceId = 1,
            Title = "Alpha Workspace Task",
            Status = TaskOverallStatus.Open,
            HasExecutionRun = true
        });

        await _taskRepository.CreateAsync(new ForgeTask
        {
            Id = 602,
            WorkspaceId = 2,
            Title = "Beta Workspace Task",
            Status = TaskOverallStatus.Implemented,
            HasExecutionRun = true
        });

        _workspaceState.SetActiveWorkspace(ws1);

        var cut = Render<TaskInspector>(p => p.Add(x => x.TaskId, 601));
        cut.WaitForState(() => cut.Markup.Contains("Alpha Workspace Task"));

        Assert.Contains("Alpha Workspace Task", cut.Markup);
        var nav = Services.GetRequiredService<NavigationManager>();

        // Switch to Workspace 2
        _workspaceState.SetActiveWorkspace(ws2);

        cut.WaitForState(() => cut.Markup.Contains("Beta Workspace Task"));

        Assert.Contains("Beta Workspace Task", cut.Markup);
        Assert.DoesNotContain("Alpha Workspace Task", cut.Markup);
        Assert.Contains("/inspector/602", nav.Uri);
    }

    [Fact]
    public async Task TaskInspector_WhenWorkspaceChangedToEmptyWorkspace_DisplaysEmptyState()
    {
        var ws1 = new Workspace { Id = 1, Name = "Workspace Alpha", RepositoryPath = "/path/alpha" };
        var ws2 = new Workspace { Id = 2, Name = "Workspace Empty", RepositoryPath = "/path/empty" };

        await _taskRepository.CreateAsync(new ForgeTask
        {
            Id = 701,
            WorkspaceId = 1,
            Title = "Alpha Active Task",
            Status = TaskOverallStatus.Open,
            HasExecutionRun = true
        });

        _workspaceState.SetActiveWorkspace(ws1);

        var cut = Render<TaskInspector>(p => p.Add(x => x.TaskId, 701));
        cut.WaitForState(() => cut.Markup.Contains("Alpha Active Task"));

        var nav = Services.GetRequiredService<NavigationManager>();

        // Switch to Workspace 2 (which has no tasks)
        _workspaceState.SetActiveWorkspace(ws2);

        cut.WaitForState(() => cut.Find("#task-inspector-empty") != null);

        Assert.NotNull(cut.Find("#task-inspector-empty"));
        Assert.Contains("/inspector", nav.Uri);
    }

    #region Fakes

    private class FakeDevQueueService : IDevQueueService
    {
        public Task<DevQueueSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new DevQueueSnapshot(
                new List<QueuedJobItem>(),
                new List<QueuedJobItem>(),
                new List<QueuedJobItem>(),
                new List<QueuedJobItem>(),
                new List<QueuedJobItem>(),
                new List<ActiveLockSnapshot>()));

        public Task<bool> PromoteToUrgentAsync(string jobId, int taskId, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<bool> PromoteToUrgentAsync(int taskId, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<bool> CancelJobAsync(string jobId, int? taskId = null, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<bool> RequeueJobAsync(string jobId, int? taskId = null, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<bool> EmergencyReleaseLockAsync(int workspaceId, int? taskId = null, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<bool> RescheduleTaskAsync(int taskId, bool restartPipeline = false, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<int> ReorderInferenceQueueByPriorityAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(0);
    }

    private class FakeTaskReviewService : ITaskReviewService
    {
        public Task<TaskReviewSnapshot?> GetSnapshotAsync(int taskId, CancellationToken cancellationToken = default)
            => Task.FromResult<TaskReviewSnapshot?>(null);

        public Task<IReadOnlyList<TaskReviewSnapshot>> GetPendingReviewsAsync(int? workspaceId = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<TaskReviewSnapshot>>(Array.Empty<TaskReviewSnapshot>());

        public Task<IReadOnlyList<FileDiffSummary>> GetTaskDiffFilesAsync(int taskId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<FileDiffSummary>>(Array.Empty<FileDiffSummary>());

        public bool IsPendingReview(TaskReviewSnapshot snapshot)
            => snapshot.HasExecutionRun && snapshot.Status == TaskOverallStatus.Ready;

        public bool ShouldDisplayConflictResolvedBanner(TaskReviewSnapshot snapshot)
            => snapshot.HasResolvedConflict && snapshot.Status == TaskOverallStatus.Ready;
    }

    #endregion
}
