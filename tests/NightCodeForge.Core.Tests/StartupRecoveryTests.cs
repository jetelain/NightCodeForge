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
    private readonly InMemoryTaskRepository _taskRepo;
    private readonly InMemoryWorkspaceRepository _workspaceRepo;
    private readonly FakeGitCliRunner _gitCliRunner;
    private readonly FakeTaskJobQueue _jobQueue;
    private readonly FakeGitWorkspaceManager _gitWorkspaceManager = new();

    public StartupRecoveryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ncf_recovery_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _taskRepo = new InMemoryTaskRepository();
        _workspaceRepo = new InMemoryWorkspaceRepository();
        _gitCliRunner = new FakeGitCliRunner();
        _jobQueue = new FakeTaskJobQueue();
    }

    private StartupRecoveryService CreateRecoveryService(IWorkflowPipelineProvider? pipelineProvider = null)
    {
        return new StartupRecoveryService(
            _workspaceRepo,
            _taskRepo,
            _gitCliRunner,
            _gitWorkspaceManager,
            pipelineProvider ?? new DefaultWorkflowPipelineProvider(),
            _jobQueue,
            new Moq.Mock<IWorkspacePathResolver>().Object,
            new Moq.Mock<IProcessRunner>().Object,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<StartupRecoveryService>.Instance);
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

        var recovery = CreateRecoveryService();

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

        var recovery = CreateRecoveryService();

        await recovery.ReconcileWorkspacesAsync();

        Assert.Contains(_gitCliRunner.ExecutedCommands, c => c.Arguments.Contains("add -A"));
        Assert.Contains(_gitCliRunner.ExecutedCommands, c => c.Arguments.Contains("commit -m") && c.Arguments.Contains("WIP-RECOVERY"));
    }

    [Fact]
    public async Task ReconcileWorkspacesAsync_AgenticStageWithoutRunningConversation_DropsRecoveredMemoryAndEnqueuesUrgent()
    {
        var staleBag = new PipelineDataBag();
        staleBag.Set(PipelineDataBagKeys.RecoveredSerializedMemory, "stale");
        var task = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 301,
            WorkspaceId = 1,
            Status = TaskOverallStatus.Open,
            CurrentStageId = "task-review",
            PipelineDataJson = staleBag.ToJson(),
            Checkpoints = new List<StageCheckpoint>
            {
                new()
                {
                    TaskId = 301,
                    StageId = "task-review",
                    SerializedMemory = "Prior conversation history...",
                    CreatedAt = DateTime.UtcNow.AddMinutes(-5)
                }
            }
        });

        var stage = TestWorkflowPipelines.CreateReviewStage();
        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stage });

        var recovery = CreateRecoveryService(new DefaultWorkflowPipelineProvider(pipeline));

        await recovery.ReconcileWorkspacesAsync();

        var updated = await _taskRepo.GetByIdAsync(301);
        Assert.NotNull(updated);
        Assert.Equal(TaskOverallStatus.Open, updated.Status);

        var bag = PipelineDataBag.FromJson(updated.PipelineDataJson);
        Assert.False(bag.ContainsKey(PipelineDataBagKeys.RecoveredSerializedMemory));

        Assert.Contains(301, _jobQueue.UrgentQueue);
    }

    [Fact]
    public async Task ReconcileWorkspacesAsync_ReconcilesOrphanedRunningDeterministicStage_ResetsMetricsAndEnqueuesUrgent()
    {
        var bag = new PipelineDataBag();
        bag.Set(PipelineDataBagKeys.TestsCurrentTotal, 50);
        bag.Set(PipelineDataBagKeys.TestsCurrentPassed, 49);
        bag.Set(PipelineDataBagKeys.TestsCurrentFailed, 1);
        bag.Set(PipelineDataBagKeys.CoverageCurrentLineRate, 0.75);
        bag.Set(PipelineDataBagKeys.WarningsCurrentTotal, 3);
        bag.Set(PipelineDataBagKeys.WarningsNew, new List<string> { "warning CS0168" });
        bag.Set(PipelineDataBagKeys.FailedTests, new List<string> { "TestA failed" });
        bag.Set(PipelineDataBagKeys.TestFailureReason, "1 failed test");

        var task = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 302,
            WorkspaceId = 1,
            Status = TaskOverallStatus.Implemented,
            CurrentStageId = DeterministicTestWithCoverageGate.DefaultStageId,
            PipelineDataJson = bag.ToJson()
        });

        var stage = new DeterministicTestWithCoverageGate(new Moq.Mock<IExecutionEnvironmentProvider>().Object);
        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stage });

        var recovery = CreateRecoveryService(new DefaultWorkflowPipelineProvider(pipeline));

        await recovery.ReconcileWorkspacesAsync();

        var updated = await _taskRepo.GetByIdAsync(302);
        Assert.NotNull(updated);
        Assert.Equal(TaskOverallStatus.Implemented, updated.Status);

        var updatedBag = PipelineDataBag.FromJson(updated.PipelineDataJson);
        Assert.False(updatedBag.ContainsKey(PipelineDataBagKeys.TestsCurrentTotal));
        Assert.False(updatedBag.ContainsKey(PipelineDataBagKeys.TestsCurrentPassed));
        Assert.False(updatedBag.ContainsKey(PipelineDataBagKeys.TestsCurrentFailed));
        Assert.False(updatedBag.ContainsKey(PipelineDataBagKeys.CoverageCurrentLineRate));
        Assert.False(updatedBag.ContainsKey(PipelineDataBagKeys.WarningsCurrentTotal));
        Assert.False(updatedBag.ContainsKey(PipelineDataBagKeys.WarningsNew));
        Assert.False(updatedBag.ContainsKey(PipelineDataBagKeys.FailedTests));
        Assert.False(updatedBag.ContainsKey(PipelineDataBagKeys.TestFailureReason));

        Assert.Contains(302, _jobQueue.UrgentQueue);
    }

    [Fact]
    public async Task ReconcileWorkspacesAsync_PerformsWslCheck_DoesNotThrow()
    {
        var recovery = CreateRecoveryService();

        var exception = await Record.ExceptionAsync(() => recovery.ReconcileWorkspacesAsync());
        Assert.Null(exception);
    }

    [Fact]
    public async Task ReconcileWorkspacesAsync_NormalizesRelativePathsOncePerEligibleTask()
    {
        await _workspaceRepo.CreateAsync(new Workspace { Id = 7, Name = "Norm", RepositoryPath = _tempDir });
        await _taskRepo.CreateAsync(new ForgeTask { Id = 701, WorkspaceId = 7, Status = TaskOverallStatus.Open });
        await _taskRepo.CreateAsync(new ForgeTask { Id = 702, WorkspaceId = 7, Status = TaskOverallStatus.Ready });
        await _taskRepo.CreateAsync(new ForgeTask { Id = 703, WorkspaceId = 7, Status = TaskOverallStatus.Closed });

        await CreateRecoveryService().ReconcileWorkspacesAsync();

        Assert.Equal(1, _gitWorkspaceManager.NormalizedTaskIds.Count(i => i == 701));
        Assert.Equal(1, _gitWorkspaceManager.NormalizedTaskIds.Count(i => i == 702));
        Assert.DoesNotContain(703, _gitWorkspaceManager.NormalizedTaskIds);
    }

    [Fact]
    public async Task ReconcileWorkspacesAsync_NormalizationFailure_DoesNotAbortRecovery()
    {
        await _workspaceRepo.CreateAsync(new Workspace { Id = 8, Name = "Fail", RepositoryPath = _tempDir });
        await _taskRepo.CreateAsync(new ForgeTask { Id = 801, WorkspaceId = 8, Status = TaskOverallStatus.Open });
        _gitWorkspaceManager.NormalizeFailure = new IOException("boom");

        var exception = await Record.ExceptionAsync(() => CreateRecoveryService().ReconcileWorkspacesAsync());

        Assert.Null(exception);
    }
}
