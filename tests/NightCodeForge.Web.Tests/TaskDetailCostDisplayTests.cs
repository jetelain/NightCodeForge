using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Web.Components.Backlog;
using NightCodeForge.Web.Components.Pages;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class TaskDetailCostDisplayTests : BunitContext
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

    public TaskDetailCostDisplayTests()
    {
        _taskRepository = new InMemoryTaskRepository();
        _workspaceRepository = new InMemoryWorkspaceRepository();
        _depStore = new InMemoryTaskDependencyStore();
        _markdownSync = new MockTaskMarkdownSyncService();
        _devQueueService = new MockDevQueueService();
        _toastService = TestServices.CreateToastService();
        _workspaceState = new WorkspaceStateService();
        _hubConnection = TestHubConnectionHelper.Create();
        _orchestrator = new MockTaskOrchestrator();
        Services.AddSingleton<ITaskOrchestrator>(_orchestrator);
        _mutationService = new TaskMutationService(
            _taskRepository,
            _workspaceRepository,
            _markdownSync,
            _orchestrator,
            new Mock<IOperationsTelemetryService>().Object,
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
        var reviewService = new TaskReviewService(
            _taskRepository,
            _workspaceRepository,
            new Mock<IGitWorkspaceManager>().Object,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TaskReviewService>.Instance);
        Services.AddSingleton<ITaskReviewService>(reviewService);
        Services.AddSingleton<ITaskDiffService>(new TaskDiffService(reviewService));
        Services.AddSingleton<ISyntaxHighlightingService>(new SimpleSyntaxHighlighter());
        Services.AddSingleton<ReviewCommentStateService>(new ReviewCommentStateService());
    }

    [Fact]
    public async Task TaskDetailPage_WhenCostDataExists_RendersTotalCostBadgeAndTimelineStageCost()
    {
        var ws = new Workspace { Id = 1, Name = "Alpha Engine", RepositoryPath = "C:\\Alpha" };
        await _workspaceRepository.CreateAsync(ws);

        var task = new ForgeTask
        {
            Id = 50,
            WorkspaceId = 1,
            Title = "Task with Cost Data",
            Status = TaskOverallStatus.Open,
            ExecutionRecords = new List<StageExecutionRecord>
            {
                new()
                {
                    TaskId = 50,
                    StageId = "Implementation",
                    StartedAt = DateTime.UtcNow.AddMinutes(-10),
                    DurationMs = 12000,
                    Status = StageExecutionStatus.Completed,
                    Cost = 0.035,
                    ActualCloudCost = 0.025,
                    ElectricityCost = 0.010,
                    CloudSavings = 0.080,
                    Currency = "$"
                },
                new()
                {
                    TaskId = 50,
                    StageId = "CodeReview",
                    StartedAt = DateTime.UtcNow.AddMinutes(-5),
                    DurationMs = 4000,
                    Status = StageExecutionStatus.Completed,
                    Cost = 0.007,
                    ActualCloudCost = 0.005,
                    ElectricityCost = 0.002,
                    CloudSavings = 0.020,
                    Currency = "$"
                }
            }
        };
        await _taskRepository.CreateAsync(task);

        var cut = Render<TaskDetail>(p => p.Add(t => t.TaskId, 50));

        // Header cost badge
        Assert.Contains("💰 $0.042", cut.Markup);
        Assert.Contains("Estimated Spend: $0.042 (Cloud: $0.030 | Electricity: $0.012 | Cloud Savings: $0.100)", cut.Markup);

        // Switch to Execution Records tab
        await cut.Find("#tab-btn-execution").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        // Timeline per-stage cost
        Assert.Contains("💰 $0.035", cut.Markup);
        Assert.Contains("💰 $0.007", cut.Markup);
    }

    [Fact]
    public async Task TaskDetailPage_WhenNoCostData_OmitsCostBadge()
    {
        var ws = new Workspace { Id = 1, Name = "Alpha Engine", RepositoryPath = "C:\\Alpha" };
        await _workspaceRepository.CreateAsync(ws);

        var task = new ForgeTask
        {
            Id = 51,
            WorkspaceId = 1,
            Title = "Unpriced Task",
            Status = TaskOverallStatus.Open,
            ExecutionRecords = new List<StageExecutionRecord>
            {
                new()
                {
                    TaskId = 51,
                    StageId = "Implementation",
                    StartedAt = DateTime.UtcNow.AddMinutes(-10),
                    DurationMs = 8000,
                    Status = StageExecutionStatus.Completed,
                    Cost = null
                }
            }
        };
        await _taskRepository.CreateAsync(task);

        var cut = Render<TaskDetail>(p => p.Add(t => t.TaskId, 51));

        Assert.DoesNotContain("💰", cut.Markup);
    }

    [Fact]
    public void TaskDetailDrawer_WhenCostDataExists_RendersCostBadgeAndStageTimeline()
    {
        var task = new ForgeTask
        {
            Id = 52,
            Title = "Drawer Task with Spend",
            Status = TaskOverallStatus.Implemented,
            ExecutionRecords = new List<StageExecutionRecord>
            {
                new()
                {
                    TaskId = 52,
                    StageId = "Implementation",
                    StartedAt = DateTime.UtcNow.AddMinutes(-5),
                    DurationMs = 6000,
                    Status = StageExecutionStatus.Completed,
                    Cost = 0.018,
                    ActualCloudCost = 0.012,
                    ElectricityCost = 0.006,
                    CloudSavings = 0.040,
                    Currency = "$"
                }
            }
        };

        var cut = Render<TaskDetailDrawer>(p => p
            .Add(d => d.IsOpen, true)
            .Add(d => d.Task, task));

        // Header badge
        Assert.Contains("💰 $0.018", cut.Markup);
        // Stage timeline
        Assert.Contains("💰 $0.018", cut.Find(".records-timeline").InnerHtml);
    }
}
