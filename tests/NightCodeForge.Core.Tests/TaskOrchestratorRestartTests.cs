using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Core.Concurrency;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Tests.Fixtures;
using NightCodeForge.Core.Workflow;
using Xunit;

namespace NightCodeForge.Core.Tests;

public class TaskOrchestratorRestartTests
{
    private readonly InMemoryTaskRepository _taskRepo;
    private readonly InMemoryWorkspaceRepository _workspaceRepo;
    private readonly InMemoryTaskDependencyStore _depStore;
    private readonly DependencyResolutionEngine _depEngine;
    private readonly WorkspaceLockProvider _lockProvider;
    private readonly FakeGitWorkspaceManager _gitManager;
    private readonly FakeTaskJobQueue _jobQueue;
    private readonly Mock<ITaskCancellationManager> _cancellationManagerMock;
    private readonly Mock<IExecutionEnvironmentProvider> _environmentProviderMock;
    private readonly Workspace _workspace;

    public TaskOrchestratorRestartTests()
    {
        _taskRepo = new InMemoryTaskRepository();
        _workspaceRepo = new InMemoryWorkspaceRepository();
        _depStore = new InMemoryTaskDependencyStore();
        _depEngine = new DependencyResolutionEngine(_depStore);
        _lockProvider = new WorkspaceLockProvider();
        _gitManager = new FakeGitWorkspaceManager();
        _jobQueue = new FakeTaskJobQueue();
        _cancellationManagerMock = new Mock<ITaskCancellationManager>();
        _environmentProviderMock = new Mock<IExecutionEnvironmentProvider>();

        _workspace = new Workspace
        {
            Id = 1,
            Name = "Restart Test Workspace",
            RepositoryPath = "C:/test/repo",
            DefaultBaseBranch = "main"
        };
        _workspaceRepo.CreateAsync(_workspace).GetAwaiter().GetResult();
    }

    private TaskOrchestrator CreateOrchestrator(
        IGitWorkspaceManager? gitManager = null,
        IExecutionEnvironmentProvider? envProvider = null)
    {
        var builder = new TestOrchestratorBuilder
        {
            TaskRepository = _taskRepo,
            WorkspaceRepository = _workspaceRepo,
            DependencyEngine = _depEngine,
            LockProvider = _lockProvider,
            GitWorkspaceManager = gitManager ?? _gitManager,
            JobQueue = _jobQueue,
            DependencyStore = _depStore,
            CancellationManager = _cancellationManagerMock.Object
        };

        if (envProvider != null)
        {
            return new TaskOrchestrator(
                _taskRepo,
                _workspaceRepo,
                _depEngine,
                _lockProvider,
                gitManager ?? _gitManager,
                envProvider,
                builder.PipelineProvider,
                _jobQueue,
                _depStore,
                builder.HarnessRegistry,
                builder.GitMirrorSyncService,
                builder.MarkdownSyncService,
                builder.PathResolver,
                builder.Streamer,
                _cancellationManagerMock.Object,
                builder.ConflictResolutionVerifier,
                builder.OperationsControlService,
                builder.TelemetryService,
                NullLogger<TaskOrchestrator>.Instance);
        }

        return builder.Build();
    }

    [Fact]
    public async Task RestartTaskPipelineAsync_WhenTaskIsClosedOrMerged_ThrowsInvalidOperationException()
    {
        // Arrange
        var mergedTask = await _taskRepo.CreateWithBaselineAsync(new ForgeTask
        {
            WorkspaceId = _workspace.Id,
            Title = "Merged Task",
            Status = TaskOverallStatus.Ready,
            SquashCommitSha = "abc1234567890abcdef1234567890abcdef1234",
            BaseBranch = "main"
        }, null, AuditActors.User, "init");

        var closedTask = await _taskRepo.CreateWithBaselineAsync(new ForgeTask
        {
            WorkspaceId = _workspace.Id,
            Title = "Closed Task",
            Status = TaskOverallStatus.Closed,
            SquashCommitSha = null,
            BaseBranch = "main"
        }, null, AuditActors.User, "init");

        var orchestrator = CreateOrchestrator();

        // Act & Assert
        var ex1 = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            orchestrator.RestartTaskPipelineAsync(mergedTask.Id, TaskRestartGitMode.PreserveWork));
        Assert.Contains("merged", ex1.Message, StringComparison.OrdinalIgnoreCase);

