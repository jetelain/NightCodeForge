namespace NightCodeForge.Installer.ViewModels.Wizard;

using NightCodeForge.Installer.Cli;
using NightCodeForge.Installer.Core.Models;

/// <summary>
/// Shared mutable wizard state coordinated across Steps 1 through 5 of the OOBE Setup Wizard.
/// </summary>
public sealed class SetupWizardState
{
    public SetupWizardState(
        InstallerStartupPlan plan,
        HostPlatformType platform,
        CancellationToken lifetimeToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        Platform = platform;
        LifetimeToken = lifetimeToken;
        SourceMode = plan.EffectiveSource.Mode;
        BundleDirectory = plan.EffectiveSource.BundleDirectory;
        InstallDirectory = plan.EffectiveInstallDirectory;
        WslDistroName = plan.EffectiveWslDistroName;
        IsUpgradeMode = plan.IsUpgradeWizardMode || plan.Discovery.NightCodeForge.IsInstalled;
        Discovery = plan.Discovery;

        ProvisionWsl = true;
        ProvisionHostDotNet = true;
        ProvisionLinuxOrWslToolchain = true;
        IncludeDocker = true;
        InstallOllamaRouter = true;
        InstallCompanionTools = true;
        CreateStartMenuShortcut = true;
        CreateDesktopShortcut = false;
        CompanionOutcomes = [];
    }

    public HostPlatformType Platform { get; }
    public CancellationToken LifetimeToken { get; }
    public InstallSourceMode SourceMode { get; set; }
    public string? BundleDirectory { get; set; }
    public string InstallDirectory { get; set; }
    public string WslDistroName { get; set; }
    public bool IsUpgradeMode { get; set; }
    public bool ProvisionWsl { get; set; }
    public bool ProvisionHostDotNet { get; set; }
    public bool ProvisionLinuxOrWslToolchain { get; set; }
    public bool IncludeDocker { get; set; }
    public bool InstallOllamaRouter { get; set; }
    public bool InstallCompanionTools { get; set; }
    public bool CreateDesktopShortcut { get; set; }
    public bool CreateStartMenuShortcut { get; set; }
    public bool AppliedDesktopShortcut { get; set; }
    public bool AppliedStartMenuShortcut { get; set; }
    public EnvironmentProbeResult? LatestProbe { get; set; }
    public InstallationDiscoveryResult Discovery { get; set; }
    public ReleaseDeploymentResult? NightCodeForgeDeployment { get; set; }
    public ReleaseDeploymentResult? OllamaRouterDeployment { get; set; }
    public IReadOnlyList<CompanionToolInstallOutcome> CompanionOutcomes { get; set; }
    public AiSetupMode SelectedAiSetupMode { get; set; } = AiSetupMode.OllamaRouter;
    public RouterRoleRecommendationsSummary? SyncedRecommendations { get; set; }
    public OpenAiProviderConfigurationSummary? ConfiguredOpenAiProvider { get; set; }
    public bool AiFleetSkipped { get; set; }

    public InstallSourceOptions ToInstallSourceOptions() =>
        new(SourceMode, SourceMode == InstallSourceMode.AirGappedBundle ? BundleDirectory : null);

    public ProvisioningOptions ToProvisioningOptions() =>
        new(
            WslDistroName,
            ProvisionWsl,
            ProvisionHostDotNet,
            ProvisionLinuxOrWslToolchain,
            SkipDocker: !IncludeDocker);
}
