using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Core.Concurrency;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Tests.Fixtures;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Core.Workflow.Stages;
using Xunit;

namespace NightCodeForge.Core.Tests;

public class TaskOrchestratorPauseTests
{
    private readonly InMemoryTaskRepository _taskRepository = new();
    private readonly InMemoryWorkspaceRepository _workspaceRepository = new();
    private readonly InMemoryTaskDependencyStore _dependencyStore = new();
    private readonly Mock<IOperationsControlService> _operationsControlMock = new();
    private readonly TaskCancellationManager _cancellationManager = new(NullLogger<TaskCancellationManager>.Instance);

    public TaskOrchestratorPauseTests()
    {
        _operationsControlMock.Setup(o => o.IsPaused).Returns(false);
        _operationsControlMock.Setup(o => o.ReclaimStatus).Returns(OperationsReclaimStatus.Idle);
    }

    private class CancellingPipelineProvider : IWorkflowPipelineProvider
    {
        private readonly Func<PipelineContext, CancellationToken, Task<StageExecutionResult>> _stageFunc;
        private readonly string _stageId;

        public CancellingPipelineProvider(
            Func<PipelineContext, CancellationToken, Task<StageExecutionResult>> stageFunc,
            string stageId = StandardStageIds.Implementation)
        {
            _stageFunc = stageFunc;
            _stageId = stageId;
        }

        public Task<IWorkflowPipeline> GetPipelineAsync(ForgeTask task, CancellationToken cancellationToken = default)
        {
            var stage = new GenericAgenticStage(_stageId, "Test Stage", new(), new MockPromptRenderer(), TimeProvider.System)
            {
                Handler = _stageFunc
            };
            return Task.FromResult<IWorkflowPipeline>(new WorkflowPipeline(new IWorkflowStage[] { stage }));
        }
    }

    private class MockPromptRenderer : IPromptTemplateRenderer
    {
        public string Render(string templateText, object model) => "rendered";
    }

    [Fact]
    public async Task ProcessTaskAsync_WhenGloballyPausedAtEntry_DefersExecutionImmediately()
    {
        _operationsControlMock.Setup(o => o.IsPaused).Returns(true);

        var ws = new Workspace { Id = 1, Name = "Test Ws", RepositoryPath = "C:/test" };
        await _workspaceRepository.CreateAsync(ws);

        var task = new ForgeTask
        {
            Id = 1,
            WorkspaceId = 1,
            Title = "Deferred task",
            Status = TaskOverallStatus.Open,
            CurrentStageId = "impl"
        };
        await _taskRepository.CreateAsync(task);
        _dependencyStore.AddTask(task.Id, 1, TaskOverallStatus.Open);

        var builder = new TestOrchestratorBuilder
        {
            TaskRepository = _taskRepository,
            WorkspaceRepository = _workspaceRepository,
            DependencyStore = _dependencyStore,
            OperationsControlService = _operationsControlMock.Object
        };

        var orchestrator = builder.Build();

        await orchestrator.ProcessTaskAsync(task.Id);

        var reloaded = await _taskRepository.GetByIdAsync(task.Id);
        Assert.NotNull(reloaded);
        Assert.False(reloaded.HasExecutionRun);
        Assert.True(reloaded.IsDeferredByPause);
    }

    [Theory]
    [InlineData(TaskOverallStatus.Cancelled)]
    [InlineData(TaskOverallStatus.Closed)]
    [InlineData(TaskOverallStatus.Rejected)]
    public async Task ProcessTaskAsync_WhenGloballyPausedAtEntry_WithTerminalStatus_DoesNotFlagIsDeferredByPause(TaskOverallStatus terminalStatus)
    {
        _operationsControlMock.Setup(o => o.IsPaused).Returns(true);

        var ws = new Workspace { Id = 1, Name = "Test Ws", RepositoryPath = "C:/test" };
        await _workspaceRepository.CreateAsync(ws);

        var task = new ForgeTask
        {
            Id = 99,
            WorkspaceId = 1,
            Title = "Terminal status task",
            Status = terminalStatus,
            CurrentStageId = "impl",
            IsDeferredByPause = false
        };
        await _taskRepository.CreateAsync(task);
        _dependencyStore.AddTask(task.Id, 1, terminalStatus);

        var builder = new TestOrchestratorBuilder
        {
            TaskRepository = _taskRepository,
            WorkspaceRepository = _workspaceRepository,
            DependencyStore = _dependencyStore,
            OperationsControlService = _operationsControlMock.Object
        };

        var orchestrator = builder.Build();

        await orchestrator.ProcessTaskAsync(task.Id);

        var reloaded = await _taskRepository.GetByIdAsync(task.Id);
        Assert.NotNull(reloaded);
        Assert.False(reloaded.HasExecutionRun);
        Assert.False(reloaded.IsDeferredByPause);
        Assert.Equal(terminalStatus, reloaded.Status);
    }

