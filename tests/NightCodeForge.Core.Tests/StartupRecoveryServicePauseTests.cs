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

    [Fact]
    public async Task ReconcileWorkspacesAsync_WhenTaskHasInterruptedConversationFromPriorPause_PreservesInterruptedConversationAndRehydratesMemoryWithoutDuplicateCrashNotice()
    {
        var workspace = await _workspaceRepo.CreateAsync(new Workspace
        {
            Id = 2,
            Name = "Task 35 Reproduction Workspace",
            RepositoryPath = _tempDir
        });

        var stage = TestWorkflowPipelines.CreateImplementationStage();
        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stage });

        var task = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 35,
            WorkspaceId = workspace.Id,
            Status = TaskOverallStatus.Open,
            CurrentStageId = stage.StageId,
            InterruptedStageId = stage.StageId,
            IsDeferredByPause = true,
            Priority = TaskPriority.Normal
        });

        // Simulate Conv #264: paused before host restart with status Interrupted (8) and existing turns
        var conv = await _taskRepo.StartConversationAsync(task.Id, stage.StageId, WorkflowStageKind.Agentic, 1);
        await _taskRepo.AddConversationTurnAsync(conv.Id, StageTurnRole.User, content: "Implement Step 11.36");
        await _taskRepo.AddConversationTurnAsync(conv.Id, StageTurnRole.Tool, toolName: "read", toolArguments: "{\"path\":\"Spec.md\"}", metadataJson: "{\"CallId\":\"call_1\"}");
        await _taskRepo.AddConversationTurnAsync(conv.Id, StageTurnRole.ToolResult, toolName: "read", toolResult: "# Spec", metadataJson: "{\"CallId\":\"call_1\"}");
        await _taskRepo.CompleteConversationAsync(conv.Id, StageExecutionStatus.Interrupted, summary: "Operations paused (GPU VRAM reclaimed)");

        var opsMock = new Mock<IOperationsControlService>();
        opsMock.Setup(m => m.IsPaused).Returns(true);

        var recovery = CreateRecoveryService(opsMock.Object, new DefaultWorkflowPipelineProvider(pipeline));
        await recovery.ReconcileWorkspacesAsync();

        var updatedTask = await _taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(updatedTask);
        Assert.Equal(stage.StageId, updatedTask.InterruptedStageId);
        Assert.True(updatedTask.IsDeferredByPause);

        //RecoveredSerializedMemory must be populated from the Interrupted conversation's turns
        var bag = PipelineDataBag.FromJson(updatedTask.PipelineDataJson);
        var memoryJson = bag.Get<string>(PipelineDataBagKeys.RecoveredSerializedMemory);
        Assert.False(string.IsNullOrWhiteSpace(memoryJson));
        Assert.Contains("Implement Step 11.36", memoryJson);
        // Must NOT inject a crash recovery notice into a cleanly paused conversation
        Assert.DoesNotContain("Host restarted unexpectedly", memoryJson);

        var convWithTurns = await _taskRepo.GetConversationWithTurnsAsync(conv.Id);
        Assert.NotNull(convWithTurns);
        Assert.Equal(StageExecutionStatus.Interrupted, convWithTurns.Status);
        Assert.Equal(3, convWithTurns.Turns.Count);
    }

    [Fact]
    public async Task ReconcileWorkspacesAsync_WhenInterruptedConversationHasZeroTurns_PreservesInterruptedStageIdAndDropsRecoveredMemory()
    {
        var workspace = await _workspaceRepo.CreateAsync(new Workspace
        {
            Id = 3,
            Name = "Zero Turn Interrupted Workspace",
            RepositoryPath = _tempDir
        });

        var stage = TestWorkflowPipelines.CreateImplementationStage();
        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stage });

        var staleBag = new PipelineDataBag();
        staleBag.Set(PipelineDataBagKeys.RecoveredSerializedMemory, "[{\"Role\":\"user\",\"Text\":\"stale\"}]");

        var task = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 36,
            WorkspaceId = workspace.Id,
            Status = TaskOverallStatus.Open,
            CurrentStageId = stage.StageId,
            PipelineDataJson = staleBag.ToJson()
        });

        var conv = await _taskRepo.StartConversationAsync(task.Id, stage.StageId, WorkflowStageKind.Agentic, 1);
        await _taskRepo.CompleteConversationAsync(conv.Id, StageExecutionStatus.Interrupted, summary: "Paused with 0 turns");

        var opsMock = new Mock<IOperationsControlService>();
        opsMock.Setup(m => m.IsPaused).Returns(false);

        var recovery = CreateRecoveryService(opsMock.Object, new DefaultWorkflowPipelineProvider(pipeline));
        await recovery.ReconcileWorkspacesAsync();

        var updatedTask = await _taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(updatedTask);
        Assert.Equal(stage.StageId, updatedTask.InterruptedStageId);

        var bag = PipelineDataBag.FromJson(updatedTask.PipelineDataJson);
        Assert.False(bag.ContainsKey(PipelineDataBagKeys.RecoveredSerializedMemory));

        var reloadedConv = await _taskRepo.GetConversationWithTurnsAsync(conv.Id);
        Assert.NotNull(reloadedConv);
        Assert.Equal(StageExecutionStatus.Interrupted, reloadedConv.Status);
    }

    [Fact]
    public async Task ReconcileWorkspacesAsync_WhenRunningConversationAlreadyEndsWithRecoveryNotice_DoesNotAppendDuplicateNotice()
    {
        var workspace = await _workspaceRepo.CreateAsync(new Workspace
        {
            Id = 4,
            Name = "Duplicate Notice Guard Workspace",
            RepositoryPath = _tempDir
        });

        var stage = TestWorkflowPipelines.CreateImplementationStage();
        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stage });

        var task = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 37,
            WorkspaceId = workspace.Id,
            Status = TaskOverallStatus.Open,
            CurrentStageId = stage.StageId
        });

        var conv = await _taskRepo.StartConversationAsync(task.Id, stage.StageId, WorkflowStageKind.Agentic, 1);
        await _taskRepo.AddConversationTurnAsync(conv.Id, StageTurnRole.User, content: "Turn 0");
        await _taskRepo.AddConversationTurnAsync(
            conv.Id,
            StageTurnRole.System,
            content: "[System Notice: Host restarted unexpectedly; workspace restored; resuming from previous conversation turn]");

        var opsMock = new Mock<IOperationsControlService>();
        opsMock.Setup(m => m.IsPaused).Returns(false);

        var recovery = CreateRecoveryService(opsMock.Object, new DefaultWorkflowPipelineProvider(pipeline));
        await recovery.ReconcileWorkspacesAsync();

        var reloadedConv = await _taskRepo.GetConversationWithTurnsAsync(conv.Id);
        Assert.NotNull(reloadedConv);
        Assert.Equal(StageExecutionStatus.Interrupted, reloadedConv.Status);
        Assert.Equal(2, reloadedConv.Turns.Count);
    }

    [Fact]
    public async Task ReconcileWorkspacesAsync_WhenWaitingForHumanHasLastHumanAnswerInDataBag_PreservesWaitingForHumanWithoutCrashNotice()
    {
        var workspace = await _workspaceRepo.CreateAsync(new Workspace
        {
            Id = 5,
            Name = "Waiting With Answer Workspace",
            RepositoryPath = _tempDir
        });

        var stage = TestWorkflowPipelines.CreateImplementationStage();
        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stage });

        var dataBag = new PipelineDataBag();
        dataBag.Set(PipelineDataBagKeys.LastHumanAnswer, "Use Redis");

        var task = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 38,
            WorkspaceId = workspace.Id,
            Status = TaskOverallStatus.Open,
            CurrentStageId = stage.StageId,
            InterruptedStageId = stage.StageId,
            IsWaitingForHuman = false,
            PipelineDataJson = dataBag.ToJson()
        });

        var conv = await _taskRepo.StartConversationAsync(task.Id, stage.StageId, WorkflowStageKind.Agentic, 1);
        await _taskRepo.AddConversationTurnAsync(conv.Id, StageTurnRole.User, content: "Implement caching");
        await _taskRepo.AddConversationTurnAsync(
            conv.Id,
            StageTurnRole.Tool,
            toolName: CanonicalToolNames.Ask,
            toolArguments: "{\"questions\":[{\"prompt\":\"Cache backend?\"}]}",
            metadataJson: "{\"CallId\":\"ask_redis\"}");
        await _taskRepo.CompleteConversationAsync(conv.Id, StageExecutionStatus.WaitingForHuman, summary: "Waiting for human");

        var opsMock = new Mock<IOperationsControlService>();
        opsMock.Setup(m => m.IsPaused).Returns(false);

        var recovery = CreateRecoveryService(opsMock.Object, new DefaultWorkflowPipelineProvider(pipeline));
        await recovery.ReconcileWorkspacesAsync();

        var reloadedConv = await _taskRepo.GetConversationWithTurnsAsync(conv.Id);
        Assert.NotNull(reloadedConv);
        // Must remain WaitingForHuman and NOT have a crash recovery System turn injected
        Assert.Equal(StageExecutionStatus.WaitingForHuman, reloadedConv.Status);
        Assert.DoesNotContain(reloadedConv.Turns, t => t.Role == StageTurnRole.System);

        var updatedTask = await _taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(updatedTask);
        var updatedBag = PipelineDataBag.FromJson(updatedTask.PipelineDataJson);
        var recoveredMemory = updatedBag.Get<string>(PipelineDataBagKeys.RecoveredSerializedMemory);
        Assert.NotNull(recoveredMemory);
        Assert.Contains("Use Redis", recoveredMemory);
    }
}
