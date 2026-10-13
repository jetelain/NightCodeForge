using NightCodeForge.Core.Concurrency;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Core.Tests;

public class CascadeUnblockingTests
{
    private readonly InMemoryIssueRepository _issueRepo;
    private readonly InMemoryWorkspaceRepository _workspaceRepo;
    private readonly InMemoryIssueDependencyStore _depStore;
    private readonly DependencyResolutionEngine _depEngine;
    private readonly WorkspaceLockProvider _lockProvider;
    private readonly FakeGitWorkspaceManager _gitManager;
    private readonly FakeIssueJobQueue _jobQueue;
    private readonly Workspace _workspace;

    public CascadeUnblockingTests()
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
            Name = "NCF Test Workspace",
            RepositoryPath = "C:/test/repo",
            DefaultBaseBranch = "main"
        };
        _workspaceRepo.CreateAsync(_workspace).GetAwaiter().GetResult();
    }

    [Fact]
    public async Task CompleteIssueAndCascadeAsync_MergesBranchCompletesIssueAndUnblocksDependents()
    {
        // DAG structure:
        // Issue 201 (Prerequisite)
        // Issue 202 depends on 201
        // Issue 203 depends on 201 AND 204 (where 204 is still Pending)
        var issue201 = await _issueRepo.CreateAsync(new Issue { Id = 201, WorkspaceId = 1, Status = IssueOverallStatus.Ready, BaseBranch = "main" });
        var issue202 = await _issueRepo.CreateAsync(new Issue { Id = 202, WorkspaceId = 1, Status = IssueOverallStatus.Open });
        var issue203 = await _issueRepo.CreateAsync(new Issue { Id = 203, WorkspaceId = 1, Status = IssueOverallStatus.Open });
        var issue204 = await _issueRepo.CreateAsync(new Issue { Id = 204, WorkspaceId = 1, Status = IssueOverallStatus.Open });

        _depStore.AddIssue(201, 1, IssueOverallStatus.Ready);
        _depStore.AddIssue(202, 1, IssueOverallStatus.Open);
        _depStore.AddIssue(203, 1, IssueOverallStatus.Open);
        _depStore.AddIssue(204, 1, IssueOverallStatus.Open);

        _depStore.AddDependency(1, 202, 201);
        _depStore.AddDependency(1, 203, 201);
        _depStore.AddDependency(1, 203, 204);

        var orchestrator = new IssueOrchestrator(
            _issueRepo, _workspaceRepo, _depEngine, _lockProvider,
            _gitManager, new DefaultWorkflowPipelineProvider(), _jobQueue, _depStore);

        await orchestrator.CompleteIssueAndCascadeAsync(201, "feat(auth): Add JWT bearer token validation (#201)");

        // 1. Squash merge executed
        Assert.Single(_gitManager.SquashMerges);
        var merge = _gitManager.SquashMerges[0];
        Assert.Equal(201, merge.IssueId);
        Assert.Equal("main", merge.BaseBranch);
        Assert.Equal("feat(auth): Add JWT bearer token validation (#201)", merge.CommitMessage);

        // 2. Issue 201 marked Closed
        var updated201 = await _issueRepo.GetByIdAsync(201);
        Assert.NotNull(updated201);
        Assert.Equal(IssueOverallStatus.Closed, updated201.Status);

        // 3. Issue 202 unblocked and enqueued to llm-inference
        Assert.Contains(202, _jobQueue.InferenceQueue);

        // 4. Issue 203 still waiting on 204
        Assert.DoesNotContain(203, _jobQueue.InferenceQueue);
    }
}
