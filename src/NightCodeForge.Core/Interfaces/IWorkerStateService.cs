namespace NightCodeForge.Core.Interfaces;

/// <summary>
/// Provides global visibility into active background worker execution state.
/// </summary>
public interface IWorkerStateService
{
    /// <summary>
    /// Identifier of the currently executing task, or null if the worker is idle.
    /// </summary>
    int? ActiveTaskId { get; }

    /// <summary>
    /// Indicates whether a task is currently executing.
    /// </summary>
    bool IsRunning { get; }
}
