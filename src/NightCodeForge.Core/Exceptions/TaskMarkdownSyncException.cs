namespace NightCodeForge.Core.Exceptions;

/// <summary>
/// Exception thrown when task markdown synchronization fails due to malformed files,
/// invalid dependencies, unknown tasks, or policy violations.
/// </summary>
public class TaskMarkdownSyncException : Exception
{
    public string? FilePath { get; }
    public int? TaskId { get; }

    public TaskMarkdownSyncException(string message, string? filePath = null, int? taskId = null)
        : base(message)
    {
        FilePath = filePath;
        TaskId = taskId;
    }

    public TaskMarkdownSyncException(string message, Exception innerException, string? filePath = null, int? taskId = null)
        : base(message, innerException)
    {
        FilePath = filePath;
        TaskId = taskId;
    }
}
