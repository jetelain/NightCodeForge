namespace NightCodeForge.Core.Domain;

/// <summary>
/// Status outcome of completing and squash-merging a task.
/// </summary>
public enum CompleteTaskStatus
{
    /// <summary>
    /// Squash and merge completed successfully, host mirror updated, worktree removed, and task closed.
    /// </summary>
    Completed,

    /// <summary>
    /// Merge conflict was detected during squash attempt. Maintainer approval was discarded and
    /// conflict resolution has been dispatched to an Implementation Agent.
    /// </summary>
    ConflictResolutionDispatched
}

/// <summary>
/// Result returned by <see cref="NightCodeForge.Core.Workflow.ITaskOrchestrator.CompleteTaskAndCascadeAsync"/>.
/// </summary>
/// <param name="Status">The outcome status.</param>
/// <param name="Message">Optional user-facing summary or details.</param>
/// <param name="ConflictedFiles">Conflicted file paths if conflict resolution was dispatched.</param>
public record CompleteTaskResult(
    CompleteTaskStatus Status,
    string? Message = null,
    IReadOnlyList<string>? ConflictedFiles = null);
