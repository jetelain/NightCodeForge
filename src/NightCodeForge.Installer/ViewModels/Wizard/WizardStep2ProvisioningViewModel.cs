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

    public ObservableCollection<WizardChecklistItemViewModel> Steps { get; }

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

    public void ResetState()
    {
        IsRunning = false;
        IsCompleted = false;
        IsRebootRequired = false;
        ProgressPercent = 0.0;
        OverallStatus = ProvisioningStatus.NotStarted;
        StatusMessage = "Ready to provision host and subsystem prerequisites.";
        Steps.Clear();
        LogLines.Clear();
    }

    public Task OnStepEnteredAsync()
    {
        if (IsRunning || IsCompleted || IsRebootRequired)
        {
            return Task.CompletedTask;
        }

        var options = _state.ToProvisioningOptions();
        if (AreSelectedPrerequisitesSatisfied(_state.LatestProbe, options))
        {
            _dispatcher.Post(() =>
            {
                PopulatePlannedSteps(_state.LatestProbe, options, markSatisfiedImmediately: true);
                LogLines.Clear();
                foreach (var step in Steps)
                {
                    LogLines.Add($"[{step.Title}] {step.Detail}");
                }

                OverallStatus = ProvisioningStatus.Verified;
                IsCompleted = true;
                ProgressPercent = 100.0;
                StatusMessage = "All selected prerequisites are already installed and verified.";
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

        var options = _state.ToProvisioningOptions();

        _dispatcher.Post(() =>
        {
            IsRunning = true;
            IsCompleted = false;
            IsRebootRequired = false;
            ProgressPercent = 5.0;
            OverallStatus = ProvisioningStatus.InProgress;
            StatusMessage = "Provisioning host and subsystem dependencies...";
            LogLines.Clear();
            PopulatePlannedSteps(_state.LatestProbe, options, markSatisfiedImmediately: false);
        });

        var progress = new UiDispatchedProgress<InstallerProgressEvent>(_dispatcher, ev =>
        {
            LogLines.Add($"[{ev.Step}] {ev.Message}");
            StatusMessage = ev.Message;
            if (ev.ProgressPercent.HasValue)
            {
                ProgressPercent = ev.ProgressPercent.Value;
            }

            if (!string.IsNullOrWhiteSpace(ev.ItemKey) && ev.ItemStatus.HasValue)
            {
                UpsertStepItem(ev.ItemKey, ev.ItemStatus.Value, ev.Message);
            }
        });

        try
        {
            var report = await _environmentProvisioner.ProvisionAsync(
                options,
                progress,
                _state.LifetimeToken).ConfigureAwait(false);

            _state.LatestProbe = report.PostProbe;

            _dispatcher.Post(() =>
            {
                foreach (var step in report.Steps)
                {
                    UpsertStepItem(step.StepName, step.Status, step.Message);
                }

                if (report.OverallStatus == ProvisioningStatus.RebootRequired)
                {
                    foreach (var item in Steps)
                    {
                        if (item.Status == ProvisioningStatus.NotStarted)
                        {
                            item.Update(ProvisioningStatus.NotStarted, "Waiting for Windows reboot.");
                        }
                    }
                }
                else if (report.OverallStatus == ProvisioningStatus.Failed)
                {
                    foreach (var item in Steps)
                    {
                        if (item.Status == ProvisioningStatus.NotStarted)
                        {
                            item.Update(ProvisioningStatus.NotStarted, "Not run due to an earlier failure.");
                        }
                    }
                }

                OverallStatus = report.OverallStatus;
                IsRebootRequired = report.OverallStatus == ProvisioningStatus.RebootRequired;
                IsCompleted = report.OverallStatus is ProvisioningStatus.Completed or ProvisioningStatus.Verified or ProvisioningStatus.Skipped;
                ProgressPercent = report.OverallStatus == ProvisioningStatus.Failed ? ProgressPercent : 100.0;
                StatusMessage = report.OverallStatus switch
                {
                    ProvisioningStatus.RebootRequired => "Windows requires a reboot (WSL2 exit code 3010). Please restart your PC and re-run the installer.",
                    ProvisioningStatus.Failed => "One or more provisioning steps failed. Inspect the log output above.",
                    ProvisioningStatus.Verified => "All selected prerequisites are verified and ready.",
                    _ => "Host and subsystem provisioning completed."
                };
            });
        }
        catch (OperationCanceledException)
        {
            _dispatcher.Post(() => StatusMessage = "Provisioning cancelled.");
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() =>
            {
                foreach (var item in Steps)
                {
                    if (item.Status == ProvisioningStatus.InProgress)
                    {
                        item.Update(ProvisioningStatus.Failed, ex.Message);
                    }
                }

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

    private void PopulatePlannedSteps(
        EnvironmentProbeResult? probe,
        ProvisioningOptions options,
        bool markSatisfiedImmediately)
    {
        Steps.Clear();
        var platform = probe?.Platform ?? _state.Platform;
        var distroLabel = string.IsNullOrWhiteSpace(probe?.ResolvedWslDistroName)
            ? options.WslDistroName
            : probe.ResolvedWslDistroName;

        if (platform == HostPlatformType.Windows)
        {
            AddPlannedStep(
                ProvisioningStepNames.Wsl2,
                "WSL2 Subsystem",
                options.ProvisionWsl,
                markSatisfiedImmediately,
                "WSL2 is installed and ready.",
                "WSL2 provisioning skipped by options.");
            AddPlannedStep(
                ProvisioningStepNames.Wsl2Distro,
                $"WSL2 Distribution ({distroLabel})",
                options.ProvisionWsl,
                markSatisfiedImmediately,
                $"WSL2 distribution '{distroLabel}' is installed.",
                "WSL2 distribution provisioning skipped by options.");
            AddPlannedStep(
                ProvisioningStepNames.HostDotNet,
                "Host .NET 10 SDK",
                options.ProvisionHostDotNet,
                markSatisfiedImmediately,
                ".NET 10 SDK is installed on Windows host.",
                "Host .NET 10 SDK provisioning skipped by options.");
            AddPlannedStep(
                ProvisioningStepNames.SubsystemToolchain,
                $"WSL2 Toolchain ({distroLabel})",
                options.ProvisionLinuxOrWslToolchain,
                markSatisfiedImmediately,
                $".NET 10 SDK and ripgrep are installed inside '{distroLabel}'.",
                "WSL2 toolchain provisioning skipped by options.");
            AddPlannedStep(
                ProvisioningStepNames.DockerEngine,
                "Docker Engine (WSL2)",
                options.ProvisionLinuxOrWslToolchain && !options.SkipDocker,
                markSatisfiedImmediately,
                $"Docker Engine is running inside '{distroLabel}'.",
                "Docker Engine provisioning skipped by options.");
            return;
        }

        if (platform == HostPlatformType.MacOS)
        {
            AddPlannedStep(
                ProvisioningStepNames.HostDotNet,
                "Host .NET 10 SDK",
                options.ProvisionHostDotNet,
                markSatisfiedImmediately,
                ".NET 10 SDK is installed on macOS host.",
                "Host .NET 10 SDK check skipped by options.");
            AddPlannedStep(
                ProvisioningStepNames.Git,
                "Git",
                options.ProvisionLinuxOrWslToolchain,
                markSatisfiedImmediately,
                "Git is installed.",
                "Git check skipped by options.");
            AddPlannedStep(
                ProvisioningStepNames.Ripgrep,
                "ripgrep (rg)",
                options.ProvisionLinuxOrWslToolchain,
                markSatisfiedImmediately,
                "ripgrep is installed.",
                "ripgrep check skipped by options.");
            AddPlannedStep(
                ProvisioningStepNames.DockerRuntime,
                "Docker Runtime",
                options.ProvisionLinuxOrWslToolchain && !options.SkipDocker,
                markSatisfiedImmediately,
                "Docker runtime is available.",
                "Docker runtime check skipped by options.");
            return;
        }

        AddPlannedStep(
            ProvisioningStepNames.HostDotNet,
            "Host .NET 10 SDK",
            options.ProvisionHostDotNet,
            markSatisfiedImmediately,
            ".NET 10 SDK is installed.",
            "Host .NET 10 SDK provisioning skipped by options.");
        AddPlannedStep(
            ProvisioningStepNames.SubsystemToolchain,
            "Linux Toolchain (git, ripgrep, ICU, locales)",
            options.ProvisionLinuxOrWslToolchain,
            markSatisfiedImmediately,
            "Linux toolchain prerequisites are installed.",
            "Linux toolchain provisioning skipped by options.");
        AddPlannedStep(
            ProvisioningStepNames.DockerEngine,
            "Docker Engine",
            options.ProvisionLinuxOrWslToolchain && !options.SkipDocker,
            markSatisfiedImmediately,
            "Docker Engine is running.",
            "Docker Engine provisioning skipped by options.");
    }

    private void AddPlannedStep(
        string key,
        string title,
        bool isEnabled,
        bool markSatisfiedImmediately,
        string verifiedDetail,
        string skippedDetail)
    {
        if (!isEnabled)
        {
            Steps.Add(new WizardChecklistItemViewModel(key, title, ProvisioningStatus.Skipped, skippedDetail));
            return;
        }

        var status = markSatisfiedImmediately ? ProvisioningStatus.Verified : ProvisioningStatus.NotStarted;
        var detail = markSatisfiedImmediately ? verifiedDetail : "Waiting to verify / install...";
        Steps.Add(new WizardChecklistItemViewModel(key, title, status, detail));
    }

    private void UpsertStepItem(string key, ProvisioningStatus status, string message)
    {
        var existing = Steps.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            existing.Update(status, message);
            return;
        }

        Steps.Add(new WizardChecklistItemViewModel(key, key, status, message));
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

        if (options.ProvisionHostDotNet && !probe.HostDotNet10Installed)
        {
            return false;
        }

        if (options.ProvisionLinuxOrWslToolchain)
        {
            if (!probe.HostGitInstalled || !probe.HostRipgrepInstalled)
            {
                return false;
            }

            if (!options.SkipDocker && !probe.HostDockerAvailable)
            {
                return false;
            }
        }

        return true;
    }
}
