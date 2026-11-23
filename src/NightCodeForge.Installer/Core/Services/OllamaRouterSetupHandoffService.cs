namespace NightCodeForge.Installer.Core.Services;

using System.ComponentModel;
using NightCodeForge.Installer.Core.Abstractions;
using NightCodeForge.Installer.Core.Constants;
using NightCodeForge.Installer.Core.Exceptions;
using NightCodeForge.Installer.Core.Models;

/// <summary>
/// Resolves the <c>ollamarouter</c> CLI binary, runs <c>ollamarouter setup --wizard --wait</c>,
/// and triggers <see cref="IOllamaRouterRecommendationSyncService"/> when the wizard exits with code 0.
/// </summary>
public sealed class OllamaRouterSetupHandoffService : IOllamaRouterSetupHandoffService
{
    private const string StepWizardHandoff = "OllamaRouter Wizard";
    private const double ProgressWizardLaunchPercent = 10.0;
    private const double ProgressSyncStartPercent = 60.0;

    private readonly IPlatformEnvironment _platform;
    private readonly IProcessRunner _processRunner;
    private readonly IInstallationDetector _installationDetector;
    private readonly IOllamaRouterRecommendationSyncService _recommendationSyncService;

    public OllamaRouterSetupHandoffService(
        IPlatformEnvironment platform,
        IProcessRunner processRunner,
        IInstallationDetector installationDetector,
        IOllamaRouterRecommendationSyncService recommendationSyncService)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _installationDetector = installationDetector ?? throw new ArgumentNullException(nameof(installationDetector));
        _recommendationSyncService = recommendationSyncService ?? throw new ArgumentNullException(nameof(recommendationSyncService));
    }

    public bool IsOllamaRouterInstalled() => ResolveOllamaRouterCliPath() is not null;

    public string? ResolveOllamaRouterCliPath()
    {
        var cliFileName = _platform.Platform == HostPlatformType.Windows
            ? InstallerConstants.OllamaRouterCliWindows
            : InstallerConstants.OllamaRouterCliUnix;

        foreach (var candidateDir in BuildCandidateDirectories())
        {
            var directPath = Path.Combine(candidateDir, cliFileName);
            if (File.Exists(directPath))
            {
                return directPath;
            }

            var binPath = Path.Combine(candidateDir, InstallerConstants.BinDirectoryName, cliFileName);
            if (File.Exists(binPath))
            {
                return binPath;
            }
        }

        return _platform.IsCommandOnPath(cliFileName) ? cliFileName : null;
    }

    public Task<OllamaRouterWizardHandoffResult> RunSetupWizardAndSyncAsync(
        CancellationToken cancellationToken = default) =>
        RunSetupWizardAndSyncAsync(
            installDirectory: null,
            routerBaseUrl: null,
            progress: null,
            cancellationToken: cancellationToken);

    public async Task<OllamaRouterWizardHandoffResult> RunSetupWizardAndSyncAsync(
        string? installDirectory,
        string? routerBaseUrl = null,
        IProgress<InstallerProgressEvent>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var cliPath = ResolveOllamaRouterCliPath();
        if (cliPath is null)
        {
            const string missingMessage =
                "OllamaRouter CLI ('ollamarouter') was not found on PATH or in the standard OllamaRouter installation directory. " +
                "Install OllamaRouter first, retry the wizard, or skip local LLM setup.";
            Report(progress, missingMessage, InstallerLogLevel.Error, null);
            return new OllamaRouterWizardHandoffResult(
                Succeeded: false,
                ExitCode: InstallerConstants.ExitCodeError,
                ErrorMessage: missingMessage,
                SyncedRecommendations: null);
        }

        Report(
            progress,
            "Launching OllamaRouter Setup Wizard (ollamarouter setup --wizard --wait)...",
            InstallerLogLevel.Info,
            ProgressWizardLaunchPercent);

        var workingDirectory = Path.IsPathRooted(cliPath)
            ? Path.GetDirectoryName(cliPath)
            : null;

        ProcessExecutionResult executionResult;
        try
        {
            executionResult = await _processRunner.RunAsync(
                new ProcessRunRequest
                {
                    FileName = cliPath,
                    Arguments =
                    [
                        InstallerConstants.OllamaRouterSetupCommand,
                        InstallerConstants.OllamaRouterWizardFlag,
                        InstallerConstants.OllamaRouterWaitFlag
                    ],
                    WorkingDirectory = workingDirectory,
                    Timeout = null
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            var launchError =
                $"Failed to launch OllamaRouter Setup Wizard ('{cliPath}'): {ex.Message} You can retry the wizard or skip local LLM setup.";
            Report(progress, launchError, InstallerLogLevel.Error, null);
            return new OllamaRouterWizardHandoffResult(
                Succeeded: false,
                ExitCode: InstallerConstants.ExitCodeError,
                ErrorMessage: launchError,
                SyncedRecommendations: null);
        }

        if (executionResult.ExitCode != InstallerConstants.ExitCodeSuccess)
        {
            var detail = !string.IsNullOrWhiteSpace(executionResult.StandardError)
                ? executionResult.StandardError.Trim()
                : executionResult.StandardOutput?.Trim();
            var cleanDetail = detail?.TrimEnd('.');
            var errorMessage = string.IsNullOrWhiteSpace(cleanDetail)
                ? $"OllamaRouter Setup Wizard exited with code {executionResult.ExitCode} before completion. You can retry the wizard or skip local LLM setup."
                : $"OllamaRouter Setup Wizard did not complete (exit code {executionResult.ExitCode}): {cleanDetail}. You can retry the wizard or skip local LLM setup.";

            Report(progress, errorMessage, InstallerLogLevel.Warning, null);
            return new OllamaRouterWizardHandoffResult(
                Succeeded: false,
                ExitCode: executionResult.ExitCode,
                ErrorMessage: errorMessage,
                SyncedRecommendations: null);
        }

        Report(
            progress,
            "OllamaRouter Setup Wizard completed. Synchronizing recommended models...",
            InstallerLogLevel.Info,
            ProgressSyncStartPercent);

        IProgress<InstallerProgressEvent>? syncProgress = progress is null
            ? null
            : new RemappedProgress(progress, ProgressSyncStartPercent);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var summary = await _recommendationSyncService.SyncRecommendationsToConfigAsync(
                    installDirectory,
                    routerBaseUrl,
                    syncProgress,
                    cancellationToken).ConfigureAwait(false);

                return new OllamaRouterWizardHandoffResult(
                    Succeeded: true,
                    ExitCode: InstallerConstants.ExitCodeSuccess,
                    ErrorMessage: null,
                    SyncedRecommendations: summary);
            }
            catch (OllamaRouterSyncException) when (attempt < InstallerConstants.RouterPostWizardSyncAttempts)
            {
                await Task.Delay(InstallerConstants.RouterPostWizardSyncDelayMs, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OllamaRouterSyncException ex)
            {
                var syncError = $"OllamaRouter Setup Wizard completed, but syncing recommendations failed: {ex.Message}";
                Report(progress, syncError, InstallerLogLevel.Error, null);
                return new OllamaRouterWizardHandoffResult(
                    Succeeded: false,
                    ExitCode: InstallerConstants.ExitCodeSuccess,
                    ErrorMessage: syncError,
                    SyncedRecommendations: null);
            }
        }
    }

    private List<string> BuildCandidateDirectories()
    {
        var comparer = _platform.Platform == HostPlatformType.Windows
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var dirs = new List<string>();
        AddCandidate(dirs, _platform.GetEnvironmentVariable(InstallerConstants.EnvOllamaRouterHome), comparer);

        var detected = _installationDetector.Detect();
        AddCandidate(dirs, detected.OllamaRouter.InstallDirectory, comparer);
        AddCandidate(dirs, _installationDetector.GetDefaultOllamaRouterInstallDirectory(), comparer);

        return dirs;
    }

    private static void AddCandidate(List<string> dirs, string? rawPath, StringComparer comparer)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return;
        }

        try
        {
            var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rawPath.Trim()));
            if (!dirs.Contains(normalized, comparer))
            {
                dirs.Add(normalized);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Justification: Malformed environment override path is ignored when probing candidate binaries.
            _ = ex;
        }
    }

    private static void Report(
        IProgress<InstallerProgressEvent>? progress,
        string message,
        InstallerLogLevel level,
        double? percent) =>
        progress?.Report(new InstallerProgressEvent(StepWizardHandoff, message, level, percent));

    private sealed class RemappedProgress(IProgress<InstallerProgressEvent> inner, double startPercent)
        : IProgress<InstallerProgressEvent>
    {
        private const double FullPercent = 100.0;

        public void Report(InstallerProgressEvent value)
        {
            var mappedPercent = value.ProgressPercent.HasValue
                ? startPercent + (Math.Clamp(value.ProgressPercent.Value, 0.0, FullPercent) * ((FullPercent - startPercent) / FullPercent))
                : (double?)null;

            inner.Report(value with { ProgressPercent = mappedPercent });
        }
    }
}
