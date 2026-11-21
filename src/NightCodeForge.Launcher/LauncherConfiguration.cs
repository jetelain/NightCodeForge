namespace NightCodeForge.Launcher;

using System.Globalization;

/// <summary>
/// Resolved configuration for <see cref="LauncherSupervisor"/>.
/// </summary>
public sealed record LauncherConfiguration(
    string TargetExecutablePath,
    string ContentRootPath,
    string LauncherLogPath,
    IReadOnlyList<string> ChildArguments,
    bool AutoRestartEnabled,
    TimeSpan RestartDelay,
    int MaxRapidCrashes,
    TimeSpan HealthyUptimeThreshold,
    TimeSpan ChildShutdownGraceTimeout);

/// <summary>
/// Resolves target binary, content root, launcher log path, forwarded arguments,
/// and crash-restart thresholds from CLI arguments, environment variables, and <c>launcher.conf</c>.
/// </summary>
public static class LauncherConfigurationResolver
{
    public const string EnvWebExe = "NCF_WEB_EXE";
    public const string EnvLauncherLog = "NCF_LAUNCHER_LOG";
    public const string EnvRestartDelaySeconds = "NCF_RESTART_DELAY_SECONDS";
    public const string EnvMaxRapidCrashes = "NCF_MAX_RAPID_CRASHES";
    public const string EnvHealthyUptimeSeconds = "NCF_HEALTHY_UPTIME_SECONDS";
    public const string EnvNoAutoRestart = "NCF_NO_AUTO_RESTART";
    public const string FlagNoAutoRestart = "--no-auto-restart";
    public const string ContentRootArgName = "--contentRoot";
    public const int DefaultRestartDelaySeconds = 5;
    public const int DefaultMaxRapidCrashes = 5;
    public const int DefaultHealthyUptimeSeconds = 60;
    public const int DefaultChildShutdownGraceTimeoutSeconds = 20;
    public const int MaxTimerDelaySeconds = 4_294_967;
    public const string DefaultLogsDirectoryName = "logs";
    public const string LauncherLogFileName = "launcher.log";
    public const string LauncherConfigFileName = "launcher.conf";
    public const string LogsDirConfigPrefix = "logs_dir=";
    public const string TargetExeConfigPrefix = "target_exe=";
    public const string WindowsTargetExecutableName = "NightCodeForge.Web.exe";
    public const string UnixTargetExecutableName = "NightCodeForge.Web";
    private const string TruthyOne = "1";
    private const string TruthyTrue = "true";

    public static LauncherConfiguration Resolve(
        string[] args,
        string launcherDirectory,
        Func<string, string?> getEnv,
        Func<string, bool> fileExists,
        Func<string, string[]> readAllLines,
        bool isWindows)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentException.ThrowIfNullOrWhiteSpace(launcherDirectory);
        ArgumentNullException.ThrowIfNull(getEnv);
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentNullException.ThrowIfNull(readAllLines);

