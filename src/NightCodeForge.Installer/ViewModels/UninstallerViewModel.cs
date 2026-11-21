namespace NightCodeForge.Installer.ViewModels;

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NightCodeForge.Installer.Cli;
using NightCodeForge.Installer.Core.Constants;
using NightCodeForge.Installer.Core.Models;
using NightCodeForge.Installer.Core.Services;

/// <summary>
/// Mode 3 ViewModel: Interactive Uninstaller launched from Windows Installed Apps or Maintenance Hub.
/// Defaults to preserving user databases, configuration, and logs (<see cref="KeepData"/> = <c>true</c>).
/// </summary>
public sealed partial class UninstallerViewModel : ObservableObject
{
    private readonly IUninstallService _uninstallService;
    private readonly IInstallationDetector _installationDetector;
    private readonly InstallerStartupPlan _startupPlan;
    private readonly IInstallerUiHost _uiHost;
    private readonly IUiDispatcher _dispatcher;
    private readonly Action _returnToHubAction;
    private readonly bool _canReturnToHub;
    private readonly CancellationToken _lifetimeToken;

    public UninstallerViewModel(
        IUninstallService uninstallService,
        IInstallationDetector installationDetector,
        InstallerStartupPlan startupPlan,
        IInstallerUiHost uiHost,
        IUiDispatcher dispatcher,
        Action returnToHubAction,
        bool canReturnToHub,
        CancellationToken lifetimeToken)
    {
        _uninstallService = uninstallService ?? throw new ArgumentNullException(nameof(uninstallService));
        _installationDetector = installationDetector ?? throw new ArgumentNullException(nameof(installationDetector));
        _startupPlan = startupPlan ?? throw new ArgumentNullException(nameof(startupPlan));
        _uiHost = uiHost ?? throw new ArgumentNullException(nameof(uiHost));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _returnToHubAction = returnToHubAction ?? throw new ArgumentNullException(nameof(returnToHubAction));
        _canReturnToHub = canReturnToHub;
        _lifetimeToken = lifetimeToken;

        Steps = [];
        LogLines = [];

        // Interactive Uninstaller preserves user config/data/logs by default per specification Section 3.4.
        KeepData = true;
        RemoveCompanionTools = startupPlan.CliOptions.UninstallOptions.RemoveCompanionTools;
        RemoveOllamaRouter = startupPlan.CliOptions.UninstallOptions.RemoveOllamaRouter;

        InstallDirectory = startupPlan.EffectiveInstallDirectory;
        InstalledVersion = FormatVersion(startupPlan.Discovery.NightCodeForge.Version);
        IsLegacyInstall = startupPlan.Discovery.NightCodeForge.IsLegacyPublishInstall;
        StatusMessage = "Select uninstallation options and click Uninstall to remove NightCodeForge.";
        ErrorMessage = string.Empty;
    }

    public ObservableCollection<UninstallStepResult> Steps { get; }

    public ObservableCollection<string> LogLines { get; }

    [ObservableProperty]
    public partial string InstallDirectory { get; set; }

    [ObservableProperty]
    public partial string InstalledVersion { get; set; }

    [ObservableProperty]
    public partial bool IsLegacyInstall { get; set; }

    [ObservableProperty]
    public partial bool KeepData { get; set; }

    [ObservableProperty]
    public partial bool RemoveCompanionTools { get; set; }

    [ObservableProperty]
    public partial bool RemoveOllamaRouter { get; set; }

    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial bool IsCompleted { get; set; }

    [ObservableProperty]
    public partial bool Succeeded { get; set; }

    [ObservableProperty]
    public partial bool DeferredSelfCleanupScheduled { get; set; }

    [ObservableProperty]
    public partial double ProgressPercent { get; set; }

