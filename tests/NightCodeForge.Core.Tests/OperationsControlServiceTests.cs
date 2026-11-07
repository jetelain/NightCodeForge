using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Workflow;
using Xunit;

namespace NightCodeForge.Core.Tests;

public class OperationsControlServiceTests
{
    private readonly Mock<IOperationsStateStore> _stateStoreMock = new();
    private readonly Mock<ILlmGatewayService> _gatewayMock = new();
    private readonly Mock<IWorkerStateService> _workerStateMock = new();
    private readonly Mock<ITaskCancellationManager> _cancellationManagerMock = new();
    private readonly Mock<ITaskJobQueue> _jobQueueMock = new();
    private readonly Mock<IDependencyResolutionEngine> _dependencyEngineMock = new();
    private readonly Mock<IWorkflowPipelineProvider> _pipelineProviderMock = new();
    private readonly Mock<IExecutionEnvironmentProvider> _environmentProviderMock = new();
    private readonly InMemoryTaskRepository _taskRepository = new();

    private readonly ServiceProvider _serviceProvider;
    private readonly IServiceScopeFactory _scopeFactory;

    public OperationsControlServiceTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITaskRepository>(_taskRepository);
        services.AddSingleton(_jobQueueMock.Object);
        services.AddSingleton(_dependencyEngineMock.Object);
        services.AddSingleton(_pipelineProviderMock.Object);
        services.AddSingleton(_environmentProviderMock.Object);

        _serviceProvider = services.BuildServiceProvider();
        _scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();

        _stateStoreMock.Setup(s => s.GetIsPausedAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
    }

    private OperationsControlService CreateService(
        OperationsControlOptions? options = null,
        IOperationsStateStore? stateStore = null,
        IServiceScopeFactory? scopeFactory = null)
    {
        return new OperationsControlService(
            stateStore ?? _stateStoreMock.Object,
            _gatewayMock.Object,
            _workerStateMock.Object,
            _cancellationManagerMock.Object,
            scopeFactory ?? _scopeFactory,
            Options.Create(options ?? new OperationsControlOptions()),
            NullLogger<OperationsControlService>.Instance);
    }

