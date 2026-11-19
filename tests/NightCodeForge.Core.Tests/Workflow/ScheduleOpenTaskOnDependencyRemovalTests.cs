using Microsoft.Extensions.Logging;
using Moq;
using NightCodeForge.Core.Concurrency;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Tests.Fixtures;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Core.Tests.Workflow;

public class ScheduleOpenTaskOnDependencyRemovalTests
{
    private static (TestOrchestratorBuilder Builder, TaskOrchestrator Orchestrator) CreateOrchestrator(
        InMemoryTaskRepository? taskRepo = null,
        InMemoryTaskDependencyStore? depStore = null,
        FakeTaskJobQueue? jobQueue = null,
        IWorkflowPipelineProvider? pipelineProvider = null)
    {
        var builder = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo ?? new InMemoryTaskRepository(),
            DependencyStore = depStore ?? new InMemoryTaskDependencyStore(),
            JobQueue = jobQueue ?? new FakeTaskJobQueue()
        };

        if (pipelineProvider != null)
        {
            builder.PipelineProvider = pipelineProvider;
        }

        builder.WorkspaceRepository.CreateAsync(new Workspace
        {
            Id = 1,
            Name = "Test Workspace",
            RepositoryPath = "C:/repo",
            DefaultBaseBranch = "main"
        }).GetAwaiter().GetResult();

