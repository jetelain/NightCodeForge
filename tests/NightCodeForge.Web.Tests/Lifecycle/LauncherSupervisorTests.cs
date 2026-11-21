namespace NightCodeForge.Web.Tests.Lifecycle;

using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using NightCodeForge.Launcher;
using Xunit;

public class LauncherSupervisorTests : IDisposable
{
    private readonly string _tempDir;

    public LauncherSupervisorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ncf_launcher_test_" + Guid.NewGuid().ToString("N"));
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
            // Justification: Best-effort cleanup of temporary directory on Windows handle release timing.
            _ = ex;
        }
    }

    [Fact]
    public void ConfigurationResolver_FindsTargetInSameDirOrParentDir_AndPrependsContentRoot()
    {
        var installDir = Path.Combine(_tempDir, "install");
        var binDir = Path.Combine(installDir, "bin");
        var expectedExe = Path.Combine(installDir, LauncherConfigurationResolver.WindowsTargetExecutableName);

        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        bool FileExists(string path) =>
            string.Equals(Path.GetFullPath(path), Path.GetFullPath(expectedExe), StringComparison.OrdinalIgnoreCase);

        var fromInstall = LauncherConfigurationResolver.Resolve(
            ["--urls", "http://localhost:5270"],
            installDir,
            k => env.GetValueOrDefault(k),
            FileExists,
            _ => [],
            isWindows: true);

        Assert.Equal(Path.GetFullPath(expectedExe), fromInstall.TargetExecutablePath);
        Assert.Equal(Path.GetFullPath(installDir), fromInstall.ContentRootPath);
        Assert.Equal(
            [LauncherConfigurationResolver.ContentRootArgName, Path.GetFullPath(installDir), "--urls", "http://localhost:5270"],
            fromInstall.ChildArguments);
        Assert.Equal(
            Path.Combine(Path.GetFullPath(installDir), LauncherConfigurationResolver.DefaultLogsDirectoryName, LauncherConfigurationResolver.LauncherLogFileName),
            fromInstall.LauncherLogPath);

        var fromBin = LauncherConfigurationResolver.Resolve(
            ["--tailscale"],
            binDir,
            k => env.GetValueOrDefault(k),
            FileExists,
            _ => [],
            isWindows: true);

        Assert.Equal(Path.GetFullPath(expectedExe), fromBin.TargetExecutablePath);
        Assert.Equal(Path.GetFullPath(installDir), fromBin.ContentRootPath);
        Assert.Equal(
            [LauncherConfigurationResolver.ContentRootArgName, Path.GetFullPath(installDir), "--tailscale"],
            fromBin.ChildArguments);
    }

    [Fact]
    public void ConfigurationResolver_DoesNotDuplicateContentRootAndHonorsOverrides()
    {
        var customExe = Path.Combine(_tempDir, "custom", "NightCodeForge.Web.exe");
        var customRoot = Path.Combine(_tempDir, "explicit_root");
        var confPath = Path.Combine(Path.GetDirectoryName(customExe)!, LauncherConfigurationResolver.LauncherConfigFileName);

        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [LauncherConfigurationResolver.EnvWebExe] = customExe,
            [LauncherConfigurationResolver.EnvRestartDelaySeconds] = "12",
            [LauncherConfigurationResolver.EnvMaxRapidCrashes] = "9",
            [LauncherConfigurationResolver.EnvHealthyUptimeSeconds] = "120"
        };

        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFullPath(customExe),
            Path.GetFullPath(confPath)
        };

        var config = LauncherConfigurationResolver.Resolve(
            ["--contentRoot", customRoot, LauncherConfigurationResolver.FlagNoAutoRestart, "--urls", "http://127.0.0.1:5275"],
            _tempDir,
            k => env.GetValueOrDefault(k),
            p => files.Contains(Path.GetFullPath(p)),
            _ => ["# comment", $"logs_dir={Path.Combine(_tempDir, "custom_logs")}"],
            isWindows: true);

        Assert.False(config.AutoRestartEnabled);
        Assert.Equal(TimeSpan.FromSeconds(12), config.RestartDelay);
        Assert.Equal(9, config.MaxRapidCrashes);
        Assert.Equal(TimeSpan.FromSeconds(120), config.HealthyUptimeThreshold);
        Assert.Equal(Path.Combine(_tempDir, "custom_logs", LauncherConfigurationResolver.LauncherLogFileName), config.LauncherLogPath);
        Assert.Equal(["--contentRoot", customRoot, "--urls", "http://127.0.0.1:5275"], config.ChildArguments);

        // Env launcher log overrides launcher.conf and env disable overrides auto-restart
        env[LauncherConfigurationResolver.EnvLauncherLog] = Path.Combine(_tempDir, "env_override.log");
        env[LauncherConfigurationResolver.EnvNoAutoRestart] = "true";
        var configEnvLog = LauncherConfigurationResolver.Resolve(
            ["--contentRoot=" + customRoot],
            _tempDir,
            k => env.GetValueOrDefault(k),
            p => files.Contains(Path.GetFullPath(p)),
            _ => [$"logs_dir={Path.Combine(_tempDir, "custom_logs")}"],
            isWindows: true);

        Assert.False(configEnvLog.AutoRestartEnabled);
        Assert.Equal(Path.GetFullPath(env[LauncherConfigurationResolver.EnvLauncherLog]!), configEnvLog.LauncherLogPath);
        Assert.Equal(["--contentRoot=" + customRoot], configEnvLog.ChildArguments);
    }

    [Fact]
    public void ConfigurationResolver_ThrowsWhenTargetExecutableMissing()
    {
        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [LauncherConfigurationResolver.EnvWebExe] = Path.Combine(_tempDir, "missing.exe")
        };

        Assert.Throws<FileNotFoundException>(() => LauncherConfigurationResolver.Resolve(
            [],
            _tempDir,
            k => env.GetValueOrDefault(k),
            _ => false,
            _ => [],
            isWindows: true));

        Assert.Throws<FileNotFoundException>(() => LauncherConfigurationResolver.Resolve(
            [],
            _tempDir,
            _ => null,
            _ => false,
            _ => [],
            isWindows: true));
    }

    [Theory]
    [InlineData(0, true, true)]
    [InlineData(ExitCodeClassifier.ExitCodeControlCWindows, true, true)]
    [InlineData(ExitCodeClassifier.ExitCodeControlCWindows, false, false)]
    [InlineData(ExitCodeClassifier.ExitCodeSigIntUnix, false, true)]
    [InlineData(ExitCodeClassifier.ExitCodeSigQuitUnix, false, true)]
    [InlineData(ExitCodeClassifier.ExitCodeSigTermUnix, false, true)]
    [InlineData(ExitCodeClassifier.ExitCodeSigIntUnix, true, false)]
    [InlineData(-1073740791, true, false)]
    [InlineData(-1073741819, true, false)]
    [InlineData(1, true, false)]
    public void ExitCodeClassifier_ClassifiesIntentionalAndCrashExitCodes(int exitCode, bool isWindows, bool expectedIntentional)
    {
        Assert.Equal(expectedIntentional, ExitCodeClassifier.IsIntentionalShutdownExitCode(exitCode, isWindows));
    }

    [Fact]
    public void ExitCodeClassifier_FormatsKnownAndUnknownCrashCodesWithHex()
    {
        var fastFail = ExitCodeClassifier.FormatExitCode(unchecked((int)0xC0000409));
        Assert.Contains("-1073740791", fastFail, StringComparison.Ordinal);
        Assert.Contains("0xC0000409", fastFail, StringComparison.Ordinal);
        Assert.Contains("FastFail", fastFail, StringComparison.Ordinal);

        var accessViolation = ExitCodeClassifier.FormatExitCode(unchecked((int)0xC0000005));
        Assert.Contains("0xC0000005", accessViolation, StringComparison.Ordinal);
        Assert.Contains("STATUS_ACCESS_VIOLATION", accessViolation, StringComparison.Ordinal);

        Assert.Equal("1", ExitCodeClassifier.FormatExitCode(1));
        Assert.Equal("0", ExitCodeClassifier.FormatExitCode(0));
    }

    [Fact]
    public async Task Supervisor_CleanExitZero_RunsOnceWithoutRestart()
    {
        var handle = FakeChildProcessHandle.Completed(pid: 1001, exitCode: 0);
        var starter = new FakeChildProcessStarter(_ => handle);
        var logger = new CapturingLauncherLogger();
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 11, 20, 22, 0, 0, TimeSpan.Zero));

        using var supervisor = new LauncherSupervisor(starter, logger, timeProvider, isWindows: true);
        var exitCode = await supervisor.RunAsync(CreateConfig(), CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(1, starter.StartCount);
        Assert.False(handle.GracefulShutdownRequested);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task Supervisor_WindowsCtrlCExitCode_DoesNotRestart()
    {
        var starter = new FakeChildProcessStarter(_ =>
            FakeChildProcessHandle.Completed(pid: 1002, exitCode: ExitCodeClassifier.ExitCodeControlCWindows));
        var logger = new CapturingLauncherLogger();
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 11, 20, 22, 0, 0, TimeSpan.Zero));

        using var supervisor = new LauncherSupervisor(starter, logger, timeProvider, isWindows: true);
        var exitCode = await supervisor.RunAsync(CreateConfig(), CancellationToken.None);

        Assert.Equal(ExitCodeClassifier.ExitCodeControlCWindows, exitCode);
        Assert.Equal(1, starter.StartCount);
        Assert.Contains(logger.Entries, e => e.Level == "INF" && e.Message.Contains("0xC000013A", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Supervisor_CrashWithFastFail0xC0000409_RestartsAndThenExitsCleanly()
    {
        var fastFailCode = unchecked((int)0xC0000409);
        var starter = new FakeChildProcessStarter(run =>
            FakeChildProcessHandle.Completed(pid: 2000 + run, exitCode: run == 1 ? fastFailCode : 0));
        var logger = new CapturingLauncherLogger();
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 11, 20, 22, 0, 0, TimeSpan.Zero));
        var config = CreateConfig(restartDelay: TimeSpan.FromSeconds(5));

        using var supervisor = new LauncherSupervisor(starter, logger, timeProvider, isWindows: true);
        var runTask = supervisor.RunAsync(config, CancellationToken.None);

        await timeProvider.WaitForNextTimerAsync();
        timeProvider.Advance(TimeSpan.FromSeconds(5));

        var exitCode = await runTask;

        Assert.Equal(0, exitCode);
        Assert.Equal(2, starter.StartCount);
        Assert.Contains(logger.Entries, e =>
            e.Level == "WRN" &&
            e.Message.Contains("-1073740791", StringComparison.Ordinal) &&
            e.Message.Contains("0xC0000409", StringComparison.Ordinal) &&
            e.Message.Contains("Restarting in 5s (restart #1, rapid crash 1/5)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Supervisor_RapidCrashLimitReached_StopsRestarting()
    {
        var fastFailCode = unchecked((int)0xC0000409);
        var starter = new FakeChildProcessStarter(run =>
            FakeChildProcessHandle.Completed(pid: 3000 + run, exitCode: fastFailCode));
        var logger = new CapturingLauncherLogger();
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 11, 20, 22, 0, 0, TimeSpan.Zero));
        var config = CreateConfig(restartDelay: TimeSpan.Zero, maxRapidCrashes: 3, healthyUptime: TimeSpan.FromSeconds(60));

        using var supervisor = new LauncherSupervisor(starter, logger, timeProvider, isWindows: true);
        var exitCode = await supervisor.RunAsync(config, CancellationToken.None);

        Assert.Equal(fastFailCode, exitCode);
        Assert.Equal(3, starter.StartCount);
        Assert.Contains(logger.Entries, e =>
            e.Level == "ERR" &&
            e.Message.Contains("Reached rapid-crash limit (3/3", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Supervisor_HealthyUptimeBeforeCrash_ResetsRapidCrashCounter()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 11, 20, 22, 0, 0, TimeSpan.Zero));
        var starter = new FakeChildProcessStarter(run =>
        {
            if (run <= 3)
            {
                // Simulate 90s healthy runtime before crashing
                timeProvider.Advance(TimeSpan.FromSeconds(90));
                return FakeChildProcessHandle.Completed(pid: 4000 + run, exitCode: unchecked((int)0xC0000005));
            }

            return FakeChildProcessHandle.Completed(pid: 4000 + run, exitCode: 0);
        });
        var logger = new CapturingLauncherLogger();
        var config = CreateConfig(restartDelay: TimeSpan.Zero, maxRapidCrashes: 2, healthyUptime: TimeSpan.FromSeconds(60));

        using var supervisor = new LauncherSupervisor(starter, logger, timeProvider, isWindows: true);
        var exitCode = await supervisor.RunAsync(config, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(4, starter.StartCount);
        Assert.DoesNotContain(logger.Entries, e => e.Level == "ERR");
    }

    [Fact]
    public async Task Supervisor_AutoRestartDisabled_DoesNotRestartOnCrash()
    {
        var starter = new FakeChildProcessStarter(_ => FakeChildProcessHandle.Completed(pid: 5001, exitCode: 1));
        var logger = new CapturingLauncherLogger();
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 11, 20, 22, 0, 0, TimeSpan.Zero));

        using var supervisor = new LauncherSupervisor(starter, logger, timeProvider, isWindows: true);
        var exitCode = await supervisor.RunAsync(CreateConfig(autoRestartEnabled: false), CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Equal(1, starter.StartCount);
        Assert.Contains(logger.Entries, e => e.Level == "ERR" && e.Message.Contains("Auto-restart is disabled", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Supervisor_RequestShutdownDuringActiveChild_WaitsAndSkipsRestart()
    {
        var activeHandle = FakeChildProcessHandle.Pending(pid: 6001);
        var starter = new FakeChildProcessStarter(_ => activeHandle);
        var logger = new CapturingLauncherLogger();
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 11, 20, 22, 0, 0, TimeSpan.Zero));
        var config = CreateConfig(childShutdownGrace: TimeSpan.FromSeconds(20));

        using var supervisor = new LauncherSupervisor(starter, logger, timeProvider, isWindows: true);
        var runTask = supervisor.RunAsync(config, CancellationToken.None);

        await activeHandle.WaitForAwaiterAsync();
        supervisor.RequestShutdown("SIGINT (Ctrl+C)", PosixSignal.SIGINT);
        await timeProvider.WaitForNextTimerAsync();

        activeHandle.CompleteWithExitCode(1);
        var exitCode = await runTask;

        Assert.Equal(1, exitCode);
        Assert.Equal(1, starter.StartCount);
        Assert.True(activeHandle.GracefulShutdownRequested);
        Assert.Equal(PosixSignal.SIGINT, activeHandle.GracefulShutdownSignal);
        Assert.False(activeHandle.TerminateTreeCalled);
        Assert.Contains(logger.Entries, e => e.Level == "INF" && e.Message.Contains("during launcher shutdown", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Supervisor_RequestShutdownDuringRestartCountdown_CancelsDelayAndExits()
    {
        var starter = new FakeChildProcessStarter(_ => FakeChildProcessHandle.Completed(pid: 7001, exitCode: 1));
        var logger = new CapturingLauncherLogger();
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 11, 20, 22, 0, 0, TimeSpan.Zero));
        var config = CreateConfig(restartDelay: TimeSpan.FromSeconds(10));

        using var supervisor = new LauncherSupervisor(starter, logger, timeProvider, isWindows: true);
        var runTask = supervisor.RunAsync(config, CancellationToken.None);

        await timeProvider.WaitForNextTimerAsync();
        supervisor.RequestShutdown("SIGTERM", PosixSignal.SIGTERM);

        var exitCode = await runTask;

        Assert.Equal(1, exitCode);
        Assert.Equal(1, starter.StartCount);
        Assert.Contains(logger.Entries, e => e.Level == "INF" && e.Message.Contains("Pending server restart cancelled", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Supervisor_RequestShutdownWhenChildHangsPastGraceTimeout_TerminatesTree()
    {
        var activeHandle = FakeChildProcessHandle.Pending(pid: 8001, onTerminateTree: h => h.CompleteWithExitCode(-1));
        var starter = new FakeChildProcessStarter(_ => activeHandle);
        var logger = new CapturingLauncherLogger();
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 11, 20, 22, 0, 0, TimeSpan.Zero));
        var config = CreateConfig(childShutdownGrace: TimeSpan.FromSeconds(15));

        using var supervisor = new LauncherSupervisor(starter, logger, timeProvider, isWindows: true);
        var runTask = supervisor.RunAsync(config, CancellationToken.None);

        await activeHandle.WaitForAwaiterAsync();
        supervisor.RequestShutdown("SIGHUP", PosixSignal.SIGHUP);
        await timeProvider.WaitForNextTimerAsync();
        timeProvider.Advance(TimeSpan.FromSeconds(15));

        var exitCode = await runTask;

        Assert.Equal(-1, exitCode);
        Assert.True(activeHandle.GracefulShutdownRequested);
        Assert.Equal(PosixSignal.SIGHUP, activeHandle.GracefulShutdownSignal);
        Assert.True(activeHandle.TerminateTreeCalled);
        Assert.Contains(logger.Entries, e => e.Level == "WRN" && e.Message.Contains("terminating process tree", StringComparison.Ordinal));
    }

    [Fact]
    public void ConsoleAndFileLauncherLogger_CreatesDirectoriesAndWritesConsoleAndFile()
    {
        var logPath = Path.Combine(_tempDir, "nested", "logs", "launcher.log");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var fixedTime = new DateTimeOffset(2026, 11, 20, 22, 15, 30, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(fixedTime);

        var logger = new ConsoleAndFileLauncherLogger(logPath, stdout, stderr, timeProvider);
        logger.Info("Starting server");
        logger.Warn("Restarting soon");
        logger.Error("Crash loop stopped");

        Assert.Contains("[2026-11-20 22:15:30 INF] [Launcher] Starting server", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("[2026-11-20 22:15:30 WRN] [Launcher] Restarting soon", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("[2026-11-20 22:15:30 ERR] [Launcher] Crash loop stopped", stderr.ToString(), StringComparison.Ordinal);

        var fileContent = File.ReadAllText(logPath);
        Assert.Contains("Starting server", fileContent, StringComparison.Ordinal);
        Assert.Contains("Restarting soon", fileContent, StringComparison.Ordinal);
        Assert.Contains("Crash loop stopped", fileContent, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigurationResolver_HandlesTrailingSeparatorsAndLauncherConfTargetExeAndLogsDir()
    {
        var installDir = Path.Combine(_tempDir, "install");
        var customBinDir = Path.Combine(_tempDir, "custom_bin");
        var binDirWithTrailingSeps = Path.Combine(installDir, "bin") + Path.DirectorySeparatorChar + Path.DirectorySeparatorChar;
        var expectedExe = Path.Combine(installDir, LauncherConfigurationResolver.WindowsTargetExecutableName);
        var customBinConf = Path.Combine(customBinDir, LauncherConfigurationResolver.LauncherConfigFileName);
        var customLogs = Path.Combine(_tempDir, "shared_logs");
        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFullPath(expectedExe),
            Path.GetFullPath(customBinConf)
        };

        var fromTrailingSep = LauncherConfigurationResolver.Resolve(
            [],
            binDirWithTrailingSeps,
            k => env.GetValueOrDefault(k),
            p => files.Contains(Path.GetFullPath(p)),
            _ => [],
            isWindows: true);
        Assert.Equal(Path.GetFullPath(expectedExe), fromTrailingSep.TargetExecutablePath);

        var relativeTargetFromCustomBin = Path.Combine("..", "install", LauncherConfigurationResolver.WindowsTargetExecutableName);
        var fromCustomBin = LauncherConfigurationResolver.Resolve(
            [],
            customBinDir,
            k => env.GetValueOrDefault(k),
            p => files.Contains(Path.GetFullPath(p)),
            p => string.Equals(Path.GetFullPath(p), Path.GetFullPath(customBinConf), StringComparison.OrdinalIgnoreCase)
                ? [$"target_exe={relativeTargetFromCustomBin}", $"logs_dir={customLogs}"]
                : [],
            isWindows: true);
        Assert.Equal(Path.GetFullPath(expectedExe), fromCustomBin.TargetExecutablePath);
        Assert.Equal(Path.GetFullPath(installDir), fromCustomBin.ContentRootPath);
        Assert.Equal(
            Path.Combine(Path.GetFullPath(customLogs), LauncherConfigurationResolver.LauncherLogFileName),
            fromCustomBin.LauncherLogPath);

        // NCF_WEB_EXE overrides target_exe in launcher.conf
        var overrideExe = Path.Combine(_tempDir, "override", LauncherConfigurationResolver.WindowsTargetExecutableName);
        files.Add(Path.GetFullPath(overrideExe));
        env[LauncherConfigurationResolver.EnvWebExe] = overrideExe;
        var fromEnvOverride = LauncherConfigurationResolver.Resolve(
            [],
            customBinDir,
            k => env.GetValueOrDefault(k),
            p => files.Contains(Path.GetFullPath(p)),
            _ => [$"target_exe={relativeTargetFromCustomBin}"],
            isWindows: true);
        Assert.Equal(Path.GetFullPath(overrideExe), fromEnvOverride.TargetExecutablePath);

        // Unreadable or malformed logs_dir in launcher.conf fails open to <contentRoot>/logs/launcher.log
        env.Clear();
        var installConf = Path.Combine(installDir, LauncherConfigurationResolver.LauncherConfigFileName);
        files.Add(Path.GetFullPath(installConf));
        var malformedConfig = LauncherConfigurationResolver.Resolve(
            [],
            installDir,
            k => env.GetValueOrDefault(k),
            p => files.Contains(Path.GetFullPath(p)),
            _ => ["logs_dir=bad\0path"],
            isWindows: true);
        Assert.Equal(
            Path.Combine(Path.GetFullPath(installDir), LauncherConfigurationResolver.DefaultLogsDirectoryName, LauncherConfigurationResolver.LauncherLogFileName),
            malformedConfig.LauncherLogPath);

        var unreadableConfig = LauncherConfigurationResolver.Resolve(
            [],
            installDir,
            k => env.GetValueOrDefault(k),
            p => string.Equals(Path.GetFullPath(p), Path.GetFullPath(expectedExe), StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(Path.GetFullPath(p), Path.GetFullPath(installConf), StringComparison.OrdinalIgnoreCase),
            p => string.Equals(Path.GetFullPath(p), Path.GetFullPath(installConf), StringComparison.OrdinalIgnoreCase)
                ? throw new IOException("Locked")
                : [],
            isWindows: true);
        Assert.Equal(
            Path.Combine(Path.GetFullPath(installDir), LauncherConfigurationResolver.DefaultLogsDirectoryName, LauncherConfigurationResolver.LauncherLogFileName),
            unreadableConfig.LauncherLogPath);
    }

    [Fact]
    public void ConfigurationResolver_OutOfRangeAndInvalidEnvValues_FallBackToDefaults_AndSupportsUnixTarget()
    {
        var installDir = Path.Combine(_tempDir, "unix_install");
        var unixExe = Path.Combine(installDir, LauncherConfigurationResolver.UnixTargetExecutableName);
        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [LauncherConfigurationResolver.EnvRestartDelaySeconds] = "-1",
            [LauncherConfigurationResolver.EnvMaxRapidCrashes] = "0",
            [LauncherConfigurationResolver.EnvHealthyUptimeSeconds] = (LauncherConfigurationResolver.MaxTimerDelaySeconds + 1).ToString(CultureInfo.InvariantCulture),
            [LauncherConfigurationResolver.EnvNoAutoRestart] = "1"
        };

        var config = LauncherConfigurationResolver.Resolve(
            [],
            installDir,
            k => env.GetValueOrDefault(k),
            p => string.Equals(Path.GetFullPath(p), Path.GetFullPath(unixExe), StringComparison.OrdinalIgnoreCase),
            _ => [],
            isWindows: false);

        Assert.Equal(Path.GetFullPath(unixExe), config.TargetExecutablePath);
        Assert.False(config.AutoRestartEnabled);
        Assert.Equal(TimeSpan.FromSeconds(LauncherConfigurationResolver.DefaultRestartDelaySeconds), config.RestartDelay);
        Assert.Equal(LauncherConfigurationResolver.DefaultMaxRapidCrashes, config.MaxRapidCrashes);
        Assert.Equal(TimeSpan.FromSeconds(LauncherConfigurationResolver.DefaultHealthyUptimeSeconds), config.HealthyUptimeThreshold);

        env[LauncherConfigurationResolver.EnvRestartDelaySeconds] = LauncherConfigurationResolver.MaxTimerDelaySeconds.ToString(CultureInfo.InvariantCulture);
        env[LauncherConfigurationResolver.EnvMaxRapidCrashes] = "not_a_number";
        env[LauncherConfigurationResolver.EnvHealthyUptimeSeconds] = "0";
        env[LauncherConfigurationResolver.EnvNoAutoRestart] = "0";

        var maxConfig = LauncherConfigurationResolver.Resolve(
            [],
            installDir,
            k => env.GetValueOrDefault(k),
            p => string.Equals(Path.GetFullPath(p), Path.GetFullPath(unixExe), StringComparison.OrdinalIgnoreCase),
            _ => [],
            isWindows: false);

        Assert.True(maxConfig.AutoRestartEnabled);
        Assert.Equal(TimeSpan.FromSeconds(LauncherConfigurationResolver.MaxTimerDelaySeconds), maxConfig.RestartDelay);
        Assert.Equal(LauncherConfigurationResolver.DefaultMaxRapidCrashes, maxConfig.MaxRapidCrashes);
        Assert.Equal(TimeSpan.Zero, maxConfig.HealthyUptimeThreshold);
    }

    [Fact]
    public void ExitCodeClassifier_FormatsFatalAppExitAndUnknownCodes()
    {
        var fatalAppExit = ExitCodeClassifier.FormatExitCode(unchecked((int)0x40000015));
        Assert.Contains("0x40000015", fatalAppExit, StringComparison.Ordinal);
        Assert.Contains("STATUS_FATAL_APP_EXIT", fatalAppExit, StringComparison.Ordinal);

        Assert.Equal("-12345 (0xFFFFCFC7)", ExitCodeClassifier.FormatExitCode(-12345));
        Assert.Equal("300 (0x0000012C)", ExitCodeClassifier.FormatExitCode(300));
        Assert.Equal("255", ExitCodeClassifier.FormatExitCode(ExitCodeClassifier.MaxPosixExitCode));
    }

    [Fact]
    public async Task Supervisor_RequestShutdown_ForwardsGracefulSignalAndSkipsTerminateIfChildExitsImmediately()
    {
        var activeHandle = FakeChildProcessHandle.Pending(
            pid: 8101,
            onGracefulShutdown: (h, _) => h.CompleteWithExitCode(ExitCodeClassifier.ExitCodeSigTermUnix));
        var starter = new FakeChildProcessStarter(_ => activeHandle);
        var logger = new CapturingLauncherLogger();
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 11, 20, 22, 0, 0, TimeSpan.Zero));
        var config = CreateConfig(childShutdownGrace: TimeSpan.FromSeconds(20));

        using var supervisor = new LauncherSupervisor(starter, logger, timeProvider, isWindows: false);
        var runTask = supervisor.RunAsync(config, CancellationToken.None);

        await activeHandle.WaitForAwaiterAsync();
        supervisor.RequestShutdown("SIGTERM", PosixSignal.SIGTERM);

        var exitCode = await runTask;

        Assert.Equal(ExitCodeClassifier.ExitCodeSigTermUnix, exitCode);
        Assert.Equal(1, starter.StartCount);
        Assert.True(activeHandle.GracefulShutdownRequested);
        Assert.Equal(PosixSignal.SIGTERM, activeHandle.GracefulShutdownSignal);
        Assert.False(activeHandle.TerminateTreeCalled);
    }

    [Fact]
    public async Task Supervisor_RequestShutdownWithZeroGraceTimeout_TerminatesTreeImmediately()
    {
        var activeHandle = FakeChildProcessHandle.Pending(
            pid: 8201,
            onTerminateTree: h => h.CompleteWithExitCode(-1));
        var starter = new FakeChildProcessStarter(_ => activeHandle);
        var logger = new CapturingLauncherLogger();
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 11, 20, 22, 0, 0, TimeSpan.Zero));
        var config = CreateConfig(childShutdownGrace: TimeSpan.Zero);

        using var supervisor = new LauncherSupervisor(starter, logger, timeProvider, isWindows: true);
        var runTask = supervisor.RunAsync(config, CancellationToken.None);

        await activeHandle.WaitForAwaiterAsync();
        supervisor.RequestShutdown("SIGINT", PosixSignal.SIGINT);

        var exitCode = await runTask;

        Assert.Equal(-1, exitCode);
        Assert.True(activeHandle.GracefulShutdownRequested);
        Assert.Equal(PosixSignal.SIGINT, activeHandle.GracefulShutdownSignal);
        Assert.True(activeHandle.TerminateTreeCalled);
        Assert.Contains(logger.Entries, e => e.Level == "WRN" && e.Message.Contains("grace timeout is 0s", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Supervisor_ProcessStartFailure_LogsErrorAndReturnsLaunchFailureExitCode()
    {
        var starter = new ThrowingChildProcessStarter(new Win32Exception("Access is denied"));
        var logger = new CapturingLauncherLogger();
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 11, 20, 22, 0, 0, TimeSpan.Zero));

        using var supervisor = new LauncherSupervisor(starter, logger, timeProvider, isWindows: true);
        var exitCode = await supervisor.RunAsync(CreateConfig(), CancellationToken.None);

        Assert.Equal(ExitCodeClassifier.ExitCodeLaunchFailure, exitCode);
        Assert.Contains(logger.Entries, e =>
            e.Level == "ERR" &&
            e.Message.Contains("Failed to start server", StringComparison.Ordinal) &&
            e.Message.Contains("Access is denied", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Supervisor_RequestShutdown_CancelsEvenIfLoggerThrows()
    {
        var activeHandle = FakeChildProcessHandle.Pending(
            pid: 8301,
            onGracefulShutdown: (h, _) => h.CompleteWithExitCode(ExitCodeClassifier.ExitCodeSuccess));
        var starter = new FakeChildProcessStarter(_ => activeHandle);
        var logger = new SelectiveThrowingLauncherLogger(
            message => message.Contains("Shutdown signal received", StringComparison.Ordinal),
            new IOException("Console closed"));
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 11, 20, 22, 0, 0, TimeSpan.Zero));

        using var supervisor = new LauncherSupervisor(starter, logger, timeProvider, isWindows: true);
        var runTask = supervisor.RunAsync(CreateConfig(), CancellationToken.None);

        await activeHandle.WaitForAwaiterAsync();
        Assert.Throws<IOException>(() => supervisor.RequestShutdown("SIGHUP", PosixSignal.SIGHUP));

        Assert.True(supervisor.IsShutdownRequested);
        var exitCode = await runTask;
        Assert.Equal(ExitCodeClassifier.ExitCodeSuccess, exitCode);
        Assert.True(activeHandle.GracefulShutdownRequested);
        Assert.Equal(PosixSignal.SIGHUP, activeHandle.GracefulShutdownSignal);
    }

    [Fact]
    public void ConsoleAndFileLauncherLogger_BrokenConsoleStreamStillWritesFile_AndUnwritableLogWarnsOnce()
    {
        var logPath = Path.Combine(_tempDir, "broken_console", "launcher.log");
        using var brokenStdout = new ThrowingTextWriter(new IOException("Broken pipe"));
        using var okStderr = new StringWriter();
        var fixedTime = new DateTimeOffset(2026, 11, 20, 22, 30, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(fixedTime);

        var loggerWithBrokenStdout = new ConsoleAndFileLauncherLogger(logPath, brokenStdout, okStderr, timeProvider);
        loggerWithBrokenStdout.Info("Persisted despite broken stdout");

        Assert.True(File.Exists(logPath));
        Assert.Contains("Persisted despite broken stdout", File.ReadAllText(logPath), StringComparison.Ordinal);

        // Log path targeting a directory path causes IOException/UnauthorizedAccessException on file open
        var blockedPath = Path.Combine(_tempDir, "directory_as_file");
        Directory.CreateDirectory(blockedPath);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var unwritableLogger = new ConsoleAndFileLauncherLogger(blockedPath, stdout, stderr, timeProvider);
        unwritableLogger.Info("First write");
        unwritableLogger.Info("Second write");

        var stderrLines = stderr.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(stderrLines);
        Assert.Contains("Failed to write launcher log", stderrLines[0], StringComparison.Ordinal);

        // Even if stderr also throws during fallback warning, logger never throws
        using var brokenStderr = new ThrowingTextWriter(new ObjectDisposedException("stderr"));
        var fullyBrokenLogger = new ConsoleAndFileLauncherLogger(blockedPath, brokenStdout, brokenStderr, timeProvider);
        fullyBrokenLogger.Error("Still safe");
    }

    private LauncherConfiguration CreateConfig(
        bool autoRestartEnabled = true,
        TimeSpan? restartDelay = null,
        int maxRapidCrashes = 5,
        TimeSpan? healthyUptime = null,
        TimeSpan? childShutdownGrace = null)
    {
        return new LauncherConfiguration(
            TargetExecutablePath: Path.Combine(_tempDir, "NightCodeForge.Web.exe"),
            ContentRootPath: _tempDir,
            LauncherLogPath: Path.Combine(_tempDir, "logs", "launcher.log"),
            ChildArguments: ["--contentRoot", _tempDir],
            AutoRestartEnabled: autoRestartEnabled,
            RestartDelay: restartDelay ?? TimeSpan.Zero,
            MaxRapidCrashes: maxRapidCrashes,
            HealthyUptimeThreshold: healthyUptime ?? TimeSpan.FromSeconds(60),
            ChildShutdownGraceTimeout: childShutdownGrace ?? TimeSpan.FromSeconds(20));
    }

    private sealed class ThrowingChildProcessStarter(Exception exceptionToThrow) : IChildProcessStarter
    {
        public IChildProcessHandle Start(string executablePath, string workingDirectory, IReadOnlyList<string> arguments) =>
            throw exceptionToThrow;
    }

    private sealed class FakeChildProcessStarter(Func<int, IChildProcessHandle> onStart) : IChildProcessStarter
    {
        private int _startCount;
        public int StartCount => Volatile.Read(ref _startCount);

        public IChildProcessHandle Start(string executablePath, string workingDirectory, IReadOnlyList<string> arguments)
        {
            var run = Interlocked.Increment(ref _startCount);
            return onStart(run);
        }
    }

    private sealed class FakeChildProcessHandle : IChildProcessHandle
    {
        private readonly TaskCompletionSource<int> _exitTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _awaiterReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Action<FakeChildProcessHandle>? _onTerminateTree;
        private readonly Action<FakeChildProcessHandle, PosixSignal>? _onGracefulShutdown;

        private FakeChildProcessHandle(
            int pid,
            Action<FakeChildProcessHandle>? onTerminateTree = null,
            Action<FakeChildProcessHandle, PosixSignal>? onGracefulShutdown = null)
        {
            ProcessId = pid;
            _onTerminateTree = onTerminateTree;
            _onGracefulShutdown = onGracefulShutdown;
        }

        public int ProcessId { get; }
        public bool HasExited => _exitTcs.Task.IsCompleted;
        public bool GracefulShutdownRequested { get; private set; }
        public PosixSignal? GracefulShutdownSignal { get; private set; }
        public bool TerminateTreeCalled { get; private set; }

        public static FakeChildProcessHandle Completed(int pid, int exitCode)
        {
            var handle = new FakeChildProcessHandle(pid);
            handle._exitTcs.TrySetResult(exitCode);
            return handle;
        }

        public static FakeChildProcessHandle Pending(
            int pid,
            Action<FakeChildProcessHandle>? onTerminateTree = null,
            Action<FakeChildProcessHandle, PosixSignal>? onGracefulShutdown = null) =>
            new(pid, onTerminateTree, onGracefulShutdown);

        public Task WaitForAwaiterAsync() => _awaiterReady.Task;

        public void CompleteWithExitCode(int exitCode) => _exitTcs.TrySetResult(exitCode);

        public Task<int> WaitForExitAsync(CancellationToken cancellationToken)
        {
            _awaiterReady.TrySetResult();
            return _exitTcs.Task.WaitAsync(cancellationToken);
        }

        public void TryRequestGracefulShutdown(PosixSignal signal)
        {
            GracefulShutdownRequested = true;
            GracefulShutdownSignal = signal;
            _onGracefulShutdown?.Invoke(this, signal);
        }

        public void TryTerminateTree()
        {
            TerminateTreeCalled = true;
            _onTerminateTree?.Invoke(this);
        }

        public void Dispose() { }
    }

    private sealed class SelectiveThrowingLauncherLogger(Func<string, bool> shouldThrow, Exception exceptionToThrow) : ILauncherLogger
    {
        public void Info(string message)
        {
            if (shouldThrow(message))
            {
                throw exceptionToThrow;
            }
        }

        public void Warn(string message)
        {
            if (shouldThrow(message))
            {
                throw exceptionToThrow;
            }
        }

        public void Error(string message)
        {
            if (shouldThrow(message))
            {
                throw exceptionToThrow;
            }
        }
    }

    private sealed class ThrowingTextWriter(Exception exceptionToThrow) : TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
        public override void WriteLine(string? value) => throw exceptionToThrow;
        public override void Flush() => throw exceptionToThrow;
    }

    private sealed record CapturedEntry(string Level, string Message);

    private sealed class CapturingLauncherLogger : ILauncherLogger
    {
        public ConcurrentQueue<CapturedEntry> Entries { get; } = new();
        public void Info(string message) => Entries.Enqueue(new CapturedEntry("INF", message));
        public void Warn(string message) => Entries.Enqueue(new CapturedEntry("WRN", message));
        public void Error(string message) => Entries.Enqueue(new CapturedEntry("ERR", message));
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private readonly object _lock = new();
        private readonly List<FakeTimer> _timers = [];
        private readonly Queue<TaskCompletionSource> _timerWaiters = new();
        private DateTimeOffset _utcNow;

        public FakeTimeProvider(DateTimeOffset initialUtcNow)
        {
            _utcNow = initialUtcNow;
        }

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_lock)
            {
                return _utcNow;
            }
        }

        public Task WaitForNextTimerAsync()
        {
            lock (_lock)
            {
                if (_timers.Any(t => !t.IsDisposed && t.IsArmed))
                {
                    return Task.CompletedTask;
                }

                var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _timerWaiters.Enqueue(waiter);
                return waiter.Task;
            }
        }

        public void Advance(TimeSpan delta)
        {
            List<FakeTimer> dueTimers;
            lock (_lock)
            {
                _utcNow = _utcNow.Add(delta);
                dueTimers = _timers.Where(t => !t.IsDisposed && t.IsArmed && t.DueAt <= _utcNow).ToList();
                foreach (var timer in dueTimers)
                {
                    if (timer.Period == Timeout.InfiniteTimeSpan)
                    {
                        timer.Disarm();
                    }
                    else
                    {
                        timer.DueAt = _utcNow.Add(timer.Period);
                    }
                }
            }

            foreach (var timer in dueTimers)
            {
                timer.Fire();
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new FakeTimer(this, callback, state, dueTime, period);
            if (dueTime == TimeSpan.Zero)
            {
                timer.Fire();
            }

            return timer;
        }

        private void RegisterOrNotifyTimer(FakeTimer timer)
        {
            List<TaskCompletionSource>? waitersToRelease = null;
            lock (_lock)
            {
                if (!_timers.Contains(timer))
                {
                    _timers.Add(timer);
                }

                if (timer.IsArmed && _timerWaiters.Count > 0)
                {
                    waitersToRelease = [];
                    while (_timerWaiters.Count > 0)
                    {
                        waitersToRelease.Add(_timerWaiters.Dequeue());
                    }
                }
            }

            if (waitersToRelease != null)
            {
                foreach (var w in waitersToRelease)
                {
                    w.TrySetResult();
                }
            }
        }

        private sealed class FakeTimer : ITimer
        {
            private readonly FakeTimeProvider _owner;
            private readonly TimerCallback _callback;
            private readonly object? _state;

            public FakeTimer(
                FakeTimeProvider owner,
                TimerCallback callback,
                object? state,
                TimeSpan dueTime,
                TimeSpan period)
            {
                _owner = owner;
                _callback = callback;
                _state = state;
                Period = period;
                if (dueTime != Timeout.InfiniteTimeSpan && dueTime > TimeSpan.Zero)
                {
                    IsArmed = true;
                    DueAt = owner.GetUtcNow().Add(dueTime);
                }

                _owner.RegisterOrNotifyTimer(this);
            }

            public bool IsArmed { get; private set; }
            public bool IsDisposed { get; private set; }
            public DateTimeOffset DueAt { get; set; }
            public TimeSpan Period { get; private set; }

            public void Disarm() => IsArmed = false;

            public void Fire()
            {
                if (!IsDisposed)
                {
                    _callback(_state);
                }
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (IsDisposed)
                {
                    return false;
                }

                Period = period;
                if (dueTime == Timeout.InfiniteTimeSpan)
                {
                    IsArmed = false;
                }
                else if (dueTime == TimeSpan.Zero)
                {
                    IsArmed = false;
                    Fire();
                }
                else
                {
                    IsArmed = true;
                    DueAt = _owner.GetUtcNow().Add(dueTime);
                    _owner.RegisterOrNotifyTimer(this);
                }

                return true;
            }

            public void Dispose()
            {
                IsDisposed = true;
                IsArmed = false;
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
