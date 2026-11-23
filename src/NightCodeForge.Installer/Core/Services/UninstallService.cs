namespace NightCodeForge.Installer.Core.Services;

using System.Text;
using System.Text.Json;
using NightCodeForge.Installer.Core.Abstractions;
using NightCodeForge.Installer.Core.Constants;
using NightCodeForge.Installer.Core.Exceptions;
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
    private readonly IEnvironmentProvisioner _environmentProvisioner;

    public UninstallService(
        IPlatformEnvironment platform,
        IProcessRunner processRunner,
        IProcessControlService processControl,
        IInstallationDetector installationDetector,
        ICompanionNuGetProvisioner nuGetProvisioner,
        IOsIntegrationService osIntegration,
        IEnvironmentProvisioner environmentProvisioner)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _processControl = processControl ?? throw new ArgumentNullException(nameof(processControl));
        _installationDetector = installationDetector ?? throw new ArgumentNullException(nameof(installationDetector));
        _nuGetProvisioner = nuGetProvisioner ?? throw new ArgumentNullException(nameof(nuGetProvisioner));
        _osIntegration = osIntegration ?? throw new ArgumentNullException(nameof(osIntegration));
        _environmentProvisioner = environmentProvisioner ?? throw new ArgumentNullException(nameof(environmentProvisioner));
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

        try
        {
            var discovery = _installationDetector.Detect(explicitInstallDirectory: targetInstallDirectory);
            var installDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
                string.IsNullOrWhiteSpace(targetInstallDirectory)
                    ? discovery.NightCodeForge.InstallDirectory
                    : targetInstallDirectory.Trim()));

            ValidateSafeDeletionDirectory(installDir, isOllamaRouter: false);

            var binDir = Path.Combine(installDir, InstallerConstants.BinDirectoryName);
            var requestedDistro = string.IsNullOrWhiteSpace(wslDistroName)
                ? InstallerConstants.DefaultWslDistro
                : wslDistroName.Trim();

            string? resolvedWslDistro = null;
            if (_platform.Platform == HostPlatformType.Windows && options.RemoveCompanionTools)
            {
                var probe = await _environmentProvisioner.ProbeAsync(requestedDistro, cancellationToken).ConfigureAwait(false);
                if (probe.WslInstalled && probe.WslDistroInstalled)
                {
                    resolvedWslDistro = probe.ResolvedWslDistroName ?? requestedDistro;
                }
            }

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
                var outcomes = await _nuGetProvisioner.UninstallAllAsync(resolvedWslDistro, progress, cancellationToken)
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
                ValidateSafeDeletionDirectory(routerDir, isOllamaRouter: true);

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
                        protectedPaths: [],
                        cancellationToken).ConfigureAwait(false);
                }

                steps.Add(new UninstallStepResult(StepRemoveOllamaRouter, ProvisioningStatus.Completed, $"Removed OllamaRouter from '{routerDir}'."));
            }
            else
            {
                steps.Add(new UninstallStepResult(StepRemoveOllamaRouter, ProvisioningStatus.Skipped, "OllamaRouter preserved."));
            }

            // 6. Delete <InstallDir> files (preserving config/, data/, logs/, wks/, launcher.conf when KeepData == true).
            Report(progress, StepDeleteFiles, $"Cleaning NightCodeForge files in '{installDir}'...", InstallerLogLevel.Info, 85);
            var runningInstallerPath = _processControl.CurrentProcessPath;
            var isSelfInsideInstallDir = IsPathInsideDirectory(runningInstallerPath, installDir);
            var isWindowsSelfInside = isSelfInsideInstallDir &&
                                      _platform.Platform == HostPlatformType.Windows &&
                                      !string.IsNullOrWhiteSpace(runningInstallerPath);

            var protectedPaths = isWindowsSelfInside
                ? CollectRunningInstallerFiles(runningInstallerPath!)
                : [];

            if (Directory.Exists(installDir))
            {
                await DeleteDirectoryContentsWithPreservationAsync(
                    installDir,
                    options.KeepData ? InstallerConstants.NightCodeForgeKeepDataItems : [],
                    protectedPaths,
                    cancellationToken).ConfigureAwait(false);
            }

            steps.Add(new UninstallStepResult(
                StepDeleteFiles,
                ProvisioningStatus.Completed,
                options.KeepData
                    ? $"Removed binaries while keeping config/data/logs/wks in '{installDir}'."
                    : $"Removed '{installDir}'."));

            // 7. On Windows, schedule PID-aware deferred self-cleanup if the running binary resides inside <InstallDir>.
            var deferredScheduled = false;
            if (isWindowsSelfInside)
            {
                var deferredTargets = ResolveDeferredCleanupTargets(installDir, runningInstallerPath!, options.KeepData);
                var script = BuildWindowsDeferredSelfCleanupScript(_processControl.CurrentProcessId, deferredTargets);
                var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

                var spawnResult = await _processRunner.RunAsync(
                    new ProcessRunRequest
                    {
                        FileName = InstallerConstants.PowerShellExecutable,
                        Arguments =
                        [
                            "-NoProfile",
                            "-NonInteractive",
                            "-WindowStyle",
                            "Hidden",
                            "-ExecutionPolicy",
                            "Bypass",
                            "-EncodedCommand",
                            encodedCommand
                        ],
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

    internal static string BuildWindowsDeferredSelfCleanupScript(
        int processId,
        IReadOnlyList<string> targetPaths)
    {
        ArgumentNullException.ThrowIfNull(targetPaths);
        if (targetPaths.Count == 0)
        {
            throw new ArgumentException("At least one deferred cleanup target path is required.", nameof(targetPaths));
        }

        var sb = new StringBuilder();
        sb.Append("$ErrorActionPreference = 'SilentlyContinue'\n");
        sb.Append($"Wait-Process -Id {processId} -Timeout {InstallerConstants.DeferredCleanupWaitTimeoutSeconds} -ErrorAction SilentlyContinue\n");
        sb.Append("$targets = @(\n");
        for (var i = 0; i < targetPaths.Count; i++)
        {
            var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(targetPaths[i]));
            var suffix = i + 1 < targetPaths.Count ? "," : string.Empty;
            sb.Append($"  ([System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String('{b64}'))){suffix}\n");
        }

        sb.Append(")\n");
        sb.Append($"for ($attempt = 0; $attempt -lt {InstallerConstants.DeferredCleanupRetryAttempts}; $attempt++) {{\n");
        sb.Append("  $remaining = $false\n");
        sb.Append("  foreach ($path in $targets) {\n");
        sb.Append("    if (Test-Path -LiteralPath $path) {\n");
        sb.Append("      Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue\n");
        sb.Append("      if (Test-Path -LiteralPath $path) { $remaining = $true }\n");
        sb.Append("    }\n");
        sb.Append("  }\n");
        sb.Append("  if (-not $remaining) { break }\n");
        sb.Append($"  Start-Sleep -Milliseconds {InstallerConstants.DeferredCleanupRetryDelayMs}\n");
        sb.Append("}\n");
        return sb.ToString();
    }

    internal static bool IsPathInsideDirectory(string? filePath, string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(directoryPath))
        {
            return false;
        }

        var fullFile = Path.TrimEndingDirectorySeparator(Path.GetFullPath(filePath));
        var fullDir = Path.GetFullPath(directoryPath);
        if (!fullDir.EndsWith(Path.DirectorySeparatorChar))
        {
            fullDir += Path.DirectorySeparatorChar;
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return fullFile.StartsWith(fullDir, comparison) ||
               string.Equals(fullFile, Path.TrimEndingDirectorySeparator(fullDir), comparison);
    }

    private void ValidateSafeDeletionDirectory(string directoryPath, bool isOllamaRouter)
    {
        var fullTarget = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directoryPath));
        var root = Path.TrimEndingDirectorySeparator(Path.GetPathRoot(fullTarget) ?? string.Empty);
        if (string.IsNullOrWhiteSpace(fullTarget) ||
            string.Equals(fullTarget, root, GetPathComparison()))
        {
            throw new UninstallException($"Refusing to uninstall filesystem root '{directoryPath}'.");
        }

        var forbiddenDirs = new[]
        {
            _platform.UserHomeDirectory,
            _platform.LocalApplicationDataDirectory,
            _platform.ApplicationDataDirectory,
            _platform.DesktopDirectory,
            Path.GetTempPath(),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "/usr",
            "/etc",
            "/var",
            "/bin",
            "/sbin",
            "/lib",
            "/opt",
            "/home",
            "/Users",
            "/Applications",
            "/tmp"
        };

        var comparer = GetPathComparer();
        foreach (var rawForbidden in forbiddenDirs)
        {
            if (string.IsNullOrWhiteSpace(rawForbidden))
            {
                continue;
            }

            var forbidden = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rawForbidden));
            if (comparer.Equals(fullTarget, forbidden) || IsPathInsideDirectory(forbidden, fullTarget))
            {
                throw new UninstallException(
                    $"Refusing to uninstall protected or ancestor system/user directory '{fullTarget}'.");
            }
        }

        if (Directory.Exists(fullTarget) && !HasExpectedProductMarkers(fullTarget, isOllamaRouter))
        {
            var componentLabel = isOllamaRouter ? "OllamaRouter" : "NightCodeForge";
            throw new UninstallException(
                $"Directory '{fullTarget}' does not appear to contain a {componentLabel} installation; aborting destructive cleanup.");
        }
    }

    private static bool HasExpectedProductMarkers(string directory, bool isOllamaRouter)
    {
        if (isOllamaRouter)
        {
            return InstallationDetector.FindOllamaRouterBinary(directory) is not null ||
                   File.Exists(Path.Combine(directory, InstallerConstants.OllamaRouterCliWindows)) ||
                   File.Exists(Path.Combine(directory, InstallerConstants.OllamaRouterCliUnix)) ||
                   File.Exists(Path.Combine(directory, InstallerConstants.OllamaRouterAppWindows)) ||
                   File.Exists(Path.Combine(directory, InstallerConstants.OllamaRouterAppUnix));
        }

        return InstallationDetector.FindNightCodeForgeBinary(directory) is not null ||
               File.Exists(Path.Combine(directory, InstallerConstants.UninstallManifestFileName)) ||
               File.Exists(Path.Combine(directory, InstallerConstants.InstallerExeWindows)) ||
               File.Exists(Path.Combine(directory, InstallerConstants.InstallerExeUnix)) ||
               File.Exists(Path.Combine(directory, InstallerConstants.InstallerSubDirectoryName, InstallerConstants.InstallerExeWindows)) ||
               File.Exists(Path.Combine(directory, InstallerConstants.InstallerSubDirectoryName, InstallerConstants.InstallerExeUnix)) ||
               File.Exists(Path.Combine(directory, InstallerConstants.LauncherLongWindows)) ||
               File.Exists(Path.Combine(directory, InstallerConstants.LauncherLongUnix));
    }

    private static IReadOnlyList<string> CollectRunningInstallerFiles(string runningInstallerPath)
    {
        var files = new List<string> { Path.GetFullPath(runningInstallerPath) };
        var dir = Path.GetDirectoryName(Path.GetFullPath(runningInstallerPath));
        if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
        {
            foreach (var lib in InstallerConstants.InstallerNativeLibraryFileNames)
            {
                var libPath = Path.Combine(dir, lib);
                if (File.Exists(libPath))
                {
                    files.Add(Path.GetFullPath(libPath));
                }
            }
        }

        return files;
    }

    private static IReadOnlyList<string> ResolveDeferredCleanupTargets(
        string installDir,
        string runningInstallerPath,
        bool keepData)
    {
        if (!keepData)
        {
            return [installDir];
        }

        var fullExe = Path.GetFullPath(runningInstallerPath);
        var exeDir = Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(fullExe) ?? string.Empty);
        var installerSubDir = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(Path.Combine(installDir, InstallerConstants.InstallerSubDirectoryName)));

        if (string.Equals(exeDir, installerSubDir, StringComparison.OrdinalIgnoreCase))
        {
            return [installerSubDir];
        }

        return CollectRunningInstallerFiles(fullExe);
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

    private async Task DeleteDirectoryContentsWithPreservationAsync(
        string directory,
        IReadOnlyList<string> preservedNames,
        IReadOnlyList<string> protectedPaths,
        CancellationToken cancellationToken)
    {
        var comparer = GetPathComparer();
        var preservedSet = new HashSet<string>(preservedNames, comparer);
        var protectedSet = new HashSet<string>(
            protectedPaths.Select(static p => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p))),
            comparer);

        if (preservedSet.Count == 0 && protectedSet.Count == 0)
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

            var fullEntry = Path.TrimEndingDirectorySeparator(Path.GetFullPath(entryPath));
            if (protectedSet.Contains(fullEntry))
            {
                continue;
            }

            if (Directory.Exists(entryPath))
            {
                if (protectedSet.Any(p => IsPathInsideDirectory(p, fullEntry)))
                {
                    await DeleteDirectoryContentsWithPreservationAsync(
                        entryPath,
                        [],
                        protectedPaths,
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
    }

    private StringComparer GetPathComparer() =>
        _platform.Platform == HostPlatformType.Windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private StringComparison GetPathComparison() =>
        _platform.Platform == HostPlatformType.Windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

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
