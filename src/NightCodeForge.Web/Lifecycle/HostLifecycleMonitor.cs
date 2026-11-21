namespace NightCodeForge.Web.Lifecycle;

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NightCodeForge.Infrastructure.Configuration;

/// <summary>
/// Monitors and logs host startup, listening readiness, shutdown signals, unhandled exceptions,
/// process exits, and previous-run unclean termination markers.
/// </summary>
public sealed class HostLifecycleMonitor : IDisposable
{
    public const string VersionFileName = "VERSION";
    public const string UnknownVersion = "unknown";
    public const string TriggerSigInt = "OS signal SIGINT (Ctrl+C)";
    public const string TriggerSigQuit = "OS signal SIGQUIT (Ctrl+Break)";
    public const string TriggerSigTermWindows = "OS signal SIGTERM (OS shutdown / CTRL_SHUTDOWN_EVENT)";
    public const string TriggerSigTermUnix = "OS signal SIGTERM (termination signal)";
    public const string TriggerSigHupWindows = "OS signal SIGHUP (console window closed / CTRL_CLOSE_EVENT)";
    public const string TriggerSigHupUnix = "OS signal SIGHUP (terminal hangup)";
    public const string TriggerHostStopApplication = "IHostApplicationLifetime.StopApplication()";
    public const string TriggerHostAborted = "HostAbortedException (design-time tooling)";
    public const string TriggerProcessExit = "AppDomain.ProcessExit";
    public const string TriggerStartupOrHostExceptionPrefix = "Unhandled startup/host exception";
    public const string TriggerUnhandledExceptionPrefix = "AppDomain.UnhandledException";
    public const string UnknownTrigger = "Unknown";
    public const string ConsoleWindowTitle = "NightCodeForge";
    public const string RunningConsoleMessagePrefix = "✅ NightCodeForge is running : ";

    private const string UnavailableAddresses = "unavailable";
    private const string LocalhostHost = "localhost";
    private const string WildcardPlus = "+";
    private const string WildcardStar = "*";
    private const string WildcardIpv4Any = "0.0.0.0";
    private const string WildcardIpv6Any = "[::]";
    private const string SchemeSeparator = "://";

    private readonly InstanceLifecycleStateStore _stateStore;
    private readonly HostLifecycleInfo _hostInfo;
    private readonly ILogger<HostLifecycleMonitor> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly Action _flushLogs;
    private readonly Action<string> _writeConsoleLine;
    private readonly object _syncLock = new();
    private readonly object _stateLock = new();
    private readonly List<PosixSignalRegistration> _signalRegistrations = [];
    private readonly List<CancellationTokenRegistration> _lifetimeRegistrations = [];

    private UnhandledExceptionEventHandler? _unhandledExceptionHandler;
    private EventHandler? _processExitHandler;
    private EventHandler<UnobservedTaskExceptionEventArgs>? _unobservedTaskExceptionHandler;

    private long _startupTimestampTicks;
    private long _stoppingTimestampTicks = long.MinValue;
    private string? _shutdownTrigger;
    private bool _terminalStateRecorded;
    private int _processEventsAttached;
    private int _disposed;

    public HostLifecycleMonitor(
        InstanceLifecycleStateStore stateStore,
        HostLifecycleInfo hostInfo,
        ILogger<HostLifecycleMonitor> logger,
        TimeProvider timeProvider,
        Action flushLogs,
        Action<string> writeConsoleLine)
    {
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _hostInfo = hostInfo ?? throw new ArgumentNullException(nameof(hostInfo));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _flushLogs = flushLogs ?? throw new ArgumentNullException(nameof(flushLogs));
        _writeConsoleLine = writeConsoleLine ?? throw new ArgumentNullException(nameof(writeConsoleLine));
    }

    /// <summary>
    /// Configures UTF-8 console output encoding and sets the console window title.
    /// </summary>
    public static void ConfigureConsoleWindow()
    {
        ConfigureConsoleWindow(
            static () => Console.OutputEncoding = Encoding.UTF8,
            static () => Console.Title = ConsoleWindowTitle);
    }