        return (builder, builder.Build());
    }

    [Fact]
    public async Task TryScheduleOpenTaskIfReadyAsync_OpenTaskWithNoDependencies_EnqueuesToInitialStageUpdatesJobIdAndLogs()
    {
        var taskRepo = new InMemoryTaskRepository();
        var depStore = new InMemoryTaskDependencyStore();
        var jobQueue = new FakeTaskJobQueue();
        var (_, orchestrator) = CreateOrchestrator(taskRepo, depStore, jobQueue);

        var task = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 10,
            WorkspaceId = 1,
            Title = "Unblocked Open Task",
            Status = TaskOverallStatus.Open,
            Priority = TaskPriority.High,
            IsDeferredByPause = true
        });
        depStore.AddTask(task.Id, 1, TaskOverallStatus.Open);

        var scheduled = await orchestrator.TryScheduleOpenTaskIfReadyAsync(task.Id);

        Assert.True(scheduled);
        var enqueued = Assert.Single(jobQueue.EnqueuedStages);
        Assert.Equal(task.Id, enqueued.TaskId);
        Assert.Equal(StageQueueTier.TaskReview, enqueued.Tier);
        Assert.Equal(TaskPriority.High, enqueued.Priority);

        var reloaded = await taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(reloaded);
        Assert.False(reloaded!.IsDeferredByPause);
        Assert.False(string.IsNullOrWhiteSpace(reloaded.LastHangfireJobId));
        Assert.Contains(reloaded.ExecutionLogs, l =>
            l.Level == LogLevel.Information &&
            l.Message.Contains("unblocked and scheduled", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TryScheduleOpenTaskIfReadyAsync_OpenTaskWithValidCurrentStageId_UsesCurrentStageQueueTier()
    {
        var taskRepo = new InMemoryTaskRepository();
        var jobQueue = new FakeTaskJobQueue();
        var (_, orchestrator) = CreateOrchestrator(taskRepo, jobQueue: jobQueue);

        var task = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 11,
            WorkspaceId = 1,
            Title = "Open Task at Implementation Stage",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.Implementation,
            Priority = TaskPriority.Normal
        });

        var scheduled = await orchestrator.TryScheduleOpenTaskIfReadyAsync(task.Id);

        Assert.True(scheduled);
        var enqueued = Assert.Single(jobQueue.EnqueuedStages);
        Assert.Equal(StageQueueTier.Implementation, enqueued.Tier);
        Assert.Contains(task.Id, jobQueue.ImplementationQueue);
    }

    [Fact]
    public async Task TryScheduleOpenTaskIfReadyAsync_OpenTaskWithClosedPrerequisites_EnqueuesCleanly()
    {
        var taskRepo = new InMemoryTaskRepository();
        var depStore = new InMemoryTaskDependencyStore();
        var jobQueue = new FakeTaskJobQueue();
        var (_, orchestrator) = CreateOrchestrator(taskRepo, depStore, jobQueue);

        var closedRepoPrereq = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 20,
            WorkspaceId = 1,
            Title = "Closed Repo Prereq",
            Status = TaskOverallStatus.Closed
        });
        depStore.AddTask(21, 1, TaskOverallStatus.Closed);

        var openTask = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 22,
            WorkspaceId = 1,
            Title = "Ready Open Task",
            Status = TaskOverallStatus.Open
        }, dependsOn: [closedRepoPrereq.Id]);
        depStore.AddTask(openTask.Id, 1, TaskOverallStatus.Open);
        depStore.AddDependency(1, openTask.Id, 21);

        var scheduled = await orchestrator.TryScheduleOpenTaskIfReadyAsync(openTask.Id);

        Assert.True(scheduled);
        Assert.Single(jobQueue.EnqueuedStages);
    }

    [Fact]
    public async Task TryScheduleOpenTaskIfReadyAsync_OpenTaskWithUnclosedPrerequisiteInRepository_DoesNotEnqueue()
    {
        var taskRepo = new InMemoryTaskRepository();
        var depStore = new InMemoryTaskDependencyStore();
        var jobQueue = new FakeTaskJobQueue();
        var (_, orchestrator) = CreateOrchestrator(taskRepo, depStore, jobQueue);

        var closedPrereq = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 30,
            WorkspaceId = 1,
            Title = "Closed Prereq",
            Status = TaskOverallStatus.Closed
        });
        var openPrereq = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 31,
            WorkspaceId = 1,
            Title = "Still Open Prereq",
            Status = TaskOverallStatus.Open
        });
        var target = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 32,
            WorkspaceId = 1,
            Title = "Target Task",
            Status = TaskOverallStatus.Open
        }, dependsOn: [closedPrereq.Id, openPrereq.Id]);

        var scheduled = await orchestrator.TryScheduleOpenTaskIfReadyAsync(target.Id);

        Assert.False(scheduled);
        Assert.Empty(jobQueue.EnqueuedStages);
        Assert.Null((await taskRepo.GetByIdAsync(target.Id))!.LastHangfireJobId);
    }

    [Fact]
    public async Task TryScheduleOpenTaskIfReadyAsync_OpenTaskWithUnclosedPrerequisiteInDependencyStore_DoesNotEnqueue()
    {
        var taskRepo = new InMemoryTaskRepository();
        var depStore = new InMemoryTaskDependencyStore();
        var jobQueue = new FakeTaskJobQueue();
        var (_, orchestrator) = CreateOrchestrator(taskRepo, depStore, jobQueue);

        var target = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 40,
            WorkspaceId = 1,
            Title = "Store Blocked Task",
            Status = TaskOverallStatus.Open
        });
        depStore.AddTask(41, 1, TaskOverallStatus.Reviewed);
        depStore.AddTask(target.Id, 1, TaskOverallStatus.Open);
        depStore.AddDependency(1, target.Id, 41);

        var scheduled = await orchestrator.TryScheduleOpenTaskIfReadyAsync(target.Id);

        Assert.False(scheduled);
        Assert.Empty(jobQueue.EnqueuedStages);
    }

    [Theory]
    [InlineData(TaskOverallStatus.Draft)]
    [InlineData(TaskOverallStatus.Suggested)]
    [InlineData(TaskOverallStatus.Reviewed)]
    [InlineData(TaskOverallStatus.Implemented)]
    [InlineData(TaskOverallStatus.Ready)]
    [InlineData(TaskOverallStatus.Closed)]
    [InlineData(TaskOverallStatus.Rejected)]
    [InlineData(TaskOverallStatus.Cancelled)]
    [InlineData(TaskOverallStatus.Failed)]
    public async Task TryScheduleOpenTaskIfReadyAsync_NonOpenStatus_DoesNotEnqueue(TaskOverallStatus status)
    {
        var taskRepo = new InMemoryTaskRepository();
        var jobQueue = new FakeTaskJobQueue();
        var (_, orchestrator) = CreateOrchestrator(taskRepo, jobQueue: jobQueue);

        var task = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 50,
            WorkspaceId = 1,
            Title = $"Status {status}",
            Status = status
        });

        var scheduled = await orchestrator.TryScheduleOpenTaskIfReadyAsync(task.Id);

        Assert.False(scheduled);
        Assert.Empty(jobQueue.EnqueuedStages);
    }

    [Fact]
    public async Task TryScheduleOpenTaskIfReadyAsync_WhenHasExecutionRunOrWaitingForHuman_DoesNotEnqueue()
    {
        var taskRepo = new InMemoryTaskRepository();
        var depStore = new InMemoryTaskDependencyStore();
        var jobQueue = new FakeTaskJobQueue();
        var (_, orchestrator) = CreateOrchestrator(taskRepo, depStore, jobQueue);

        var executedTask = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 60,
            WorkspaceId = 1,
            Title = "Executed Task",
            Status = TaskOverallStatus.Open,
            HasExecutionRun = true
        });
        var waitingEntityTask = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 61,
            WorkspaceId = 1,
            Title = "Waiting Entity Task",
            Status = TaskOverallStatus.Open,
            IsWaitingForHuman = true
        });
        var waitingStoreTask = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 62,
            WorkspaceId = 1,
            Title = "Waiting Store Task",
            Status = TaskOverallStatus.Open,
            IsWaitingForHuman = false
        });
        depStore.AddTask(waitingStoreTask.Id, 1, TaskOverallStatus.Open, isWaitingForHuman: true);

        Assert.False(await orchestrator.TryScheduleOpenTaskIfReadyAsync(executedTask.Id));
        Assert.False(await orchestrator.TryScheduleOpenTaskIfReadyAsync(waitingEntityTask.Id));
        Assert.False(await orchestrator.TryScheduleOpenTaskIfReadyAsync(waitingStoreTask.Id));
        Assert.Empty(jobQueue.EnqueuedStages);
    }

    [Fact]
    public async Task TryScheduleOpenTaskIfReadyAsync_ActiveJobVsStaleJob_SkipsDuplicateAndReEnqueuesStale()
    {
        var taskRepo = new InMemoryTaskRepository();
        var jobQueue = new FakeTaskJobQueue();
        var (_, orchestrator) = CreateOrchestrator(taskRepo, jobQueue: jobQueue);

        var activeJobTask = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 70,
            WorkspaceId = 1,
            Title = "Active Job Task",
            Status = TaskOverallStatus.Open,
            LastHangfireJobId = "active-job-70"
        });
        var deletedJobTask = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 71,
            WorkspaceId = 1,
            Title = "Deleted Job Task",
            Status = TaskOverallStatus.Open,
            LastHangfireJobId = "deleted-job-71"
        });
        jobQueue.Delete("deleted-job-71");

        var completedJobTask = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 72,
            WorkspaceId = 1,
            Title = "Completed Job Task",
            Status = TaskOverallStatus.Open,
            LastHangfireJobId = "completed-job-72"
        });
        jobQueue.CompletedJobs.Add("completed-job-72");

        Assert.False(await orchestrator.TryScheduleOpenTaskIfReadyAsync(activeJobTask.Id));
        Assert.Equal("active-job-70", (await taskRepo.GetByIdAsync(activeJobTask.Id))!.LastHangfireJobId);

        Assert.True(await orchestrator.TryScheduleOpenTaskIfReadyAsync(deletedJobTask.Id));
        Assert.True(await orchestrator.TryScheduleOpenTaskIfReadyAsync(completedJobTask.Id));

        Assert.Equal(2, jobQueue.EnqueuedStages.Count);
        Assert.NotEqual("deleted-job-71", (await taskRepo.GetByIdAsync(deletedJobTask.Id))!.LastHangfireJobId);
        Assert.NotEqual("completed-job-72", (await taskRepo.GetByIdAsync(completedJobTask.Id))!.LastHangfireJobId);
    }

    [Fact]
    public async Task TryScheduleOpenTaskIfReadyAsync_MissingTaskOrMissingStage_FailsFast()
    {
        var taskRepo = new InMemoryTaskRepository();
        var emptyPipelineProvider = new DefaultWorkflowPipelineProvider(new WorkflowPipeline(Array.Empty<IWorkflowStage>()));
        var (_, orchestrator) = CreateOrchestrator(taskRepo, pipelineProvider: emptyPipelineProvider);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => orchestrator.TryScheduleOpenTaskIfReadyAsync(9999));

        var task = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 80,
            WorkspaceId = 1,
            Title = "No Stages Task",
            Status = TaskOverallStatus.Open
        });

        await Assert.ThrowsAsync<WorkflowValidationException>(() => orchestrator.TryScheduleOpenTaskIfReadyAsync(task.Id));
    }

    [Fact]
    public async Task ProcessTaskAsync_WhenEnsureReadyForExecutionExitsEarly_ClearsStaleLastHangfireJobId()
    {
        var taskRepo = new InMemoryTaskRepository();
        var depStore = new InMemoryTaskDependencyStore();
        var jobQueue = new FakeTaskJobQueue();
        var (_, orchestrator) = CreateOrchestrator(taskRepo, depStore, jobQueue);

        var prereq = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 90,
            WorkspaceId = 1,
            Title = "Unclosed Prerequisite",
            Status = TaskOverallStatus.Open
        });
        var blockedTask = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 91,
            WorkspaceId = 1,
            Title = "Blocked Task With Stale Job",
            Status = TaskOverallStatus.Open,
            LastHangfireJobId = "stale-blocked-job-91"
        }, dependsOn: [prereq.Id]);

        depStore.AddTask(prereq.Id, 1, TaskOverallStatus.Open);
        depStore.AddTask(blockedTask.Id, 1, TaskOverallStatus.Open);
        depStore.AddDependency(1, blockedTask.Id, prereq.Id);

        await orchestrator.ProcessTaskAsync(blockedTask.Id);

        var reloaded = await taskRepo.GetByIdAsync(blockedTask.Id);
        Assert.NotNull(reloaded);
        Assert.Null(reloaded!.LastHangfireJobId);
        Assert.Null(blockedTask.LastHangfireJobId);
    }

    [Fact]
    public async Task ProcessTaskAsync_WhenBlockedTaskSupersededWhileWaitingForLock_DoesNotClobberNewerLastHangfireJobId()
    {
        var taskRepo = new InMemoryTaskRepository();
        var depStore = new InMemoryTaskDependencyStore();
        var lockEnteredTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lockProvider = new SignalingTaskLockProvider(
            new WorkspaceLockProvider(),
            callIndex =>
            {
                if (callIndex == 1)
                {
                    lockEnteredTcs.TrySetResult();
                }
            });

        var builder = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            DependencyStore = depStore,
            LockProvider = lockProvider
        };
        await builder.WorkspaceRepository.CreateAsync(new Workspace
        {
            Id = 1,
            Name = "Test Workspace",
            RepositoryPath = "C:/repo",
            DefaultBaseBranch = "main"
        });
        var orchestrator = builder.Build();

        var prereq = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 92,
            WorkspaceId = 1,
            Title = "Unclosed Prerequisite",
            Status = TaskOverallStatus.Open
        });
        var blockedTask = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 93,
            WorkspaceId = 1,
            Title = "Superseded Blocked Task",
            Status = TaskOverallStatus.Open,
            LastHangfireJobId = "job-old"
        }, dependsOn: [prereq.Id]);

        depStore.AddTask(prereq.Id, 1, TaskOverallStatus.Open);
        depStore.AddTask(blockedTask.Id, 1, TaskOverallStatus.Open);
        depStore.AddDependency(1, blockedTask.Id, prereq.Id);

        Task processTask;
        using (await lockProvider.Inner.AcquireTaskLockAsync(1, blockedTask.Id))
        {
            processTask = Task.Run(() => orchestrator.ProcessTaskAsync(blockedTask.Id));
            await lockEnteredTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

            blockedTask.LastHangfireJobId = "job-new";
            await taskRepo.UpdateAsync(blockedTask);
        }

        await processTask.WaitAsync(TimeSpan.FromSeconds(5));

        var reloaded = await taskRepo.GetByIdAsync(blockedTask.Id);
        Assert.NotNull(reloaded);
        Assert.Equal("job-new", reloaded!.LastHangfireJobId);
    }

    [Fact]
    public async Task ProcessTaskAsync_And_TryScheduleOpenTaskIfReadyAsync_ConcurrentUnblock_DoesNotLoseWakeup()
    {
        var taskRepo = new InMemoryTaskRepository();
        var innerDepStore = new InMemoryTaskDependencyStore();
        var enteredReadinessTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowReadinessFinishTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var signalingDepStore = new GateFirstPrereqReadDependencyStore(
            innerDepStore,
            targetTaskId: 95,
            enteredReadinessTcs,
            allowReadinessFinishTcs);

        var lockProvider = new SignalingTaskLockProvider(
            new WorkspaceLockProvider(),
            callIndex =>
            {
                if (callIndex == 2)
                {
                    allowReadinessFinishTcs.TrySetResult();
                }
            });

        var jobQueue = new FakeTaskJobQueue();
        var builder = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            DependencyStore = signalingDepStore,
            JobQueue = jobQueue,
            LockProvider = lockProvider
        };
        await builder.WorkspaceRepository.CreateAsync(new Workspace
        {
            Id = 1,
            Name = "Test Workspace",
            RepositoryPath = "C:/repo",
            DefaultBaseBranch = "main"
        });
        var orchestrator = builder.Build();

        var prereq = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 94,
            WorkspaceId = 1,
            Title = "Initial Blocker",
            Status = TaskOverallStatus.Open
        });
        var task = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 95,
            WorkspaceId = 1,
            Title = "Concurrently Unblocked Task",
            Status = TaskOverallStatus.Open,
            LastHangfireJobId = "job-active-1"
        }, dependsOn: [prereq.Id]);

        innerDepStore.AddTask(prereq.Id, 1, TaskOverallStatus.Open);
        innerDepStore.AddTask(task.Id, 1, TaskOverallStatus.Open);
        innerDepStore.AddDependency(1, task.Id, prereq.Id);

        // Start ProcessTaskAsync for job-active-1; it acquires the lock and pauses inside EnsureReadyForExecutionAsync
        var processTask = Task.Run(() => orchestrator.ProcessTaskAsync(task.Id));
        await enteredReadinessTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // While job-active-1 still holds the lock (and jobQueue.IsJobActive("job-active-1") == true),
        // unblock the task and call TryScheduleOpenTaskIfReadyAsync.
        await taskRepo.UpdateWithHistoryAsync(task, Array.Empty<int>(), AuditActors.User, "Unblocked concurrently");
        innerDepStore.RemoveDependency(1, task.Id, prereq.Id);

        var scheduled = await orchestrator.TryScheduleOpenTaskIfReadyAsync(task.Id).WaitAsync(TimeSpan.FromSeconds(5));
        await processTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(scheduled);
        var enqueued = Assert.Single(jobQueue.EnqueuedStages);
        Assert.Equal(task.Id, enqueued.TaskId);
        var reloaded = await taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(reloaded);
        Assert.False(string.IsNullOrWhiteSpace(reloaded!.LastHangfireJobId));
        Assert.NotEqual("job-active-1", reloaded.LastHangfireJobId);
    }

    [Fact]
    public async Task TryScheduleOpenTaskIfReadyAsync_ReplacingStaleJobId_ClearsStaleJobIdBeforeEnqueue_PreventingSelfSupersedeAbort()
    {
        var taskRepo = new InMemoryTaskRepository();
        var depStore = new InMemoryTaskDependencyStore();
        var workerWaitingOnLockTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lockProvider = new SignalingTaskLockProvider(
            new WorkspaceLockProvider(),
            callIndex =>
            {
                if (callIndex == 2)
                {
                    workerWaitingOnLockTcs.TrySetResult();
                }
            });

        var stageExecuted = false;
        var stage = new TestWorkflowStage(StandardStageIds.TaskReview, WorkflowStageKind.Agentic)
        {
            QueueTier = StageQueueTier.TaskReview,
            Handler = (_, _) =>
            {
                stageExecuted = true;
                return Task.FromResult(StageExecutionResult.Success());
            }
        };
        var pipelineProvider = new DefaultWorkflowPipelineProvider(new WorkflowPipeline([stage]));

        Task? concurrentWorkerTask = null;
        string? observedJobIdDuringEnqueue = "not-checked";
        TaskOrchestrator? orchestrator = null;

        var jobQueue = new InterceptingEnqueueTaskJobQueue(onEnqueueStage: taskId =>
        {
            observedJobIdDuringEnqueue = taskRepo.GetByIdAsync(taskId).GetAwaiter().GetResult()!.LastHangfireJobId;
            concurrentWorkerTask = Task.Run(() => orchestrator!.ProcessTaskAsync(taskId));
            workerWaitingOnLockTcs.Task.GetAwaiter().GetResult();
        });

        var builder = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            DependencyStore = depStore,
            JobQueue = jobQueue,
            LockProvider = lockProvider,
            PipelineProvider = pipelineProvider
        };
        await builder.WorkspaceRepository.CreateAsync(new Workspace
        {
            Id = 1,
            Name = "Test Workspace",
            RepositoryPath = "C:/repo",
            DefaultBaseBranch = "main"
        });
        orchestrator = builder.Build();

        var task = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 96,
            WorkspaceId = 1,
            Title = "Task With Stale Job",
            Status = TaskOverallStatus.Open,
            LastHangfireJobId = "stale-job-96"
        });
        depStore.AddTask(task.Id, 1, TaskOverallStatus.Open);
        jobQueue.Delete("stale-job-96");

        var scheduled = await orchestrator.TryScheduleOpenTaskIfReadyAsync(task.Id).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(scheduled);
        Assert.Null(observedJobIdDuringEnqueue);
        Assert.NotNull(concurrentWorkerTask);

        await concurrentWorkerTask!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(stageExecuted);
    }

    private sealed class SignalingTaskLockProvider : IWorkspaceLockProvider
    {
        private readonly Action<int> _onAcquireTaskLockCalled;
        private int _taskLockCalls;

        public SignalingTaskLockProvider(WorkspaceLockProvider inner, Action<int> onAcquireTaskLockCalled)
        {
            Inner = inner;
            _onAcquireTaskLockCalled = onAcquireTaskLockCalled;
        }

        public WorkspaceLockProvider Inner { get; }

        public Task<IDisposable> AcquireLockAsync(int workspaceId, CancellationToken cancellationToken = default) =>
            Inner.AcquireLockAsync(workspaceId, cancellationToken);

        public bool TryAcquireLock(int workspaceId, out IDisposable? lockReleaser) =>
            Inner.TryAcquireLock(workspaceId, out lockReleaser);

        public Task<(bool Acquired, IDisposable? LockReleaser)> TryAcquireLockAsync(int workspaceId, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Inner.TryAcquireLockAsync(workspaceId, timeout, cancellationToken);

        public Task<IDisposable> AcquireTaskLockAsync(int workspaceId, int taskId, CancellationToken cancellationToken = default)
        {
            var callIndex = Interlocked.Increment(ref _taskLockCalls);
            _onAcquireTaskLockCalled(callIndex);
            return Inner.AcquireTaskLockAsync(workspaceId, taskId, cancellationToken);
        }

        public bool TryAcquireTaskLock(int workspaceId, int taskId, out IDisposable? lockReleaser) =>
            Inner.TryAcquireTaskLock(workspaceId, taskId, out lockReleaser);

        public Task<(bool Acquired, IDisposable? LockReleaser)> TryAcquireTaskLockAsync(int workspaceId, int taskId, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Inner.TryAcquireTaskLockAsync(workspaceId, taskId, timeout, cancellationToken);

        public bool IsTaskLocked(int workspaceId, int taskId) => Inner.IsTaskLocked(workspaceId, taskId);
        public IReadOnlyList<ActiveLockSnapshot> GetActiveLocks() => Inner.GetActiveLocks();
        public bool EmergencyRelease(int workspaceId, int? taskId = null) => Inner.EmergencyRelease(workspaceId, taskId);
        public void EmergencyReleaseAll() => Inner.EmergencyReleaseAll();
    }

    private sealed class GateFirstPrereqReadDependencyStore : ITaskDependencyStore
    {
        private readonly ITaskDependencyStore _inner;
        private readonly int _targetTaskId;
        private readonly TaskCompletionSource _enteredTcs;
        private readonly TaskCompletionSource _allowFinishTcs;
        private int _gated;

        public GateFirstPrereqReadDependencyStore(
            ITaskDependencyStore inner,
            int targetTaskId,
            TaskCompletionSource enteredTcs,
            TaskCompletionSource allowFinishTcs)
        {
            _inner = inner;
            _targetTaskId = targetTaskId;
            _enteredTcs = enteredTcs;
            _allowFinishTcs = allowFinishTcs;
        }

        public Task<IReadOnlyList<(int TaskId, int DependsOnTaskId)>> GetDependenciesForWorkspaceAsync(int workspaceId, CancellationToken cancellationToken = default) =>
            _inner.GetDependenciesForWorkspaceAsync(workspaceId, cancellationToken);

        public async Task<IReadOnlyList<int>> GetPrerequisiteTaskIdsAsync(int taskId, CancellationToken cancellationToken = default)
        {
            var result = await _inner.GetPrerequisiteTaskIdsAsync(taskId, cancellationToken);
            if (taskId == _targetTaskId && Interlocked.Exchange(ref _gated, 1) == 0)
            {
                _enteredTcs.TrySetResult();
                await _allowFinishTcs.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }
            return result;
        }

        public Task<IReadOnlyList<int>> GetDependentTaskIdsAsync(int prerequisiteTaskId, CancellationToken cancellationToken = default) =>
            _inner.GetDependentTaskIdsAsync(prerequisiteTaskId, cancellationToken);

        public Task<TaskOverallStatus?> GetTaskStatusAsync(int taskId, CancellationToken cancellationToken = default) =>
            _inner.GetTaskStatusAsync(taskId, cancellationToken);

        public Task UpdateTaskStatusAsync(int taskId, TaskOverallStatus newStatus, CancellationToken cancellationToken = default) =>
            _inner.UpdateTaskStatusAsync(taskId, newStatus, cancellationToken);

        public Task<bool> IsTaskWaitingForHumanAsync(int taskId, CancellationToken cancellationToken = default) =>
            _inner.IsTaskWaitingForHumanAsync(taskId, cancellationToken);

        public Task SetWaitingForHumanAsync(int taskId, bool isWaiting, CancellationToken cancellationToken = default) =>
            _inner.SetWaitingForHumanAsync(taskId, isWaiting, cancellationToken);
    }

    private sealed class InterceptingEnqueueTaskJobQueue : ITaskJobQueue
    {
        private readonly FakeTaskJobQueue _inner = new();
        private readonly Action<int> _onEnqueueStage;

        public InterceptingEnqueueTaskJobQueue(Action<int> onEnqueueStage)
        {
            _onEnqueueStage = onEnqueueStage;
        }

        public string EnqueueUrgent(int taskId, DateTime? originalEnqueuedAt = null) =>
            _inner.EnqueueUrgent(taskId, originalEnqueuedAt);

        public string EnqueueStage(int taskId, StageQueueTier tier, int priority, DateTime? originalEnqueuedAt = null)
        {
            _onEnqueueStage(taskId);
            return _inner.EnqueueStage(taskId, tier, priority, originalEnqueuedAt);
        }

        public bool Delete(string jobId) => _inner.Delete(jobId);

        public Task<bool> HasUrgentJobsWaitingAsync(CancellationToken cancellationToken = default) =>
            _inner.HasUrgentJobsWaitingAsync(cancellationToken);

        public bool IsJobActive(string? jobId) => _inner.IsJobActive(jobId);
    }
}
