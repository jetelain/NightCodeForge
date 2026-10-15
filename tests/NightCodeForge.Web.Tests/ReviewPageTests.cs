using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NightCodeForge.Core.Domain;
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
        _reviewService = new TaskReviewService(_taskRepository, _workspaceRepository, NullLogger<TaskReviewService>.Instance);
        _orchestrator = new MockTaskOrchestrator();
        _commentState = new ReviewCommentStateService();
        _toastService = new ToastService();
        _workspaceState = new WorkspaceStateService();

        Services.AddSingleton<ITaskRepository>(_taskRepository);
        Services.AddSingleton<ITaskReviewService>(_reviewService);
        Services.AddSingleton<ITaskOrchestrator>(_orchestrator);
        Services.AddSingleton(_commentState);
        Services.AddSingleton(_toastService);
        Services.AddSingleton(_workspaceState);
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
        bag.Set("coverage.baseline.line_rate", 0.75);
        bag.Set("coverage.current.line_rate", 0.82);
        bag.Set("warnings.baseline.total", 4);
        bag.Set("warnings.current.total", 2);
        bag.Set("tests.current.total", 30);
        bag.Set("tests.current.passed", 30);
        bag.Set("tests.current.failed", 0);
        bag.Set("tests.current.duration_ms", 850);

        var files = new List<FileDiffSummary>
        {
            new("src/Core/Parser.cs", FileChangeType.Modified, 5, 2, "old line", "new line")
        };
        bag.Set("ReviewFiles", files);

        var task = new ForgeTask
        {
            Id = 55,
            Title = "Refactor JSON Parser",
            Status = TaskOverallStatus.Ready,
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
}
