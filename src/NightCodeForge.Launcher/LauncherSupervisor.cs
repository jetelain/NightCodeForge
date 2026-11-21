namespace NightCodeForge.Launcher;

using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;

/// <summary>
/// Supervises the child web host process, restarting automatically on unexpected crashes while respecting
/// user shutdown signals and rapid-crash-loop thresholds.
/// </summary>
public sealed class LauncherSupervisor : IDisposable
{
    private readonly IChildProcessStarter _processStarter;
    private readonly ILauncherLogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly bool _isWindows;
    private readonly CancellationTokenSource _shutdownCts = new();
    private int _shutdownRequested;
    private int _shutdownSignal = (int)PosixSignal.SIGTERM;
    private int _disposed;

    public LauncherSupervisor(
        IChildProcessStarter processStarter,
        ILauncherLogger logger,
        TimeProvider timeProvider,
        bool isWindows)
    {
        _processStarter = processStarter ?? throw new ArgumentNullException(nameof(processStarter));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _isWindows = isWindows;
    }

    public bool IsShutdownRequested => Volatile.Read(ref _shutdownRequested) != 0;

    /// <summary>
    /// Marks shutdown as requested by an OS signal and disables any pending or future automatic restarts.
    /// </summary>
    public void RequestShutdown(string signalDescription, PosixSignal signal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signalDescription);

