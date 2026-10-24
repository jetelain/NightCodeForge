using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Core.Services;

/// <summary>
/// Singleton service managing global execution pause, GPU VRAM eviction, and resumption reconciliation.
/// </summary>
public class OperationsControlService : IOperationsControlService
{
    public static readonly TimeSpan DefaultWorkerDrainTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan WorkerDrainPollInterval = TimeSpan.FromMilliseconds(100);
    public static readonly TimeSpan DefaultCancellationTimeout = TimeSpan.FromSeconds(5);

    private readonly IOperationsStateStore _stateStore;
    private readonly IOllamaRouterGatewayService _ollamaRouterGatewayService;
    private readonly IWorkerStateService _workerStateService;
    private readonly ITaskCancellationManager _cancellationManager;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OperationsControlService> _logger;

    private readonly SemaphoreSlim _operationsLock = new(1, 1);
    private readonly object _stateLock = new();
    private volatile bool _isPaused;
    private OperationsReclaimStatus _reclaimStatus = OperationsReclaimStatus.Idle;
    private string? _lastReclaimError;

    public OperationsControlService(
        IOperationsStateStore stateStore,
        IOllamaRouterGatewayService ollamaRouterGatewayService,
        IWorkerStateService workerStateService,
        ITaskCancellationManager cancellationManager,
        IServiceScopeFactory scopeFactory,
        ILogger<OperationsControlService> logger)
    {
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _ollamaRouterGatewayService = ollamaRouterGatewayService ?? throw new ArgumentNullException(nameof(ollamaRouterGatewayService));
        _workerStateService = workerStateService ?? throw new ArgumentNullException(nameof(workerStateService));
        _cancellationManager = cancellationManager ?? throw new ArgumentNullException(nameof(cancellationManager));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Fail fast if database cannot be queried at startup: no synthetic un-paused state
        _isPaused = _stateStore.GetIsPausedAsync().GetAwaiter().GetResult();
        _reclaimStatus = _isPaused ? OperationsReclaimStatus.Evicted : OperationsReclaimStatus.Idle;
    }

    public bool IsPaused => _isPaused;

    public OperationsReclaimStatus ReclaimStatus
    {
        get
        {
            lock (_stateLock)
            {
                return _reclaimStatus;
            }
        }
        private set
        {
            lock (_stateLock)
            {
                _reclaimStatus = value;
            }
        }
    }

    public string? LastReclaimError
    {
        get
        {
            lock (_stateLock)
            {
                return _lastReclaimError;
            }
        }
        private set
        {
            lock (_stateLock)
            {
                _lastReclaimError = value;
            }
        }
    }

    public event Action<bool>? OnOperationsStateChanged;

