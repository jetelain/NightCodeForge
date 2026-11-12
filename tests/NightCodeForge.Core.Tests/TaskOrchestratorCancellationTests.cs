using Moq;
using NightCodeForge.Core.Concurrency;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Core.Workflow.Stages;

namespace NightCodeForge.Core.Tests;

public class TaskOrchestratorCancellationTests
{
    private readonly InMemoryTaskRepository _taskRepo = new();
    private readonly InMemoryWorkspaceRepository _workspaceRepo = new();
    private readonly InMemoryTaskDependencyStore _depStore = new();
    private readonly WorkspaceLockProvider _lockProvider = new();
    private readonly FakeTaskJobQueue _jobQueue = new();
    private readonly ITaskCancellationManager _cancellationManager = new TaskCancellationManager(Microsoft.Extensions.Logging.Abstractions.NullLogger<TaskCancellationManager>.Instance);
    private readonly Workspace _workspace;

    public TaskOrchestratorCancellationTests()
    {
        _workspace = new Workspace
        {
            Id = 1,
            Name = "Cancellation Test Workspace",
            RepositoryPath = "C:/test/repo",
            DefaultBaseBranch = "main"
        };
        _workspaceRepo.CreateAsync(_workspace).GetAwaiter().GetResult();
    }

    private TaskOrchestrator CreateOrchestrator(IWorkflowPipelineProvider pipelineProvider)
    {
        return new Fixtures.TestOrchestratorBuilder
        {
            TaskRepository = _taskRepo,
            WorkspaceRepository = _workspaceRepo,
            LockProvider = _lockProvider,
            JobQueue = _jobQueue,
            DependencyStore = _depStore,
            CancellationManager = _cancellationManager,
            PipelineProvider = pipelineProvider
        }.Build();
    }

    private class CancellingPipelineProvider : IWorkflowPipelineProvider
    {
        private readonly Func<PipelineContext, CancellationToken, Task<StageExecutionResult>> _stageFunc;

        public CancellingPipelineProvider(Func<PipelineContext, CancellationToken, Task<StageExecutionResult>> stageFunc)
        {
            _stageFunc = stageFunc;
        }

        public Task<IWorkflowPipeline> GetPipelineAsync(ForgeTask task, CancellationToken cancellationToken = default)
        {
            var stage = new GenericAgenticStage("test-stage", "Test Stage", new(), new MockPromptRenderer(), TimeProvider.System)
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
    public async Task ProcessTaskAsync_WhenOperationCanceledExceptionThrown_PreservesTaskStatusAndMarksExecutionCancelled()
    {
        using var cts = new CancellationTokenSource();

        var pipelineProvider = new CancellingPipelineProvider(async (ctx, ct) =>
        {
            // Simulate work then cancellation
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return StageExecutionResult.Success();
        });

        var orchestrator = CreateOrchestrator(pipelineProvider);

        var task = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 42,
            WorkspaceId = 1,
            Status = TaskOverallStatus.Open
        });
        _depStore.AddTask(task.Id, _workspace.Id, TaskOverallStatus.Open);

        // Act
        await orchestrator.ProcessTaskAsync(task.Id, cts.Token);

        // Assert
        var updatedTask = await _taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(updatedTask);
        // Crucial requirement: cancellation MUST NOT mark task as Failed
        Assert.NotEqual(TaskOverallStatus.Failed, updatedTask.Status);

        // Verify execution record
        var records = _taskRepo.GetAllExecutionRecords().Where(r => r.TaskId == task.Id).ToList();
        var lastRecord = records.LastOrDefault();
        Assert.NotNull(lastRecord);
        Assert.Equal(StageExecutionStatus.Cancelled, lastRecord.Status);
    }

