using NightCodeForge.Core.Concurrency;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Core.Tests;

public class MergeModeWorkflowTests
{
    private readonly InMemoryIssueRepository _issueRepo;
    private readonly InMemoryWorkspaceRepository _workspaceRepo;
    private readonly InMemoryIssueDependencyStore _depStore;
    private readonly DependencyResolutionEngine _depEngine;
    private readonly WorkspaceLockProvider _lockProvider;
    private readonly FakeGitWorkspaceManager _gitManager;
    private readonly FakeIssueJobQueue _jobQueue;
    private readonly Workspace _workspace;

    public MergeModeWorkflowTests()
    {
        _issueRepo = new InMemoryIssueRepository();
        _workspaceRepo = new InMemoryWorkspaceRepository();
        _depStore = new InMemoryIssueDependencyStore();
        _depEngine = new DependencyResolutionEngine(_depStore);
        _lockProvider = new WorkspaceLockProvider();
        _gitManager = new FakeGitWorkspaceManager();
        _jobQueue = new FakeIssueJobQueue();

        _workspace = new Workspace
        {
            Id = 1,
            Name = "MergeMode Test Workspace",
            RepositoryPath = "C:/test/repo",
            DefaultBaseBranch = "main"
        };
        _workspaceRepo.CreateAsync(_workspace).GetAwaiter().GetResult();
    }

    [Fact]
    public async Task FinalStage_WithAutomaticMergeMode_AutoSquashesMergesToClosedAndCascades()
    {
        // Issue 501 (Automatic merge mode)
        // Issue 502 depends on 501
        var issue501 = await _issueRepo.CreateAsync(new Issue
        {
            Id = 501,
            WorkspaceId = 1,
            Title = "Auto-merge Feature",
            Status = IssueOverallStatus.Implemented,
            CurrentStageId = "final-stage",
            BaseBranch = "main",
            MergeMode = MergeMode.Automatic
        });

        var issue502 = await _issueRepo.CreateAsync(new Issue
        {
            Id = 502,
            WorkspaceId = 1,
            Title = "Dependent on 501",
            Status = IssueOverallStatus.Open
        });

        _depStore.AddIssue(501, 1, IssueOverallStatus.Implemented);
        _depStore.AddIssue(502, 1, IssueOverallStatus.Open);
        _depStore.AddDependency(1, 502, 501);

        var finalStage = new TestWorkflowStage("final-stage")
        {
            Handler = (ctx, ct) => Task.FromResult(StageExecutionResult.Success())
        };
        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { finalStage });

        var orchestrator = new IssueOrchestrator(
            _issueRepo, _workspaceRepo, _depEngine, _lockProvider,
            _gitManager, new DefaultWorkflowPipelineProvider(pipeline), _jobQueue, _depStore);

        // Act: Process final stage
        await orchestrator.ProcessIssueAsync(501);

        // 1. Issue 501 automatically merged and marked Closed
        var updated501 = await _issueRepo.GetByIdAsync(501);
        Assert.NotNull(updated501);
        Assert.Equal(IssueOverallStatus.Closed, updated501.Status);

        // 2. Git squash merge performed
        Assert.Single(_gitManager.SquashMerges);
        Assert.Equal(501, _gitManager.SquashMerges[0].IssueId);

