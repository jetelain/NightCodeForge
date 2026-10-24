using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Web.Components.Pages;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class ReviewPageTests : BunitContext
{
    private readonly InMemoryTaskRepository _taskRepository;
    private readonly InMemoryWorkspaceRepository _workspaceRepository;
    private readonly TaskReviewService _reviewService;
    private readonly MockTaskOrchestrator _orchestrator;
    private readonly ReviewCommentStateService _commentState;
    private readonly ToastService _toastService;
    private readonly WorkspaceStateService _workspaceState;

    public ReviewPageTests()
    {
        _taskRepository = new InMemoryTaskRepository();
        _workspaceRepository = new InMemoryWorkspaceRepository();
        _reviewService = new TaskReviewService(
            _taskRepository,
            _workspaceRepository,
            new Mock<IGitWorkspaceManager>().Object,
            NullLogger<TaskReviewService>.Instance);
        _orchestrator = new MockTaskOrchestrator();
        _commentState = new ReviewCommentStateService();
        _toastService = TestServices.CreateToastService();
        _workspaceState = new WorkspaceStateService();

        Services.AddSingleton<ITaskRepository>(_taskRepository);
        Services.AddSingleton<ITaskReviewService>(_reviewService);
        Services.AddSingleton<ITaskOrchestrator>(_orchestrator);
        Services.AddSingleton(_commentState);
        Services.AddSingleton(_toastService);
        Services.AddSingleton(_workspaceState);
        Services.AddSingleton<ITaskMutationService>(new Mock<ITaskMutationService>().Object);
        Services.AddSingleton<ISyntaxHighlightingService>(new SimpleSyntaxHighlighter());
    }

    [Fact]
    public void EmptyReviewRoom_RendersNoTasksState()
    {
        var cut = Render<Review>();

        Assert.NotNull(cut.Find("#review-empty-state"));
        Assert.Contains("No Tasks Awaiting Review", cut.Markup);
    }

    [Fact]
    public async Task ReviewRoom_LoadsTask_RendersHeaderAndGateMetrics()
    {
        var bag = new PipelineDataBag();
        bag.Set(PipelineDataBagKeys.CoverageBaselineLineRate, 0.75);
        bag.Set(PipelineDataBagKeys.CoverageCurrentLineRate, 0.82);
        bag.Set(PipelineDataBagKeys.WarningsBaselineTotal, 4);
        bag.Set(PipelineDataBagKeys.WarningsCurrentTotal, 2);
        bag.Set(PipelineDataBagKeys.TestsCurrentTotal, 30);
        bag.Set(PipelineDataBagKeys.TestsCurrentPassed, 30);
        bag.Set(PipelineDataBagKeys.TestsCurrentFailed, 0);
        bag.Set(PipelineDataBagKeys.TestsCurrentDurationMs, 850);

        var files = new List<FileDiffSummary>
        {
            new("src/Core/Parser.cs", FileChangeType.Modified, 5, 2, "old line", "new line")
        };
        bag.Set(PipelineDataBagKeys.ReviewFiles, files);

        var task = new ForgeTask
        {
            Id = 55,
            Title = "Refactor JSON Parser",
            Status = TaskOverallStatus.Ready,
            HasExecutionRun = true,
            MergeMode = MergeMode.ManualReviewRequired,
            BaseBranch = "main",
            FeatureBranch = "feature/task-55",
            PipelineDataJson = bag.ToJson()
        };
        await _taskRepository.CreateAsync(task);

        var cut = Render<Review>(parameters => parameters
            .Add(p => p.TaskId, 55));

        // Header check
        Assert.Contains("Review: ForgeTask #55 - Refactor JSON Parser", cut.Markup);
        Assert.Contains("feature/task-55", cut.Markup);
        Assert.Contains("Mode: ManualReviewRequired", cut.Markup);
        Assert.Contains("Status: Ready", cut.Markup);

        // Quality Gate Cards check
        var covCard = cut.Find("#card-metric-coverage");
        Assert.Contains("gate-pass", covCard.ClassName);
        Assert.Contains("82.0%", covCard.TextContent);
        Assert.Contains("+7.0%", covCard.TextContent);

        var warnCard = cut.Find("#card-metric-warnings");
        Assert.Contains("gate-pass", warnCard.ClassName);
        Assert.Contains("2", warnCard.TextContent);
        Assert.Contains("-2", warnCard.TextContent);

        var testCard = cut.Find("#card-metric-tests");
        Assert.Contains("gate-pass", testCard.ClassName);
        Assert.Contains("30 / 30", testCard.TextContent);
        Assert.Contains("850ms", testCard.TextContent);

        // File list check
        Assert.NotNull(cut.Find("#file-list"));
        Assert.Contains("src/Core/Parser.cs", cut.Markup);

        // Diff viewer panel check
        Assert.NotNull(cut.Find("#diff-viewer-panel"));
        Assert.Contains("src/Core/Parser.cs", cut.Find("#diff-viewer-panel").InnerHtml);

        // Action buttons check
        Assert.NotNull(cut.Find("#btn-request-changes"));
        Assert.NotNull(cut.Find("#btn-approve-merge"));
    }

    [Fact]
    public async Task ReviewRoom_OpenChangeRequestModal_AndApproveModal()
    {
        var task = new ForgeTask
        {
            Id = 60,
            Title = "Feature Gate Check",
            Status = TaskOverallStatus.Ready,
            HasExecutionRun = true,
            MergeMode = MergeMode.ManualReviewRequired,
            PipelineDataJson = new PipelineDataBag().ToJson()
        };
        await _taskRepository.CreateAsync(task);

        var cut = Render<Review>(parameters => parameters
            .Add(p => p.TaskId, 60));

        // Click Request Changes
        cut.Find("#btn-request-changes").Click();
        Assert.NotNull(cut.Find("#change-request-modal"));

        // Close change request modal
        cut.Find("#btn-modal-close").Click();
        Assert.Null(cut.FindAll("#change-request-modal").FirstOrDefault());

        // Click Approve & Merge
        cut.Find("#btn-approve-merge").Click();
        Assert.NotNull(cut.Find("#approve-merge-modal"));

        // Close approve modal
        cut.Find("#btn-approve-close").Click();
        Assert.Null(cut.FindAll("#approve-merge-modal").FirstOrDefault());
    }

    [Fact]
    public async Task ReviewRoom_WhenTaskInFlight_RendersInFlightBannerAndHidesActionButtons()
    {
        var task = new ForgeTask
        {
            Id = 90,
            Title = "In-Flight Gates Task",
            Status = TaskOverallStatus.Implemented,
            HasExecutionRun = true,
            MergeMode = MergeMode.ManualReviewRequired,
            PipelineDataJson = new PipelineDataBag().ToJson()
        };
        await _taskRepository.CreateAsync(task);

        var cut = Render<Review>(parameters => parameters
            .Add(p => p.TaskId, 90));

        // In-flight banner is rendered
        Assert.NotNull(cut.Find("#alert-in-flight-review"));
        Assert.Contains("undergoing automated quality gates and code review", cut.Markup);
        Assert.Contains("/inspector/90", cut.Markup);

        // Action buttons are NOT rendered
        Assert.Null(cut.FindAll("#btn-request-changes").FirstOrDefault());
        Assert.Null(cut.FindAll("#btn-approve-merge").FirstOrDefault());
    }

    [Fact]
    public async Task ReviewRoom_WhenConflictResolved_RendersBannerOnlyWhenReady()
    {
        var bagWithConflict = new PipelineDataBag();
        bagWithConflict.Set(PipelineDataBagKeys.HasResolvedConflict, true);

        // Ready task with resolved conflict
        var readyTask = new ForgeTask
        {
            Id = 91,
            Title = "Ready Conflict Task",
            Status = TaskOverallStatus.Ready,
            HasExecutionRun = true,
            MergeMode = MergeMode.ManualReviewRequired,
            PipelineDataJson = bagWithConflict.ToJson()
        };
        await _taskRepository.CreateAsync(readyTask);

        var readyCut = Render<Review>(parameters => parameters
            .Add(p => p.TaskId, 91));

        Assert.NotNull(readyCut.Find("#alert-re-review-conflict"));
        Assert.Contains("Autonomous Conflict Resolution Completed", readyCut.Markup);
        Assert.NotNull(readyCut.Find("#btn-request-changes"));

        // Implemented task with resolved conflict (still in flight downstream)
        var inFlightTask = new ForgeTask
        {
            Id = 92,
            Title = "In-Flight Conflict Task",
            Status = TaskOverallStatus.Implemented,
            HasExecutionRun = true,
            MergeMode = MergeMode.ManualReviewRequired,
            PipelineDataJson = bagWithConflict.ToJson()
        };
        await _taskRepository.CreateAsync(inFlightTask);

        var inFlightCut = Render<Review>(parameters => parameters
            .Add(p => p.TaskId, 92));

        Assert.Null(inFlightCut.FindAll("#alert-re-review-conflict").FirstOrDefault());
        Assert.NotNull(inFlightCut.Find("#alert-in-flight-review"));
        Assert.Null(inFlightCut.FindAll("#btn-request-changes").FirstOrDefault());
    }

    [Fact]
    public async Task ReviewRoom_AutoMergedBadge_RendersForClosedAutomaticTasks()
    {
        var task = new ForgeTask
        {
            Id = 70,
            Title = "Auto-Merged Task",
            Status = TaskOverallStatus.Closed,
            MergeMode = MergeMode.Automatic,
            BaseBranch = "main",
            PipelineDataJson = new PipelineDataBag().ToJson()
        };
        await _taskRepository.CreateAsync(task);

        var cut = Render<Review>(parameters => parameters
            .Add(p => p.TaskId, 70));

        Assert.NotNull(cut.Find("#badge-auto-merged"));
        Assert.Contains("Auto-Merged to main", cut.Find("#badge-auto-merged").TextContent);
        Assert.Null(cut.FindAll("#btn-request-changes").FirstOrDefault());
        Assert.Null(cut.FindAll("#btn-approve-merge").FirstOrDefault());
    }

    [Fact]
    public async Task ReviewRoom_WhenActiveWorkspaceChanges_ReloadsPendingReviewsForWorkspace()
    {
        var task1 = new ForgeTask
        {
            Id = 81,
            WorkspaceId = 1,
            Title = "Task in Workspace 1",
            Status = TaskOverallStatus.Ready,
            HasExecutionRun = true,
            BaseBranch = "main",
            PipelineDataJson = new PipelineDataBag().ToJson()
        };
        var task2 = new ForgeTask
        {
            Id = 82,
            WorkspaceId = 2,
            Title = "Task in Workspace 2",
            Status = TaskOverallStatus.Ready,
            HasExecutionRun = true,
            BaseBranch = "main",
            PipelineDataJson = new PipelineDataBag().ToJson()
        };
        await _taskRepository.CreateAsync(task1);
        await _taskRepository.CreateAsync(task2);

        _workspaceState.SetActiveWorkspace(new Workspace { Id = 1, Name = "WS1" });

        var cut = Render<Review>();
        cut.WaitForState(() => cut.Markup.Contains("Task in Workspace 1"));
        Assert.Contains("Task in Workspace 1", cut.Markup);

        // Switch to Workspace 2; Review should automatically reload and display Task in Workspace 2
        _workspaceState.SetActiveWorkspace(new Workspace { Id = 2, Name = "WS2" });

        cut.WaitForState(() => cut.Markup.Contains("Task in Workspace 2"));
        Assert.Contains("Task in Workspace 2", cut.Markup);
    }
}
