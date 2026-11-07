using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
    private readonly ILlmGatewayService _ollamaRouterGatewayService;
    private readonly IWorkerStateService _workerStateService;
    private readonly ITaskCancellationManager _cancellationManager;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<OperationsControlOptions> _options;
    private readonly ILogger<OperationsControlService> _logger;

    private readonly SemaphoreSlim _operationsLock = new(1, 1);
    private readonly object _stateLock = new();
    private volatile bool _isPaused;
    private OperationsReclaimStatus _reclaimStatus = OperationsReclaimStatus.Idle;
    private string? _lastReclaimError;

    public OperationsControlService(
        IOperationsStateStore stateStore,
        ILlmGatewayService ollamaRouterGatewayService,
        IWorkerStateService workerStateService,
        ITaskCancellationManager cancellationManager,
        IServiceScopeFactory scopeFactory,
        IOptions<OperationsControlOptions> options,
        ILogger<OperationsControlService> logger)
    {
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _ollamaRouterGatewayService = ollamaRouterGatewayService ?? throw new ArgumentNullException(nameof(ollamaRouterGatewayService));
        _workerStateService = workerStateService ?? throw new ArgumentNullException(nameof(workerStateService));
        _cancellationManager = cancellationManager ?? throw new ArgumentNullException(nameof(cancellationManager));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        ArgumentNullException.ThrowIfNull(options);
        _ = options.Value ?? throw new ArgumentNullException(nameof(options));
        _options = options;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Fail fast if database cannot be queried at startup: no synthetic un-paused state
        _isPaused = _stateStore.GetIsPausedAsync().GetAwaiter().GetResult();
        _reclaimStatus = _isPaused && _options.Value.ShouldReclaimVramOnPause ? OperationsReclaimStatus.Evicted : OperationsReclaimStatus.Idle;
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

            // 1. Signal pause to any active tasks BEFORE setting _isPaused to avoid race with inference gating
            var activeTaskIds = GetCurrentActiveTaskIds();
            foreach (var activeTaskId in activeTaskIds)
            {
                _logger.LogInformation("Active Task #{TaskId} detected. Signalling pause request before setting global pause flag...", activeTaskId);
                _cancellationManager.RequestPause(activeTaskId);
            }

            try
            {
                await _stateStore.SetIsPausedAsync(true, cancellationToken);
            }
            catch (Exception ex)
            {
                foreach (var activeTaskId in activeTaskIds)
                {
                    _cancellationManager.ClearPause(activeTaskId);
                }
                _logger.LogError(ex, "Failed persisting IsPaused state to storage during pause.");
                throw new InvalidOperationException("Failed persisting operations pause state to storage.", ex);
            }

            var shouldReclaimVram = _options.Value.ShouldReclaimVramOnPause;
            _isPaused = true;
            ReclaimStatus = shouldReclaimVram ? OperationsReclaimStatus.Evicting : OperationsReclaimStatus.Idle;
            LastReclaimError = null;

            OnOperationsStateChanged?.Invoke(true);

            // 2. Wait for active workers to drain and clean up containers
            if (activeTaskIds.Count > 0)
            {
                _logger.LogInformation("Waiting up to {TimeoutSec}s for {Count} active task worker(s) to release...", DefaultWorkerDrainTimeout.TotalSeconds, activeTaskIds.Count);
                await WaitForWorkerIdleAsync(DefaultWorkerDrainTimeout, cancellationToken);

                foreach (var activeTaskId in activeTaskIds)
                {
                    try
                    {
                        using var scope = _scopeFactory.CreateScope();
                        var taskRepository = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
                        var environmentProvider = scope.ServiceProvider.GetRequiredService<IExecutionEnvironmentProvider>();

                        var task = await taskRepository.GetByIdAsync(activeTaskId, cancellationToken);
                        if (task?.Workspace != null)
                        {
                            await environmentProvider.CleanupTaskContainerAsync(task.Workspace, activeTaskId, cancellationToken);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error while cleaning up task container for task #{TaskId} during pause.", activeTaskId);
                    }
                }
            }

            // 3. Trigger GPU VRAM unload if enabled
            if (shouldReclaimVram)
            {
                ReclaimStatus = OperationsReclaimStatus.Evicting;
                LastReclaimError = null;

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
            }
            else
            {
                _logger.LogInformation("VRAM reclaim on pause is disabled by configuration (ReclaimVramOnPause is false). Skipping GPU VRAM eviction.");
                ReclaimStatus = OperationsReclaimStatus.Idle;
                LastReclaimError = null;
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

    private IReadOnlyList<int> GetCurrentActiveTaskIds()
    {
        var ids = new List<int>();
        if (_workerStateService.ActiveTaskIds is { Count: > 0 } multiIds)
        {
            foreach (var id in multiIds)
            {
                if (id > 0 && !ids.Contains(id))
                {
                    ids.Add(id);
                }
            }
        }

        var singleId = _workerStateService.ActiveTaskId;
        if (singleId is > 0 && !ids.Contains(singleId.Value))
        {
            ids.Add(singleId.Value);
        }

        return ids;
    }

    private async Task WaitForWorkerIdleAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var start = DateTime.UtcNow;
        while (GetCurrentActiveTaskIds().Count > 0 && DateTime.UtcNow - start < timeout)
        {
            try
            {
                await Task.Delay(WorkerDrainPollInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("WaitForWorkerIdleAsync cancelled while waiting for active task(s) to release worker.");
                break;
            }
        }

        var remaining = GetCurrentActiveTaskIds();
        if (remaining.Count > 0)
        {
            _logger.LogWarning("WaitForWorkerIdleAsync timed out after {TimeoutSec}s; active task(s) [{TaskIds}] have not released worker.", timeout.TotalSeconds, string.Join(", ", remaining));
        }
    }
}
