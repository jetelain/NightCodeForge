using Moq;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Core.Workflow.Stages;
using Xunit;

namespace NightCodeForge.Core.Tests;

public class StartupRecoveryServicePauseTests : IDisposable
{
    private readonly string _tempDir;
    private readonly InMemoryTaskRepository _taskRepo;
    private readonly InMemoryWorkspaceRepository _workspaceRepo;
    private readonly FakeGitCliRunner _gitCliRunner;
    private readonly FakeTaskJobQueue _jobQueue;
    private readonly FakeGitWorkspaceManager _gitWorkspaceManager = new();

    public StartupRecoveryServicePauseTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ncf_recovery_pause_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _taskRepo = new InMemoryTaskRepository();
        _workspaceRepo = new InMemoryWorkspaceRepository();
        _gitCliRunner = new FakeGitCliRunner();
        _jobQueue = new FakeTaskJobQueue();
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
            // Ignore cleanup errors
        }
    }

    private StartupRecoveryService CreateRecoveryService(
        IOperationsControlService operationsControlService,
        IWorkflowPipelineProvider? pipelineProvider = null)
    {
        return new StartupRecoveryService(
            _workspaceRepo,
            _taskRepo,
            _gitCliRunner,
            _gitWorkspaceManager,
            pipelineProvider ?? new DefaultWorkflowPipelineProvider(),
            _jobQueue,
            new Mock<IWorkspacePathResolver>().Object,
            new Mock<IProcessRunner>().Object,
            new NightCodeForge.Core.Execution.SingleExecutionEnvironmentProvider(new Mock<IExecutionEnvironment>().Object),
            operationsControlService,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<StartupRecoveryService>.Instance);
    }

    [Fact]
    public async Task ReconcileWorkspacesAsync_WhenGloballyPaused_ReconcilesLocks_WithoutEnqueuingTasks()
    {
        // 1. Prepare Git directory with locks
        var gitDir = Path.Combine(_tempDir, ".git");
        var refsHeadsDir = Path.Combine(gitDir, "refs", "heads");
        Directory.CreateDirectory(refsHeadsDir);

        var indexLock = Path.Combine(gitDir, "index.lock");
        var branchLock = Path.Combine(refsHeadsDir, "feature.lock");
        File.WriteAllText(indexLock, "lock");
        File.WriteAllText(branchLock, "lock");

        var workspace = await _workspaceRepo.CreateAsync(new Workspace
        {
            Id = 1,
            Name = "Recovery Paused Workspace",
            RepositoryPath = _tempDir
        });

        // 2. Prepare an active task
        var stage = TestWorkflowPipelines.CreateReviewStage();
        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stage });

        var task = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 101,
            WorkspaceId = workspace.Id,
            Status = TaskOverallStatus.Open,
            CurrentStageId = stage.StageId,
            Priority = TaskPriority.High
        });

        // 3. Configure OperationsControlService as paused
        var opsMock = new Mock<IOperationsControlService>();
        opsMock.Setup(m => m.IsPaused).Returns(true);

        var recovery = CreateRecoveryService(opsMock.Object, new DefaultWorkflowPipelineProvider(pipeline));

        // 4. Run recovery reconciliation
        await recovery.ReconcileWorkspacesAsync();

        // 5. Assert: Stale Git locks are purged
        Assert.False(File.Exists(indexLock));
        Assert.False(File.Exists(branchLock));

        // 6. Assert: No tasks are enqueued to Hangfire job queue
        Assert.Empty(_jobQueue.EnqueuedStages);

        // 7. Assert: Task is checkpointed as Interrupted and kept in Open status
        var updatedTask = await _taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(updatedTask);
        Assert.Equal(TaskOverallStatus.Open, updatedTask.Status);
        Assert.Equal(stage.StageId, updatedTask.InterruptedStageId);
        Assert.True(updatedTask.IsDeferredByPause);

        // 8. Assert: Execution log recorded the pause hold
        var logs = _taskRepo.GetAllLogs();
        Assert.Contains(logs, l => l.Message.Contains("Operations are paused"));
    }
}
