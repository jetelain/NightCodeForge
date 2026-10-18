namespace NightCodeForge.Core.Exceptions;

/// <summary>
/// Exception thrown when a task modified on disk conflicts with a newer database version.
/// </summary>
public class TaskConcurrencyException : Exception
{
    public int TaskId { get; }
    public int ExpectedVersion { get; }
    public int ActualVersion { get; }

    public TaskConcurrencyException(int taskId, int expectedVersion, int actualVersion)
        : base($"Concurrency conflict detected for Task #{taskId}: expected version {expectedVersion}, but database has version {actualVersion}.")
    {
        TaskId = taskId;
        ExpectedVersion = expectedVersion;
        ActualVersion = actualVersion;
    }
}
