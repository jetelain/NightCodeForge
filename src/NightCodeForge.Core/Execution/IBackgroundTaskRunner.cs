namespace NightCodeForge.Core.Execution;

/// <summary>
/// Service interface for dispatching tracked fire-and-forget background tasks with centralized exception handling and logging.
/// </summary>
public interface IBackgroundTaskRunner
{
    /// <summary>
    /// Executes a background asynchronous action, observing and logging any unhandled exceptions.
    /// </summary>
    /// <param name="work">The asynchronous work to execute.</param>
    /// <param name="description">A descriptive name of the background operation for diagnostic logging.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    void RunTracked(Func<CancellationToken, Task> work, string description, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a background action, observing and logging any unhandled exceptions.
    /// </summary>
    /// <param name="work">The asynchronous work to execute.</param>
    /// <param name="description">A descriptive name of the background operation for diagnostic logging.</param>
    void RunTracked(Func<Task> work, string description);
}
