namespace NightCodeForge.Installer.Cli;

using NightCodeForge.Installer.Core.Abstractions;
using NightCodeForge.Installer.Core.Constants;
using NightCodeForge.Installer.Core.Models;

/// <summary>
/// Resolved startup plan describing whether <c>NightCodeForge.Installer</c> runs headlessly or opens a specific Avalonia UI mode.
/// </summary>
public sealed record InstallerStartupPlan(
    bool RunHeadless,
    InstallerLaunchMode HeadlessAction,
    InstallerUiMode InitialUiMode,
    bool IsUpgradeWizardMode,
    bool AutoFocusUpgrade,
    InstallSourceOptions EffectiveSource,
    string EffectiveInstallDirectory,
    string EffectiveWslDistroName,
    InstallationDiscoveryResult Discovery,
    InstallerCliOptions CliOptions);

/// <summary>
/// Maps parsed CLI flags and detected host installations onto a deterministic <see cref="InstallerStartupPlan"/>.
/// </summary>
public static class InstallerStartupRouter
{
    public static InstallerStartupPlan Resolve(
        InstallerCliOptions cliOptions,
        InstallationDiscoveryResult discovery,
        IPlatformEnvironment platform)
    {
        ArgumentNullException.ThrowIfNull(cliOptions);
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(platform);

        var runHeadless = cliOptions.Mode == InstallerLaunchMode.Help ||
                          (cliOptions.Mode is InstallerLaunchMode.Uninstall or InstallerLaunchMode.Upgrade &&
                           cliOptions.UninstallOptions.Quiet);

        var headlessAction = runHeadless ? cliOptions.Mode : InstallerLaunchMode.DefaultAuto;

        var initialUiMode = cliOptions.Mode switch
        {
            InstallerLaunchMode.Uninstall => InstallerUiMode.InteractiveUninstaller,
            InstallerLaunchMode.Upgrade => InstallerUiMode.MaintenanceHub,
            InstallerLaunchMode.Wizard => InstallerUiMode.OobeWizard,
            _ => discovery.NightCodeForge.IsInstalled
                ? InstallerUiMode.MaintenanceHub
                : InstallerUiMode.OobeWizard
        };

        var isUpgradeWizardMode = cliOptions.Mode == InstallerLaunchMode.Wizard && discovery.NightCodeForge.IsInstalled;
        var autoFocusUpgrade = cliOptions.Mode == InstallerLaunchMode.Upgrade;

        var effectiveSource = ResolveSourceOptions(cliOptions, discovery, platform);
        var effectiveInstallDir = string.IsNullOrWhiteSpace(cliOptions.InstallDirectory)
            ? discovery.NightCodeForge.InstallDirectory
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(cliOptions.InstallDirectory));

        var effectiveDistro = string.IsNullOrWhiteSpace(cliOptions.WslDistroName)
            ? InstallerConstants.DefaultWslDistro
            : cliOptions.WslDistroName.Trim();

        return new InstallerStartupPlan(
            RunHeadless: runHeadless,
            HeadlessAction: headlessAction,
            InitialUiMode: initialUiMode,
            IsUpgradeWizardMode: isUpgradeWizardMode,
            AutoFocusUpgrade: autoFocusUpgrade,
            EffectiveSource: effectiveSource,
            EffectiveInstallDirectory: effectiveInstallDir,
            EffectiveWslDistroName: effectiveDistro,
            Discovery: discovery,
            CliOptions: cliOptions);
    }

    private static InstallSourceOptions ResolveSourceOptions(
        InstallerCliOptions cliOptions,
        InstallationDiscoveryResult discovery,
        IPlatformEnvironment platform)
    {
        if (cliOptions.AirGappedRequested || discovery.RecommendedSourceMode == InstallSourceMode.AirGappedBundle)
        {
            var bundleDir = cliOptions.BundleDirectory
                            ?? discovery.DetectedBundleDirectory
                            ?? Path.Combine(platform.InstallerBaseDirectory, InstallerConstants.BundleDirectoryName);

            return new InstallSourceOptions(InstallSourceMode.AirGappedBundle, bundleDir);
        }

        return new InstallSourceOptions(InstallSourceMode.Online, discovery.DetectedBundleDirectory);
    }
}
