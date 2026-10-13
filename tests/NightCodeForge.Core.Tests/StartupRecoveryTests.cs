using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Core.Workflow.Stages;

namespace NightCodeForge.Core.Tests;

public class StartupRecoveryTests : IDisposable
{
    private readonly string _tempDir;
    private readonly InMemoryIssueRepository _issueRepo;
    private readonly InMemoryWorkspaceRepository _workspaceRepo;
    private readonly FakeGitCliRunner _gitCliRunner;
    private readonly FakeIssueJobQueue _jobQueue;

    public StartupRecoveryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ncf_recovery_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _issueRepo = new InMemoryIssueRepository();
        _workspaceRepo = new InMemoryWorkspaceRepository();
        _gitCliRunner = new FakeGitCliRunner();
        _jobQueue = new FakeIssueJobQueue();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
            // Ignore cleanup errors in tests
        }
    }

    [Fact]
    public async Task ReconcileWorkspacesAsync_PurgesStaleGitLockFiles()
    {
        var gitDir = Path.Combine(_tempDir, ".git");
        var refsHeadsDir = Path.Combine(gitDir, "refs", "heads");
        Directory.CreateDirectory(refsHeadsDir);

        var indexLock = Path.Combine(gitDir, "index.lock");
        var branchLock = Path.Combine(refsHeadsDir, "feature.lock");

        File.WriteAllText(indexLock, "lock");
        File.WriteAllText(branchLock, "lock");

        Assert.True(File.Exists(indexLock));
        Assert.True(File.Exists(branchLock));

        var workspace = await _workspaceRepo.CreateAsync(new Workspace
        {
            Id = 1,
            Name = "Recovery Workspace",
            RepositoryPath = _tempDir
        });

        var recovery = new StartupRecoveryService(
            _workspaceRepo, _issueRepo, _gitCliRunner,
            new DefaultWorkflowPipelineProvider(), _jobQueue);

        await recovery.ReconcileWorkspacesAsync();

        Assert.False(File.Exists(indexLock));
        Assert.False(File.Exists(branchLock));
    }

    [Fact]
    public async Task ReconcileWorkspacesAsync_WhenDirtyWorkingTree_CreatesRescueCommit()
    {
        var gitDir = Path.Combine(_tempDir, ".git");
        Directory.CreateDirectory(gitDir);

        var workspace = await _workspaceRepo.CreateAsync(new Workspace
        {
            Id = 2,
            Name = "Dirty Workspace",
            RepositoryPath = _tempDir
        });

        _gitCliRunner.Handler = (dir, args) =>
        {
            if (args.Contains("status --porcelain"))
            {
                return new GitCommandResult(0, " M src/File.cs\n?? untracked.cs\n", string.Empty, TimeSpan.Zero);
            }
            return new GitCommandResult(0, string.Empty, string.Empty, TimeSpan.Zero);
        };

        var recovery = new StartupRecoveryService(
            _workspaceRepo, _issueRepo, _gitCliRunner,
            new DefaultWorkflowPipelineProvider(), _jobQueue);

        await recovery.ReconcileWorkspacesAsync();

        Assert.Contains(_gitCliRunner.ExecutedCommands, c => c.Arguments.Contains("add -A"));
        Assert.Contains(_gitCliRunner.ExecutedCommands, c => c.Arguments.Contains("commit -m") && c.Arguments.Contains("WIP-RECOVERY"));
    }

    [Fact]
    public async Task ReconcileWorkspacesAsync_ReconcilesOrphanedRunningAgenticStage_RehydratesMemoryAndEnqueuesUrgent()
    {
        var issue = await _issueRepo.CreateAsync(new Issue
        {
            Id = 301,
            WorkspaceId = 1,
            Status = IssueOverallStatus.Running,
            CurrentStageId = IssueReviewStage.DefaultStageId,
            Checkpoints = new List<StageCheckpoint>
            {
                new()
                {
                    IssueId = 301,
                    StageId = IssueReviewStage.DefaultStageId,
                    SerializedMemory = "Prior conversation history...",
                    CreatedAt = DateTime.UtcNow.AddMinutes(-5)
                }
            }
        });

        var stage = new IssueReviewStage();
        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stage });

        var recovery = new StartupRecoveryService(
            _workspaceRepo, _issueRepo, _gitCliRunner,
            new DefaultWorkflowPipelineProvider(pipeline), _jobQueue);

        await recovery.ReconcileWorkspacesAsync();

        var updated = await _issueRepo.GetByIdAsync(301);
        Assert.NotNull(updated);
        Assert.Equal(IssueOverallStatus.Pending, updated.Status);

        var bag = PipelineDataBag.FromJson(updated.PipelineDataJson);
        var memory = bag.Get<string>("SerializedMemory");
        Assert.NotNull(memory);
        Assert.Contains("Prior conversation history...", memory);
        Assert.Contains("[System Notice: Host restarted unexpectedly; workspace restored]", memory);

        Assert.Contains(301, _jobQueue.UrgentQueue);
    }

    [Fact]
    public async Task ReconcileWorkspacesAsync_ReconcilesOrphanedRunningDeterministicStage_ResetsMetricsAndEnqueuesUrgent()
    {
        var bag = new PipelineDataBag();
        bag.Set("DeterministicMetrics", "Partial metrics to discard");

        var issue = await _issueRepo.CreateAsync(new Issue
        {
            Id = 302,
            WorkspaceId = 1,
            Status = IssueOverallStatus.Running,
            CurrentStageId = DeterministicCoverageGate.DefaultStageId,
            PipelineDataJson = bag.ToJson()
        });

        var stage = new DeterministicCoverageGate();
        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stage });

        var recovery = new StartupRecoveryService(
            _workspaceRepo, _issueRepo, _gitCliRunner,
            new DefaultWorkflowPipelineProvider(pipeline), _jobQueue);

        await recovery.ReconcileWorkspacesAsync();

        var updated = await _issueRepo.GetByIdAsync(302);
        Assert.NotNull(updated);
        Assert.Equal(IssueOverallStatus.Pending, updated.Status);

        var updatedBag = PipelineDataBag.FromJson(updated.PipelineDataJson);
        Assert.False(updatedBag.ContainsKey("DeterministicMetrics"));

        Assert.Contains(302, _jobQueue.UrgentQueue);
    }
}