    [Fact]
    public async Task ProcessTaskAsync_WhenPauseRequested_CheckpointsTaskAsOpen()
    {
        var ws = new Workspace
        {
            Id = 1,
            Name = "Test Ws",
            RepositoryPath = "C:/test",
            DefaultBaseBranch = "main"
        };
        await _workspaceRepository.CreateAsync(ws);

        var task = new ForgeTask
        {
            Id = 10,
            WorkspaceId = 1,
            Title = "Task to Pause",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.Implementation
        };
        await _taskRepository.CreateAsync(task);
        await _taskRepository.StartConversationAsync(task.Id, StandardStageIds.Implementation, WorkflowStageKind.Agentic, 1);
        _dependencyStore.AddTask(task.Id, 1, TaskOverallStatus.Open);

        _cancellationManager.RequestPause(10);

        using var cts = new CancellationTokenSource();
        var pipelineProvider = new CancellingPipelineProvider(async (ctx, ct) =>
        {
            await Task.Yield();
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return StageExecutionResult.Success();
        });

        var builder = new TestOrchestratorBuilder
        {
            TaskRepository = _taskRepository,
            WorkspaceRepository = _workspaceRepository,
            DependencyStore = _dependencyStore,
            CancellationManager = _cancellationManager,
            OperationsControlService = _operationsControlMock.Object,
            PipelineProvider = pipelineProvider
        };

        var orchestrator = builder.Build();

        await orchestrator.ProcessTaskAsync(task.Id, cts.Token);

        var reloaded = await _taskRepository.GetByIdAsync(task.Id);
        Assert.NotNull(reloaded);
        Assert.Equal(TaskOverallStatus.Open, reloaded.Status);
        Assert.Equal(StandardStageIds.Implementation, reloaded.InterruptedStageId);
        Assert.False(reloaded.IsWaitingForHuman);
        Assert.True(reloaded.IsDeferredByPause);

        var convs = await _taskRepository.GetConversationsAsync(task.Id);
        var conv = Assert.Single(convs);
        Assert.Equal(StageExecutionStatus.Interrupted, conv.Status);
    }

    [Fact]
    public async Task ProcessTaskAsync_WhenExplicitCancelRequested_CancelsTask()
    {
        var ws = new Workspace
        {
            Id = 1,
            Name = "Test Ws",
            RepositoryPath = "C:/test",
            DefaultBaseBranch = "main"
        };
        await _workspaceRepository.CreateAsync(ws);

        var task = new ForgeTask
        {
            Id = 20,
            WorkspaceId = 1,
            Title = "Task to Cancel",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.Implementation
        };
        await _taskRepository.CreateAsync(task);
        await _taskRepository.StartConversationAsync(task.Id, StandardStageIds.Implementation, WorkflowStageKind.Agentic, 1);
        _dependencyStore.AddTask(task.Id, 1, TaskOverallStatus.Open);

        using var cts = new CancellationTokenSource();
        var pipelineProvider = new CancellingPipelineProvider(async (ctx, ct) =>
        {
            await Task.Yield();
            _cancellationManager.Cancel(20);
            ct.ThrowIfCancellationRequested();
            return StageExecutionResult.Success();
        });

        var builder = new TestOrchestratorBuilder
        {
            TaskRepository = _taskRepository,
            WorkspaceRepository = _workspaceRepository,
            DependencyStore = _dependencyStore,
            CancellationManager = _cancellationManager,
            OperationsControlService = _operationsControlMock.Object,
            PipelineProvider = pipelineProvider
        };

        var orchestrator = builder.Build();

        await orchestrator.ProcessTaskAsync(task.Id, cts.Token);

        var reloaded = await _taskRepository.GetByIdAsync(task.Id);
        Assert.NotNull(reloaded);
        Assert.Equal(TaskOverallStatus.Cancelled, reloaded.Status);

        var convs = await _taskRepository.GetConversationsAsync(task.Id);
        var conv = Assert.Single(convs);
        Assert.Equal(StageExecutionStatus.Cancelled, conv.Status);
    }

