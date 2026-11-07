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
    /// Identifiers of all currently executing tasks across concurrent workers.
    /// </summary>
    IReadOnlyList<int> ActiveTaskIds => ActiveTaskId.HasValue ? [ActiveTaskId.Value] : [];

    /// <summary>
    /// Indicates whether a task is currently executing.
    /// </summary>
    bool IsRunning { get; }
}
