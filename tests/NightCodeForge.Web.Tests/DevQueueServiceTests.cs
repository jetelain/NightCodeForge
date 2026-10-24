using Hangfire;
using Hangfire.Common;
using Hangfire.MemoryStorage;
using Hangfire.States;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Infrastructure.BackgroundJobs;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class DevQueueServiceTests : IDisposable
{
    private readonly MockLockProvider _lockProvider;
    private readonly MockBackgroundJobClient _jobClient;
    private readonly MockTaskJobQueue _jobQueue;
    private readonly InMemoryTaskRepository _taskRepo;
    private readonly InMemoryWorkspaceRepository _workspaceRepo;
    private readonly TestDevQueueMarkdownSyncService _markdownSync;
    private readonly Mock<ITaskCancellationManager> _cancellationManager;
    private readonly Mock<IWorkspacePathResolver> _pathResolver;
    private readonly IDevQueueSkippedJobRegistry _skippedJobRegistry = new DevQueueSkippedJobRegistry();
    private readonly ServiceProvider _serviceProvider;
    private readonly JobStorage _jobStorage = new MemoryStorage();
    private readonly string _tempDir;

    public DevQueueServiceTests()
    {
        _lockProvider = new MockLockProvider();
        _jobClient = new MockBackgroundJobClient(_jobStorage);
        _jobQueue = new MockTaskJobQueue();
        _taskRepo = new InMemoryTaskRepository();
        _workspaceRepo = new InMemoryWorkspaceRepository();
        _markdownSync = new TestDevQueueMarkdownSyncService();
        _cancellationManager = new Mock<ITaskCancellationManager>();
        _pathResolver = new Mock<IWorkspacePathResolver>();

        var services = new ServiceCollection();
        services.AddSingleton<ITaskRepository>(_taskRepo);
        services.AddSingleton<IWorkspaceRepository>(_workspaceRepo);
        services.AddSingleton<ITaskMarkdownSyncService>(_markdownSync);

        var mockStage = new Mock<IWorkflowStage>();
        mockStage.Setup(s => s.StageId).Returns("task-review");
        mockStage.Setup(s => s.QueueTier).Returns(StageQueueTier.TaskReview);

        var mockImplStage = new Mock<IWorkflowStage>();
        mockImplStage.Setup(s => s.StageId).Returns("deterministic-post-impl");
        mockImplStage.Setup(s => s.QueueTier).Returns(StageQueueTier.Implementation);

        var pipeline = new WorkflowPipeline(new[] { mockStage.Object, mockImplStage.Object });
        services.AddSingleton<IWorkflowPipelineProvider>(new DefaultWorkflowPipelineProvider(pipeline));

        _serviceProvider = services.BuildServiceProvider();

        _tempDir = Path.Combine(Path.GetTempPath(), "ncf-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        PurgeJobStorage();
    }

    private void PurgeJobStorage()
    {
        var monitoring = _jobStorage.GetMonitoringApi();
        var client = new BackgroundJobClient(_jobStorage);
        foreach (var q in HangfireQueues.All)
        {
            var jobs = monitoring.EnqueuedJobs(q, 0, 1000);
            foreach (var (jobId, _) in jobs)
            {
                try
                {
                    client.Delete(jobId);
                }
                catch (Exception ex)
                {
                    // Justification: Best-effort test cleanup if job was already removed
                    _ = ex.Message;
                }
            }
        }
    }

    private DevQueueService CreateService(IDevQueueSkippedJobRegistry? registry = null) => new(
        _lockProvider,
        _jobClient,
        _jobQueue,
        _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
        _jobStorage,
        _cancellationManager.Object,
        _pathResolver.Object,
        registry ?? _skippedJobRegistry,
        NullLogger<DevQueueService>.Instance);

    [Fact]
    public async Task GetSnapshotAsync_ReturnsActiveLocks_WhenNoJobStorage()
    {
        _lockProvider.ActiveLocks.Add(new ActiveLockSnapshot(
            WorkspaceId: 1,
            TaskId: null,
            LockType: "WorkspaceExclusive",
            AcquiredAt: DateTime.UtcNow.AddMinutes(-5),
            DurationHeld: TimeSpan.FromMinutes(5)));

        var service = CreateService();

        var snapshot = await service.GetSnapshotAsync();

        Assert.NotNull(snapshot);
        Assert.Single(snapshot.ActiveLocks);
        Assert.Equal(1, snapshot.ActiveLocks[0].WorkspaceId);
        Assert.Equal("WorkspaceExclusive", snapshot.ActiveLocks[0].LockType);
        Assert.Empty(snapshot.UrgentJobs);
        Assert.Empty(snapshot.InferenceJobs);
    }

    [Fact]
    public async Task PromoteToUrgentAsync_WithJobId_ChangesJobState()
    {
        var service = CreateService();

        var result = await service.PromoteToUrgentAsync("job-123", 42);

        Assert.True(result);
        Assert.Equal("job-123", _jobClient.LastChangedJobId);
        Assert.Equal(HangfireQueues.LlmUrgent, _jobClient.LastChangedStateName);
    }

    [Fact]
    public async Task PromoteToUrgentAsync_WithoutJobId_EnqueuesToUrgentQueue()
    {
        var service = CreateService();

        var result = await service.PromoteToUrgentAsync("", 99);

        Assert.True(result);
        Assert.Equal(99, _jobQueue.LastEnqueuedUrgentTaskId);
    }

    [Fact]
    public async Task CancelJobAsync_DeletesJobAndUpdatesTaskStatus()
    {
        var task = new ForgeTask
        {
            Id = 55,
            WorkspaceId = 1,
            Title = "Task to Cancel",
            Status = TaskOverallStatus.Open
        };
        await _taskRepo.CreateAsync(task);

        var service = CreateService();

        var result = await service.CancelJobAsync("job-55", 55);

        Assert.True(result);
        Assert.Equal("job-55", _jobClient.LastDeletedJobId);

        var updated = await _taskRepo.GetByIdAsync(55);
        Assert.NotNull(updated);
        Assert.Equal(TaskOverallStatus.Cancelled, updated.Status);
    }

    [Fact]
    public async Task CancelJobAsync_WhenTaskCancelled_ArchivesMarkdownTaskFile()
    {
        var ws = new Workspace
        {
            Id = 1,
            Name = "Test Workspace",
            RepositoryPath = _tempDir
        };
        await _workspaceRepo.CreateAsync(ws);

        var task = new ForgeTask
        {
            Id = 88,
            WorkspaceId = 1,
            Title = "Task to Cancel and Archive",
            Status = TaskOverallStatus.Open
        };
        await _taskRepo.CreateAsync(task);

        var service = CreateService();

        var result = await service.CancelJobAsync("job-88", 88);

        Assert.True(result);
        Assert.Single(_markdownSync.ArchivedCalls);
        Assert.Equal(88, _markdownSync.ArchivedCalls[0].Task.Id);
        Assert.Equal(_tempDir, _markdownSync.ArchivedCalls[0].WorkspacePath);
        Assert.True(_markdownSync.ArchivedCalls[0].Archive);
    }

    [Fact]
    public async Task RequeueJobAsync_RequeuesJob()
    {
        var service = CreateService();

        var result = await service.RequeueJobAsync("job-fail-1", 77);

        Assert.True(result);
        Assert.Equal("job-fail-1", _jobClient.LastRequeuedJobId);
    }

    [Fact]
    public async Task EmergencyReleaseLockAsync_ReleasesLockAndDeletesIndexLock()
    {
        var gitDir = Path.Combine(_tempDir, ".git");
        Directory.CreateDirectory(gitDir);
        var indexLock = Path.Combine(gitDir, "index.lock");
        await File.WriteAllTextAsync(indexLock, "lock");

        var workspace = new Workspace
        {
            Id = 10,
            Name = "Crash Repo",
            RepositoryPath = _tempDir
        };
        await _workspaceRepo.CreateAsync(workspace);

        var service = CreateService();

        var result = await service.EmergencyReleaseLockAsync(10);

        Assert.True(result);
        Assert.Equal(10, _lockProvider.LastEmergencyWorkspaceId);
        Assert.False(File.Exists(indexLock), ".git/index.lock should have been purged");
    }

    [Fact]
    public async Task CancelJobAsync_CancelsActiveTaskOnCancellationManager()
    {
        var task = new ForgeTask
        {
            Id = 66,
            WorkspaceId = 1,
            Title = "Task Active Cancellation",
            Status = TaskOverallStatus.Open
        };
        await _taskRepo.CreateAsync(task);

        var service = CreateService();

        var result = await service.CancelJobAsync("job-66", 66);

        Assert.True(result);
        _cancellationManager.Verify(c => c.Cancel(66), Times.Once);
    }

    [Fact]
    public async Task EmergencyReleaseLockAsync_PurgesLocksInWorktreeLinkedGitDir()
    {
        var worktreeDir = Path.Combine(_tempDir, "worktree-1");
        Directory.CreateDirectory(worktreeDir);
        var targetGitDir = Path.Combine(_tempDir, "main-repo", ".git", "worktrees", "worktree-1");
        Directory.CreateDirectory(targetGitDir);

        var gitFile = Path.Combine(worktreeDir, ".git");
        await File.WriteAllTextAsync(gitFile, $"gitdir: {targetGitDir}");

        var indexLock = Path.Combine(targetGitDir, "index.lock");
        var headLock = Path.Combine(targetGitDir, "HEAD.lock");
        await File.WriteAllTextAsync(indexLock, "lock");
        await File.WriteAllTextAsync(headLock, "lock");

        var workspace = new Workspace
        {
            Id = 11,
            Name = "Worktree Repo",
            RepositoryPath = worktreeDir
        };
        await _workspaceRepo.CreateAsync(workspace);

        var service = CreateService();

        var result = await service.EmergencyReleaseLockAsync(11);

        Assert.True(result);
        Assert.False(File.Exists(indexLock), "Worktree target index.lock should be purged");
        Assert.False(File.Exists(headLock), "Worktree target HEAD.lock should be purged");
    }

    [Fact]
    public async Task EmergencyReleaseLockAsync_PurgesLocksInExt4Mirror()
    {
        var mirrorDir = Path.Combine(_tempDir, "ext4-mirror");
        var gitDir = Path.Combine(mirrorDir, ".git");
        Directory.CreateDirectory(gitDir);
        var indexLock = Path.Combine(gitDir, "index.lock");
        await File.WriteAllTextAsync(indexLock, "lock");

        var workspace = new Workspace
        {
            Id = 12,
            Name = "WSL Repo",
            RepositoryPath = _tempDir,
            Ext4MirrorPath = mirrorDir
        };
        await _workspaceRepo.CreateAsync(workspace);

        var service = CreateService();

        var result = await service.EmergencyReleaseLockAsync(12);

        Assert.True(result);
        Assert.False(File.Exists(indexLock), "Ext4 mirror index.lock should be purged");
    }

    [Fact]
    public async Task RescheduleTaskAsync_RestartPipelineTrue_ResetsSquashCommitShaAndStatusAndStage()
    {
        var task = new ForgeTask
        {
            Id = 301,
            WorkspaceId = 1,
            Title = "Failed Task Restart",
            Status = TaskOverallStatus.Failed,
            CurrentStageId = "deterministic-post-impl",
            SquashCommitSha = "deadbeef1234567890abcdef1234567890abcdef",
            Priority = TaskPriority.Normal
        };
        await _taskRepo.CreateAsync(task);

        var service = CreateService();

        var result = await service.RescheduleTaskAsync(301, restartPipeline: true);

        Assert.True(result);
        var reloaded = await _taskRepo.GetByIdAsync(301);
        Assert.NotNull(reloaded);
        Assert.Null(reloaded.SquashCommitSha);
        Assert.Equal(string.Empty, reloaded.CurrentStageId);
        Assert.Equal(TaskOverallStatus.Open, reloaded.Status);
        Assert.Equal(301, _jobQueue.LastEnqueuedInferenceTaskId);

        var history = await _taskRepo.GetHistoryAsync(301);
        Assert.Equal(2, history.Count);
        Assert.Equal(2, history[1].Version);
        Assert.Equal(AuditActors.User, history[1].ChangedBy);
        Assert.Contains("Pipeline restarted from beginning via DevQueue", history[1].ChangeReason);
    }

    [Fact]
    public async Task RescheduleTaskAsync_RestartPipelineFalse_PreservesSquashCommitShaAndStage()
    {
        var task = new ForgeTask
        {
            Id = 302,
            WorkspaceId = 1,
            Title = "Failed Task Retry Stage",
            Status = TaskOverallStatus.Failed,
            CurrentStageId = "deterministic-post-impl",
            SquashCommitSha = "c0ffee1234567890abcdef1234567890abcdef",
            Priority = TaskPriority.Normal
        };
        await _taskRepo.CreateAsync(task);

        var service = CreateService();

        var result = await service.RescheduleTaskAsync(302, restartPipeline: false);

        Assert.True(result);
        var reloaded = await _taskRepo.GetByIdAsync(302);
        Assert.NotNull(reloaded);
        Assert.Equal("c0ffee1234567890abcdef1234567890abcdef", reloaded.SquashCommitSha);
        Assert.Equal("deterministic-post-impl", reloaded.CurrentStageId);
        Assert.Equal(TaskOverallStatus.Open, reloaded.Status);
        Assert.Equal(302, _jobQueue.LastEnqueuedInferenceTaskId);

        var history = await _taskRepo.GetHistoryAsync(302);
        Assert.Equal(2, history.Count);
        Assert.Equal(2, history[1].Version);
        Assert.Equal(AuditActors.User, history[1].ChangedBy);
        Assert.Contains("Rescheduled task execution via DevQueue", history[1].ChangeReason);
    }

    [Fact]
    public async Task ReorderInferenceQueueByPriorityAsync_WhenQueueOutOfOrder_ReordersPriorityAscending()
    {
        var task401 = await _taskRepo.CreateAsync(new ForgeTask { Id = 401, WorkspaceId = 1, Title = "P4 Task", Priority = TaskPriority.Low });
        var task402 = await _taskRepo.CreateAsync(new ForgeTask { Id = 402, WorkspaceId = 1, Title = "P2 Task", Priority = TaskPriority.High });
        var task403 = await _taskRepo.CreateAsync(new ForgeTask { Id = 403, WorkspaceId = 1, Title = "P3 Task", Priority = TaskPriority.Normal });

        var client = new BackgroundJobClient(_jobStorage);
        var job1 = client.Create<ITaskOrchestrator>(o => o.ProcessTaskAsync(401, default), new EnqueuedState(HangfireQueues.LlmInference));
        var job2 = client.Create<ITaskOrchestrator>(o => o.ProcessTaskAsync(402, default), new EnqueuedState(HangfireQueues.LlmInference));
        var job3 = client.Create<ITaskOrchestrator>(o => o.ProcessTaskAsync(403, default), new EnqueuedState(HangfireQueues.LlmInference));

        var service = CreateService();

        var reorderedCount = await service.ReorderInferenceQueueByPriorityAsync();

        Assert.Equal(3, reorderedCount);
        Assert.Contains(job1, _jobClient.DeletedJobIds);
        Assert.Contains(job2, _jobClient.DeletedJobIds);
        Assert.Contains(job3, _jobClient.DeletedJobIds);

        // Verification: tasks re-enqueued in Priority ASC order: 402 (P2), 403 (P3), 401 (P4)
        Assert.Equal(new[] { 402, 403, 401 }, _jobQueue.EnqueuedInferenceTaskIds);
    }

    [Fact]
    public async Task ReorderInferenceQueueByPriorityAsync_WhenCriticalTaskInInferenceQueue_DispatchesToUrgent()
    {
        var task501 = await _taskRepo.CreateAsync(new ForgeTask { Id = 501, WorkspaceId = 1, Title = "P1 Task", Priority = TaskPriority.Critical });
        var task502 = await _taskRepo.CreateAsync(new ForgeTask { Id = 502, WorkspaceId = 1, Title = "P3 Task", Priority = TaskPriority.Normal });

        var client = new BackgroundJobClient(_jobStorage);
        var job1 = client.Create<ITaskOrchestrator>(o => o.ProcessTaskAsync(501, default), new EnqueuedState(HangfireQueues.LlmInference));
        var job2 = client.Create<ITaskOrchestrator>(o => o.ProcessTaskAsync(502, default), new EnqueuedState(HangfireQueues.LlmInference));

        var service = CreateService();

        var reorderedCount = await service.ReorderInferenceQueueByPriorityAsync();

        Assert.Equal(2, reorderedCount);
        Assert.Single(_jobQueue.EnqueuedUrgentTaskIds);
        Assert.Equal(501, _jobQueue.EnqueuedUrgentTaskIds[0]);

        Assert.Single(_jobQueue.EnqueuedInferenceTaskIds);
        Assert.Equal(502, _jobQueue.EnqueuedInferenceTaskIds[0]);
    }

    [Fact]
    public async Task PromoteToUrgentAsync_UpdatesTaskPriorityToCriticalInDatabase()
    {
        var task = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 601,
            WorkspaceId = 1,
            Title = "Task To Promote",
            Priority = TaskPriority.Normal,
            Status = TaskOverallStatus.Open
        });

        var service = CreateService();

        var result = await service.PromoteToUrgentAsync("job-601", 601);

        Assert.True(result);
        var reloaded = await _taskRepo.GetByIdAsync(601);
        Assert.NotNull(reloaded);
        Assert.Equal(TaskPriority.Critical, reloaded.Priority);
    }

    [Fact]
    public async Task PromoteToUrgentAsync_WhenWorkspaceConfigured_SyncsMarkdownToDisk()
    {
        await _workspaceRepo.CreateAsync(new Workspace
        {
            Id = 1,
            Name = "Test WS",
            RepositoryPath = _tempDir
        });

        var task = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 602,
            WorkspaceId = 1,
            Title = "Task To Promote With Disk",
            Priority = TaskPriority.Normal,
            Status = TaskOverallStatus.Open
        });

        var service = CreateService();

        var result = await service.PromoteToUrgentAsync("job-602", 602);

        Assert.True(result);
        Assert.Single(_markdownSync.SyncedCalls);
        Assert.Equal(602, _markdownSync.SyncedCalls[0].Task.Id);
        Assert.Equal(TaskPriority.Critical, _markdownSync.SyncedCalls[0].Task.Priority);
        Assert.Equal(_tempDir, _markdownSync.SyncedCalls[0].WorkspacePath);
    }

    [Fact]
    public async Task PromoteToUrgentAsync_ByTaskId_WhenQueuedInInference_PromotesJobAndEscalatesPriorityWithHistory()
    {
        var task = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 701,
            WorkspaceId = 1,
            Title = "Inference Queued Task",
            Priority = TaskPriority.Normal,
            Status = TaskOverallStatus.Open
        });

        var client = new BackgroundJobClient(_jobStorage);
        var jobId = client.Create<ITaskOrchestrator>(o => o.ProcessTaskAsync(701, default), new EnqueuedState(HangfireQueues.LlmInference));

        var service = CreateService();

        var result = await service.PromoteToUrgentAsync(701);

        Assert.True(result);
        var reloaded = await _taskRepo.GetByIdAsync(701);
        Assert.NotNull(reloaded);
        Assert.Equal(TaskPriority.Critical, reloaded.Priority);

        var history = await _taskRepo.GetHistoryAsync(701);
        Assert.NotEmpty(history);
        Assert.Contains("Updated Priority from P3 to P1", history.Last().ChangeReason);

        var monitoring = _jobStorage.GetMonitoringApi();
        var urgentJobs = monitoring.EnqueuedJobs(HangfireQueues.LlmUrgent, 0, 10);
        Assert.Contains(urgentJobs, j => j.Key == jobId);
    }

    [Fact]
    public async Task PromoteToUrgentAsync_ByTaskId_WhenAlreadyInUrgent_DoesNotEnqueueDuplicate()
    {
        var task = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 702,
            WorkspaceId = 1,
            Title = "Already Urgent Task",
            Priority = TaskPriority.Critical,
            Status = TaskOverallStatus.Open
        });

        var client = new BackgroundJobClient(_jobStorage);
        client.Create<ITaskOrchestrator>(o => o.ProcessTaskAsync(702, default), new EnqueuedState(HangfireQueues.LlmUrgent));

        var service = CreateService();

        var result = await service.PromoteToUrgentAsync(702);

        Assert.True(result);
        Assert.Empty(_jobQueue.EnqueuedUrgentTaskIds);
    }

    [Fact]
    public async Task PromoteToUrgentAsync_ByTaskId_WhenNotQueued_EscalatesPriorityWithHistoryWithoutEnqueueing()
    {
        var task = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 703,
            WorkspaceId = 1,
            Title = "Suggested Unqueued Task",
            Priority = TaskPriority.Normal,
            Status = TaskOverallStatus.Suggested
        });

        var service = CreateService();

        var result = await service.PromoteToUrgentAsync(703);

        Assert.False(result); // No queued Hangfire job was moved
        Assert.Empty(_jobQueue.EnqueuedUrgentTaskIds); // Did NOT launch execution of unapproved task!

        var reloaded = await _taskRepo.GetByIdAsync(703);
        Assert.NotNull(reloaded);
        Assert.Equal(TaskPriority.Critical, reloaded.Priority);

        var history = await _taskRepo.GetHistoryAsync(703);
        Assert.NotEmpty(history);
        Assert.Contains("Updated Priority from P3 to P1", history.Last().ChangeReason);
    }

    [Fact]
    public async Task ReorderInferenceQueueByPriorityAsync_WhenQueueContainsDeletedJobs_IgnoresDeletedJobs()
    {

        await _taskRepo.CreateAsync(new ForgeTask { Id = 850, WorkspaceId = 1, Title = "Task 850", Priority = TaskPriority.Normal });

        var client = new BackgroundJobClient(_jobStorage);
        var deletedJob = client.Create(() => TestUtilityJobTarget.Run(), new EnqueuedState(HangfireQueues.LlmTaskReview));
        client.Delete(deletedJob);

        var activeJob = client.Create<ITaskOrchestrator>(o => o.ProcessTaskAsync(850, default), new EnqueuedState(HangfireQueues.LlmTaskReview));

        var service = CreateService();

        var reorderedCount = await service.ReorderInferenceQueueByPriorityAsync();

        // Queue has only 1 active task job (850), so it is already sorted
        Assert.Equal(0, reorderedCount);
    }

    [Fact]
    public async Task ReorderInferenceQueueByPriorityAsync_WhenQueueAlreadySorted_ReturnsZeroAndDeletesNothing()
    {
        var task701 = await _taskRepo.CreateAsync(new ForgeTask { Id = 701, WorkspaceId = 1, Title = "P2 Task", Priority = TaskPriority.High });
        var task702 = await _taskRepo.CreateAsync(new ForgeTask { Id = 702, WorkspaceId = 1, Title = "P3 Task", Priority = TaskPriority.Normal });

        var client = new BackgroundJobClient(_jobStorage);
        var job1 = client.Create<ITaskOrchestrator>(o => o.ProcessTaskAsync(701, default), new EnqueuedState(HangfireQueues.LlmTaskReview));
        var job2 = client.Create<ITaskOrchestrator>(o => o.ProcessTaskAsync(702, default), new EnqueuedState(HangfireQueues.LlmTaskReview));

        // MemoryStorage uses ConcurrentDictionary internally with hash-based iteration.
        // Ensure that whichever job is first in the enqueued list is mapped to High priority
        // so the queue is genuinely already sorted.
        var enqueued = _jobStorage.GetMonitoringApi().EnqueuedJobs(HangfireQueues.LlmTaskReview, 0, 10);
        if (enqueued.Count >= 2 && enqueued[0].Key == job2)
        {
            task701.Priority = TaskPriority.Normal;
            task702.Priority = TaskPriority.High;
            await _taskRepo.UpdateAsync(task701);
            await _taskRepo.UpdateAsync(task702);
        }

        var service = CreateService();

        var reorderedCount = await service.ReorderInferenceQueueByPriorityAsync();

        Assert.Equal(0, reorderedCount);
        Assert.Empty(_jobClient.DeletedJobIds);
    }

    [Fact]
    public async Task ReorderInferenceQueueByPriorityAsync_WhenJobsInLegacyQueue_MigratesJobsToTargetTierQueues()
    {
        var task701 = await _taskRepo.CreateAsync(new ForgeTask { Id = 701, WorkspaceId = 1, Title = "P2 Task", Priority = TaskPriority.High });
        var task702 = await _taskRepo.CreateAsync(new ForgeTask { Id = 702, WorkspaceId = 1, Title = "P3 Task", Priority = TaskPriority.Normal });

        var client = new BackgroundJobClient(_jobStorage);
        var job1 = client.Create<ITaskOrchestrator>(o => o.ProcessTaskAsync(701, default), new EnqueuedState(HangfireQueues.LlmInference));
        var job2 = client.Create<ITaskOrchestrator>(o => o.ProcessTaskAsync(702, default), new EnqueuedState(HangfireQueues.LlmInference));

        var service = CreateService();

        var reorderedCount = await service.ReorderInferenceQueueByPriorityAsync();

        Assert.Equal(2, reorderedCount);
        Assert.Contains(job1, _jobClient.DeletedJobIds);
        Assert.Contains(job2, _jobClient.DeletedJobIds);
        Assert.Contains(_jobQueue.EnqueuedStages, s => s.TaskId == 701 && s.Tier == StageQueueTier.TaskReview);
        Assert.Contains(_jobQueue.EnqueuedStages, s => s.TaskId == 702 && s.Tier == StageQueueTier.TaskReview);
    }

    [Fact]
    public async Task ReorderInferenceQueueByPriorityAsync_WhenNonTaskJobPresent_PreservesNonTaskJob()
    {
        var task801 = await _taskRepo.CreateAsync(new ForgeTask { Id = 801, WorkspaceId = 1, Title = "P4 Task", Priority = TaskPriority.Low });
        var task802 = await _taskRepo.CreateAsync(new ForgeTask { Id = 802, WorkspaceId = 1, Title = "P2 Task", Priority = TaskPriority.High });

        var client = new BackgroundJobClient(_jobStorage);
        var jobNonTask = client.Create(() => TestUtilityJobTarget.Run(), new EnqueuedState(HangfireQueues.LlmInference));
        var job1 = client.Create<ITaskOrchestrator>(o => o.ProcessTaskAsync(801, default), new EnqueuedState(HangfireQueues.LlmInference));
        var job2 = client.Create<ITaskOrchestrator>(o => o.ProcessTaskAsync(802, default), new EnqueuedState(HangfireQueues.LlmInference));

        var service = CreateService();



        var reorderedCount = await service.ReorderInferenceQueueByPriorityAsync();

        Assert.Equal(2, reorderedCount);
        // Non-task job should NOT be deleted
        Assert.DoesNotContain(jobNonTask, _jobClient.DeletedJobIds);
        Assert.Contains(job1, _jobClient.DeletedJobIds);
        Assert.Contains(job2, _jobClient.DeletedJobIds);
    }

    [Fact]
    public async Task ReorderInferenceQueueByPriorityAsync_WhenOrphanedJobFound_DeletesOrphanedJobAndContinues()
    {
        var task801 = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 801,
            WorkspaceId = 1,
            Title = "Valid Task",
            Priority = TaskPriority.Normal
        });

        var client = new BackgroundJobClient(_jobStorage);
        var orphanedJob = client.Create<ITaskOrchestrator>(o => o.ProcessTaskAsync(899, default), new EnqueuedState(HangfireQueues.LlmInference));
        var validJob = client.Create<ITaskOrchestrator>(o => o.ProcessTaskAsync(801, default), new EnqueuedState(HangfireQueues.LlmInference));

        var service = CreateService();

        var reorderedCount = await service.ReorderInferenceQueueByPriorityAsync();

        Assert.Contains(orphanedJob, _jobClient.DeletedJobIds);
        Assert.Equal(1, reorderedCount);
        Assert.Contains(801, _jobQueue.EnqueuedInferenceTaskIds);
    }

    [Fact]
    public async Task ReorderInferenceQueueByPriorityAsync_WhenEnqueueThrows_PreservesOldJobAndThrowsAggregateException()
    {
        var task = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 888,
            WorkspaceId = 1,
            Title = "Task with Enqueue Failure",
            Priority = TaskPriority.Normal
        });

        var client = new BackgroundJobClient(_jobStorage);
        var job = client.Create<ITaskOrchestrator>(o => o.ProcessTaskAsync(888, default), new EnqueuedState(HangfireQueues.LlmInference));

        _jobQueue.ShouldThrowOnEnqueue = true;

        var service = CreateService();

        var ex = await Assert.ThrowsAsync<AggregateException>(() => service.ReorderInferenceQueueByPriorityAsync());
        Assert.Single(ex.InnerExceptions);

        // Old job MUST NOT have been deleted
        Assert.DoesNotContain(job, _jobClient.DeletedJobIds);
    }

    [Fact]
    public async Task ReorderInferenceQueueByPriorityAsync_WhenEnqueueFailsForOneTask_PreservesFailedTaskJobAndReordersOthersAndThrowsAggregateException()
    {
        var taskFailing = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 888,
            WorkspaceId = 1,
            Title = "Failing Enqueue Task",
            Priority = TaskPriority.Normal
        });
        var taskPassing = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 889,
            WorkspaceId = 1,
            Title = "Passing Task",
            Priority = TaskPriority.Normal
        });

        var client = new BackgroundJobClient(_jobStorage);
        var job1 = client.Create<ITaskOrchestrator>(o => o.ProcessTaskAsync(888, default), new EnqueuedState(HangfireQueues.LlmInference));
        var job2 = client.Create<ITaskOrchestrator>(o => o.ProcessTaskAsync(889, default), new EnqueuedState(HangfireQueues.LlmInference));

        // Throw only for task 888
        _jobQueue.ShouldThrowForTaskId = id => id == 888;

        var service = CreateService();

        var ex = await Assert.ThrowsAsync<AggregateException>(() => service.ReorderInferenceQueueByPriorityAsync());
        Assert.Single(ex.InnerExceptions);

        // Failing task's old job is preserved
        Assert.DoesNotContain(job1, _jobClient.DeletedJobIds);

        // Passing task was reordered and its old job deleted
        Assert.Contains(job2, _jobClient.DeletedJobIds);
        Assert.Contains(889, _jobQueue.EnqueuedInferenceTaskIds);
    }

    [Fact]
    public async Task ReorderInferenceQueueByPriorityAsync_WhenOldJobAlreadyPickedUpByWorker_DeletesNewJobToPreventDuplicateExecution()
    {
        var task = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 890,
            WorkspaceId = 1,
            Title = "Race Condition Task",
            Priority = TaskPriority.Normal
        });

        var client = new BackgroundJobClient(_jobStorage);
        var oldJob = client.Create<ITaskOrchestrator>(o => o.ProcessTaskAsync(890, default), new EnqueuedState(HangfireQueues.LlmInference));

        // Simulate worker picked up old job: ChangeState for oldJob with expectedState="Enqueued" returns false
        _jobClient.ChangeStateHandler = (jobId, state, expectedState) =>
        {
            if (jobId == oldJob && expectedState == "Enqueued")
            {
                return false;
            }
            return true;
        };

        var service = CreateService();

        var reorderedCount = await service.ReorderInferenceQueueByPriorityAsync();

        // 0 jobs reordered because old job was taken by worker
        Assert.Equal(0, reorderedCount);

        // Newly enqueued job must be deleted immediately to prevent duplicate execution
        var expectedNewJobId = "job-taskreview-890";
        Assert.Contains(expectedNewJobId, _jobClient.DeletedJobIds);
    }

    [Fact]
    public async Task ReorderInferenceQueueByPriorityAsync_WhenUnknownTier_ThrowsArgumentOutOfRangeException()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ITaskRepository>(_taskRepo);
        services.AddSingleton<IWorkspaceRepository>(new InMemoryWorkspaceRepository());

        var mockStage = new Mock<IWorkflowStage>();
        mockStage.Setup(s => s.StageId).Returns("invalid-tier-stage");
        mockStage.Setup(s => s.QueueTier).Returns((StageQueueTier)999);

        var pipeline = new WorkflowPipeline(new[] { mockStage.Object });
        services.AddSingleton<IWorkflowPipelineProvider>(new DefaultWorkflowPipelineProvider(pipeline));

        var sp = services.BuildServiceProvider();

        var task = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 891,
            WorkspaceId = 1,
            Title = "Task with Invalid Tier",
            Priority = TaskPriority.Normal,
            CurrentStageId = "invalid-tier-stage"
        });

        var client = new BackgroundJobClient(_jobStorage);
        client.Create<ITaskOrchestrator>(o => o.ProcessTaskAsync(891, default), new EnqueuedState(HangfireQueues.LlmInference));

        var service = new DevQueueService(
            _lockProvider,
            _jobClient,
            _jobQueue,
            sp.GetRequiredService<IServiceScopeFactory>(),
            _jobStorage,
            _cancellationManager.Object,
            _pathResolver.Object,
            _skippedJobRegistry,
            NullLogger<DevQueueService>.Instance);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.ReorderInferenceQueueByPriorityAsync());
    }

    [Fact]
    public async Task ReorderInferenceQueueByPriorityAsync_WhenWorkflowStageUnresolvable_SurfacesInSkippedJobs()
    {
        var task = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 892,
            WorkspaceId = 1,
            Title = "Task with Missing Stage",
            Priority = TaskPriority.Normal,
            CurrentStageId = "non-existent-stage"
        });

        var client = new BackgroundJobClient(_jobStorage);
        var job = client.Create<ITaskOrchestrator>(o => o.ProcessTaskAsync(892, default), new EnqueuedState(HangfireQueues.LlmInference));

        var service = CreateService();

        var reorderedCount = await service.ReorderInferenceQueueByPriorityAsync();
        Assert.Equal(0, reorderedCount);

        var snapshot = await service.GetSnapshotAsync();
        Assert.NotNull(snapshot.SkippedJobs);
        var skipped = Assert.Single(snapshot.SkippedJobs);
        Assert.Equal(job, skipped.JobId);
        Assert.Equal(892, skipped.TaskId);
        Assert.Contains("non-existent-stage", skipped.Reason);
    }

    [Fact]
    public async Task ReorderInferenceQueueByPriorityAsync_WhenUpdateLastHangfireJobIdFails_PropagatesException()
    {
        var failingRepoMock = new Mock<ITaskRepository>();
        failingRepoMock.Setup(r => r.GetByIdsAsync(It.IsAny<IEnumerable<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ForgeTask>
            {
                new() { Id = 893, WorkspaceId = 1, Title = "Update Failing Task", Priority = TaskPriority.Normal }
            });
        failingRepoMock.Setup(r => r.UpdateLastHangfireJobIdAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Database connection dead"));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ITaskRepository>(failingRepoMock.Object);
        services.AddSingleton<IWorkspaceRepository>(new InMemoryWorkspaceRepository());

        var mockStage = new Mock<IWorkflowStage>();
        mockStage.Setup(s => s.StageId).Returns("task-review");
        mockStage.Setup(s => s.QueueTier).Returns(StageQueueTier.TaskReview);
        var pipeline = new WorkflowPipeline(new[] { mockStage.Object });
        services.AddSingleton<IWorkflowPipelineProvider>(new DefaultWorkflowPipelineProvider(pipeline));

        var sp = services.BuildServiceProvider();

        var client = new BackgroundJobClient(_jobStorage);
        client.Create<ITaskOrchestrator>(o => o.ProcessTaskAsync(893, default), new EnqueuedState(HangfireQueues.LlmInference));

        var service = new DevQueueService(
            _lockProvider,
            _jobClient,
            _jobQueue,
            sp.GetRequiredService<IServiceScopeFactory>(),
            _jobStorage,
            _cancellationManager.Object,
            _pathResolver.Object,
            _skippedJobRegistry,
            NullLogger<DevQueueService>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReorderInferenceQueueByPriorityAsync());
        Assert.Equal("Database connection dead", ex.Message);
    }

    [Fact]
    public async Task ReorderInferenceQueueByPriorityAsync_WhenSkipRecordedOnOneInstance_ReadsFromSecondInstanceViaRegistry()
    {
        var sharedRegistry = new DevQueueSkippedJobRegistry();

        var instance1 = CreateService(sharedRegistry);
        var instance2 = CreateService(sharedRegistry);

        var task = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 894,
            WorkspaceId = 1,
            Title = "Task with Missing Stage across instances",
            Priority = TaskPriority.Normal,
            CurrentStageId = "missing-stage-cross-instance"
        });

        var client = new BackgroundJobClient(_jobStorage);
        var job = client.Create<ITaskOrchestrator>(o => o.ProcessTaskAsync(894, default), new EnqueuedState(HangfireQueues.LlmInference));

        // Instance 1 runs reorder, encountering the unresolvable stage
        var reorderedCount = await instance1.ReorderInferenceQueueByPriorityAsync();
        Assert.Equal(0, reorderedCount);

        // Instance 2 takes a snapshot: it must see the skipped job recorded by instance 1
        var snapshot = await instance2.GetSnapshotAsync();
        Assert.NotNull(snapshot.SkippedJobs);
        var skipped = Assert.Single(snapshot.SkippedJobs);
        Assert.Equal(job, skipped.JobId);
        Assert.Equal(894, skipped.TaskId);
        Assert.Contains("missing-stage-cross-instance", skipped.Reason);
    }

    [Fact]
    public async Task ReorderInferenceQueueByPriorityAsync_WhenOldJobAndNewJobBothTakenByWorker_LogsErrorAndDoesNotThrow()
    {
        var task = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 895,
            WorkspaceId = 1,
            Title = "Double Race Task",
            Priority = TaskPriority.Normal
        });

        var client = new BackgroundJobClient(_jobStorage);
        var oldJob = client.Create<ITaskOrchestrator>(o => o.ProcessTaskAsync(895, default), new EnqueuedState(HangfireQueues.LlmInference));

        // Simulate worker picked up old job AND new job (both ChangeState return false)
        _jobClient.ChangeStateHandler = (jobId, state, expectedState) => false;

        var service = CreateService();

        var reorderedCount = await service.ReorderInferenceQueueByPriorityAsync();
        Assert.Equal(0, reorderedCount);
    }

    [Fact]
    public async Task RequeueJobAsync_WithoutJobId_WhenTaskIsCritical_EnqueuesToUrgentQueue()
    {
        var task = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 901,
            WorkspaceId = 1,
            Title = "Critical Failed Task",
            Priority = TaskPriority.Critical,
            Status = TaskOverallStatus.Failed
        });

        var service = CreateService();

        var result = await service.RequeueJobAsync(string.Empty, taskId: 901);

        Assert.True(result);
        Assert.Single(_jobQueue.EnqueuedUrgentTaskIds);
        Assert.Equal(901, _jobQueue.EnqueuedUrgentTaskIds[0]);
    }



    public void Dispose()
    {
        PurgeJobStorage();
        _serviceProvider.Dispose();
        if (Directory.Exists(_tempDir))
        {
            try
            {
                Directory.Delete(_tempDir, true);
            }
            catch (Exception ex)
            {
                // Justification: Best-effort test temporary directory cleanup
                _ = ex.Message;
            }
        }
    }

    #region Mock Implementations

    private class MockLockProvider : IWorkspaceLockProvider
    {
        public List<ActiveLockSnapshot> ActiveLocks { get; } = new();
        public int? LastEmergencyWorkspaceId { get; private set; }
        public int? LastEmergencyTaskId { get; private set; }

        public IReadOnlyList<ActiveLockSnapshot> GetActiveLocks() => ActiveLocks.ToList();

        public bool EmergencyRelease(int workspaceId, int? taskId = null)
        {
            LastEmergencyWorkspaceId = workspaceId;
            LastEmergencyTaskId = taskId;
            ActiveLocks.RemoveAll(l => l.WorkspaceId == workspaceId && l.TaskId == taskId);
            return true;
        }

        public void EmergencyReleaseAll() => ActiveLocks.Clear();

        public Task<IDisposable> AcquireLockAsync(int workspaceId, CancellationToken cancellationToken = default)
            => Task.FromResult<IDisposable>(new DummyReleaser());

        public bool TryAcquireLock(int workspaceId, out IDisposable? lockReleaser)
        {
            lockReleaser = new DummyReleaser();
            return true;
        }

        public Task<(bool Acquired, IDisposable? LockReleaser)> TryAcquireLockAsync(int workspaceId, TimeSpan timeout, CancellationToken cancellationToken = default)
            => Task.FromResult<(bool, IDisposable?)>((true, new DummyReleaser()));

        public Task<IDisposable> AcquireTaskLockAsync(int workspaceId, int taskId, CancellationToken cancellationToken = default)
            => Task.FromResult<IDisposable>(new DummyReleaser());

        public bool TryAcquireTaskLock(int workspaceId, int taskId, out IDisposable? lockReleaser)
        {
            lockReleaser = new DummyReleaser();
            return true;
        }

        public Task<(bool Acquired, IDisposable? LockReleaser)> TryAcquireTaskLockAsync(int workspaceId, int taskId, TimeSpan timeout, CancellationToken cancellationToken = default)
            => Task.FromResult<(bool, IDisposable?)>((true, new DummyReleaser()));

        private class DummyReleaser : IDisposable { public void Dispose() { } }
    }

    private class MockBackgroundJobClient : IBackgroundJobClient
    {
        private readonly JobStorage? _storage;

        public MockBackgroundJobClient(JobStorage? storage = null)
        {
            _storage = storage;
        }

        public string? LastChangedJobId { get; private set; }
        public string? LastChangedStateName { get; private set; }
        public string? LastDeletedJobId { get; private set; }
        public string? LastRequeuedJobId { get; private set; }
        public List<string> DeletedJobIds { get; } = new();
        public Func<string, IState, string?, bool>? ChangeStateHandler { get; set; }

        public bool ChangeState(string jobId, IState state, string? expectedState = null)
        {
            LastChangedJobId = jobId;
            LastChangedStateName = state.Name;
            if (state is EnqueuedState enqueued)
            {
                LastChangedStateName = enqueued.Queue;
                LastRequeuedJobId = jobId;
            }
            if (state is DeletedState)
            {
                LastDeletedJobId = jobId;
                DeletedJobIds.Add(jobId);
            }

            if (ChangeStateHandler != null)
            {
                return ChangeStateHandler(jobId, state, expectedState);
            }

            if (_storage != null)
            {
                try
                {
                    return new BackgroundJobClient(_storage).ChangeState(jobId, state, expectedState);
                }
                catch (Exception ex)
                {
                    // Justification: Mock job IDs may not exist in backing storage
                    _ = ex.Message;
                    return false;
                }
            }
            return true;
        }

        public string Create(Job job, IState state) => "mock-job-id";
    }

    private class MockTaskJobQueue : ITaskJobQueue
    {
        public int? LastEnqueuedUrgentTaskId { get; private set; }
        public int? LastEnqueuedInferenceTaskId { get; private set; }
        public List<int> EnqueuedUrgentTaskIds { get; } = new();
        public List<int> EnqueuedInferenceTaskIds { get; } = new();
        public List<(int TaskId, StageQueueTier Tier, int Priority)> EnqueuedStages { get; } = new();
        public bool ShouldThrowOnEnqueue { get; set; }
        public Func<int, bool>? ShouldThrowForTaskId { get; set; }
        public int EnqueueStageCallCount { get; private set; }

        public string EnqueueUrgent(int taskId, DateTime? originalEnqueuedAt = null)
        {
            if (ShouldThrowOnEnqueue || (ShouldThrowForTaskId != null && ShouldThrowForTaskId(taskId)))
            {
                throw new InvalidOperationException($"Simulated enqueue urgent failure for {taskId}");
            }
            LastEnqueuedUrgentTaskId = taskId;
            EnqueuedUrgentTaskIds.Add(taskId);
            return $"urgent-job-{taskId}";
        }

        public string EnqueueStage(int taskId, StageQueueTier tier, int priority, DateTime? originalEnqueuedAt = null)
        {
            EnqueueStageCallCount++;
            if (ShouldThrowOnEnqueue || (ShouldThrowForTaskId != null && ShouldThrowForTaskId(taskId)))
            {
                throw new InvalidOperationException($"Simulated enqueue failure for {taskId}");
            }
            EnqueuedStages.Add((taskId, tier, priority));
            if (priority <= TaskPriority.Critical)
            {
                LastEnqueuedUrgentTaskId = taskId;
                EnqueuedUrgentTaskIds.Add(taskId);
                return $"urgent-job-{taskId}";
            }

            LastEnqueuedInferenceTaskId = taskId;
            EnqueuedInferenceTaskIds.Add(taskId);
            return $"job-{tier.ToString().ToLowerInvariant()}-{taskId}";
        }

        public bool Delete(string jobId) => true;
    }

    private class TestDevQueueMarkdownSyncService : ITaskMarkdownSyncService
    {
        public List<(ForgeTask Task, string WorkspacePath, bool Archive)> ArchivedCalls { get; } = new();
        public List<(ForgeTask Task, string WorkspacePath)> SyncedCalls { get; } = new();

        public Task ArchiveOrCleanupTaskFileAsync(ForgeTask task, string workspacePath, bool archive = true, CancellationToken cancellationToken = default)
        {
            ArchivedCalls.Add((task, workspacePath, archive));
            return Task.CompletedTask;
        }

        public Task<string> SyncTaskToDiskAsync(ForgeTask task, string workspacePath, IReadOnlyList<int>? dependencies = null, string? agentProfileId = null, CancellationToken cancellationToken = default)
        {
            SyncedCalls.Add((task, workspacePath));
            return Task.FromResult(string.Empty);
        }
        public string GenerateMarkdown(ForgeTask task, IReadOnlyList<int>? dependencies = null, string? agentProfileId = null) => string.Empty;
        public (TaskFrontmatter Frontmatter, string Body) ParseMarkdown(string markdownContent) => (new TaskFrontmatter(), string.Empty);
        public string ComputeContentHash(string content) => string.Empty;
        public void SuppressContentHash(string content) { }
        public Task ProcessFileContentAsync(string filePath, string content, int workspaceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ProcessFileChangedAsync(string filePath, int workspaceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void StartWatching(int workspaceId, string workspacePath) { }
        public void StopWatching(int workspaceId) { }
        public void Dispose() { }
    }

    #endregion
}

public static class TestUtilityJobTarget
{
    public static void Run() { }
}

