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
    private WorkerStatusSnapshot _current = new(
        WorkerExecutionStatus.Idle,
        null,
        null,
        null,
        null,
        "Local GPU",
        0);

    public WorkerStatusSnapshot Current => _current;

    public event Action? OnWorkerStateChanged;

    public void UpdateStatus(WorkerStatusSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _current = snapshot;
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

        var startedAt = execStatus == WorkerExecutionStatus.Running
            ? (_current.Status == WorkerExecutionStatus.Running && _current.StartedAt.HasValue ? _current.StartedAt : DateTime.UtcNow)
            : (DateTime?)null;

        _current = _current with
        {
            Status = execStatus,
            ActiveTaskId = activeTaskId ?? (execStatus == WorkerExecutionStatus.Idle ? null : _current.ActiveTaskId),
            ActiveTaskTitle = taskTitle ?? (execStatus == WorkerExecutionStatus.Idle ? null : _current.ActiveTaskTitle),
            StageId = stageId ?? (execStatus == WorkerExecutionStatus.Idle ? null : _current.StageId),
            HardwareTarget = string.IsNullOrWhiteSpace(hardwareTarget) ? _current.HardwareTarget : hardwareTarget,
            BranchName = branchName ?? (execStatus == WorkerExecutionStatus.Idle ? null : _current.BranchName),
            StartedAt = startedAt
        };

        OnWorkerStateChanged?.Invoke();
    }
}
