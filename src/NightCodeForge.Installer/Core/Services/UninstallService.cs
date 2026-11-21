namespace NightCodeForge.Installer.Core.Services;

using System.Text.Json;
using NightCodeForge.Installer.Core.Abstractions;
using NightCodeForge.Installer.Core.Constants;
using NightCodeForge.Installer.Core.Models;
using NightCodeForge.Installer.Core.Serialization;

/// <summary>
/// Executes the 7-step uninstallation sequence across Windows, Linux, and macOS.
/// </summary>
public sealed class UninstallService : IUninstallService
{
    private const string StepStopProcesses = "Stop Processes";
    private const string StepCleanPath = "Remove PATH Entries";
    private const string StepUnregisterOs = "Unregister OS Integration";
    private const string StepRemoveTools = "Remove Companion NuGet Tools";
    private const string StepRemoveOllamaRouter = "Remove OllamaRouter";
    private const string StepDeleteFiles = "Remove Application Files";
    private const string StepSelfCleanup = "Deferred Self-Cleanup";

    private readonly IPlatformEnvironment _platform;
    private readonly IProcessRunner _processRunner;
    private readonly IProcessControlService _processControl;
    private readonly IInstallationDetector _installationDetector;
    private readonly ICompanionNuGetProvisioner _nuGetProvisioner;
    private readonly IOsIntegrationService _osIntegration;

