using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Streaming;
using NightCodeForge.Core.Tests.Fixtures;
using NightCodeForge.Core.Workflow;
using Xunit;

namespace NightCodeForge.Core.Tests.Workflow;

public class TaskMergeAndCascadeCoordinatorTests
{
    private readonly InMemoryTaskRepository _taskRepo = new();
    private readonly InMemoryWorkspaceRepository _workspaceRepo = new();
    private readonly InMemoryTaskDependencyStore _depStore = new();
    private readonly DependencyResolutionEngine _depEngine;
    private readonly FakeTaskJobQueue _jobQueue = new();
    private readonly Mock<ITaskWorkspacePreparer> _preparerMock = new();
    private readonly Mock<IConflictResolutionVerifier> _verifierMock = new();
    private readonly Mock<IAgentActivityStreamer> _streamerMock = new();
    private readonly IWorkflowPipelineProvider _pipelineProvider;
    private readonly Workspace _workspace;

    public TaskMergeAndCascadeCoordinatorTests()
    {
        _depEngine = new DependencyResolutionEngine(_depStore);
        var stages = new IWorkflowStage[]
        {
            new TestWorkflowStage(StandardStageIds.TaskReview, WorkflowStageKind.Agentic) { QueueTier = StageQueueTier.TaskReview },
            new TestWorkflowStage(StandardStageIds.Implementation, WorkflowStageKind.Agentic) { QueueTier = StageQueueTier.Implementation },
            new TestWorkflowStage(StandardStageIds.CodeReviewAndFix, WorkflowStageKind.Agentic) { QueueTier = StageQueueTier.CodeReview }
        };
        _pipelineProvider = new DefaultWorkflowPipelineProvider(new WorkflowPipeline(stages));

        _workspace = new Workspace
        {
            Id = 1,
            Name = "Merge Coordinator WS",
            RepositoryPath = "C:/repo",
            DefaultBaseBranch = "main"
        };
        _workspaceRepo.CreateAsync(_workspace).GetAwaiter().GetResult();

        _preparerMock.Setup(p => p.AcquireWorkspaceLockAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());
        _preparerMock.Setup(p => p.GetWorkspaceAsync(It.IsAny<ForgeTask>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_workspace);
        _preparerMock.Setup(p => p.GetBranchShaAsync(It.IsAny<Workspace>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("base-sha-1");
        _preparerMock.Setup(p => p.SquashAndMergeAsync(It.IsAny<Workspace>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("squash-sha-99");
    }

    private TaskMergeAndCascadeCoordinator CreateSut() => new(
        _taskRepo,
        _preparerMock.Object,
        _depEngine,
        _depStore,
        _jobQueue,
        _pipelineProvider,
        _verifierMock.Object,
        _streamerMock.Object,
        NullLogger<TaskMergeAndCascadeCoordinator>.Instance);

    [Fact]
    public void Constructor_WhenAnyDependencyIsNull_ThrowsArgumentNullException()
    {
        var logger = NullLogger<TaskMergeAndCascadeCoordinator>.Instance;
        Assert.Throws<ArgumentNullException>(() => new TaskMergeAndCascadeCoordinator(null!, _preparerMock.Object, _depEngine, _depStore, _jobQueue, _pipelineProvider, _verifierMock.Object, _streamerMock.Object, logger));
        Assert.Throws<ArgumentNullException>(() => new TaskMergeAndCascadeCoordinator(_taskRepo, null!, _depEngine, _depStore, _jobQueue, _pipelineProvider, _verifierMock.Object, _streamerMock.Object, logger));
        Assert.Throws<ArgumentNullException>(() => new TaskMergeAndCascadeCoordinator(_taskRepo, _preparerMock.Object, null!, _depStore, _jobQueue, _pipelineProvider, _verifierMock.Object, _streamerMock.Object, logger));
        Assert.Throws<ArgumentNullException>(() => new TaskMergeAndCascadeCoordinator(_taskRepo, _preparerMock.Object, _depEngine, null!, _jobQueue, _pipelineProvider, _verifierMock.Object, _streamerMock.Object, logger));
        Assert.Throws<ArgumentNullException>(() => new TaskMergeAndCascadeCoordinator(_taskRepo, _preparerMock.Object, _depEngine, _depStore, null!, _pipelineProvider, _verifierMock.Object, _streamerMock.Object, logger));
        Assert.Throws<ArgumentNullException>(() => new TaskMergeAndCascadeCoordinator(_taskRepo, _preparerMock.Object, _depEngine, _depStore, _jobQueue, null!, _verifierMock.Object, _streamerMock.Object, logger));
        Assert.Throws<ArgumentNullException>(() => new TaskMergeAndCascadeCoordinator(_taskRepo, _preparerMock.Object, _depEngine, _depStore, _jobQueue, _pipelineProvider, null!, _streamerMock.Object, logger));
        Assert.Throws<ArgumentNullException>(() => new TaskMergeAndCascadeCoordinator(_taskRepo, _preparerMock.Object, _depEngine, _depStore, _jobQueue, _pipelineProvider, _verifierMock.Object, null!, logger));
        Assert.Throws<ArgumentNullException>(() => new TaskMergeAndCascadeCoordinator(_taskRepo, _preparerMock.Object, _depEngine, _depStore, _jobQueue, _pipelineProvider, _verifierMock.Object, _streamerMock.Object, null!));
    }

    [Fact]
    public async Task StoreAndQueuePassthroughMethods_DelegateCorrectly()
    {
        var sut = CreateSut();
        var task = new ForgeTask { Id = 1, WorkspaceId = 1, Status = TaskOverallStatus.Open };
        _depStore.AddTask(1, 1, TaskOverallStatus.Open);

        Assert.NotNull(await sut.GetPipelineAsync(task));
        Assert.True(await sut.IsReadyForExecutionAsync(1));

        await sut.SetWaitingForHumanInStoreAsync(1, true);
        Assert.False(await sut.IsReadyForExecutionAsync(1));

        await sut.UpdateTaskStatusInStoreAsync(1, TaskOverallStatus.Closed);
        Assert.Equal(TaskOverallStatus.Closed, await _depStore.GetTaskStatusAsync(1));

        var jobId = sut.EnqueueStage(1, StageQueueTier.TaskReview, TaskPriority.Critical);
        Assert.False(string.IsNullOrWhiteSpace(jobId));
        Assert.True(await sut.HasUrgentJobsWaitingAsync());
    }

    [Fact]
    public async Task EnsureReadyForExecutionAsync_WhenWaitingOrBlockedOrNonExecutable_LogsReasonAndReturnsFalse()
    {
        var sut = CreateSut();
        var task = await _taskRepo.CreateAsync(new ForgeTask { Id = 1, WorkspaceId = 1, Status = TaskOverallStatus.Open });
        _depStore.AddTask(1, 1, TaskOverallStatus.Open);
        await _depStore.SetWaitingForHumanAsync(1, true);

        Assert.False(await sut.EnsureReadyForExecutionAsync(task));
        Assert.Contains(_taskRepo.GetAllLogs(), l => l.TaskId == 1 && l.Message.Contains("waiting for human clarification input"));

        await _depStore.SetWaitingForHumanAsync(1, false);
        await _depStore.UpdateTaskStatusAsync(1, TaskOverallStatus.Closed);
        Assert.False(await sut.EnsureReadyForExecutionAsync(task));
        Assert.Contains(_taskRepo.GetAllLogs(), l => l.TaskId == 1 && l.Message.Contains("not in an executable status"));
    }

    [Fact]
    public async Task VerifyAndHandleImplementationConflictsAsync_WhenVerificationPasses_ClearsConflictKeysAndMarksResolved()
    {
        var sut = CreateSut();
        var task = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 2,
            WorkspaceId = 1,
            Status = TaskOverallStatus.Open,
            BaseBranch = "main"
        });
        var pipelineContext = new PipelineContext(task, _workspace);
        pipelineContext.DataBag.Set(PipelineDataBagKeys.ConflictFiles, new List<string> { "src/App.cs" });
        pipelineContext.DataBag.Set(PipelineDataBagKeys.ConflictResolutionAttempts, 1);

        _verifierMock.Setup(v => v.VerifyResolutionAsync(_workspace, 2, "main", It.IsAny<IReadOnlyList<string>>(), pipelineContext.DataBag, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConflictResolutionVerificationResult(true));

        var pipeline = await _pipelineProvider.GetPipelineAsync(task);
        var outcome = await sut.VerifyAndHandleImplementationConflictsAsync(task, _workspace, pipelineContext, pipeline);

        Assert.Equal(ConflictVerificationOutcome.VerifiedOrNone, outcome);
        Assert.False(pipelineContext.DataBag.ContainsKey(PipelineDataBagKeys.ConflictFiles));
        Assert.True(pipelineContext.DataBag.Get<bool>(PipelineDataBagKeys.HasResolvedConflict));
        Assert.Equal(0, pipelineContext.DataBag.Get<int>(PipelineDataBagKeys.ConflictResolutionAttempts));

        var persistedTask = await _taskRepo.GetByIdAsync(2);
        Assert.NotNull(persistedTask);
        var persistedBag = PipelineDataBag.FromJson(persistedTask.PipelineDataJson);
        Assert.False(persistedBag.ContainsKey(PipelineDataBagKeys.ConflictFiles));
        Assert.True(persistedBag.Get<bool>(PipelineDataBagKeys.HasResolvedConflict));
        Assert.Equal(0, persistedBag.Get<int>(PipelineDataBagKeys.ConflictResolutionAttempts));
    }

    [Fact]
    public async Task VerifyAndHandleImplementationConflictsAsync_WhenVerificationFailsWithinBudget_LoopsBackToImplementation()
    {
        var sut = CreateSut();
        var task = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 3,
            WorkspaceId = 1,
            Status = TaskOverallStatus.Open,
            BaseBranch = "main",
            Priority = TaskPriority.High
        });
        _depStore.AddTask(3, 1, TaskOverallStatus.Open);

        var pipelineContext = new PipelineContext(task, _workspace);
        pipelineContext.DataBag.Set(PipelineDataBagKeys.ConflictFiles, new List<string> { "src/App.cs" });

        _verifierMock.Setup(v => v.VerifyResolutionAsync(_workspace, 3, "main", It.IsAny<IReadOnlyList<string>>(), pipelineContext.DataBag, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConflictResolutionVerificationResult(false, ErrorMessage: "Unresolved conflict markers", CompilerErrors: "CS1026"));

        var pipeline = await _pipelineProvider.GetPipelineAsync(task);
        var outcome = await sut.VerifyAndHandleImplementationConflictsAsync(task, _workspace, pipelineContext, pipeline);

        Assert.Equal(ConflictVerificationOutcome.LoopedBackToImplementation, outcome);
        Assert.Equal(1, pipelineContext.DataBag.Get<int>(PipelineDataBagKeys.ConflictResolutionAttempts));
        Assert.Contains(3, _jobQueue.ImplementationQueue);
    }

    [Fact]
    public async Task VerifyAndHandleImplementationConflictsAsync_WhenBudgetExhausted_EscalatesToHuman()
    {
        var sut = CreateSut();
        var task = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 4,
            WorkspaceId = 1,
            Status = TaskOverallStatus.Open,
            BaseBranch = "main"
        });
        _depStore.AddTask(4, 1, TaskOverallStatus.Open);

        var pipelineContext = new PipelineContext(task, _workspace);
        pipelineContext.DataBag.Set(PipelineDataBagKeys.ConflictFiles, new List<string> { "src/App.cs" });
        pipelineContext.DataBag.Set(PipelineDataBagKeys.ConflictResolutionAttempts, ConflictResolutionConstants.MaxConflictResolutionAttempts - 1);

        _verifierMock.Setup(v => v.VerifyResolutionAsync(_workspace, 4, "main", It.IsAny<IReadOnlyList<string>>(), pipelineContext.DataBag, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConflictResolutionVerificationResult(false, ErrorMessage: "Markers remain"));

        var pipeline = await _pipelineProvider.GetPipelineAsync(task);
        var outcome = await sut.VerifyAndHandleImplementationConflictsAsync(task, _workspace, pipelineContext, pipeline);

        Assert.Equal(ConflictVerificationOutcome.EscalatedToHuman, outcome);
        Assert.True(task.IsWaitingForHuman);
        Assert.Equal(StandardStageIds.Implementation, task.InterruptedStageId);
        Assert.True(await _depStore.IsTaskWaitingForHumanAsync(4));
        _preparerMock.Verify(p => p.SyncTaskMarkdownAndMirrorAsync(task, _workspace, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CheckAndSyncGate2MergeabilityAsync_WhenNotMergeable_DispatchesConflictResolution()
    {
        var sut = CreateSut();
        var task = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 5,
            WorkspaceId = 1,
            Status = TaskOverallStatus.Open,
            BaseBranch = "main",
            CurrentStageId = StandardStageIds.CodeReviewAndFix
        });
        _depStore.AddTask(5, 1, TaskOverallStatus.Open);
        var pipelineContext = new PipelineContext(task, _workspace);

        _preparerMock.Setup(p => p.CheckMergeableAsync(_workspace, 5, "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MergeabilityCheckResult(IsMergeable: false, ConflictedFiles: ["Program.cs"], BaseBranchSha: "b1", FeatureBranchSha: "f1"));

        var outcome = await sut.CheckAndSyncGate2MergeabilityAsync(task, _workspace, pipelineContext, StandardStageIds.CodeReviewAndFix);

        Assert.True(outcome.DispatchedConflictResolution);
        Assert.False(outcome.BaseHadAdvanced);
        Assert.Equal(StandardStageIds.Implementation, task.CurrentStageId);
        Assert.Contains(5, _jobQueue.ImplementationQueue);
        _preparerMock.Verify(p => p.SyncTaskMarkdownAndMirrorAsync(task, _workspace, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CheckAndSyncGate2MergeabilityAsync_WhenMergeableAndBaseAdvanced_ReturnsBaseHadAdvancedTrue()
    {
        var sut = CreateSut();
        var task = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 6,
            WorkspaceId = 1,
            Status = TaskOverallStatus.Open,
            BaseBranch = "main",
            CurrentStageId = StandardStageIds.PostReviewVerification
        });
        var pipelineContext = new PipelineContext(task, _workspace);

        _preparerMock.Setup(p => p.CheckMergeableAsync(_workspace, 6, "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MergeabilityCheckResult(IsMergeable: true, ConflictedFiles: [], BaseBranchSha: "b2", FeatureBranchSha: "f1"));
        _preparerMock.Setup(p => p.SyncFromBaseBranchAsync(_workspace, 6, "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MergeSyncResult(Success: true, HasConflict: false, ConflictedFiles: [], MergeCommitSha: "new-merge-sha"));

        var outcome = await sut.CheckAndSyncGate2MergeabilityAsync(task, _workspace, pipelineContext, StandardStageIds.PostReviewVerification);

        Assert.False(outcome.DispatchedConflictResolution);
        Assert.True(outcome.BaseHadAdvanced);
    }

    [Fact]
    public async Task CompleteTaskAndCascadeAsync_WhenTaskOrWorkspaceMissing_ThrowsAppropriateException()
    {
        var sut = CreateSut();
        await Assert.ThrowsAsync<KeyNotFoundException>(() => sut.CompleteTaskAndCascadeAsync(999, "msg"));

        await _taskRepo.CreateAsync(new ForgeTask { Id = 88, WorkspaceId = 999, Status = TaskOverallStatus.Ready });
        _preparerMock.Setup(p => p.GetWorkspaceAsync(It.Is<ForgeTask>(t => t.Id == 88), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Workspace?)null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.CompleteTaskAndCascadeAsync(88, "msg"));
    }

    [Fact]
    public async Task CompleteTaskAndCascadeAsync_MergesAndUnblocksDependentTasksOrderedByPriority()
    {
        var sut = CreateSut();
        var prereq = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 10,
            WorkspaceId = 1,
            Title = "Prereq",
            Status = TaskOverallStatus.Ready,
            BaseBranch = "main"
        });
        var lowPrioDep = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 11,
            WorkspaceId = 1,
            Title = "Low Prio Dependent",
            Status = TaskOverallStatus.Open,
            Priority = TaskPriority.Low,
            CreatedAt = DateTime.UtcNow.AddMinutes(-10)
        });
        var highPrioDep = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 12,
            WorkspaceId = 1,
            Title = "High Prio Dependent",
            Status = TaskOverallStatus.Open,
            Priority = TaskPriority.High,
            CreatedAt = DateTime.UtcNow.AddMinutes(-5)
        });

        _depStore.AddTask(10, 1, TaskOverallStatus.Ready);
        _depStore.AddTask(11, 1, TaskOverallStatus.Open);
        _depStore.AddTask(12, 1, TaskOverallStatus.Open);
        _depStore.AddDependency(1, 11, 10);
        _depStore.AddDependency(1, 12, 10);

        var result = await sut.CompleteTaskAndCascadeAsync(10, "Squash task 10");

        Assert.Equal(CompleteTaskStatus.Completed, result.Status);
        var reloadedPrereq = await _taskRepo.GetByIdAsync(10);
        Assert.Equal(TaskOverallStatus.Closed, reloadedPrereq!.Status);
        Assert.Equal("squash-sha-99", reloadedPrereq.SquashCommitSha);

        // High priority dependent (#12) must be enqueued before low priority dependent (#11)
        Assert.Equal([12, 11], _jobQueue.EnqueuedStages.Select(e => e.TaskId).ToList());
    }

    [Fact]
    public async Task CompleteTaskAndCascadeInternalAsync_WhenMergeConflictThrown_DispatchesConflictResolution()
    {
        var sut = CreateSut();
        var task = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 15,
            WorkspaceId = 1,
            Status = TaskOverallStatus.Ready,
            BaseBranch = "main",
            MergeMode = MergeMode.ManualReviewRequired
        });
        _depStore.AddTask(15, 1, TaskOverallStatus.Ready);

        _preparerMock.Setup(p => p.SquashAndMergeAsync(_workspace, 15, "main", "Merge 15", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new MergeConflictException(15, "main", ["Conflict.cs"], "Conflict"));

        var result = await sut.CompleteTaskAndCascadeInternalAsync(task, _workspace, "Merge 15");

        Assert.Equal(CompleteTaskStatus.ConflictResolutionDispatched, result.Status);
        Assert.Equal(["Conflict.cs"], result.ConflictedFiles);
        Assert.Equal(TaskOverallStatus.Open, task.Status);
        Assert.Equal(StandardStageIds.Implementation, task.CurrentStageId);
    }

    [Fact]
    public async Task CompleteTaskAndCascadeInternalAsync_WhenNonFastForwardPushRetriesExhausted_ResetsBranchAndThrowsMirrorPushRejectedException()
    {
        var sut = CreateSut();
        var mirrorWorkspace = new Workspace
        {
            Id = 2,
            Name = "Mirror WS",
            RepositoryPath = "C:/repo",
            Ext4MirrorPath = "/mirror/clone",
            DefaultBaseBranch = "main"
        };
        var task = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 16,
            WorkspaceId = 2,
            Status = TaskOverallStatus.Ready,
            BaseBranch = "main"
        });

        _preparerMock.Setup(p => p.PushBaseBranchToHostAsync("/mirror/clone", "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitSyncResult(false, false, "push", "non-fast-forward", true));

        await Assert.ThrowsAsync<MirrorPushRejectedException>(() =>
            sut.CompleteTaskAndCascadeInternalAsync(task, mirrorWorkspace, "Merge 16"));

        _preparerMock.Verify(
            p => p.ResetCanonicalBranchAsync(mirrorWorkspace, "main", "base-sha-1", It.IsAny<CancellationToken>()),
            Times.Exactly(ConflictResolutionConstants.MaxPushRetries + 1));
    }

    [Fact]
    public async Task InitiateConflictResolutionAsync_WhenLifetimeDetectionsExceedBudget_EscalatesToHuman()
    {
        var sut = CreateSut();
        var task = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 20,
            WorkspaceId = 1,
            Status = TaskOverallStatus.Open,
            BaseBranch = "main"
        });
        _depStore.AddTask(20, 1, TaskOverallStatus.Open);

        var pipelineContext = new PipelineContext(task, _workspace);
        pipelineContext.DataBag.Set(PipelineDataBagKeys.LifetimeConflictDetections, ConflictResolutionConstants.MaxLifetimeConflictDetections - 1);

        await sut.InitiateConflictResolutionAsync(task, _workspace, pipelineContext, ["File.cs"]);

        Assert.True(task.IsWaitingForHuman);
        Assert.Equal(StandardStageIds.Implementation, task.InterruptedStageId);
        Assert.True(await _depStore.IsTaskWaitingForHumanAsync(20));
        _preparerMock.Verify(p => p.SyncTaskMarkdownAndMirrorAsync(task, _workspace, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CompleteTaskAndCascadeAsync_BroadcastsMergeStageCompletedBeforeCascadeUnblocking()
    {
        var sut = CreateSut();
        var prereq = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 30,
            WorkspaceId = 1,
            Status = TaskOverallStatus.Ready,
            BaseBranch = "main"
        });
        _depStore.AddTask(30, 1, TaskOverallStatus.Ready);
        // Dependent task #31 is in dependency store, but missing from _taskRepo so cascade unblocking throws TaskNotFoundException
        _depStore.AddTask(31, 1, TaskOverallStatus.Open);
        _depStore.AddDependency(1, 31, 30);

        await Assert.ThrowsAsync<TaskNotFoundException>(() =>
            sut.CompleteTaskAndCascadeAsync(30, "feat: merge 30"));

        // Even though cascade unblocking threw after merge + container cleanup, Merge StageCompleted must already have been broadcast
        _streamerMock.Verify(s => s.BroadcastAsync(
            It.Is<AgentActivityEvent>(e =>
                e.TaskId == 30 &&
                e.StageId == StandardStageIds.Merge &&
                e.Kind == AgentActivityKind.StageCompleted),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