    [Fact]
    public async Task ProcessTaskAsync_WhenStageReturnsCancelledResult_PreservesTaskStatusAndRecordsCancelled()
    {
        var pipelineProvider = new CancellingPipelineProvider((ctx, ct) =>
        {
            return Task.FromResult(StageExecutionResult.Cancelled("User requested cancellation"));
        });

        var orchestrator = CreateOrchestrator(pipelineProvider);

        var task = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 43,
            WorkspaceId = 1,
            Status = TaskOverallStatus.Open
        });
        _depStore.AddTask(task.Id, _workspace.Id, TaskOverallStatus.Open);

        // Act
        await orchestrator.ProcessTaskAsync(task.Id, CancellationToken.None);

        // Assert
        var updatedTask = await _taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(updatedTask);
        Assert.NotEqual(TaskOverallStatus.Failed, updatedTask.Status);

        var records = _taskRepo.GetAllExecutionRecords().Where(r => r.TaskId == task.Id).ToList();
        var lastRecord = records.LastOrDefault();
        Assert.NotNull(lastRecord);
        Assert.Equal(StageExecutionStatus.Cancelled, lastRecord.Status);
    }

    [Fact]
    public async Task ProcessTaskAsync_WhenTaskCancellationManagerSignalsCancel_CancelsRunningOrchestratorAndPersistsCancelledStatus()
    {
        var task = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 44,
            WorkspaceId = 1,
            Status = TaskOverallStatus.Open
        });
        _depStore.AddTask(task.Id, _workspace.Id, TaskOverallStatus.Open);

        var pipelineProvider = new CancellingPipelineProvider(async (ctx, ct) =>
        {
            // Simulate external cancellation via ITaskCancellationManager
            _cancellationManager.Cancel(task.Id);
            ct.ThrowIfCancellationRequested();
            await Task.Delay(10, ct);
            return StageExecutionResult.Success();
        });

        var orchestrator = CreateOrchestrator(pipelineProvider);

        // Act
        await orchestrator.ProcessTaskAsync(task.Id, CancellationToken.None);

        // Assert
        var updatedTask = await _taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(updatedTask);
        Assert.Equal(TaskOverallStatus.Cancelled, updatedTask.Status);

        var records = _taskRepo.GetAllExecutionRecords().Where(r => r.TaskId == task.Id).ToList();
        var lastRecord = records.LastOrDefault();
        Assert.NotNull(lastRecord);
        Assert.Equal(StageExecutionStatus.Cancelled, lastRecord.Status);
    }

    [Fact]
    public async Task ProcessTaskAsync_WhenTaskCancelledInDatabase_StageTransitionDoesNotOverwriteStatus()
    {
        var task = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 45,
            WorkspaceId = 1,
            Status = TaskOverallStatus.Open
        });
        _depStore.AddTask(task.Id, _workspace.Id, TaskOverallStatus.Open);

        var pipelineProvider = new CancellingPipelineProvider(async (ctx, ct) =>
        {
            // Simulate out-of-band cancellation update in database while stage is executing
            task.Status = TaskOverallStatus.Cancelled;
            await _taskRepo.UpdateAsync(task);
            return StageExecutionResult.Success();
        });

        var orchestrator = CreateOrchestrator(pipelineProvider);

        // Act
        await orchestrator.ProcessTaskAsync(task.Id, CancellationToken.None);

        // Assert
        var updatedTask = await _taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(updatedTask);
        // Stage transition must not overwrite out-of-band cancelled status
        Assert.Equal(TaskOverallStatus.Cancelled, updatedTask.Status);
    }

    [Fact]
    public async Task ProcessTaskAsync_WhenUnhandledTimeoutOperationCanceledExceptionThrown_MarksStageAsFailedNotCancelled()
    {
        var task = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 51,
            WorkspaceId = 1,
            Status = TaskOverallStatus.Open
        });
        _depStore.AddTask(task.Id, _workspace.Id, TaskOverallStatus.Open);

        var pipelineProvider = new CancellingPipelineProvider((ctx, ct) =>
        {
            // Simulate uncancelled HttpClient.Timeout throwing TaskCanceledException wrapping TimeoutException
            var timeoutEx = new TaskCanceledException(
                "The request was canceled due to the configured HttpClient.Timeout of 900 seconds elapsing.",
                new TimeoutException("The operation was canceled."));
            throw timeoutEx;
        });

        var orchestrator = CreateOrchestrator(pipelineProvider);

        // Act
        await orchestrator.ProcessTaskAsync(task.Id, CancellationToken.None);

        // Assert
        var updatedTask = await _taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(updatedTask);
        Assert.Equal(TaskOverallStatus.Failed, updatedTask.Status);

        var history = await _taskRepo.GetHistoryAsync(task.Id);
        Assert.NotEmpty(history);
        Assert.Contains("LLM inference request timed out after 900 seconds", history.Last().ChangeReason);

        var records = _taskRepo.GetAllExecutionRecords().Where(r => r.TaskId == task.Id).ToList();
        var lastRecord = records.LastOrDefault();
        Assert.NotNull(lastRecord);
        Assert.Equal(StageExecutionStatus.Failed, lastRecord.Status);
    }

    [Fact]
    public async Task ProcessTaskAsync_WhenHostShuttingDownDuringStage_MarksConversationInterrupted_PreservesOpenStatusAndInterruptedStageId()
    {
        var task = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 52,
            WorkspaceId = 1,
            Status = TaskOverallStatus.Open,
            CurrentStageId = "test-stage"
        });
        _depStore.AddTask(task.Id, _workspace.Id, TaskOverallStatus.Open);

        var conv = await _taskRepo.StartConversationAsync(task.Id, "test-stage", WorkflowStageKind.Agentic, 1);
        await _taskRepo.AddConversationTurnAsync(conv.Id, StageTurnRole.User, content: "Initial prompt");

        using var cts = new CancellationTokenSource();
        var pipelineProvider = new CancellingPipelineProvider((ctx, ct) =>
        {
            _cancellationManager.NotifyHostShuttingDown();
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(StageExecutionResult.Success());
        });

        var orchestrator = CreateOrchestrator(pipelineProvider);

        await orchestrator.ProcessTaskAsync(task.Id, cts.Token);

        var updatedTask = await _taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(updatedTask);
        Assert.Equal(TaskOverallStatus.Open, updatedTask.Status);
        Assert.Equal("test-stage", updatedTask.InterruptedStageId);

        var convs = await _taskRepo.GetConversationsAsync(task.Id);
        var updatedConv = Assert.Single(convs);
        Assert.Equal(StageExecutionStatus.Interrupted, updatedConv.Status);
        Assert.Contains("interrupted by application shutdown", updatedConv.Summary);

        var logs = _taskRepo.GetAllLogs().Where(l => l.TaskId == task.Id).ToList();
        Assert.Contains(logs, l => l.Message.Contains("Stage 'test-stage' execution interrupted by application shutdown."));
    }

    [Fact]
    public async Task ProcessTaskAsync_WhenTaskAlreadyFailedAtEntry_AbortsExecutionImmediately()
    {
        bool stageExecuted = false;
        var pipelineProvider = new CancellingPipelineProvider((ctx, ct) =>
        {
            stageExecuted = true;
            return Task.FromResult(StageExecutionResult.Success());
        });

        var orchestrator = CreateOrchestrator(pipelineProvider);

        var task = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 53,
            WorkspaceId = 1,
            Status = TaskOverallStatus.Failed
        });
        _depStore.AddTask(task.Id, _workspace.Id, TaskOverallStatus.Failed);

        await orchestrator.ProcessTaskAsync(task.Id, CancellationToken.None);

        Assert.False(stageExecuted);
        var reloaded = await _taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(reloaded);
        Assert.Equal(TaskOverallStatus.Failed, reloaded.Status);
    }

    [Fact]
    public async Task ProcessTaskAsync_WhenTaskTransitionsToFailedWhileWaitingForTaskLock_AbortsExecutionImmediately()
    {
        bool stageExecuted = false;
        var pipelineProvider = new CancellingPipelineProvider((ctx, ct) =>
        {
            stageExecuted = true;
            return Task.FromResult(StageExecutionResult.Success());
        });

        var orchestrator = CreateOrchestrator(pipelineProvider);

        var task = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 54,
            WorkspaceId = 1,
            Status = TaskOverallStatus.Open
        });
        _depStore.AddTask(task.Id, _workspace.Id, TaskOverallStatus.Open);

        var lockHandle = await _lockProvider.AcquireTaskLockAsync(_workspace.Id, task.Id);

        var runTask = Task.Run(() => orchestrator.ProcessTaskAsync(task.Id, CancellationToken.None));
        await Task.Delay(50);

        task.Status = TaskOverallStatus.Failed;
        await _taskRepo.UpdateAsync(task);
        lockHandle.Dispose();

        await runTask;

        Assert.False(stageExecuted);
        var reloaded = await _taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(reloaded);
        Assert.Equal(TaskOverallStatus.Failed, reloaded.Status);
    }

    [Fact]
    public async Task ProcessTaskAsync_WhenTaskTransitionsToFailedWhileWaitingForBareHostLock_AbortsExecutionImmediately()
    {
        var bareHostWs = await _workspaceRepo.CreateAsync(new Workspace
        {
            Id = 2,
            Name = "BareHost Workspace",
            RepositoryPath = "C:/test/barehost",
            DefaultBaseBranch = "main",
            ExecutionMode = ExecutionMode.BareHost
        });

        bool stageExecuted = false;
        var pipelineProvider = new CancellingPipelineProvider((ctx, ct) =>
        {
            stageExecuted = true;
            return Task.FromResult(StageExecutionResult.Success());
        });

        var orchestrator = CreateOrchestrator(pipelineProvider);

        var task = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 55,
            WorkspaceId = bareHostWs.Id,
            Status = TaskOverallStatus.Open
        });
        _depStore.AddTask(task.Id, bareHostWs.Id, TaskOverallStatus.Open);

        var sentinelHandle = await _lockProvider.AcquireTaskLockAsync(
            bareHostWs.Id,
            WorkspaceLockProvider.WorkspaceExecutionSentinelTaskId);

        var runTask = Task.Run(() => orchestrator.ProcessTaskAsync(task.Id, CancellationToken.None));
        await Task.Delay(50);

        task.Status = TaskOverallStatus.Failed;
        await _taskRepo.UpdateAsync(task);
        sentinelHandle.Dispose();

        await runTask;

        Assert.False(stageExecuted);
        var reloaded = await _taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(reloaded);
        Assert.Equal(TaskOverallStatus.Failed, reloaded.Status);
    }

    private sealed class TwoStageSameCohortPipelineProvider : IWorkflowPipelineProvider
    {
        private readonly Func<PipelineContext, CancellationToken, Task<StageExecutionResult>> _stage1Func;
        private readonly Func<PipelineContext, CancellationToken, Task<StageExecutionResult>> _stage2Func;

        public TwoStageSameCohortPipelineProvider(
            Func<PipelineContext, CancellationToken, Task<StageExecutionResult>> stage1Func,
            Func<PipelineContext, CancellationToken, Task<StageExecutionResult>> stage2Func)
        {
            _stage1Func = stage1Func;
            _stage2Func = stage2Func;
        }

        public Task<IWorkflowPipeline> GetPipelineAsync(ForgeTask task, CancellationToken cancellationToken = default)
        {
            var stage1 = new GenericAgenticStage("stage-1", "Stage 1", new(), new MockPromptRenderer(), TimeProvider.System)
            {
                Handler = _stage1Func
            };
            var stage2 = new GenericAgenticStage("stage-2", "Stage 2", new(), new MockPromptRenderer(), TimeProvider.System)
            {
                Handler = _stage2Func
            };
            return Task.FromResult<IWorkflowPipeline>(new WorkflowPipeline(new IWorkflowStage[] { stage1, stage2 }));
        }
    }

    [Fact]
    public async Task ProcessTaskAsync_WhenHostShuttingDownBetweenInProcessStages_SetsInterruptedStageIdToNextStage()
    {
        using var cts = new CancellationTokenSource();
        bool stage2Entered = false;

        var pipelineProvider = new TwoStageSameCohortPipelineProvider(
            stage1Func: (ctx, ct) => Task.FromResult(StageExecutionResult.Success()),
            stage2Func: (ctx, ct) =>
            {
                stage2Entered = true;
                return Task.FromResult(StageExecutionResult.Success());
            });

        var markdownSyncMock = new Mock<ITaskMarkdownSyncService>();
        markdownSyncMock
            .Setup(m => m.SyncAllTasksToDiskAsync(
                It.IsAny<Workspace>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SyncAllToDiskResult(
                new Dictionary<int, string>(),
                new Dictionary<int, TaskBaselineEntry>()));
        markdownSyncMock
            .Setup(m => m.SyncAllTasksFromDiskAsync(
                It.IsAny<Workspace>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<IReadOnlyDictionary<int, TaskBaselineEntry>?>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ForgeTask>());
        markdownSyncMock
            .Setup(m => m.SyncTaskToDiskAsync(
                It.IsAny<ForgeTask>(),
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<int>?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                // Host shutdown occurs right after stage-1 persisted advancement to stage-2, before loop continues to stage-2
                _cancellationManager.NotifyHostShuttingDown();
                cts.Cancel();
            })
            .ReturnsAsync("C:/test/repo/docs/tasks/0056-task.md");

        var orchestrator = new Fixtures.TestOrchestratorBuilder
        {
            TaskRepository = _taskRepo,
            WorkspaceRepository = _workspaceRepo,
            LockProvider = _lockProvider,
            JobQueue = _jobQueue,
            DependencyStore = _depStore,
            CancellationManager = _cancellationManager,
            PipelineProvider = pipelineProvider,
            MarkdownSyncService = markdownSyncMock.Object
        }.Build();

        var task = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 56,
            WorkspaceId = _workspace.Id,
            Status = TaskOverallStatus.Open,
            CurrentStageId = "stage-1"
        });
        _depStore.AddTask(task.Id, _workspace.Id, TaskOverallStatus.Open);

        await orchestrator.ProcessTaskAsync(task.Id, cts.Token);

        Assert.False(stage2Entered);
        var updatedTask = await _taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(updatedTask);
        Assert.Equal(TaskOverallStatus.Open, updatedTask.Status);
        Assert.Equal("stage-2", updatedTask.CurrentStageId);
        Assert.Equal("stage-2", updatedTask.InterruptedStageId);
    }

    [Fact]
    public async Task ProcessTaskAsync_WhenHostShuttingDownAndTaskConcurrentlyMarkedFailedInDb_DoesNotOverwriteFailedStatus()
    {
        var task = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 57,
            WorkspaceId = _workspace.Id,
            Status = TaskOverallStatus.Open,
            CurrentStageId = "test-stage"
        });
        _depStore.AddTask(task.Id, _workspace.Id, TaskOverallStatus.Open);

        using var cts = new CancellationTokenSource();
        var pipelineProvider = new CancellingPipelineProvider(async (ctx, ct) =>
        {
            var dbTask = await _taskRepo.GetByIdAsync(task.Id);
            dbTask!.Status = TaskOverallStatus.Failed;
            await _taskRepo.UpdateAsync(dbTask);

            _cancellationManager.NotifyHostShuttingDown();
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return StageExecutionResult.Success();
        });

        var orchestrator = CreateOrchestrator(pipelineProvider);

        await orchestrator.ProcessTaskAsync(task.Id, cts.Token);

        var updatedTask = await _taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(updatedTask);
        Assert.Equal(TaskOverallStatus.Failed, updatedTask.Status);
    }
}
