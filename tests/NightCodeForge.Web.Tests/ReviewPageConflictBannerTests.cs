using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Web.Components.Pages;
using NightCodeForge.Web.Components.Review;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class ReviewPageConflictBannerTests : BunitContext
{
    private readonly InMemoryTaskRepository _taskRepository;
    private readonly InMemoryWorkspaceRepository _workspaceRepository;
    private readonly TaskReviewService _reviewService;
    private readonly MockTaskOrchestrator _orchestrator;
    private readonly ReviewCommentStateService _commentState;
    private readonly ToastService _toastService;
    private readonly WorkspaceStateService _workspaceState;
    private readonly Mock<ITaskMutationService> _mutationService;

    public ReviewPageConflictBannerTests()
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
        _mutationService = new Mock<ITaskMutationService>();

        Services.AddSingleton<ITaskRepository>(_taskRepository);
        Services.AddSingleton<ITaskReviewService>(_reviewService);
        Services.AddSingleton<ITaskOrchestrator>(_orchestrator);
        Services.AddSingleton<ITaskMutationService>(_mutationService.Object);
        Services.AddSingleton(_commentState);
        Services.AddSingleton(_toastService);
        Services.AddSingleton(_workspaceState);
        Services.AddSingleton<ISyntaxHighlightingService>(new SimpleSyntaxHighlighter());
    }

    [Fact]
    public async Task ReviewRoom_HasResolvedConflictTrue_RendersConflictResolutionBanner()
    {
        // Arrange
        var bag = new PipelineDataBag();
        bag.Set(PipelineDataBagKeys.HasResolvedConflict, true);
        bag.Set(PipelineDataBagKeys.CoverageBaselineLineRate, 0.80);
        bag.Set(PipelineDataBagKeys.CoverageCurrentLineRate, 0.85);

        var task = new ForgeTask
        {
            Id = 55,
            WorkspaceId = 1,
            Title = "Conflict Resolved Task",
            Status = TaskOverallStatus.Ready,
            BaseBranch = "main",
            FeatureBranch = "feature/task-55",
            PipelineDataJson = bag.ToJson()
        };
        await _taskRepository.CreateAsync(task);

        // Act
        var cut = Render<Review>(parameters => parameters.Add(p => p.TaskId, 55));

        // Assert
        var banner = cut.Find("#alert-re-review-conflict");
        Assert.NotNull(banner);
        Assert.Contains(ConflictResolutionConstants.ReReviewBannerTitle, banner.TextContent);
        Assert.Contains("main", banner.TextContent);
    }

    [Fact]
    public async Task ReviewRoom_HasResolvedConflictFalse_DoesNotRenderBanner()
    {
        // Arrange
        var task = new ForgeTask
        {
            Id = 56,
            WorkspaceId = 1,
            Title = "Normal Task",
            Status = TaskOverallStatus.Ready,
            BaseBranch = "main",
            FeatureBranch = "feature/task-56"
        };
        await _taskRepository.CreateAsync(task);

        // Act
        var cut = Render<Review>(parameters => parameters.Add(p => p.TaskId, 56));

        // Assert
        Assert.Empty(cut.FindAll("#alert-re-review-conflict"));
    }

    [Fact]
    public void ApproveMergeModal_ConflictResolutionDispatched_ShowsToastAndCloses()
    {
        // Arrange
        _orchestrator.CompleteResult = new CompleteTaskResult(
            CompleteTaskStatus.ConflictResolutionDispatched,
            ConflictedFiles: new[] { "conflict.cs" });

        bool approvedInvoked = false;
        bool closedInvoked = false;

        var cut = Render<ApproveMergeModal>(p => p
            .Add(m => m.TaskId, 77)
            .Add(m => m.BaseBranch, "main")
            .Add(m => m.OnApproved, () => { approvedInvoked = true; })
            .Add(m => m.OnClosed, () => { closedInvoked = true; }));

        // Act
        var confirmBtn = cut.Find("#btn-confirm-approve-merge");
        confirmBtn.Click();

        // Assert
        Assert.True(approvedInvoked);
        Assert.True(closedInvoked);
    }

    [Fact]
    public async Task ReviewRoom_ApproveModal_PassesEvaluatedTaskTitle()
    {
        var task = new ForgeTask
        {
            Id = 57,
            WorkspaceId = 1,
            Title = "Real Evaluated Title",
            Status = TaskOverallStatus.Ready,
            HasExecutionRun = true,
            BaseBranch = "main",
            FeatureBranch = "feature/task-57"
        };
        await _taskRepository.CreateAsync(task);

        var cut = Render<Review>(parameters => parameters.Add(p => p.TaskId, 57));

        var approveBtn = cut.Find("#btn-approve-merge");
        approveBtn.Click();

        var modal = cut.FindComponent<ApproveMergeModal>();
        Assert.NotNull(modal);
        Assert.Equal("Real Evaluated Title", modal.Instance.TaskTitle);
        Assert.Equal("main", modal.Instance.BaseBranch);
        Assert.DoesNotContain("_snapshot.Title", cut.Markup);
        Assert.DoesNotContain("_snapshot.BaseBranch", cut.Markup);
        Assert.Contains("Merge task #57: Real Evaluated Title", cut.Markup);
    }
}
