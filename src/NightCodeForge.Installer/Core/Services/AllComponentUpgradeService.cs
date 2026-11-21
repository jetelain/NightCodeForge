namespace NightCodeForge.Installer.Core.Services;

using System.Globalization;
using System.Text.Json;
using NightCodeForge.Installer.Core.Abstractions;
using NightCodeForge.Installer.Core.Constants;
using NightCodeForge.Installer.Core.Exceptions;
using NightCodeForge.Installer.Core.Models;

/// <summary>
/// Resolves update availability (Online or Air-Gapped Local Bundle) and executes atomic 1-click upgrades
/// across NightCodeForge Web, OllamaRouter, and all 5 companion NuGet global tools.
/// </summary>
public sealed class AllComponentUpgradeService : IAllComponentUpgradeService
{
    private const string StepUpgrade = "1-Click Upgrade";
    private const double ProgressOllamaRouterStart = 5.0;
    private const double ProgressOllamaRouterEnd = 35.0;
    private const double ProgressToolsStart = 35.0;
    private const double ProgressToolsEnd = 60.0;
    private const double ProgressNightCodeForgeStart = 60.0;
    private const double ProgressNightCodeForgeEnd = 100.0;

    private readonly IInstallationDetector _installationDetector;
    private readonly IReleaseManager _releaseManager;
    private readonly ICompanionNuGetProvisioner _nuGetProvisioner;
    private readonly IEnvironmentProvisioner _environmentProvisioner;
    private readonly IPlatformEnvironment _platform;

    public AllComponentUpgradeService(
        IInstallationDetector installationDetector,
        IReleaseManager releaseManager,
        ICompanionNuGetProvisioner nuGetProvisioner,
        IEnvironmentProvisioner environmentProvisioner,
        IPlatformEnvironment platform)
    {
        _installationDetector = installationDetector ?? throw new ArgumentNullException(nameof(installationDetector));
        _releaseManager = releaseManager ?? throw new ArgumentNullException(nameof(releaseManager));
        _nuGetProvisioner = nuGetProvisioner ?? throw new ArgumentNullException(nameof(nuGetProvisioner));
        _environmentProvisioner = environmentProvisioner ?? throw new ArgumentNullException(nameof(environmentProvisioner));
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
    }