    [ObservableProperty]
    public partial string StatusMessage { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    [NotifyPropertyChangedFor(nameof(CanClose))]
    public partial string ErrorMessage { get; set; }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public bool CanUninstall => !IsRunning && !IsCompleted;
    public bool CanCancel => !IsRunning && !IsCompleted;
    public bool CanClose => !IsRunning && (IsCompleted || HasError);

    public void RefreshSummary()
    {
        var discovery = _installationDetector.Detect(
            _startupPlan.EffectiveSource.BundleDirectory,
            _startupPlan.CliOptions.InstallDirectory);
        var effectiveDir = string.IsNullOrWhiteSpace(_startupPlan.CliOptions.InstallDirectory)
            ? discovery.NightCodeForge.InstallDirectory
            : _startupPlan.EffectiveInstallDirectory;
        var formattedVersion = FormatVersion(discovery.NightCodeForge.Version);
        var isLegacy = discovery.NightCodeForge.IsLegacyPublishInstall;

        _dispatcher.Post(() =>
        {
            InstallDirectory = effectiveDir;
            InstalledVersion = formattedVersion;
            IsLegacyInstall = isLegacy;
        });
    }

    partial void OnIsRunningChanged(bool value) => NotifyActionStatesChanged();
    partial void OnIsCompletedChanged(bool value) => NotifyActionStatesChanged();
    partial void OnErrorMessageChanged(string value) => NotifyActionStatesChanged();

    [RelayCommand(CanExecute = nameof(CanUninstall))]
    public async Task UninstallAsync()
    {
        if (IsRunning)
        {
            return;
        }

        _dispatcher.Post(() =>
        {
            IsRunning = true;
            IsCompleted = false;
            Succeeded = false;
            DeferredSelfCleanupScheduled = false;
            ErrorMessage = string.Empty;
            ProgressPercent = 5.0;
            Steps.Clear();
            LogLines.Clear();
            StatusMessage = "Uninstalling NightCodeForge...";
        });

        var options = new UninstallOptions(
            KeepData: KeepData,
            RemoveCompanionTools: RemoveCompanionTools,
            RemoveOllamaRouter: RemoveOllamaRouter,
            Quiet: false);

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
            var result = await _uninstallService.UninstallAsync(
                options,
                InstallDirectory,
                _startupPlan.EffectiveWslDistroName,
                progress,
                _lifetimeToken).ConfigureAwait(false);

            _dispatcher.Post(() =>
            {
                Steps.Clear();
                foreach (var step in result.Steps)
                {
                    Steps.Add(step);
                }

                Succeeded = result.Succeeded;
                DeferredSelfCleanupScheduled = result.DeferredSelfCleanupScheduled;
                IsCompleted = result.Succeeded;

                if (result.Succeeded)
                {
                    ProgressPercent = 100.0;
                    StatusMessage = result.DeferredSelfCleanupScheduled
                        ? "Uninstallation complete. Remaining installer files will finish cleaning up after this window closes."
                        : "Uninstallation completed successfully.";
                }
                else
                {
                    ErrorMessage = result.ErrorMessage ?? "Uninstallation failed.";
                    StatusMessage = ErrorMessage;
                }
            });
        }
        catch (OperationCanceledException)
        {
            _dispatcher.Post(() => StatusMessage = "Uninstallation cancelled.");
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() =>
            {
                Succeeded = false;
                IsCompleted = false;
                ErrorMessage = ex.Message;
                StatusMessage = $"Uninstallation failed: {ex.Message}";
                LogLines.Add($"[Error] {ex.Message}");
            });
        }
        finally
        {
            _dispatcher.Post(() => IsRunning = false);
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    public void Cancel()
    {
        if (_canReturnToHub)
        {
            _returnToHubAction();
            return;
        }

        _uiHost.RequestExit(InstallerConstants.ExitCodeSuccess);
    }

    [RelayCommand(CanExecute = nameof(CanClose))]
    public void Close() =>
        _uiHost.RequestExit(Succeeded ? InstallerConstants.ExitCodeSuccess : InstallerConstants.ExitCodeError);

    private void NotifyActionStatesChanged()
    {
        OnPropertyChanged(nameof(CanUninstall));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanClose));
        UninstallCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        CloseCommand.NotifyCanExecuteChanged();
    }

    private static string FormatVersion(string? version) =>
        string.IsNullOrWhiteSpace(version) ? "Unknown" : $"v{version.Trim().TrimStart('v', 'V')}";
}
