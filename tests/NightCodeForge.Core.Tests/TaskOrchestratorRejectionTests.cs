using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NightCodeForge.Core.Concurrency;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Tests.Fixtures;
using NightCodeForge.Core.Workflow;
using Xunit;

namespace NightCodeForge.Core.Tests;

public class TaskOrchestratorRejectionTests
{
    private readonly InMemoryTaskRepository _taskRepo;
    private readonly InMemoryWorkspaceRepository _workspaceRepo;
    private readonly InMemoryTaskDependencyStore _depStore;
    private readonly DependencyResolutionEngine _depEngine;
    private readonly WorkspaceLockProvider _lockProvider;
    private readonly FakeGitWorkspaceManager _gitManager;
    private readonly FakeTaskJobQueue _jobQueue;
    private readonly ITaskCancellationManager _cancellationManager;
    private readonly Workspace _workspace;

    private TaskOrchestrator CreateOrchestrator() =>
        new TestOrchestratorBuilder
        {
            TaskRepository = _taskRepo,
            WorkspaceRepository = _workspaceRepo,
            DependencyEngine = _depEngine,
            LockProvider = _lockProvider,
            GitWorkspaceManager = _gitManager,
            PipelineProvider = new DefaultWorkflowPipelineProvider(),
            JobQueue = _jobQueue,
            DependencyStore = _depStore,
            CancellationManager = _cancellationManager
        }.Build();

    public TaskOrchestratorRejectionTests()
    {
        _taskRepo = new InMemoryTaskRepository();
        _workspaceRepo = new InMemoryWorkspaceRepository();
        _depStore = new InMemoryTaskDependencyStore();
        _depEngine = new DependencyResolutionEngine(_depStore);
        _lockProvider = new WorkspaceLockProvider();
        _gitManager = new FakeGitWorkspaceManager();
        _jobQueue = new FakeTaskJobQueue();
        _cancellationManager = new TaskCancellationManager(NullLogger<TaskCancellationManager>.Instance);

        _workspace = new Workspace
        {
            Id = 1,
            Name = "Rejection Test Workspace",
            RepositoryPath = "C:/test/repo",
            DefaultBaseBranch = "main"
        };
        _workspaceRepo.CreateAsync(_workspace).GetAwaiter().GetResult();
    }

    [Fact]
    public async Task RejectTaskAsync_WhenTaskIsOpen_TransitionsToRejected_UpdatesHistoryAndDependencyStore()
    {
        var task = await _taskRepo.CreateWithBaselineAsync(new ForgeTask
        {
            WorkspaceId = _workspace.Id,
            Title = "Task to reject",
            Status = TaskOverallStatus.Open,
            Priority = TaskPriority.Normal,
            MergeMode = MergeMode.ManualReviewRequired
        }, null, AuditActors.User, "initial creation");

        _depStore.AddTask(task.Id, _workspace.Id, TaskOverallStatus.Open);

        var orchestrator = CreateOrchestrator();
        await orchestrator.RejectTaskAsync(task.Id, "Out of scope for this sprint.");

        var updated = await _taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(updated);
        Assert.Equal(TaskOverallStatus.Rejected, updated.Status);

        // Audit History
        var histories = await _taskRepo.GetHistoryAsync(task.Id);
        Assert.NotEmpty(histories);
        var lastHistory = histories.Last();
        Assert.Equal(AuditActors.User, lastHistory.ChangedBy);
        Assert.Equal("Out of scope for this sprint.", lastHistory.ChangeReason);

        // Dependency Store
        var storeStatus = await _depStore.GetTaskStatusAsync(task.Id);
        Assert.Equal(TaskOverallStatus.Rejected, storeStatus);

        // Execution Logs
        var logs = _taskRepo.GetAllLogs();
        Assert.Contains(logs, l => l.Level == LogLevel.Warning && l.Message.Contains("rejected by maintainer"));
    }

    [Fact]
    public async Task RejectTaskAsync_WhenTaskHasHangfireJob_DeletesJobFromQueue()
    {
        var task = await _taskRepo.CreateWithBaselineAsync(new ForgeTask
        {
            WorkspaceId = _workspace.Id,
            Title = "Queued task",
            Status = TaskOverallStatus.Open,
            Priority = TaskPriority.Critical,
            LastHangfireJobId = "hangfire-job-999"
        }, null, AuditActors.User, "baseline");

        var orchestrator = CreateOrchestrator();
        await orchestrator.RejectTaskAsync(task.Id, "Cancelling queued work");

        Assert.Contains("hangfire-job-999", _jobQueue.DeletedJobs);
    }

