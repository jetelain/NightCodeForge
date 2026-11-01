namespace NightCodeForge.Core.Exceptions;

/// <summary>
/// Thrown when a task mutation (edit, reprioritization, removal) is attempted on a task that is no longer
/// in an editable unstarted state (already executed, locked by a running stage, or in a non-editable status).
/// </summary>
public class TaskCannotBeModifiedException : InvalidOperationException
{
    public int TaskId { get; }

    public TaskCannotBeModifiedException(int taskId, string reason)
        : base($"Task #{taskId} cannot be modified: {reason}")
    {
        TaskId = taskId;
    }
}

