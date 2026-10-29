namespace NightCodeForge.Core.Exceptions;

/// <summary>
/// Exception thrown when an active worktree directory cannot be found on disk for a task.
/// </summary>
public class WorktreeNotFoundException : Exception
{
    public int TaskId { get; }
    public string AttemptedPath { get; }

    public WorktreeNotFoundException(int taskId, string attemptedPath, Exception? innerException = null)
        : base($"Active worktree directory for task #{taskId} was not found at '{attemptedPath}'.", innerException)
    {
        TaskId = taskId;
        AttemptedPath = attemptedPath;
    }
}
