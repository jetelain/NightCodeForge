using Microsoft.Extensions.Options;
using NightCodeForge.Agents.Inference;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Web.Services;

public enum WorkerExecutionStatus
{
    Idle,
    Running,
    WaitingForHuman,
    Blocked
}

public record WorkerStatusSnapshot(
    WorkerExecutionStatus Status,
    int? ActiveTaskId,
    string? ActiveTaskTitle,
    string? StageId,
    string? StageKind,
    string HardwareTarget,
    int PendingQuestionCount,
    string? BranchName = null,
    string? Harness = "Crucible",
    string? Model = "qwen2.5-coder:32b",
    DateTime? StartedAt = null,
    int PendingReviewCount = 0,
    IReadOnlyList<ActiveTaskSummary>? ActiveTasks = null,
    int ConfiguredWorkerCount = 1)
{
    public IReadOnlyList<ActiveTaskSummary> ActiveTasks { get; init; } =
        ActiveTasks ?? (Status == WorkerExecutionStatus.Running && ActiveTaskId.HasValue && AutonomousTaskClassifier.IsAutonomousTask(ActiveTaskId, StageId)
            ? new[]
            {
                new ActiveTaskSummary(
                    ActiveTaskId.Value,
                    ActiveTaskTitle ?? $"Task #{ActiveTaskId.Value}",
                    StageId ?? "Executing",
                    1,
                    "default",
                    StartedAt ?? DateTime.UtcNow,
                    HardwareTarget)
            }
            : Array.Empty<ActiveTaskSummary>());
    public int ActiveTaskCount => ActiveTasks.Count;
    public int ConfiguredWorkerCount { get; init; } = ConfiguredWorkerCount <= 0 ? 1 : Math.Clamp(ConfiguredWorkerCount, 1, 16);
}

/// <summary>
/// Singleton service providing real-time snapshot and event dispatch for worker execution state and questions.
/// </summary>
public class WorkerStateService : IWorkerStateService, IDisposable
{
    private readonly IOperationsTelemetryService _telemetryService;
    private readonly IOptions<LlmProviderOptions> _llmOptions;
    private readonly object _lock = new();
    private readonly Dictionary<int, string?> _lastTelemetryStageByTask = new();
    private WorkerStatusSnapshot _current;
    private int? _lastIdleTaskId;

    public int? ActiveTaskId => Current.ActiveTaskId;
    public IReadOnlyList<int> ActiveTaskIds => Current.ActiveTasks.Select(t => t.TaskId).ToList();
    public IReadOnlyList<ActiveTaskSummary> ActiveTasks => Current.ActiveTasks;
    public int ActiveTaskCount => Current.ActiveTaskCount;
    public int ConfiguredWorkerCount => Math.Clamp(_llmOptions.Value?.MaxConcurrentInferenceTasks ?? 1, 1, 16);
    public bool IsRunning => Current.Status == WorkerExecutionStatus.Running;
    public ModelPreloadTelemetryRecord? LatestModelPreload => _telemetryService.GetLatestModelPreload();

    public WorkerStateService(
        IOperationsTelemetryService telemetryService,
        IOptions<LlmProviderOptions> llmOptions)
    {
        _telemetryService = telemetryService ?? throw new ArgumentNullException(nameof(telemetryService));
        _llmOptions = llmOptions ?? throw new ArgumentNullException(nameof(llmOptions));
        _telemetryService.OnActiveTaskChanged += HandleTelemetryActiveTaskChanged;
        _telemetryService.OnActiveTasksChanged += HandleTelemetryActiveTasksChanged;
        _telemetryService.OnPendingCountsChanged += HandleTelemetryPendingCountsChanged;
        _telemetryService.OnModelPreloadChanged += HandleTelemetryModelPreloadChanged;

        var configuredWorkers = ConfiguredWorkerCount;
        var activeTasks = GetTelemetryActiveTasks();
        foreach (var t in activeTasks)
        {
            _lastTelemetryStageByTask[t.TaskId] = t.CurrentStageId;
        }

        if (activeTasks.Count > 0)
        {
            var active = activeTasks[^1];
            _current = new WorkerStatusSnapshot(
                WorkerExecutionStatus.Running,
                active.TaskId,
                active.TaskTitle,
                active.CurrentStageId,
                null,
                string.IsNullOrWhiteSpace(active.HardwareTarget) ? "Local GPU" : active.HardwareTarget,
                0,
                StartedAt: active.StartedAt,
                ActiveTasks: activeTasks,
                ConfiguredWorkerCount: configuredWorkers);
        }
        else
        {
            _current = new WorkerStatusSnapshot(
                WorkerExecutionStatus.Idle,
                null,
                null,
                null,
                null,
                "Local GPU",
                0,
                ActiveTasks: Array.Empty<ActiveTaskSummary>(),
                ConfiguredWorkerCount: configuredWorkers);
        }
    }

