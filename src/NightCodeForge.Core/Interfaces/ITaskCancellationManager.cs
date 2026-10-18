namespace NightCodeForge.Core.Interfaces;

/// <summary>
/// Coordinates runtime cooperative cancellation of active background tasks and orchestrator executions.
/// </summary>
public interface ITaskCancellationManager
{
    /// <summary>
    /// Registers an active execution for the specified task, linking with an optional parent token.
    /// Returns a CancellationTokenSource owned by the caller (the caller must dispose it).
    /// An existing registration is never cancelled or disposed by a new registration.
    /// </summary>
    CancellationTokenSource Register(int taskId, CancellationToken parentToken = default);

    /// <summary>
    /// Requests cancellation for the specified running task.
    /// Returns true if an active execution was found and cancelled, false otherwise.
    /// </summary>
    bool Cancel(int taskId);

    /// <summary>
    /// Returns true when <see cref="Cancel"/> was explicitly requested for the current registration of the task
    /// (as opposed to a parent token cancellation such as application shutdown).
    /// </summary>
    bool IsCancelRequested(int taskId);

    /// <summary>
    /// Unregisters the task execution owned by <paramref name="source"/>, cleaning up tracking state.
    /// No-op if a newer registration has replaced it.
    /// </summary>
    void Unregister(int taskId, CancellationTokenSource source);
}
