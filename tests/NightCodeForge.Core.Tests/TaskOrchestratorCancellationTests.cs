using NightCodeForge.Core.Concurrency;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
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
            var stage = new GenericAgenticStage("test-stage", "Test Stage", new(), new MockPromptRenderer())
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
}