        var ex2 = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            orchestrator.RestartTaskPipelineAsync(closedTask.Id, TaskRestartGitMode.HardResetToBaseBranch));
        Assert.Contains("merged", ex2.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RestartTaskPipelineAsync_WhenTaskIsRunning_CancelsRunningExecutionAndDeletesHangfireJob()
    {
        // Arrange
        var task = await _taskRepo.CreateWithBaselineAsync(new ForgeTask
        {
            WorkspaceId = _workspace.Id,
            Title = "Active Task",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.Implementation,
            LastHangfireJobId = "hangfire-job-999",
            BaseBranch = "main"
        }, null, AuditActors.User, "init");

        _cancellationManagerMock.Setup(c => c.IsRegistered(task.Id)).Returns(true);
        var orchestrator = CreateOrchestrator();

        // Act
        var result = await orchestrator.RestartTaskPipelineAsync(task.Id, TaskRestartGitMode.PreserveWork);

        // Assert
        Assert.True(result);
        _cancellationManagerMock.Verify(c => c.Cancel(task.Id), Times.Once);
        Assert.Contains("hangfire-job-999", _jobQueue.DeletedJobs);
    }

    [Fact]
    public async Task RestartTaskPipelineAsync_HardReset_RecyclesContainerBeforeGitReset()
    {
        // Arrange
        var task = await _taskRepo.CreateWithBaselineAsync(new ForgeTask
        {
            WorkspaceId = _workspace.Id,
            Title = "Task with Container Recycling",
            Status = TaskOverallStatus.Failed,
            CurrentStageId = StandardStageIds.Implementation,
            BaseBranch = "main"
        }, null, AuditActors.User, "init");

        var callOrder = new List<string>();

        var envMock = new Mock<IExecutionEnvironmentProvider>();
        envMock.Setup(e => e.CleanupTaskContainerAsync(It.IsAny<Workspace>(), task.Id, It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("CleanupTaskContainerAsync"))
            .Returns(Task.CompletedTask);

        var gitMock = new Mock<IGitWorkspaceManager>();
        gitMock.Setup(g => g.ResetTaskBranchToBaseAsync(It.IsAny<Workspace>(), task.Id, "main", It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("ResetTaskBranchToBaseAsync"))
            .Returns(Task.CompletedTask);

        var orchestrator = CreateOrchestrator(gitManager: gitMock.Object, envProvider: envMock.Object);

        // Act
        var result = await orchestrator.RestartTaskPipelineAsync(task.Id, TaskRestartGitMode.HardResetToBaseBranch);

        // Assert
        Assert.True(result);
        Assert.Equal(2, callOrder.Count);
        Assert.Equal("CleanupTaskContainerAsync", callOrder[0]);
        Assert.Equal("ResetTaskBranchToBaseAsync", callOrder[1]);
    }

    [Fact]
    public async Task RestartTaskPipelineAsync_HardReset_ClearsPipelineDataBagAndResetsState()
    {
        // Arrange
        var task = await _taskRepo.CreateWithBaselineAsync(new ForgeTask
        {
            WorkspaceId = _workspace.Id,
            Title = "Dirty Task with Diagnostics",
            Status = TaskOverallStatus.Failed,
            CurrentStageId = StandardStageIds.Implementation,
            HasExecutionRun = true,
            MaxExecutedStageOrder = 3,
            PipelineDataJson = "{\"ApprovedPlan\":\"some plan\",\"FailedTests\":[\"Test1\"]}",
            IsWaitingForHuman = true,
            InterruptedStageId = StandardStageIds.Implementation,
            InterruptedAgentProfileId = "coder",
            BaseBranch = "main"
        }, null, AuditActors.User, "init");

        // Add conversations to test superseding across Completed, Running, WaitingForHuman, and Pending
        var conv1 = await _taskRepo.StartConversationAsync(task.Id, StandardStageIds.TaskReview, WorkflowStageKind.Agentic, 1);
        await _taskRepo.CompleteConversationAsync(conv1.Id, StageExecutionStatus.Completed);

        // Truly Running conversation (started, not completed)
        var conv2 = await _taskRepo.StartConversationAsync(task.Id, StandardStageIds.Implementation, WorkflowStageKind.Agentic, 1);

        // WaitingForHuman conversation
        var conv3 = await _taskRepo.StartConversationAsync(task.Id, StandardStageIds.Implementation, WorkflowStageKind.Agentic, 2);
        await _taskRepo.CompleteConversationAsync(conv3.Id, StageExecutionStatus.WaitingForHuman);

        // Pending conversation
        var conv4 = await _taskRepo.StartConversationAsync(task.Id, StandardStageIds.CodeReviewAndFix, WorkflowStageKind.Agentic, 1);
        conv4.Status = StageExecutionStatus.Pending;

        var orchestrator = CreateOrchestrator();

        // Act
        var result = await orchestrator.RestartTaskPipelineAsync(task.Id, TaskRestartGitMode.HardResetToBaseBranch);

        // Assert
        Assert.True(result);

        var updated = await _taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(updated);
        Assert.Equal(TaskOverallStatus.Open, updated.Status);
        Assert.Equal(StandardStageIds.TaskReview, updated.CurrentStageId);
        Assert.Equal("{}", updated.PipelineDataJson);
        Assert.False(updated.HasExecutionRun);
        Assert.Equal(0, updated.MaxExecutedStageOrder);
        Assert.False(updated.IsWaitingForHuman);
        Assert.Null(updated.InterruptedStageId);
        Assert.Null(updated.InterruptedAgentProfileId);
        Assert.Null(updated.SquashCommitSha);

        // Conversations marked appropriately
        var reloadedConvs = await _taskRepo.GetConversationsAsync(task.Id);
        var reloadedConv1 = reloadedConvs.First(c => c.Id == conv1.Id);
        var reloadedConv2 = reloadedConvs.First(c => c.Id == conv2.Id);
        var reloadedConv3 = reloadedConvs.First(c => c.Id == conv3.Id);
        var reloadedConv4 = reloadedConvs.First(c => c.Id == conv4.Id);
        Assert.Equal(StageExecutionStatus.Completed, reloadedConv1.Status);
        Assert.Equal(StageExecutionStatus.Superseded, reloadedConv2.Status);
        Assert.Equal(StageExecutionStatus.Superseded, reloadedConv3.Status);
        Assert.Equal(StageExecutionStatus.Superseded, reloadedConv4.Status);

        // Task enqueued on Stage 1 tier (TaskReview)
        var enqueued = Assert.Single(_jobQueue.EnqueuedStages);
        Assert.Equal(task.Id, enqueued.TaskId);
        Assert.Equal(StageQueueTier.TaskReview, enqueued.Tier);

        // Audit History recorded
        var history = await _taskRepo.GetHistoryAsync(task.Id);
        var lastEntry = history.Last();
        Assert.Equal(AuditActors.User, lastEntry.ChangedBy);
        Assert.Contains("Hard Reset", lastEntry.ChangeReason);
    }

    [Fact]
    public async Task RestartTaskPipelineAsync_WhenGitResetFails_SetsStatusFailed_ThrowsAndDoesNotReenqueue()
    {
        // Arrange
        var task = await _taskRepo.CreateWithBaselineAsync(new ForgeTask
        {
            WorkspaceId = _workspace.Id,
            Title = "Task with Failing Reset",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.Implementation,
            BaseBranch = "main"
        }, null, AuditActors.User, "init");

        var gitMock = new Mock<IGitWorkspaceManager>();
        gitMock.Setup(g => g.ResetTaskBranchToBaseAsync(It.IsAny<Workspace>(), task.Id, "main", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitCommandException("C:/test/repo", ["reset", "--hard"], 1, "", "fatal: simulated git error"));

        var orchestrator = CreateOrchestrator(gitManager: gitMock.Object);

        // Act & Assert
        await Assert.ThrowsAsync<GitCommandException>(() =>
            orchestrator.RestartTaskPipelineAsync(task.Id, TaskRestartGitMode.HardResetToBaseBranch));

        // Verify task status was set to Failed in DB and store
        var reloaded = await _taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(reloaded);
        Assert.Equal(TaskOverallStatus.Failed, reloaded.Status);
        Assert.Equal(TaskOverallStatus.Failed, await _depStore.GetTaskStatusAsync(task.Id));

        // Verify nothing was re-enqueued
        Assert.Empty(_jobQueue.EnqueuedStages);
    }

    [Fact]
    public async Task RestartTaskPipelineAsync_ReloadsEntityAfterAcquiringLock_PreventingStaleEntityOverwrite()
    {
        // Arrange
        var task = await _taskRepo.CreateWithBaselineAsync(new ForgeTask
        {
            WorkspaceId = _workspace.Id,
            Title = "Task with Concurrency Race",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.Implementation,
            PipelineDataJson = "{\"old\":true}",
            BaseBranch = "main"
        }, null, AuditActors.User, "init");

        // Simulate that while waiting for lock, the running worker unwinds and sets specific cancellation reason/fields
        _cancellationManagerMock.Setup(c => c.IsRegistered(task.Id)).Returns(true);
        _cancellationManagerMock.Setup(c => c.Cancel(task.Id)).Callback(() =>
        {
            // Worker unwinding updates task in repo
            task.InterruptedStageId = "stage-unwound";
            _taskRepo.UpdateAsync(task).GetAwaiter().GetResult();
        }).Returns(true);

        var orchestrator = CreateOrchestrator();

        // Act
        var result = await orchestrator.RestartTaskPipelineAsync(task.Id, TaskRestartGitMode.PreserveWork);

        // Assert
        Assert.True(result);
        var reloaded = await _taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(reloaded);
        // Restart pipeline clears interruption fields on the freshly reloaded entity
        Assert.Null(reloaded.InterruptedStageId);
        Assert.Equal(TaskOverallStatus.Open, reloaded.Status);
        Assert.Equal(StandardStageIds.TaskReview, reloaded.CurrentStageId);
    }

    [Fact]
    public async Task RestartTaskPipelineAsync_SoftRestart_LeavesPipelineDataJsonAndHasExecutionRunUntouched()
    {
        // Arrange
        const string existingDataBag = "{\"ApprovedPlan\":\"keep-this-plan\",\"CustomConfig\":\"keep-me\"}";
        var task = await _taskRepo.CreateWithBaselineAsync(new ForgeTask
        {
            WorkspaceId = _workspace.Id,
            Title = "Soft Restart Task",
            Status = TaskOverallStatus.Failed,
            CurrentStageId = StandardStageIds.Implementation,
            HasExecutionRun = true,
            MaxExecutedStageOrder = 2,
            PipelineDataJson = existingDataBag,
            BaseBranch = "main"
        }, null, AuditActors.User, "init");

        var orchestrator = CreateOrchestrator();

        // Act
        var result = await orchestrator.RestartTaskPipelineAsync(task.Id, TaskRestartGitMode.PreserveWork);

        // Assert
        Assert.True(result);
        var reloaded = await _taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(reloaded);
        Assert.Equal(existingDataBag, reloaded.PipelineDataJson);
        Assert.True(reloaded.HasExecutionRun);
        Assert.Equal(2, reloaded.MaxExecutedStageOrder);
        Assert.Equal(TaskOverallStatus.Open, reloaded.Status);
        Assert.Equal(StandardStageIds.TaskReview, reloaded.CurrentStageId);
    }

    [Fact]
    public async Task ProcessTaskAsync_WhenJobSupersededWhileWaitingForLock_AbortsWithoutExecuting()
    {
        // Arrange
        var task = await _taskRepo.CreateWithBaselineAsync(new ForgeTask
        {
            WorkspaceId = _workspace.Id,
            Title = "Task with Stale Job",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.TaskReview,
            LastHangfireJobId = "job-old",
            BaseBranch = "main"
        }, null, AuditActors.User, "init");

        Task processTask;

        // Hold the task lock to simulate worker waiting on lock
        using (await _lockProvider.AcquireTaskLockAsync(_workspace.Id, task.Id))
        {
            // Background thread starts ProcessTaskAsync
            processTask = Task.Run(async () =>
            {
                var orchestrator = CreateOrchestrator();
                await orchestrator.ProcessTaskAsync(task.Id);
            });

            // Allow worker to enter ProcessTaskAsync and block on AcquireTaskLockAsync
            await Task.Delay(50);

            // While worker is waiting on the lock, simulate a restart/re-enqueue updating LastHangfireJobId
            task.LastHangfireJobId = "job-new";
            await _taskRepo.UpdateAsync(task);
        }

        // Await the background worker execution after lock is released
        await processTask;

        // Verify stage execution was not run and cancellation source was never registered
        _cancellationManagerMock.Verify(c => c.Register(task.Id, It.IsAny<CancellationToken>()), Times.Never);
    }
}