    internal static void ConfigureConsoleWindow(Action setEncoding, Action setTitle)
    {
        ArgumentNullException.ThrowIfNull(setEncoding);
        ArgumentNullException.ThrowIfNull(setTitle);

        try
        {
            setEncoding();
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
        {
            // Justification: Non-interactive/detached hosts or redirected streams may lack a console handle or disallow encoding changes.
            _ = ex;
        }

        try
        {
            setTitle();
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
        {
            // Justification: Non-interactive hosts or restricted terminals may not support setting Console.Title.
            _ = ex;
        }
    }

    /// <summary>
    /// Normalizes a Kestrel listening address for display, converting bind-all wildcard hosts to <c>localhost</c>.
    /// </summary>
    public static string NormalizeDisplayAddress(string address)
    {
        ArgumentNullException.ThrowIfNull(address);

        var trimmed = address.Trim().TrimEnd('/');
        var schemeIndex = trimmed.IndexOf(SchemeSeparator, StringComparison.Ordinal);
        if (schemeIndex < 0)
        {
            return trimmed;
        }

        var authorityStart = schemeIndex + SchemeSeparator.Length;
        if (authorityStart >= trimmed.Length)
        {
            return trimmed;
        }

        int hostEnd;
        if (trimmed[authorityStart] == '[')
        {
            var closingBracket = trimmed.IndexOf(']', authorityStart + 1);
            if (closingBracket < 0)
            {
                return trimmed;
            }

            hostEnd = closingBracket + 1;
        }
        else
        {
            var colonOrSlash = trimmed.IndexOfAny([':', '/'], authorityStart);
            hostEnd = colonOrSlash < 0 ? trimmed.Length : colonOrSlash;
        }

        var host = trimmed[authorityStart..hostEnd];
        if (string.Equals(host, WildcardPlus, StringComparison.Ordinal) ||
            string.Equals(host, WildcardStar, StringComparison.Ordinal) ||
            string.Equals(host, WildcardIpv4Any, StringComparison.Ordinal) ||
            string.Equals(host, WildcardIpv6Any, StringComparison.Ordinal))
        {
            return $"{trimmed[..authorityStart]}{LocalhostHost}{trimmed[hostEnd..]}";
        }

        return trimmed;
    }

    /// <summary>
    /// Formats the user-facing console banner indicating the web server is ready.
    /// </summary>
    public static string FormatRunningConsoleMessage(ICollection<string>? rawAddresses)
    {
        if (rawAddresses is not { Count: > 0 })
        {
            return $"{RunningConsoleMessagePrefix}{UnavailableAddresses}";
        }

        var normalized = rawAddresses
            .Where(static address => !string.IsNullOrWhiteSpace(address))
            .Select(NormalizeDisplayAddress)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var displayAddresses = normalized.Length > 0
            ? string.Join(", ", normalized)
            : UnavailableAddresses;

        return $"{RunningConsoleMessagePrefix}{displayAddresses}";
    }

    /// <summary>
    /// Resolves the deployed application version from the <c>VERSION</c> file or assembly attributes.
    /// </summary>
    public static string ResolveApplicationVersion(string contentRootPath, Assembly entryAssembly)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);
        ArgumentNullException.ThrowIfNull(entryAssembly);

        var versionFilePath = Path.Combine(contentRootPath, VersionFileName);
        try
        {
            if (File.Exists(versionFilePath))
            {
                var fileVersion = File.ReadAllText(versionFilePath).Trim();
                if (!string.IsNullOrWhiteSpace(fileVersion))
                {
                    return fileVersion;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Justification: Missing/unreadable VERSION file falls back to assembly metadata during early startup.
            _ = ex;
        }

        var informational = entryAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            return informational.Trim();
        }

        return entryAssembly.GetName().Version?.ToString() ?? UnknownVersion;
    }

    /// <summary>
    /// Reads the current process start timestamp in UTC, falling back to <paramref name="timeProvider"/> when unavailable.
    /// </summary>
    public static DateTimeOffset TryGetCurrentProcessStartTimeUtc(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        try
        {
            using var process = Process.GetCurrentProcess();
            return process.StartTime.ToUniversalTime();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Justification: Some restricted/sandboxed hosts disallow querying Process.StartTime.
            _ = ex;
            return timeProvider.GetUtcNow();
        }
    }

    /// <summary>
    /// Maps a POSIX / Windows console signal to a human-readable shutdown trigger description.
    /// </summary>
    public static string FormatPosixSignalReason(PosixSignal signal, bool isWindows)
    {
        return signal switch
        {
            PosixSignal.SIGINT => TriggerSigInt,
            PosixSignal.SIGQUIT => TriggerSigQuit,
            PosixSignal.SIGTERM => isWindows ? TriggerSigTermWindows : TriggerSigTermUnix,
            PosixSignal.SIGHUP => isWindows ? TriggerSigHupWindows : TriggerSigHupUnix,
            _ => $"OS signal {signal}"
        };
    }

    /// <summary>
    /// Logs instance startup details, assesses the previous run's marker for unclean exits,
    /// and records the current instance as <see cref="InstanceLifecycleStatus.Running"/>.
    /// </summary>
    public void BeginStartup()
    {
        var now = _timeProvider.GetUtcNow();
        Interlocked.Exchange(ref _startupTimestampTicks, now.UtcTicks);

        _logger.LogInformation(
            "NightCodeForge instance starting (Version: {Version}, PID: {ProcessId}, SessionId: {SessionId}, Environment: {Environment}, OS: {OsDescription}, Runtime: {FrameworkDescription}, ContentRoot: {ContentRootPath}, LogsDirectory: {LogsDirectory}, UserConfig: {UserConfigStatus}).",
            _hostInfo.Version,
            _hostInfo.ProcessId,
            _hostInfo.SessionId,
            _hostInfo.EnvironmentName,
            _hostInfo.OsDescription,
            _hostInfo.FrameworkDescription,
            _hostInfo.ContentRootPath,
            _hostInfo.LogsDirectory,
            _hostInfo.UserConfigStatus);

        if (_hostInfo.UserConfigStatus == UserConfigLoadStatus.InvalidJson)
        {
            _logger.LogWarning(
                "User configuration file {ConfigPath} contains invalid JSON and was IGNORED (fail-open); factory defaults from appsettings.json remain in effect.",
                HostConfigurationSources.GetFullPath(_hostInfo.ContentRootPath));
        }

        LogPreviousRunAssessment(_stateStore.InspectPreviousRun(_hostInfo.ProcessId));

        Exception? writeError = null;
        lock (_stateLock)
        {
            if (!_terminalStateRecorded)
            {
                TryPersistStateUnderLock(
                    CreateState(
                        InstanceLifecycleStatus.Running,
                        stoppingAtUtc: null,
                        stoppedAtUtc: null,
                        shutdownReason: null,
                        exitCode: null),
                    out writeError);
            }
        }

        LogWriteFailureIfAny(InstanceLifecycleStatus.Running, writeError);
    }

    /// <summary>
    /// Subscribes to OS termination signals, AppDomain unhandled exceptions, unobserved task errors, and ProcessExit.
    /// </summary>
    public void AttachProcessEventHandlers()
    {
        if (Interlocked.Exchange(ref _processEventsAttached, 1) != 0)
        {
            return;
        }

        RegisterPosixSignalHandlers();

        _unhandledExceptionHandler = (_, args) =>
            HandleUnhandledException(args.ExceptionObject, args.IsTerminating);
        _processExitHandler = (_, _) =>
            HandleProcessExit(Environment.ExitCode);
        _unobservedTaskExceptionHandler = (_, args) =>
            HandleUnobservedTaskException(args.Exception);

        AppDomain.CurrentDomain.UnhandledException += _unhandledExceptionHandler;
        AppDomain.CurrentDomain.ProcessExit += _processExitHandler;
        TaskScheduler.UnobservedTaskException += _unobservedTaskExceptionHandler;
    }

    /// <summary>
    /// Wires <see cref="IHostApplicationLifetime"/> started/stopping/stopped callbacks and logs bound server addresses.
    /// </summary>
    public void AttachHostLifetime(IHostApplicationLifetime lifetime, IServer server)
    {
        ArgumentNullException.ThrowIfNull(lifetime);
        ArgumentNullException.ThrowIfNull(server);

        lock (_syncLock)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            _lifetimeRegistrations.Add(lifetime.ApplicationStarted.Register(() => OnApplicationStarted(server)));
            _lifetimeRegistrations.Add(lifetime.ApplicationStopping.Register(OnApplicationStopping));
            _lifetimeRegistrations.Add(lifetime.ApplicationStopped.Register(OnApplicationStopped));
        }
    }

