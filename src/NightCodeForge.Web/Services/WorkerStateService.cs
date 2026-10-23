using NightCodeForge.Core.Interfaces;

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
public class WorkerStateService
{
    private readonly IOperationsTelemetryService _telemetryService;
    private readonly object _lock = new();
    private WorkerStatusSnapshot _current;
    private bool _hasAuthoritativeStatus;

    public WorkerStateService(IOperationsTelemetryService telemetryService)
    {
        _telemetryService = telemetryService ?? throw new ArgumentNullException(nameof(telemetryService));
        var active = _telemetryService.GetActiveTask();
        if (active != null)
        {
            _current = new WorkerStatusSnapshot(
                WorkerExecutionStatus.Running,
                active.TaskId,
                active.TaskTitle,
                active.CurrentStageId,
                null,
                "Local GPU",
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

    public WorkerStatusSnapshot Current
    {
        get
        {
            lock (_lock)
            {
                // Telemetry is only a fallback until the hub reports an authoritative status;
                // afterwards an explicit Idle must not be overridden by stale telemetry.
                if (!_hasAuthoritativeStatus && _current.ActiveTaskId == null)
                {
                    var active = _telemetryService.GetActiveTask();
                    if (active != null)
                    {
                        _current = _current with
                        {
                            Status = WorkerExecutionStatus.Running,
                            ActiveTaskId = active.TaskId,
                            ActiveTaskTitle = active.TaskTitle,
                            StageId = active.CurrentStageId,
                            StartedAt = active.StartedAt
                        };
                    }
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
            _hasAuthoritativeStatus = true;
        }
        OnWorkerStateChanged?.Invoke();
    }

    public void UpdatePendingQuestions(int count)
    {
        _current = _current with { PendingQuestionCount = count };
        OnWorkerStateChanged?.Invoke();
    }

    public void HandleWorkerStatus(string status, int? activeTaskId, string? stageId, string hardwareTarget, string? taskTitle = null, string? branchName = null)
    {
        var execStatus = status?.ToLowerInvariant() switch
        {
            "running" => WorkerExecutionStatus.Running,
            "waitingforhuman" or "waiting_for_human" or "waiting" => WorkerExecutionStatus.WaitingForHuman,
            "blocked" => WorkerExecutionStatus.Blocked,
            _ => WorkerExecutionStatus.Idle
        };

        lock (_lock)
        {
            var startedAt = execStatus == WorkerExecutionStatus.Running
                ? (_current.Status == WorkerExecutionStatus.Running && _current.StartedAt.HasValue ? _current.StartedAt : DateTime.UtcNow)
                : (DateTime?)null;

            var taskChanged = activeTaskId.HasValue && activeTaskId != _current.ActiveTaskId;
            if (taskChanged)
            {
                // A different task started: never carry over the previous task's title/branch/start time.
                startedAt = execStatus == WorkerExecutionStatus.Running ? DateTime.UtcNow : null;
            }
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
            _hasAuthoritativeStatus = true;
        }

        OnWorkerStateChanged?.Invoke();
    }
}