    private List<ActiveTaskSummary> GetTelemetryActiveTasks()
    {
        var activeTasks = (_telemetryService.GetActiveTasks() ?? Array.Empty<ActiveTaskSummary>())
            .Where(AutonomousTaskClassifier.IsAutonomousTask)
            .OrderBy(t => t.StartedAt)
            .ToList();

        if (activeTasks.Count == 0)
        {
            var single = _telemetryService.GetActiveTask();
            if (AutonomousTaskClassifier.IsAutonomousTask(single))
            {
                activeTasks.Add(single!);
            }
        }

        return activeTasks;
    }

    public static string FormatRunningStatusLabel(WorkerStatusSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.ConfiguredWorkerCount > 1 || snapshot.ActiveTasks.Count > 1)
        {
            return $"RUNNING ({snapshot.ActiveTasks.Count} / {snapshot.ConfiguredWorkerCount})";
        }
        return "RUNNING";
    }

    public static string FormatWorkerUtilizationLabel(WorkerStatusSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return $"{snapshot.ActiveTasks.Count} / {snapshot.ConfiguredWorkerCount} Workers Active";
    }

    private void HandleTelemetryActiveTasksChanged(IReadOnlyList<ActiveTaskSummary> activeTasks)
    {
        lock (_lock)
        {
            var validTasks = (activeTasks ?? Array.Empty<ActiveTaskSummary>())
                .Where(AutonomousTaskClassifier.IsAutonomousTask)
                .OrderBy(t => t.StartedAt)
                .ToList();

            if (validTasks.Count > 0)
            {
                var latest = validTasks[^1];
                _lastIdleTaskId = null;
                _current = _current with
                {
                    Status = WorkerExecutionStatus.Running,
                    ActiveTaskId = latest.TaskId,
                    ActiveTaskTitle = latest.TaskTitle,
                    StageId = latest.CurrentStageId,
                    HardwareTarget = string.IsNullOrWhiteSpace(latest.HardwareTarget) ? _current.HardwareTarget : latest.HardwareTarget,
                    StartedAt = latest.StartedAt,
                    ActiveTasks = validTasks,
                    ConfiguredWorkerCount = ConfiguredWorkerCount
                };
            }
            else
            {
                if (_current.Status == WorkerExecutionStatus.Running)
                {
                    _current = _current with
                    {
                        Status = WorkerExecutionStatus.Idle,
                        ActiveTaskId = null,
                        ActiveTaskTitle = null,
                        StageId = null,
                        StartedAt = null,
                        ActiveTasks = Array.Empty<ActiveTaskSummary>(),
                        ConfiguredWorkerCount = ConfiguredWorkerCount
                    };
                }
                else
                {
                    _current = _current with
                    {
                        ActiveTasks = Array.Empty<ActiveTaskSummary>(),
                        ConfiguredWorkerCount = ConfiguredWorkerCount
                    };
                }
            }
        }
        OnWorkerStateChanged?.Invoke();
    }

    private void HandleTelemetryActiveTaskChanged(NightCodeForge.Core.Workflow.ActiveTaskSummary? active)
    {
        lock (_lock)
        {
            var tasks = GetTelemetryActiveTasks();

            if (active != null)
            {
                if (!AutonomousTaskClassifier.IsAutonomousTask(active))
                {
                    return;
                }
                _lastIdleTaskId = null;
                var existingIdx = tasks.FindIndex(t => t.TaskId == active.TaskId);
                if (existingIdx >= 0)
                {
                    tasks[existingIdx] = active;
                }
                else
                {
                    tasks.Add(active);
                }
                tasks = tasks.OrderBy(t => t.StartedAt).ToList();
                _current = _current with
                {
                    Status = WorkerExecutionStatus.Running,
                    ActiveTaskId = active.TaskId,
                    ActiveTaskTitle = active.TaskTitle,
                    StageId = active.CurrentStageId,
                    HardwareTarget = string.IsNullOrWhiteSpace(active.HardwareTarget) ? _current.HardwareTarget : active.HardwareTarget,
                    StartedAt = active.StartedAt,
                    ActiveTasks = tasks,
                    ConfiguredWorkerCount = ConfiguredWorkerCount
                };
            }
            else
            {
                if (tasks.Count > 0)
                {
                    var latest = tasks[^1];
                    _current = _current with
                    {
                        Status = WorkerExecutionStatus.Running,
                        ActiveTaskId = latest.TaskId,
                        ActiveTaskTitle = latest.TaskTitle,
                        StageId = latest.CurrentStageId,
                        HardwareTarget = string.IsNullOrWhiteSpace(latest.HardwareTarget) ? _current.HardwareTarget : latest.HardwareTarget,
                        StartedAt = latest.StartedAt,
                        ActiveTasks = tasks,
                        ConfiguredWorkerCount = ConfiguredWorkerCount
                    };
                }
                else if (_current.Status == WorkerExecutionStatus.Running)
                {
                    _current = _current with
                    {
                        Status = WorkerExecutionStatus.Idle,
                        ActiveTaskId = null,
                        ActiveTaskTitle = null,
                        StageId = null,
                        StartedAt = null,
                        ActiveTasks = Array.Empty<ActiveTaskSummary>(),
                        ConfiguredWorkerCount = ConfiguredWorkerCount
                    };
                }
            }
        }
        OnWorkerStateChanged?.Invoke();
    }

    public WorkerStatusSnapshot Current
    {
        get
        {
            lock (_lock)
            {
                var activeTasks = GetTelemetryActiveTasks();

                if (_current.ActiveTaskId == null)
                {
                    var nonStaleTasks = _lastIdleTaskId.HasValue
                        ? activeTasks.Where(t => t.TaskId != _lastIdleTaskId.Value).ToList()
                        : activeTasks;

                    if (nonStaleTasks.Count > 0)
                    {
                        var active = nonStaleTasks[^1];
                        _current = _current with
                        {
                            Status = WorkerExecutionStatus.Running,
                            ActiveTaskId = active.TaskId,
                            ActiveTaskTitle = active.TaskTitle,
                            StageId = active.CurrentStageId,
                            HardwareTarget = string.IsNullOrWhiteSpace(active.HardwareTarget) ? _current.HardwareTarget : active.HardwareTarget,
                            StartedAt = active.StartedAt,
                            ActiveTasks = nonStaleTasks,
                            ConfiguredWorkerCount = ConfiguredWorkerCount
                        };
                    }
                    else if (_current.ConfiguredWorkerCount != ConfiguredWorkerCount)
                    {
                        _current = _current with { ConfiguredWorkerCount = ConfiguredWorkerCount };
                    }
                }
                else if (activeTasks.Count > 0)
                {
                    for (int i = 0; i < activeTasks.Count; i++)
                    {
                        var teleTask = activeTasks[i];
                        var teleStageChanged = !_lastTelemetryStageByTask.TryGetValue(teleTask.TaskId, out var prevTeleStage) ||
                            !string.Equals(teleTask.CurrentStageId, prevTeleStage, StringComparison.OrdinalIgnoreCase);
                        _lastTelemetryStageByTask[teleTask.TaskId] = teleTask.CurrentStageId;

                        if (!teleStageChanged)
                        {
                            var currentMatch = _current.ActiveTasks.FirstOrDefault(t => t.TaskId == teleTask.TaskId);
                            if (currentMatch != null)
                            {
                                activeTasks[i] = currentMatch;
                            }
                        }
                    }

                    var active = activeTasks.FirstOrDefault(t => t.TaskId == _current.ActiveTaskId) ?? activeTasks[^1];
                    if (active.TaskId != _current.ActiveTaskId)
                    {
                        _current = _current with
                        {
                            Status = WorkerExecutionStatus.Running,
                            ActiveTaskId = active.TaskId,
                            ActiveTaskTitle = active.TaskTitle,
                            StageId = active.CurrentStageId,
                            HardwareTarget = string.IsNullOrWhiteSpace(active.HardwareTarget) ? _current.HardwareTarget : active.HardwareTarget,
                            StartedAt = active.StartedAt,
                            ActiveTasks = activeTasks,
                            ConfiguredWorkerCount = ConfiguredWorkerCount
                        };
                    }
                    else if (!string.Equals(active.CurrentStageId, _current.StageId, StringComparison.OrdinalIgnoreCase))
                    {
                        _current = _current with
                        {
                            StageId = active.CurrentStageId,
                            HardwareTarget = string.IsNullOrWhiteSpace(active.HardwareTarget) ? _current.HardwareTarget : active.HardwareTarget,
                            ActiveTasks = activeTasks,
                            ConfiguredWorkerCount = ConfiguredWorkerCount
                        };
                    }
                    else
                    {
                        _current = _current with
                        {
                            ActiveTasks = activeTasks,
                            ConfiguredWorkerCount = ConfiguredWorkerCount
                        };
                    }
                }
                else
                {
                    var fallbackTasks = _current.Status == WorkerExecutionStatus.Running && _current.ActiveTasks.Count > 0
                        ? _current.ActiveTasks
                        : Array.Empty<ActiveTaskSummary>();
                    _current = _current with
                    {
                        ActiveTasks = fallbackTasks,
                        ConfiguredWorkerCount = ConfiguredWorkerCount
                    };
                }
                return _current;
            }
        }
    }

    public event Action? OnWorkerStateChanged;

    public void UpdateStatus(WorkerStatusSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_lock)
        {
            _current = snapshot;
            if (snapshot.Status == WorkerExecutionStatus.Idle)
            {
                _lastIdleTaskId = snapshot.ActiveTaskId ?? _telemetryService.GetActiveTask()?.TaskId;
            }
            else if (snapshot.Status == WorkerExecutionStatus.Running)
            {
                _lastIdleTaskId = null;
            }
        }
        OnWorkerStateChanged?.Invoke();
    }

    /// <summary>
    /// Re-synchronizes the snapshot from live telemetry and notifies subscribers.
    /// Used when a mobile browser tab returns to the foreground.
    /// </summary>
    public void RefreshFromTelemetry()
    {
        lock (_lock)
        {
            var activeTasks = GetTelemetryActiveTasks();

            if (activeTasks.Count == 0 && _current.Status == WorkerExecutionStatus.Running)
            {
                _current = _current with
                {
                    Status = WorkerExecutionStatus.Idle,
                    ActiveTaskId = null,
                    ActiveTaskTitle = null,
                    StageId = null,
                    StartedAt = null,
                    ActiveTasks = Array.Empty<ActiveTaskSummary>(),
                    ConfiguredWorkerCount = ConfiguredWorkerCount
                };
            }
            else if (activeTasks.Count > 0)
            {
                var active = activeTasks[^1];
                _current = _current with
                {
                    Status = WorkerExecutionStatus.Running,
                    ActiveTaskId = active.TaskId,
                    ActiveTaskTitle = active.TaskTitle,
                    StageId = active.CurrentStageId,
                    HardwareTarget = string.IsNullOrWhiteSpace(active.HardwareTarget) ? _current.HardwareTarget : active.HardwareTarget,
                    StartedAt = active.StartedAt,
                    ActiveTasks = activeTasks,
                    ConfiguredWorkerCount = ConfiguredWorkerCount
                };
            }
        }
        _ = Current;
        OnWorkerStateChanged?.Invoke();
    }

    public void UpdatePendingQuestions(int count)
    {
        lock (_lock)
        {
            if (_current.PendingQuestionCount == count)
            {
                return;
            }

            _current = _current with { PendingQuestionCount = count };
        }
        OnWorkerStateChanged?.Invoke();
    }

    public void UpdatePendingReviews(int count)
    {
        lock (_lock)
        {
            if (_current.PendingReviewCount == count)
            {
                return;
            }

            _current = _current with { PendingReviewCount = count };
        }
        OnWorkerStateChanged?.Invoke();
    }

    public void UpdatePendingCounts(int pendingQuestionCount, int pendingReviewCount)
    {
        bool changed = false;
        lock (_lock)
        {
            if (_current.PendingQuestionCount != pendingQuestionCount || _current.PendingReviewCount != pendingReviewCount)
            {
                _current = _current with
                {
                    PendingQuestionCount = pendingQuestionCount,
                    PendingReviewCount = pendingReviewCount
                };
                changed = true;
            }
        }
        if (changed)
        {
            OnWorkerStateChanged?.Invoke();
        }
    }

    public async Task RefreshCountsFromRepositoryAsync(ITaskRepository taskRepository, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(taskRepository);
        var (qCount, rCount) = await TaskBadgeCounter.ComputeCountsAsync(taskRepository, cancellationToken);
        UpdatePendingCounts(qCount, rCount);
    }

    public void HandleWorkerStatus(string status, int? activeTaskId, string? stageId, string hardwareTarget, string? taskTitle = null, string? branchName = null)
    {
        if (activeTaskId.HasValue && !AutonomousTaskClassifier.IsAutonomousTask(activeTaskId, stageId))
        {
            return;
        }

        var incomingStatus = WorkerStatusConstants.IsRunning(status)
            ? WorkerExecutionStatus.Running
            : WorkerStatusConstants.IsWaitingForHuman(status)
                ? WorkerExecutionStatus.WaitingForHuman
                : WorkerStatusConstants.IsBlocked(status)
                    ? WorkerExecutionStatus.Blocked
                    : WorkerExecutionStatus.Idle;

        lock (_lock)
        {
            var telemetryTasks = GetTelemetryActiveTasks();

            if (incomingStatus == WorkerExecutionStatus.Running)
            {
                _lastIdleTaskId = null;

                ActiveTaskSummary? matchedExisting = null;
                if (activeTaskId.HasValue)
                {
                    var existingIdx = telemetryTasks.FindIndex(t => t.TaskId == activeTaskId.Value);
                    if (existingIdx >= 0)
                    {
                        matchedExisting = telemetryTasks[existingIdx];
                        _lastTelemetryStageByTask[activeTaskId.Value] = matchedExisting.CurrentStageId;
                    }
                    var taskSummary = new ActiveTaskSummary(
                        activeTaskId.Value,
                        taskTitle ?? (matchedExisting != null ? matchedExisting.TaskTitle : $"Task #{activeTaskId.Value}"),
                        stageId ?? (matchedExisting != null ? matchedExisting.CurrentStageId : "Executing"),
                        matchedExisting?.Priority ?? 1,
                        matchedExisting?.QueueType ?? "default",
                        matchedExisting?.StartedAt ?? DateTime.UtcNow,
                        string.IsNullOrWhiteSpace(hardwareTarget) ? (matchedExisting?.HardwareTarget ?? _current.HardwareTarget) : hardwareTarget);

                    if (existingIdx >= 0)
                    {
                        telemetryTasks[existingIdx] = taskSummary;
                    }
                    else
                    {
                        telemetryTasks.Add(taskSummary);
                    }
                }

                var taskChanged = activeTaskId.HasValue && activeTaskId != _current.ActiveTaskId;
                var startedAt = matchedExisting?.StartedAt ?? (taskChanged ? DateTime.UtcNow : (_current.StartedAt ?? DateTime.UtcNow));
                var keepPrevious = !taskChanged;

                _current = _current with
                {
                    Status = WorkerExecutionStatus.Running,
                    ActiveTaskId = activeTaskId ?? _current.ActiveTaskId,
                    ActiveTaskTitle = taskTitle ?? (keepPrevious ? _current.ActiveTaskTitle : matchedExisting?.TaskTitle),
                    StageId = stageId ?? (keepPrevious ? _current.StageId : matchedExisting?.CurrentStageId),
                    HardwareTarget = string.IsNullOrWhiteSpace(hardwareTarget) ? _current.HardwareTarget : hardwareTarget,
                    BranchName = branchName ?? (keepPrevious ? _current.BranchName : null),
                    StartedAt = startedAt,
                    ActiveTasks = telemetryTasks.OrderBy(t => t.StartedAt).ToList(),
                    ConfiguredWorkerCount = ConfiguredWorkerCount
                };
            }
            else
            {
                if (activeTaskId.HasValue)
                {
                    telemetryTasks.RemoveAll(t => t.TaskId == activeTaskId.Value);
                }
                else
                {
                    telemetryTasks.Clear();
                }

                if (telemetryTasks.Count > 0)
                {
                    var nextActive = telemetryTasks[^1];
                    _current = _current with
                    {
                        Status = WorkerExecutionStatus.Running,
                        ActiveTaskId = nextActive.TaskId,
                        ActiveTaskTitle = nextActive.TaskTitle,
                        StageId = nextActive.CurrentStageId,
                        HardwareTarget = string.IsNullOrWhiteSpace(nextActive.HardwareTarget) ? _current.HardwareTarget : nextActive.HardwareTarget,
                        StartedAt = nextActive.StartedAt,
                        ActiveTasks = telemetryTasks.OrderBy(t => t.StartedAt).ToList(),
                        ConfiguredWorkerCount = ConfiguredWorkerCount
                    };
                }
                else
                {
                    if (incomingStatus == WorkerExecutionStatus.Idle)
                    {
                        _lastIdleTaskId = activeTaskId ?? _current.ActiveTaskId ?? _telemetryService.GetActiveTask()?.TaskId;
                    }

                    _current = _current with
                    {
                        Status = incomingStatus,
                        ActiveTaskId = incomingStatus == WorkerExecutionStatus.Idle ? null : activeTaskId,
                        ActiveTaskTitle = incomingStatus == WorkerExecutionStatus.Idle ? null : taskTitle,
                        StageId = incomingStatus == WorkerExecutionStatus.Idle ? null : stageId,
                        HardwareTarget = string.IsNullOrWhiteSpace(hardwareTarget) ? _current.HardwareTarget : hardwareTarget,
                        BranchName = incomingStatus == WorkerExecutionStatus.Idle ? null : branchName,
                        StartedAt = null,
                        ActiveTasks = Array.Empty<ActiveTaskSummary>(),
                        ConfiguredWorkerCount = ConfiguredWorkerCount
                    };
                }
            }
        }

        OnWorkerStateChanged?.Invoke();
    }

    private void HandleTelemetryPendingCountsChanged(int questionCount, int reviewCount)
    {
        UpdatePendingCounts(questionCount, reviewCount);
    }

    private void HandleTelemetryModelPreloadChanged(ModelPreloadTelemetryRecord? record)
    {
        OnWorkerStateChanged?.Invoke();
    }

    public void Dispose()
    {
        _telemetryService.OnActiveTaskChanged -= HandleTelemetryActiveTaskChanged;
        _telemetryService.OnActiveTasksChanged -= HandleTelemetryActiveTasksChanged;
        _telemetryService.OnPendingCountsChanged -= HandleTelemetryPendingCountsChanged;
        _telemetryService.OnModelPreloadChanged -= HandleTelemetryModelPreloadChanged;
    }
}
