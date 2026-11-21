namespace NightCodeForge.Installer.Core.Services;

using NightCodeForge.Installer.Core.Abstractions;
using NightCodeForge.Installer.Core.Constants;
using NightCodeForge.Installer.Core.Exceptions;
using NightCodeForge.Installer.Core.Models;

/// <summary>
/// Cross-platform NuGet Global Tool provisioner supporting online NuGet.org and air-gapped local bundle feeds,
/// including same-version local debug rebuild reinstallation and WSL2 path translation.
/// </summary>
public sealed class CompanionNuGetProvisioner : ICompanionNuGetProvisioner
{
    private const string StepTools = "Companion NuGet Tools";
    private const string EnvHost = "Host";
    private const string EnvWsl = "WSL2";

    private readonly IPlatformEnvironment _platform;
    private readonly IProcessRunner _processRunner;

    public CompanionNuGetProvisioner(
        IPlatformEnvironment platform,
        IProcessRunner processRunner)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
    }

    public IReadOnlyList<CompanionNuGetToolSpec> Catalog => CompanionToolCatalog.All;

    public async Task<IReadOnlyList<CompanionToolStatus>> ProbeToolsAsync(
        string? wslDistroName,
        CancellationToken cancellationToken = default)
    {
        var hostTools = await ListGlobalToolsAsync(wslDistro: null, cancellationToken).ConfigureAwait(false);
        Dictionary<string, string> wslTools = new(StringComparer.OrdinalIgnoreCase);

        if (_platform.Platform == HostPlatformType.Windows && !string.IsNullOrWhiteSpace(wslDistroName))
        {
            wslTools = await ListGlobalToolsAsync(wslDistroName, cancellationToken).ConfigureAwait(false);
        }

        var statuses = new List<CompanionToolStatus>(Catalog.Count);
        foreach (var spec in Catalog)
        {
            var onHost = hostTools.TryGetValue(spec.PackageId, out var hostVer);
            string? wslVer = null;
            var inWsl = spec.InstallInWsl && wslTools.TryGetValue(spec.PackageId, out wslVer);

            statuses.Add(new CompanionToolStatus(
                Spec: spec,
                InstalledOnHost: onHost,
                HostVersion: onHost ? hostVer : null,
                InstalledInWsl: inWsl,
                WslVersion: inWsl ? wslVer : null));
        }

        return statuses;
    }

    public async Task<IReadOnlyList<CompanionToolInstallOutcome>> ProvisionAllAsync(
        InstallSourceOptions source,
        string? wslDistroName,
        IProgress<InstallerProgressEvent>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        string? hostBundleDir = null;
        string? wslBundleDir = null;

        if (source.Mode == InstallSourceMode.AirGappedBundle)
        {
            if (string.IsNullOrWhiteSpace(source.BundleDirectory) || !Directory.Exists(source.BundleDirectory))
            {
                throw new BundleValidationException(
                    $"Air-gapped NuGet bundle directory was not found: '{source.BundleDirectory}'.");
            }

            hostBundleDir = Path.GetFullPath(source.BundleDirectory);
        }

        var existingHostTools = await ListGlobalToolsAsync(wslDistro: null, cancellationToken).ConfigureAwait(false);
        Dictionary<string, string> existingWslTools = new(StringComparer.OrdinalIgnoreCase);

        var provisionWsl = _platform.Platform == HostPlatformType.Windows &&
                           !string.IsNullOrWhiteSpace(wslDistroName);

        if (provisionWsl)
        {
            existingWslTools = await ListGlobalToolsAsync(wslDistroName, cancellationToken).ConfigureAwait(false);

            if (source.Mode == InstallSourceMode.AirGappedBundle && hostBundleDir is not null)
            {
                wslBundleDir = await TranslateToWslPathAsync(wslDistroName!, hostBundleDir, cancellationToken).ConfigureAwait(false);
            }
        }

        var outcomes = new List<CompanionToolInstallOutcome>();

        for (var i = 0; i < Catalog.Count; i++)
        {
            var spec = Catalog[i];
            var basePercent = (double)i / Catalog.Count * 100.0;

            if (spec.InstallOnHost)
            {
                progress?.Report(new InstallerProgressEvent(
                    StepTools,
                    $"Provisioning {spec.PackageId} ({spec.ToolCommand}) on Host...",
                    InstallerLogLevel.Info,
                    basePercent));

                var isInstalled = existingHostTools.ContainsKey(spec.PackageId);
                var outcome = await InstallOrUpgradeToolAsync(
                    spec,
                    source.Mode,
                    hostBundleDir,
                    wslDistro: null,
                    isInstalled,
                    cancellationToken).ConfigureAwait(false);
                outcomes.Add(outcome);
            }

            if (provisionWsl && spec.InstallInWsl)
            {
                progress?.Report(new InstallerProgressEvent(
                    StepTools,
                    $"Provisioning {spec.PackageId} ({spec.ToolCommand}) inside WSL2 '{wslDistroName}'...",
                    InstallerLogLevel.Info,
                    basePercent + 5));

                var isInstalledInWsl = existingWslTools.ContainsKey(spec.PackageId);
                var outcome = await InstallOrUpgradeToolAsync(
                    spec,
                    source.Mode,
                    wslBundleDir,
                    wslDistro: wslDistroName,
                    isInstalledInWsl,
                    cancellationToken).ConfigureAwait(false);
                outcomes.Add(outcome);
            }
        }

        progress?.Report(new InstallerProgressEvent(
            StepTools,
            "All companion NuGet global tools provisioned.",
            InstallerLogLevel.Info,
            100));

        return outcomes;
    }

    public async Task<IReadOnlyList<CompanionToolInstallOutcome>> UninstallAllAsync(
        string? wslDistroName,
        IProgress<InstallerProgressEvent>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var outcomes = new List<CompanionToolInstallOutcome>();
        var removeWsl = _platform.Platform == HostPlatformType.Windows && !string.IsNullOrWhiteSpace(wslDistroName);

        foreach (var spec in Catalog)
        {
            if (spec.InstallOnHost)
            {
                progress?.Report(new InstallerProgressEvent(
                    StepTools,
                    $"Removing global tool {spec.PackageId} from Host...",
                    InstallerLogLevel.Info,
                    null));

                outcomes.Add(await UninstallToolAsync(spec, wslDistro: null, cancellationToken).ConfigureAwait(false));
            }

            if (removeWsl && spec.InstallInWsl)
            {
                progress?.Report(new InstallerProgressEvent(
                    StepTools,
                    $"Removing global tool {spec.PackageId} from WSL2 '{wslDistroName}'...",
                    InstallerLogLevel.Info,
                    null));

                outcomes.Add(await UninstallToolAsync(spec, wslDistro: wslDistroName, cancellationToken).ConfigureAwait(false));
            }
        }

        return outcomes;
    }

    internal static Dictionary<string, string> ParseToolListOutput(string output)
    {
        var tools = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(output))
        {
            return tools;
        }

        var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(static l => l.Trim())
            .Where(static l => !string.IsNullOrWhiteSpace(l))
            .ToArray();

        var lastSeparatorIndex = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length >= 3 && line.All(static c => c == '-'))
            {
                lastSeparatorIndex = i;
            }
        }

        var startIndex = lastSeparatorIndex >= 0 ? lastSeparatorIndex + 1 : 0;
        for (var i = startIndex; i < lines.Length; i++)
        {
            var line = lines[i];
            if (lastSeparatorIndex < 0 && line.StartsWith("Package Id", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[1].Length > 0 && char.IsAsciiDigit(parts[1][0]))
            {
                tools[parts[0]] = parts[1];
            }
        }

        return tools;
    }

    private async Task<Dictionary<string, string>> ListGlobalToolsAsync(
        string? wslDistro,
        CancellationToken cancellationToken)
    {
        var result = await RunDotNetCommandAsync(wslDistro, ["tool", "list", "-g"], cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != InstallerConstants.ExitCodeSuccess)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        return ParseToolListOutput(result.StandardOutput);
    }

    private async Task<CompanionToolInstallOutcome> InstallOrUpgradeToolAsync(
        CompanionNuGetToolSpec spec,
        InstallSourceMode mode,
        string? bundleSourceDir,
        string? wslDistro,
        bool currentlyInstalled,
        CancellationToken cancellationToken)
    {
        var targetEnv = wslDistro is null ? EnvHost : $"{EnvWsl}:{wslDistro}";

        if (mode == InstallSourceMode.AirGappedBundle)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(bundleSourceDir);
            await ClearNuGetPackageCacheAsync(spec.PackageId, wslDistro, cancellationToken).ConfigureAwait(false);

            var primaryVerb = currentlyInstalled ? "update" : "install";
            var primaryArgs = new[]
            {
                "tool", primaryVerb, "-g", spec.PackageId, "--add-source", bundleSourceDir, "--no-cache"
            };
            var result = await RunDotNetCommandAsync(wslDistro, primaryArgs, cancellationToken).ConfigureAwait(false);

            // Air-gapped local rebuilds may retain an identical package version; uninstall + install handles same-version swaps cleanly.
            var combinedOutput = result.StandardOutput + "\n" + result.StandardError;
            if (result.ExitCode != InstallerConstants.ExitCodeSuccess ||
                combinedOutput.Contains(InstallerConstants.AlreadyInstalledMarker, StringComparison.OrdinalIgnoreCase))
            {
                await RunDotNetCommandAsync(wslDistro, ["tool", "uninstall", "-g", spec.PackageId], cancellationToken)
                    .ConfigureAwait(false);

                result = await RunDotNetCommandAsync(
                    wslDistro,
                    ["tool", "install", "-g", spec.PackageId, "--add-source", bundleSourceDir, "--no-cache"],
                    cancellationToken).ConfigureAwait(false);
            }

            if (result.ExitCode != InstallerConstants.ExitCodeSuccess)
            {
                throw new NuGetToolProvisioningException(
                    $"Failed to install '{spec.PackageId}' from air-gapped bundle '{bundleSourceDir}' on {targetEnv}: {FormatError(result)}");
            }

            return new CompanionToolInstallOutcome(
                spec.PackageId,
                targetEnv,
                ProvisioningStatus.Completed,
                Version: null,
                Message: $"Installed/updated '{spec.PackageId}' from local bundle.");
        }

        var onlineVerb = currentlyInstalled ? "update" : "install";
        var onlineResult = await RunDotNetCommandAsync(
            wslDistro,
            ["tool", onlineVerb, "-g", spec.PackageId],
            cancellationToken).ConfigureAwait(false);

        if (onlineResult.ExitCode != InstallerConstants.ExitCodeSuccess &&
            !currentlyInstalled &&
            (onlineResult.StandardOutput + onlineResult.StandardError).Contains(
                InstallerConstants.AlreadyInstalledMarker,
                StringComparison.OrdinalIgnoreCase))
        {
            onlineResult = await RunDotNetCommandAsync(
                wslDistro,
                ["tool", "update", "-g", spec.PackageId],
                cancellationToken).ConfigureAwait(false);
        }

        if (onlineResult.ExitCode != InstallerConstants.ExitCodeSuccess)
        {
            throw new NuGetToolProvisioningException(
                $"Failed to {onlineVerb} NuGet tool '{spec.PackageId}' on {targetEnv}: {FormatError(onlineResult)}");
        }

        return new CompanionToolInstallOutcome(
            spec.PackageId,
            targetEnv,
            ProvisioningStatus.Completed,
            Version: null,
            Message: $"Provisioned '{spec.PackageId}' ({onlineVerb}).");
    }

    private async Task<CompanionToolInstallOutcome> UninstallToolAsync(
        CompanionNuGetToolSpec spec,
        string? wslDistro,
        CancellationToken cancellationToken)
    {
        var targetEnv = wslDistro is null ? EnvHost : $"{EnvWsl}:{wslDistro}";
        var result = await RunDotNetCommandAsync(
            wslDistro,
            ["tool", "uninstall", "-g", spec.PackageId],
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode == InstallerConstants.ExitCodeSuccess)
        {
            return new CompanionToolInstallOutcome(
                spec.PackageId,
                targetEnv,
                ProvisioningStatus.Completed,
                Version: null,
                Message: $"Uninstalled '{spec.PackageId}' from {targetEnv}.");
        }

        var stdout = EnvironmentProvisioner.StripNullCharacters(result.StandardOutput);
        var stderr = EnvironmentProvisioner.StripNullCharacters(result.StandardError);
        var combined = stdout + "\n" + stderr;

        if (combined.Contains(InstallerConstants.NotInstalledMarker, StringComparison.OrdinalIgnoreCase) ||
            combined.Contains(InstallerConstants.NotFoundMarker, StringComparison.OrdinalIgnoreCase) ||
            (wslDistro is not null && IsWslUnavailableError(combined)))
        {
            return new CompanionToolInstallOutcome(
                spec.PackageId,
                targetEnv,
                ProvisioningStatus.Skipped,
                Version: null,
                Message: $"Skipped uninstall of '{spec.PackageId}' on {targetEnv}: {combined.Trim()}");
        }

        throw new NuGetToolProvisioningException(
            $"Failed to uninstall NuGet tool '{spec.PackageId}' from {targetEnv}: {FormatError(result)}");
    }

    private async Task ClearNuGetPackageCacheAsync(
        string packageId,
        string? wslDistro,
        CancellationToken cancellationToken)
    {
        var packageIdLower = packageId.ToLowerInvariant();
        if (wslDistro is null)
        {
            var hostCachePath = Path.Combine(_platform.UserHomeDirectory, ".nuget", "packages", packageIdLower);
            if (Directory.Exists(hostCachePath))
            {
                Directory.Delete(hostCachePath, recursive: true);
            }

            return;
        }

        await _processRunner.RunAsync(
            new ProcessRunRequest
            {
                FileName = InstallerConstants.WslExecutable,
                Arguments = ["-d", wslDistro, "--", "bash", "-c", $"rm -rf \"$HOME/.nuget/packages/{packageIdLower}\""],
                EnvironmentVariables = new Dictionary<string, string>
                {
                    [InstallerConstants.WslUtf8EnvVar] = "1"
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> TranslateToWslPathAsync(
        string distro,
        string hostPath,
        CancellationToken cancellationToken)
    {
        var forwardSlash = hostPath.Replace('\\', '/');
        var result = await _processRunner.RunAsync(
            new ProcessRunRequest
            {
                FileName = InstallerConstants.WslExecutable,
                Arguments = ["-d", distro, "--", "wslpath", "-u", forwardSlash],
                EnvironmentVariables = new Dictionary<string, string>
                {
                    [InstallerConstants.WslUtf8EnvVar] = "1"
                }
            },
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != InstallerConstants.ExitCodeSuccess)
        {
            throw new NuGetToolProvisioningException(
                $"Failed to translate bundle path '{hostPath}' via wslpath in distro '{distro}': {FormatError(result)}");
        }

        return EnvironmentProvisioner.StripNullCharacters(result.StandardOutput).Trim();
    }

    private Task<ProcessExecutionResult> RunDotNetCommandAsync(
        string? wslDistro,
        IReadOnlyList<string> dotnetArguments,
        CancellationToken cancellationToken)
    {
        if (wslDistro is null)
        {
            return _processRunner.RunAsync(
                new ProcessRunRequest
                {
                    FileName = InstallerConstants.DotNetExecutable,
                    Arguments = dotnetArguments,
                    EnvironmentVariables = new Dictionary<string, string>
                    {
                        [InstallerConstants.DotNetCliUiLanguageEnvVar] = InstallerConstants.DotNetCliUiLanguageEnglish
                    }
                },
                cancellationToken);
        }

        var wslArgs = new List<string>(dotnetArguments.Count + 4) { "-d", wslDistro, "--", InstallerConstants.DotNetExecutable };
        wslArgs.AddRange(dotnetArguments);

        return _processRunner.RunAsync(
            new ProcessRunRequest
            {
                FileName = InstallerConstants.WslExecutable,
                Arguments = wslArgs,
                EnvironmentVariables = new Dictionary<string, string>
                {
                    [InstallerConstants.WslUtf8EnvVar] = "1",
                    [InstallerConstants.DotNetCliUiLanguageEnvVar] = InstallerConstants.DotNetCliUiLanguageEnglish,
                    [InstallerConstants.WslEnvVar] = BuildWslEnvWithDotNetLanguage()
                }
            },
            cancellationToken);
    }

    private string BuildWslEnvWithDotNetLanguage()
    {
        var existing = _platform.GetEnvironmentVariable(InstallerConstants.WslEnvVar);
        if (string.IsNullOrWhiteSpace(existing))
        {
            return InstallerConstants.DotNetCliUiLanguageEnvVar;
        }

        var parts = existing.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            var nameOnly = part.Split('/')[0];
            if (string.Equals(nameOnly, InstallerConstants.DotNetCliUiLanguageEnvVar, StringComparison.OrdinalIgnoreCase))
            {
                return existing;
            }
        }

        return $"{existing}:{InstallerConstants.DotNetCliUiLanguageEnvVar}";
    }

    private static bool IsWslUnavailableError(string combinedOutput) =>
        combinedOutput.Contains(InstallerConstants.WslDistroNotFoundMarker, StringComparison.OrdinalIgnoreCase) ||
        combinedOutput.Contains(InstallerConstants.WslNoDistrosMarker, StringComparison.OrdinalIgnoreCase) ||
        combinedOutput.Contains(InstallerConstants.WslDistroNotFoundErrorCode, StringComparison.OrdinalIgnoreCase) ||
        combinedOutput.Contains(InstallerConstants.DotNetCommandNotFoundMarker, StringComparison.OrdinalIgnoreCase);

    private static string FormatError(ProcessExecutionResult result)
    {
        var combined = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput
            : $"{result.StandardError} {result.StandardOutput}";
        return $"exit code {result.ExitCode}: {combined.Trim()}";
    }
}
