namespace NightCodeForge.Installer.ViewModels;

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NightCodeForge.Installer.Cli;
using NightCodeForge.Installer.Core.Constants;
using NightCodeForge.Installer.Core.Exceptions;
using NightCodeForge.Installer.Core.Models;

/// <summary>
/// Row ViewModel representing a single companion NuGet global tool in the Maintenance Hub.
/// </summary>
public sealed class CompanionToolRowViewModel
{
    public CompanionToolRowViewModel(
        string toolCommand,
        string packageId,
        string description,
        bool installedOnHost,
        string? hostVersion,
        bool installedInWsl,
        string? wslVersion,
        bool installInWsl,
        bool showWslColumn)
    {
        ToolCommand = toolCommand;
        PackageId = packageId;
        Description = description;
        HostInstalled = installedOnHost;
        HostStatusText = installedOnHost
            ? string.IsNullOrWhiteSpace(hostVersion) ? "Installed" : $"v{hostVersion}"
            : "Missing";
        WslInstalled = installedInWsl;
        ShowWslColumn = showWslColumn;
        WslStatusText = !showWslColumn || !installInWsl
            ? "N/A"
            : installedInWsl
                ? string.IsNullOrWhiteSpace(wslVersion) ? "Installed" : $"v{wslVersion}"
                : "Missing";
    }

    public string ToolCommand { get; }
    public string PackageId { get; }
    public string Description { get; }
    public bool HostInstalled { get; }
    public string HostStatusText { get; }
    public bool WslInstalled { get; }
    public string WslStatusText { get; }
    public bool ShowWslColumn { get; }
}

/// <summary>
/// Mode 2 ViewModel: Maintenance &amp; Updater Hub for managing an existing NightCodeForge installation
/// (status, 1-click all-component Online/Air-Gapped upgrade, companion NuGet tools, OllamaRouter handoff,
/// Forge Doctor diagnostics, and transition to Upgrade Wizard or Interactive Uninstaller).
/// </summary>
public sealed partial class MaintenanceHubViewModel : ObservableObject
{
    public const string OnlineUpgradeLabel = "⬆️ Upgrade All Components";
    public const string LocalBundleUpgradeLabel = "🔄 Reinstall / Upgrade from Local Bundle";

    private readonly InstallerServiceContainer _services;
    private readonly InstallerStartupPlan _startupPlan;
    private readonly IInstallerUiHost _uiHost;
    private readonly IUiDispatcher _dispatcher;
    private readonly Action _openWizardInUpgradeMode;
    private readonly Action _openUninstaller;
    private readonly CancellationToken _lifetimeToken;
    private bool _synchronizingSource;
    private InstallationDiscoveryResult _latestDiscovery;

    public MaintenanceHubViewModel(
        InstallerServiceContainer services,
        InstallerStartupPlan startupPlan,
        IInstallerUiHost uiHost,
        IUiDispatcher dispatcher,
        Action openWizardInUpgradeMode,
        Action openUninstaller,
        CancellationToken lifetimeToken)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _startupPlan = startupPlan ?? throw new ArgumentNullException(nameof(startupPlan));
        _uiHost = uiHost ?? throw new ArgumentNullException(nameof(uiHost));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _openWizardInUpgradeMode = openWizardInUpgradeMode ?? throw new ArgumentNullException(nameof(openWizardInUpgradeMode));
        _openUninstaller = openUninstaller ?? throw new ArgumentNullException(nameof(openUninstaller));
        _lifetimeToken = lifetimeToken;

        _latestDiscovery = startupPlan.Discovery;
        CompanionTools = [];
        DoctorChecks = [];
        LogLines = [];

        _synchronizingSource = true;
        IsOnlineSource = startupPlan.EffectiveSource.Mode == InstallSourceMode.Online;
        IsAirGappedSource = startupPlan.EffectiveSource.Mode == InstallSourceMode.AirGappedBundle;
        _synchronizingSource = false;

