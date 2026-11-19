using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Core.Concurrency;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Streaming;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class TaskMutationServiceTests
{
    private readonly InMemoryTaskRepository _taskRepository;
    private readonly InMemoryWorkspaceRepository _workspaceRepository;
    private readonly MockTaskMarkdownSyncService _markdownSync;
    private readonly MockTaskOrchestrator _taskOrchestrator;
    private readonly TaskMutationService _service;
    private readonly Workspace _workspace;

    public TaskMutationServiceTests()
    {
        _taskRepository = new InMemoryTaskRepository();
        _workspaceRepository = new InMemoryWorkspaceRepository();
        _markdownSync = new MockTaskMarkdownSyncService();
        _taskOrchestrator = new MockTaskOrchestrator();

        _service = new TaskMutationService(
            _taskRepository,
            _workspaceRepository,
            new InMemoryTaskDependencyStore(),
            _markdownSync,
            _taskOrchestrator,
            new Mock<IOperationsTelemetryService>().Object,
            NullLogger<TaskMutationService>.Instance);

        _workspace = new Workspace
        {
            Id = 1,
            Name = "Test Workspace",
            RepositoryPath = Path.GetTempPath(),
            DefaultBaseBranch = "main"
        };
        _workspaceRepository.CreateAsync(_workspace).GetAwaiter().GetResult();
    }

    [Fact]
    public async Task UpdateTitleAsync_ThrowsArgumentException_WhenTitleIsEmpty()
    {
        var task = new ForgeTask { Id = 1, WorkspaceId = 1, Title = "Original" };
        await _taskRepository.CreateWithBaselineAsync(task, null, AuditActors.User, "baseline");

        await Assert.ThrowsAsync<ArgumentException>(() => _service.UpdateTitleAsync(task, "   "));
    }

    [Fact]
    public async Task UpdateTitleAsync_NoOps_WhenTitleIsUnchanged()
    {
        var task = new ForgeTask { Id = 2, WorkspaceId = 1, Title = "Unchanged Title" };
        await _taskRepository.CreateWithBaselineAsync(task, null, AuditActors.User, "baseline");

        await _service.UpdateTitleAsync(task, "Unchanged Title");

        var history = await _taskRepository.GetHistoryAsync(2);
        Assert.Single(history);
        Assert.Equal(1, task.Version);
    }

    [Fact]
    public async Task UpdateTitleAsync_UpdatesTaskAndRecordsHistoryWithUserAttribution()
    {
        var task = new ForgeTask { Id = 3, WorkspaceId = 1, Title = "Old Title" };
        await _taskRepository.CreateWithBaselineAsync(task, null, AuditActors.User, "baseline");

        await _service.UpdateTitleAsync(task, "New Title");

        Assert.Equal("New Title", task.Title);
        var history = await _taskRepository.GetHistoryAsync(3);
        Assert.Equal(2, history.Count);

        var v2 = history[1];
        Assert.Equal(2, v2.Version);
        Assert.Equal("New Title", v2.Title);
        Assert.Equal(AuditActors.User, v2.ChangedBy);
        Assert.Equal("Renamed task from \"Old Title\" to \"New Title\"", v2.ChangeReason);
    }

    [Fact]
    public async Task UpdateDescriptionAsync_UpdatesTaskAndRecordsHistory()
    {
        var task = new ForgeTask { Id = 4, WorkspaceId = 1, Description = "Line 1" };
        await _taskRepository.CreateWithBaselineAsync(task, null, AuditActors.User, "baseline");

        await _service.UpdateDescriptionAsync(task, "Line 1\nLine 2 Added");

        Assert.Equal("Line 1\nLine 2 Added", task.Description);
        var history = await _taskRepository.GetHistoryAsync(4);
        Assert.Equal(2, history.Count);

        var v2 = history[1];
        Assert.Equal(2, v2.Version);
        Assert.Equal("Line 1\nLine 2 Added", v2.Description);
        Assert.Equal(AuditActors.User, v2.ChangedBy);
        Assert.Equal("Updated task description and specification", v2.ChangeReason);
    }

    [Fact]
    public async Task UpdatePriorityAsync_UpdatesPriorityAndRecordsHistory()
    {
        var task = new ForgeTask { Id = 5, WorkspaceId = 1, Priority = TaskPriority.Normal };
        await _taskRepository.CreateWithBaselineAsync(task, null, AuditActors.User, "baseline");

        await _service.UpdatePriorityAsync(task, TaskPriority.Critical);

        Assert.Equal(TaskPriority.Critical, task.Priority);
        var history = await _taskRepository.GetHistoryAsync(5);
        Assert.Equal(2, history.Count);

        var v2 = history[1];
        Assert.Equal(TaskPriority.Critical, v2.Priority);
        Assert.Equal(AuditActors.User, v2.ChangedBy);
        Assert.Equal("Updated Priority from P3 to P1", v2.ChangeReason);
    }

    [Fact]
    public async Task UpdateMergeModeAsync_UpdatesMergeModeAndRecordsHistory()
    {
        var task = new ForgeTask { Id = 6, WorkspaceId = 1, MergeMode = MergeMode.ManualReviewRequired };
        await _taskRepository.CreateWithBaselineAsync(task, null, AuditActors.User, "baseline");

        await _service.UpdateMergeModeAsync(task, MergeMode.Automatic);

        Assert.Equal(MergeMode.Automatic, task.MergeMode);
        var history = await _taskRepository.GetHistoryAsync(6);
        Assert.Equal(2, history.Count);

        var v2 = history[1];
        Assert.Equal(MergeMode.Automatic, v2.MergeMode);
        Assert.Equal(AuditActors.User, v2.ChangedBy);
        Assert.Equal("Updated MergeMode from ManualReviewRequired to Automatic", v2.ChangeReason);
    }

    [Fact]
    public async Task UpdateDependenciesAsync_UpdatesDependenciesAndRecordsHistory()
    {
        var task = new ForgeTask { Id = 7, WorkspaceId = 1 };
        await _taskRepository.CreateWithBaselineAsync(task, null, AuditActors.User, "baseline");

        await _service.UpdateDependenciesAsync(task, new[] { 101, 102 });

        var deps = await _taskRepository.GetDependenciesAsync(7);
        Assert.Equal(2, deps.Count);
        Assert.Contains(101, deps);
        Assert.Contains(102, deps);

        var history = await _taskRepository.GetHistoryAsync(7);
        Assert.Equal(2, history.Count);

        var v2 = history[1];
        Assert.Equal(AuditActors.User, v2.ChangedBy);
        Assert.Equal("Updated dependencies (2 prerequisite(s))", v2.ChangeReason);
        Assert.Contains("101", v2.SerializedDependencies);
        Assert.Contains("102", v2.SerializedDependencies);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(6)]
    [InlineData(99)]
    public async Task UpdatePriorityAsync_ThrowsArgumentOutOfRangeException_WhenPriorityOutOfRange(int invalidPriority)
    {
        var task = new ForgeTask { Id = 8, WorkspaceId = 1, Priority = TaskPriority.Normal };
        await _taskRepository.CreateWithBaselineAsync(task, null, AuditActors.User, "baseline");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _service.UpdatePriorityAsync(task, invalidPriority));
    }

    [Fact]
    public async Task UpdateMergeModeAsync_ThrowsArgumentOutOfRangeException_WhenMergeModeInvalid()
    {
        var task = new ForgeTask { Id = 9, WorkspaceId = 1, MergeMode = MergeMode.ManualReviewRequired };
        await _taskRepository.CreateWithBaselineAsync(task, null, AuditActors.User, "baseline");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _service.UpdateMergeModeAsync(task, (MergeMode)999));
    }

    [Fact]
    public async Task UpdateDependenciesAsync_ThrowsArgumentException_WhenTaskDependsOnItself()
    {
        var task = new ForgeTask { Id = 10, WorkspaceId = 1 };
        await _taskRepository.CreateWithBaselineAsync(task, null, AuditActors.User, "baseline");

        await Assert.ThrowsAsync<ArgumentException>(() => _service.UpdateDependenciesAsync(task, new[] { 10 }));
    }

    [Fact]
    public async Task UpdateDependenciesAsync_UpdatesInMemoryTaskDependencies()
    {
        var task = new ForgeTask { Id = 11, WorkspaceId = 1 };
        await _taskRepository.CreateWithBaselineAsync(task, new[] { 50 }, AuditActors.User, "baseline");

        await _service.UpdateDependenciesAsync(task, new[] { 60, 70 });

        Assert.NotNull(task.Dependencies);
        Assert.Equal(2, task.Dependencies.Count);
        Assert.Contains(task.Dependencies, d => d.DependsOnTaskId == 60);
        Assert.Contains(task.Dependencies, d => d.DependsOnTaskId == 70);
        Assert.DoesNotContain(task.Dependencies, d => d.DependsOnTaskId == 50);
    }

    [Fact]
    public async Task UpdateTitleAsync_PreservesDependenciesOnDisk_WhenPrerequisiteIdsNull()
    {
        var task = new ForgeTask { Id = 12, WorkspaceId = 1, Title = "Initial Title" };
        await _taskRepository.CreateWithBaselineAsync(task, new[] { 88, 99 }, AuditActors.User, "baseline");

        // Call UpdateTitleAsync without passing prerequisiteIds
        await _service.UpdateTitleAsync(task, "New Preserved Title");

        var syncCalls = _markdownSync.SyncTaskCalls;
        Assert.NotEmpty(syncCalls);
        var lastSync = syncCalls.Last();
        Assert.Equal(12, lastSync.Task.Id);
        Assert.NotNull(lastSync.Dependencies);
        Assert.Equal(2, lastSync.Dependencies!.Count);
        Assert.Contains(88, lastSync.Dependencies);
        Assert.Contains(99, lastSync.Dependencies);
    }

    [Fact]
    public async Task RejectTaskAsync_DelegatesToOrchestrator_WithReason_AndUpdatesEntityStatus()
    {
        var task = new ForgeTask
        {
            Id = 42,
            WorkspaceId = 1,
            Title = "To be rejected",
            Status = TaskOverallStatus.Open,
            IsWaitingForHuman = true,
            UpdatedAt = DateTime.UtcNow.AddHours(-1)
        };
        await _service.RejectTaskAsync(task, "Not needed anymore");

        Assert.NotNull(_taskOrchestrator.LastRejectCall);
        Assert.Equal(42, _taskOrchestrator.LastRejectCall.Value.TaskId);
        Assert.Equal("Not needed anymore", _taskOrchestrator.LastRejectCall.Value.Reason);

        Assert.Equal(TaskOverallStatus.Rejected, task.Status);
        Assert.False(task.IsWaitingForHuman);
        Assert.True(task.UpdatedAt > DateTime.UtcNow.AddMinutes(-5));
    }

    [Fact]
    public async Task UpdateBaseBranchAsync_ThrowsArgumentNullException_WhenTaskIsNull()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => _service.UpdateBaseBranchAsync((ForgeTask)null!, "master"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task UpdateBaseBranchAsync_ThrowsArgumentException_WhenBaseBranchIsEmptyOrWhitespace(string? invalidBranch)
    {
        var task = new ForgeTask { Id = 20, WorkspaceId = 1, BaseBranch = "main" };
        await _taskRepository.CreateWithBaselineAsync(task, null, AuditActors.User, "baseline");

        await Assert.ThrowsAsync<ArgumentException>(() => _service.UpdateBaseBranchAsync(task, invalidBranch!));
    }

    [Fact]
    public async Task UpdateBaseBranchAsync_NoOps_WhenBaseBranchIsUnchanged()
    {
        var task = new ForgeTask { Id = 21, WorkspaceId = 1, BaseBranch = "main" };
        await _taskRepository.CreateWithBaselineAsync(task, null, AuditActors.User, "baseline");

        await _service.UpdateBaseBranchAsync(task, "main");

        var history = await _taskRepository.GetHistoryAsync(21);
        Assert.Single(history);
        Assert.Equal(1, task.Version);
    }

    [Fact]
    public async Task UpdateBaseBranchAsync_UpdatesTaskAndRecordsHistoryWithUserAttribution()
    {
        var task = new ForgeTask { Id = 22, WorkspaceId = 1, BaseBranch = "main" };
        await _taskRepository.CreateWithBaselineAsync(task, null, AuditActors.User, "baseline");

        await _service.UpdateBaseBranchAsync(task, "master");

        Assert.Equal("master", task.BaseBranch);
        var history = await _taskRepository.GetHistoryAsync(22);
        Assert.Equal(2, history.Count);

        var v2 = history[1];
        Assert.Equal(2, v2.Version);
        Assert.Equal("master", v2.BaseBranch);
        Assert.Equal(AuditActors.User, v2.ChangedBy);
        Assert.Equal("Updated BaseBranch from \"main\" to \"master\"", v2.ChangeReason);
    }

    [Fact]
    public async Task UpdateBaseBranchAsync_SyncsMarkdownToDisk()
    {
        var task = new ForgeTask { Id = 23, WorkspaceId = 1, BaseBranch = "main" };
        await _taskRepository.CreateWithBaselineAsync(task, new[] { 10 }, AuditActors.User, "baseline");

        await _service.UpdateBaseBranchAsync(task, "release/1.0");

        var syncCalls = _markdownSync.SyncTaskCalls;
        Assert.NotEmpty(syncCalls);
        var lastSync = syncCalls.Last();
        Assert.Equal(23, lastSync.Task.Id);
        Assert.Equal("release/1.0", lastSync.Task.BaseBranch);
        Assert.Contains(10, lastSync.Dependencies!);
    }

    [Fact]
    public async Task UpdateBaseBranchAsync_WithTaskId_UpdatesTaskAndRecordsHistory()
    {
        var task = new ForgeTask { Id = 24, WorkspaceId = 1, BaseBranch = "dev" };
        await _taskRepository.CreateWithBaselineAsync(task, null, AuditActors.User, "baseline");

        await _service.UpdateBaseBranchAsync(24, "master");

        var updated = await _taskRepository.GetByIdAsync(24);
        Assert.NotNull(updated);
        Assert.Equal("master", updated.BaseBranch);

        var history = await _taskRepository.GetHistoryAsync(24);
        Assert.Equal(2, history.Count);
        Assert.Equal("Updated BaseBranch from \"dev\" to \"master\"", history[1].ChangeReason);
    }

    [Fact]
    public async Task UpdateBaseBranchAsync_WithTaskId_ThrowsKeyNotFoundException_WhenTaskDoesNotExist()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _service.UpdateBaseBranchAsync(99999, "master"));
    }

    [Fact]
    public async Task UpdateDependenciesAsync_CallsTryScheduleOpenTaskIfReadyOnlyWhenDependenciesChange()
    {
        var task = new ForgeTask { Id = 25, WorkspaceId = 1, Status = TaskOverallStatus.Open };
        await _taskRepository.CreateWithBaselineAsync(task, new[] { 101 }, AuditActors.User, "baseline");

        // Same dependencies -> no-op, orchestrator not invoked
        await _service.UpdateDependenciesAsync(task, new[] { 101 });
        Assert.Empty(_taskOrchestrator.TryScheduleOpenTaskCalls);

        // Changed dependencies -> invokes TryScheduleOpenTaskIfReadyAsync
        await _service.UpdateDependenciesAsync(task, Array.Empty<int>());
        Assert.Equal([25], _taskOrchestrator.TryScheduleOpenTaskCalls);
    }

    [Fact]
    public async Task UpdateDependenciesAsync_WithRealOrchestrator_SchedulesOnlyUnblockedEligibleOpenTasks()
    {
        var taskRepo = new InMemoryTaskRepository();
        var depStore = new InMemoryTaskDependencyStore();
        var depEngine = new DependencyResolutionEngine(depStore);
        var jobQueue = new MockTaskJobQueue();

        var stage = Mock.Of<IWorkflowStage>(s =>
            s.StageId == StandardStageIds.TaskReview &&
            s.DisplayName == "Task Review" &&
            s.QueueTier == StageQueueTier.TaskReview);
        var pipelineProvider = new DefaultWorkflowPipelineProvider(new WorkflowPipeline([stage]));

        var lockProvider = new WorkspaceLockProvider();
        var preparer = new TaskWorkspacePreparer(
            _workspaceRepository,
            lockProvider,
            Mock.Of<IGitWorkspaceManager>(),
            Mock.Of<IGitMirrorSyncService>(),
            _markdownSync,
            Mock.Of<IWorkspacePathResolver>(),
            Mock.Of<IExecutionEnvironmentProvider>(),
            Mock.Of<IGitHubPullRequestDeliveryService>(),
            Mock.Of<IGitHubTaskMirrorService>(),
            NullLogger<TaskWorkspacePreparer>.Instance);

        var streamer = new InMemoryAgentActivityStreamer();
        var telemetry = Mock.Of<IOperationsTelemetryService>();
        var mergeCoordinator = new TaskMergeAndCascadeCoordinator(
            taskRepo,
            preparer,
            depEngine,
            depStore,
            jobQueue,
            pipelineProvider,
            Mock.Of<IConflictResolutionVerifier>(),
            streamer,
            telemetry,
            NullLogger<TaskMergeAndCascadeCoordinator>.Instance);

        var cancellationManager = new TaskCancellationManager(NullLogger<TaskCancellationManager>.Instance);
        var lifecycleCoordinator = new TaskLifecycleMutationCoordinator(
            taskRepo,
            preparer,
            mergeCoordinator,
            pipelineProvider,
            jobQueue,
            cancellationManager,
            streamer,
            telemetry,
            NullLogger<TaskLifecycleMutationCoordinator>.Instance);

        var orchestrator = new TaskOrchestrator(
            taskRepo,
            preparer,
            mergeCoordinator,
            lifecycleCoordinator,
            new HarnessRegistry(),
            streamer,
            Mock.Of<IOperationsControlService>(),
            telemetry,
            new InMemoryTaskAttachmentRepository(taskRepo),
            NullLogger<TaskOrchestrator>.Instance);

        var mutationService = new TaskMutationService(
            taskRepo,
            _workspaceRepository,
            depStore,
            _markdownSync,
            orchestrator,
            telemetry,
            NullLogger<TaskMutationService>.Instance);

        var unclosedBlocker1 = await taskRepo.CreateWithBaselineAsync(
            new ForgeTask { WorkspaceId = 1, Title = "Blocker 1", Status = TaskOverallStatus.Open },
            null, AuditActors.User, "init");
        var unclosedBlocker2 = await taskRepo.CreateWithBaselineAsync(
            new ForgeTask { WorkspaceId = 1, Title = "Blocker 2", Status = TaskOverallStatus.Reviewed },
            null, AuditActors.User, "init");

        // 1. Removing 1 of 2 blockers does NOT enqueue; removing the last blocker DOES enqueue
        var openTask = await taskRepo.CreateWithBaselineAsync(
            new ForgeTask { WorkspaceId = 1, Title = "Open Task", Status = TaskOverallStatus.Open, Priority = TaskPriority.High },
            new[] { unclosedBlocker1.Id, unclosedBlocker2.Id }, AuditActors.User, "init");

        await mutationService.UpdateDependenciesAsync(openTask, new[] { unclosedBlocker2.Id });
        Assert.Empty(jobQueue.EnqueuedStages);

        await mutationService.UpdateDependenciesAsync(openTask, Array.Empty<int>());
        Assert.Single(jobQueue.EnqueuedStages);
        Assert.Equal(openTask.Id, jobQueue.EnqueuedStages[0].TaskId);
        Assert.False(string.IsNullOrWhiteSpace(openTask.LastHangfireJobId));

        // 2. Active job prevents duplicate enqueue when dependencies change again (add closed prereq then remove it)
        var closedBlocker = await taskRepo.CreateWithBaselineAsync(
            new ForgeTask { WorkspaceId = 1, Title = "Closed Blocker", Status = TaskOverallStatus.Closed },
            null, AuditActors.User, "init");
        var originalJobId = openTask.LastHangfireJobId!;

        await mutationService.UpdateDependenciesAsync(openTask, new[] { closedBlocker.Id });
        Assert.Single(jobQueue.EnqueuedStages);
        Assert.Equal(originalJobId, openTask.LastHangfireJobId);

        // Stale deleted job IS replaced cleanly when unblocked dependency change occurs
        jobQueue.Delete(originalJobId);
        await mutationService.UpdateDependenciesAsync(openTask, Array.Empty<int>());
        Assert.Equal(2, jobQueue.EnqueuedStages.Count);

        // 3. Draft, Suggested, HasExecutionRun, and IsWaitingForHuman tasks are NOT enqueued
        var draftTask = await taskRepo.CreateWithBaselineAsync(
            new ForgeTask { WorkspaceId = 1, Title = "Draft Task", Status = TaskOverallStatus.Draft },
            new[] { unclosedBlocker1.Id }, AuditActors.User, "init");
        var suggestedTask = await taskRepo.CreateWithBaselineAsync(
            new ForgeTask { WorkspaceId = 1, Title = "Suggested Task", Status = TaskOverallStatus.Suggested },
            new[] { unclosedBlocker1.Id }, AuditActors.User, "init");
        var executedTask = await taskRepo.CreateWithBaselineAsync(
            new ForgeTask { WorkspaceId = 1, Title = "Executed Task", Status = TaskOverallStatus.Open, HasExecutionRun = true },
            new[] { unclosedBlocker1.Id }, AuditActors.User, "init");
        var waitingTask = await taskRepo.CreateWithBaselineAsync(
            new ForgeTask { WorkspaceId = 1, Title = "Waiting Task", Status = TaskOverallStatus.Open, IsWaitingForHuman = true },
            new[] { unclosedBlocker1.Id }, AuditActors.User, "init");

        await mutationService.UpdateDependenciesAsync(draftTask, Array.Empty<int>());
        await mutationService.UpdateDependenciesAsync(suggestedTask, Array.Empty<int>());
        await mutationService.UpdateDependenciesAsync(executedTask, Array.Empty<int>());
        await mutationService.UpdateDependenciesAsync(waitingTask, Array.Empty<int>());

        Assert.Equal(2, jobQueue.EnqueuedStages.Count);
    }

    [Fact]
    public async Task UpdateDependenciesAsync_WhenInMemoryDependenciesAreNullOrStale_UsesPersistedDependenciesAndSchedules()
    {
        var blocker = await _taskRepository.CreateWithBaselineAsync(
            new ForgeTask { Id = 50, WorkspaceId = 1, Status = TaskOverallStatus.Open },
            null, AuditActors.User, "init");
        var openTask = await _taskRepository.CreateWithBaselineAsync(
            new ForgeTask { Id = 51, WorkspaceId = 1, Status = TaskOverallStatus.Open },
            new[] { blocker.Id }, AuditActors.User, "init");

        // Caller holds an entity whose navigation property was not loaded (null)
        var nullDepsSnapshot = new ForgeTask
        {
            Id = openTask.Id,
            WorkspaceId = 1,
            Status = TaskOverallStatus.Open,
            Dependencies = null!
        };

        await _service.UpdateDependenciesAsync(nullDepsSnapshot, Array.Empty<int>());

        Assert.Empty(await _taskRepository.GetDependenciesAsync(openTask.Id));
        Assert.NotNull(nullDepsSnapshot.Dependencies);
        Assert.Empty(nullDepsSnapshot.Dependencies);
        Assert.Equal([openTask.Id], _taskOrchestrator.TryScheduleOpenTaskCalls);

        // Caller holds an entity with stale empty Dependencies while persisted store has a blocker
        var openTask2 = await _taskRepository.CreateWithBaselineAsync(
            new ForgeTask { Id = 52, WorkspaceId = 1, Status = TaskOverallStatus.Open },
            new[] { blocker.Id }, AuditActors.User, "init");
        var staleEmptyDepsSnapshot = new ForgeTask
        {
            Id = openTask2.Id,
            WorkspaceId = 1,
            Status = TaskOverallStatus.Open,
            Dependencies = new List<TaskDependency>()
        };

        await _service.UpdateDependenciesAsync(staleEmptyDepsSnapshot, Array.Empty<int>());

        Assert.Empty(await _taskRepository.GetDependenciesAsync(openTask2.Id));
        Assert.Equal([openTask.Id, openTask2.Id], _taskOrchestrator.TryScheduleOpenTaskCalls);
    }
}
