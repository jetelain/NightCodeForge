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
                      ?? InstallerConstants.UnknownVersionPlaceholder;

        InstalledVersionDisplay = $"v{version}";
        InstallDirectoryDisplay = _state.NightCodeForgeDeployment?.InstallDirectory ?? _state.InstallDirectory;
        CreateStartMenuShortcut = _state.CreateStartMenuShortcut;
        CreateDesktopShortcut = _state.CreateDesktopShortcut;

        OllamaRouterSummary = _state.OllamaRouterDeployment is not null
            ? $"Installed v{_state.OllamaRouterDeployment.Version} ({_state.OllamaRouterDeployment.InstallDirectory})"
            : _state.Discovery.OllamaRouter.IsInstalled
                ? $"Detected v{_state.Discovery.OllamaRouter.Version ?? "unknown"} ({_state.Discovery.OllamaRouter.InstallDirectory})"
                : "Not installed";

        AiModelsSummary = _state.ConfiguredOpenAiProvider is not null
            ? FormatOpenAiSummary(_state.ConfiguredOpenAiProvider)
            : _state.SyncedRecommendations is not null
                ? $"OllamaRouter — Architect: {_state.SyncedRecommendations.ArchitectModelId ?? "-"} | Coder: {_state.SyncedRecommendations.CoderModelId ?? "-"}"
                : _state.AiFleetSkipped
                    ? "Skipped (configure Cloud, ACP, or OllamaRouter in Web UI)"
                    : "Not configured yet";

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
            _state.CreateStartMenuShortcut = CreateStartMenuShortcut;
            _state.CreateDesktopShortcut = CreateDesktopShortcut;
            var installDir = _state.NightCodeForgeDeployment?.InstallDirectory ?? _state.InstallDirectory;

            await _osIntegration.ApplyShortcutPreferencesAsync(
                installDir,
                CreateStartMenuShortcut,
                CreateDesktopShortcut,
                _state.LifetimeToken).ConfigureAwait(false);

            _state.AppliedStartMenuShortcut = CreateStartMenuShortcut;
            _state.AppliedDesktopShortcut = CreateDesktopShortcut;

            _dispatcher.Post(() => StatusMessage = "OS shortcuts and launcher preferences updated.");
        }
        catch (OperationCanceledException)
        {
            _dispatcher.Post(() => StatusMessage = "Shortcut update cancelled.");
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => StatusMessage = $"Failed to update OS shortcuts: {ex.Message}");
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
            var cancellationToken = _state.LifetimeToken;
            var installDir = _state.NightCodeForgeDeployment?.InstallDirectory ?? _state.InstallDirectory;
            if (CreateDesktopShortcut != _state.AppliedDesktopShortcut ||
                CreateStartMenuShortcut != _state.AppliedStartMenuShortcut)
            {
                _state.CreateStartMenuShortcut = CreateStartMenuShortcut;
                _state.CreateDesktopShortcut = CreateDesktopShortcut;
                await _osIntegration.ApplyShortcutPreferencesAsync(
                    installDir,
                    CreateStartMenuShortcut,
                    CreateDesktopShortcut,
                    cancellationToken).ConfigureAwait(false);
                _state.AppliedStartMenuShortcut = CreateStartMenuShortcut;
                _state.AppliedDesktopShortcut = CreateDesktopShortcut;
            }

            if (!_hostAppControl.IsWebServerRunning())
            {
                var started = await _hostAppControl.StartWebServerAsync(installDir, cancellationToken)
                    .ConfigureAwait(false);
                if (!started)
                {
                    _dispatcher.Post(() => StatusMessage = $"Could not locate Web launcher in '{installDir}'.");
                    return;
                }
            }

            var webUrl = _hostAppControl.GetConfiguredWebUrl(installDir);
            var opened = await _hostAppControl.OpenWebUiInBrowserAsync(webUrl, cancellationToken)
                .ConfigureAwait(false);

            _dispatcher.Post(() =>
                StatusMessage = opened
                    ? $"Opened NightCodeForge Web UI at {webUrl}."
                    : $"Server started; open {webUrl} in your browser.");
        }
        catch (OperationCanceledException)
        {
            _dispatcher.Post(() => StatusMessage = "Launch cancelled.");
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => StatusMessage = $"Unable to launch NightCodeForge Web: {ex.Message}");
        }
        finally
        {
            _dispatcher.Post(() => IsBusy = false);
        }
    }

    [RelayCommand]
    public async Task LaunchNightCodeAssayAsync()
    {
        if (IsBusy)
        {
            return;
        }

        _dispatcher.Post(() =>
        {
            IsBusy = true;
            StatusMessage = "Launching NightCode Assay...";
        });

        try
        {
            var launched = await _hostAppControl.LaunchNightCodeAssayAsync(_state.LifetimeToken).ConfigureAwait(false);
            _dispatcher.Post(() =>
                StatusMessage = launched
                    ? "Launched NightCode Assay (nightcodeassay)."
                    : "Could not start 'nightcodeassay'. Ensure global tools are on PATH.");
        }
        catch (OperationCanceledException)
        {
            _dispatcher.Post(() => StatusMessage = "NightCode Assay launch cancelled.");
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => StatusMessage = $"Failed to launch NightCode Assay: {ex.Message}");
        }
        finally
        {
            _dispatcher.Post(() => IsBusy = false);
        }
    }

    [RelayCommand]
    private void OpenMaintenanceHub() => _openMaintenanceHub();

    [RelayCommand]
    private void Finish() => _uiHost.RequestExit(InstallerConstants.ExitCodeSuccess);

    private static string FormatOpenAiSummary(Core.Models.OpenAiProviderConfigurationSummary openAi)
    {
        var roleParts = new List<string> { $"Default: {openAi.DefaultModelId}" };
        if (!string.IsNullOrWhiteSpace(openAi.ArchitectModelId))
        {
            roleParts.Add($"Architect: {openAi.ArchitectModelId}");
        }

        if (!string.IsNullOrWhiteSpace(openAi.CoderModelId))
        {
            roleParts.Add($"Coder: {openAi.CoderModelId}");
        }

        return $"OpenAI Compatible ({openAi.ProviderName} @ {openAi.Endpoint}) — {string.Join(" | ", roleParts)}";
    }
}