    /// <summary>
    /// Records an OS termination signal without cancelling propagation to ASP.NET Core's <c>ConsoleLifetime</c>.
    /// </summary>
    public void HandlePosixSignal(PosixSignal signal)
    {
        var reason = FormatPosixSignalReason(signal, OperatingSystem.IsWindows());
        Exception? writeError = null;

        lock (_stateLock)
        {
            if (_shutdownTrigger is null || string.Equals(_shutdownTrigger, TriggerHostStopApplication, StringComparison.Ordinal))
            {
                _shutdownTrigger = reason;
            }

            var stoppingAt = EnsureStoppingTimestampUnderLock();
            var effectiveTrigger = _shutdownTrigger;

            // Persist immediately so abrupt console close (SIGHUP) is captured on disk even if OS terminates before StopAsync finishes.
            if (!_terminalStateRecorded)
            {
                TryPersistStateUnderLock(
                    CreateState(
                        InstanceLifecycleStatus.Stopping,
                        stoppingAtUtc: stoppingAt,
                        stoppedAtUtc: null,
                        shutdownReason: effectiveTrigger,
                        exitCode: null),
                    out writeError);
            }
        }

        LogWriteFailureIfAny(InstanceLifecycleStatus.Stopping, writeError);

        var uptime = FormatUptime();
        if (signal == PosixSignal.SIGHUP)
        {
            _logger.LogWarning(
                "Shutdown signal received: {Signal} — {Reason} (PID: {ProcessId}, SessionId: {SessionId}, Uptime: {Uptime}).",
                signal,
                reason,
                _hostInfo.ProcessId,
                _hostInfo.SessionId,
                uptime);
        }
        else
        {
            _logger.LogInformation(
                "Shutdown signal received: {Signal} — {Reason} (PID: {ProcessId}, SessionId: {SessionId}, Uptime: {Uptime}).",
                signal,
                reason,
                _hostInfo.ProcessId,
                _hostInfo.SessionId,
                uptime);
        }
    }

