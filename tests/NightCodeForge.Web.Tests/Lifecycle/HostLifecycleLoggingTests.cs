namespace NightCodeForge.Web.Tests.Lifecycle;

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using NightCodeForge.Infrastructure.Configuration;
using NightCodeForge.Web.Lifecycle;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;
using Xunit;

public class HostLifecycleLoggingTests : IDisposable
{
    private readonly string _tempDir;

    public HostLifecycleLoggingTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ncf_lifecycle_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Justification: Best-effort cleanup of isolated test temp folder on Windows file handle release timing.
            _ = ex;
        }
    }

    [Fact]
    public void ResolveLogsDirectory_HandlesDefaultRelativeAndRootedPaths()
    {
        var baseDir = Path.Combine(_tempDir, "base");
        Directory.CreateDirectory(baseDir);

        var defaultConfig = new ConfigurationBuilder().Build();
        var resolvedDefault = HostLoggingConfigurator.ResolveLogsDirectory(defaultConfig, baseDir);
        Assert.Equal(Path.Combine(baseDir, HostLoggingConfigurator.DefaultLogsDirectoryName), resolvedDefault);
        Assert.True(Directory.Exists(resolvedDefault));

        var relativeConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [HostLoggingConfigurator.LogsDirectoryConfigKey] = "custom_logs"
            })
            .Build();
        var resolvedRelative = HostLoggingConfigurator.ResolveLogsDirectory(relativeConfig, baseDir);
        Assert.Equal(Path.Combine(baseDir, "custom_logs"), resolvedRelative);
        Assert.True(Directory.Exists(resolvedRelative));

        var rootedPath = Path.Combine(_tempDir, "absolute_logs");
        var rootedConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [HostLoggingConfigurator.LogsDirectoryConfigKey] = rootedPath
            })
            .Build();
        var resolvedRooted = HostLoggingConfigurator.ResolveLogsDirectory(rootedConfig, baseDir);
        Assert.Equal(rootedPath, resolvedRooted);
        Assert.True(Directory.Exists(resolvedRooted));
    }

    [Theory]
    [InlineData(LogEventLevel.Information, HostLoggingConfigurator.LifecycleSourceContextPrefix, true)]
    [InlineData(LogEventLevel.Warning, HostLoggingConfigurator.LifecycleSourceContextPrefix + ".HostLifecycleMonitor", true)]
    [InlineData(LogEventLevel.Error, HostLoggingConfigurator.LifecycleSourceContextPrefix + ".HostLifecycleMonitor", true)]
    [InlineData(LogEventLevel.Fatal, HostLoggingConfigurator.LifecycleSourceContextPrefix + ".HostLifecycleMonitor", true)]
    [InlineData(LogEventLevel.Debug, HostLoggingConfigurator.LifecycleSourceContextPrefix, false)]
    [InlineData(LogEventLevel.Verbose, HostLoggingConfigurator.LifecycleSourceContextPrefix + ".HostLifecycleMonitor", false)]
    [InlineData(LogEventLevel.Fatal, "NightCodeForge.Agents.SomeService", true)]
    [InlineData(LogEventLevel.Fatal, null, true)]
    [InlineData(LogEventLevel.Warning, HostLoggingConfigurator.HostingLifetimeSourceContext, true)]
    [InlineData(LogEventLevel.Error, HostLoggingConfigurator.HostingLifetimeSourceContext, true)]
    [InlineData(LogEventLevel.Information, HostLoggingConfigurator.HostingLifetimeSourceContext, false)]
    [InlineData(LogEventLevel.Information, "NightCodeForge.Agents.SomeService", false)]
    [InlineData(LogEventLevel.Warning, "NightCodeForge.Agents.SomeService", false)]
    [InlineData(LogEventLevel.Error, "NightCodeForge.Agents.SomeService", false)]
    [InlineData(LogEventLevel.Information, "Microsoft.AspNetCore.Routing", false)]
    public void ShouldWriteToConsole_FiltersLifecycleAndFatalEventsOnly(
        LogEventLevel level,
        string? sourceContext,
        bool expected)
    {
        var properties = sourceContext is null
            ? Array.Empty<LogEventProperty>()
            : [new LogEventProperty(Serilog.Core.Constants.SourceContextPropertyName, new ScalarValue(sourceContext))];

        var logEvent = new LogEvent(
            DateTimeOffset.UtcNow,
            level,
            exception: null,
            new MessageTemplate([]),
            properties);

        Assert.Equal(expected, HostLoggingConfigurator.ShouldWriteToConsole(logEvent));
    }

    [Fact]
    public void Configure_RoutesLifecycleToConsoleAndFile_WhileOperationalLogsGoOnlyToFile()
    {
        var logsDir = Path.Combine(_tempDir, "pipeline_logs");
        Directory.CreateDirectory(logsDir);
        var logFilePattern = HostLoggingConfigurator.GetLogFilePath(logsDir);
        var consoleSink = new CapturingSerilogSink();
        var config = new ConfigurationBuilder().Build();

        using (var logger = HostLoggingConfigurator.Configure(
            new LoggerConfiguration(),
            config,
            services: null,
            logFilePattern,
            consoleLc => consoleLc.WriteTo.Sink(consoleSink)).CreateLogger())
        {
            logger.ForContext(Serilog.Core.Constants.SourceContextPropertyName, typeof(HostLifecycleMonitor).FullName)
                .Information("Lifecycle startup probe");
            logger.ForContext(Serilog.Core.Constants.SourceContextPropertyName, "NightCodeForge.Infrastructure.SomeWorker")
                .Information("Operational info probe");
            logger.ForContext(Serilog.Core.Constants.SourceContextPropertyName, "NightCodeForge.Infrastructure.SomeWorker")
                .Fatal("Operational fatal probe");
        }

        Assert.Equal(2, consoleSink.Events.Count);
        Assert.Contains(consoleSink.Events, e => e.RenderMessage().Contains("Lifecycle startup probe", StringComparison.Ordinal));
        Assert.Contains(consoleSink.Events, e => e.RenderMessage().Contains("Operational fatal probe", StringComparison.Ordinal));
        Assert.DoesNotContain(consoleSink.Events, e => e.RenderMessage().Contains("Operational info probe", StringComparison.Ordinal));

        var logFiles = Directory.GetFiles(logsDir, "nightcodeforge-*.log");
        Assert.Single(logFiles);
        var fileContent = File.ReadAllText(logFiles[0]);
        Assert.Contains("Lifecycle startup probe", fileContent, StringComparison.Ordinal);
        Assert.Contains("Operational info probe", fileContent, StringComparison.Ordinal);
        Assert.Contains("Operational fatal probe", fileContent, StringComparison.Ordinal);
    }

    [Fact]
    public void StateStore_InspectPreviousRun_ReturnsNoneWhenFileDoesNotExist()
    {
        var store = CreateStateStore(new FakeProcessLivenessChecker(_ => false));

        var assessment = store.InspectPreviousRun(currentProcessId: 999);

        Assert.Equal(PreviousInstanceAssessmentKind.None, assessment.Kind);
        Assert.Null(assessment.PreviousState);
        Assert.Null(assessment.DiagnosticError);
    }

    [Theory]
    [InlineData(InstanceLifecycleStatus.StoppedCleanly, PreviousInstanceAssessmentKind.StoppedCleanly)]
    [InlineData(InstanceLifecycleStatus.Crashed, PreviousInstanceAssessmentKind.Crashed)]
    [InlineData(InstanceLifecycleStatus.ExitedWithoutGracefulShutdown, PreviousInstanceAssessmentKind.ExitedWithoutGracefulShutdown)]
    public void StateStore_InspectPreviousRun_PreservesTerminalStatuses(
        InstanceLifecycleStatus persistedStatus,
        PreviousInstanceAssessmentKind expectedKind)
    {
        var store = CreateStateStore(new FakeProcessLivenessChecker(_ => false));
        var state = CreateSampleState(pid: 4242, persistedStatus) with
        {
            ShutdownReason = "Test reason",
            ExitCode = persistedStatus == InstanceLifecycleStatus.StoppedCleanly ? 0 : 1,
            ExceptionType = persistedStatus == InstanceLifecycleStatus.Crashed ? "System.InvalidOperationException" : null,
            StoppedAtUtc = DateTimeOffset.UtcNow
        };
        Assert.True(store.TryWriteState(state, out var writeError));
        Assert.Null(writeError);

        var assessment = store.InspectPreviousRun(currentProcessId: 5000);

        Assert.Equal(expectedKind, assessment.Kind);
        Assert.NotNull(assessment.PreviousState);
        Assert.Equal(4242, assessment.PreviousState.ProcessId);
        Assert.Equal("Test reason", assessment.PreviousState.ShutdownReason);
        Assert.Equal(state.ExceptionType, assessment.PreviousState.ExceptionType);
    }

    [Fact]
    public void StateStore_InspectPreviousRun_DetectsUncleanTerminationWhenPreviousProcessDead()
    {
        var store = CreateStateStore(new FakeProcessLivenessChecker(_ => false));
        var state = CreateSampleState(pid: 4242, InstanceLifecycleStatus.Running);
        Assert.True(store.TryWriteState(state, out _));

        var assessment = store.InspectPreviousRun(currentProcessId: 5000);

        Assert.Equal(PreviousInstanceAssessmentKind.UncleanTerminationDetected, assessment.Kind);
        Assert.NotNull(assessment.PreviousState);
        Assert.Equal(4242, assessment.PreviousState.ProcessId);
    }

    [Fact]
    public void StateStore_InspectPreviousRun_DetectsAnotherRunningInstanceWhenPreviousProcessAlive()
    {
        var store = CreateStateStore(new FakeProcessLivenessChecker(pid => pid == 4242));
        var state = CreateSampleState(pid: 4242, InstanceLifecycleStatus.Running);
        Assert.True(store.TryWriteState(state, out _));

        var assessment = store.InspectPreviousRun(currentProcessId: 5000);

        Assert.Equal(PreviousInstanceAssessmentKind.AnotherInstanceStillRunning, assessment.Kind);
    }

    [Fact]
    public void StateStore_InspectPreviousRun_DetectsTerminatedDuringShutdownWhenStoppingProcessDead()
    {
        var store = CreateStateStore(new FakeProcessLivenessChecker(_ => false));
        var state = CreateSampleState(pid: 4242, InstanceLifecycleStatus.Stopping) with
        {
            StoppingAtUtc = DateTimeOffset.UtcNow,
            ShutdownReason = HostLifecycleMonitor.TriggerSigInt
        };
        Assert.True(store.TryWriteState(state, out _));

        var assessment = store.InspectPreviousRun(currentProcessId: 5000);

        Assert.Equal(PreviousInstanceAssessmentKind.TerminatedDuringShutdown, assessment.Kind);
        Assert.Equal(HostLifecycleMonitor.TriggerSigInt, assessment.PreviousState?.ShutdownReason);
    }

    [Fact]
    public void StateStore_InspectPreviousRun_FailsOpenOnCorruptedJson()
    {
        var store = CreateStateStore(new FakeProcessLivenessChecker(_ => false));
        File.WriteAllText(store.StateFilePath, "{ corrupted json ");

        var assessment = store.InspectPreviousRun(currentProcessId: 5000);

        Assert.Equal(PreviousInstanceAssessmentKind.CorruptedStateFile, assessment.Kind);
        Assert.Null(assessment.PreviousState);
        Assert.False(string.IsNullOrWhiteSpace(assessment.DiagnosticError));
    }

    [Fact]
    public void StateStore_TryWriteState_CreatesParentDirectoriesAndWritesIndentedStringEnums()
    {
        var nestedPath = Path.Combine(_tempDir, "nested", "logs", InstanceLifecycleStateStore.StateFileName);
        var store = new InstanceLifecycleStateStore(nestedPath, new FakeProcessLivenessChecker(_ => false));
        var state = CreateSampleState(pid: 1234, InstanceLifecycleStatus.StoppedCleanly);

        var written = store.TryWriteState(state, out var error);

        Assert.True(written);
        Assert.Null(error);
        var rawJson = File.ReadAllText(nestedPath);
        Assert.Contains("\"Status\": \"StoppedCleanly\"", rawJson, StringComparison.Ordinal);
        Assert.Contains("\n", rawJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Monitor_CleanStartupListeningSignalAndShutdown_LogsAndPersistsFullLifecycle()
    {
        var store = CreateStateStore(new FakeProcessLivenessChecker(_ => false));
        var logger = new CapturingLogger<HostLifecycleMonitor>();
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 11, 20, 22, 0, 0, TimeSpan.Zero));
        var flushCalls = 0;
        var lifetime = new FakeHostApplicationLifetime();
        var server = CreateServerMock("http://localhost:5270", "https://localhost:5271");
        var consoleLines = new List<string>();

        using var monitor = new HostLifecycleMonitor(
            store,
            CreateHostInfo(),
            logger,
            timeProvider,
            () => flushCalls++,
            consoleLines.Add);

        monitor.BeginStartup();
        monitor.AttachHostLifetime(lifetime, server);

        timeProvider.Advance(TimeSpan.FromMilliseconds(850));
        lifetime.TriggerStarted();

        Assert.Contains("✅ NightCodeForge is running : http://localhost:5270, https://localhost:5271", consoleLines);

        timeProvider.Advance(TimeSpan.FromSeconds(10));
        monitor.HandlePosixSignal(PosixSignal.SIGINT);
        lifetime.TriggerStopping();

        timeProvider.Advance(TimeSpan.FromMilliseconds(250));
        lifetime.TriggerStopped();

        var afterStopAssessment = store.InspectPreviousRun(currentProcessId: 99999);
        Assert.Equal(PreviousInstanceAssessmentKind.StoppedCleanly, afterStopAssessment.Kind);
        Assert.Equal(HostLifecycleMonitor.TriggerSigInt, afterStopAssessment.PreviousState?.ShutdownReason);
        Assert.Equal(0, afterStopAssessment.PreviousState?.ExitCode);

        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Information &&
            e.Message.Contains("NightCodeForge instance starting", StringComparison.Ordinal) &&
            e.Message.Contains("1.2.3", StringComparison.Ordinal));

        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Information &&
            e.Message.Contains("NightCodeForge started and listening on http://localhost:5270, https://localhost:5271", StringComparison.Ordinal) &&
            e.Message.Contains("StartupDurationMs: 850", StringComparison.Ordinal));

        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Information &&
            e.Message.Contains("Shutdown signal received: SIGINT", StringComparison.Ordinal) &&
            e.Message.Contains(HostLifecycleMonitor.TriggerSigInt, StringComparison.Ordinal));

        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Information &&
            e.Message.Contains("NightCodeForge is stopping", StringComparison.Ordinal) &&
            e.Message.Contains(HostLifecycleMonitor.TriggerSigInt, StringComparison.Ordinal));

        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Information &&
            e.Message.Contains("NightCodeForge stopped cleanly", StringComparison.Ordinal) &&
            e.Message.Contains(HostLifecycleMonitor.TriggerSigInt, StringComparison.Ordinal) &&
            e.Message.Contains("ShutdownDurationMs: 250", StringComparison.Ordinal));

        Assert.Equal(0, flushCalls);
    }

    [Fact]
    public void Monitor_StopApplicationWithoutSignal_RecordsStopApplicationTrigger()
    {
        var store = CreateStateStore(new FakeProcessLivenessChecker(_ => false));
        var logger = new CapturingLogger<HostLifecycleMonitor>();
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 11, 20, 22, 0, 0, TimeSpan.Zero));
        var lifetime = new FakeHostApplicationLifetime();

        using var monitor = new HostLifecycleMonitor(store, CreateHostInfo(), logger, timeProvider, () => { }, _ => { });
        monitor.BeginStartup();
        monitor.AttachHostLifetime(lifetime, CreateServerMock("http://localhost:5270"));
        lifetime.TriggerStarted();

        lifetime.StopApplication();
        lifetime.TriggerStopping();
        lifetime.TriggerStopped();

        var assessment = store.InspectPreviousRun(currentProcessId: 99999);
        Assert.Equal(PreviousInstanceAssessmentKind.StoppedCleanly, assessment.Kind);
        Assert.Equal(HostLifecycleMonitor.TriggerHostStopApplication, assessment.PreviousState?.ShutdownReason);
        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Information &&
            e.Message.Contains("NightCodeForge stopped cleanly", StringComparison.Ordinal) &&
            e.Message.Contains(HostLifecycleMonitor.TriggerHostStopApplication, StringComparison.Ordinal));
    }

    [Fact]
    public void Monitor_FormatPosixSignalReasonAndSigHup_HandlesSignalsAndPersistsStoppingImmediately()
    {
        Assert.Equal(HostLifecycleMonitor.TriggerSigInt, HostLifecycleMonitor.FormatPosixSignalReason(PosixSignal.SIGINT, isWindows: true));
        Assert.Equal(HostLifecycleMonitor.TriggerSigQuit, HostLifecycleMonitor.FormatPosixSignalReason(PosixSignal.SIGQUIT, isWindows: true));
        Assert.Equal(HostLifecycleMonitor.TriggerSigTermWindows, HostLifecycleMonitor.FormatPosixSignalReason(PosixSignal.SIGTERM, isWindows: true));
        Assert.Equal(HostLifecycleMonitor.TriggerSigTermUnix, HostLifecycleMonitor.FormatPosixSignalReason(PosixSignal.SIGTERM, isWindows: false));
        Assert.Equal(HostLifecycleMonitor.TriggerSigHupWindows, HostLifecycleMonitor.FormatPosixSignalReason(PosixSignal.SIGHUP, isWindows: true));
        Assert.Equal(HostLifecycleMonitor.TriggerSigHupUnix, HostLifecycleMonitor.FormatPosixSignalReason(PosixSignal.SIGHUP, isWindows: false));

        var store = CreateStateStore(new FakeProcessLivenessChecker(_ => false));
        var logger = new CapturingLogger<HostLifecycleMonitor>();
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 11, 20, 22, 0, 0, TimeSpan.Zero));

        using var monitor = new HostLifecycleMonitor(store, CreateHostInfo(), logger, timeProvider, () => { }, _ => { });
        monitor.BeginStartup();
        monitor.HandlePosixSignal(PosixSignal.SIGHUP);

        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Warning &&
            e.Message.Contains("Shutdown signal received: SIGHUP", StringComparison.Ordinal));

        var assessment = store.InspectPreviousRun(currentProcessId: 99999);
        Assert.Equal(PreviousInstanceAssessmentKind.TerminatedDuringShutdown, assessment.Kind);
        Assert.Equal(
            HostLifecycleMonitor.FormatPosixSignalReason(PosixSignal.SIGHUP, OperatingSystem.IsWindows()),
            assessment.PreviousState?.ShutdownReason);
    }

    [Fact]
    public void Monitor_BeginStartup_LogsWarningsForPreviousUncleanAndStoppingTerminationsAndCleanInfo()
    {
        var store = CreateStateStore(new FakeProcessLivenessChecker(_ => false));
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 11, 20, 22, 0, 0, TimeSpan.Zero));

        // 1. Unclean termination while Running
        Assert.True(store.TryWriteState(CreateSampleState(1111, InstanceLifecycleStatus.Running), out _));
        var logger1 = new CapturingLogger<HostLifecycleMonitor>();
        using (var monitor1 = new HostLifecycleMonitor(store, CreateHostInfo(), logger1, timeProvider, () => { }, _ => { }))
        {
            monitor1.BeginStartup();
        }
        Assert.Contains(logger1.Entries, e =>
            e.Level == LogLevel.Warning &&
            e.Message.Contains("terminated unexpectedly while Running", StringComparison.Ordinal) &&
            e.Message.Contains("Windows Event Viewer", StringComparison.Ordinal));

        // 2. Terminated during shutdown
        Assert.True(store.TryWriteState(CreateSampleState(2222, InstanceLifecycleStatus.Stopping) with
        {
            StoppingAtUtc = timeProvider.GetUtcNow(),
            ShutdownReason = HostLifecycleMonitor.TriggerSigTermWindows
        }, out _));
        var logger2 = new CapturingLogger<HostLifecycleMonitor>();
        using (var monitor2 = new HostLifecycleMonitor(store, CreateHostInfo(), logger2, timeProvider, () => { }, _ => { }))
        {
            monitor2.BeginStartup();
        }
        Assert.Contains(logger2.Entries, e =>
            e.Level == LogLevel.Warning &&
            e.Message.Contains("began shutting down", StringComparison.Ordinal) &&
            e.Message.Contains(HostLifecycleMonitor.TriggerSigTermWindows, StringComparison.Ordinal));

        // 3. Stopped cleanly
        Assert.True(store.TryWriteState(CreateSampleState(3333, InstanceLifecycleStatus.StoppedCleanly) with
        {
            StoppedAtUtc = timeProvider.GetUtcNow(),
            ShutdownReason = HostLifecycleMonitor.TriggerSigInt
        }, out _));
        var logger3 = new CapturingLogger<HostLifecycleMonitor>();
        using (var monitor3 = new HostLifecycleMonitor(store, CreateHostInfo(), logger3, timeProvider, () => { }, _ => { }))
        {
            monitor3.BeginStartup();
        }
        Assert.Contains(logger3.Entries, e =>
            e.Level == LogLevel.Information &&
            e.Message.Contains("Previous instance stopped cleanly", StringComparison.Ordinal) &&
            e.Message.Contains(HostLifecycleMonitor.TriggerSigInt, StringComparison.Ordinal));
    }

    [Fact]
    public void Monitor_FailureAndAbortedHandlers_RecordAndLogExpectedDiagnostics()
    {
        var store = CreateStateStore(new FakeProcessLivenessChecker(_ => false));
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 11, 20, 22, 0, 0, TimeSpan.Zero));

        // 1. Fatal startup/host exception
        var logger1 = new CapturingLogger<HostLifecycleMonitor>();
        using (var monitor1 = new HostLifecycleMonitor(store, CreateHostInfo(), logger1, timeProvider, () => { }, _ => { }))
        {
            monitor1.BeginStartup();
            monitor1.RecordFatalStartupOrHostException(new InvalidOperationException("Boom startup"));
        }
        Assert.Contains(logger1.Entries, e =>
            e.Level == LogLevel.Critical &&
            e.Exception is InvalidOperationException &&
            e.Message.Contains("fatal startup or host exception", StringComparison.Ordinal));
        var crashed = store.InspectPreviousRun(currentProcessId: 99999);
        Assert.Equal(PreviousInstanceAssessmentKind.Crashed, crashed.Kind);
        Assert.Equal(typeof(InvalidOperationException).FullName, crashed.PreviousState?.ExceptionType);
        Assert.Equal("Boom startup", crashed.PreviousState?.ExceptionMessage);

        // 2. HostAbortedException (design-time tooling)
        var logger2 = new CapturingLogger<HostLifecycleMonitor>();
        using (var monitor2 = new HostLifecycleMonitor(store, CreateHostInfo(), logger2, timeProvider, () => { }, _ => { }))
        {
            monitor2.BeginStartup();
            monitor2.RecordHostAborted();
        }
        Assert.Contains(logger2.Entries, e =>
            e.Level == LogLevel.Information &&
            e.Message.Contains("HostAbortedException", StringComparison.Ordinal));
        Assert.DoesNotContain(logger2.Entries, e => e.Level >= LogLevel.Error);
        var aborted = store.InspectPreviousRun(currentProcessId: 99999);
        Assert.Equal(PreviousInstanceAssessmentKind.StoppedCleanly, aborted.Kind);
        Assert.Equal(HostLifecycleMonitor.TriggerHostAborted, aborted.PreviousState?.ShutdownReason);

        // 3. Unhandled terminating AppDomain exception + UnobservedTaskException
        var logger3 = new CapturingLogger<HostLifecycleMonitor>();
        var flushCalls3 = 0;
        using (var monitor3 = new HostLifecycleMonitor(store, CreateHostInfo(), logger3, timeProvider, () => flushCalls3++, _ => { }))
        {
            monitor3.BeginStartup();
            monitor3.HandleUnobservedTaskException(new AggregateException(new TimeoutException("Background task failed")));
            monitor3.HandleUnhandledException(new ApplicationException("Unhandled domain crash"), isTerminating: true);
        }
        Assert.Contains(logger3.Entries, e =>
            e.Level == LogLevel.Error &&
            e.Exception is AggregateException &&
            e.Message.Contains("Unobserved Task exception", StringComparison.Ordinal));
        Assert.Contains(logger3.Entries, e =>
            e.Level == LogLevel.Critical &&
            e.Exception is ApplicationException &&
            e.Message.Contains("Unhandled AppDomain exception detected", StringComparison.Ordinal));
        Assert.Equal(1, flushCalls3);
        var unhandledCrashed = store.InspectPreviousRun(currentProcessId: 99999);
        Assert.Equal(PreviousInstanceAssessmentKind.Crashed, unhandledCrashed.Kind);
        Assert.Equal(typeof(ApplicationException).FullName, unhandledCrashed.PreviousState?.ExceptionType);
    }

    [Fact]
    public void Monitor_HandleProcessExit_DifferentiatesCleanStopFromUngracefulExit()
    {
        var store = CreateStateStore(new FakeProcessLivenessChecker(_ => false));
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 11, 20, 22, 0, 0, TimeSpan.Zero));

        // ProcessExit without clean host stop
        var loggerUngraceful = new CapturingLogger<HostLifecycleMonitor>();
        var flushUngraceful = 0;
        using (var monitor = new HostLifecycleMonitor(store, CreateHostInfo(), loggerUngraceful, timeProvider, () => flushUngraceful++, _ => { }))
        {
            monitor.BeginStartup();
            monitor.HandleProcessExit(exitCode: 42);
        }
        Assert.Equal(1, flushUngraceful);
        Assert.Contains(loggerUngraceful.Entries, e =>
            e.Level == LogLevel.Warning &&
            e.Message.Contains("Process exiting via AppDomain.ProcessExit", StringComparison.Ordinal) &&
            e.Message.Contains("ExitCode: 42", StringComparison.Ordinal));
        var ungracefulAssessment = store.InspectPreviousRun(currentProcessId: 99999);
        Assert.Equal(PreviousInstanceAssessmentKind.ExitedWithoutGracefulShutdown, ungracefulAssessment.Kind);
        Assert.Equal(42, ungracefulAssessment.PreviousState?.ExitCode);

        // ProcessExit AFTER clean stop preserves StoppedCleanly and still flushes
        var cleanStore = new InstanceLifecycleStateStore(
            Path.Combine(_tempDir, "clean_process_exit_" + InstanceLifecycleStateStore.StateFileName),
            new FakeProcessLivenessChecker(_ => false));
        var loggerClean = new CapturingLogger<HostLifecycleMonitor>();
        var flushClean = 0;
        var lifetime = new FakeHostApplicationLifetime();
        using (var monitor = new HostLifecycleMonitor(cleanStore, CreateHostInfo(), loggerClean, timeProvider, () => flushClean++, _ => { }))
        {
            monitor.BeginStartup();
            monitor.AttachHostLifetime(lifetime, CreateServerMock("http://localhost:5270"));
            lifetime.TriggerStarted();
            lifetime.TriggerStopping();
            lifetime.TriggerStopped();
            monitor.HandleProcessExit(exitCode: 0);
        }
        Assert.Equal(1, flushClean);
        Assert.DoesNotContain(loggerClean.Entries, e => e.Level == LogLevel.Warning);
        var cleanAssessment = cleanStore.InspectPreviousRun(currentProcessId: 99999);
        Assert.Equal(PreviousInstanceAssessmentKind.StoppedCleanly, cleanAssessment.Kind);
    }

    [Fact]
    public void ResolveApplicationVersion_PrefersVersionFileAndFallsBackToAssemblyMetadata()
    {
        var contentRoot = Path.Combine(_tempDir, "version_root");
        Directory.CreateDirectory(contentRoot);

        var fallbackVersion = HostLifecycleMonitor.ResolveApplicationVersion(contentRoot, typeof(HostLifecycleMonitor).Assembly);
        Assert.False(string.IsNullOrWhiteSpace(fallbackVersion));
        Assert.NotEqual(HostLifecycleMonitor.UnknownVersion, fallbackVersion);

        File.WriteAllText(Path.Combine(contentRoot, HostLifecycleMonitor.VersionFileName), "  2026.11.20+build99 \n");
        var fileVersion = HostLifecycleMonitor.ResolveApplicationVersion(contentRoot, typeof(HostLifecycleMonitor).Assembly);
        Assert.Equal("2026.11.20+build99", fileVersion);
    }

    [Fact]
    public void Monitor_TerminalStateIsMonotonic_LateSignalsAndStoppingDoNotOverwriteTerminalState()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 11, 20, 22, 0, 0, TimeSpan.Zero));
        var store1 = new InstanceLifecycleStateStore(
            Path.Combine(_tempDir, "monotonic_clean.json"),
            new FakeProcessLivenessChecker(_ => false));
        var logger1 = new CapturingLogger<HostLifecycleMonitor>();
        var lifetime = new FakeHostApplicationLifetime();

        using (var monitor = new HostLifecycleMonitor(store1, CreateHostInfo(), logger1, timeProvider, () => { }, _ => { }))
        {
            monitor.BeginStartup();
            monitor.AttachHostLifetime(lifetime, CreateServerMock("http://localhost:5270"));
            lifetime.TriggerStarted();
            lifetime.TriggerStopping();
            lifetime.TriggerStopped();

            // Late signals/stopping events after StoppedCleanly must not revert state to Stopping or Crashed
            monitor.HandlePosixSignal(PosixSignal.SIGHUP);
            monitor.RecordFatalStartupOrHostException(new InvalidOperationException("Late error"));
            monitor.HandleProcessExit(exitCode: 99);
        }

        var cleanAssessment = store1.InspectPreviousRun(currentProcessId: 99999);
        Assert.Equal(PreviousInstanceAssessmentKind.StoppedCleanly, cleanAssessment.Kind);
        Assert.Equal(HostLifecycleMonitor.TriggerHostStopApplication, cleanAssessment.PreviousState?.ShutdownReason);
        Assert.Equal(0, cleanAssessment.PreviousState?.ExitCode);

        var store2 = new InstanceLifecycleStateStore(
            Path.Combine(_tempDir, "monotonic_crashed.json"),
            new FakeProcessLivenessChecker(_ => false));
        var logger2 = new CapturingLogger<HostLifecycleMonitor>();
        using (var monitor2 = new HostLifecycleMonitor(store2, CreateHostInfo(), logger2, timeProvider, () => { }, _ => { }))
        {
            monitor2.BeginStartup();
            monitor2.RecordFatalStartupOrHostException(new InvalidOperationException("Startup boom"));
            monitor2.HandlePosixSignal(PosixSignal.SIGTERM);
            monitor2.MarkRunCompleted();
        }

        var crashedAssessment = store2.InspectPreviousRun(currentProcessId: 99999);
        Assert.Equal(PreviousInstanceAssessmentKind.Crashed, crashedAssessment.Kind);
        Assert.Equal(typeof(InvalidOperationException).FullName, crashedAssessment.PreviousState?.ExceptionType);
    }

    [Fact]
    public void Monitor_MultipleSignalsAndStoppingRace_PreservesFirstSignalAndUpgradesHostStopTrigger()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 11, 20, 22, 0, 0, TimeSpan.Zero));

        // Scenario A: SIGINT followed by SIGTERM preserves SIGINT trigger in Stopping and StoppedCleanly
        var storeA = new InstanceLifecycleStateStore(
            Path.Combine(_tempDir, "multi_signal_a.json"),
            new FakeProcessLivenessChecker(_ => false));
        var lifetimeA = new FakeHostApplicationLifetime();
        using (var monitorA = new HostLifecycleMonitor(storeA, CreateHostInfo(), new CapturingLogger<HostLifecycleMonitor>(), timeProvider, () => { }, _ => { }))
        {
            monitorA.BeginStartup();
            monitorA.AttachHostLifetime(lifetimeA, CreateServerMock("http://localhost:5270"));
            monitorA.HandlePosixSignal(PosixSignal.SIGINT);
            monitorA.HandlePosixSignal(PosixSignal.SIGTERM);

            var stoppingAssessment = storeA.InspectPreviousRun(currentProcessId: 99999);
            Assert.Equal(PreviousInstanceAssessmentKind.TerminatedDuringShutdown, stoppingAssessment.Kind);
            Assert.Equal(HostLifecycleMonitor.TriggerSigInt, stoppingAssessment.PreviousState?.ShutdownReason);

            lifetimeA.TriggerStopping();
            lifetimeA.TriggerStopped();
        }

        var finalA = storeA.InspectPreviousRun(currentProcessId: 99999);
        Assert.Equal(PreviousInstanceAssessmentKind.StoppedCleanly, finalA.Kind);
        Assert.Equal(HostLifecycleMonitor.TriggerSigInt, finalA.PreviousState?.ShutdownReason);

        // Scenario B: ApplicationStopping fires slightly before HandlePosixSignal(SIGINT) -> upgrades generic trigger to SIGINT
        var storeB = new InstanceLifecycleStateStore(
            Path.Combine(_tempDir, "multi_signal_b.json"),
            new FakeProcessLivenessChecker(_ => false));
        var lifetimeB = new FakeHostApplicationLifetime();
        using (var monitorB = new HostLifecycleMonitor(storeB, CreateHostInfo(), new CapturingLogger<HostLifecycleMonitor>(), timeProvider, () => { }, _ => { }))
        {
            monitorB.BeginStartup();
            monitorB.AttachHostLifetime(lifetimeB, CreateServerMock("http://localhost:5270"));
            lifetimeB.TriggerStopping();
            monitorB.HandlePosixSignal(PosixSignal.SIGINT);

            var stoppingAssessment = storeB.InspectPreviousRun(currentProcessId: 99999);
            Assert.Equal(PreviousInstanceAssessmentKind.TerminatedDuringShutdown, stoppingAssessment.Kind);
            Assert.Equal(HostLifecycleMonitor.TriggerSigInt, stoppingAssessment.PreviousState?.ShutdownReason);

            lifetimeB.TriggerStopped();
        }

        var finalB = storeB.InspectPreviousRun(currentProcessId: 99999);
        Assert.Equal(PreviousInstanceAssessmentKind.StoppedCleanly, finalB.Kind);
        Assert.Equal(HostLifecycleMonitor.TriggerSigInt, finalB.PreviousState?.ShutdownReason);
    }

    [Fact]
    public void Monitor_NonTerminatingUnhandledException_LogsCriticalWithoutRecordingCrashOrFlushing()
    {
        var store = CreateStateStore(new FakeProcessLivenessChecker(_ => false));
        var logger = new CapturingLogger<HostLifecycleMonitor>();
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 11, 20, 22, 0, 0, TimeSpan.Zero));
        var flushCalls = 0;

        using (var monitor = new HostLifecycleMonitor(store, CreateHostInfo(), logger, timeProvider, () => flushCalls++, _ => { }))
        {
            monitor.BeginStartup();
            monitor.HandleUnhandledException(new InvalidOperationException("Non-terminating"), isTerminating: false);
        }

        Assert.Equal(0, flushCalls);
        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Critical &&
            e.Message.Contains("IsTerminating: False", StringComparison.Ordinal));
        var assessment = store.InspectPreviousRun(currentProcessId: 99999);
        Assert.Equal(PreviousInstanceAssessmentKind.UncleanTerminationDetected, assessment.Kind);
    }

    [Fact]
    public void Monitor_BeginStartup_LogsRemainingPreviousAssessmentsAndInvalidUserConfig()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 11, 20, 22, 0, 0, TimeSpan.Zero));
        var store = CreateStateStore(new FakeProcessLivenessChecker(pid => pid == 7777));

        // 1. Crashed + InvalidJson user config
        Assert.True(store.TryWriteState(CreateSampleState(1111, InstanceLifecycleStatus.Crashed) with
        {
            StoppedAtUtc = timeProvider.GetUtcNow(),
            ExceptionType = "System.Exception",
            ShutdownReason = "Crash"
        }, out _));
        var logger1 = new CapturingLogger<HostLifecycleMonitor>();
        var invalidConfigInfo = CreateHostInfo() with { UserConfigStatus = UserConfigLoadStatus.InvalidJson };
        using (var m1 = new HostLifecycleMonitor(store, invalidConfigInfo, logger1, timeProvider, () => { }, _ => { }))
        {
            m1.BeginStartup();
        }
        Assert.Contains(logger1.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("contains invalid JSON", StringComparison.Ordinal));
        Assert.Contains(logger1.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("crashed with an unhandled exception", StringComparison.Ordinal));

        // 2. ExitedWithoutGracefulShutdown
        Assert.True(store.TryWriteState(CreateSampleState(2222, InstanceLifecycleStatus.ExitedWithoutGracefulShutdown) with
        {
            StoppedAtUtc = timeProvider.GetUtcNow(),
            ExitCode = 7,
            ShutdownReason = HostLifecycleMonitor.TriggerProcessExit
        }, out _));
        var logger2 = new CapturingLogger<HostLifecycleMonitor>();
        using (var m2 = new HostLifecycleMonitor(store, CreateHostInfo(), logger2, timeProvider, () => { }, _ => { }))
        {
            m2.BeginStartup();
        }
        Assert.Contains(logger2.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("exited via ProcessExit", StringComparison.Ordinal));

        // 3. AnotherInstanceStillRunning
        Assert.True(store.TryWriteState(CreateSampleState(7777, InstanceLifecycleStatus.Running), out _));
        var logger3 = new CapturingLogger<HostLifecycleMonitor>();
        using (var m3 = new HostLifecycleMonitor(store, CreateHostInfo(), logger3, timeProvider, () => { }, _ => { }))
        {
            m3.BeginStartup();
        }
        Assert.Contains(logger3.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("appears to still be running", StringComparison.Ordinal));

        // 4. CorruptedStateFile
        File.WriteAllText(store.StateFilePath, "{ broken");
        var logger4 = new CapturingLogger<HostLifecycleMonitor>();
        using (var m4 = new HostLifecycleMonitor(store, CreateHostInfo(), logger4, timeProvider, () => { }, _ => { }))
        {
            m4.BeginStartup();
        }
        Assert.Contains(logger4.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("could not be parsed", StringComparison.Ordinal));
    }

    [Fact]
    public void Monitor_MarkRunCompleted_PersistsStoppedCleanlyAndIsIdempotent()
    {
        var store = CreateStateStore(new FakeProcessLivenessChecker(_ => false));
        var logger = new CapturingLogger<HostLifecycleMonitor>();
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 11, 20, 22, 0, 0, TimeSpan.Zero));

        using var monitor = new HostLifecycleMonitor(store, CreateHostInfo(), logger, timeProvider, () => { }, _ => { });
        monitor.BeginStartup();
        monitor.MarkRunCompleted();
        monitor.MarkRunCompleted();

        var assessment = store.InspectPreviousRun(currentProcessId: 99999);
        Assert.Equal(PreviousInstanceAssessmentKind.StoppedCleanly, assessment.Kind);
        var stoppedLogs = logger.Entries.Count(e => e.Message.Contains("NightCodeForge stopped cleanly", StringComparison.Ordinal));
        Assert.Equal(1, stoppedLogs);
    }

    [Fact]
    public void StateStore_InspectPreviousRun_HandlesNullJsonUnknownStatusSamePidAndLiveStoppingInstance()
    {
        var store = CreateStateStore(new FakeProcessLivenessChecker(_ => true));

        File.WriteAllText(store.StateFilePath, "null");
        var nullResult = store.InspectPreviousRun(currentProcessId: 5000);
        Assert.Equal(PreviousInstanceAssessmentKind.CorruptedStateFile, nullResult.Kind);

        var unknownStatusJson = """
            {
              "ProcessId": 4242,
              "SessionId": "s1",
              "Version": "1.0.0",
              "EnvironmentName": "Production",
              "ContentRootPath": "C:\\app",
              "ProcessStartedAtUtc": "2026-11-20T21:00:00Z",
              "RecordedStartedAtUtc": "2026-11-20T21:00:01Z",
              "Status": 999
            }
            """;
        File.WriteAllText(store.StateFilePath, unknownStatusJson);
        var unknownStatusResult = store.InspectPreviousRun(currentProcessId: 5000);
        Assert.Equal(PreviousInstanceAssessmentKind.CorruptedStateFile, unknownStatusResult.Kind);

        // Same PID skips liveness check even if checker returns true
        Assert.True(store.TryWriteState(CreateSampleState(pid: 5000, InstanceLifecycleStatus.Running), out _));
        var samePidResult = store.InspectPreviousRun(currentProcessId: 5000);
        Assert.Equal(PreviousInstanceAssessmentKind.UncleanTerminationDetected, samePidResult.Kind);

        // Stopping with live different PID -> AnotherInstanceStillRunning
        Assert.True(store.TryWriteState(CreateSampleState(pid: 4242, InstanceLifecycleStatus.Stopping), out _));
        var liveStoppingResult = store.InspectPreviousRun(currentProcessId: 5000);
        Assert.Equal(PreviousInstanceAssessmentKind.AnotherInstanceStillRunning, liveStoppingResult.Kind);
    }

    [Fact]
    public void SystemProcessLivenessChecker_ValidatesPidAndStartTimeTolerance()
    {
        var checker = new SystemProcessLivenessChecker();

        Assert.False(checker.IsProcessRunning(0, DateTimeOffset.UtcNow));
        Assert.False(checker.IsProcessRunning(-5, DateTimeOffset.UtcNow));
        Assert.False(checker.IsProcessRunning(int.MaxValue, DateTimeOffset.UtcNow));

        using var current = System.Diagnostics.Process.GetCurrentProcess();
        var startUtc = current.StartTime.ToUniversalTime();
        Assert.True(checker.IsProcessRunning(current.Id, startUtc));
        Assert.False(checker.IsProcessRunning(current.Id, startUtc.AddMinutes(-10)));
    }

    [Theory]
    [InlineData("http://+:5270/", "http://localhost:5270")]
    [InlineData("https://*:5271", "https://localhost:5271")]
    [InlineData("http://0.0.0.0:5270/", "http://localhost:5270")]
    [InlineData("http://[::]:5270", "http://localhost:5270")]
    [InlineData("http://[::1]:5270", "http://[::1]:5270")]
    [InlineData("http://localhost:5270/", "http://localhost:5270")]
    [InlineData("http://127.0.0.1:5270", "http://127.0.0.1:5270")]
    [InlineData("http://+:5270/sub+path/", "http://localhost:5270/sub+path")]
    public void NormalizeDisplayAddress_ReplacesWildcardsAndTrimsTrailingSlash(string rawAddress, string expected)
    {
        Assert.Equal(expected, HostLifecycleMonitor.NormalizeDisplayAddress(rawAddress));
    }

    [Fact]
    public void FormatRunningConsoleMessage_DeduplicatesNormalizedAddressesAndHandlesEmpty()
    {
        var deduplicated = HostLifecycleMonitor.FormatRunningConsoleMessage(["http://0.0.0.0:5270", "http://[::]:5270/"]);
        Assert.Equal("✅ NightCodeForge is running : http://localhost:5270", deduplicated);

        var multi = HostLifecycleMonitor.FormatRunningConsoleMessage(["http://+:5270/", "https://*:5271"]);
        Assert.Equal("✅ NightCodeForge is running : http://localhost:5270, https://localhost:5271", multi);

        Assert.Equal("✅ NightCodeForge is running : unavailable", HostLifecycleMonitor.FormatRunningConsoleMessage(null));
        Assert.Equal("✅ NightCodeForge is running : unavailable", HostLifecycleMonitor.FormatRunningConsoleMessage([]));
    }

    [Fact]
    public void Monitor_ApplicationStarted_ToleratesBrokenConsoleWriter()
    {
        var store = CreateStateStore(new FakeProcessLivenessChecker(_ => false));
        var logger = new CapturingLogger<HostLifecycleMonitor>();
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 11, 20, 22, 0, 0, TimeSpan.Zero));
        var lifetime = new FakeHostApplicationLifetime();

        using var monitor = new HostLifecycleMonitor(
            store,
            CreateHostInfo(),
            logger,
            timeProvider,
            () => { },
            _ => throw new IOException("Stdout handle closed"));

        monitor.BeginStartup();
        monitor.AttachHostLifetime(lifetime, CreateServerMock("http://localhost:5270"));
        lifetime.TriggerStarted();

        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Information &&
            e.Message.Contains("NightCodeForge started and listening on http://localhost:5270", StringComparison.Ordinal));
    }

    [Fact]
    public void ConfigureConsoleWindow_IgnoresNonInteractiveConsoleExceptions()
    {
        var titleCalledAfterEncodingIoFailure = false;
        HostLifecycleMonitor.ConfigureConsoleWindow(
            () => throw new IOException("No console handle"),
            () => titleCalledAfterEncodingIoFailure = true);
        Assert.True(titleCalledAfterEncodingIoFailure);

        HostLifecycleMonitor.ConfigureConsoleWindow(
            () => throw new PlatformNotSupportedException("Unsupported encoding"),
            () => throw new PlatformNotSupportedException("Unsupported title"));

        HostLifecycleMonitor.ConfigureConsoleWindow(
            () => { },
            () => throw new IOException("Invalid console handle"));
    }

    private InstanceLifecycleStateStore CreateStateStore(IProcessLivenessChecker livenessChecker)
    {
        return new InstanceLifecycleStateStore(
            Path.Combine(_tempDir, InstanceLifecycleStateStore.StateFileName),
            livenessChecker);
    }

    private HostLifecycleInfo CreateHostInfo() => new(
        ProcessId: 12345,
        SessionId: "sess-abc123",
        Version: "1.2.3",
        EnvironmentName: "Production",
        ContentRootPath: _tempDir,
        LogsDirectory: _tempDir,
        UserConfigStatus: UserConfigLoadStatus.Loaded,
        ProcessStartedAtUtc: new DateTimeOffset(2026, 11, 20, 21, 59, 59, TimeSpan.Zero),
        OsDescription: "Windows 11",
        FrameworkDescription: ".NET 10.0");

    private InstanceLifecycleState CreateSampleState(int pid, InstanceLifecycleStatus status) => new()
    {
        ProcessId = pid,
        SessionId = "prev-sess",
        Version = "1.0.0",
        EnvironmentName = "Production",
        ContentRootPath = _tempDir,
        ProcessStartedAtUtc = new DateTimeOffset(2026, 11, 20, 21, 0, 0, TimeSpan.Zero),
        RecordedStartedAtUtc = new DateTimeOffset(2026, 11, 20, 21, 0, 1, TimeSpan.Zero),
        Status = status
    };

    private static IServer CreateServerMock(params string[] addresses)
    {
        var features = new FeatureCollection();
        features.Set<IServerAddressesFeature>(new TestServerAddressesFeature(addresses));

        var serverMock = new Mock<IServer>();
        serverMock.SetupGet(s => s.Features).Returns(features);
        return serverMock.Object;
    }

    private sealed class TestServerAddressesFeature(params string[] addresses) : IServerAddressesFeature
    {
        public ICollection<string> Addresses { get; } = [.. addresses];
        public bool PreferHostingUrls { get; set; }
    }

    private sealed class FakeProcessLivenessChecker(Func<int, bool> isRunning) : IProcessLivenessChecker
    {
        public bool IsProcessRunning(int processId, DateTimeOffset expectedStartTimeUtc) => isRunning(processId);
    }

    private sealed class ManualTimeProvider(DateTimeOffset initialUtcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = initialUtcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
    }

    private sealed class FakeHostApplicationLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();

        public CancellationToken ApplicationStarted => _started.Token;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => _stopped.Token;

        public void StopApplication() => _stopping.Cancel();
        public void TriggerStarted() => _started.Cancel();
        public void TriggerStopping() => _stopping.Cancel();
        public void TriggerStopped() => _stopped.Cancel();

        public void Dispose()
        {
            _started.Dispose();
            _stopping.Dispose();
            _stopped.Dispose();
        }
    }

    private sealed class CapturingSerilogSink : ILogEventSink
    {
        public ConcurrentQueue<LogEvent> Events { get; } = new();

        public void Emit(LogEvent logEvent) => Events.Enqueue(logEvent);
    }

    private sealed record CapturedLogEntry(LogLevel Level, Exception? Exception, string Message);

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<CapturedLogEntry> Entries { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Enqueue(new CapturedLogEntry(logLevel, exception, formatter(state, exception)));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