        if (Interlocked.Exchange(ref _shutdownRequested, 1) == 0)
        {
            Volatile.Write(ref _shutdownSignal, (int)signal);
            try
            {
                _logger.Info($"Shutdown signal received ({signalDescription}); auto-restart disabled for this session.");
            }
            finally
            {
                if (Volatile.Read(ref _disposed) == 0)
                {
                    try
                    {
                        _shutdownCts.Cancel();
                    }
                    catch (ObjectDisposedException)
                    {
                        // Justification: Supervisor was disposed concurrently while shutdown signal arrived.
                    }
                }
            }
        }
    }

    /// <summary>
    /// Runs the child supervision loop until clean exit, user termination, or rapid-crash exhaustion.
    /// </summary>
    public async Task<int> RunAsync(LauncherConfiguration config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var consecutiveRapidCrashes = 0;
        var restartCount = 0;

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token, cancellationToken);

        while (true)
        {
            if (IsShutdownRequested || cancellationToken.IsCancellationRequested)
            {
                return ExitCodeClassifier.ExitCodeSuccess;
            }

            var startedAt = _timeProvider.GetUtcNow();
            IChildProcessHandle child;
            try
            {
                child = _processStarter.Start(
                    config.TargetExecutablePath,
                    config.ContentRootPath,
                    config.ChildArguments);
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException or UnauthorizedAccessException)
            {
                _logger.Error($"Failed to start server '{config.TargetExecutablePath}': {ex.Message}");
                return ExitCodeClassifier.ExitCodeLaunchFailure;
            }

            int exitCode;
            int childPid;

            using (child)
            {
                childPid = child.ProcessId;
                exitCode = await WaitForChildWithShutdownGraceAsync(
                    child,
                    config.ChildShutdownGraceTimeout,
                    linkedCts.Token).ConfigureAwait(false);
            }

            var uptime = _timeProvider.GetUtcNow() - startedAt;
            if (uptime < TimeSpan.Zero)
            {
                uptime = TimeSpan.Zero;
            }

            var formattedExit = ExitCodeClassifier.FormatExitCode(exitCode);

            if (IsShutdownRequested || cancellationToken.IsCancellationRequested)
            {
                _logger.Info($"Server (PID {childPid}) stopped with exit code {formattedExit} during launcher shutdown.");
                return exitCode;
            }

            if (ExitCodeClassifier.IsIntentionalShutdownExitCode(exitCode, _isWindows))
            {
                if (exitCode != ExitCodeClassifier.ExitCodeSuccess)
                {
                    _logger.Info($"Server (PID {childPid}) stopped by user signal with exit code {formattedExit}.");
                }

                return exitCode;
            }

            var uptimeText = FormatDuration(uptime);
            if (!config.AutoRestartEnabled)
            {
                _logger.Error($"Server (PID {childPid}) exited unexpectedly with code {formattedExit} after {uptimeText}. Auto-restart is disabled.");
                return exitCode;
            }

            if (uptime >= config.HealthyUptimeThreshold)
            {
                consecutiveRapidCrashes = 0;
            }
            else
            {
                consecutiveRapidCrashes++;
            }

            if (consecutiveRapidCrashes >= config.MaxRapidCrashes)
            {
                _logger.Error(
                    $"Server (PID {childPid}) exited unexpectedly with code {formattedExit} after {uptimeText}. " +
                    $"Reached rapid-crash limit ({consecutiveRapidCrashes}/{config.MaxRapidCrashes} crashes under {(int)config.HealthyUptimeThreshold.TotalSeconds}s). Stopping auto-restart.");
                return exitCode;
            }

            restartCount++;
            _logger.Warn(
                $"Server (PID {childPid}) crashed with exit code {formattedExit} after {uptimeText}. " +
                $"Restarting in {(int)config.RestartDelay.TotalSeconds}s (restart #{restartCount}, rapid crash {consecutiveRapidCrashes}/{config.MaxRapidCrashes})...");

            if (config.RestartDelay > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(config.RestartDelay, _timeProvider, linkedCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _logger.Info("Pending server restart cancelled by shutdown signal.");
                    return exitCode;
                }
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _shutdownCts.Dispose();
        }
    }

    private async Task<int> WaitForChildWithShutdownGraceAsync(
        IChildProcessHandle child,
        TimeSpan graceTimeout,
        CancellationToken shutdownToken)
    {
        var exitTask = child.WaitForExitAsync(CancellationToken.None);

        if (!shutdownToken.IsCancellationRequested)
        {
            var shutdownTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using (shutdownToken.Register(static state => ((TaskCompletionSource)state!).TrySetResult(), shutdownTcs))
            {
                var firstCompleted = await Task.WhenAny(exitTask, shutdownTcs.Task).ConfigureAwait(false);
                if (ReferenceEquals(firstCompleted, exitTask))
                {
                    return await exitTask.ConfigureAwait(false);
                }
            }
        }

        if (!child.HasExited)
        {
            child.TryRequestGracefulShutdown((PosixSignal)Volatile.Read(ref _shutdownSignal));

            if (child.HasExited)
            {
                return await exitTask.ConfigureAwait(false);
            }

            if (graceTimeout <= TimeSpan.Zero)
            {
                _logger.Warn($"Terminating server (PID {child.ProcessId}) process tree immediately (grace timeout is 0s).");
                child.TryTerminateTree();
                return await exitTask.ConfigureAwait(false);
            }

            var graceSeconds = (int)Math.Ceiling(graceTimeout.TotalSeconds);
            _logger.Info($"Waiting up to {graceSeconds}s for server (PID {child.ProcessId}) to exit gracefully...");

            using var graceCts = new CancellationTokenSource();
            var graceDelayTask = Task.Delay(graceTimeout, _timeProvider, graceCts.Token);
            var winner = await Task.WhenAny(exitTask, graceDelayTask).ConfigureAwait(false);

            if (ReferenceEquals(winner, exitTask))
            {
                graceCts.Cancel();
            }
            else if (!child.HasExited)
            {
                _logger.Warn($"Server (PID {child.ProcessId}) did not exit within {graceSeconds}s; terminating process tree.");
                child.TryTerminateTree();
            }
        }

        return await exitTask.ConfigureAwait(false);
    }

    private static string FormatDuration(TimeSpan elapsed)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{(long)elapsed.TotalHours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}.{elapsed.Milliseconds:D3}");
    }
}
