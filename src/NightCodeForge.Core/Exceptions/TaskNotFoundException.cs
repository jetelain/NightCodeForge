namespace NightCodeForge.Core.Exceptions;

/// <summary>
/// Thrown when a requested task cannot be found in the repository or database.
/// </summary>
public class TaskNotFoundException : InvalidOperationException
{
    public int TaskId { get; }

    public TaskNotFoundException(int taskId)
        : base($"Task with ID #{taskId} was not found.")
    {
        TaskId = taskId;
    }

    public TaskNotFoundException(int taskId, string message)
        : base(message)
    {
        TaskId = taskId;
    }

    public TaskNotFoundException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
