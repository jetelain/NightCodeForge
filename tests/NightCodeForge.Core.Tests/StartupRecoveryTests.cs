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
            pipelineProvider ?? new DefaultWorkflowPipelineProvider(),
            _jobQueue,
            new Moq.Mock<IWorkspacePathResolver>().Object,
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
    public async Task ReconcileWorkspacesAsync_ReconcilesOrphanedRunningAgenticStage_RehydratesMemoryAndEnqueuesUrgent()
    {
        var task = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 301,
            WorkspaceId = 1,
            Status = TaskOverallStatus.Open,
            CurrentStageId = "task-review",
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

        var task = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 302,
            WorkspaceId = 1,
            Status = TaskOverallStatus.Implemented,
            CurrentStageId = DeterministicCoverageGate.DefaultStageId,
            PipelineDataJson = bag.ToJson()
        });

        var stage = new DeterministicCoverageGate(new Moq.Mock<IExecutionEnvironmentProvider>().Object);
        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stage });

        var recovery = CreateRecoveryService(new DefaultWorkflowPipelineProvider(pipeline));

        await recovery.ReconcileWorkspacesAsync();

        var updated = await _taskRepo.GetByIdAsync(302);
        Assert.NotNull(updated);
        Assert.Equal(TaskOverallStatus.Implemented, updated.Status);

        var updatedBag = PipelineDataBag.FromJson(updated.PipelineDataJson);
        Assert.False(updatedBag.ContainsKey("DeterministicMetrics"));

        Assert.Contains(302, _jobQueue.UrgentQueue);
    }

    [Fact]
    public async Task ReconcileWorkspacesAsync_PerformsWslCheck_DoesNotThrow()
    {
        var recovery = CreateRecoveryService();

        var exception = await Record.ExceptionAsync(() => recovery.ReconcileWorkspacesAsync());
        Assert.Null(exception);
    }
}
