namespace NightCodeForge.Installer.ViewModels.Wizard;

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NightCodeForge.Installer.Core.Abstractions;
using NightCodeForge.Installer.Core.Constants;
using NightCodeForge.Installer.Core.Models;
using NightCodeForge.Installer.Core.Services;

/// <summary>
/// Step 3 ViewModel: Deploys NightCodeForge Web + Native AOT <c>ncf-web</c>, OllamaRouter,
/// all 5 companion NuGet global tools, and OS uninstall/shortcut registration.
/// </summary>
public sealed partial class WizardStep3SoftwareViewModel : ObservableObject
{
    private readonly SetupWizardState _state;
    private readonly IReleaseManager _releaseManager;
    private readonly ICompanionNuGetProvisioner _nuGetProvisioner;
    private readonly IEnvironmentProvisioner _environmentProvisioner;
    private readonly IPlatformEnvironment _platform;
    private readonly IUiDispatcher _dispatcher;

    public WizardStep3SoftwareViewModel(
        SetupWizardState state,
        IReleaseManager releaseManager,
        ICompanionNuGetProvisioner nuGetProvisioner,
        IEnvironmentProvisioner environmentProvisioner,
        IPlatformEnvironment platform,
        IUiDispatcher dispatcher)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _releaseManager = releaseManager ?? throw new ArgumentNullException(nameof(releaseManager));
        _nuGetProvisioner = nuGetProvisioner ?? throw new ArgumentNullException(nameof(nuGetProvisioner));
        _environmentProvisioner = environmentProvisioner ?? throw new ArgumentNullException(nameof(environmentProvisioner));
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

        SummaryLines = [];
        LogLines = [];
        CreateDesktopShortcut = _state.CreateDesktopShortcut;
        StatusMessage = "Ready to install NightCodeForge, OllamaRouter, and companion tools.";
        ErrorMessage = string.Empty;
    }

    public ObservableCollection<string> SummaryLines { get; }

    public ObservableCollection<string> LogLines { get; }

    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial bool IsCompleted { get; set; }

    [ObservableProperty]
    public partial bool HasError { get; set; }

    [ObservableProperty]
    public partial double ProgressPercent { get; set; }

    [ObservableProperty]
    public partial string StatusMessage { get; set; }

    [ObservableProperty]
    public partial string ErrorMessage { get; set; }

    [ObservableProperty]
    public partial bool CreateDesktopShortcut { get; set; }

    partial void OnCreateDesktopShortcutChanged(bool value) => _state.CreateDesktopShortcut = value;

    public Task OnStepEnteredAsync()
    {
        if (IsRunning || IsCompleted)
        {
            return Task.CompletedTask;
        }

        return RunInstallationAsync();
    }

    [RelayCommand]
    public async Task RunInstallationAsync()
    {
        if (IsRunning)
        {
            return;
        }

        _dispatcher.Post(() =>
        {
            IsRunning = true;
            IsCompleted = false;
            HasError = false;
            ErrorMessage = string.Empty;
            ProgressPercent = 5.0;
            StatusMessage = "Starting software and companion tool installation...";
            SummaryLines.Clear();
            LogLines.Clear();
        });

        var source = _state.ToInstallSourceOptions();
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
            var ncfAsset = await _releaseManager.ResolveLatestNightCodeForgeReleaseAsync(source, CancellationToken.None)
                .ConfigureAwait(false);
            var ncfDeployment = await _releaseManager.DeployNightCodeForgeAsync(
                ncfAsset,
                _state.InstallDirectory,
                CreateDesktopShortcut,
                CreateBandProgress(progress, 5.0, 45.0),
                CancellationToken.None).ConfigureAwait(false);

            _state.NightCodeForgeDeployment = ncfDeployment;
            _state.InstallDirectory = ncfDeployment.InstallDirectory;
            _dispatcher.Post(() =>
                SummaryLines.Add($"NightCodeForge Web v{ncfDeployment.Version} deployed to '{ncfDeployment.InstallDirectory}'."));

            if (_state.InstallOllamaRouter)
            {
                var routerAsset = await _releaseManager.ResolveLatestOllamaRouterReleaseAsync(source, CancellationToken.None)
                    .ConfigureAwait(false);
                var routerDeployment = await _releaseManager.DeployOllamaRouterAsync(
                    routerAsset,
                    targetInstallDirectory: null,
                    CreateBandProgress(progress, 45.0, 72.0),
                    CancellationToken.None).ConfigureAwait(false);

                _state.OllamaRouterDeployment = routerDeployment;
                _dispatcher.Post(() =>
                    SummaryLines.Add($"OllamaRouter v{routerDeployment.Version} deployed to '{routerDeployment.InstallDirectory}'."));
            }

            if (_state.InstallCompanionTools)
            {
                var wslDistro = await ResolveWslDistroAsync().ConfigureAwait(false);
                var outcomes = await _nuGetProvisioner.ProvisionAllAsync(
                    source,
                    wslDistro,
                    CreateBandProgress(progress, 72.0, 98.0),
                    CancellationToken.None).ConfigureAwait(false);

                _state.CompanionOutcomes = outcomes;
                _dispatcher.Post(() =>
                    SummaryLines.Add($"Provisioned {outcomes.Count} companion NuGet tool target(s) ({string.Join(", ", CompanionToolCatalog.All.Select(static t => t.ToolCommand))})."));
            }

            _dispatcher.Post(() =>
            {
                IsCompleted = true;
                ProgressPercent = 100.0;
                StatusMessage = "Software, companion tools, and OS integration installed successfully.";
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _dispatcher.Post(() =>
            {
                HasError = true;
                ErrorMessage = ex.Message;
                StatusMessage = $"Installation failed: {ex.Message}";
                LogLines.Add($"[Error] {ex.Message}");
            });
        }
        finally
        {
            _dispatcher.Post(() => IsRunning = false);
        }
    }

    private async Task<string?> ResolveWslDistroAsync()
    {
        if (_platform.Platform != HostPlatformType.Windows)
        {
            return null;
        }

        var probe = _state.LatestProbe
                    ?? await _environmentProvisioner.ProbeAsync(_state.WslDistroName, CancellationToken.None).ConfigureAwait(false);
        _state.LatestProbe = probe;

        return probe.WslInstalled && probe.WslDistroInstalled
            ? probe.ResolvedWslDistroName ?? _state.WslDistroName
            : null;
    }

    private static IProgress<InstallerProgressEvent> CreateBandProgress(
        IProgress<InstallerProgressEvent> outer,
        double startPercent,
        double endPercent) =>
        new BandProgress(outer, startPercent, endPercent);

    private sealed class BandProgress(IProgress<InstallerProgressEvent> inner, double start, double end)
        : IProgress<InstallerProgressEvent>
    {
        public void Report(InstallerProgressEvent value)
        {
            var mapped = value.ProgressPercent.HasValue
                ? start + (Math.Clamp(value.ProgressPercent.Value, 0.0, 100.0) / 100.0 * (end - start))
                : (double?)null;

            inner.Report(value with { ProgressPercent = mapped });
        }
    }
}