        var fullLauncherDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(launcherDirectory));
        var (forwardedArgs, cliDisabledRestart) = FilterLauncherFlags(args);
        var autoRestartEnabled = !cliDisabledRestart && !IsTruthy(getEnv(EnvNoAutoRestart));

        var (targetExecutablePath, contentRootPath) = ResolveTargetAndContentRoot(
            fullLauncherDir,
            getEnv,
            fileExists,
            readAllLines,
            isWindows);

        var launcherLogPath = ResolveLauncherLogPath(
            contentRootPath,
            fullLauncherDir,
            getEnv,
            fileExists,
            readAllLines);

        var restartDelaySeconds = ParseIntInRange(
            getEnv(EnvRestartDelaySeconds),
            DefaultRestartDelaySeconds,
            minValue: 0,
            maxValue: MaxTimerDelaySeconds);
        var maxRapidCrashes = ParseIntInRange(
            getEnv(EnvMaxRapidCrashes),
            DefaultMaxRapidCrashes,
            minValue: 1,
            maxValue: int.MaxValue);
        var healthyUptimeSeconds = ParseIntInRange(
            getEnv(EnvHealthyUptimeSeconds),
            DefaultHealthyUptimeSeconds,
            minValue: 0,
            maxValue: MaxTimerDelaySeconds);

        var childArguments = BuildChildArguments(contentRootPath, forwardedArgs);

        return new LauncherConfiguration(
            TargetExecutablePath: targetExecutablePath,
            ContentRootPath: contentRootPath,
            LauncherLogPath: launcherLogPath,
            ChildArguments: childArguments,
            AutoRestartEnabled: autoRestartEnabled,
            RestartDelay: TimeSpan.FromSeconds(restartDelaySeconds),
            MaxRapidCrashes: maxRapidCrashes,
            HealthyUptimeThreshold: TimeSpan.FromSeconds(healthyUptimeSeconds),
            ChildShutdownGraceTimeout: TimeSpan.FromSeconds(DefaultChildShutdownGraceTimeoutSeconds));
    }

    private static (List<string> ForwardedArgs, bool CliDisabledRestart) FilterLauncherFlags(string[] args)
    {
        var forwarded = new List<string>(args.Length);
        var disabled = false;

        foreach (var arg in args)
        {
            if (string.Equals(arg, FlagNoAutoRestart, StringComparison.OrdinalIgnoreCase))
            {
                disabled = true;
                continue;
            }

            forwarded.Add(arg);
        }

        return (forwarded, disabled);
    }

    private static (string TargetExecutablePath, string ContentRootPath) ResolveTargetAndContentRoot(
        string fullLauncherDir,
        Func<string, string?> getEnv,
        Func<string, bool> fileExists,
        Func<string, string[]> readAllLines,
        bool isWindows)
    {
        var envExe = getEnv(EnvWebExe);
        if (!string.IsNullOrWhiteSpace(envExe))
        {
            var fullEnvExe = Path.GetFullPath(envExe.Trim());
            if (!fileExists(fullEnvExe))
            {
                throw new FileNotFoundException(
                    $"Target executable specified by '{EnvWebExe}' was not found: '{fullEnvExe}'.",
                    fullEnvExe);
            }

            var envContentRoot = Path.GetDirectoryName(fullEnvExe) ?? fullLauncherDir;
            return (fullEnvExe, envContentRoot);
        }

        var launcherConfPath = Path.Combine(fullLauncherDir, LauncherConfigFileName);
        var configuredExe = TryReadConfigValue(launcherConfPath, TargetExeConfigPrefix, fileExists, readAllLines);
        if (!string.IsNullOrWhiteSpace(configuredExe))
        {
            var fullConfiguredExe = Path.IsPathRooted(configuredExe)
                ? Path.GetFullPath(configuredExe)
                : Path.GetFullPath(Path.Combine(fullLauncherDir, configuredExe));
            if (!fileExists(fullConfiguredExe))
            {
                throw new FileNotFoundException(
                    $"Target executable specified in '{launcherConfPath}' was not found: '{fullConfiguredExe}'.",
                    fullConfiguredExe);
            }

            var configuredRoot = Path.GetDirectoryName(fullConfiguredExe) ?? fullLauncherDir;
            return (fullConfiguredExe, configuredRoot);
        }

        var parentDir = Path.GetDirectoryName(fullLauncherDir);
        var primaryName = isWindows ? WindowsTargetExecutableName : UnixTargetExecutableName;
        var secondaryName = isWindows ? UnixTargetExecutableName : WindowsTargetExecutableName;

        var candidates = new List<string>(4);
        AddCandidate(candidates, fullLauncherDir, primaryName);
        if (!string.IsNullOrWhiteSpace(parentDir))
        {
            AddCandidate(candidates, parentDir, primaryName);
        }
        AddCandidate(candidates, fullLauncherDir, secondaryName);
        if (!string.IsNullOrWhiteSpace(parentDir))
        {
            AddCandidate(candidates, parentDir, secondaryName);
        }

        foreach (var candidate in candidates)
        {
            if (fileExists(candidate))
            {
                var resolvedRoot = Path.GetDirectoryName(candidate) ?? fullLauncherDir;
                return (candidate, resolvedRoot);
            }
        }

        throw new FileNotFoundException(
            $"Could not locate '{primaryName}' in '{fullLauncherDir}' or its parent directory (probed: {string.Join(", ", candidates)}).");
    }

    private static void AddCandidate(List<string> candidates, string directory, string fileName)
    {
        var path = Path.GetFullPath(Path.Combine(directory, fileName));
        if (!candidates.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            candidates.Add(path);
        }
    }

    private static string ResolveLauncherLogPath(
        string contentRootPath,
        string fullLauncherDir,
        Func<string, string?> getEnv,
        Func<string, bool> fileExists,
        Func<string, string[]> readAllLines)
    {
        var envLog = getEnv(EnvLauncherLog);
        if (!string.IsNullOrWhiteSpace(envLog))
        {
            return Path.GetFullPath(envLog.Trim());
        }

        var contentRootConfPath = Path.Combine(contentRootPath, LauncherConfigFileName);
        var resolvedFromContentRoot = TryResolveLogsDirFromConf(contentRootConfPath, contentRootPath, fileExists, readAllLines);
        if (resolvedFromContentRoot is not null)
        {
            return resolvedFromContentRoot;
        }

        var normalizedContentRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(contentRootPath));
        if (!string.Equals(fullLauncherDir, normalizedContentRoot, StringComparison.OrdinalIgnoreCase))
        {
            var launcherConfPath = Path.Combine(fullLauncherDir, LauncherConfigFileName);
            var resolvedFromLauncherDir = TryResolveLogsDirFromConf(launcherConfPath, contentRootPath, fileExists, readAllLines);
            if (resolvedFromLauncherDir is not null)
            {
                return resolvedFromLauncherDir;
            }
        }

        return Path.Combine(contentRootPath, DefaultLogsDirectoryName, LauncherLogFileName);
    }

    private static string? TryResolveLogsDirFromConf(
        string confPath,
        string contentRootPath,
        Func<string, bool> fileExists,
        Func<string, string[]> readAllLines)
    {
        var configuredDir = TryReadConfigValue(confPath, LogsDirConfigPrefix, fileExists, readAllLines);
        if (string.IsNullOrWhiteSpace(configuredDir))
        {
            return null;
        }

        try
        {
            var fullDir = Path.IsPathRooted(configuredDir)
                ? Path.GetFullPath(configuredDir)
                : Path.GetFullPath(Path.Combine(contentRootPath, configuredDir));
            return Path.Combine(fullDir, LauncherLogFileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Justification: Malformed logs_dir value in optional launcher.conf must fail open to default logs/launcher.log.
            _ = ex;
            return null;
        }
    }

    private static string? TryReadConfigValue(
        string confPath,
        string prefix,
        Func<string, bool> fileExists,
        Func<string, string[]> readAllLines)
    {
        if (!fileExists(confPath))
        {
            return null;
        }

        try
        {
            foreach (var rawLine in readAllLines(confPath))
            {
                var line = rawLine.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith('#') || line.StartsWith(';'))
                {
                    continue;
                }

                if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    var value = line[prefix.Length..].Trim().Trim('"');
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Justification: Unreadable optional launcher.conf must fail open to defaults.
            _ = ex;
        }

        return null;
    }

    private static IReadOnlyList<string> BuildChildArguments(string contentRootPath, List<string> forwardedArgs)
    {
        var hasContentRoot = forwardedArgs.Any(arg =>
            arg.Equals(ContentRootArgName, StringComparison.OrdinalIgnoreCase) ||
            arg.StartsWith(ContentRootArgName + "=", StringComparison.OrdinalIgnoreCase));

        if (hasContentRoot)
        {
            return forwardedArgs;
        }

        var result = new List<string>(forwardedArgs.Count + 2)
        {
            ContentRootArgName,
            contentRootPath
        };
        result.AddRange(forwardedArgs);
        return result;
    }

    private static int ParseIntInRange(string? raw, int defaultValue, int minValue, int maxValue)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        return int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
               parsed >= minValue &&
               parsed <= maxValue
            ? parsed
            : defaultValue;
    }

    private static bool IsTruthy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        return string.Equals(trimmed, TruthyOne, StringComparison.Ordinal) ||
               string.Equals(trimmed, TruthyTrue, StringComparison.OrdinalIgnoreCase);
    }
}
