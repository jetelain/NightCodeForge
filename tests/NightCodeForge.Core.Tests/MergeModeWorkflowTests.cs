using NightCodeForge.Core.Concurrency;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Core.Tests;

public class MergeModeWorkflowTests
{
    private readonly InMemoryTaskRepository _taskRepo;
    private readonly InMemoryWorkspaceRepository _workspaceRepo;
    private readonly InMemoryTaskDependencyStore _depStore;
    private readonly DependencyResolutionEngine _depEngine;
    private readonly WorkspaceLockProvider _lockProvider;
    private readonly FakeGitWorkspaceManager _gitManager;
    private readonly FakeTaskJobQueue _jobQueue;
    private readonly Workspace _workspace;

    public MergeModeWorkflowTests()
    {
        _taskRepo = new InMemoryTaskRepository();
        _workspaceRepo = new InMemoryWorkspaceRepository();
        _depStore = new InMemoryTaskDependencyStore();
        _depEngine = new DependencyResolutionEngine(_depStore);
        _lockProvider = new WorkspaceLockProvider();
        _gitManager = new FakeGitWorkspaceManager();
        _jobQueue = new FakeTaskJobQueue();

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
        // ForgeTask 501 (Automatic merge mode)
        // ForgeTask 502 depends on 501
        var task501 = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 501,
            WorkspaceId = 1,
            Title = "Auto-merge Feature",
            Status = TaskOverallStatus.Implemented,
            CurrentStageId = "final-stage",
            BaseBranch = "main",
            MergeMode = MergeMode.Automatic
        });

        var task502 = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 502,
            WorkspaceId = 1,
            Title = "Dependent on 501",
            Status = TaskOverallStatus.Open
        });

        _depStore.AddTask(501, 1, TaskOverallStatus.Implemented);
        _depStore.AddTask(502, 1, TaskOverallStatus.Open);
        _depStore.AddDependency(1, 502, 501);

        var finalStage = new TestWorkflowStage("final-stage")
        {
            Handler = (ctx, ct) => Task.FromResult(StageExecutionResult.Success())
        };
        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { finalStage });

        var orchestrator = new TaskOrchestrator(
            _taskRepo, _workspaceRepo, _depEngine, _lockProvider,
            _gitManager, new DefaultWorkflowPipelineProvider(pipeline), _jobQueue, _depStore);

        // Act: Process final stage
        await orchestrator.ProcessTaskAsync(501);

        // 1. ForgeTask 501 automatically merged and marked Closed
        var updated501 = await _taskRepo.GetByIdAsync(501);
        Assert.NotNull(updated501);
        Assert.Equal(TaskOverallStatus.Closed, updated501.Status);

        // 2. Git squash merge performed
        Assert.Single(_gitManager.SquashMerges);
        Assert.Equal(501, _gitManager.SquashMerges[0].TaskId);

        // 3. Dependent task 502 unblocked and enqueued to inference
        Assert.Contains(502, _jobQueue.InferenceQueue);
    }

    [Fact]
    public async Task FinalStage_WithManualReviewRequired_PausesAtReadyAwaitingMaintainerApproval()
    {
        // ForgeTask 601 (ManualReviewRequired merge mode)
        // ForgeTask 602 depends on 601
        var task601 = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 601,
            WorkspaceId = 1,
            Title = "Manual Review Feature",
            Status = TaskOverallStatus.Implemented,
            CurrentStageId = "final-stage",
            BaseBranch = "main",
            MergeMode = MergeMode.ManualReviewRequired
        });

        var task602 = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 602,
            WorkspaceId = 1,
            Title = "Dependent on 601",
            Status = TaskOverallStatus.Open
        });

        _depStore.AddTask(601, 1, TaskOverallStatus.Implemented);
        _depStore.AddTask(602, 1, TaskOverallStatus.Open);
        _depStore.AddDependency(1, 602, 601);

        _gitManager.GeneratedDiff = "diff --git a/Feature.cs b/Feature.cs\n+new line";

        var finalStage = new TestWorkflowStage("final-stage")
        {
            Handler = (ctx, ct) => Task.FromResult(StageExecutionResult.Success())
        };
        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { finalStage });

        var orchestrator = new TaskOrchestrator(
            _taskRepo, _workspaceRepo, _depEngine, _lockProvider,
            _gitManager, new DefaultWorkflowPipelineProvider(pipeline), _jobQueue, _depStore);

        // Act: Process final stage
        await orchestrator.ProcessTaskAsync(601);

        // 1. ForgeTask 601 transitions to Ready, NOT Closed
        var updated601 = await _taskRepo.GetByIdAsync(601);
        Assert.NotNull(updated601);
        Assert.Equal(TaskOverallStatus.Ready, updated601.Status);

        // 2. Review diff saved
        var bag = PipelineDataBag.FromJson(updated601.PipelineDataJson);
        Assert.Equal(_gitManager.GeneratedDiff, bag.Get<string>("ReviewDiff"));

        // 3. No git squash merge executed yet
        Assert.Empty(_gitManager.SquashMerges);

        // 4. Dependent task 602 NOT yet unblocked
        Assert.DoesNotContain(602, _jobQueue.InferenceQueue);

        // Act: Maintainer manually approves in Review Room
        await orchestrator.CompleteTaskAndCascadeAsync(601, "Merge branch 'feat/task-601' into main");

        // 5. Now marked Closed and squash merged
        var closed601 = await _taskRepo.GetByIdAsync(601);
        Assert.NotNull(closed601);
        Assert.Equal(TaskOverallStatus.Closed, closed601.Status);
        Assert.Single(_gitManager.SquashMerges);

        // 6. Dependent task 602 unblocked now
        Assert.Contains(602, _jobQueue.InferenceQueue);
    }

    [Fact]
    public async Task PromoteSuggestedTaskAsync_WhenPromotedToOpenWithDependenciesClosed_EnqueuesToInference()
    {
        var prereq = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 701,
            WorkspaceId = 1,
            Status = TaskOverallStatus.Closed
        });
        _depStore.AddTask(701, 1, TaskOverallStatus.Closed);

        var suggested = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 702,
            WorkspaceId = 1,
            Title = "Suggested Feature",
            Status = TaskOverallStatus.Suggested
        }, dependsOn: new[] { 701 });

        _depStore.AddTask(702, 1, TaskOverallStatus.Suggested);
        _depStore.AddDependency(1, 702, 701);

        var orchestrator = new TaskOrchestrator(
            _taskRepo, _workspaceRepo, _depEngine, _lockProvider,
            _gitManager, new DefaultWorkflowPipelineProvider(), _jobQueue, _depStore);

        // Act: Maintainer promotes Suggested -> Open
        await orchestrator.PromoteSuggestedTaskAsync(
            taskId: 702,
            targetStatus: TaskOverallStatus.Open,
            priority: 2,
            mergeMode: MergeMode.Automatic);

        var updated = await _taskRepo.GetByIdAsync(702);
        Assert.NotNull(updated);
        Assert.Equal(TaskOverallStatus.Open, updated.Status);
        Assert.Equal(2, updated.Priority);
        Assert.Equal(MergeMode.Automatic, updated.MergeMode);

        // Because prerequisite 701 is Closed, 702 is ready and enqueued!
        Assert.Contains(702, _jobQueue.InferenceQueue);
    }

    [Fact]
    public async Task PromoteSuggestedTaskAsync_WhenPromotedToOpenWithUnfinishedPrerequisites_DoesNotEnqueue()
    {
        var prereq = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 801,
            WorkspaceId = 1,
            Status = TaskOverallStatus.Open // Not yet closed!
        });
        _depStore.AddTask(801, 1, TaskOverallStatus.Open);

        var suggested = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 802,
            WorkspaceId = 1,
            Title = "Suggested Feature 2",
            Status = TaskOverallStatus.Suggested
        }, dependsOn: new[] { 801 });

        _depStore.AddTask(802, 1, TaskOverallStatus.Suggested);
        _depStore.AddDependency(1, 802, 801);

        var orchestrator = new TaskOrchestrator(
            _taskRepo, _workspaceRepo, _depEngine, _lockProvider,
            _gitManager, new DefaultWorkflowPipelineProvider(), _jobQueue, _depStore);

        // Act: Maintainer promotes Suggested -> Open
        await orchestrator.PromoteSuggestedTaskAsync(
            taskId: 802,
            targetStatus: TaskOverallStatus.Open,
            priority: 1,
            mergeMode: MergeMode.ManualReviewRequired);

        var updated = await _taskRepo.GetByIdAsync(802);
        Assert.NotNull(updated);
        Assert.Equal(TaskOverallStatus.Open, updated.Status);

        // Prerequisite 801 is not closed yet, so 802 must NOT be enqueued
        Assert.DoesNotContain(802, _jobQueue.InferenceQueue);
    }

    [Fact]
    public async Task PromoteSuggestedTaskAsync_WhenRejected_SetsStatusRejectedAndDoesNotEnqueue()
    {
        var suggested = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 901,
            WorkspaceId = 1,
            Title = "Out of scope feature",
            Status = TaskOverallStatus.Suggested
        });
        _depStore.AddTask(901, 1, TaskOverallStatus.Suggested);

        var orchestrator = new TaskOrchestrator(
            _taskRepo, _workspaceRepo, _depEngine, _lockProvider,
            _gitManager, new DefaultWorkflowPipelineProvider(), _jobQueue, _depStore);

        // Act: Maintainer rejects task
        await orchestrator.PromoteSuggestedTaskAsync(
            taskId: 901,
            targetStatus: TaskOverallStatus.Rejected,
            priority: 5,
            mergeMode: MergeMode.ManualReviewRequired);

        var updated = await _taskRepo.GetByIdAsync(901);
        Assert.NotNull(updated);
        Assert.Equal(TaskOverallStatus.Rejected, updated.Status);
        Assert.DoesNotContain(901, _jobQueue.InferenceQueue);
    }
}
