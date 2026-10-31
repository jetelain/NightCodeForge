using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
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
    DateTime? StartedAt = null);

/// <summary>
/// Singleton service providing real-time snapshot and event dispatch for worker execution state and questions.
/// </summary>
public class WorkerStateService : IWorkerStateService, IDisposable
{
    private readonly IOperationsTelemetryService _telemetryService;
    private readonly object _lock = new();
    private WorkerStatusSnapshot _current;
    private int? _lastIdleTaskId;

    public int? ActiveTaskId => Current.ActiveTaskId;
    public bool IsRunning => Current.Status == WorkerExecutionStatus.Running;

    public WorkerStateService(IOperationsTelemetryService telemetryService)
    {
        _telemetryService = telemetryService ?? throw new ArgumentNullException(nameof(telemetryService));
        _telemetryService.OnActiveTaskChanged += HandleTelemetryActiveTaskChanged;

        var active = _telemetryService.GetActiveTask();
        if (AutonomousTaskClassifier.IsAutonomousTask(active))
        {
            _current = new WorkerStatusSnapshot(
                WorkerExecutionStatus.Running,
                active!.TaskId,
                active.TaskTitle,
                active.CurrentStageId,
                null,
                string.IsNullOrWhiteSpace(active.HardwareTarget) ? "Local GPU" : active.HardwareTarget,
                0,
                StartedAt: active.StartedAt);
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
                0);
        }
    }

    private void HandleTelemetryActiveTaskChanged(NightCodeForge.Core.Workflow.ActiveTaskSummary? active)
    {
        lock (_lock)
        {
            if (active != null)
            {
                if (!AutonomousTaskClassifier.IsAutonomousTask(active))
                {
                    return;
                }
                _lastIdleTaskId = null;
                _current = _current with
                {
                    Status = WorkerExecutionStatus.Running,
                    ActiveTaskId = active.TaskId,
                    ActiveTaskTitle = active.TaskTitle,
                    StageId = active.CurrentStageId,
                    HardwareTarget = string.IsNullOrWhiteSpace(active.HardwareTarget) ? _current.HardwareTarget : active.HardwareTarget,
                    StartedAt = active.StartedAt
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
                        StartedAt = null
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
                var active = _telemetryService.GetActiveTask();
                if (!AutonomousTaskClassifier.IsAutonomousTask(active))
                {
                    active = null;
                }
                if (_current.ActiveTaskId == null)
                {
                    if (active != null && active.TaskId != _lastIdleTaskId)
                    {
                        _current = _current with
                        {
                            Status = WorkerExecutionStatus.Running,
                            ActiveTaskId = active.TaskId,
                            ActiveTaskTitle = active.TaskTitle,
                            StageId = active.CurrentStageId,
                            HardwareTarget = string.IsNullOrWhiteSpace(active.HardwareTarget) ? _current.HardwareTarget : active.HardwareTarget,
                            StartedAt = active.StartedAt
                        };
                    }
                }
                else if (active != null && active.TaskId != _current.ActiveTaskId)
                {
                    _current = _current with
                    {
                        Status = WorkerExecutionStatus.Running,
                        ActiveTaskId = active.TaskId,
                        ActiveTaskTitle = active.TaskTitle,
                        StageId = active.CurrentStageId,
                        HardwareTarget = string.IsNullOrWhiteSpace(active.HardwareTarget) ? _current.HardwareTarget : active.HardwareTarget,
                        StartedAt = active.StartedAt
                    };
                }
                else if (active != null && active.TaskId == _current.ActiveTaskId && !string.Equals(active.CurrentStageId, _current.StageId, StringComparison.OrdinalIgnoreCase))
                {
                    _current = _current with
                    {
                        StageId = active.CurrentStageId,
                        HardwareTarget = string.IsNullOrWhiteSpace(active.HardwareTarget) ? _current.HardwareTarget : active.HardwareTarget
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

    public void HandleWorkerStatus(string status, int? activeTaskId, string? stageId, string hardwareTarget, string? taskTitle = null, string? branchName = null)
    {
        if (activeTaskId.HasValue && !AutonomousTaskClassifier.IsAutonomousTask(activeTaskId, stageId))
        {
            return;
        }

        var execStatus = WorkerStatusConstants.IsRunning(status)
            ? WorkerExecutionStatus.Running
            : WorkerStatusConstants.IsWaitingForHuman(status)
                ? WorkerExecutionStatus.WaitingForHuman
                : WorkerStatusConstants.IsBlocked(status)
                    ? WorkerExecutionStatus.Blocked
                    : WorkerExecutionStatus.Idle;

        lock (_lock)
        {
            if (execStatus == WorkerExecutionStatus.Idle)
            {
                // If this idle event is targeted at a specific task that is NOT the currently running task,
                // ignore it so that a completion of another task (e.g. merge of task 10) does not clobber task 8.
                if (activeTaskId.HasValue && _current.Status == WorkerExecutionStatus.Running && _current.ActiveTaskId.HasValue && activeTaskId.Value != _current.ActiveTaskId.Value)
                {
                    return;
                }

                _lastIdleTaskId = activeTaskId ?? _current.ActiveTaskId ?? _telemetryService.GetActiveTask()?.TaskId;
            }
            else if (execStatus == WorkerExecutionStatus.Running)
            {
                _lastIdleTaskId = null;
            }

            var taskChanged = activeTaskId.HasValue && activeTaskId != _current.ActiveTaskId;
            var startedAt = execStatus == WorkerExecutionStatus.Running
                ? (taskChanged ? DateTime.UtcNow : (_current.StartedAt ?? DateTime.UtcNow))
                : (DateTime?)null;
            var keepPrevious = execStatus != WorkerExecutionStatus.Idle && !taskChanged;

            _current = _current with
            {
                Status = execStatus,
                ActiveTaskId = activeTaskId ?? (execStatus == WorkerExecutionStatus.Idle ? null : _current.ActiveTaskId),
                ActiveTaskTitle = taskTitle ?? (keepPrevious ? _current.ActiveTaskTitle : null),
                StageId = stageId ?? (execStatus == WorkerExecutionStatus.Idle ? null : _current.StageId),
                HardwareTarget = string.IsNullOrWhiteSpace(hardwareTarget) ? _current.HardwareTarget : hardwareTarget,
                BranchName = branchName ?? (keepPrevious ? _current.BranchName : null),
                StartedAt = startedAt
            };
        }

        OnWorkerStateChanged?.Invoke();
    }

    public void Dispose()
    {
        _telemetryService.OnActiveTaskChanged -= HandleTelemetryActiveTaskChanged;
    }
}