    [Fact]
    public async Task PauseOperationsAsync_SetsIsPausedTrue_AndSkipsVramReclaimByDefault()
    {
        _workerStateMock.Setup(w => w.ActiveTaskId).Returns((int?)null);

        var service = CreateService();

        var observedStatuses = new List<OperationsReclaimStatus>();
        bool? stateChangedEventValue = null;
        service.OnOperationsStateChanged += val =>
        {
            stateChangedEventValue = val;
            observedStatuses.Add(service.ReclaimStatus);
        };

        var result = await service.PauseOperationsAsync();

        Assert.True(result);
        Assert.True(service.IsPaused);
        Assert.Equal(OperationsReclaimStatus.Idle, service.ReclaimStatus);
        Assert.Null(service.LastReclaimError);
        Assert.True(stateChangedEventValue);
        Assert.All(observedStatuses, status => Assert.Equal(OperationsReclaimStatus.Idle, status));

        _gatewayMock.Verify(g => g.ReclaimVramAsync(It.IsAny<CancellationToken>()), Times.Never);
        _stateStoreMock.Verify(s => s.SetIsPausedAsync(true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PauseOperationsAsync_WhenReclaimEnabled_SetsIsPausedTrue_AndTriggersVramReclaim()
    {
        _gatewayMock.Setup(g => g.ReclaimVramAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _workerStateMock.Setup(w => w.ActiveTaskId).Returns((int?)null);

        var service = CreateService(new OperationsControlOptions { ReclaimVramOnPause = true });

        bool? stateChangedEventValue = null;
        service.OnOperationsStateChanged += val => stateChangedEventValue = val;

        var result = await service.PauseOperationsAsync();

        Assert.True(result);
        Assert.True(service.IsPaused);
        Assert.Equal(OperationsReclaimStatus.Evicted, service.ReclaimStatus);
        Assert.Null(service.LastReclaimError);
        Assert.True(stateChangedEventValue);

        _gatewayMock.Verify(g => g.ReclaimVramAsync(It.IsAny<CancellationToken>()), Times.Once);
        _stateStoreMock.Verify(s => s.SetIsPausedAsync(true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PauseOperationsAsync_WhenActiveTaskPresent_RequestsPauseAndCleansContainer()
    {
        var task = new ForgeTask
        {
            Id = 42,
            Title = "Active task",
            Status = TaskOverallStatus.Open,
            Workspace = new Workspace { Id = 1, Name = "Ws", RepositoryPath = "C:/ws" }
        };
        await _taskRepository.CreateAsync(task);

        _workerStateMock.SetupSequence(w => w.ActiveTaskId)
            .Returns(42)
            .Returns((int?)null);

        _gatewayMock.Setup(g => g.ReclaimVramAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var service = CreateService(new OperationsControlOptions { ReclaimVramOnPause = true });

        var result = await service.PauseOperationsAsync();

        Assert.True(result);
        _cancellationManagerMock.Verify(c => c.RequestPause(42), Times.Once);
        _environmentProviderMock.Verify(e => e.CleanupTaskContainerAsync(task.Workspace, 42, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PauseOperationsAsync_WhenReclaimFails_SetsStatusFailedWithoutSilentDrop()
    {
        _gatewayMock.Setup(g => g.ReclaimVramAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _workerStateMock.Setup(w => w.ActiveTaskId).Returns((int?)null);

        var service = CreateService(new OperationsControlOptions { ReclaimVramOnPause = true });

        var result = await service.PauseOperationsAsync();

        Assert.True(result);
        Assert.True(service.IsPaused);
        Assert.Equal(OperationsReclaimStatus.Failed, service.ReclaimStatus);
        Assert.NotNull(service.LastReclaimError);
        Assert.Contains("OllamaRouter reported VRAM reclaim failure", service.LastReclaimError);
    }

    [Fact]
    public async Task PauseOperationsAsync_WhenGatewayThrows_RecordsErrorStatus()
    {
        _gatewayMock.Setup(g => g.ReclaimVramAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection refused by OllamaRouter"));
        _workerStateMock.Setup(w => w.ActiveTaskId).Returns((int?)null);

        var service = CreateService(new OperationsControlOptions { ReclaimVramOnPause = true });

        var result = await service.PauseOperationsAsync();

        Assert.True(result);
        Assert.Equal(OperationsReclaimStatus.Failed, service.ReclaimStatus);
        Assert.Contains("Connection refused", service.LastReclaimError);
    }

    [Fact]
    public async Task ResumeOperationsAsync_SetsIsPausedFalse_ClearsPauses_AndDispatchesDeferredTasks()
    {
        var ws = new Workspace { Id = 1, Name = "Ws", RepositoryPath = "C:/ws" };
        var taskReady = new ForgeTask
        {
            Id = 10,
            Title = "Task Ready",
            Status = TaskOverallStatus.Open,
            InterruptedStageId = "impl",
            CurrentStageId = "impl",
            IsDeferredByPause = true,
            Priority = TaskPriority.High,
            Workspace = ws
        };
        var taskBlockedPrereq = new ForgeTask
        {
            Id = 20,
            Title = "Task Blocked Prereq",
            Status = TaskOverallStatus.Open,
            InterruptedStageId = "impl",
            CurrentStageId = "impl",
            IsDeferredByPause = true,
            Priority = TaskPriority.Normal,
            Workspace = ws
        };
        var taskWaitingHuman = new ForgeTask
        {
            Id = 30,
            Title = "Task Waiting Human",
            Status = TaskOverallStatus.Open,
            IsWaitingForHuman = true,
            IsDeferredByPause = true,
            Workspace = ws
        };
        var taskNotDeferred = new ForgeTask
        {
            Id = 40,
            Title = "Task Not Deferred",
            Status = TaskOverallStatus.Open,
            IsDeferredByPause = false,
            Workspace = ws
        };

        await _taskRepository.CreateAsync(taskReady);
        await _taskRepository.CreateAsync(taskBlockedPrereq);
        await _taskRepository.CreateAsync(taskWaitingHuman);
        await _taskRepository.CreateAsync(taskNotDeferred);

        _dependencyEngineMock.Setup(d => d.IsReadyForExecutionAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _dependencyEngineMock.Setup(d => d.IsReadyForExecutionAsync(20, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var stageMock = new Mock<IWorkflowStage>();
        stageMock.Setup(s => s.StageId).Returns("impl");
        stageMock.Setup(s => s.QueueTier).Returns(StageQueueTier.Implementation);

        var pipelineMock = new Mock<IWorkflowPipeline>();
        pipelineMock.Setup(p => p.GetStage("impl")).Returns(stageMock.Object);
        _pipelineProviderMock.Setup(p => p.GetPipelineAsync(It.IsAny<ForgeTask>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(pipelineMock.Object);

        _jobQueueMock.Setup(j => j.EnqueueStage(10, StageQueueTier.Implementation, TaskPriority.High))
            .Returns("job-ready-10");

        _stateStoreMock.Setup(s => s.GetIsPausedAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var service = CreateService();

        bool? stateChangedEventValue = null;
        service.OnOperationsStateChanged += val => stateChangedEventValue = val;

        var result = await service.ResumeOperationsAsync();

        Assert.True(result);
        Assert.False(service.IsPaused);
        Assert.Equal(OperationsReclaimStatus.Idle, service.ReclaimStatus);
        Assert.False(stateChangedEventValue);

        _stateStoreMock.Verify(s => s.SetIsPausedAsync(false, It.IsAny<CancellationToken>()), Times.Once);
        _cancellationManagerMock.Verify(c => c.ClearAllPauses(), Times.Once);

        _jobQueueMock.Verify(j => j.EnqueueStage(10, StageQueueTier.Implementation, TaskPriority.High), Times.Once);
        _jobQueueMock.Verify(j => j.EnqueueStage(20, It.IsAny<StageQueueTier>(), It.IsAny<int>()), Times.Never);
        _jobQueueMock.Verify(j => j.EnqueueStage(40, It.IsAny<StageQueueTier>(), It.IsAny<int>()), Times.Never);

        var reloaded = await _taskRepository.GetByIdAsync(10);
        Assert.NotNull(reloaded);
        Assert.Equal("impl", reloaded.InterruptedStageId);
        Assert.Equal("impl", reloaded.CurrentStageId);
        Assert.False(reloaded.IsDeferredByPause);
        Assert.Equal("job-ready-10", reloaded.LastHangfireJobId);

        var reloadedBlocked = await _taskRepository.GetByIdAsync(20);
        Assert.NotNull(reloadedBlocked);
        Assert.False(reloadedBlocked.IsDeferredByPause);
    }

    [Fact]
    public async Task GetDeferredTaskCountAsync_CountsOnlyOpenNonWaitingDeferredTasks()
    {
        var repo = new InMemoryTaskRepository();
        await repo.CreateAsync(new ForgeTask { Id = 1, Status = TaskOverallStatus.Open, IsDeferredByPause = true });
        await repo.CreateAsync(new ForgeTask { Id = 2, Status = TaskOverallStatus.Open, IsDeferredByPause = true });
        await repo.CreateAsync(new ForgeTask { Id = 3, Status = TaskOverallStatus.Open, IsDeferredByPause = false });
        await repo.CreateAsync(new ForgeTask { Id = 4, Status = TaskOverallStatus.Closed, IsDeferredByPause = true });
        await repo.CreateAsync(new ForgeTask { Id = 5, Status = TaskOverallStatus.Open, IsWaitingForHuman = true, IsDeferredByPause = true });

        var services = new ServiceCollection();
        services.AddSingleton<ITaskRepository>(repo);
        services.AddSingleton(_jobQueueMock.Object);
        services.AddSingleton(_dependencyEngineMock.Object);
        services.AddSingleton(_pipelineProviderMock.Object);
        services.AddSingleton(_environmentProviderMock.Object);
        var sp = services.BuildServiceProvider();

        var service = CreateService(scopeFactory: sp.GetRequiredService<IServiceScopeFactory>());

        var count = await service.GetDeferredTaskCountAsync();
        Assert.Equal(2, count);
    }

    [Fact]
    public void OperationsControlService_Constructor_WhenStateStoreThrows_FailsFast()
    {
        var storeMock = new Mock<IOperationsStateStore>();
        storeMock.Setup(s => s.GetIsPausedAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("DB unavailable"));

        Assert.Throws<InvalidOperationException>(() => CreateService(stateStore: storeMock.Object));
    }

    [Fact]
    public async Task PauseOperationsAsync_WhenStateStoreThrows_FailsFastWithInvalidOperationException()
    {
        _workerStateMock.Setup(w => w.ActiveTaskId).Returns(123);
        _stateStoreMock.Setup(s => s.SetIsPausedAsync(true, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("DB write failure"));

        var service = CreateService();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.PauseOperationsAsync());
        Assert.Contains("Failed persisting operations pause state", ex.Message);

        // In-memory state remains untouched on persistence failure
        Assert.False(service.IsPaused);
        Assert.Equal(OperationsReclaimStatus.Idle, service.ReclaimStatus);

        // Pause request is rolled back on the active task
        _cancellationManagerMock.Verify(c => c.RequestPause(123), Times.Once);
        _cancellationManagerMock.Verify(c => c.ClearPause(123), Times.Once);
    }

    [Fact]
    public async Task ResumeOperationsAsync_WhenStateStoreThrows_FailsFastWithInvalidOperationException()
    {
        _stateStoreMock.Setup(s => s.GetIsPausedAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _stateStoreMock.Setup(s => s.SetIsPausedAsync(false, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("DB write failure"));

        var service = CreateService(new OperationsControlOptions { ReclaimVramOnPause = true });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ResumeOperationsAsync());
        Assert.Contains("Failed persisting operations resume state", ex.Message);

        // In-memory state remains paused on persistence failure
        Assert.True(service.IsPaused);
        Assert.Equal(OperationsReclaimStatus.Evicted, service.ReclaimStatus);
        _cancellationManagerMock.Verify(c => c.ClearAllPauses(), Times.Never);
    }

    [Fact]
    public async Task ResumeOperationsAsync_WhenSomeTasksFailToEnqueue_ProcessesRemainingTasksAndThrowsAggregateException()
    {
        var taskFailing = new ForgeTask
        {
            Id = 100,
            Title = "Task Failing Re-enqueue",
            Status = TaskOverallStatus.Open,
            InterruptedStageId = "failing-stage",
            IsDeferredByPause = true,
            Priority = TaskPriority.Normal
        };
        var taskSucceeding = new ForgeTask
        {
            Id = 200,
            Title = "Task Succeeding Re-enqueue",
            Status = TaskOverallStatus.Open,
            InterruptedStageId = "good-stage",
            IsDeferredByPause = true,
            Priority = TaskPriority.High
        };
        await _taskRepository.CreateAsync(taskFailing);
        await _taskRepository.CreateAsync(taskSucceeding);

        _dependencyEngineMock.Setup(d => d.IsReadyForExecutionAsync(100, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _dependencyEngineMock.Setup(d => d.IsReadyForExecutionAsync(200, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        _pipelineProviderMock.Setup(p => p.GetPipelineAsync(It.Is<ForgeTask>(t => t.Id == 100), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Workflow provider error for task 100"));

        var goodStageMock = new Mock<IWorkflowStage>();
        goodStageMock.Setup(s => s.StageId).Returns("good-stage");
        goodStageMock.Setup(s => s.QueueTier).Returns(StageQueueTier.Implementation);

        var goodPipelineMock = new Mock<IWorkflowPipeline>();
        goodPipelineMock.Setup(p => p.GetStage("good-stage")).Returns(goodStageMock.Object);
        _pipelineProviderMock.Setup(p => p.GetPipelineAsync(It.Is<ForgeTask>(t => t.Id == 200), It.IsAny<CancellationToken>()))
            .ReturnsAsync(goodPipelineMock.Object);

        _jobQueueMock.Setup(j => j.EnqueueStage(200, StageQueueTier.Implementation, TaskPriority.High))
            .Returns("job-200");

        _stateStoreMock.Setup(s => s.GetIsPausedAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var service = CreateService();

        var aggEx = await Assert.ThrowsAsync<AggregateException>(() => service.ResumeOperationsAsync());
        Assert.Single(aggEx.InnerExceptions);
        Assert.Contains("Workflow provider error for task 100", aggEx.InnerExceptions[0].Message);

        // Service state is unpaused despite the partial task re-enqueue failure
        Assert.False(service.IsPaused);

        // The successful task was re-enqueued
        _jobQueueMock.Verify(j => j.EnqueueStage(200, StageQueueTier.Implementation, TaskPriority.High), Times.Once);
        var reloaded200 = await _taskRepository.GetByIdAsync(200);
        Assert.NotNull(reloaded200);
        Assert.False(reloaded200.IsDeferredByPause);
        Assert.Equal("job-200", reloaded200.LastHangfireJobId);
    }

    [Fact]
    public async Task ResumeOperationsAsync_WhenAlreadyUnpaused_WithRemainingDeferredTasks_RetriesReconciliationAndReEnqueues()
    {
        var taskDeferred = new ForgeTask
        {
            Id = 300,
            Title = "Task Deferred From Previous Failed Resume",
            Status = TaskOverallStatus.Open,
            InterruptedStageId = "stage-retry",
            IsDeferredByPause = true,
            Priority = TaskPriority.Normal
        };
        await _taskRepository.CreateAsync(taskDeferred);

        _dependencyEngineMock.Setup(d => d.IsReadyForExecutionAsync(300, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var retryStageMock = new Mock<IWorkflowStage>();
        retryStageMock.Setup(s => s.StageId).Returns("stage-retry");
        retryStageMock.Setup(s => s.QueueTier).Returns(StageQueueTier.Implementation);

        var retryPipelineMock = new Mock<IWorkflowPipeline>();
        retryPipelineMock.Setup(p => p.GetStage("stage-retry")).Returns(retryStageMock.Object);
        _pipelineProviderMock.Setup(p => p.GetPipelineAsync(It.Is<ForgeTask>(t => t.Id == 300), It.IsAny<CancellationToken>()))
            .ReturnsAsync(retryPipelineMock.Object);

        _jobQueueMock.Setup(j => j.EnqueueStage(300, StageQueueTier.Implementation, TaskPriority.Normal))
            .Returns("job-300");

        // Service is already unpaused (IsPaused = false)
        _stateStoreMock.Setup(s => s.GetIsPausedAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var service = CreateService();

        Assert.False(service.IsPaused);

        // Calling Resume while already unpaused should retry reconciling remaining deferred tasks
        var result = await service.ResumeOperationsAsync();
        Assert.True(result);

        _jobQueueMock.Verify(j => j.EnqueueStage(300, StageQueueTier.Implementation, TaskPriority.Normal), Times.Once);
        var reloaded = await _taskRepository.GetByIdAsync(300);
        Assert.NotNull(reloaded);
        Assert.False(reloaded.IsDeferredByPause);
        Assert.Equal("job-300", reloaded.LastHangfireJobId);
    }

    [Fact]
    public async Task PauseAndResume_AreSerializedByLock()
    {
        var tcs = new TaskCompletionSource<bool>();
        _gatewayMock.Setup(g => g.ReclaimVramAsync(It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await tcs.Task;
                return true;
            });

        var service = CreateService(new OperationsControlOptions { ReclaimVramOnPause = true });

        // Start pause in background (it will await tcs.Task while holding lock)
        var pauseTask = service.PauseOperationsAsync();

        // While pause is in progress, attempt resume
        var resumeTask = service.ResumeOperationsAsync();

        // Resume should be blocked waiting for lock
        await Task.Delay(50);
        Assert.False(resumeTask.IsCompleted);

        // Unblock pause
        tcs.SetResult(true);

        await Task.WhenAll(pauseTask, resumeTask);

        // Resume executed after pause completed and cleared paused status
        Assert.False(service.IsPaused);
        Assert.Equal(OperationsReclaimStatus.Idle, service.ReclaimStatus);
    }

    [Fact]
    public async Task PauseOperationsAsync_WhenMultipleActiveTasksPresent_RequestsPauseAndCleansContainersForAll()
    {
        var ws = new Workspace { Id = 1, Name = "Ws", RepositoryPath = "C:/ws" };
        var task1 = new ForgeTask { Id = 41, Title = "Active task 1", Status = TaskOverallStatus.Open, Workspace = ws };
        var task2 = new ForgeTask { Id = 42, Title = "Active task 2", Status = TaskOverallStatus.Open, Workspace = ws };
        await _taskRepository.CreateAsync(task1);
        await _taskRepository.CreateAsync(task2);

        _workerStateMock.SetupSequence(w => w.ActiveTaskIds)
            .Returns(new[] { 41, 42 })
            .Returns(Array.Empty<int>())
            .Returns(Array.Empty<int>());
        _workerStateMock.Setup(w => w.ActiveTaskId).Returns((int?)null);
        _gatewayMock.Setup(g => g.ReclaimVramAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var service = CreateService(new OperationsControlOptions { ReclaimVramOnPause = true });

        var result = await service.PauseOperationsAsync();

        Assert.True(result);
        _cancellationManagerMock.Verify(c => c.RequestPause(41), Times.Once);
        _cancellationManagerMock.Verify(c => c.RequestPause(42), Times.Once);
        _environmentProviderMock.Verify(e => e.CleanupTaskContainerAsync(ws, 41, It.IsAny<CancellationToken>()), Times.Once);
        _environmentProviderMock.Verify(e => e.CleanupTaskContainerAsync(ws, 42, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void OperationsControlOptions_DefaultValues_AndOverrides_WorkCorrectly()
    {
        // 1. Default should be false
        var defaultOptions = new OperationsControlOptions();
        Assert.False(defaultOptions.ReclaimVramOnPause);
        Assert.Null(defaultOptions.DisableVramReclaimOnPause);
        Assert.False(defaultOptions.ShouldReclaimVramOnPause);

        // 2. Explicit ReclaimVramOnPause = true
        var enabledOptions = new OperationsControlOptions { ReclaimVramOnPause = true };
        Assert.True(enabledOptions.ReclaimVramOnPause);
        Assert.True(enabledOptions.ShouldReclaimVramOnPause);

        // 3. DisableVramReclaimOnPause = true forces off even if ReclaimVramOnPause was true
        var forceDisabledOptions = new OperationsControlOptions
        {
            ReclaimVramOnPause = true,
            DisableVramReclaimOnPause = true
        };
        Assert.False(forceDisabledOptions.ShouldReclaimVramOnPause);

        // 4. DisableVramReclaimOnPause = false forces on
        var forceEnabledOptions = new OperationsControlOptions
        {
            ReclaimVramOnPause = false,
            DisableVramReclaimOnPause = false
        };
        Assert.True(forceEnabledOptions.ShouldReclaimVramOnPause);

        // 5. Environment variable overrides both ReclaimVramOnPause and DisableVramReclaimOnPause (with trimming)
        var previousEnv = Environment.GetEnvironmentVariable(OperationsControlOptions.ReclaimVramOnPauseEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(OperationsControlOptions.ReclaimVramOnPauseEnvironmentVariable, " true ");
            Assert.True(defaultOptions.ReclaimVramOnPause);
            Assert.True(forceDisabledOptions.ShouldReclaimVramOnPause);

            Environment.SetEnvironmentVariable(OperationsControlOptions.ReclaimVramOnPauseEnvironmentVariable, " 0 ");
            Assert.False(enabledOptions.ReclaimVramOnPause);
            Assert.False(forceEnabledOptions.ShouldReclaimVramOnPause);

            Environment.SetEnvironmentVariable(OperationsControlOptions.ReclaimVramOnPauseEnvironmentVariable, "invalid");
            Assert.False(defaultOptions.ShouldReclaimVramOnPause);
            Assert.True(enabledOptions.ShouldReclaimVramOnPause);
        }
        finally
        {
            Environment.SetEnvironmentVariable(OperationsControlOptions.ReclaimVramOnPauseEnvironmentVariable, previousEnv);
        }
    }

    [Fact]
    public void OperationsControlService_Constructor_WhenPausedAtStartup_SetsReclaimStatusBasedOnOptions()
    {
        _stateStoreMock.Setup(s => s.GetIsPausedAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var defaultService = CreateService(new OperationsControlOptions { ReclaimVramOnPause = false });
        Assert.True(defaultService.IsPaused);
        Assert.Equal(OperationsReclaimStatus.Idle, defaultService.ReclaimStatus);

        var reclaimService = CreateService(new OperationsControlOptions { ReclaimVramOnPause = true });
        Assert.True(reclaimService.IsPaused);
        Assert.Equal(OperationsReclaimStatus.Evicted, reclaimService.ReclaimStatus);
    }
}
