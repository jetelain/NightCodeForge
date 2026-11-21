namespace NightCodeForge.Installer.ViewModels.Wizard;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NightCodeForge.Installer.Core.Constants;
using NightCodeForge.Installer.Core.Services;

/// <summary>
/// Step 5 ViewModel: Installation summary, shortcut preferences, and launch actions for NightCodeForge Web &amp; NightCodeAssay.
/// </summary>
public sealed partial class WizardStep5ReadyViewModel : ObservableObject
{
    private readonly SetupWizardState _state;
    private readonly IOsIntegrationService _osIntegration;
    private readonly IHostAppControlService _hostAppControl;
    private readonly IInstallerUiHost _uiHost;
    private readonly IUiDispatcher _dispatcher;
    private readonly Action _openMaintenanceHub;

    public WizardStep5ReadyViewModel(
        SetupWizardState state,
        IOsIntegrationService osIntegration,
        IHostAppControlService hostAppControl,
        IInstallerUiHost uiHost,
        IUiDispatcher dispatcher,
        Action openMaintenanceHub)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _osIntegration = osIntegration ?? throw new ArgumentNullException(nameof(osIntegration));
        _hostAppControl = hostAppControl ?? throw new ArgumentNullException(nameof(hostAppControl));
        _uiHost = uiHost ?? throw new ArgumentNullException(nameof(uiHost));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _openMaintenanceHub = openMaintenanceHub ?? throw new ArgumentNullException(nameof(openMaintenanceHub));