    /// <summary>
    /// Logs an unhandled AppDomain exception and records a <see cref="InstanceLifecycleStatus.Crashed"/> state if terminating.
    /// </summary>
    public void HandleUnhandledException(object? exceptionObject, bool isTerminating)
    {
        var ex = exceptionObject as Exception;
        var exType = ex?.GetType().FullName ?? exceptionObject?.GetType().FullName ?? UnknownTrigger;
        var exMessage = ex?.Message ?? exceptionObject?.ToString() ?? "Non-exception object thrown.";
        var uptime = FormatUptime();

        _logger.LogCritical(
            ex,
            "Unhandled AppDomain exception detected (IsTerminating: {IsTerminating}, PID: {ProcessId}, SessionId: {SessionId}, Uptime: {Uptime}, ExceptionType: {ExceptionType}, Detail: {Detail}).",
            isTerminating,
            _hostInfo.ProcessId,
            _hostInfo.SessionId,
            uptime,
            exType,
            exMessage);

        if (!isTerminating)
        {
            return;
        }

        var reason = $"{TriggerUnhandledExceptionPrefix}: {ex?.GetType().Name ?? exType}: {exMessage}";
        Exception? writeError = null;

        lock (_stateLock)
        {
            if (_shutdownTrigger is null || string.Equals(_shutdownTrigger, TriggerHostStopApplication, StringComparison.Ordinal))
            {
                _shutdownTrigger = reason;
            }

            if (!_terminalStateRecorded)
            {
                _terminalStateRecorded = true;
                var now = _timeProvider.GetUtcNow();
                TryPersistStateUnderLock(
                    CreateState(
                        InstanceLifecycleStatus.Crashed,
                        stoppingAtUtc: TryGetStoppingTimestampUnderLock(),
                        stoppedAtUtc: now,
                        shutdownReason: _shutdownTrigger,
                        exitCode: null,
                        exceptionType: exType,
                        exceptionMessage: exMessage),
                    out writeError);
            }
        }

        LogWriteFailureIfAny(InstanceLifecycleStatus.Crashed, writeError);
        _flushLogs();
    }

    /// <summary>
    /// Logs an unobserved task exception surfaced by <see cref="TaskScheduler.UnobservedTaskException"/>.
    /// </summary>
    public void HandleUnobservedTaskException(AggregateException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        _logger.LogError(
            exception,
            "Unobserved Task exception observed by TaskScheduler (PID: {ProcessId}, SessionId: {SessionId}, Uptime: {Uptime}).",
            _hostInfo.ProcessId,
            _hostInfo.SessionId,
            FormatUptime());
    }

