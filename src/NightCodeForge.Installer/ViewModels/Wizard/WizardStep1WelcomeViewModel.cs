namespace NightCodeForge.Installer.ViewModels.Wizard;

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NightCodeForge.Installer.Core.Abstractions;
using NightCodeForge.Installer.Core.Constants;
using NightCodeForge.Installer.Core.Models;
using NightCodeForge.Installer.Core.Services;

/// <summary>
/// Step 1 ViewModel: Welcome, installation source selector (Online vs. Air-Gapped Local Bundle),
/// detected host summary, and prerequisite toggles.
/// </summary>
public sealed partial class WizardStep1WelcomeViewModel : ObservableObject
{
    private readonly SetupWizardState _state;
    private readonly IEnvironmentProvisioner _environmentProvisioner;
    private readonly IInstallationDetector _installationDetector;
    private readonly ICompanionNuGetProvisioner _nuGetProvisioner;
    private readonly IHostAppControlService _hostAppControl;
    private readonly IInstallerUiHost _uiHost;
    private readonly IUiDispatcher _dispatcher;
    private bool _synchronizingSource;
    private bool _suppressConfigChanged;

    public WizardStep1WelcomeViewModel(
        SetupWizardState state,
        IEnvironmentProvisioner environmentProvisioner,
        IInstallationDetector installationDetector,
        ICompanionNuGetProvisioner nuGetProvisioner,
        IHostAppControlService hostAppControl,
        IInstallerUiHost uiHost,
        IUiDispatcher dispatcher)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _environmentProvisioner = environmentProvisioner ?? throw new ArgumentNullException(nameof(environmentProvisioner));
        _installationDetector = installationDetector ?? throw new ArgumentNullException(nameof(installationDetector));
        _nuGetProvisioner = nuGetProvisioner ?? throw new ArgumentNullException(nameof(nuGetProvisioner));
        _hostAppControl = hostAppControl ?? throw new ArgumentNullException(nameof(hostAppControl));
        _uiHost = uiHost ?? throw new ArgumentNullException(nameof(uiHost));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

        DetectedBundleAssets = [];

        _synchronizingSource = true;
        _suppressConfigChanged = true;
        IsOnlineSource = _state.SourceMode == InstallSourceMode.Online;
        IsAirGappedSource = _state.SourceMode == InstallSourceMode.AirGappedBundle;
        BundleDirectory = _state.BundleDirectory ?? string.Empty;
        InstallDirectory = _state.InstallDirectory;
        WslDistroName = _state.WslDistroName;
        IsUpgradeMode = _state.IsUpgradeMode;
        IsWindowsHost = _state.Platform == HostPlatformType.Windows;
        UpgradeBannerText = BuildUpgradeBannerText(_state.Discovery);

        ProvisionWsl = _state.ProvisionWsl;
        ProvisionHostDotNet = _state.ProvisionHostDotNet;
        ProvisionLinuxOrWslToolchain = _state.ProvisionLinuxOrWslToolchain;
        IncludeDocker = _state.IncludeDocker;
        InstallOllamaRouter = _state.InstallOllamaRouter;
        InstallCompanionTools = _state.InstallCompanionTools;
        _synchronizingSource = false;
        _suppressConfigChanged = false;

        WslStatusText = "Pending host scan...";
        DockerStatusText = "Pending host scan...";
        DotNetStatusText = "Pending host scan...";
        GitStatusText = "Pending host scan...";
        OllamaRouterStatusText = FormatOllamaRouterSummary(_state.Discovery.OllamaRouter);
        CompanionToolsStatusText = "Pending tool scan...";
        StatusMessage = "Review host prerequisites and installation source.";

