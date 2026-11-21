namespace NightCodeForge.Installer.Core.Services;

using System.Security;
using System.Text;
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
    private const string CmdTool = "tool";
    private const string CmdList = "list";
    private const string CmdInstall = "install";
    private const string CmdUpdate = "update";
    private const string CmdUninstall = "uninstall";
    private const string FlagGlobal = "-g";
    private const string FlagConfigFile = "--configfile";
    private const string FlagNoCache = "--no-cache";
    private const string FlagPrerelease = "--prerelease";
    private const int TableSeparatorMinLength = 3;
    private const double ProgressRangeMax = 100.0;
    private const double WslProgressStepOffset = 5.0;

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
        string? hostNuGetConfigPath = null;
        string? wslNuGetConfigHostPath = null;
        string? wslNuGetConfigPath = null;

        if (source.Mode == InstallSourceMode.AirGappedBundle)
        {
            if (string.IsNullOrWhiteSpace(source.BundleDirectory) || !Directory.Exists(source.BundleDirectory))
            {
                throw new BundleValidationException(
                    $"Air-gapped NuGet bundle directory was not found: '{source.BundleDirectory}'.");
            }

            hostBundleDir = Path.GetFullPath(source.BundleDirectory);
        }

        var provisionWsl = _platform.Platform == HostPlatformType.Windows &&
                           !string.IsNullOrWhiteSpace(wslDistroName);

        try
        {
            if (hostBundleDir is not null)
            {
                hostNuGetConfigPath = await WriteIsolatedBundleNuGetConfigAsync(hostBundleDir, cancellationToken).ConfigureAwait(false);
            }

            var existingHostTools = await ListGlobalToolsAsync(wslDistro: null, cancellationToken).ConfigureAwait(false);
            Dictionary<string, string> existingWslTools = new(StringComparer.OrdinalIgnoreCase);

            if (provisionWsl)
            {
                existingWslTools = await ListGlobalToolsAsync(wslDistroName, cancellationToken).ConfigureAwait(false);

                if (source.Mode == InstallSourceMode.AirGappedBundle && hostBundleDir is not null)
                {
                    wslBundleDir = await TranslateToWslPathAsync(wslDistroName!, hostBundleDir, cancellationToken).ConfigureAwait(false);
                    wslNuGetConfigHostPath = await WriteIsolatedBundleNuGetConfigAsync(wslBundleDir, cancellationToken).ConfigureAwait(false);
                    wslNuGetConfigPath = await TranslateToWslPathAsync(wslDistroName!, wslNuGetConfigHostPath, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            var outcomes = new List<CompanionToolInstallOutcome>();

            for (var i = 0; i < Catalog.Count; i++)
            {
                var spec = Catalog[i];
                var basePercent = (double)i / Catalog.Count * ProgressRangeMax;
                var isPrereleaseBundle = hostBundleDir is not null && HasPrereleaseBundlePackage(hostBundleDir, spec.PackageId);
                var itemKey = DeploymentComponentKeys.ForCompanionTool(spec.PackageId);
                var willInstallWsl = provisionWsl && spec.InstallInWsl;

                try
                {
                    if (spec.InstallOnHost)
                    {
                        progress?.Report(new InstallerProgressEvent(
                            StepTools,
                            $"Provisioning {spec.PackageId} ({spec.ToolCommand}) on Host...",
                            InstallerLogLevel.Info,
                            basePercent,
                            itemKey,
                            ProvisioningStatus.InProgress));

                        var isInstalled = existingHostTools.ContainsKey(spec.PackageId);
                        var outcome = await InstallOrUpgradeToolAsync(
                            spec,
                            source.Mode,
                            hostBundleDir,
                            hostNuGetConfigPath,
                            isPrereleaseBundle,
                            wslDistro: null,
                            isInstalled,
                            cancellationToken).ConfigureAwait(false);
                        outcomes.Add(outcome);

                        if (!willInstallWsl)
                        {
                            progress?.Report(new InstallerProgressEvent(
                                StepTools,
                                $"{spec.PackageId} ({spec.ToolCommand}) ready on Host.",
                                InstallerLogLevel.Info,
                                ((double)(i + 1) / Catalog.Count) * ProgressRangeMax,
                                itemKey,
                                outcome.Status));
                        }
                        else
                        {
                            progress?.Report(new InstallerProgressEvent(
                                StepTools,
                                $"Host ✓ — provisioning {spec.PackageId} ({spec.ToolCommand}) inside WSL2 '{wslDistroName}'...",
                                InstallerLogLevel.Info,
                                basePercent + WslProgressStepOffset,
                                itemKey,
                                ProvisioningStatus.InProgress));
                        }
                    }

                    if (willInstallWsl)
                    {
                        if (!spec.InstallOnHost)
                        {
                            progress?.Report(new InstallerProgressEvent(
                                StepTools,
                                $"Provisioning {spec.PackageId} ({spec.ToolCommand}) inside WSL2 '{wslDistroName}'...",
                                InstallerLogLevel.Info,
                                basePercent + WslProgressStepOffset,
                                itemKey,
                                ProvisioningStatus.InProgress));
                        }

                        var isInstalledInWsl = existingWslTools.ContainsKey(spec.PackageId);
                        var outcome = await InstallOrUpgradeToolAsync(
                            spec,
                            source.Mode,
                            wslBundleDir,
                            wslNuGetConfigPath,
                            isPrereleaseBundle,
                            wslDistro: wslDistroName,
                            isInstalledInWsl,
                            cancellationToken).ConfigureAwait(false);
                        outcomes.Add(outcome);

                        var finalScopeMessage = spec.InstallOnHost
                            ? $"{spec.PackageId} ({spec.ToolCommand}) ready on Host and WSL2 '{wslDistroName}'."
                            : $"{spec.PackageId} ({spec.ToolCommand}) ready in WSL2 '{wslDistroName}'.";
                        progress?.Report(new InstallerProgressEvent(
                            StepTools,
                            finalScopeMessage,
                            InstallerLogLevel.Info,
                            ((double)(i + 1) / Catalog.Count) * ProgressRangeMax,
                            itemKey,
                            outcome.Status));
                    }
                }
                catch (NuGetToolProvisioningException ex)
                {
                    progress?.Report(new InstallerProgressEvent(
                        StepTools,
                        ex.Message,
                        InstallerLogLevel.Error,
                        null,
                        itemKey,
                        ProvisioningStatus.Failed));
                    throw;
                }
            }

            progress?.Report(new InstallerProgressEvent(
                StepTools,
                "All companion NuGet global tools provisioned.",
                InstallerLogLevel.Info,
                ProgressRangeMax));

            return outcomes;
        }
        finally
        {
            DeleteTempFileBestEffort(hostNuGetConfigPath);
            DeleteTempFileBestEffort(wslNuGetConfigHostPath);
        }
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
            if (line.Length >= TableSeparatorMinLength && line.All(static c => c == '-'))
            {
                lastSeparatorIndex = i;
            }
        }

        var startIndex = lastSeparatorIndex >= 0 ? lastSeparatorIndex + 1 : 0;
        for (var i = startIndex; i < lines.Length; i++)
        {
            var line = lines[i];
            if (lastSeparatorIndex < 0 && line.StartsWith(InstallerConstants.NuGetPackageIdHeaderPrefix, StringComparison.OrdinalIgnoreCase))
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

    internal static bool HasPrereleaseBundlePackage(string bundleDir, string packageId)
    {
        if (string.IsNullOrWhiteSpace(bundleDir) || !Directory.Exists(bundleDir))
        {
            return false;
        }

        var prefix = packageId + ".";
        foreach (var filePath in Directory.EnumerateFiles(bundleDir, "*" + InstallerConstants.NupkgExtension))
        {
            var fileName = Path.GetFileNameWithoutExtension(filePath);
            if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var versionPart = fileName[prefix.Length..];
            if (versionPart.Length > 0 && char.IsAsciiDigit(versionPart[0]) && versionPart.Contains('-', StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    internal static string BuildIsolatedNuGetConfigXml(string bundleSourcePath)
    {
        var escaped = SecurityElement.Escape(bundleSourcePath) ?? bundleSourcePath;
        return
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
            "<configuration>\n" +
            "  <packageSources>\n" +
            "    <clear />\n" +
            $"    <add key=\"AirGappedBundle\" value=\"{escaped}\" />\n" +
            "  </packageSources>\n" +
            "  <packageSourceMapping>\n" +
            "    <clear />\n" +
            "  </packageSourceMapping>\n" +
            "</configuration>\n";
    }

    private async Task<Dictionary<string, string>> ListGlobalToolsAsync(
        string? wslDistro,
        CancellationToken cancellationToken)
    {
        var result = await RunDotNetCommandAsync(wslDistro, [CmdTool, CmdList, FlagGlobal], cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != InstallerConstants.ExitCodeSuccess)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var cleaned = EnvironmentProvisioner.StripNullCharacters(result.StandardOutput);
        return ParseToolListOutput(cleaned);
    }

    private async Task<CompanionToolInstallOutcome> InstallOrUpgradeToolAsync(
        CompanionNuGetToolSpec spec,
        InstallSourceMode mode,
        string? bundleSourceDir,
        string? isolatedConfigPath,
        bool allowPrerelease,
        string? wslDistro,
        bool currentlyInstalled,
        CancellationToken cancellationToken)
    {
        var targetEnv = wslDistro is null ? EnvHost : $"{EnvWsl}:{wslDistro}";

        if (mode == InstallSourceMode.AirGappedBundle)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(bundleSourceDir);
            ArgumentException.ThrowIfNullOrWhiteSpace(isolatedConfigPath);
            await ClearNuGetPackageCacheAsync(spec.PackageId, wslDistro, cancellationToken).ConfigureAwait(false);

            var primaryVerb = currentlyInstalled ? CmdUpdate : CmdInstall;
            var primaryArgs = BuildAirGappedToolArgs(primaryVerb, spec.PackageId, isolatedConfigPath, allowPrerelease);
            var result = await RunDotNetCommandAsync(wslDistro, primaryArgs, cancellationToken).ConfigureAwait(false);

            // Air-gapped local rebuilds may retain an identical package version; uninstall + install handles same-version swaps cleanly.
            var combinedOutput = result.StandardOutput + "\n" + result.StandardError;
            if ((result.ExitCode != InstallerConstants.ExitCodeSuccess && currentlyInstalled) ||
                combinedOutput.Contains(InstallerConstants.AlreadyInstalledMarker, StringComparison.OrdinalIgnoreCase))
            {
                await RunDotNetCommandAsync(wslDistro, [CmdTool, CmdUninstall, FlagGlobal, spec.PackageId], cancellationToken)
                    .ConfigureAwait(false);

                var installArgs = BuildAirGappedToolArgs(CmdInstall, spec.PackageId, isolatedConfigPath, allowPrerelease);
                result = await RunDotNetCommandAsync(wslDistro, installArgs, cancellationToken).ConfigureAwait(false);
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

        var onlineVerb = currentlyInstalled ? CmdUpdate : CmdInstall;
        var onlineResult = await RunDotNetCommandAsync(
            wslDistro,
            [CmdTool, onlineVerb, FlagGlobal, spec.PackageId],
            cancellationToken).ConfigureAwait(false);

        if (onlineResult.ExitCode != InstallerConstants.ExitCodeSuccess &&
            !currentlyInstalled &&
            (onlineResult.StandardOutput + onlineResult.StandardError).Contains(
                InstallerConstants.AlreadyInstalledMarker,
                StringComparison.OrdinalIgnoreCase))
        {
            onlineResult = await RunDotNetCommandAsync(
                wslDistro,
                [CmdTool, CmdUpdate, FlagGlobal, spec.PackageId],
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
            [CmdTool, CmdUninstall, FlagGlobal, spec.PackageId],
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
            combined.Contains(InstallerConstants.CannotFindFileMarker, StringComparison.OrdinalIgnoreCase) ||
            combined.Contains(InstallerConstants.NoSuchFileMarker, StringComparison.OrdinalIgnoreCase) ||
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
            var hostCachePath = Path.Combine(
                _platform.UserHomeDirectory,
                InstallerConstants.NuGetDirectoryName,
                InstallerConstants.NuGetPackagesDirectoryName,
                packageIdLower);
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
                Arguments = ["-d", wslDistro, "--", InstallerConstants.BashExecutable, "-c", $"rm -rf \"$HOME/.nuget/packages/{packageIdLower}\""],
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

        var wslArgs = new List<string>(dotnetArguments.Count + 4)
        {
            "-d",
            wslDistro,
            "--",
            InstallerConstants.DotNetExecutable
        };
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

    private static IReadOnlyList<string> BuildAirGappedToolArgs(
        string verb,
        string packageId,
        string isolatedConfigPath,
        bool allowPrerelease)
    {
        var args = new List<string>(9)
        {
            CmdTool,
            verb,
            FlagGlobal,
            packageId,
            FlagConfigFile,
            isolatedConfigPath,
            FlagNoCache
        };

        if (allowPrerelease)
        {
            args.Add(FlagPrerelease);
        }

        return args;
    }

    private static async Task<string> WriteIsolatedBundleNuGetConfigAsync(
        string bundleSourcePath,
        CancellationToken cancellationToken)
    {
        var configPath = Path.Combine(Path.GetTempPath(), $"ncf-nuget-{Guid.NewGuid():N}.config");
        var xml = BuildIsolatedNuGetConfigXml(bundleSourcePath);
        await File.WriteAllTextAsync(configPath, xml, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        return configPath;
    }

    private static void DeleteTempFileBestEffort(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Justification: Best-effort temp nuget.config cleanup must not mask provisioning result.
            _ = ex;
        }
    }

    private static bool IsWslUnavailableError(string combinedOutput) =>
        combinedOutput.Contains(InstallerConstants.WslDistroNotFoundMarker, StringComparison.OrdinalIgnoreCase) ||
        combinedOutput.Contains(InstallerConstants.WslNoDistrosMarker, StringComparison.OrdinalIgnoreCase) ||
        combinedOutput.Contains(InstallerConstants.WslDistroNotFoundErrorCode, StringComparison.OrdinalIgnoreCase) ||
        combinedOutput.Contains(InstallerConstants.DotNetCommandNotFoundMarker, StringComparison.OrdinalIgnoreCase) ||
        combinedOutput.Contains("execvpe dotnet failed", StringComparison.OrdinalIgnoreCase);

    private static string FormatError(ProcessExecutionResult result)
    {
        var combined = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput
            : $"{result.StandardError} {result.StandardOutput}";
        return $"exit code {result.ExitCode}: {combined.Trim()}";
    }
}