    public async Task<UpgradeAvailabilityResult> CheckAvailableUpdatesAsync(
        InstallSourceOptions source,
        string? explicitInstallDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        var discovery = _installationDetector.Detect(source.BundleDirectory, explicitInstallDirectory);
        var warnings = new List<string>();

        ComponentReleaseAsset? ncfRelease = null;
        try
        {
            ncfRelease = await _releaseManager.ResolveLatestNightCodeForgeReleaseAsync(source, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            warnings.Add($"NightCodeForge: Timed out checking updates ({ex.Message})");
        }
        catch (Exception ex) when (ex is ReleaseDownloadException or BundleValidationException or HttpRequestException or JsonException)
        {
            warnings.Add($"NightCodeForge: {ex.Message}");
        }

        ComponentReleaseAsset? routerRelease = null;
        try
        {
            routerRelease = await _releaseManager.ResolveLatestOllamaRouterReleaseAsync(source, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            warnings.Add($"OllamaRouter: Timed out checking updates ({ex.Message})");
        }
        catch (Exception ex) when (ex is ReleaseDownloadException or BundleValidationException or HttpRequestException or JsonException)
        {
            warnings.Add($"OllamaRouter: {ex.Message}");
        }

        var installedNcfVersion = ResolveInstalledVersion(discovery.NightCodeForge, explicitInstallDirectory);
        var isNcfUpdateAvailable = ncfRelease is not null &&
                                   (!discovery.NightCodeForge.IsInstalled ||
                                    IsVersionNewer(ncfRelease.Version, installedNcfVersion));

        var isRouterUpdateAvailable = routerRelease is not null &&
                                      (!discovery.OllamaRouter.IsInstalled ||
                                       IsVersionNewer(routerRelease.Version, discovery.OllamaRouter.Version));

        var canUpgradeFromBundle = source.Mode == InstallSourceMode.AirGappedBundle && ncfRelease is not null;

        return new UpgradeAvailabilityResult(
            Discovery: discovery,
            Source: source,
            NightCodeForgeRelease: ncfRelease,
            OllamaRouterRelease: routerRelease,
            IsNightCodeForgeUpdateAvailable: isNcfUpdateAvailable,
            IsOllamaRouterUpdateAvailable: isRouterUpdateAvailable,
            CanUpgradeFromBundle: canUpgradeFromBundle,
            ResolutionWarning: warnings.Count > 0 ? string.Join(" ", warnings) : null);
    }

    public async Task<AllComponentUpgradeResult> UpgradeAllAsync(
        InstallSourceOptions source,
        string? targetInstallDirectory,
        string? wslDistroName,
        bool createDesktopShortcut,
        IProgress<InstallerProgressEvent>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        ReleaseDeploymentResult? ncfDeployment = null;
        ReleaseDeploymentResult? routerDeployment = null;
        IReadOnlyList<CompanionToolInstallOutcome> toolOutcomes = [];

        try
        {
            var effectiveDistro = string.IsNullOrWhiteSpace(wslDistroName)
                ? InstallerConstants.DefaultWslDistro
                : wslDistroName.Trim();

            Report(progress, "Resolving release packages...", InstallerLogLevel.Info, ProgressOllamaRouterStart);
            var ncfAsset = await _releaseManager.ResolveLatestNightCodeForgeReleaseAsync(source, cancellationToken)
                .ConfigureAwait(false);

            ComponentReleaseAsset? routerAsset = null;
            try
            {
                routerAsset = await _releaseManager.ResolveLatestOllamaRouterReleaseAsync(source, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (BundleValidationException ex) when (source.Mode == InstallSourceMode.AirGappedBundle)
            {
                Report(
                    progress,
                    $"OllamaRouter package not found in local bundle ({ex.Message}); skipping OllamaRouter upgrade.",
                    InstallerLogLevel.Warning,
                    null);
            }

            if (routerAsset is not null)
            {
                Report(progress, "Upgrading OllamaRouter...", InstallerLogLevel.Info, ProgressOllamaRouterStart);
                routerDeployment = await _releaseManager.DeployOllamaRouterAsync(
                    routerAsset,
                    targetInstallDirectory: null,
                    CreateRangeProgress(progress, ProgressOllamaRouterStart, ProgressOllamaRouterEnd),
                    cancellationToken).ConfigureAwait(false);
            }

            string? resolvedWslDistro = null;
            if (_platform.Platform == HostPlatformType.Windows)
            {
                var probe = await _environmentProvisioner.ProbeAsync(effectiveDistro, cancellationToken).ConfigureAwait(false);
                if (probe.WslInstalled && probe.WslDistroInstalled)
                {
                    resolvedWslDistro = probe.ResolvedWslDistroName ?? effectiveDistro;
                }
            }

            Report(progress, "Updating companion NuGet global tools...", InstallerLogLevel.Info, ProgressToolsStart);
            toolOutcomes = await _nuGetProvisioner.ProvisionAllAsync(
                source,
                resolvedWslDistro,
                CreateRangeProgress(progress, ProgressToolsStart, ProgressToolsEnd),
                cancellationToken).ConfigureAwait(false);

            Report(progress, "Upgrading NightCodeForge Web...", InstallerLogLevel.Info, ProgressNightCodeForgeStart);
            ncfDeployment = await _releaseManager.DeployNightCodeForgeAsync(
                ncfAsset,
                targetInstallDirectory,
                createDesktopShortcut,
                CreateRangeProgress(progress, ProgressNightCodeForgeStart, ProgressNightCodeForgeEnd),
                cancellationToken).ConfigureAwait(false);

            Report(progress, "All components upgraded successfully.", InstallerLogLevel.Info, 100);
            return new AllComponentUpgradeResult(
                Succeeded: true,
                NightCodeForgeDeployment: ncfDeployment,
                OllamaRouterDeployment: routerDeployment,
                CompanionToolOutcomes: toolOutcomes,
                ErrorMessage: null);
        }
        catch (Exception ex) when (
            ex is InstallerException or IOException or UnauthorizedAccessException or HttpRequestException or JsonException ||
            (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            Report(progress, $"Upgrade failed: {ex.Message}", InstallerLogLevel.Error, null);
            return new AllComponentUpgradeResult(
                Succeeded: false,
                NightCodeForgeDeployment: ncfDeployment,
                OllamaRouterDeployment: routerDeployment,
                CompanionToolOutcomes: toolOutcomes,
                ErrorMessage: ex.Message);
        }
    }

    internal static bool IsVersionNewer(string? availableVersion, string? installedVersion)
    {
        if (string.IsNullOrWhiteSpace(availableVersion))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(installedVersion))
        {
            return true;
        }

        var available = ParseVersionParts(availableVersion);
        var installed = ParseVersionParts(installedVersion);

        var maxNumericLength = Math.Max(available.Numbers.Length, installed.Numbers.Length);
        for (var i = 0; i < maxNumericLength; i++)
        {
            var availPart = i < available.Numbers.Length ? available.Numbers[i] : 0;
            var instPart = i < installed.Numbers.Length ? installed.Numbers[i] : 0;
            if (availPart != instPart)
            {
                return availPart > instPart;
            }
        }

        // Stable release (no pre-release suffix) is newer than a pre-release with the same numeric version.
        if (string.IsNullOrEmpty(available.PreRelease) != string.IsNullOrEmpty(installed.PreRelease))
        {
            return string.IsNullOrEmpty(available.PreRelease);
        }

        if (!string.IsNullOrEmpty(available.PreRelease) && !string.IsNullOrEmpty(installed.PreRelease))
        {
            return ComparePreRelease(available.PreRelease, installed.PreRelease) > 0;
        }

        return false;
    }

    private static string? ResolveInstalledVersion(InstalledComponentInfo detected, string? explicitInstallDirectory)
    {
        if (!string.IsNullOrWhiteSpace(explicitInstallDirectory))
        {
            try
            {
                var versionPath = Path.Combine(explicitInstallDirectory.Trim(), InstallerConstants.VersionFileName);
                if (File.Exists(versionPath))
                {
                    var raw = File.ReadAllText(versionPath).Trim();
                    if (!string.IsNullOrWhiteSpace(raw))
                    {
                        return raw;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Justification: Unreadable or malformed explicit version path falls back to detector result.
                _ = ex;
            }
        }

        return detected.Version;
    }

    private static (long[] Numbers, string PreRelease) ParseVersionParts(string rawVersion)
    {
        var clean = rawVersion.Trim();
        if (clean.StartsWith('v') || clean.StartsWith('V'))
        {
            clean = clean[1..];
        }

        var buildMetaIdx = clean.IndexOf('+');
        if (buildMetaIdx >= 0)
        {
            clean = clean[..buildMetaIdx];
        }

        var dashIdx = clean.IndexOf('-');
        var corePart = dashIdx >= 0 ? clean[..dashIdx] : clean;
        var preRelease = dashIdx >= 0 ? clean[(dashIdx + 1)..] : string.Empty;

        var segments = corePart.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var numbers = new long[segments.Length];
        for (var i = 0; i < segments.Length; i++)
        {
            if (long.TryParse(segments[i], NumberStyles.None, CultureInfo.InvariantCulture, out var num))
            {
                numbers[i] = num;
            }
        }

        return (numbers, preRelease);
    }

    private static int ComparePreRelease(string left, string right)
    {
        var leftParts = left.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var rightParts = right.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var count = Math.Max(leftParts.Length, rightParts.Length);

        for (var i = 0; i < count; i++)
        {
            if (i >= leftParts.Length)
            {
                return -1;
            }

            if (i >= rightParts.Length)
            {
                return 1;
            }

            var leftIsNum = long.TryParse(leftParts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var leftNum);
            var rightIsNum = long.TryParse(rightParts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var rightNum);

            var cmp = (leftIsNum, rightIsNum) switch
            {
                (true, true) => leftNum.CompareTo(rightNum),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.Compare(leftParts[i], rightParts[i], StringComparison.OrdinalIgnoreCase)
            };

            if (cmp != 0)
            {
                return cmp;
            }
        }

        return 0;
    }

    private static IProgress<InstallerProgressEvent>? CreateRangeProgress(
        IProgress<InstallerProgressEvent>? outer,
        double startPercent,
        double endPercent) =>
        outer is null ? null : new RangeProgressAdapter(outer, startPercent, endPercent);

    private static void Report(
        IProgress<InstallerProgressEvent>? progress,
        string message,
        InstallerLogLevel level,
        double? percent) =>
        progress?.Report(new InstallerProgressEvent(StepUpgrade, message, level, percent));

    private sealed class RangeProgressAdapter(
        IProgress<InstallerProgressEvent> inner,
        double startPercent,
        double endPercent) : IProgress<InstallerProgressEvent>
    {
        private const double FullPercent = 100.0;

        public void Report(InstallerProgressEvent value)
        {
            var mapped = value.ProgressPercent.HasValue
                ? startPercent + (Math.Clamp(value.ProgressPercent.Value, 0.0, FullPercent) / FullPercent * (endPercent - startPercent))
                : (double?)null;

            inner.Report(value with { ProgressPercent = mapped });
        }
    }
}
