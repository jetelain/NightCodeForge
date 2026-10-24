using NightCodeForge.Core.Concurrency;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Tests.Fixtures;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Core.Tests;

public class CascadeUnblockingTests
{
    private readonly InMemoryTaskRepository _taskRepo;
    private readonly InMemoryWorkspaceRepository _workspaceRepo;
    private readonly InMemoryTaskDependencyStore _depStore;
    private readonly DependencyResolutionEngine _depEngine;
    private readonly WorkspaceLockProvider _lockProvider;
    private readonly FakeGitWorkspaceManager _gitManager;
    private readonly FakeTaskJobQueue _jobQueue;
    private readonly Workspace _workspace;

    public CascadeUnblockingTests()
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
            Name = "NCF Test Workspace",
            RepositoryPath = "C:/test/repo",
            DefaultBaseBranch = "main"
        };
        _workspaceRepo.CreateAsync(_workspace).GetAwaiter().GetResult();
    }

    [Fact]
    public async Task CompleteTaskAndCascadeAsync_MergesBranchCompletesTaskAndUnblocksDependents()
    {
        // DAG structure:
        // ForgeTask 201 (Prerequisite)
        // ForgeTask 202 depends on 201
        // ForgeTask 203 depends on 201 AND 204 (where 204 is still Pending)
        var task201 = await _taskRepo.CreateAsync(new ForgeTask { Id = 201, WorkspaceId = 1, Status = TaskOverallStatus.Ready, BaseBranch = "main" });
        var task202 = await _taskRepo.CreateAsync(new ForgeTask { Id = 202, WorkspaceId = 1, Status = TaskOverallStatus.Open });
        var task203 = await _taskRepo.CreateAsync(new ForgeTask { Id = 203, WorkspaceId = 1, Status = TaskOverallStatus.Open });
        var task204 = await _taskRepo.CreateAsync(new ForgeTask { Id = 204, WorkspaceId = 1, Status = TaskOverallStatus.Open });

        _depStore.AddTask(201, 1, TaskOverallStatus.Ready);
        _depStore.AddTask(202, 1, TaskOverallStatus.Open);
        _depStore.AddTask(203, 1, TaskOverallStatus.Open);
        _depStore.AddTask(204, 1, TaskOverallStatus.Open);

        _depStore.AddDependency(1, 202, 201);
        _depStore.AddDependency(1, 203, 201);
        _depStore.AddDependency(1, 203, 204);

        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = _taskRepo,
            WorkspaceRepository = _workspaceRepo,
            DependencyEngine = _depEngine,
            LockProvider = _lockProvider,
            GitWorkspaceManager = _gitManager,
            JobQueue = _jobQueue,
            DependencyStore = _depStore
        }.Build();

        await orchestrator.CompleteTaskAndCascadeAsync(201, "feat(auth): Add JWT bearer token validation (#201)");

        // 1. Squash merge executed
        Assert.Single(_gitManager.SquashMerges);
        var merge = _gitManager.SquashMerges[0];
        Assert.Equal(201, merge.TaskId);
        Assert.Equal("main", merge.BaseBranch);
        Assert.Equal("feat(auth): Add JWT bearer token validation (#201)", merge.CommitMessage);

        // 2. ForgeTask 201 marked Closed
        var updated201 = await _taskRepo.GetByIdAsync(201);
        Assert.NotNull(updated201);
        Assert.Equal(TaskOverallStatus.Closed, updated201.Status);

        // 3. ForgeTask 202 unblocked and enqueued to llm-inference
        Assert.Contains(202, _jobQueue.InferenceQueue);

        // 4. ForgeTask 203 still waiting on 204
        Assert.DoesNotContain(203, _jobQueue.InferenceQueue);
    }

    [Fact]
    public async Task CompleteTaskAndCascadeAsync_OrdersCascadeUnblocking_ByPriorityAscendingThenCreatedAtAscending()
    {
        // DAG structure: Task 100 unblocks 101 (P4), 102 (P1), 103 (P2, newer), 104 (P2, older)
        var baseTime = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        var task100 = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 100,
            WorkspaceId = 1,
            Status = TaskOverallStatus.Ready,
            BaseBranch = "main"
        });

        var task101 = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 101,
            WorkspaceId = 1,
            Status = TaskOverallStatus.Open,
            Priority = TaskPriority.Low, // P4
            CreatedAt = baseTime.AddMinutes(1)
        });

        var task102 = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 102,
            WorkspaceId = 1,
            Status = TaskOverallStatus.Open,
            Priority = TaskPriority.Critical, // P1
            CreatedAt = baseTime.AddMinutes(2)
        });

        var task103 = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 103,
            WorkspaceId = 1,
            Status = TaskOverallStatus.Open,
            Priority = TaskPriority.High, // P2
            CreatedAt = baseTime.AddMinutes(10) // newer P2
        });

        var task104 = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 104,
            WorkspaceId = 1,
            Status = TaskOverallStatus.Open,
            Priority = TaskPriority.High, // P2
            CreatedAt = baseTime.AddMinutes(5) // older P2
        });

        _depStore.AddTask(100, 1, TaskOverallStatus.Ready);
        _depStore.AddTask(101, 1, TaskOverallStatus.Open);
        _depStore.AddTask(102, 1, TaskOverallStatus.Open);
        _depStore.AddTask(103, 1, TaskOverallStatus.Open);
        _depStore.AddTask(104, 1, TaskOverallStatus.Open);

        _depStore.AddDependency(1, 101, 100);
        _depStore.AddDependency(1, 102, 100);
        _depStore.AddDependency(1, 103, 100);
        _depStore.AddDependency(1, 104, 100);

        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = _taskRepo,
            WorkspaceRepository = _workspaceRepo,
            DependencyEngine = _depEngine,
            LockProvider = _lockProvider,
            GitWorkspaceManager = _gitManager,
            JobQueue = _jobQueue,
            DependencyStore = _depStore
        }.Build();

        await orchestrator.CompleteTaskAndCascadeAsync(100, "Merge task 100");

        // P1 task 102 must be enqueued to llm-urgent
        Assert.Single(_jobQueue.UrgentQueue);
        Assert.Equal(102, _jobQueue.UrgentQueue[0]);

        // P2 and P4 tasks must be enqueued to llm-inference ordered by Priority ASC, then CreatedAt ASC
        // Expected order: 104 (P2, older), 103 (P2, newer), 101 (P4)
        Assert.Equal(3, _jobQueue.InferenceQueue.Count);
        Assert.Equal(104, _jobQueue.InferenceQueue[0]);
        Assert.Equal(103, _jobQueue.InferenceQueue[1]);
        Assert.Equal(101, _jobQueue.InferenceQueue[2]);
    }

    [Fact]
    public async Task CompleteTaskAndCascadeAsync_WhenUnblockedTaskNotFoundInRepository_ThrowsTaskNotFoundExceptionAndDoesNotEnqueueMissingTask()
    {
        var task300 = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 300,
            WorkspaceId = 1,
            Status = TaskOverallStatus.Ready,
            BaseBranch = "main"
        });

        var task301 = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 301,
            WorkspaceId = 1,
            Status = TaskOverallStatus.Open,
            Priority = TaskPriority.Normal
        });

        // 302 is in dependency store but deliberately omitted from _taskRepo
        _depStore.AddTask(300, 1, TaskOverallStatus.Ready);
        _depStore.AddTask(301, 1, TaskOverallStatus.Open);
        _depStore.AddTask(302, 1, TaskOverallStatus.Open);

        _depStore.AddDependency(1, 301, 300);
        _depStore.AddDependency(1, 302, 300);

        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = _taskRepo,
            WorkspaceRepository = _workspaceRepo,
            DependencyEngine = _depEngine,
            LockProvider = _lockProvider,
            GitWorkspaceManager = _gitManager,
            JobQueue = _jobQueue,
            DependencyStore = _depStore
        }.Build();

        // Must throw TaskNotFoundException when an unblocked task is missing
        var ex = await Assert.ThrowsAsync<TaskNotFoundException>(() => orchestrator.CompleteTaskAndCascadeAsync(300, "Merge task 300"));
        Assert.Equal(302, ex.TaskId);

        // Neither missing task 302 nor valid task 301 is enqueued (fail-fast before any jobs are queued)
        Assert.Empty(_jobQueue.InferenceQueue);
        Assert.Empty(_jobQueue.UrgentQueue);
        Assert.Empty(_jobQueue.EnqueuedStages);
    }

    [Fact]
    public async Task CompleteTaskAndCascadeAsync_WhenAnotherTaskLockIsHeld_ExecutesSquashMergeImmediatelyWithoutBlocking()
    {
        // ForgeTask 401 is Ready for Review and squash-merge
        var task401 = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 401,
            WorkspaceId = 1,
            Status = TaskOverallStatus.Ready,
            BaseBranch = "main"
        });
        _depStore.AddTask(401, 1, TaskOverallStatus.Ready);

        // ForgeTask 999 is currently executing in Hangfire dev-queue in its isolated worktree
        var task999 = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 999,
            WorkspaceId = 1,
            Status = TaskOverallStatus.Open,
            BaseBranch = "main"
        });
        _depStore.AddTask(999, 1, TaskOverallStatus.Open);

        // Simulate background task 999 actively holding its task execution lock
        using var backgroundTaskLock = await _lockProvider.AcquireTaskLockAsync(1, 999);

        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = _taskRepo,
            WorkspaceRepository = _workspaceRepo,
            DependencyEngine = _depEngine,
            LockProvider = _lockProvider,
            GitWorkspaceManager = _gitManager,
            JobQueue = _jobQueue,
            DependencyStore = _depStore
        }.Build();

        // Maintainer clicks "Approve & Merge" in Review Room for Task 401:
        // Must complete immediately (< 500ms) without being blocked by task 999
        var mergeTask = orchestrator.CompleteTaskAndCascadeAsync(401, "feat: squash task 401");
        await mergeTask.WaitAsync(TimeSpan.FromSeconds(1));

        // 1. Squash merge executed for Task 401
        Assert.Single(_gitManager.SquashMerges);
        var merge = _gitManager.SquashMerges[0];
        Assert.Equal(401, merge.TaskId);
        Assert.Equal("main", merge.BaseBranch);

        // 2. Status updated to Closed
        var closed401 = await _taskRepo.GetByIdAsync(401);
        Assert.NotNull(closed401);
        Assert.Equal(TaskOverallStatus.Closed, closed401.Status);
    }
}