        // 3. Dependent issue 502 unblocked and enqueued to inference
        Assert.Contains(502, _jobQueue.InferenceQueue);
    }

    [Fact]
    public async Task FinalStage_WithManualReviewRequired_PausesAtReadyAwaitingMaintainerApproval()
    {
        // Issue 601 (ManualReviewRequired merge mode)
        // Issue 602 depends on 601
        var issue601 = await _issueRepo.CreateAsync(new Issue
        {
            Id = 601,
            WorkspaceId = 1,
            Title = "Manual Review Feature",
            Status = IssueOverallStatus.Implemented,
            CurrentStageId = "final-stage",
            BaseBranch = "main",
            MergeMode = MergeMode.ManualReviewRequired
        });

        var issue602 = await _issueRepo.CreateAsync(new Issue
        {
            Id = 602,
            WorkspaceId = 1,
            Title = "Dependent on 601",
            Status = IssueOverallStatus.Open
        });

        _depStore.AddIssue(601, 1, IssueOverallStatus.Implemented);
        _depStore.AddIssue(602, 1, IssueOverallStatus.Open);
        _depStore.AddDependency(1, 602, 601);

        _gitManager.GeneratedDiff = "diff --git a/Feature.cs b/Feature.cs\n+new line";

        var finalStage = new TestWorkflowStage("final-stage")
        {
            Handler = (ctx, ct) => Task.FromResult(StageExecutionResult.Success())
        };
        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { finalStage });

        var orchestrator = new IssueOrchestrator(
            _issueRepo, _workspaceRepo, _depEngine, _lockProvider,
            _gitManager, new DefaultWorkflowPipelineProvider(pipeline), _jobQueue, _depStore);

        // Act: Process final stage
        await orchestrator.ProcessIssueAsync(601);

        // 1. Issue 601 transitions to Ready, NOT Closed
        var updated601 = await _issueRepo.GetByIdAsync(601);
        Assert.NotNull(updated601);
        Assert.Equal(IssueOverallStatus.Ready, updated601.Status);

        // 2. Review diff saved
        var bag = PipelineDataBag.FromJson(updated601.PipelineDataJson);
        Assert.Equal(_gitManager.GeneratedDiff, bag.Get<string>("ReviewDiff"));

        // 3. No git squash merge executed yet
        Assert.Empty(_gitManager.SquashMerges);

        // 4. Dependent issue 602 NOT yet unblocked
        Assert.DoesNotContain(602, _jobQueue.InferenceQueue);

        // Act: Maintainer manually approves in Review Room
        await orchestrator.CompleteIssueAndCascadeAsync(601, "Merge branch 'feat/issue-601' into main");

        // 5. Now marked Closed and squash merged
        var closed601 = await _issueRepo.GetByIdAsync(601);
        Assert.NotNull(closed601);
        Assert.Equal(IssueOverallStatus.Closed, closed601.Status);
        Assert.Single(_gitManager.SquashMerges);

        // 6. Dependent issue 602 unblocked now
        Assert.Contains(602, _jobQueue.InferenceQueue);
    }

    [Fact]
    public async Task PromoteSuggestedIssueAsync_WhenPromotedToOpenWithDependenciesClosed_EnqueuesToInference()
    {
        var prereq = await _issueRepo.CreateAsync(new Issue
        {
            Id = 701,
            WorkspaceId = 1,
            Status = IssueOverallStatus.Closed
        });
        _depStore.AddIssue(701, 1, IssueOverallStatus.Closed);

        var suggested = await _issueRepo.CreateAsync(new Issue
        {
            Id = 702,
            WorkspaceId = 1,
            Title = "Suggested Feature",
            Status = IssueOverallStatus.Suggested
        }, dependsOn: new[] { 701 });

        _depStore.AddIssue(702, 1, IssueOverallStatus.Suggested);
        _depStore.AddDependency(1, 702, 701);

        var orchestrator = new IssueOrchestrator(
            _issueRepo, _workspaceRepo, _depEngine, _lockProvider,
            _gitManager, new DefaultWorkflowPipelineProvider(), _jobQueue, _depStore);

        // Act: Maintainer promotes Suggested -> Open
        await orchestrator.PromoteSuggestedIssueAsync(
            issueId: 702,
            targetStatus: IssueOverallStatus.Open,
            priority: 2,
            mergeMode: MergeMode.Automatic);

        var updated = await _issueRepo.GetByIdAsync(702);
        Assert.NotNull(updated);
        Assert.Equal(IssueOverallStatus.Open, updated.Status);
        Assert.Equal(2, updated.Priority);
        Assert.Equal(MergeMode.Automatic, updated.MergeMode);

        // Because prerequisite 701 is Closed, 702 is ready and enqueued!
        Assert.Contains(702, _jobQueue.InferenceQueue);
    }

    [Fact]
    public async Task PromoteSuggestedIssueAsync_WhenPromotedToOpenWithUnfinishedPrerequisites_DoesNotEnqueue()
    {
        var prereq = await _issueRepo.CreateAsync(new Issue
        {
            Id = 801,
            WorkspaceId = 1,
            Status = IssueOverallStatus.Open // Not yet closed!
        });
        _depStore.AddIssue(801, 1, IssueOverallStatus.Open);

        var suggested = await _issueRepo.CreateAsync(new Issue
        {
            Id = 802,
            WorkspaceId = 1,
            Title = "Suggested Feature 2",
            Status = IssueOverallStatus.Suggested
        }, dependsOn: new[] { 801 });

        _depStore.AddIssue(802, 1, IssueOverallStatus.Suggested);
        _depStore.AddDependency(1, 802, 801);

        var orchestrator = new IssueOrchestrator(
            _issueRepo, _workspaceRepo, _depEngine, _lockProvider,
            _gitManager, new DefaultWorkflowPipelineProvider(), _jobQueue, _depStore);

        // Act: Maintainer promotes Suggested -> Open
        await orchestrator.PromoteSuggestedIssueAsync(
            issueId: 802,
            targetStatus: IssueOverallStatus.Open,
            priority: 1,
            mergeMode: MergeMode.ManualReviewRequired);

        var updated = await _issueRepo.GetByIdAsync(802);
        Assert.NotNull(updated);
        Assert.Equal(IssueOverallStatus.Open, updated.Status);

        // Prerequisite 801 is not closed yet, so 802 must NOT be enqueued
        Assert.DoesNotContain(802, _jobQueue.InferenceQueue);
    }

    [Fact]
    public async Task PromoteSuggestedIssueAsync_WhenRejected_SetsStatusRejectedAndDoesNotEnqueue()
    {
        var suggested = await _issueRepo.CreateAsync(new Issue
        {
            Id = 901,
            WorkspaceId = 1,
            Title = "Out of scope feature",
            Status = IssueOverallStatus.Suggested
        });
        _depStore.AddIssue(901, 1, IssueOverallStatus.Suggested);

        var orchestrator = new IssueOrchestrator(
            _issueRepo, _workspaceRepo, _depEngine, _lockProvider,
            _gitManager, new DefaultWorkflowPipelineProvider(), _jobQueue, _depStore);

        // Act: Maintainer rejects issue
        await orchestrator.PromoteSuggestedIssueAsync(
            issueId: 901,
            targetStatus: IssueOverallStatus.Rejected,
            priority: 5,
            mergeMode: MergeMode.ManualReviewRequired);

        var updated = await _issueRepo.GetByIdAsync(901);
        Assert.NotNull(updated);
        Assert.Equal(IssueOverallStatus.Rejected, updated.Status);
        Assert.DoesNotContain(901, _jobQueue.InferenceQueue);
    }
}