        InstalledVersionDisplay = "-";
        InstallDirectoryDisplay = _state.InstallDirectory;
        OllamaRouterSummary = "Pending";
        AiModelsSummary = "Pending";
        CompanionToolsSummary = "Pending";
        CreateStartMenuShortcut = _state.CreateStartMenuShortcut;
        CreateDesktopShortcut = _state.CreateDesktopShortcut;
        StatusMessage = "NightCodeForge is ready to forge.";
    }

    [ObservableProperty]
    public partial string InstalledVersionDisplay { get; set; }

    [ObservableProperty]
    public partial string InstallDirectoryDisplay { get; set; }

    [ObservableProperty]
    public partial string OllamaRouterSummary { get; set; }

    [ObservableProperty]
    public partial string AiModelsSummary { get; set; }

    [ObservableProperty]
    public partial string CompanionToolsSummary { get; set; }

    [ObservableProperty]
    public partial bool CreateStartMenuShortcut { get; set; }

    [ObservableProperty]
    public partial bool CreateDesktopShortcut { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string StatusMessage { get; set; }

    partial void OnCreateStartMenuShortcutChanged(bool value) => _state.CreateStartMenuShortcut = value;

    public void RefreshSummary()
    {
        var version = _state.NightCodeForgeDeployment?.Version
                      ?? _state.Discovery.NightCodeForge.Version
                      ?? InstallerConstants.DefaultFallbackVersion;

        InstalledVersionDisplay = $"v{version}";
        InstallDirectoryDisplay = _state.NightCodeForgeDeployment?.InstallDirectory ?? _state.InstallDirectory;
        CreateDesktopShortcut = _state.CreateDesktopShortcut;

        OllamaRouterSummary = _state.OllamaRouterDeployment is not null
            ? $"Installed v{_state.OllamaRouterDeployment.Version} ({_state.OllamaRouterDeployment.InstallDirectory})"
            : _state.Discovery.OllamaRouter.IsInstalled
                ? $"Detected v{_state.Discovery.OllamaRouter.Version ?? "unknown"} ({_state.Discovery.OllamaRouter.InstallDirectory})"
                : "Not installed";

        AiModelsSummary = _state.SyncedRecommendations is not null
            ? $"Architect: {_state.SyncedRecommendations.ArchitectModelId ?? "-"} | Coder: {_state.SyncedRecommendations.CoderModelId ?? "-"}"
            : _state.AiFleetSkipped
                ? "Skipped (configure Cloud / ACP / OllamaRouter in Web UI)"
                : "Not synced yet";

        CompanionToolsSummary = _state.CompanionOutcomes.Count > 0
            ? $"{_state.CompanionOutcomes.Count} tool installation target(s) completed."
            : "ncf, nightcodeassay, csnav, mdfetch, codecondenser";
    }

    [RelayCommand]
    public async Task ApplyShortcutsAsync()
    {
        if (IsBusy)
        {
            return;
        }

        _dispatcher.Post(() =>
        {
            IsBusy = true;
            StatusMessage = "Updating desktop and Start Menu shortcuts...";
        });

        try
        {
            _state.CreateDesktopShortcut = CreateDesktopShortcut;
            var version = _state.NightCodeForgeDeployment?.Version
                          ?? _state.Discovery.NightCodeForge.Version
                          ?? InstallerConstants.DefaultFallbackVersion;
            var installDir = _state.NightCodeForgeDeployment?.InstallDirectory ?? _state.InstallDirectory;

            await _osIntegration.RegisterNightCodeForgeInstallationAsync(
                installDir,
                version,
                CreateDesktopShortcut,
                CancellationToken.None).ConfigureAwait(false);

            _dispatcher.Post(() => StatusMessage = "OS shortcuts and uninstall registration updated.");
        }
        finally
        {
            _dispatcher.Post(() => IsBusy = false);
        }
    }

    [RelayCommand]
    public async Task LaunchNightCodeForgeWebAsync()
    {
        if (IsBusy)
        {
            return;
        }

        _dispatcher.Post(() =>
        {
            IsBusy = true;
            StatusMessage = "Starting NightCodeForge Web server...";
        });

        try
        {
            var installDir = _state.NightCodeForgeDeployment?.InstallDirectory ?? _state.InstallDirectory;
            if (CreateDesktopShortcut != _state.CreateDesktopShortcut)
            {
                _state.CreateDesktopShortcut = CreateDesktopShortcut;
                var version = _state.NightCodeForgeDeployment?.Version
                              ?? _state.Discovery.NightCodeForge.Version
                              ?? InstallerConstants.DefaultFallbackVersion;
                await _osIntegration.RegisterNightCodeForgeInstallationAsync(
                    installDir,
                    version,
                    CreateDesktopShortcut,
                    CancellationToken.None).ConfigureAwait(false);
            }

            if (!_hostAppControl.IsWebServerRunning())
            {
                var started = await _hostAppControl.StartWebServerAsync(installDir, CancellationToken.None)
                    .ConfigureAwait(false);
                if (!started)
                {
                    _dispatcher.Post(() => StatusMessage = $"Could not locate Web launcher in '{installDir}'.");
                    return;
                }
            }

            var webUrl = _hostAppControl.GetConfiguredWebUrl(installDir);
            var opened = await _hostAppControl.OpenWebUiInBrowserAsync(webUrl, CancellationToken.None)
                .ConfigureAwait(false);

            _dispatcher.Post(() =>
                StatusMessage = opened
                    ? $"Opened NightCodeForge Web UI at {webUrl}."
                    : $"Server started; open {webUrl} in your browser.");
        }
        finally
        {
            _dispatcher.Post(() => IsBusy = false);
        }
    }

    [RelayCommand]
    public async Task LaunchNightCodeAssayAsync()
    {
        var launched = await _hostAppControl.LaunchNightCodeAssayAsync(CancellationToken.None).ConfigureAwait(false);
        _dispatcher.Post(() =>
            StatusMessage = launched
                ? "Launched NightCode Assay (nightcodeassay)."
                : "Could not start 'nightcodeassay'. Ensure global tools are on PATH.");
    }

    [RelayCommand]
    private void OpenMaintenanceHub() => _openMaintenanceHub();

    [RelayCommand]
    private void Finish() => _uiHost.RequestExit(InstallerConstants.ExitCodeSuccess);
}
