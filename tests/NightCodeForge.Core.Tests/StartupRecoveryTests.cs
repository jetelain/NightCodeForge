using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
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

    private StartupRecoveryService CreateRecoveryService(IWorkflowPipelineProvider? pipelineProvider = null, IOperationsControlService? operationsControlService = null)
    {
        var ops = operationsControlService;
        if (ops == null)
        {
            var mock = new Moq.Mock<IOperationsControlService>();
            mock.Setup(m => m.IsPaused).Returns(false);
            ops = mock.Object;
        }

        return new StartupRecoveryService(
            _workspaceRepo,
            _taskRepo,
            _gitCliRunner,
            _gitWorkspaceManager,
            pipelineProvider ?? new DefaultWorkflowPipelineProvider(),
            _jobQueue,
            new Moq.Mock<IWorkspacePathResolver>().Object,
            new Moq.Mock<IProcessRunner>().Object,
            new NightCodeForge.Core.Execution.SingleExecutionEnvironmentProvider(new Moq.Mock<IExecutionEnvironment>().Object),
            ops,
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

        Assert.Contains(301, _jobQueue.TaskReviewQueue);
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
            Status = TaskOverallStatus.Open,
            CurrentStageId = DeterministicTestWithCoverageGate.DefaultStageId,
            PipelineDataJson = bag.ToJson()
        });

        var stage = new DeterministicTestWithCoverageGate(new NightCodeForge.Core.Execution.SingleExecutionEnvironmentProvider(new Moq.Mock<IExecutionEnvironment>().Object));
        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stage });

        var recovery = CreateRecoveryService(new DefaultWorkflowPipelineProvider(pipeline));

        await recovery.ReconcileWorkspacesAsync();

        var updated = await _taskRepo.GetByIdAsync(302);
        Assert.NotNull(updated);
        Assert.Equal(TaskOverallStatus.Open, updated.Status);

        var updatedBag = PipelineDataBag.FromJson(updated.PipelineDataJson);
        Assert.False(updatedBag.ContainsKey(PipelineDataBagKeys.TestsCurrentTotal));
        Assert.False(updatedBag.ContainsKey(PipelineDataBagKeys.TestsCurrentPassed));
        Assert.False(updatedBag.ContainsKey(PipelineDataBagKeys.TestsCurrentFailed));
        Assert.False(updatedBag.ContainsKey(PipelineDataBagKeys.CoverageCurrentLineRate));
        Assert.False(updatedBag.ContainsKey(PipelineDataBagKeys.WarningsCurrentTotal));
        Assert.False(updatedBag.ContainsKey(PipelineDataBagKeys.WarningsNew));
        Assert.False(updatedBag.ContainsKey(PipelineDataBagKeys.FailedTests));
        Assert.False(updatedBag.ContainsKey(PipelineDataBagKeys.TestFailureReason));

        Assert.Contains(302, _jobQueue.ImplementationQueue);
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

    [Fact]
    public async Task ReconcileWorkspacesAsync_MigratesLegacyImplementedTask_ToPostImplementationGatesAndEnqueuesImplementationTier()
    {
        await _workspaceRepo.CreateAsync(new Workspace { Id = 10, Name = "Legacy Workspace Impl", RepositoryPath = _tempDir });
        var task = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 1001,
            WorkspaceId = 10,
            Status = TaskOverallStatus.Implemented,
            CurrentStageId = StandardStageIds.Implementation,
            Priority = TaskPriority.Normal
        });

        var postImplMock = new Moq.Mock<IWorkflowStage>();
        postImplMock.SetupGet(s => s.StageId).Returns(StandardStageIds.PostImplementationGates);
        postImplMock.SetupGet(s => s.DisplayName).Returns("Post-Implementation Gates");
        postImplMock.SetupGet(s => s.Kind).Returns(WorkflowStageKind.Deterministic);
        postImplMock.SetupProperty(s => s.QueueTier, StageQueueTier.Implementation);

        var pipeline = new WorkflowPipeline(new[] { postImplMock.Object });
        var recovery = CreateRecoveryService(new DefaultWorkflowPipelineProvider(pipeline));

        await recovery.ReconcileWorkspacesAsync();

        var updated = await _taskRepo.GetByIdAsync(1001);
        Assert.NotNull(updated);
        Assert.Equal(TaskOverallStatus.Open, updated.Status);
        Assert.Equal(StandardStageIds.PostImplementationGates, updated.CurrentStageId);
        Assert.True(updated.HasExecutionRun);
        Assert.NotNull(updated.LastHangfireJobId);

        Assert.Contains(1001, _jobQueue.ImplementationQueue);
        Assert.DoesNotContain(1001, _jobQueue.UrgentQueue);

        var history = await _taskRepo.GetHistoryAsync(1001);
        Assert.Contains(history, h => h.ChangedBy == AuditActors.System && h.ChangeReason != null && h.ChangeReason.Contains("Legacy status migration"));
    }

    [Fact]
    public async Task ReconcileWorkspacesAsync_MigratesLegacyReviewedTask_ToPostReviewVerificationAndEnqueuesCodeReviewTier()
    {
        await _workspaceRepo.CreateAsync(new Workspace { Id = 11, Name = "Legacy Workspace Rev", RepositoryPath = _tempDir });
        var task = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 1002,
            WorkspaceId = 11,
            Status = TaskOverallStatus.Reviewed,
            CurrentStageId = StandardStageIds.TaskReview,
            Priority = TaskPriority.Normal
        });

        var postReviewMock = new Moq.Mock<IWorkflowStage>();
        postReviewMock.SetupGet(s => s.StageId).Returns(StandardStageIds.PostReviewVerification);
        postReviewMock.SetupGet(s => s.DisplayName).Returns("Post-Review Verification");
        postReviewMock.SetupGet(s => s.Kind).Returns(WorkflowStageKind.Deterministic);
        postReviewMock.SetupProperty(s => s.QueueTier, StageQueueTier.CodeReview);

        var pipeline = new WorkflowPipeline(new[] { postReviewMock.Object });
        var recovery = CreateRecoveryService(new DefaultWorkflowPipelineProvider(pipeline));

        await recovery.ReconcileWorkspacesAsync();

        var updated = await _taskRepo.GetByIdAsync(1002);
        Assert.NotNull(updated);
        Assert.Equal(TaskOverallStatus.Open, updated.Status);
        Assert.Equal(StandardStageIds.PostReviewVerification, updated.CurrentStageId);
        Assert.True(updated.HasExecutionRun);
        Assert.NotNull(updated.LastHangfireJobId);

        Assert.Contains(1002, _jobQueue.CodeReviewQueue);
        Assert.DoesNotContain(1002, _jobQueue.UrgentQueue);

        var history = await _taskRepo.GetHistoryAsync(1002);
        Assert.Contains(history, h => h.ChangedBy == AuditActors.System && h.ChangeReason != null && h.ChangeReason.Contains("Legacy status migration"));
    }

    [Fact]
    public async Task ReconcileWorkspacesAsync_WhenLegacyTaskTargetStageNotDefined_ThrowsWorkflowValidationException()
    {
        await _workspaceRepo.CreateAsync(new Workspace { Id = 12, Name = "Legacy Missing Stage", RepositoryPath = _tempDir });
        await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 1003,
            WorkspaceId = 12,
            Status = TaskOverallStatus.Implemented,
            CurrentStageId = StandardStageIds.Implementation,
            Priority = TaskPriority.Normal
        });

        var emptyPipeline = new WorkflowPipeline(Array.Empty<IWorkflowStage>());
        var recovery = CreateRecoveryService(new DefaultWorkflowPipelineProvider(emptyPipeline));

        await Assert.ThrowsAsync<WorkflowValidationException>(() => recovery.ReconcileWorkspacesAsync());
    }

    [Fact]
    public async Task ReconcileWorkspacesAsync_OrdersCandidateTasks_FurtherAlongStageFirst()
    {
        await _workspaceRepo.CreateAsync(new Workspace { Id = 15, Name = "Ordering Workspace", RepositoryPath = _tempDir });

        var stage1Mock = new Moq.Mock<IWorkflowStage>();
        stage1Mock.SetupGet(s => s.StageId).Returns(StandardStageIds.TaskReview);
        stage1Mock.SetupProperty(s => s.QueueTier, StageQueueTier.TaskReview);

        var stage2Mock = new Moq.Mock<IWorkflowStage>();
        stage2Mock.SetupGet(s => s.StageId).Returns(StandardStageIds.PreImplementationBaseline);
        stage2Mock.SetupProperty(s => s.QueueTier, StageQueueTier.Implementation);

        var stage3Mock = new Moq.Mock<IWorkflowStage>();
        stage3Mock.SetupGet(s => s.StageId).Returns(StandardStageIds.Implementation);
        stage3Mock.SetupProperty(s => s.QueueTier, StageQueueTier.Implementation);

        var stage4Mock = new Moq.Mock<IWorkflowStage>();
        stage4Mock.SetupGet(s => s.StageId).Returns(StandardStageIds.PostImplementationGates);
        stage4Mock.SetupProperty(s => s.QueueTier, StageQueueTier.Implementation);

        var stage5Mock = new Moq.Mock<IWorkflowStage>();
        stage5Mock.SetupGet(s => s.StageId).Returns(StandardStageIds.CodeReviewAndFix);
        stage5Mock.SetupProperty(s => s.QueueTier, StageQueueTier.CodeReview);

        var pipeline = new WorkflowPipeline(new[] { stage1Mock.Object, stage2Mock.Object, stage3Mock.Object, stage4Mock.Object, stage5Mock.Object });

        // Task A is at Stage 1 (task-review)
        await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 1,
            WorkspaceId = 15,
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.TaskReview,
            Priority = TaskPriority.Normal,
            UpdatedAt = DateTime.UtcNow.AddMinutes(-20)
        });

        // Task B is at Stage 3 (implementation), created earlier
        await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 8,
            WorkspaceId = 15,
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.Implementation,
            Priority = TaskPriority.Normal,
            UpdatedAt = DateTime.UtcNow.AddMinutes(-10)
        });

        // Task C is at Stage 4 (deterministic-post-impl), created later
        await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 10,
            WorkspaceId = 15,
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.PostImplementationGates,
            Priority = TaskPriority.Normal,
            UpdatedAt = DateTime.UtcNow.AddMinutes(-5)
        });

        // Task D is at Stage 5 (code-review)
        await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 12,
            WorkspaceId = 15,
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.CodeReviewAndFix,
            Priority = TaskPriority.Normal,
            UpdatedAt = DateTime.UtcNow.AddMinutes(-2)
        });

        var recovery = CreateRecoveryService(new DefaultWorkflowPipelineProvider(pipeline));
        await recovery.ReconcileWorkspacesAsync();

        // Candidates must be enqueued furthest along first:
        // 1. Task 12 (CodeReview tier)
        // 2. Task 10 (Implementation tier, stage 4)
        // 3. Task 8 (Implementation tier, stage 3)
        // 4. Task 1 (TaskReview tier, stage 1)
        var enqueuedIds = _jobQueue.EnqueuedStages.Select(e => e.TaskId).ToList();
        Assert.Equal(new[] { 12, 10, 8, 1 }, enqueuedIds);
    }

    [Fact]
    public async Task ReconcileWorkspacesAsync_DeletesPreviousHangfireJobBeforeReEnqueuing()
    {
        await _workspaceRepo.CreateAsync(new Workspace { Id = 16, Name = "Stale Job Workspace", RepositoryPath = _tempDir });

        var implStageMock = new Moq.Mock<IWorkflowStage>();
        implStageMock.SetupGet(s => s.StageId).Returns(StandardStageIds.Implementation);
        implStageMock.SetupProperty(s => s.QueueTier, StageQueueTier.Implementation);

        var pipeline = new WorkflowPipeline(new[] { implStageMock.Object });

        await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 30,
            WorkspaceId = 16,
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.Implementation,
            Priority = TaskPriority.Normal,
            LastHangfireJobId = "stale-processing-job-299"
        });

        var recovery = CreateRecoveryService(new DefaultWorkflowPipelineProvider(pipeline));
        await recovery.ReconcileWorkspacesAsync();

        Assert.Contains("stale-processing-job-299", _jobQueue.DeletedJobs);
        var updated = await _taskRepo.GetByIdAsync(30);
        Assert.NotNull(updated);
        Assert.NotNull(updated.LastHangfireJobId);
        Assert.NotEqual("stale-processing-job-299", updated.LastHangfireJobId);
    }
}