    [Fact]
    public async Task ProcessTaskAsync_WhenGloballyPausedDuringExecution_CheckpointsTaskAsOpen()
    {
        var ws = new Workspace
        {
            Id = 1,
            Name = "Test Ws",
            RepositoryPath = "C:/test",
            DefaultBaseBranch = "main"
        };
        await _workspaceRepository.CreateAsync(ws);

        var task = new ForgeTask
        {
            Id = 30,
            WorkspaceId = 1,
            Title = "Task Paused Globally",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.Implementation
        };
        await _taskRepository.CreateAsync(task);
        await _taskRepository.StartConversationAsync(task.Id, StandardStageIds.Implementation, WorkflowStageKind.Agentic, 1);
        _dependencyStore.AddTask(task.Id, 1, TaskOverallStatus.Open);

        using var cts = new CancellationTokenSource();
        var pipelineProvider = new CancellingPipelineProvider(async (ctx, ct) =>
        {
            await Task.Yield();
            // Global pause set mid-execution
            _operationsControlMock.Setup(o => o.IsPaused).Returns(true);
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return StageExecutionResult.Success();
        });

        var builder = new TestOrchestratorBuilder
        {
            TaskRepository = _taskRepository,
            WorkspaceRepository = _workspaceRepository,
            DependencyStore = _dependencyStore,
            CancellationManager = _cancellationManager,
            OperationsControlService = _operationsControlMock.Object,
            PipelineProvider = pipelineProvider
        };

        var orchestrator = builder.Build();

        await orchestrator.ProcessTaskAsync(task.Id, cts.Token);

        var reloaded = await _taskRepository.GetByIdAsync(task.Id);
        Assert.NotNull(reloaded);
        Assert.Equal(TaskOverallStatus.Open, reloaded.Status);
        Assert.Equal(StandardStageIds.Implementation, reloaded.InterruptedStageId);

        var convs = await _taskRepository.GetConversationsAsync(task.Id);
        var conv = Assert.Single(convs);
        Assert.Equal(StageExecutionStatus.Interrupted, conv.Status);
    }

    [Fact]
    public async Task ProcessTaskAsync_WhenPausedWhileWaitingForLock_DefersExecution()
    {
        var ws = new Workspace { Id = 1, Name = "Test Ws", RepositoryPath = "C:/test" };
        await _workspaceRepository.CreateAsync(ws);

        var task = new ForgeTask
        {
            Id = 55,
            WorkspaceId = 1,
            Title = "Lock Contention Paused Task",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.Implementation
        };
        await _taskRepository.CreateAsync(task);
        _dependencyStore.AddTask(task.Id, 1, TaskOverallStatus.Open);

        var lockProvider = new WorkspaceLockProvider();
        var lockHandle = await lockProvider.AcquireTaskLockAsync(ws.Id, task.Id);

        var builder = new TestOrchestratorBuilder
        {
            TaskRepository = _taskRepository,
            WorkspaceRepository = _workspaceRepository,
            DependencyStore = _dependencyStore,
            LockProvider = lockProvider,
            CancellationManager = _cancellationManager,
            OperationsControlService = _operationsControlMock.Object
        };
        var orchestrator = builder.Build();

        var processTask = Task.Run(async () => await orchestrator.ProcessTaskAsync(task.Id));

        await Task.Delay(50);
        _operationsControlMock.Setup(o => o.IsPaused).Returns(true);
        lockHandle.Dispose();

        await processTask;

        var reloaded = await _taskRepository.GetByIdAsync(task.Id);
        Assert.NotNull(reloaded);
        Assert.Equal(TaskOverallStatus.Open, reloaded.Status);
        Assert.True(reloaded.IsDeferredByPause);
        Assert.False(reloaded.HasExecutionRun);
    }