    [Fact]
    public async Task RejectTaskAsync_WhenTaskAlreadyClosed_ThrowsInvalidOperationException()
    {
        var task = await _taskRepo.CreateWithBaselineAsync(new ForgeTask
        {
            WorkspaceId = _workspace.Id,
            Title = "Closed task",
            Status = TaskOverallStatus.Closed
        }, null, AuditActors.User, "baseline");

        var orchestrator = CreateOrchestrator();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            orchestrator.RejectTaskAsync(task.Id, "Cannot reject closed"));
    }

    [Fact]
    public async Task RejectTaskAsync_WhenTaskAlreadyRejected_ThrowsInvalidOperationException()
    {
        var task = await _taskRepo.CreateWithBaselineAsync(new ForgeTask
        {
            WorkspaceId = _workspace.Id,
            Title = "Rejected task",
            Status = TaskOverallStatus.Rejected
        }, null, AuditActors.User, "baseline");

        var orchestrator = CreateOrchestrator();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            orchestrator.RejectTaskAsync(task.Id, "Already rejected"));
    }

    [Fact]
    public async Task RejectTaskAsync_WhenTaskNotFound_ThrowsKeyNotFoundException()
    {
        var orchestrator = CreateOrchestrator();
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            orchestrator.RejectTaskAsync(99999, "Non-existent"));
    }

    [Fact]
    public async Task RejectTaskAsync_SignalsCancellationOnActiveTask()
    {
        var task = await _taskRepo.CreateWithBaselineAsync(new ForgeTask
        {
            WorkspaceId = _workspace.Id,
            Title = "Active running task",
            Status = TaskOverallStatus.Reviewed
        }, null, AuditActors.User, "baseline");

        using var cts = _cancellationManager.Register(task.Id);
        Assert.False(cts.IsCancellationRequested);

        var orchestrator = CreateOrchestrator();
        await orchestrator.RejectTaskAsync(task.Id, "Stopping active execution");

        Assert.True(cts.IsCancellationRequested);
        Assert.True(_cancellationManager.IsCancelRequested(task.Id));
    }

    [Theory]
    [InlineData(TaskOverallStatus.Suggested)]
    [InlineData(TaskOverallStatus.Reviewed)]
    [InlineData(TaskOverallStatus.Implemented)]
    [InlineData(TaskOverallStatus.Ready)]
    [InlineData(TaskOverallStatus.Failed)]
    public async Task RejectTaskAsync_AllowsRejectionAcrossAllNonTerminalStatuses(TaskOverallStatus initialStatus)
    {
        var task = await _taskRepo.CreateWithBaselineAsync(new ForgeTask
        {
            WorkspaceId = _workspace.Id,
            Title = $"Task in {initialStatus}",
            Status = initialStatus
        }, null, AuditActors.User, "baseline");

        _depStore.AddTask(task.Id, _workspace.Id, initialStatus);

        var orchestrator = CreateOrchestrator();
        await orchestrator.RejectTaskAsync(task.Id, $"Rejecting from {initialStatus}");

        var updated = await _taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(updated);
        Assert.Equal(TaskOverallStatus.Rejected, updated.Status);
    }

    [Fact]
    public async Task PromoteSuggestedTaskAsync_WhenTargetStatusIsRejected_DelegatesToRejectTaskAsync()
    {
        var task = await _taskRepo.CreateWithBaselineAsync(new ForgeTask
        {
            WorkspaceId = _workspace.Id,
            Title = "Suggested task to reject via promotion flow",
            Status = TaskOverallStatus.Suggested
        }, null, AuditActors.User, "suggested");

        _depStore.AddTask(task.Id, _workspace.Id, TaskOverallStatus.Suggested);

        var orchestrator = CreateOrchestrator();
        await orchestrator.PromoteSuggestedTaskAsync(task.Id, TaskOverallStatus.Rejected, TaskPriority.Normal, MergeMode.Automatic);

        var updated = await _taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(updated);
        Assert.Equal(TaskOverallStatus.Rejected, updated.Status);

        var logs = _taskRepo.GetAllLogs();
        Assert.Contains(logs, l => l.Level == LogLevel.Warning && l.Message.Contains("rejected by maintainer"));
    }

    [Fact]
    public async Task PromoteSuggestedTaskAsync_WhenOpenAndUnblocked_StoresLastHangfireJobId_WhichIsDeletedOnRejection()
    {
        var task = await _taskRepo.CreateWithBaselineAsync(new ForgeTask
        {
            WorkspaceId = _workspace.Id,
            Title = "Promoted unblocked task",
            Status = TaskOverallStatus.Suggested,
            Priority = TaskPriority.Critical
        }, null, AuditActors.User, "suggested");

        _depStore.AddTask(task.Id, _workspace.Id, TaskOverallStatus.Suggested);

        var orchestrator = CreateOrchestrator();
        await orchestrator.PromoteSuggestedTaskAsync(task.Id, TaskOverallStatus.Open, TaskPriority.Critical, MergeMode.Automatic);

        var promoted = await _taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(promoted);
        Assert.False(string.IsNullOrWhiteSpace(promoted.LastHangfireJobId));

        var queuedJobId = promoted.LastHangfireJobId;

        // Now reject the task; the queued job must be deleted from FakeTaskJobQueue
        await orchestrator.RejectTaskAsync(task.Id, "Decided not to run");
        Assert.Contains(queuedJobId, _jobQueue.DeletedJobs);

        var rejected = await _taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(rejected);
        Assert.Null(rejected.LastHangfireJobId);
    }

    [Fact]
    public async Task RejectTaskAsync_WhenTaskIsWaitingForHuman_ResetsIsWaitingForHumanAndClearsJobId()
    {
        var task = await _taskRepo.CreateWithBaselineAsync(new ForgeTask
        {
            WorkspaceId = _workspace.Id,
            Title = "Task with questions",
            Status = TaskOverallStatus.Open,
            IsWaitingForHuman = true,
            LastHangfireJobId = "job-waiting-123"
        }, null, AuditActors.User, "baseline");

        var orchestrator = CreateOrchestrator();
        await orchestrator.RejectTaskAsync(task.Id, "Closing blocked inquiry");

        var rejected = await _taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(rejected);
        Assert.Equal(TaskOverallStatus.Rejected, rejected.Status);
        Assert.False(rejected.IsWaitingForHuman);
        Assert.Null(rejected.LastHangfireJobId);
        Assert.Contains("job-waiting-123", _jobQueue.DeletedJobs);
    }

    [Fact]
    public async Task RequestChangesAsync_AssignsLastHangfireJobId_AndPersistsToTask()
    {
        var task = await _taskRepo.CreateWithBaselineAsync(new ForgeTask
        {
            WorkspaceId = _workspace.Id,
            Title = "Task under review",
            Status = TaskOverallStatus.Ready,
            CurrentStageId = "stage-verify"
        }, null, AuditActors.User, "baseline");

        var orchestrator = CreateOrchestrator();
        await orchestrator.RequestChangesAsync(task.Id, "Fix formatting in tests");

        var updated = await _taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(updated);
        Assert.False(string.IsNullOrWhiteSpace(updated.LastHangfireJobId));
        Assert.Equal(TaskOverallStatus.Reviewed, updated.Status);
    }

    [Fact]
    public async Task RejectTaskAsync_WhenTaskIsRejected_InvokesMarkdownSyncService()
    {
        var task = await _taskRepo.CreateWithBaselineAsync(new ForgeTask
        {
            WorkspaceId = _workspace.Id,
            Title = "Task to sync to disk on reject",
            Status = TaskOverallStatus.Open
        }, null, AuditActors.User, "baseline");

        var mockMarkdownSync = new Moq.Mock<NightCodeForge.Core.Services.ITaskMarkdownSyncService>();
        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = _taskRepo,
            WorkspaceRepository = _workspaceRepo,
            DependencyEngine = _depEngine,
            LockProvider = _lockProvider,
            GitWorkspaceManager = _gitManager,
            PipelineProvider = new DefaultWorkflowPipelineProvider(),
            JobQueue = _jobQueue,
            DependencyStore = _depStore,
            CancellationManager = _cancellationManager,
            MarkdownSyncService = mockMarkdownSync.Object
        }.Build();

        await orchestrator.RejectTaskAsync(task.Id, "Sync test reason");

        mockMarkdownSync.Verify(
            m => m.SyncTaskToDiskAsync(
                Moq.It.Is<ForgeTask>(t => t.Id == task.Id && t.Status == TaskOverallStatus.Rejected),
                Moq.It.IsAny<string>(),
                Moq.It.IsAny<IReadOnlyList<int>?>(),
                Moq.It.IsAny<string?>(),
                Moq.It.IsAny<CancellationToken>()),
            Moq.Times.Once);
    }

    [Fact]
    public async Task ProcessTaskAsync_WhenTaskAlreadyRejected_AbortsExecutionImmediately()
    {
        var task = await _taskRepo.CreateWithBaselineAsync(new ForgeTask
        {
            WorkspaceId = _workspace.Id,
            Title = "Already rejected task",
            Status = TaskOverallStatus.Rejected
        }, null, AuditActors.User, "baseline");

        _depStore.AddTask(task.Id, _workspace.Id, TaskOverallStatus.Rejected);

        var orchestrator = CreateOrchestrator();
        await orchestrator.ProcessTaskAsync(task.Id);

        var persisted = await _taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(persisted);
        Assert.Equal(TaskOverallStatus.Rejected, persisted.Status);
    }

    [Fact]
    public async Task ProcessTaskAsync_WhenCancelledAndTaskWasRejected_PreservesRejectedStatus()
    {
        var task = await _taskRepo.CreateWithBaselineAsync(new ForgeTask
        {
            WorkspaceId = _workspace.Id,
            Title = "Task rejected during run",
            Status = TaskOverallStatus.Open
        }, null, AuditActors.User, "baseline");

        _depStore.AddTask(task.Id, _workspace.Id, TaskOverallStatus.Open);

        // Pre-cancel the token in cancellation manager to simulate rejection triggering cancellation
        using var reg = _cancellationManager.Register(task.Id);
        _cancellationManager.Cancel(task.Id);

        // Transition task status to Rejected directly as RejectTaskAsync would do
        task.Status = TaskOverallStatus.Rejected;
        await _taskRepo.UpdateAsync(task);
        await _depStore.UpdateTaskStatusAsync(task.Id, TaskOverallStatus.Rejected);

        var orchestrator = CreateOrchestrator();
        await orchestrator.ProcessTaskAsync(task.Id);

        var persisted = await _taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(persisted);
        Assert.Equal(TaskOverallStatus.Rejected, persisted.Status);
    }
}

