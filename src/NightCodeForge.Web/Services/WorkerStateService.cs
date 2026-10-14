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
    int PendingQuestionCount);

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
}