    public async Task<bool> PauseOperationsAsync(
        string reason = "User initiated pause (Gamer / Dedicated Workload Mode)",
        CancellationToken cancellationToken = default)
    {
        await _operationsLock.WaitAsync(cancellationToken);
        try
        {
            if (_isPaused)
            {
                _logger.LogInformation("Global operations are already paused.");
                return true;
            }

            _logger.LogInformation("Initiating global operations pause: {Reason}", reason);

            // 1. Signal pause to any active task BEFORE setting _isPaused to avoid race with inference gating
            var activeTaskId = _workerStateService.ActiveTaskId;
            if (activeTaskId.HasValue)
            {
                _logger.LogInformation("Active Task #{TaskId} detected. Signalling pause request before setting global pause flag...", activeTaskId.Value);
                _cancellationManager.RequestPause(activeTaskId.Value);
            }

            try
            {
                await _stateStore.SetIsPausedAsync(true, cancellationToken);
            }
            catch (Exception ex)
            {
                if (activeTaskId.HasValue)
                {
                    _cancellationManager.ClearPause(activeTaskId.Value);
                }
                _logger.LogError(ex, "Failed persisting IsPaused state to storage during pause.");
                throw new InvalidOperationException("Failed persisting operations pause state to storage.", ex);
            }

            _isPaused = true;
            ReclaimStatus = OperationsReclaimStatus.Evicting;
            LastReclaimError = null;

            OnOperationsStateChanged?.Invoke(true);

            // 2. Wait for active worker to drain and clean up container
            if (activeTaskId.HasValue)
            {
                _logger.LogInformation("Waiting up to {TimeoutSec}s for active Task #{TaskId} worker release...", DefaultWorkerDrainTimeout.TotalSeconds, activeTaskId.Value);
                await WaitForWorkerIdleAsync(DefaultWorkerDrainTimeout, cancellationToken);

                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var taskRepository = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
                    var environmentProvider = scope.ServiceProvider.GetRequiredService<IExecutionEnvironmentProvider>();

                    var task = await taskRepository.GetByIdAsync(activeTaskId.Value, cancellationToken);
                    if (task?.Workspace != null)
                    {
                        await environmentProvider.CleanupTaskContainerAsync(task.Workspace, activeTaskId.Value, cancellationToken);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error while cleaning up task container for task #{TaskId} during pause.", activeTaskId.Value);
                }
            }

            // 3. Trigger GPU VRAM unload
            try
            {
                _logger.LogInformation("Triggering GPU VRAM reclamation...");
                var success = await _ollamaRouterGatewayService.ReclaimVramAsync(cancellationToken);
                if (success)
                {
                    ReclaimStatus = OperationsReclaimStatus.Evicted;
                    LastReclaimError = null;
                    _logger.LogInformation("GPU VRAM successfully reclaimed.");
                }
                else
                {
                    ReclaimStatus = OperationsReclaimStatus.Failed;
                    LastReclaimError = "OllamaRouter reported VRAM reclaim failure or endpoint unavailable.";
                    _logger.LogWarning("GPU VRAM reclaim failed: {Error}", LastReclaimError);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to reclaim GPU VRAM during operations pause.");
                ReclaimStatus = OperationsReclaimStatus.Failed;
                LastReclaimError = ex.Message;
            }

            OnOperationsStateChanged?.Invoke(true);
            return true;
        }
        finally
        {
            _operationsLock.Release();
        }
    }

    public async Task<bool> ResumeOperationsAsync(CancellationToken cancellationToken = default)
    {
        await _operationsLock.WaitAsync(cancellationToken);
        try
        {
            if (!_isPaused)
            {
                using var scope = _scopeFactory.CreateScope();
                var taskRepo = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
                var openTasks = await taskRepo.GetByStatusAsync(TaskOverallStatus.Open, cancellationToken);
                var deferredCandidates = openTasks
                    .Where(t => !t.IsWaitingForHuman && t.IsDeferredByPause)
                    .ToList();

                if (deferredCandidates.Count == 0)
                {
                    _logger.LogInformation("Global operations are already active (not paused) and no deferred tasks remain.");
                    return true;
                }

                _logger.LogInformation(
                    "Global operations are active, but {Count} deferred task(s) remain; proceeding with reconciliation retry.",
                    deferredCandidates.Count);
            }
            else
            {
                _logger.LogInformation("Resuming global operations...");

                try
                {
                    await _stateStore.SetIsPausedAsync(false, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed persisting IsPaused state to storage during resume.");
                    throw new InvalidOperationException("Failed persisting operations resume state to storage.", ex);
                }

                _isPaused = false;
                ReclaimStatus = OperationsReclaimStatus.Idle;
                LastReclaimError = null;

                // Clear all tracked pause states across all tasks
                _cancellationManager.ClearAllPauses();
            }

            // Reconcile and re-enqueue open tasks with DAG validation
            var taskErrors = new List<Exception>();
            int candidateCount = 0;

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var taskRepository = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
                var dependencyEngine = scope.ServiceProvider.GetRequiredService<IDependencyResolutionEngine>();
                var pipelineProvider = scope.ServiceProvider.GetRequiredService<IWorkflowPipelineProvider>();
                var jobQueue = scope.ServiceProvider.GetRequiredService<ITaskJobQueue>();

                var openTasks = await taskRepository.GetByStatusAsync(TaskOverallStatus.Open, cancellationToken);
                var candidates = openTasks
                    .Where(t => !t.IsWaitingForHuman && t.IsDeferredByPause)
                    .ToList();
                candidateCount = candidates.Count;

                foreach (var task in candidates)
                {
                    try
                    {
                        var isReady = await dependencyEngine.IsReadyForExecutionAsync(task.Id, cancellationToken);
                        if (!isReady)
                        {
                            _logger.LogInformation("Task #{TaskId} is not ready for execution on resume (prerequisites pending); clearing pause deferral flag and skipping Hangfire dispatch.", task.Id);
                            task.IsDeferredByPause = false;
                            await taskRepository.UpdateAsync(task, cancellationToken: cancellationToken);
                            continue;
                        }

                        var pipeline = await pipelineProvider.GetPipelineAsync(task, cancellationToken);
                        var targetStageId = task.InterruptedStageId ?? task.CurrentStageId;
                        IWorkflowStage? targetStage = null;
                        if (!string.IsNullOrWhiteSpace(targetStageId))
                        {
                            targetStage = pipeline.GetStage(targetStageId);
                        }
                        targetStage ??= pipeline.InitialStage;

                        if (targetStage != null)
                        {
                            task.CurrentStageId = targetStage.StageId;
                            task.IsDeferredByPause = false;
                            // Note: Keep task.InterruptedStageId intact so TaskOrchestrator detects isResumedRun,
                            // preserves uncommitted disk edits, and resets the conflict budget. TaskOrchestrator clears it on entry.
                            var jobId = jobQueue.EnqueueStage(task.Id, targetStage.QueueTier, task.Priority);
                            task.LastHangfireJobId = jobId;
                            await taskRepository.UpdateAsync(task, cancellationToken: cancellationToken);
                            _logger.LogInformation("Re-enqueued Task #{TaskId} to stage queue {Tier} (JobId: {JobId}) on resume at stage '{StageId}'.", task.Id, targetStage.QueueTier, jobId, targetStage.StageId);
                        }
                    }
                    catch (Exception taskEx)
                    {
                        _logger.LogError(taskEx, "Failed to re-enqueue Task #{TaskId} during operations resume.", task.Id);
                        taskErrors.Add(taskEx);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reconciling open tasks during operations resume.");
                taskErrors.Add(ex);
            }

            OnOperationsStateChanged?.Invoke(false);

            if (taskErrors.Count > 0)
            {
                throw new AggregateException(
                    $"Failed to re-enqueue {taskErrors.Count} candidate task(s) (out of {candidateCount}) during operations resume reconciliation.",
                    taskErrors);
            }

            return true;
        }
        finally
        {
            _operationsLock.Release();
        }
    }

    public async Task<int> GetDeferredTaskCountAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var taskRepository = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
        var openTasks = await taskRepository.GetByStatusAsync(TaskOverallStatus.Open, cancellationToken);
        return openTasks.Count(t => !t.IsWaitingForHuman && t.IsDeferredByPause);
    }

    private async Task WaitForWorkerIdleAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var start = DateTime.UtcNow;
        while (_workerStateService.ActiveTaskId.HasValue && DateTime.UtcNow - start < timeout)
        {
            try
            {
                await Task.Delay(WorkerDrainPollInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("WaitForWorkerIdleAsync cancelled while waiting for active task #{TaskId} to release worker.", _workerStateService.ActiveTaskId);
                break;
            }
        }

        if (_workerStateService.ActiveTaskId.HasValue)
        {
            _logger.LogWarning("WaitForWorkerIdleAsync timed out after {TimeoutSec}s; active task #{TaskId} has not released worker.", timeout.TotalSeconds, _workerStateService.ActiveTaskId);
        }
    }
}