        RefreshBundleAssets();
    }

    public event EventHandler? ConfigurationChanged;

    public ObservableCollection<string> DetectedBundleAssets { get; }

    [ObservableProperty]
    public partial bool IsOnlineSource { get; set; }

    [ObservableProperty]
    public partial bool IsAirGappedSource { get; set; }

    [ObservableProperty]
    public partial string BundleDirectory { get; set; }

    [ObservableProperty]
    public partial string InstallDirectory { get; set; }

    [ObservableProperty]
    public partial string WslDistroName { get; set; }

    [ObservableProperty]
    public partial bool IsWindowsHost { get; set; }

    [ObservableProperty]
    public partial bool IsUpgradeMode { get; set; }

    [ObservableProperty]
    public partial string UpgradeBannerText { get; set; }

    [ObservableProperty]
    public partial string WslStatusText { get; set; }

    [ObservableProperty]
    public partial string DockerStatusText { get; set; }

    [ObservableProperty]
    public partial string DotNetStatusText { get; set; }

    [ObservableProperty]
    public partial string GitStatusText { get; set; }

    [ObservableProperty]
    public partial string OllamaRouterStatusText { get; set; }

    [ObservableProperty]
    public partial string CompanionToolsStatusText { get; set; }

    [ObservableProperty]
    public partial bool ProvisionWsl { get; set; }

    [ObservableProperty]
    public partial bool ProvisionHostDotNet { get; set; }

    [ObservableProperty]
    public partial bool ProvisionLinuxOrWslToolchain { get; set; }

    [ObservableProperty]
    public partial bool IncludeDocker { get; set; }

    [ObservableProperty]
    public partial bool InstallOllamaRouter { get; set; }

    [ObservableProperty]
    public partial bool InstallCompanionTools { get; set; }

    [ObservableProperty]
    public partial bool IsProbing { get; set; }

    [ObservableProperty]
    public partial string StatusMessage { get; set; }

    public bool IsConfigurationValid =>
        !string.IsNullOrWhiteSpace(InstallDirectory) &&
        (!IsWindowsHost || !string.IsNullOrWhiteSpace(WslDistroName));

    public string BundleAssetSummary =>
        DetectedBundleAssets.Count == 0
            ? "No .zip, .tar.gz, or .nupkg bundle assets detected in selected folder."
            : $"{DetectedBundleAssets.Count} local bundle asset(s) detected.";

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
                _state.SourceMode = InstallSourceMode.Online;
            }
            else if (!IsAirGappedSource)
            {
                IsAirGappedSource = true;
                _state.SourceMode = InstallSourceMode.AirGappedBundle;
            }
        }
        finally
        {
            _synchronizingSource = false;
        }

        NotifyConfigurationChanged();
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
                _state.SourceMode = InstallSourceMode.AirGappedBundle;
            }
            else if (!IsOnlineSource)
            {
                IsOnlineSource = true;
                _state.SourceMode = InstallSourceMode.Online;
            }
        }
        finally
        {
            _synchronizingSource = false;
        }

        NotifyConfigurationChanged();
    }

    partial void OnBundleDirectoryChanged(string value)
    {
        _state.BundleDirectory = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        RefreshBundleAssets();
        NotifyConfigurationChanged();
    }

    partial void OnInstallDirectoryChanged(string value)
    {
        _state.InstallDirectory = value.Trim();
        OnPropertyChanged(nameof(IsConfigurationValid));
        if (string.IsNullOrWhiteSpace(_state.InstallDirectory))
        {
            StatusMessage = "Install directory cannot be empty.";
        }

        NotifyConfigurationChanged();
    }

    partial void OnWslDistroNameChanged(string value)
    {
        _state.WslDistroName = value.Trim();
        OnPropertyChanged(nameof(IsConfigurationValid));
        if (IsWindowsHost && string.IsNullOrWhiteSpace(_state.WslDistroName))
        {
            StatusMessage = "WSL2 distribution name cannot be empty on Windows hosts.";
        }

        NotifyConfigurationChanged();
    }

    partial void OnIsWindowsHostChanged(bool value) => OnPropertyChanged(nameof(IsConfigurationValid));

    partial void OnProvisionWslChanged(bool value)
    {
        _state.ProvisionWsl = value;
        NotifyConfigurationChanged();
    }

    partial void OnProvisionHostDotNetChanged(bool value)
    {
        _state.ProvisionHostDotNet = value;
        NotifyConfigurationChanged();
    }

    partial void OnProvisionLinuxOrWslToolchainChanged(bool value)
    {
        _state.ProvisionLinuxOrWslToolchain = value;
        NotifyConfigurationChanged();
    }

    partial void OnIncludeDockerChanged(bool value)
    {
        _state.IncludeDocker = value;
        NotifyConfigurationChanged();
    }

    partial void OnInstallOllamaRouterChanged(bool value)
    {
        _state.InstallOllamaRouter = value;
        NotifyConfigurationChanged();
    }

    partial void OnInstallCompanionToolsChanged(bool value)
    {
        _state.InstallCompanionTools = value;
        NotifyConfigurationChanged();
    }

    public void ApplyConfiguration(InstallSourceOptions source, string? installDirectory)
    {
        ArgumentNullException.ThrowIfNull(source);

        _synchronizingSource = true;
        _suppressConfigChanged = true;
        try
        {
            _state.SourceMode = source.Mode;
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
            _suppressConfigChanged = false;
        }
    }

    public void ApplyUpgradeMode(bool isUpgradeMode, InstallationDiscoveryResult discovery)
    {
        ArgumentNullException.ThrowIfNull(discovery);

        _state.Discovery = discovery;
        _state.IsUpgradeMode = isUpgradeMode;
        IsUpgradeMode = isUpgradeMode;
        UpgradeBannerText = BuildUpgradeBannerText(discovery);
        OllamaRouterStatusText = FormatOllamaRouterSummary(discovery.OllamaRouter);
    }

    [RelayCommand]
    public async Task BrowseBundleFolderAsync()
    {
        try
        {
            var picked = await _uiHost.PickBundleFolderAsync(
                string.IsNullOrWhiteSpace(BundleDirectory) ? null : BundleDirectory,
                _state.LifetimeToken).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(picked))
            {
                return;
            }

            _dispatcher.Post(() =>
            {
                BundleDirectory = picked;
                IsAirGappedSource = true;
                StatusMessage = $"Selected air-gapped bundle folder: {picked}";
            });
        }
        catch (OperationCanceledException)
        {
            // Justification: Folder picker cancelled or closed during teardown.
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => StatusMessage = $"Unable to browse bundle folder: {ex.Message}");
        }
    }

    [RelayCommand]
    public async Task RescanEnvironmentAsync()
    {
        if (IsProbing)
        {
            return;
        }

        _dispatcher.Post(() =>
        {
            IsProbing = true;
            StatusMessage = "Scanning host environment, WSL2, Docker, and companion tools...";
        });

        try
        {
            var distroForProbe = string.IsNullOrWhiteSpace(_state.WslDistroName)
                ? InstallerConstants.DefaultWslDistro
                : _state.WslDistroName;
            var probe = await _environmentProvisioner.ProbeAsync(distroForProbe, _state.LifetimeToken)
                .ConfigureAwait(false);
            var discovery = _installationDetector.Detect(
                _state.BundleDirectory,
                string.IsNullOrWhiteSpace(_state.InstallDirectory) ? null : _state.InstallDirectory);
            var wslDistro = probe.Platform == HostPlatformType.Windows && probe.WslInstalled && probe.WslDistroInstalled
                ? probe.ResolvedWslDistroName ?? distroForProbe
                : null;
            var tools = await _nuGetProvisioner.ProbeToolsAsync(wslDistro, _state.LifetimeToken)
                .ConfigureAwait(false);

            _state.LatestProbe = probe;
            _state.Discovery = discovery;

            _dispatcher.Post(() =>
            {
                IsWindowsHost = probe.Platform == HostPlatformType.Windows;
                if (string.IsNullOrWhiteSpace(BundleDirectory) && !string.IsNullOrWhiteSpace(discovery.DetectedBundleDirectory))
                {
                    _suppressConfigChanged = true;
                    try
                    {
                        BundleDirectory = discovery.DetectedBundleDirectory;
                    }
                    finally
                    {
                        _suppressConfigChanged = false;
                    }
                }

                ApplyProbeSummary(probe, discovery, tools);
                StatusMessage = IsConfigurationValid
                    ? "Host environment scan complete."
                    : "Host scan complete. Fix invalid configuration fields above before continuing.";
            });
        }
        catch (OperationCanceledException)
        {
            // Justification: Lifetime token cancellation on window shutdown.
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() =>
            {
                StatusMessage = $"Environment probe warning: {ex.Message}";
            });
        }
        finally
        {
            _dispatcher.Post(() => IsProbing = false);
        }
    }

    private void NotifyConfigurationChanged()
    {
        if (!_suppressConfigChanged)
        {
            ConfigurationChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void RefreshBundleAssets()
    {
        var assets = _hostAppControl.ScanBundleDirectoryAssets(BundleDirectory) ?? [];
        DetectedBundleAssets.Clear();
        foreach (var asset in assets)
        {
            DetectedBundleAssets.Add(asset);
        }

        OnPropertyChanged(nameof(BundleAssetSummary));
    }

    private void ApplyProbeSummary(
        EnvironmentProbeResult probe,
        InstallationDiscoveryResult discovery,
        IReadOnlyList<CompanionToolStatus> tools)
    {
        WslStatusText = probe.Platform != HostPlatformType.Windows
            ? $"Native {probe.Platform} ({probe.RuntimeIdentifier})"
            : probe.WslInstalled
                ? probe.WslDistroInstalled
                    ? $"Installed ({probe.ResolvedWslDistroName})"
                    : $"WSL2 enabled, distro '{_state.WslDistroName}' missing"
                : "Not installed (will enable with UAC elevation)";

        var dockerReady = probe.Platform == HostPlatformType.Windows
            ? probe.WslDockerInstalled
            : probe.HostDockerAvailable;
        DockerStatusText = dockerReady ? "Available" : "Missing (will provision)";

        DotNetStatusText = probe.Platform == HostPlatformType.Windows
            ? $"Host: {(probe.HostDotNet10Installed ? "Ready" : "Missing")} | WSL2: {(probe.WslDotNet10Installed ? "Ready" : "Missing")}"
            : probe.HostDotNet10Installed
                ? ".NET 10 SDK Ready"
                : ".NET 10 SDK Missing";

        GitStatusText = probe.HostGitInstalled ? "Installed" : "Not detected on PATH";
        OllamaRouterStatusText = FormatOllamaRouterSummary(discovery.OllamaRouter);
        UpgradeBannerText = BuildUpgradeBannerText(discovery);

        var installedHostCount = tools.Count(static t => t.InstalledOnHost);
        CompanionToolsStatusText = $"{installedHostCount} / {tools.Count} installed on Host";
    }

    private static string FormatOllamaRouterSummary(InstalledComponentInfo info) =>
        info.IsInstalled
            ? $"Installed v{info.Version ?? "unknown"} ({info.InstallDirectory})"
            : "Not installed yet (will install)";

    private static string BuildUpgradeBannerText(InstallationDiscoveryResult discovery)
    {
        if (!discovery.NightCodeForge.IsInstalled)
        {
            return string.Empty;
        }

        var legacyLabel = discovery.NightCodeForge.IsLegacyPublishInstall ? " (legacy publish.ps1 install)" : string.Empty;
        return $"Upgrade / Repair Mode: existing NightCodeForge v{discovery.NightCodeForge.Version ?? "unknown"}{legacyLabel} detected at '{discovery.NightCodeForge.InstallDirectory}'.";
    }
}