    /// <summary>
    /// Handles <see cref="AppDomain.ProcessExit"/>, warning and persisting state if graceful host stop did not finish.
    /// </summary>
    public void HandleProcessExit(int exitCode)
    {
        var recordedNow = false;
        var trigger = UnknownTrigger;
        Exception? writeError = null;

        lock (_stateLock)
        {
            if (!_terminalStateRecorded)
            {
                _terminalStateRecorded = true;
                recordedNow = true;
                _shutdownTrigger ??= TriggerProcessExit;
                trigger = _shutdownTrigger;
                var now = _timeProvider.GetUtcNow();

                TryPersistStateUnderLock(
                    CreateState(
                        InstanceLifecycleStatus.ExitedWithoutGracefulShutdown,
                        stoppingAtUtc: TryGetStoppingTimestampUnderLock(),
                        stoppedAtUtc: now,
                        shutdownReason: trigger,
                        exitCode: exitCode),
                    out writeError);
            }
        }

        if (recordedNow)
        {
            LogWriteFailureIfAny(InstanceLifecycleStatus.ExitedWithoutGracefulShutdown, writeError);
            _logger.LogWarning(
                "Process exiting via AppDomain.ProcessExit without graceful host shutdown completion (PID: {ProcessId}, SessionId: {SessionId}, ExitCode: {ExitCode}, Trigger: {Trigger}, Uptime: {Uptime}).",
                _hostInfo.ProcessId,
                _hostInfo.SessionId,
                exitCode,
                trigger,
                FormatUptime());
        }

        _flushLogs();
    }

    /// <summary>
    /// Records an intentional abort triggered by EF Core / design-time tooling (<see cref="HostAbortedException"/>).
    /// </summary>
    public void RecordHostAborted()
    {
        Exception? writeError = null;

        lock (_stateLock)
        {
            _shutdownTrigger ??= TriggerHostAborted;
            if (!_terminalStateRecorded)
            {
                _terminalStateRecorded = true;
                var now = _timeProvider.GetUtcNow();
                TryPersistStateUnderLock(
                    CreateState(
                        InstanceLifecycleStatus.StoppedCleanly,
                        stoppingAtUtc: TryGetStoppingTimestampUnderLock(),
                        stoppedAtUtc: now,
                        shutdownReason: TriggerHostAborted,
                        exitCode: 0),
                    out writeError);
            }
        }

        LogWriteFailureIfAny(InstanceLifecycleStatus.StoppedCleanly, writeError);
        _logger.LogInformation(
            "Host execution aborted by design-time tooling (HostAbortedException) (PID: {ProcessId}, SessionId: {SessionId}, Uptime: {Uptime}).",
            _hostInfo.ProcessId,
            _hostInfo.SessionId,
            FormatUptime());
    }

    /// <summary>
    /// Logs and persists a fatal exception that terminated startup or <c>app.Run()</c>.
    /// </summary>
    public void RecordFatalStartupOrHostException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var reason = $"{TriggerStartupOrHostExceptionPrefix}: {exception.GetType().Name}: {exception.Message}";
        Exception? writeError = null;

        lock (_stateLock)
        {
            if (_shutdownTrigger is null || string.Equals(_shutdownTrigger, TriggerHostStopApplication, StringComparison.Ordinal))
            {
                _shutdownTrigger = reason;
            }

            if (!_terminalStateRecorded)
            {
                _terminalStateRecorded = true;
                var now = _timeProvider.GetUtcNow();
                TryPersistStateUnderLock(
                    CreateState(
                        InstanceLifecycleStatus.Crashed,
                        stoppingAtUtc: TryGetStoppingTimestampUnderLock(),
                        stoppedAtUtc: now,
                        shutdownReason: _shutdownTrigger,
                        exitCode: null,
                        exceptionType: exception.GetType().FullName,
                        exceptionMessage: exception.Message),
                    out writeError);
            }
        }

