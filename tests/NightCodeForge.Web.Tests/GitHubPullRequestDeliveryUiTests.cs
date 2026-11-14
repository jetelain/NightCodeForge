using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Security;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Web.Components.Backlog;
using NightCodeForge.Web.Components.Ideation;
using NightCodeForge.Web.Components.Pages;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class GitHubPullRequestDeliveryUiTests : BunitContext
{
    private readonly InMemoryTaskRepository _taskRepository;
    private readonly InMemoryWorkspaceRepository _workspaceRepository;
    private readonly InMemoryTaskDependencyStore _depStore;
    private readonly Mock<IGitHubPullRequestDeliveryService> _prDeliveryMock;
    private readonly Mock<IGitHubTokenStore> _tokenStoreMock;
    private readonly MockTaskOrchestrator _orchestrator;
    private readonly MockTaskMarkdownSyncService _markdownSync;
    private readonly WorkspaceStateService _workspaceState;
    private readonly ToastService _toastService;
    private readonly TaskReviewService _reviewService;

    public GitHubPullRequestDeliveryUiTests()
    {
        _taskRepository = new InMemoryTaskRepository();
        _workspaceRepository = new InMemoryWorkspaceRepository();
        _depStore = new InMemoryTaskDependencyStore();
        _prDeliveryMock = new Mock<IGitHubPullRequestDeliveryService>();
        _tokenStoreMock = new Mock<IGitHubTokenStore>();
        _orchestrator = new MockTaskOrchestrator();
        _markdownSync = new MockTaskMarkdownSyncService();
        _workspaceState = new WorkspaceStateService();
        _toastService = TestServices.CreateToastService();

        _reviewService = new TaskReviewService(
            _taskRepository,
            _workspaceRepository,
            new Mock<IGitWorkspaceManager>().Object,
            _prDeliveryMock.Object,
            _orchestrator,
            _markdownSync,
            NullLogger<TaskReviewService>.Instance);

        Services.AddSingleton<ITaskRepository>(_taskRepository);
        Services.AddSingleton<IWorkspaceRepository>(_workspaceRepository);
        Services.AddSingleton<IWorkflowRepository>(new InMemoryWorkflowRepository());
        Services.AddSingleton<ITaskDependencyStore>(_depStore);
        Services.AddSingleton<IDependencyResolutionEngine>(new DependencyResolutionEngine(_depStore));
        Services.AddSingleton<IGitHubPullRequestDeliveryService>(_prDeliveryMock.Object);
        Services.AddSingleton<IGitHubTokenStore>(_tokenStoreMock.Object);
        Services.AddSingleton<IOptions<SecurityOptions>>(Options.Create(new SecurityOptions { AirGappedMode = false }));
        Services.AddSingleton<ITaskOrchestrator>(_orchestrator);
        Services.AddSingleton<ITaskJobQueue>(new Mock<ITaskJobQueue>().Object);
        Services.AddSingleton<ITaskMarkdownSyncService>(_markdownSync);
        Services.AddSingleton<ITaskReviewService>(_reviewService);
        Services.AddSingleton<ITaskDiffService>(new TaskDiffService(_reviewService));
        Services.AddSingleton<IDevQueueService>(new MockDevQueueService());
        Services.AddSingleton<ITaskHistoryDiffService, TaskHistoryDiffService>();
        Services.AddSingleton<ITaskMutationService>(new TaskMutationService(
            _taskRepository,
            _workspaceRepository,
            _markdownSync,
            _orchestrator,
            new Mock<IOperationsTelemetryService>().Object,
            NullLogger<TaskMutationService>.Instance));
        Services.AddSingleton(_workspaceState);
        Services.AddSingleton(_toastService);
        Services.AddSingleton(TestHubConnectionHelper.Create());
        Services.AddSingleton(new ReviewCommentStateService());
        Services.AddSingleton(WorkerStateTestHelper.Create(new Mock<IOperationsTelemetryService>().Object));
        Services.AddSingleton<ISyntaxHighlightingService>(new SimpleSyntaxHighlighter());
        Services.AddSingleton<IDagLayoutEngine, DagLayoutEngine>();
    }

    [Fact]
    public void KanbanCard_And_DagVisualizer_RenderExternalPullRequestBadge()
    {
        var task = new ForgeTask
        {
            Id = 42,
            WorkspaceId = 1,
            Title = "External PR Feature",
            Status = TaskOverallStatus.Ready,
            Priority = TaskPriority.Normal,
            MergeMode = MergeMode.ExternalPullRequest
        };

        var cardCut = Render<KanbanCard>(p => p.Add(c => c.Task, task));
        var mergeBadge = cardCut.Find(".badge-purple");
        Assert.Contains("PR", mergeBadge.TextContent);

        var dagCut = Render<DagVisualizer>(p => p
            .Add(d => d.Tasks, new List<ForgeTask> { task })
            .Add(d => d.Dependencies, Array.Empty<(int TaskId, int DependsOnTaskId)>()));
        Assert.Contains("PR", dagCut.Markup);
    }

    [Fact]
    public async Task CreateTask_WhenPullRequestDeliveryDisabled_HidesExternalPullRequestOption()
    {
        var ws = await _workspaceRepository.CreateAsync(new Workspace
        {
            Id = 1,
            Name = "Local Workspace",
            RepositoryPath = "/repo/local",
            GitHubSettings = new GitHubIntegrationSettings
            {
                Enabled = false,
                PullRequestDeliveryEnabled = false
            }
        });
        _workspaceState.SetKnownWorkspaces(new[] { ws });
        _workspaceState.SetActiveWorkspace(ws);
        _prDeliveryMock.Setup(s => s.IsPullRequestDeliveryAvailableAsync(It.IsAny<Workspace?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var cut = Render<CreateTask>();

        Assert.Equal(2, cut.FindAll(".mergemode-card").Count);
        Assert.DoesNotContain("External Pull Request (GitHub)", cut.Markup);
    }

    [Fact]
    public async Task CreateTask_WhenPullRequestDeliveryEnabledAndAuthenticated_ShowsExternalPullRequestOption()
    {
        var ws = await _workspaceRepository.CreateAsync(new Workspace
        {
            Id = 1,
            Name = "GitHub Workspace",
            RepositoryPath = "/repo/gh",
            GitHubSettings = new GitHubIntegrationSettings
            {
                Enabled = true,
                PullRequestDeliveryEnabled = true,
                RepositoryOwner = "acme",
                RepositoryName = "demo"
            }
        });
        _workspaceState.SetKnownWorkspaces(new[] { ws });
        _workspaceState.SetActiveWorkspace(ws);
        _tokenStoreMock.Setup(t => t.HasTokenAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _prDeliveryMock.Setup(s => s.IsPullRequestDeliveryAvailableAsync(It.IsAny<Workspace?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var cut = Render<CreateTask>();

        Assert.Equal(3, cut.FindAll(".mergemode-card").Count);
        Assert.Contains("External Pull Request (GitHub)", cut.Markup);
    }

    [Fact]
    public async Task ReviewPage_WithExternalPullRequestTask_RendersOpenPrLink_SyncPr_RequestChanges_AndCloseMergedPr()
    {
        var ws = await _workspaceRepository.CreateAsync(new Workspace
        {
            Id = 1,
            Name = "GitHub Workspace",
            RepositoryPath = "/repo/gh",
            GitHubSettings = new GitHubIntegrationSettings
            {
                Enabled = true,
                PullRequestDeliveryEnabled = true,
                RepositoryOwner = "acme",
                RepositoryName = "demo"
            }
        });
        _workspaceState.SetKnownWorkspaces(new[] { ws });
        _workspaceState.SetActiveWorkspace(ws);
        _prDeliveryMock.Setup(s => s.IsPullRequestDeliveryAvailableAsync(It.IsAny<Workspace?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var bag = new PipelineDataBag();
        bag.Set(PipelineDataBagKeys.ReviewFiles, new List<FileDiffSummary>
        {
            new("src/App.cs", FileChangeType.Modified, 4, 1, "old", "new")
        });

        await _taskRepository.CreateAsync(new ForgeTask
        {
            Id = 450,
            WorkspaceId = 1,
            Title = "Deliver PR Feature",
            Status = TaskOverallStatus.Ready,
            HasExecutionRun = true,
            MergeMode = MergeMode.ExternalPullRequest,
            PullRequestNumber = 45,
            PullRequestUrl = "https://github.com/acme/demo/pull/45",
            BaseBranch = "main",
            FeatureBranch = "feature/task-450",
            PipelineDataJson = bag.ToJson()
        });

        var cut = Render<Review>(p => p.Add(r => r.TaskId, 450));

        var openPrLink = cut.Find("#btn-open-github-pr");
        Assert.Equal("https://github.com/acme/demo/pull/45", openPrLink.GetAttribute("href"));
        Assert.Contains("Open GitHub Pull Request (#45)", openPrLink.TextContent);

        var syncBtn = cut.Find("#btn-create-or-sync-pr");
        Assert.Contains("Sync Pull Request", syncBtn.TextContent);

        var closeMergedBtn = cut.Find("#btn-close-merged-pr");
        Assert.Contains("Close Merged PR", closeMergedBtn.TextContent);

        Assert.NotNull(cut.Find("#btn-request-changes"));
        Assert.NotNull(cut.Find("#btn-approve-merge"));
    }

    [Fact]
    public async Task ReviewPage_WhenExternalPullRequestUrlIsNull_RendersCreateGitHubPullRequestButton()
    {
        var ws = await _workspaceRepository.CreateAsync(new Workspace
        {
            Id = 1,
            Name = "GitHub Workspace",
            RepositoryPath = "/repo/gh",
            GitHubSettings = new GitHubIntegrationSettings
            {
                Enabled = true,
                PullRequestDeliveryEnabled = true,
                RepositoryOwner = "acme",
                RepositoryName = "demo"
            }
        });
        _workspaceState.SetKnownWorkspaces(new[] { ws });
        _workspaceState.SetActiveWorkspace(ws);
        _prDeliveryMock.Setup(s => s.IsPullRequestDeliveryAvailableAsync(It.IsAny<Workspace?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var bag = new PipelineDataBag();
        bag.Set(PipelineDataBagKeys.ReviewFiles, new List<FileDiffSummary>
        {
            new("src/App.cs", FileChangeType.Modified, 4, 1, "old", "new")
        });

        await _taskRepository.CreateAsync(new ForgeTask
        {
            Id = 451,
            WorkspaceId = 1,
            Title = "Uncreated PR Feature",
            Status = TaskOverallStatus.Ready,
            HasExecutionRun = true,
            MergeMode = MergeMode.ExternalPullRequest,
            PullRequestNumber = null,
            PullRequestUrl = null,
            BaseBranch = "main",
            FeatureBranch = "feature/task-451",
            PipelineDataJson = bag.ToJson()
        });

        var cut = Render<Review>(p => p.Add(r => r.TaskId, 451));

        var createBtn = cut.Find("#btn-create-or-sync-pr");
        Assert.Contains("Create GitHub Pull Request", createBtn.TextContent);
        Assert.Empty(cut.FindAll("#btn-open-github-pr"));
        Assert.Empty(cut.FindAll("#btn-close-merged-pr"));
    }

    [Fact]
    public async Task TaskDetail_And_TaskDetailDrawer_RenderPullRequestActionsAndBadge()
    {
        var ws = await _workspaceRepository.CreateAsync(new Workspace
        {
            Id = 1,
            Name = "GitHub Workspace",
            RepositoryPath = "/repo/gh",
            GitHubSettings = new GitHubIntegrationSettings
            {
                Enabled = true,
                PullRequestDeliveryEnabled = true,
                RepositoryOwner = "acme",
                RepositoryName = "demo"
            }
        });
        _workspaceState.SetKnownWorkspaces(new[] { ws });
        _workspaceState.SetActiveWorkspace(ws);
        _tokenStoreMock.Setup(t => t.HasTokenAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _prDeliveryMock.Setup(s => s.IsPullRequestDeliveryAvailableAsync(It.IsAny<Workspace?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var task = await _taskRepository.CreateAsync(new ForgeTask
        {
            Id = 452,
            WorkspaceId = 1,
            Title = "Detail PR Task",
            Status = TaskOverallStatus.Ready,
            MergeMode = MergeMode.ExternalPullRequest,
            PullRequestNumber = 45,
            PullRequestUrl = "https://github.com/acme/demo/pull/45"
        });

        var detailCut = Render<TaskDetail>(p => p.Add(t => t.TaskId, 452));
        Assert.Contains("Open GitHub Pull Request (#45)", detailCut.Markup);
        Assert.NotNull(detailCut.Find("#btn-close-merged-pr"));

        var drawerCut = Render<TaskDetailDrawer>(p => p
            .Add(d => d.IsOpen, true)
            .Add(d => d.Task, task));
        Assert.Contains("Open GitHub Pull Request (#45)", drawerCut.Markup);
        Assert.NotNull(drawerCut.Find("#btn-drawer-close-merged-pr"));
    }

    [Fact]
    public async Task IdeationProposalCard_And_Promotion_SupportMergeModeExternalPullRequest()
    {
        var ws = await _workspaceRepository.CreateAsync(new Workspace
        {
            Id = 1,
            Name = "GitHub Workspace",
            RepositoryPath = "/repo/gh",
            GitHubSettings = new GitHubIntegrationSettings
            {
                Enabled = false,
                PullRequestDeliveryEnabled = false
            }
        });
        _workspaceState.SetKnownWorkspaces(new[] { ws });
        _workspaceState.SetActiveWorkspace(ws);
        _prDeliveryMock.Setup(s => s.IsPullRequestDeliveryAvailableAsync(It.IsAny<Workspace?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var proposalVm = IdeationProposalViewModel.FromProposal(
            new IdeationProposal("Add PR Mode Support", "Scope details", TaskPriority.High));

        // 1. When PR delivery is disabled, ExternalPullRequest option is hidden
        var disabledCut = Render<IdeationProposalCard>(p => p.Add(c => c.Proposal, proposalVm));
        var disabledSelect = disabledCut.Find(".proposal-mergemode-select");
        Assert.DoesNotContain("ExternalPullRequest", disabledSelect.InnerHtml);

        // 2. When PR delivery is enabled, ExternalPullRequest option is rendered and selectable
        ws.GitHubSettings.Enabled = true;
        ws.GitHubSettings.PullRequestDeliveryEnabled = true;
        _prDeliveryMock.Setup(s => s.IsPullRequestDeliveryAvailableAsync(It.IsAny<Workspace?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var enabledCut = Render<IdeationProposalCard>(p => p.Add(c => c.Proposal, proposalVm));
        var enabledSelect = enabledCut.Find(".proposal-mergemode-select");
        Assert.Contains("External Pull Request (GitHub)", enabledSelect.InnerHtml);

        enabledSelect.Change(MergeMode.ExternalPullRequest.ToString());
        Assert.Equal(MergeMode.ExternalPullRequest, proposalVm.MergeMode);

        // 3. Promoting the proposal preserves MergeMode.ExternalPullRequest
        var workflowRepo = Services.GetRequiredService<IWorkflowRepository>();
        var createdTask = await IdeationPresentationHelper.PromoteOrCreateProposalTaskAsync(
            ws,
            proposalVm,
            _taskRepository,
            workflowRepo);

        Assert.Equal(MergeMode.ExternalPullRequest, createdTask.MergeMode);
    }

    [Fact]
    public void MergeModeHelper_GetBadgeLabel_And_GetSvgPillColor_ReturnExpectedValuesForAllModes()
    {
        Assert.Equal("Auto-Merge", MergeModeHelper.GetBadgeLabel(MergeMode.Automatic));
        Assert.Equal("Manual Review", MergeModeHelper.GetBadgeLabel(MergeMode.ManualReviewRequired));
        Assert.Equal("External PR", MergeModeHelper.GetBadgeLabel(MergeMode.ExternalPullRequest));

        Assert.Equal("#5eead4", MergeModeHelper.GetSvgPillColor(MergeMode.Automatic));
        Assert.Equal("#a5b4fc", MergeModeHelper.GetSvgPillColor(MergeMode.ManualReviewRequired));
        Assert.Equal("#c084fc", MergeModeHelper.GetSvgPillColor(MergeMode.ExternalPullRequest));
    }
}
