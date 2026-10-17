using NightCodeForge.Core.Concurrency;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
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
}