        LogWriteFailureIfAny(InstanceLifecycleStatus.Crashed, writeError);
        _logger.LogCritical(
            exception,
            "NightCodeForge terminated due to fatal startup or host exception (PID: {ProcessId}, SessionId: {SessionId}, Uptime: {Uptime}).",
            _hostInfo.ProcessId,
            _hostInfo.SessionId,
            FormatUptime());
    }

    /// <summary>
    /// Safety net invoked after <c>app.Run()</c> returns to ensure clean-stop logging and state persistence completed.
    /// </summary>
    public void MarkRunCompleted()
    {
        OnApplicationStopped();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_unhandledExceptionHandler is not null)
        {
            AppDomain.CurrentDomain.UnhandledException -= _unhandledExceptionHandler;
        }

        if (_processExitHandler is not null)
        {
            AppDomain.CurrentDomain.ProcessExit -= _processExitHandler;
        }

        if (_unobservedTaskExceptionHandler is not null)
        {
            TaskScheduler.UnobservedTaskException -= _unobservedTaskExceptionHandler;
        }

        lock (_syncLock)
        {
            DisposeSignalRegistrationsUnderLock();

            foreach (var registration in _lifetimeRegistrations)
            {
                registration.Dispose();
            }

            _lifetimeRegistrations.Clear();
        }
    }

    private void OnApplicationStarted(IServer server)
    {
        // Re-register after ConsoleLifetime.WaitForStartAsync() so our handlers run BEFORE ConsoleLifetime in LIFO order.
        RegisterPosixSignalHandlers();

        var rawAddresses = server.Features.Get<IServerAddressesFeature>()?.Addresses;
        var addresses = rawAddresses is { Count: > 0 }
            ? string.Join(", ", rawAddresses)
            : UnavailableAddresses;

        var startTimestamp = GetEffectiveStartTimestamp();
        var startupDurationMs = Math.Max(0L, (long)Math.Round((_timeProvider.GetUtcNow() - startTimestamp).TotalMilliseconds));

        _logger.LogInformation(
            "NightCodeForge started and listening on {Addresses} (PID: {ProcessId}, SessionId: {SessionId}, Environment: {Environment}, StartupDurationMs: {StartupDurationMs}). Press Ctrl+C to shut down.",
            addresses,
            _hostInfo.ProcessId,
            _hostInfo.SessionId,
            _hostInfo.EnvironmentName,
            startupDurationMs);

        try
        {
            _writeConsoleLine(FormatRunningConsoleMessage(rawAddresses));
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Justification: Stdout handle may be closed or detached on service/background runs; startup must continue.
            _ = ex;
        }
    }

    private void OnApplicationStopping()
    {
        string trigger;
        Exception? writeError = null;

        lock (_stateLock)
        {
            _shutdownTrigger ??= TriggerHostStopApplication;
            var stoppingAt = EnsureStoppingTimestampUnderLock();
            trigger = _shutdownTrigger;

            if (!_terminalStateRecorded)
            {
                TryPersistStateUnderLock(
                    CreateState(
                        InstanceLifecycleStatus.Stopping,
                        stoppingAtUtc: stoppingAt,
                        stoppedAtUtc: null,
                        shutdownReason: trigger,
                        exitCode: null),
                    out writeError);
            }
        }

        LogWriteFailureIfAny(InstanceLifecycleStatus.Stopping, writeError);

        _logger.LogInformation(
            "NightCodeForge is stopping (PID: {ProcessId}, SessionId: {SessionId}, Trigger: {Trigger}, Uptime: {Uptime}).",
            _hostInfo.ProcessId,
            _hostInfo.SessionId,
            trigger,
            FormatUptime());
    }

    private void OnApplicationStopped()
    {
        var recordedNow = false;
        DateTimeOffset now = default;
        DateTimeOffset stoppingAt = default;
        var trigger = UnknownTrigger;
        Exception? writeError = null;

        lock (_stateLock)
        {
            if (!_terminalStateRecorded)
            {
                _terminalStateRecorded = true;
                recordedNow = true;
                now = _timeProvider.GetUtcNow();
                stoppingAt = TryGetStoppingTimestampUnderLock() ?? now;
                trigger = _shutdownTrigger ?? UnknownTrigger;

                TryPersistStateUnderLock(
                    CreateState(
                        InstanceLifecycleStatus.StoppedCleanly,
                        stoppingAtUtc: stoppingAt,
                        stoppedAtUtc: now,
                        shutdownReason: trigger,
                        exitCode: 0),
                    out writeError);
            }
        }

        if (!recordedNow)
        {
            return;
        }

        LogWriteFailureIfAny(InstanceLifecycleStatus.StoppedCleanly, writeError);
        var shutdownDurationMs = Math.Max(0L, (long)Math.Round((now - stoppingAt).TotalMilliseconds));

        _logger.LogInformation(
            "NightCodeForge stopped cleanly (PID: {ProcessId}, SessionId: {SessionId}, Trigger: {Trigger}, Uptime: {Uptime}, ShutdownDurationMs: {ShutdownDurationMs}).",
            _hostInfo.ProcessId,
            _hostInfo.SessionId,
            trigger,
            FormatUptime(),
            shutdownDurationMs);
    }

    private void LogPreviousRunAssessment(PreviousInstanceAssessment assessment)
    {
        var prev = assessment.PreviousState;
        switch (assessment.Kind)
        {
            case PreviousInstanceAssessmentKind.None:
                _logger.LogInformation(
                    "No previous instance lifecycle marker found at {StateFilePath}; treating this as a clean initial start.",
                    _stateStore.StateFilePath);
                break;

            case PreviousInstanceAssessmentKind.StoppedCleanly when prev is not null:
                _logger.LogInformation(
                    "Previous instance stopped cleanly (PreviousPID: {PreviousPid}, SessionId: {PreviousSessionId}, Version: {PreviousVersion}, StoppedAtUtc: {StoppedAtUtc:O}, Trigger: {Trigger}).",
                    prev.ProcessId,
                    prev.SessionId,
                    prev.Version,
                    prev.StoppedAtUtc,
                    prev.ShutdownReason ?? UnknownTrigger);
                break;

            case PreviousInstanceAssessmentKind.UncleanTerminationDetected when prev is not null:
                _logger.LogWarning(
                    "Previous NightCodeForge instance terminated unexpectedly while Running without recording a shutdown signal or clean stop (PreviousPID: {PreviousPid}, SessionId: {PreviousSessionId}, Version: {PreviousVersion}, StartedAtUtc: {StartedAtUtc:O}). Check Windows Event Viewer (Application log) or OS crash reports for native/runtime crash details (e.g. fast-fail, stack overflow, OOM, or external process kill).",
                    prev.ProcessId,
                    prev.SessionId,
                    prev.Version,
                    prev.RecordedStartedAtUtc);
                break;

            case PreviousInstanceAssessmentKind.TerminatedDuringShutdown when prev is not null:
                _logger.LogWarning(
                    "Previous NightCodeForge instance began shutting down at {StoppingAtUtc:O} (Trigger: {Trigger}) but terminated before completing graceful shutdown (PreviousPID: {PreviousPid}, SessionId: {PreviousSessionId}, Version: {PreviousVersion}).",
                    prev.StoppingAtUtc,
                    prev.ShutdownReason ?? UnknownTrigger,
                    prev.ProcessId,
                    prev.SessionId,
                    prev.Version);
                break;

            case PreviousInstanceAssessmentKind.Crashed when prev is not null:
                _logger.LogWarning(
                    "Previous NightCodeForge instance crashed with an unhandled exception (PreviousPID: {PreviousPid}, SessionId: {PreviousSessionId}, Version: {PreviousVersion}, StoppedAtUtc: {StoppedAtUtc:O}, ExceptionType: {ExceptionType}, Reason: {Reason}).",
                    prev.ProcessId,
                    prev.SessionId,
                    prev.Version,
                    prev.StoppedAtUtc,
                    prev.ExceptionType ?? UnknownTrigger,
                    prev.ShutdownReason ?? UnknownTrigger);
                break;

            case PreviousInstanceAssessmentKind.ExitedWithoutGracefulShutdown when prev is not null:
                _logger.LogWarning(
                    "Previous NightCodeForge instance exited via ProcessExit without completing graceful host shutdown (PreviousPID: {PreviousPid}, SessionId: {PreviousSessionId}, Version: {PreviousVersion}, ExitCode: {ExitCode}, StoppedAtUtc: {StoppedAtUtc:O}, Trigger: {Trigger}).",
                    prev.ProcessId,
                    prev.SessionId,
                    prev.Version,
                    prev.ExitCode,
                    prev.StoppedAtUtc,
                    prev.ShutdownReason ?? UnknownTrigger);
                break;

            case PreviousInstanceAssessmentKind.AnotherInstanceStillRunning when prev is not null:
                _logger.LogWarning(
                    "Another NightCodeForge instance sharing logs directory {LogsDirectory} appears to still be running (ActivePID: {PreviousPid}, SessionId: {PreviousSessionId}, StartedAtUtc: {StartedAtUtc:O}).",
                    _hostInfo.LogsDirectory,
                    prev.ProcessId,
                    prev.SessionId,
                    prev.RecordedStartedAtUtc);
                break;

            case PreviousInstanceAssessmentKind.CorruptedStateFile:
                _logger.LogWarning(
                    "Previous instance lifecycle marker {StateFilePath} could not be parsed ({DiagnosticError}); continuing startup.",
                    _stateStore.StateFilePath,
                    assessment.DiagnosticError ?? UnknownTrigger);
                break;
        }
    }

    private void RegisterPosixSignalHandlers()
    {
        PosixSignal[] signals =
        [
            PosixSignal.SIGINT,
            PosixSignal.SIGQUIT,
            PosixSignal.SIGTERM,
            PosixSignal.SIGHUP
        ];

        lock (_syncLock)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            DisposeSignalRegistrationsUnderLock();

            foreach (var signal in signals)
            {
                try
                {
                    var registration = PosixSignalRegistration.Create(
                        signal,
                        context => HandlePosixSignal(context.Signal));
                    _signalRegistrations.Add(registration);
                }
                catch (Exception ex) when (ex is PlatformNotSupportedException or IOException)
                {
                    _logger.LogDebug(ex, "OS signal {Signal} is not supported on this platform.", signal);
                }
            }
        }
    }

    private void DisposeSignalRegistrationsUnderLock()
    {
        foreach (var registration in _signalRegistrations)
        {
            registration.Dispose();
        }

        _signalRegistrations.Clear();
    }

    private DateTimeOffset EnsureStoppingTimestampUnderLock()
    {
        if (_stoppingTimestampTicks == long.MinValue)
        {
            _stoppingTimestampTicks = _timeProvider.GetUtcNow().UtcTicks;
        }

        return new DateTimeOffset(_stoppingTimestampTicks, TimeSpan.Zero);
    }

    private DateTimeOffset? TryGetStoppingTimestampUnderLock()
    {
        return _stoppingTimestampTicks == long.MinValue ? null : new DateTimeOffset(_stoppingTimestampTicks, TimeSpan.Zero);
    }

    private DateTimeOffset GetEffectiveStartTimestamp()
    {
        var ticks = Interlocked.Read(ref _startupTimestampTicks);
        return ticks != 0 ? new DateTimeOffset(ticks, TimeSpan.Zero) : _hostInfo.ProcessStartedAtUtc;
    }

    private string FormatUptime()
    {
        var elapsed = _timeProvider.GetUtcNow() - GetEffectiveStartTimestamp();
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{(long)elapsed.TotalHours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}.{elapsed.Milliseconds:D3}");
    }

    private InstanceLifecycleState CreateState(
        InstanceLifecycleStatus status,
        DateTimeOffset? stoppingAtUtc,
        DateTimeOffset? stoppedAtUtc,
        string? shutdownReason,
        int? exitCode,
        string? exceptionType = null,
        string? exceptionMessage = null)
    {
        return new InstanceLifecycleState
        {
            ProcessId = _hostInfo.ProcessId,
            SessionId = _hostInfo.SessionId,
            Version = _hostInfo.Version,
            EnvironmentName = _hostInfo.EnvironmentName,
            ContentRootPath = _hostInfo.ContentRootPath,
            ProcessStartedAtUtc = _hostInfo.ProcessStartedAtUtc,
            RecordedStartedAtUtc = GetEffectiveStartTimestamp(),
            StoppingAtUtc = stoppingAtUtc,
            StoppedAtUtc = stoppedAtUtc,
            Status = status,
            ShutdownReason = shutdownReason,
            ExitCode = exitCode,
            ExceptionType = exceptionType,
            ExceptionMessage = exceptionMessage
        };
    }

    private bool TryPersistStateUnderLock(InstanceLifecycleState state, out Exception? writeError)
    {
        return _stateStore.TryWriteState(state, out writeError);
    }

    private void LogWriteFailureIfAny(InstanceLifecycleStatus status, Exception? error)
    {
        if (error is not null)
        {
            _logger.LogWarning(
                error,
                "Failed to persist lifecycle state ({Status}) to {StateFilePath}.",
                status,
                _stateStore.StateFilePath);
        }
    }
}
