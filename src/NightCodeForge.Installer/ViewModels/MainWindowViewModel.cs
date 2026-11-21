namespace NightCodeForge.Installer.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using NightCodeForge.Installer.Cli;
using NightCodeForge.Installer.Core.Models;
using NightCodeForge.Installer.ViewModels.Wizard;

/// <summary>
/// Root shell ViewModel hosting the 3 desktop UI modes:
/// OOBE Setup Wizard, Maintenance &amp; Updater Hub, and Interactive Uninstaller.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly InstallerServiceContainer _services;
    private readonly IUiDispatcher _dispatcher;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private bool _disposed;

    public MainWindowViewModel(
        InstallerServiceContainer services,
        InstallerStartupPlan startupPlan,
        IInstallerUiHost uiHost,
        IUiDispatcher dispatcher)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        ArgumentNullException.ThrowIfNull(startupPlan);
        ArgumentNullException.ThrowIfNull(uiHost);
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

        var wizardState = new SetupWizardState(startupPlan, services.Platform.Platform, _lifetimeCts.Token);
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
            SwitchToUninstaller,
            _lifetimeCts.Token);

        Uninstaller = new UninstallerViewModel(
            services.UninstallService,
            services.InstallationDetector,
            startupPlan,
            uiHost,
            dispatcher,
            ReturnToMaintenanceHubFromUninstaller,
            canReturnToHub: startupPlan.InitialUiMode != InstallerUiMode.InteractiveUninstaller,
            _lifetimeCts.Token);

        RuntimeBadge = $"{services.Platform.Platform} · {services.Platform.CurrentRuntimeIdentifier}";
        CurrentMode = startupPlan.InitialUiMode;
        WindowTitle = BuildWindowTitle(startupPlan.InitialUiMode);
        InitializationErrorMessage = string.Empty;
    }

    public SetupWizardViewModel Wizard { get; }
    public MaintenanceHubViewModel MaintenanceHub { get; }
    public UninstallerViewModel Uninstaller { get; }
    public string RuntimeBadge { get; }

    public bool HasActiveDestructiveOperation =>
        Wizard.Step2.IsRunning ||
        Wizard.Step3.IsRunning ||
        Wizard.Step4.IsRunning ||
        MaintenanceHub.IsBusy ||
        Uninstaller.IsRunning;

    [ObservableProperty]
    public partial InstallerUiMode CurrentMode { get; set; }

    [ObservableProperty]
    public partial string WindowTitle { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInitializationError))]
    public partial string InitializationErrorMessage { get; set; }

    public bool HasInitializationError => !string.IsNullOrWhiteSpace(InitializationErrorMessage);

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

    public void ReportInitializationError(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        _dispatcher.Post(() => InitializationErrorMessage = message);
    }

    public void NotifyCloseBlockedDuringOperation()
    {
        ReportInitializationError(
            "An installation, upgrade, provisioning, or uninstall operation is currently in progress. Wait for completion before closing.");
    }

    public void SwitchToUpgradeWizard()
    {
        var hubSource = MaintenanceHub.CurrentSourceOptions;
        var hubInstallDir = MaintenanceHub.InstallDirectory;
        var discovery = _services.InstallationDetector.Detect(hubSource.BundleDirectory, hubInstallDir);
        Wizard.EnterUpgradeMode(discovery, hubSource, hubInstallDir);
        _dispatcher.Post(() =>
        {
            InitializationErrorMessage = string.Empty;
            CurrentMode = InstallerUiMode.OobeWizard;
            WindowTitle = BuildWindowTitle(InstallerUiMode.OobeWizard);
        });
    }

    public void SwitchToMaintenanceHub()
    {
        MaintenanceHub.ApplyWizardConfiguration(
            Wizard.CurrentSourceOptions,
            Wizard.CurrentInstallDirectory);

        _dispatcher.Post(() =>
        {
            InitializationErrorMessage = string.Empty;
            CurrentMode = InstallerUiMode.MaintenanceHub;
            WindowTitle = BuildWindowTitle(InstallerUiMode.MaintenanceHub);
        });

        _ = RefreshMaintenanceHubSafelyAsync();
    }

    public void SwitchToUninstaller()
    {
        Uninstaller.RefreshSummary();
        _dispatcher.Post(() =>
        {
            InitializationErrorMessage = string.Empty;
            CurrentMode = InstallerUiMode.InteractiveUninstaller;
            WindowTitle = BuildWindowTitle(InstallerUiMode.InteractiveUninstaller);
        });
    }

    public void ReturnToMaintenanceHubFromUninstaller()
    {
        _dispatcher.Post(() =>
        {
            InitializationErrorMessage = string.Empty;
            CurrentMode = InstallerUiMode.MaintenanceHub;
            WindowTitle = BuildWindowTitle(InstallerUiMode.MaintenanceHub);
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _lifetimeCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Justification: Dispose may race with shutdown teardown.
        }

        _lifetimeCts.Dispose();
    }

    private async Task RefreshMaintenanceHubSafelyAsync()
    {
        try
        {
            await MaintenanceHub.RefreshAllAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Justification: Window teardown cancels background hub refresh cleanly.
        }
        catch (Exception ex)
        {
            ReportInitializationError($"Failed to refresh Maintenance Hub: {ex.Message}");
        }
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
