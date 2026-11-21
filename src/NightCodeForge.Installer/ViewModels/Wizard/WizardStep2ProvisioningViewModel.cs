namespace NightCodeForge.Installer.ViewModels.Wizard;

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NightCodeForge.Installer.Core.Constants;
using NightCodeForge.Installer.Core.Models;
using NightCodeForge.Installer.Core.Services;

/// <summary>
/// Step 2 ViewModel: Host &amp; subsystem provisioning stepper with real-time logs and WSL2 3010 reboot detection.
/// </summary>
public sealed partial class WizardStep2ProvisioningViewModel : ObservableObject
{
    private readonly SetupWizardState _state;
    private readonly IEnvironmentProvisioner _environmentProvisioner;
    private readonly IInstallerUiHost _uiHost;
    private readonly IUiDispatcher _dispatcher;

    public WizardStep2ProvisioningViewModel(
        SetupWizardState state,
        IEnvironmentProvisioner environmentProvisioner,
        IInstallerUiHost uiHost,
        IUiDispatcher dispatcher)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _environmentProvisioner = environmentProvisioner ?? throw new ArgumentNullException(nameof(environmentProvisioner));
        _uiHost = uiHost ?? throw new ArgumentNullException(nameof(uiHost));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

        Steps = [];
        LogLines = [];
        OverallStatus = ProvisioningStatus.NotStarted;
        StatusMessage = "Ready to provision host and subsystem prerequisites.";
    }

    public ObservableCollection<ProvisioningStepResult> Steps { get; }

    public ObservableCollection<string> LogLines { get; }

    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial bool IsCompleted { get; set; }

    [ObservableProperty]
    public partial bool IsRebootRequired { get; set; }

    [ObservableProperty]
    public partial double ProgressPercent { get; set; }

    [ObservableProperty]
    public partial ProvisioningStatus OverallStatus { get; set; }

    [ObservableProperty]
    public partial string StatusMessage { get; set; }

    public Task OnStepEnteredAsync()
    {
        if (IsRunning || IsCompleted || IsRebootRequired)
        {
            return Task.CompletedTask;
        }

        if (AreSelectedPrerequisitesSatisfied(_state.LatestProbe, _state.ToProvisioningOptions()))
        {
            _dispatcher.Post(() =>
            {
                OverallStatus = ProvisioningStatus.Completed;
                IsCompleted = true;
                ProgressPercent = 100.0;
                StatusMessage = "All selected prerequisites are already installed and ready.";
            });
            return Task.CompletedTask;
        }

        return RunProvisioningAsync();
    }

    [RelayCommand]
    public async Task RunProvisioningAsync()
    {
        if (IsRunning)
        {
            return;
        }

        _dispatcher.Post(() =>
        {
            IsRunning = true;
            IsCompleted = false;
            IsRebootRequired = false;
            ProgressPercent = 5.0;
            OverallStatus = ProvisioningStatus.InProgress;
            StatusMessage = "Provisioning host and subsystem dependencies...";
            Steps.Clear();
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
            var report = await _environmentProvisioner.ProvisionAsync(
                _state.ToProvisioningOptions(),
                progress,
                CancellationToken.None).ConfigureAwait(false);

            _state.LatestProbe = report.PostProbe;

            _dispatcher.Post(() =>
            {
                Steps.Clear();
                foreach (var step in report.Steps)
                {
                    Steps.Add(step);
                }

                OverallStatus = report.OverallStatus;
                IsRebootRequired = report.OverallStatus == ProvisioningStatus.RebootRequired;
                IsCompleted = report.OverallStatus is ProvisioningStatus.Completed or ProvisioningStatus.Skipped;
                ProgressPercent = report.OverallStatus == ProvisioningStatus.Failed ? ProgressPercent : 100.0;
                StatusMessage = report.OverallStatus switch
                {
                    ProvisioningStatus.RebootRequired => "Windows requires a reboot (WSL2 exit code 3010). Please restart your PC and re-run the installer.",
                    ProvisioningStatus.Failed => "One or more provisioning steps failed. Inspect the log output above.",
                    _ => "Host and subsystem provisioning completed."
                };
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _dispatcher.Post(() =>
            {
                OverallStatus = ProvisioningStatus.Failed;
                StatusMessage = $"Provisioning failed: {ex.Message}";
                LogLines.Add($"[Error] {ex.Message}");
            });
        }
        finally
        {
            _dispatcher.Post(() => IsRunning = false);
        }
    }

    [RelayCommand]
    private void CloseForReboot() => _uiHost.RequestExit(InstallerConstants.ExitCodeRebootRequired);

    internal static bool AreSelectedPrerequisitesSatisfied(EnvironmentProbeResult? probe, ProvisioningOptions options)
    {
        if (probe is null)
        {
            return false;
        }

        if (probe.Platform == HostPlatformType.Windows)
        {
            if (options.ProvisionWsl && (!probe.WslInstalled || !probe.WslDistroInstalled))
            {
                return false;
            }

            if (options.ProvisionHostDotNet && !probe.HostDotNet10Installed)
            {
                return false;
            }

            if (options.ProvisionLinuxOrWslToolchain)
            {
                if (!probe.WslDotNet10Installed || !probe.WslRipgrepInstalled)
                {
                    return false;
                }

                if (!options.SkipDocker && !probe.WslDockerInstalled)
                {
                    return false;
                }
            }

            return true;
        }

        if (!probe.HostDotNet10Installed || !probe.HostGitInstalled || !probe.HostRipgrepInstalled)
        {
            return false;
        }

        return options.SkipDocker || probe.HostDockerAvailable;
    }
}