        BundleDirectory = startupPlan.EffectiveSource.BundleDirectory ?? string.Empty;
        InstallDirectory = startupPlan.EffectiveInstallDirectory;
        InstalledVersion = FormatVersion(startupPlan.Discovery.NightCodeForge.Version);
        AvailableVersion = "Checking...";
        IsLegacyPublishInstall = startupPlan.Discovery.NightCodeForge.IsLegacyPublishInstall;
        IsWindowsHost = services.Platform.Platform == HostPlatformType.Windows;
        AutoFocusUpgradeBanner = startupPlan.AutoFocusUpgrade;
        WebUrl = services.HostAppControl.GetConfiguredWebUrl(startupPlan.EffectiveInstallDirectory);
        OllamaRouterStatusText = FormatOllamaRouterStatus(startupPlan.Discovery.OllamaRouter);
        OllamaRouterVersionText = FormatVersion(startupPlan.Discovery.OllamaRouter.Version);
        LastSyncedModelsSummary = "Click Sync Role Recommendations to refresh model assignments.";
        DoctorSummaryText = "Click Run Forge Doctor to verify WSL2, Docker, OllamaRouter, Web port, tools, and DB.";
        StatusMessage = "Loading installation status...";
        DetectedBundleSummary = string.Empty;
        UpgradeSummaryText = "Checks NightCodeForge Web, OllamaRouter, and companion NuGet global tools.";
        UpdateSourceState();
    }

    public ObservableCollection<CompanionToolRowViewModel> CompanionTools { get; }

    public ObservableCollection<ForgeDoctorCheckItem> DoctorChecks { get; }

    public ObservableCollection<string> LogLines { get; }

    [ObservableProperty]
    public partial bool IsOnlineSource { get; set; }

    [ObservableProperty]
    public partial bool IsAirGappedSource { get; set; }

    [ObservableProperty]
    public partial string BundleDirectory { get; set; }

    [ObservableProperty]
    public partial string DetectedBundleSummary { get; set; }

    [ObservableProperty]
    public partial string InstallDirectory { get; set; }

    [ObservableProperty]
    public partial string InstalledVersion { get; set; }

    [ObservableProperty]
    public partial string AvailableVersion { get; set; }

    [ObservableProperty]
    public partial bool IsLegacyPublishInstall { get; set; }

    [ObservableProperty]
    public partial bool IsWindowsHost { get; set; }

    [ObservableProperty]
    public partial bool AutoFocusUpgradeBanner { get; set; }

    [ObservableProperty]
    public partial bool IsWebServerRunning { get; set; }

    [ObservableProperty]
    public partial string ServerStatusText { get; set; } = InstallerConstants.UiStatusWebStopped;

    [ObservableProperty]
    public partial string WebUrl { get; set; }

    [ObservableProperty]
    public partial string OllamaRouterStatusText { get; set; }

    [ObservableProperty]
    public partial string OllamaRouterVersionText { get; set; }

    [ObservableProperty]
    public partial string LastSyncedModelsSummary { get; set; }

    [ObservableProperty]
    public partial string UpgradeSummaryText { get; set; }

    [ObservableProperty]
    public partial bool IsNightCodeForgeUpdateAvailable { get; set; }

    [ObservableProperty]
    public partial bool IsOllamaRouterUpdateAvailable { get; set; }

    [ObservableProperty]
    public partial bool CanUpgradeFromBundle { get; set; }

    [ObservableProperty]
    public partial bool HasDoctorReport { get; set; }

    [ObservableProperty]
    public partial string DoctorSummaryText { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial double ProgressPercent { get; set; }

    [ObservableProperty]
    public partial string StatusMessage { get; set; }

    public string UpgradeButtonLabel =>
        IsAirGappedSource ? LocalBundleUpgradeLabel : OnlineUpgradeLabel;

    public bool CanExecuteUpgrade =>
        !IsBusy && (IsNightCodeForgeUpdateAvailable || IsOllamaRouterUpdateAvailable || CanUpgradeFromBundle);

    public InstallSourceOptions CurrentSourceOptions =>
        IsAirGappedSource
            ? new InstallSourceOptions(InstallSourceMode.AirGappedBundle, string.IsNullOrWhiteSpace(BundleDirectory) ? null : BundleDirectory.Trim())
            : new InstallSourceOptions(InstallSourceMode.Online, string.IsNullOrWhiteSpace(BundleDirectory) ? null : BundleDirectory.Trim());

    partial void OnIsOnlineSourceChanged(bool value)
    {
        if (_synchronizingSource)
        {
            return;
        }

        _synchronizingSource = true;
        try
        {
            if (value)
            {
                IsAirGappedSource = false;
            }
            else if (!IsAirGappedSource)
            {
                IsAirGappedSource = true;
            }
        }
        finally
        {
            _synchronizingSource = false;
        }

        ResetStaleUpdateAvailability();
        UpdateSourceState();
    }

    partial void OnIsAirGappedSourceChanged(bool value)
    {
        if (_synchronizingSource)
        {
            return;
        }

        _synchronizingSource = true;
        try
        {
            if (value)
            {
                IsOnlineSource = false;
            }
            else if (!IsOnlineSource)
            {
                IsOnlineSource = true;
            }
        }
        finally
        {
            _synchronizingSource = false;
        }

        ResetStaleUpdateAvailability();
        UpdateSourceState();
    }

    partial void OnBundleDirectoryChanged(string value)
    {
        if (!_synchronizingSource)
        {
            ResetStaleUpdateAvailability();
        }

        UpdateSourceState();
    }

    partial void OnIsBusyChanged(bool value) => NotifyUpgradeStateChanged();
    partial void OnIsNightCodeForgeUpdateAvailableChanged(bool value) => NotifyUpgradeStateChanged();
    partial void OnIsOllamaRouterUpdateAvailableChanged(bool value) => NotifyUpgradeStateChanged();
    partial void OnCanUpgradeFromBundleChanged(bool value) => NotifyUpgradeStateChanged();

    public void ApplyWizardConfiguration(InstallSourceOptions source, string? installDirectory)
    {
        ArgumentNullException.ThrowIfNull(source);

        _synchronizingSource = true;
        try
        {
            IsOnlineSource = source.Mode == InstallSourceMode.Online;
            IsAirGappedSource = source.Mode == InstallSourceMode.AirGappedBundle;
            BundleDirectory = source.BundleDirectory ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(installDirectory))
            {
                InstallDirectory = installDirectory.Trim();
            }
        }
        finally
        {
            _synchronizingSource = false;
        }

        ResetStaleUpdateAvailability();
        UpdateSourceState();
    }

    [RelayCommand]
    public async Task RefreshAllAsync()
    {
        if (IsBusy)
        {
            return;
        }

        _dispatcher.Post(() =>
        {
            IsBusy = true;
            StatusMessage = "Refreshing installation status, available updates, and companion tools...";
        });

        try
        {
            await RefreshStatusAndUpdatesCoreAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _dispatcher.Post(() => StatusMessage = "Status refresh cancelled.");
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() =>
            {
                StatusMessage = $"Status refresh failed: {ex.Message}";
                LogLines.Add($"[Error] {ex.Message}");
            });
        }
        finally
        {
            _dispatcher.Post(() => IsBusy = false);
        }
    }

    [RelayCommand]
    public async Task BrowseBundleFolderAsync()
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            var picked = await _uiHost.PickBundleFolderAsync(
                string.IsNullOrWhiteSpace(BundleDirectory) ? null : BundleDirectory,
                _lifetimeToken).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(picked))
            {
                return;
            }

            await _dispatcher.InvokeAsync(() =>
            {
                BundleDirectory = picked;
                IsAirGappedSource = true;
                StatusMessage = $"Selected local bundle folder: {picked}";
            }).ConfigureAwait(false);

            await RefreshAllAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _dispatcher.Post(() => StatusMessage = "Bundle selection cancelled.");
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => StatusMessage = $"Unable to browse bundle folder: {ex.Message}");
        }
    }

    [RelayCommand]
    public async Task StartServerAsync()
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            var started = await _services.HostAppControl.StartWebServerAsync(InstallDirectory, _lifetimeToken)
                .ConfigureAwait(false);

            _dispatcher.Post(() =>
            {
                RefreshRunningState();
                if (started)
                {
                    IsWebServerRunning = true;
                    ServerStatusText = InstallerConstants.UiStatusWebRunning;
                    StatusMessage = $"Started NightCodeForge Web ({WebUrl}).";
                }
                else
                {
                    StatusMessage = $"Could not locate NightCodeForge launcher in '{InstallDirectory}'.";
                }
            });
        }
        catch (OperationCanceledException)
        {
            _dispatcher.Post(() => StatusMessage = "Start server cancelled.");
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => StatusMessage = $"Failed to start server: {ex.Message}");
        }
    }

    [RelayCommand]
    public async Task StopServerAsync()
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            var stopped = await _services.HostAppControl.StopWebServerAsync(_lifetimeToken)
                .ConfigureAwait(false);

            _dispatcher.Post(() =>
            {
                RefreshRunningState();
                if (stopped && !IsWebServerRunning)
                {
                    StatusMessage = "Stopped NightCodeForge Web server.";
                }
                else if (IsWebServerRunning)
                {
                    StatusMessage = "NightCodeForge Web process could not be stopped. Check permissions or close manually.";
                }
                else
                {
                    StatusMessage = "NightCodeForge Web server was not running.";
                }
            });
        }
        catch (OperationCanceledException)
        {
            _dispatcher.Post(() => StatusMessage = "Stop server cancelled.");
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => StatusMessage = $"Failed to stop server: {ex.Message}");
        }
    }

    [RelayCommand]
    public async Task OpenWebUiAsync()
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            var url = _services.HostAppControl.GetConfiguredWebUrl(InstallDirectory);
            var opened = await _services.HostAppControl.OpenWebUiInBrowserAsync(url, _lifetimeToken)
                .ConfigureAwait(false);

            _dispatcher.Post(() =>
            {
                WebUrl = url;
                StatusMessage = opened
                    ? $"Opened {url} in browser."
                    : $"Failed to open browser for {url}.";
            });
        }
        catch (OperationCanceledException)
        {
            _dispatcher.Post(() => StatusMessage = "Open browser cancelled.");
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => StatusMessage = $"Failed to open Web UI: {ex.Message}");
        }
    }

    [RelayCommand]
    public async Task OpenCliTerminalAsync()
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            var opened = await _services.HostAppControl.OpenCliTerminalAsync(InstallDirectory, _lifetimeToken)
                .ConfigureAwait(false);

            _dispatcher.Post(() =>
                StatusMessage = opened
                    ? $"Opened CLI terminal in '{InstallDirectory}'."
                    : "Failed to open CLI terminal.");
        }
        catch (OperationCanceledException)
        {
            _dispatcher.Post(() => StatusMessage = "Open CLI terminal cancelled.");
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => StatusMessage = $"Failed to open CLI terminal: {ex.Message}");
        }
    }

    [RelayCommand(CanExecute = nameof(CanExecuteUpgrade))]
    public async Task UpgradeAllAsync()
    {
        if (IsBusy)
        {
            return;
        }

        _dispatcher.Post(() =>
        {
            IsBusy = true;
            ProgressPercent = 5.0;
            StatusMessage = "Upgrading NightCodeForge, OllamaRouter, and companion NuGet tools...";
            LogLines.Clear();
        });

        var progress = new UiDispatchedProgress<InstallerProgressEvent>(_dispatcher, ev =>
        {
            LogLines.Add($"[{ev.Step}] {ev.Message}");
            StatusMessage = ev.Message;
            if (ev.ProgressPercent.HasValue)
            {
                ProgressPercent = ev.ProgressPercent.Value;
            }
        });

        try
        {
            var result = await _services.UpgradeService.UpgradeAllAsync(
                CurrentSourceOptions,
                InstallDirectory,
                _startupPlan.EffectiveWslDistroName,
                createDesktopShortcut: false,
                progress,
                _lifetimeToken).ConfigureAwait(false);

            if (result.Succeeded)
            {
                await RefreshStatusAndUpdatesCoreAsync().ConfigureAwait(false);
                _dispatcher.Post(() =>
                {
                    ProgressPercent = 100.0;
                    StatusMessage = $"Upgraded all components cleanly (NightCodeForge v{result.NightCodeForgeDeployment?.Version ?? "?"}).";
                });
            }
            else
            {
                _dispatcher.Post(() =>
                    StatusMessage = $"Upgrade failed: {result.ErrorMessage}");
            }
        }
        catch (OperationCanceledException)
        {
            _dispatcher.Post(() => StatusMessage = "Upgrade cancelled.");
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() =>
            {
                StatusMessage = $"Upgrade failed: {ex.Message}";
                LogLines.Add($"[Error] {ex.Message}");
            });
        }
        finally
        {
            _dispatcher.Post(() => IsBusy = false);
        }
    }

    [RelayCommand]
    private void RerunSetupWizard() => _openWizardInUpgradeMode();

    [RelayCommand]
    public async Task UpdateAllToolsAsync()
    {
        if (IsBusy)
        {
            return;
        }

        _dispatcher.Post(() =>
        {
            IsBusy = true;
            ProgressPercent = 10.0;
            StatusMessage = "Reinstalling / updating all 5 companion NuGet global tools...";
        });

        var progress = new UiDispatchedProgress<InstallerProgressEvent>(_dispatcher, ev =>
        {
            LogLines.Add($"[{ev.Step}] {ev.Message}");
            StatusMessage = ev.Message;
            if (ev.ProgressPercent.HasValue)
            {
                ProgressPercent = ev.ProgressPercent.Value;
            }
        });

        try
        {
            var wslDistro = await ResolveActiveWslDistroAsync().ConfigureAwait(false);
            var outcomes = await _services.NuGetProvisioner.ProvisionAllAsync(
                CurrentSourceOptions,
                wslDistro,
                progress,
                _lifetimeToken).ConfigureAwait(false);

            await RefreshCompanionToolsCoreAsync(wslDistro).ConfigureAwait(false);
            _dispatcher.Post(() =>
            {
                ProgressPercent = 100.0;
                StatusMessage = $"Updated {outcomes.Count} companion tool target(s).";
            });
        }
        catch (OperationCanceledException)
        {
            _dispatcher.Post(() => StatusMessage = "Companion tool update cancelled.");
        }
        catch (NuGetToolProvisioningException ex)
        {
            _dispatcher.Post(() =>
            {
                StatusMessage = $"Companion tool update failed: {ex.Message}";
                LogLines.Add($"[Error] {ex.Message}");
            });
        }
        catch (BundleValidationException ex)
        {
            _dispatcher.Post(() =>
            {
                StatusMessage = $"Bundle validation failed: {ex.Message}";
                LogLines.Add($"[Error] {ex.Message}");
            });
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() =>
            {
                StatusMessage = $"Companion tool update failed: {ex.Message}";
                LogLines.Add($"[Error] {ex.Message}");
            });
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

        try
        {
            var launched = await _services.HostAppControl.LaunchNightCodeAssayAsync(_lifetimeToken)
                .ConfigureAwait(false);
            _dispatcher.Post(() =>
                StatusMessage = launched
                    ? "Launched NightCode Assay (nightcodeassay)."
                    : "Could not launch 'nightcodeassay'. Update Companion Tools first.");
        }
        catch (OperationCanceledException)
        {
            _dispatcher.Post(() => StatusMessage = "NightCode Assay launch cancelled.");
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => StatusMessage = $"Failed to launch NightCode Assay: {ex.Message}");
        }
    }

    [RelayCommand]
    public async Task OpenOllamaRouterAppAsync()
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            var opened = await _services.HostAppControl.OpenOllamaRouterAppAsync(_lifetimeToken)
                .ConfigureAwait(false);
            _dispatcher.Post(() =>
                StatusMessage = opened
                    ? "Opened OllamaRouter App."
                    : "Could not launch OllamaRouter App or ollamarouter://open handler.");
        }
        catch (OperationCanceledException)
        {
            _dispatcher.Post(() => StatusMessage = "Open OllamaRouter App cancelled.");
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => StatusMessage = $"Failed to open OllamaRouter App: {ex.Message}");
        }
    }

    [RelayCommand]
    public async Task RunOllamaRouterWizardAsync()
    {
        if (IsBusy)
        {
            return;
        }

        _dispatcher.Post(() =>
        {
            IsBusy = true;
            StatusMessage = "Launching OllamaRouter Setup Wizard (waiting for completion)...";
            LogLines.Add("[OllamaRouter] Executing 'ollamarouter setup --wizard --wait'...");
        });

        var progress = new UiDispatchedProgress<InstallerProgressEvent>(_dispatcher, ev =>
        {
            LogLines.Add($"[{ev.Step}] {ev.Message}");
            StatusMessage = ev.Message;
        });

        try
        {
            var result = await _services.OllamaRouterSetupHandoff.RunSetupWizardAndSyncAsync(
                InstallDirectory,
                InstallerConstants.DefaultOllamaRouterEndpoint,
                progress,
                _lifetimeToken).ConfigureAwait(false);

            _dispatcher.Post(() =>
            {
                if (result.Succeeded && result.SyncedRecommendations is not null)
                {
                    LastSyncedModelsSummary = FormatSyncedModels(result.SyncedRecommendations);
                    StatusMessage = "OllamaRouter Setup Wizard finished and role models synced.";
                }
                else
                {
                    StatusMessage = result.ErrorMessage ?? $"OllamaRouter wizard exited with code {result.ExitCode}.";
                }
            });
        }
        catch (OperationCanceledException)
        {
            _dispatcher.Post(() => StatusMessage = "OllamaRouter Setup Wizard cancelled.");
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() =>
            {
                StatusMessage = $"OllamaRouter Setup Wizard failed: {ex.Message}";
                LogLines.Add($"[Error] {ex.Message}");
            });
        }
        finally
        {
            _dispatcher.Post(() => IsBusy = false);
        }
    }

    [RelayCommand]
    public async Task SyncRecommendationsAsync()
    {
        if (IsBusy)
        {
            return;
        }

        _dispatcher.Post(() =>
        {
            IsBusy = true;
            StatusMessage = "Syncing role recommendations from OllamaRouter...";
        });

        var progress = new UiDispatchedProgress<InstallerProgressEvent>(_dispatcher, ev =>
        {
            LogLines.Add($"[{ev.Step}] {ev.Message}");
            StatusMessage = ev.Message;
        });

        try
        {
            var summary = await _services.OllamaRouterRecommendationSync.SyncRecommendationsToConfigAsync(
                InstallDirectory,
                InstallerConstants.DefaultOllamaRouterEndpoint,
                progress,
                _lifetimeToken).ConfigureAwait(false);

            _dispatcher.Post(() =>
            {
                LastSyncedModelsSummary = FormatSyncedModels(summary);
                StatusMessage = "Role recommendations synced into config/config.json.";
            });
        }
        catch (OperationCanceledException)
        {
            _dispatcher.Post(() => StatusMessage = "Recommendation sync cancelled.");
        }
        catch (OllamaRouterSyncException ex)
        {
            _dispatcher.Post(() =>
            {
                StatusMessage = ex.Message;
                LogLines.Add($"[Error] {ex.Message}");
            });
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() =>
            {
                StatusMessage = $"Recommendation sync failed: {ex.Message}";
                LogLines.Add($"[Error] {ex.Message}");
            });
        }
        finally
        {
            _dispatcher.Post(() => IsBusy = false);
        }
    }

    [RelayCommand]
    public async Task RunForgeDoctorAsync()
    {
        if (IsBusy)
        {
            return;
        }

        _dispatcher.Post(() =>
        {
            IsBusy = true;
            StatusMessage = "Running Forge Doctor diagnostics...";
        });

        try
        {
            var report = await _services.ForgeDoctor.RunDiagnosticsAsync(
                InstallDirectory,
                _startupPlan.EffectiveWslDistroName,
                InstallerConstants.DefaultOllamaRouterEndpoint,
                _lifetimeToken).ConfigureAwait(false);

            _dispatcher.Post(() =>
            {
                DoctorChecks.Clear();
                foreach (var check in report.Checks)
                {
                    DoctorChecks.Add(check);
                }

                HasDoctorReport = true;
                DoctorSummaryText =
                    $"{report.PassedCount} passed, {report.WarningCount} warning(s), {report.FailedCount} failed (checked {report.CheckedAtUtc:HH:mm:ss} UTC).";
                StatusMessage = $"Forge Doctor complete: {DoctorSummaryText}";
            });
        }
        catch (OperationCanceledException)
        {
            _dispatcher.Post(() => StatusMessage = "Forge Doctor diagnostics cancelled.");
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() =>
            {
                StatusMessage = $"Forge Doctor failed: {ex.Message}";
                LogLines.Add($"[Error] {ex.Message}");
            });
        }
        finally
        {
            _dispatcher.Post(() => IsBusy = false);
        }
    }

    [RelayCommand]
    private void OpenUninstaller() => _openUninstaller();

    private async Task RefreshStatusAndUpdatesCoreAsync()
    {
        var source = CurrentSourceOptions;
        var availability = await _services.UpgradeService.CheckAvailableUpdatesAsync(
            source,
            InstallDirectory,
            _lifetimeToken).ConfigureAwait(false);

        _latestDiscovery = availability.Discovery;
        var wslDistro = await ResolveActiveWslDistroAsync().ConfigureAwait(false);
        await RefreshCompanionToolsCoreAsync(wslDistro).ConfigureAwait(false);

        _dispatcher.Post(() =>
        {
            var ncf = availability.Discovery.NightCodeForge;
            if (string.IsNullOrWhiteSpace(_startupPlan.CliOptions.InstallDirectory) && ncf.IsInstalled)
            {
                InstallDirectory = ncf.InstallDirectory;
            }

            InstalledVersion = FormatVersion(ncf.Version);
            IsLegacyPublishInstall = ncf.IsLegacyPublishInstall;
            OllamaRouterStatusText = FormatOllamaRouterStatus(availability.Discovery.OllamaRouter);
            OllamaRouterVersionText = FormatVersion(availability.Discovery.OllamaRouter.Version);
            WebUrl = _services.HostAppControl.GetConfiguredWebUrl(InstallDirectory);
            RefreshRunningState();

            AvailableVersion = availability.NightCodeForgeRelease is not null
                ? $"v{availability.NightCodeForgeRelease.Version}"
                : "Unavailable";

            IsNightCodeForgeUpdateAvailable = availability.IsNightCodeForgeUpdateAvailable;
            IsOllamaRouterUpdateAvailable = availability.IsOllamaRouterUpdateAvailable;
            CanUpgradeFromBundle = availability.CanUpgradeFromBundle;

            UpgradeSummaryText = BuildUpgradeSummary(availability);
            StatusMessage = availability.ResolutionWarning ?? "Installation status refreshed.";
        });
    }

    private async Task RefreshCompanionToolsCoreAsync(string? wslDistro)
    {
        var statuses = await _services.NuGetProvisioner.ProbeToolsAsync(wslDistro, _lifetimeToken)
            .ConfigureAwait(false);

        _dispatcher.Post(() =>
        {
            CompanionTools.Clear();
            foreach (var status in statuses)
            {
                CompanionTools.Add(new CompanionToolRowViewModel(
                    status.Spec.ToolCommand,
                    status.Spec.PackageId,
                    status.Spec.Description,
                    status.InstalledOnHost,
                    status.HostVersion,
                    status.InstalledInWsl,
                    status.WslVersion,
                    status.Spec.InstallInWsl,
                    IsWindowsHost));
            }
        });
    }

    private async Task<string?> ResolveActiveWslDistroAsync()
    {
        if (_services.Platform.Platform != HostPlatformType.Windows)
        {
            return null;
        }

        var probe = await _services.EnvironmentProvisioner.ProbeAsync(
            _startupPlan.EffectiveWslDistroName,
            _lifetimeToken).ConfigureAwait(false);

        return probe.WslInstalled && probe.WslDistroInstalled
            ? probe.ResolvedWslDistroName ?? _startupPlan.EffectiveWslDistroName
            : null;
    }

    private void RefreshRunningState()
    {
        var running = _services.HostAppControl.IsWebServerRunning();
        IsWebServerRunning = running;
        ServerStatusText = running
            ? InstallerConstants.UiStatusWebRunning
            : InstallerConstants.UiStatusWebStopped;
    }

    private void ResetStaleUpdateAvailability()
    {
        IsNightCodeForgeUpdateAvailable = false;
        IsOllamaRouterUpdateAvailable = false;
        AvailableVersion = "Click Refresh Status to check";
        UpgradeSummaryText = "Source changed. Click Refresh Status to resolve available packages.";
    }

    private void UpdateSourceState()
    {
        var assets = _services.HostAppControl.ScanBundleDirectoryAssets(BundleDirectory) ?? [];
        var hasInstallArchive = assets.Any(static name =>
            name.EndsWith(InstallerConstants.ZipExtension, StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(InstallerConstants.TarGzExtension, StringComparison.OrdinalIgnoreCase));
        DetectedBundleSummary = assets.Count == 0
            ? "No bundle assets detected."
            : $"{assets.Count} local bundle asset(s) detected.";
        CanUpgradeFromBundle = IsAirGappedSource && hasInstallArchive;
        OnPropertyChanged(nameof(UpgradeButtonLabel));
        NotifyUpgradeStateChanged();
    }

    private void NotifyUpgradeStateChanged()
    {
        OnPropertyChanged(nameof(CanExecuteUpgrade));
        UpgradeAllCommand.NotifyCanExecuteChanged();
    }

    private static string BuildUpgradeSummary(UpgradeAvailabilityResult availability)
    {
        if (availability.CanUpgradeFromBundle)
        {
            return $"Local bundle ready: NightCodeForge v{availability.NightCodeForgeRelease?.Version ?? "?"}, OllamaRouter v{availability.OllamaRouterRelease?.Version ?? "missing"}.";
        }

        if (availability.IsNightCodeForgeUpdateAvailable || availability.IsOllamaRouterUpdateAvailable)
        {
            return $"Update available: NightCodeForge {FormatVersion(availability.NightCodeForgeRelease?.Version)}, OllamaRouter {FormatVersion(availability.OllamaRouterRelease?.Version)}.";
        }

        if (!string.IsNullOrWhiteSpace(availability.ResolutionWarning))
        {
            return availability.ResolutionWarning;
        }

        return "NightCodeForge and OllamaRouter are up to date.";
    }

    private static string FormatSyncedModels(RouterRoleRecommendationsSummary summary) =>
        $"Architect: {summary.ArchitectModelId ?? "-"} | Coder: {summary.CoderModelId ?? "-"} | Default: {summary.DefaultModelId ?? "-"} ({summary.ContextWindowTokens:N0} ctx)";

    private static string FormatOllamaRouterStatus(InstalledComponentInfo info) =>
        info.IsInstalled
            ? $"Installed v{info.Version ?? "unknown"} ({info.InstallDirectory})"
            : "Not installed";

    private static string FormatVersion(string? version) =>
        string.IsNullOrWhiteSpace(version) ? "Unknown" : $"v{version.Trim().TrimStart('v', 'V')}";
}