    public UninstallService(
        IPlatformEnvironment platform,
        IProcessRunner processRunner,
        IProcessControlService processControl,
        IInstallationDetector installationDetector,
        ICompanionNuGetProvisioner nuGetProvisioner,
        IOsIntegrationService osIntegration)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _processControl = processControl ?? throw new ArgumentNullException(nameof(processControl));
        _installationDetector = installationDetector ?? throw new ArgumentNullException(nameof(installationDetector));
        _nuGetProvisioner = nuGetProvisioner ?? throw new ArgumentNullException(nameof(nuGetProvisioner));
        _osIntegration = osIntegration ?? throw new ArgumentNullException(nameof(osIntegration));
    }

    public async Task<UninstallResult> UninstallAsync(
        UninstallOptions options,
        string? targetInstallDirectory = null,
        string? wslDistroName = null,
        IProgress<InstallerProgressEvent>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var steps = new List<UninstallStepResult>();
        var discovery = _installationDetector.Detect();
        var installDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
            string.IsNullOrWhiteSpace(targetInstallDirectory)
                ? discovery.NightCodeForge.InstallDirectory
                : targetInstallDirectory));
        var binDir = Path.Combine(installDir, InstallerConstants.BinDirectoryName);
        var effectiveDistro = string.IsNullOrWhiteSpace(wslDistroName)
            ? InstallerConstants.DefaultWslDistro
            : wslDistroName.Trim();

        try
        {
            // 1. Stop running ncf-web, nightcodeforge-web, and NightCodeForge.Web processes.
            Report(progress, StepStopProcesses, "Stopping NightCodeForge launcher and web processes...", InstallerLogLevel.Info, 10);
            var stoppedWatchdogs = await _processControl.StopProcessesAsync(
                InstallerConstants.NightCodeForgeLauncherProcesses,
                TimeSpan.FromSeconds(InstallerConstants.ProcessStopTimeoutSeconds),
                cancellationToken).ConfigureAwait(false);
            if (stoppedWatchdogs)
            {
                await Task.Delay(InstallerConstants.LauncherStopDelayMs, cancellationToken).ConfigureAwait(false);
            }

            var stoppedWeb = await _processControl.StopProcessesAsync(
                InstallerConstants.NightCodeForgeWebProcesses,
                TimeSpan.FromSeconds(InstallerConstants.ProcessStopTimeoutSeconds),
                cancellationToken).ConfigureAwait(false);
            if (stoppedWeb)
            {
                await Task.Delay(InstallerConstants.WebStopDelayMs, cancellationToken).ConfigureAwait(false);
            }

            steps.Add(new UninstallStepResult(StepStopProcesses, ProvisioningStatus.Completed, "Stopped NightCodeForge processes."));

            // 2. Remove <InstallDir>/bin from user PATH.
            Report(progress, StepCleanPath, $"Removing '{binDir}' from user PATH...", InstallerLogLevel.Info, 25);
            _osIntegration.RemoveDirectoryFromUserPath(binDir, InstallerConstants.PathBlockLabelNightCodeForge);
            steps.Add(new UninstallStepResult(StepCleanPath, ProvisioningStatus.Completed, $"Removed '{binDir}' from user PATH."));

            // 3. Unregister OS uninstall entry and shortcuts.
            Report(progress, StepUnregisterOs, "Removing OS uninstall entry and shortcuts...", InstallerLogLevel.Info, 40);
            await _osIntegration.UnregisterNightCodeForgeInstallationAsync(installDir, cancellationToken).ConfigureAwait(false);
            steps.Add(new UninstallStepResult(StepUnregisterOs, ProvisioningStatus.Completed, "Removed OS uninstall entry and shortcuts."));

            // 4. Optional companion NuGet global tools uninstall.
            if (options.RemoveCompanionTools)
            {
                Report(progress, StepRemoveTools, "Uninstalling companion .NET global tools...", InstallerLogLevel.Info, 55);
                var outcomes = await _nuGetProvisioner.UninstallAllAsync(effectiveDistro, progress, cancellationToken)
                    .ConfigureAwait(false);
                steps.Add(new UninstallStepResult(StepRemoveTools, ProvisioningStatus.Completed, $"Processed {outcomes.Count} companion tool removal(s)."));
            }
            else
            {
                steps.Add(new UninstallStepResult(StepRemoveTools, ProvisioningStatus.Skipped, "Companion tools preserved."));
            }

            // 5. Optional OllamaRouter removal.
            if (options.RemoveOllamaRouter)
            {
                Report(progress, StepRemoveOllamaRouter, "Removing OllamaRouter...", InstallerLogLevel.Info, 70);
                var routerDir = ResolveOllamaRouterDirectory(discovery, installDir);
                var routerBinDir = Path.Combine(routerDir, InstallerConstants.BinDirectoryName);

                await ReleaseManager.StopOllamaRouterProcessesAsync(
                    routerDir,
                    _processRunner,
                    _processControl,
                    _platform.Platform,
                    cancellationToken).ConfigureAwait(false);

                _osIntegration.RemoveDirectoryFromUserPath(routerBinDir, InstallerConstants.PathBlockLabelOllamaRouter);
                await _osIntegration.UnregisterOllamaRouterProtocolAsync(cancellationToken).ConfigureAwait(false);

                if (Directory.Exists(routerDir))
                {
                    await DeleteDirectoryContentsWithPreservationAsync(
                        routerDir,
                        options.KeepData ? InstallerConstants.OllamaRouterPreservedItems : [],
                        runningExecutablePath: null,
                        cancellationToken).ConfigureAwait(false);
                }

                steps.Add(new UninstallStepResult(StepRemoveOllamaRouter, ProvisioningStatus.Completed, $"Removed OllamaRouter from '{routerDir}'."));
            }
            else
            {
                steps.Add(new UninstallStepResult(StepRemoveOllamaRouter, ProvisioningStatus.Skipped, "OllamaRouter preserved."));
            }

            // 6. Delete <InstallDir> files (preserving config/, data/, logs/ when KeepData == true).
            Report(progress, StepDeleteFiles, $"Cleaning NightCodeForge files in '{installDir}'...", InstallerLogLevel.Info, 85);
            var runningInstallerPath = _processControl.CurrentProcessPath;
            var isSelfInsideInstallDir = IsPathInsideDirectory(runningInstallerPath, installDir);

            if (Directory.Exists(installDir))
            {
                await DeleteDirectoryContentsWithPreservationAsync(
                    installDir,
                    options.KeepData ? InstallerConstants.NightCodeForgeKeepDataItems : [],
                    isSelfInsideInstallDir && _platform.Platform == HostPlatformType.Windows ? runningInstallerPath : null,
                    cancellationToken).ConfigureAwait(false);
            }

            steps.Add(new UninstallStepResult(
                StepDeleteFiles,
                ProvisioningStatus.Completed,
                options.KeepData
                    ? $"Removed binaries while keeping config/data/logs in '{installDir}'."
                    : $"Removed '{installDir}'."));

            // 7. On Windows, schedule deferred self-cleanup if the running binary resides inside <InstallDir>.
            var deferredScheduled = false;
            var deferredRequired = _platform.Platform == HostPlatformType.Windows &&
                                   isSelfInsideInstallDir &&
                                   !string.IsNullOrWhiteSpace(runningInstallerPath);
            if (deferredRequired)
            {
                var command = BuildWindowsDeferredSelfCleanupCommand(installDir, runningInstallerPath!, options.KeepData);
                var spawnResult = await _processRunner.RunAsync(
                    new ProcessRunRequest
                    {
                        FileName = InstallerConstants.CmdExecutable,
                        RawArguments = $"/c \"{command}\"",
                        WorkingDirectory = _platform.UserHomeDirectory,
                        Detached = true
                    },
                    cancellationToken).ConfigureAwait(false);

                deferredScheduled = spawnResult.ExitCode == InstallerConstants.ExitCodeSuccess;
                steps.Add(new UninstallStepResult(
                    StepSelfCleanup,
                    deferredScheduled ? ProvisioningStatus.Completed : ProvisioningStatus.Failed,
                    deferredScheduled
                        ? "Scheduled deferred self-cleanup after exit."
                        : "Failed to schedule deferred self-cleanup."));

                if (!deferredScheduled)
                {
                    const string cleanupFailureMessage = "Failed to schedule deferred self-cleanup after exit.";
                    Report(progress, StepSelfCleanup, cleanupFailureMessage, InstallerLogLevel.Error, null);
                    return new UninstallResult(
                        Succeeded: false,
                        DeferredSelfCleanupScheduled: false,
                        Steps: steps,
                        ErrorMessage: cleanupFailureMessage);
                }
            }
            else
            {
                steps.Add(new UninstallStepResult(StepSelfCleanup, ProvisioningStatus.Skipped, "Deferred self-cleanup not required."));
            }

            Report(progress, StepDeleteFiles, "Uninstallation completed.", InstallerLogLevel.Info, 100);
            return new UninstallResult(
                Succeeded: true,
                DeferredSelfCleanupScheduled: deferredScheduled,
                Steps: steps,
                ErrorMessage: null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Report(progress, StepDeleteFiles, $"Uninstallation failed: {ex.Message}", InstallerLogLevel.Error, null);
            return new UninstallResult(
                Succeeded: false,
                DeferredSelfCleanupScheduled: false,
                Steps: steps,
                ErrorMessage: ex.Message);
        }
    }

    internal static string BuildWindowsDeferredSelfCleanupCommand(
        string installDirectory,
        string runningInstallerExecutablePath,
        bool keepData)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(runningInstallerExecutablePath);

        var pingDelay = $"ping 127.0.0.1 -n {InstallerConstants.DeferredCleanupPingCount} > nul";
        return keepData
            ? $"{pingDelay} & del /f /q \"{runningInstallerExecutablePath}\""
            : $"{pingDelay} & rmdir /s /q \"{installDirectory}\"";
    }

    internal static bool IsPathInsideDirectory(string? filePath, string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(directoryPath))
        {
            return false;
        }

        var fullFile = Path.GetFullPath(filePath);
        var fullDir = Path.GetFullPath(directoryPath);
        if (!fullDir.EndsWith(Path.DirectorySeparatorChar))
        {
            fullDir += Path.DirectorySeparatorChar;
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return fullFile.StartsWith(fullDir, comparison);
    }

    private static string ResolveOllamaRouterDirectory(InstallationDiscoveryResult discovery, string installDir)
    {
        if (discovery.OllamaRouter.IsInstalled)
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(discovery.OllamaRouter.InstallDirectory));
        }

        var manifestPath = Path.Combine(installDir, InstallerConstants.UninstallManifestFileName);
        if (File.Exists(manifestPath))
        {
            try
            {
                var json = File.ReadAllText(manifestPath);
                var manifest = JsonSerializer.Deserialize(json, InstallerJsonContext.Default.UninstallManifest);
                if (!string.IsNullOrWhiteSpace(manifest?.OllamaRouterDirectory) &&
                    Directory.Exists(manifest.OllamaRouterDirectory))
                {
                    return Path.TrimEndingDirectorySeparator(Path.GetFullPath(manifest.OllamaRouterDirectory));
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // Justification: Corrupt or unreadable manifest falls back to default OllamaRouter probe path.
                _ = ex;
            }
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(discovery.OllamaRouter.InstallDirectory));
    }

    private static async Task DeleteDirectoryContentsWithPreservationAsync(
        string directory,
        IReadOnlyList<string> preservedNames,
        string? runningExecutablePath,
        CancellationToken cancellationToken)
    {
        var preservedSet = new HashSet<string>(preservedNames, StringComparer.OrdinalIgnoreCase);
        var normalizedRunningPath = string.IsNullOrWhiteSpace(runningExecutablePath)
            ? null
            : Path.GetFullPath(runningExecutablePath);

        if (preservedSet.Count == 0 && normalizedRunningPath is null)
        {
            await DeleteDirectoryWithRetriesAsync(directory, cancellationToken).ConfigureAwait(false);
            return;
        }

        foreach (var entryPath in Directory.EnumerateFileSystemEntries(directory))
        {
            var entryName = Path.GetFileName(entryPath);
            if (preservedSet.Contains(entryName))
            {
                continue;
            }

            if (normalizedRunningPath is not null &&
                string.Equals(Path.GetFullPath(entryPath), normalizedRunningPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (Directory.Exists(entryPath))
            {
                if (normalizedRunningPath is not null && IsPathInsideDirectory(normalizedRunningPath, entryPath))
                {
                    await DeleteDirectoryContentsWithPreservationAsync(
                        entryPath,
                        [],
                        normalizedRunningPath,
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await DeleteDirectoryWithRetriesAsync(entryPath, cancellationToken).ConfigureAwait(false);
                }
            }
            else if (File.Exists(entryPath))
            {
                await DeleteFileWithRetriesAsync(entryPath, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task DeleteDirectoryWithRetriesAsync(string path, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= InstallerConstants.DeleteRetryAttempts; attempt++)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    return;
                }

                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception ex) when (attempt < InstallerConstants.DeleteRetryAttempts &&
                                       (ex is IOException or UnauthorizedAccessException))
            {
                await Task.Delay(InstallerConstants.DeleteRetryDelayMs, cancellationToken).ConfigureAwait(false);
            }
        }

        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static async Task DeleteFileWithRetriesAsync(string path, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= InstallerConstants.DeleteRetryAttempts; attempt++)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return;
                }

                File.Delete(path);
                return;
            }
            catch (Exception ex) when (attempt < InstallerConstants.DeleteRetryAttempts &&
                                       (ex is IOException or UnauthorizedAccessException))
            {
                await Task.Delay(InstallerConstants.DeleteRetryDelayMs, cancellationToken).ConfigureAwait(false);
            }
        }

        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void Report(
        IProgress<InstallerProgressEvent>? progress,
        string step,
        string message,
        InstallerLogLevel level,
        double? percent)
    {
        progress?.Report(new InstallerProgressEvent(step, message, level, percent));
    }
}
