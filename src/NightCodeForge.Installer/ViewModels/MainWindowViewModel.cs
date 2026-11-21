namespace NightCodeForge.Installer.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using NightCodeForge.Installer.Cli;
using NightCodeForge.Installer.Core.Models;
using NightCodeForge.Installer.ViewModels.Wizard;

/// <summary>
/// Root shell ViewModel hosting the 3 desktop UI modes:
/// OOBE Setup Wizard, Maintenance &amp; Updater Hub, and Interactive Uninstaller.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly InstallerServiceContainer _services;
    private readonly InstallerStartupPlan _startupPlan;
    private readonly IUiDispatcher _dispatcher;

    public MainWindowViewModel(
        InstallerServiceContainer services,
        InstallerStartupPlan startupPlan,
        IInstallerUiHost uiHost,
        IUiDispatcher dispatcher)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _startupPlan = startupPlan ?? throw new ArgumentNullException(nameof(startupPlan));
        ArgumentNullException.ThrowIfNull(uiHost);
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

        var wizardState = new SetupWizardState(startupPlan);
        var step1 = new WizardStep1WelcomeViewModel(
            wizardState,
            services.EnvironmentProvisioner,
            services.InstallationDetector,
            services.NuGetProvisioner,
            services.HostAppControl,
            uiHost,
            dispatcher);
        var step2 = new WizardStep2ProvisioningViewModel(
            wizardState,
            services.EnvironmentProvisioner,
            uiHost,
            dispatcher);
        var step3 = new WizardStep3SoftwareViewModel(
            wizardState,
            services.ReleaseManager,
            services.NuGetProvisioner,
            services.EnvironmentProvisioner,
            services.Platform,
            dispatcher);
        var step4 = new WizardStep4AiFleetViewModel(
            wizardState,
            services.OllamaRouterSetupHandoff,
            services.OllamaRouterRecommendationSync,
            dispatcher);
        var step5 = new WizardStep5ReadyViewModel(
            wizardState,
            services.OsIntegration,
            services.HostAppControl,
            uiHost,
            dispatcher,
            SwitchToMaintenanceHub);

        Wizard = new SetupWizardViewModel(
            wizardState,
            step1,
            step2,
            step3,
            step4,
            step5,
            dispatcher);

        MaintenanceHub = new MaintenanceHubViewModel(
            services,
            startupPlan,
            uiHost,
            dispatcher,
            SwitchToUpgradeWizard,
            SwitchToUninstaller);

        Uninstaller = new UninstallerViewModel(
            services.UninstallService,
            services.InstallationDetector,
            startupPlan,
            uiHost,
            dispatcher,
            ReturnToMaintenanceHubFromUninstaller,
            canReturnToHub: startupPlan.InitialUiMode != InstallerUiMode.InteractiveUninstaller);

        RuntimeBadge = $"{services.Platform.Platform} · {services.Platform.CurrentRuntimeIdentifier}";
        CurrentMode = startupPlan.InitialUiMode;
        WindowTitle = BuildWindowTitle(startupPlan.InitialUiMode);
    }

    public SetupWizardViewModel Wizard { get; }
    public MaintenanceHubViewModel MaintenanceHub { get; }
    public UninstallerViewModel Uninstaller { get; }
    public string RuntimeBadge { get; }

    [ObservableProperty]
    public partial InstallerUiMode CurrentMode { get; set; }

    [ObservableProperty]
    public partial string WindowTitle { get; set; }

    public Task InitializeAsync()
    {
        return CurrentMode switch
        {
            InstallerUiMode.OobeWizard => Wizard.InitializeAsync(),
            InstallerUiMode.MaintenanceHub => MaintenanceHub.RefreshAllAsync(),
            InstallerUiMode.InteractiveUninstaller => RefreshUninstallerAsync(),
            _ => Task.CompletedTask
        };
    }

    public void SwitchToUpgradeWizard()
    {
        var discovery = _services.InstallationDetector.Detect(MaintenanceHub.CurrentSourceOptions.BundleDirectory);
        Wizard.EnterUpgradeMode(discovery);
        _dispatcher.Post(() =>
        {
            CurrentMode = InstallerUiMode.OobeWizard;
            WindowTitle = BuildWindowTitle(InstallerUiMode.OobeWizard);
        });
    }

    public void SwitchToMaintenanceHub()
    {
        _dispatcher.Post(() =>
        {
            CurrentMode = InstallerUiMode.MaintenanceHub;
            WindowTitle = BuildWindowTitle(InstallerUiMode.MaintenanceHub);
        });
        _ = MaintenanceHub.RefreshAllAsync();
    }

    public void SwitchToUninstaller()
    {
        Uninstaller.RefreshSummary();
        _dispatcher.Post(() =>
        {
            CurrentMode = InstallerUiMode.InteractiveUninstaller;
            WindowTitle = BuildWindowTitle(InstallerUiMode.InteractiveUninstaller);
        });
    }

    public void ReturnToMaintenanceHubFromUninstaller()
    {
        _dispatcher.Post(() =>
        {
            CurrentMode = InstallerUiMode.MaintenanceHub;
            WindowTitle = BuildWindowTitle(InstallerUiMode.MaintenanceHub);
        });
    }

    private Task RefreshUninstallerAsync()
    {
        Uninstaller.RefreshSummary();
        return Task.CompletedTask;
    }

    private static string BuildWindowTitle(InstallerUiMode mode) =>
        mode switch
        {
            InstallerUiMode.OobeWizard => "NightCodeForge — Setup Wizard",
            InstallerUiMode.MaintenanceHub => "NightCodeForge — Maintenance & Updater Hub",
            InstallerUiMode.InteractiveUninstaller => "NightCodeForge — Uninstall",
            _ => "NightCodeForge Installer"
        };
}