    [Fact]
    public void TaskCancellationManager_Register_WhenPauseRequestedPriorToRegistration_CancelsReturnedToken()
    {
        var manager = new TaskCancellationManager(NullLogger<TaskCancellationManager>.Instance);
        manager.RequestPause(100);

        Assert.True(manager.IsPauseRequested(100));

        using var cts = manager.Register(100);
        Assert.True(cts.IsCancellationRequested);
        Assert.True(manager.IsPauseRequested(100));
    }

    [Fact]
    public async Task CriticalGitSection_DoesNotAbortMidCommit_OnPause()
    {
        var ws = new Workspace
        {
            Id = 1,
            Name = "Test Ws",
            RepositoryPath = "C:/test",
            DefaultBaseBranch = "main",
            Ext4MirrorPath = "C:/ext4/test"
        };
        await _workspaceRepository.CreateAsync(ws);

        var task = new ForgeTask
        {
            Id = 77,
            WorkspaceId = 1,
            Title = "Task with AutoMerge",
            Status = TaskOverallStatus.Ready,
            BaseBranch = "main"
        };
        await _taskRepository.CreateAsync(task);

        bool squashReceivedNonCancellableToken = false;
        bool pushReceivedNonCancellableToken = false;

        var gitWorkspaceManagerMock = new Mock<IGitWorkspaceManager>();
        gitWorkspaceManagerMock.Setup(g => g.GetBranchShaAsync(ws, task.BaseBranch, It.IsAny<CancellationToken>()))
            .ReturnsAsync("pre-squash-sha");
        gitWorkspaceManagerMock.Setup(g => g.SquashAndMergeAsync(ws, task.Id, task.BaseBranch, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Workspace, int, string, string, CancellationToken>((w, tid, b, msg, ct) =>
            {
                squashReceivedNonCancellableToken = !ct.CanBeCanceled;
                _cancellationManager.RequestPause(task.Id);
                return Task.FromResult("squash-sha-123");
            });
        gitWorkspaceManagerMock.Setup(g => g.RemoveWorktreeAsync(ws, task.Id, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var gitMirrorSyncMock = new Mock<IGitMirrorSyncService>();
        gitMirrorSyncMock.Setup(g => g.PushBaseBranchToHostAsync(ws.Ext4MirrorPath, task.BaseBranch, It.IsAny<CancellationToken>()))
            .Returns<string, string, CancellationToken>((path, branch, ct) =>
            {
                pushReceivedNonCancellableToken = !ct.CanBeCanceled;
                return Task.FromResult(new GitSyncResult(Success: true, FallbackUsed: false, BranchPushed: branch, WarningMessage: null));
            });

        var builder = new TestOrchestratorBuilder
        {
            TaskRepository = _taskRepository,
            WorkspaceRepository = _workspaceRepository,
            DependencyStore = _dependencyStore,
            CancellationManager = _cancellationManager,
            OperationsControlService = _operationsControlMock.Object,
            GitWorkspaceManager = gitWorkspaceManagerMock.Object,
            GitMirrorSyncService = gitMirrorSyncMock.Object
        };
        var orchestrator = builder.Build();

        var result = await orchestrator.CompleteTaskAndCascadeAsync(task.Id, "Final commit for task 77");

        Assert.NotNull(result);
        Assert.True(squashReceivedNonCancellableToken, "SquashAndMergeAsync must receive a non-cancellable token (CancellationToken.None).");
        Assert.True(pushReceivedNonCancellableToken, "PushBaseBranchToHostAsync must receive a non-cancellable token (CancellationToken.None).");

        var reloaded = await _taskRepository.GetByIdAsync(task.Id);
        Assert.NotNull(reloaded);
        Assert.Equal(TaskOverallStatus.Closed, reloaded.Status);
    }

    [Fact]
    public async Task RejectTaskAsync_And_RestartTaskPipelineAsync_TransitionInterruptedConversationsToCancelledOrSuperseded()
    {
        var ws = new Workspace
        {
            Id = 1,
            Name = "Test Ws",
            RepositoryPath = "C:/test",
            DefaultBaseBranch = "main"
        };
        await _workspaceRepository.CreateAsync(ws);

        // 1. RejectTaskAsync cancels an Interrupted conversation
        var taskToReject = new ForgeTask
        {
            Id = 81,
            WorkspaceId = 1,
            Title = "Paused Task to Reject",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.Implementation,
            InterruptedStageId = StandardStageIds.Implementation,
            IsDeferredByPause = true
        };
        await _taskRepository.CreateAsync(taskToReject);
        _dependencyStore.AddTask(taskToReject.Id, 1, TaskOverallStatus.Open);

        var convToCancel = await _taskRepository.StartConversationAsync(taskToReject.Id, StandardStageIds.Implementation, WorkflowStageKind.Agentic, 1);
        await _taskRepository.CompleteConversationAsync(convToCancel.Id, StageExecutionStatus.Interrupted, summary: "Paused");

        // 2. RestartTaskPipelineAsync supersedes an Interrupted conversation
        var taskToRestart = new ForgeTask
        {
            Id = 82,
            WorkspaceId = 1,
            Title = "Paused Task to Restart",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.Implementation,
            InterruptedStageId = StandardStageIds.Implementation,
            IsDeferredByPause = true
        };
        await _taskRepository.CreateAsync(taskToRestart);
        _dependencyStore.AddTask(taskToRestart.Id, 1, TaskOverallStatus.Open);

        var convToSupersede = await _taskRepository.StartConversationAsync(taskToRestart.Id, StandardStageIds.Implementation, WorkflowStageKind.Agentic, 1);
        await _taskRepository.CompleteConversationAsync(convToSupersede.Id, StageExecutionStatus.Interrupted, summary: "Paused");

        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = _taskRepository,
            WorkspaceRepository = _workspaceRepository,
            DependencyStore = _dependencyStore,
            CancellationManager = _cancellationManager,
            OperationsControlService = _operationsControlMock.Object
        }.Build();

        await orchestrator.RejectTaskAsync(taskToReject.Id, "No longer needed");
        var rejectedConv = await _taskRepository.GetConversationWithTurnsAsync(convToCancel.Id);
        Assert.NotNull(rejectedConv);
        Assert.Equal(StageExecutionStatus.Cancelled, rejectedConv.Status);

        await orchestrator.RestartTaskPipelineAsync(taskToRestart.Id, TaskRestartGitMode.PreserveWork);
        var supersededConv = await _taskRepository.GetConversationWithTurnsAsync(convToSupersede.Id);
        Assert.NotNull(supersededConv);
        Assert.Equal(StageExecutionStatus.Superseded, supersededConv.Status);
    }

    [Fact]
    public async Task ProcessTaskAsync_WhenPausedDuringHarnessExecution_PreservesLastHumanAnswerAndPrimingInDataBagForNextResume()
    {
        var ws = new Workspace
        {
            Id = 1,
            Name = "Test Ws",
            RepositoryPath = "C:/test",
            DefaultBaseBranch = "main",
            AgentProfiles = new List<AgentProfile>
            {
                new()
                {
                    Id = StandardStageIds.Implementation,
                    Name = "Impl Profile",
                    HarnessId = "crucible",
                    ModelName = "qwen3",
                    Mode = AgentMode.Full,
                    OptimizationFlags = new LocalOptimizationFlags { SyntheticConversationPriming = true }
                }
            }
        };
        await _workspaceRepository.CreateAsync(ws);

        var initialBag = new PipelineDataBag();
        initialBag.Set(PipelineDataBagKeys.LastHumanAnswer, "Confirmed architecture");
        initialBag.Set(PipelineDataBagKeys.PendingSyntheticPriming, new SyntheticPrimingExecution(
            SourceStageId: StandardStageIds.PreImplementationBaseline,
            Command: "dotnet build",
            Output: "Build succeeded",
            IsSuccess: true,
            DurationMs: 50));

        var task = new ForgeTask
        {
            Id = 90,
            WorkspaceId = 1,
            Title = "Task Paused Before Harness Completion",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.Implementation,
            InterruptedStageId = StandardStageIds.Implementation,
            PipelineDataJson = initialBag.ToJson()
        };
        await _taskRepository.CreateAsync(task);
        _dependencyStore.AddTask(task.Id, 1, TaskOverallStatus.Open);

        var waitingConv = await _taskRepository.StartConversationAsync(task.Id, StandardStageIds.Implementation, WorkflowStageKind.Agentic, 1);
        await _taskRepository.CompleteConversationAsync(waitingConv.Id, StageExecutionStatus.WaitingForHuman, summary: "Waiting for answer");

        var harnessMock = new Mock<IAgentHarness>();
        harnessMock.SetupGet(h => h.HarnessId).Returns("crucible");
        int runCount = 0;
        AgentSessionContext? secondRunContext = null;

        harnessMock
            .Setup(h => h.RunStageAsync(It.IsAny<AgentSessionContext>(), It.IsAny<CancellationToken>()))
            .Returns<AgentSessionContext, CancellationToken>(async (ctx, ct) =>
            {
                runCount++;
                await Task.Yield();
                if (runCount == 1)
                {
                    _cancellationManager.RequestPause(task.Id);
                    throw new OperationCanceledException(ct);
                }

                secondRunContext = ctx;
                await _taskRepository.CompleteConversationAsync(waitingConv.Id, StageExecutionStatus.Completed, summary: "Done");
                return AgentRunResult.Completed(new AgentRunMetrics(10, 5, 5, 10), summary: "Completed on resume");
            });

        var registryMock = new Mock<IHarnessRegistry>();
        IAgentHarness? outHarness = harnessMock.Object;
        registryMock.Setup(r => r.TryGetHarness("crucible", out outHarness)).Returns(true);

        var implStage = new TestWorkflowStage(StandardStageIds.Implementation, WorkflowStageKind.Agentic)
        {
            QueueTier = StageQueueTier.Implementation,
            RequiredMode = AgentMode.Full
        };

        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { implStage });
        var pipelineProvider = new DefaultWorkflowPipelineProvider(pipeline);

        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = _taskRepository,
            WorkspaceRepository = _workspaceRepository,
            DependencyStore = _dependencyStore,
            CancellationManager = _cancellationManager,
            OperationsControlService = _operationsControlMock.Object,
            HarnessRegistry = registryMock.Object,
            PipelineProvider = pipelineProvider
        }.Build();

        // 1. First execution is paused inside RunStageAsync
        await orchestrator.ProcessTaskAsync(task.Id);

        var pausedTask = await _taskRepository.GetByIdAsync(task.Id);
        Assert.NotNull(pausedTask);
        Assert.True(pausedTask.IsDeferredByPause);
        Assert.Equal(StandardStageIds.Implementation, pausedTask.InterruptedStageId);

        var pausedBag = PipelineDataBag.FromJson(pausedTask.PipelineDataJson);
        Assert.Equal("Confirmed architecture", pausedBag.Get<string>(PipelineDataBagKeys.LastHumanAnswer));
        Assert.NotNull(pausedBag.Get<SyntheticPrimingExecution>(PipelineDataBagKeys.PendingSyntheticPriming));

        // WaitingForHuman conversation must NOT have been overwritten to Interrupted before harness resumed it
        var convAfterPause = await _taskRepository.GetConversationWithTurnsAsync(waitingConv.Id);
        Assert.Equal(StageExecutionStatus.WaitingForHuman, convAfterPause!.Status);

        // 2. Clear pause and resume: second run must still receive PendingHumanAnswer and SyntheticPriming, then consume them
        _cancellationManager.ClearPause(task.Id);
        await orchestrator.ProcessTaskAsync(task.Id);

        Assert.NotNull(secondRunContext);
        Assert.Equal("Confirmed architecture", secondRunContext!.PendingHumanAnswer);
        Assert.NotNull(secondRunContext.SyntheticPriming);

        var completedTask = await _taskRepository.GetByIdAsync(task.Id);
        var finalBag = PipelineDataBag.FromJson(completedTask!.PipelineDataJson);
        Assert.Null(finalBag.Get<string>(PipelineDataBagKeys.LastHumanAnswer));
        Assert.Null(finalBag.Get<SyntheticPrimingExecution>(PipelineDataBagKeys.PendingSyntheticPriming));
    }
}
