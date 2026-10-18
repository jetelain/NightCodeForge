using Microsoft.Extensions.Logging;

namespace NightCodeForge.Core.Execution;

/// <summary>
/// Default implementation of <see cref="IBackgroundTaskRunner"/> executing background work on the ThreadPool
/// with robust exception observation and structured diagnostic logging.
/// </summary>
public class BackgroundTaskRunner : IBackgroundTaskRunner
{
    private readonly ILogger<BackgroundTaskRunner> _logger;

    public BackgroundTaskRunner(ILogger<BackgroundTaskRunner> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public void RunTracked(Func<CancellationToken, Task> work, string description, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        _ = Task.Run(async () =>
        {
            try
            {
                await work(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("Background task '{Description}' was canceled.", description);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception in background task '{Description}'.", description);
            }
        }, CancellationToken.None);
    }

    /// <inheritdoc />
    public void RunTracked(Func<Task> work, string description)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        _ = Task.Run(async () =>
        {
            try
            {
                await work().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("Background task '{Description}' was canceled.", description);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception in background task '{Description}'.", description);
            }
        }, CancellationToken.None);
    }
}
