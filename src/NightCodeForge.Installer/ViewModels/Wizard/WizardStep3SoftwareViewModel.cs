namespace NightCodeForge.Installer.ViewModels.Wizard;

using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
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

        Components = [];
        LogLines = [];
        CreateDesktopShortcut = _state.CreateDesktopShortcut;
        StatusMessage = "Ready to install NightCodeForge, OllamaRouter, and companion tools.";
        ErrorMessage = string.Empty;
    }

    public ObservableCollection<WizardChecklistItemViewModel> Components { get; }

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

    public void ResetState()
    {
        IsRunning = false;
        IsCompleted = false;
        HasError = false;
        ErrorMessage = string.Empty;
        ProgressPercent = 0.0;
        StatusMessage = "Ready to install NightCodeForge, OllamaRouter, and companion tools.";
        Components.Clear();
        LogLines.Clear();
    }

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
            LogLines.Clear();
            PopulatePlannedComponents();
        });

        var activeComponentKey = new StrongBox<string?>(DeploymentComponentKeys.NightCodeForgeWeb);
        var source = _state.ToInstallSourceOptions();
        var progress = new UiDispatchedProgress<InstallerProgressEvent>(_dispatcher, ev =>
        {
            LogLines.Add($"[{ev.Step}] {ev.Message}");
            StatusMessage = ev.Message;
            if (ev.ProgressPercent.HasValue)
            {
                ProgressPercent = ev.ProgressPercent.Value;
            }

            if (!string.IsNullOrWhiteSpace(ev.ItemKey))
            {
                if (ev.ItemStatus == ProvisioningStatus.InProgress)
                {
                    Volatile.Write(ref activeComponentKey.Value, ev.ItemKey);
                }

                UpdateComponent(
                    ev.ItemKey,
                    ev.ItemStatus ?? ProvisioningStatus.InProgress,
                    ev.Message);
            }
        });

        try
        {
            var cancellationToken = _state.LifetimeToken;
            _dispatcher.Post(() => UpdateComponent(
                DeploymentComponentKeys.NightCodeForgeWeb,
                ProvisioningStatus.InProgress,
                "Resolving NightCodeForge release..."));

            var ncfAsset = await _releaseManager.ResolveLatestNightCodeForgeReleaseAsync(source, cancellationToken)
                .ConfigureAwait(false);
            var ncfDeployment = await _releaseManager.DeployNightCodeForgeAsync(
                ncfAsset,
                _state.InstallDirectory,
                CreateDesktopShortcut,
                CreateBandProgress(progress, 5.0, 45.0, DeploymentComponentKeys.NightCodeForgeWeb),
                cancellationToken).ConfigureAwait(false);

            _state.NightCodeForgeDeployment = ncfDeployment;
            _state.InstallDirectory = ncfDeployment.InstallDirectory;
            _state.AppliedStartMenuShortcut = _state.CreateStartMenuShortcut;
            _state.AppliedDesktopShortcut = CreateDesktopShortcut;
            _dispatcher.Post(() => UpdateComponent(
                DeploymentComponentKeys.NightCodeForgeWeb,
                ProvisioningStatus.Completed,
                $"v{ncfDeployment.Version} deployed to '{ncfDeployment.InstallDirectory}'.",
                ncfDeployment.WasUpgrade ? "Upgraded" : "Installed"));

            if (_state.InstallOllamaRouter)
            {
                Volatile.Write(ref activeComponentKey.Value, DeploymentComponentKeys.OllamaRouter);
                _dispatcher.Post(() => UpdateComponent(
                    DeploymentComponentKeys.OllamaRouter,
                    ProvisioningStatus.InProgress,
                    "Resolving OllamaRouter release..."));

                var routerAsset = await _releaseManager.ResolveLatestOllamaRouterReleaseAsync(source, cancellationToken)
                    .ConfigureAwait(false);
                var routerDeployment = await _releaseManager.DeployOllamaRouterAsync(
                    routerAsset,
                    targetInstallDirectory: null,
                    CreateBandProgress(progress, 45.0, 72.0, DeploymentComponentKeys.OllamaRouter),
                    cancellationToken).ConfigureAwait(false);

                _state.OllamaRouterDeployment = routerDeployment;
                _dispatcher.Post(() => UpdateComponent(
                    DeploymentComponentKeys.OllamaRouter,
                    ProvisioningStatus.Completed,
                    $"v{routerDeployment.Version} deployed to '{routerDeployment.InstallDirectory}'.",
                    routerDeployment.WasUpgrade ? "Upgraded" : "Installed"));
            }

            if (_state.InstallCompanionTools)
            {
                Volatile.Write(
                    ref activeComponentKey.Value,
                    CompanionToolCatalog.All.Count > 0
                        ? DeploymentComponentKeys.ForCompanionTool(CompanionToolCatalog.All[0].PackageId)
                        : null);

                var wslDistro = await ResolveWslDistroAsync(cancellationToken).ConfigureAwait(false);
                var outcomes = await _nuGetProvisioner.ProvisionAllAsync(
                    source,
                    wslDistro,
                    CreateBandProgress(progress, 72.0, 98.0, null),
                    cancellationToken).ConfigureAwait(false);

                _state.CompanionOutcomes = outcomes;
                _dispatcher.Post(() => ReconcileCompanionToolOutcomes(outcomes));
            }

            _dispatcher.Post(() =>
            {
                IsCompleted = true;
                ProgressPercent = 100.0;
                StatusMessage = "Software, companion tools, and OS integration installed successfully.";
            });
        }
        catch (OperationCanceledException)
        {
            _dispatcher.Post(() => StatusMessage = "Software installation cancelled.");
        }
        catch (Exception ex)
        {
            var fallbackKey = Volatile.Read(ref activeComponentKey.Value);
            _dispatcher.Post(() =>
            {
                var markedAny = false;
                foreach (var item in Components)
                {
                    if (item.Status == ProvisioningStatus.InProgress)
                    {
                        item.Update(ProvisioningStatus.Failed, ex.Message);
                        markedAny = true;
                    }
                }

                if (!markedAny && !string.IsNullOrWhiteSpace(fallbackKey))
                {
                    UpdateComponent(fallbackKey, ProvisioningStatus.Failed, ex.Message);
                }

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

    private void PopulatePlannedComponents()
    {
        Components.Clear();

        Components.Add(new WizardChecklistItemViewModel(
            DeploymentComponentKeys.NightCodeForgeWeb,
            "NightCodeForge Web & Launcher",
            ProvisioningStatus.NotStarted,
            "Pending deployment..."));

        Components.Add(new WizardChecklistItemViewModel(
            DeploymentComponentKeys.OllamaRouter,
            "OllamaRouter (Server, CLI & App)",
            _state.InstallOllamaRouter ? ProvisioningStatus.NotStarted : ProvisioningStatus.Skipped,
            _state.InstallOllamaRouter ? "Pending deployment..." : "Skipped by Step 1 options."));

        foreach (var spec in CompanionToolCatalog.All)
        {
            Components.Add(new WizardChecklistItemViewModel(
                DeploymentComponentKeys.ForCompanionTool(spec.PackageId),
                $"{spec.ToolCommand} ({spec.PackageId})",
                _state.InstallCompanionTools ? ProvisioningStatus.NotStarted : ProvisioningStatus.Skipped,
                _state.InstallCompanionTools ? "Pending installation..." : "Skipped by Step 1 options."));
        }
    }

    private void UpdateComponent(
        string key,
        ProvisioningStatus status,
        string detail,
        string? statusLabelOverride = null)
    {
        var item = Components.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));
        if (item is not null)
        {
            item.Update(status, detail, statusLabelOverride);
        }
    }

    private void ReconcileCompanionToolOutcomes(IReadOnlyList<CompanionToolInstallOutcome> outcomes)
    {
        foreach (var spec in CompanionToolCatalog.All)
        {
            var key = DeploymentComponentKeys.ForCompanionTool(spec.PackageId);
            var matching = outcomes
                .Where(o => string.Equals(o.PackageId, spec.PackageId, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matching.Count > 0)
            {
                var status = matching.Any(o => o.Status == ProvisioningStatus.Failed)
                    ? ProvisioningStatus.Failed
                    : matching.Any(o => o.Status == ProvisioningStatus.Completed)
                        ? ProvisioningStatus.Completed
                        : ProvisioningStatus.Skipped;

                var environments = string.Join(", ", matching.Select(o => o.TargetEnvironment));
                var detail = status == ProvisioningStatus.Failed
                    ? string.Join(" | ", matching.Where(o => o.Status == ProvisioningStatus.Failed).Select(o => o.Message))
                    : $"{spec.ToolCommand} ready ({environments}).";
                UpdateComponent(key, status, detail);
            }
            else
            {
                var existing = Components.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));
                if (existing is not null && existing.Status is ProvisioningStatus.NotStarted or ProvisioningStatus.InProgress)
                {
                    existing.Update(ProvisioningStatus.Completed, $"{spec.ToolCommand} provisioned.");
                }
            }
        }
    }

    private async Task<string?> ResolveWslDistroAsync(CancellationToken cancellationToken)
    {
        if (_platform.Platform != HostPlatformType.Windows)
        {
            return null;
        }

        var probe = _state.LatestProbe
                    ?? await _environmentProvisioner.ProbeAsync(_state.WslDistroName, cancellationToken).ConfigureAwait(false);
        _state.LatestProbe = probe;

        return probe.WslInstalled && probe.WslDistroInstalled
            ? probe.ResolvedWslDistroName ?? _state.WslDistroName
            : null;
    }

    private static IProgress<InstallerProgressEvent> CreateBandProgress(
        IProgress<InstallerProgressEvent> outer,
        double startPercent,
        double endPercent,
        string? defaultItemKey) =>
        new BandProgress(outer, startPercent, endPercent, defaultItemKey);

    private sealed class BandProgress(
        IProgress<InstallerProgressEvent> inner,
        double start,
        double end,
        string? defaultItemKey)
        : IProgress<InstallerProgressEvent>
    {
        public void Report(InstallerProgressEvent value)
        {
            var mapped = value.ProgressPercent.HasValue
                ? start + (Math.Clamp(value.ProgressPercent.Value, 0.0, 100.0) / 100.0 * (end - start))
                : (double?)null;

            var itemKey = value.ItemKey ?? defaultItemKey;
            var itemStatus = value.ItemStatus ?? (itemKey is not null ? ProvisioningStatus.InProgress : (ProvisioningStatus?)null);

            inner.Report(value with
            {
                ProgressPercent = mapped,
                ItemKey = itemKey,
                ItemStatus = itemStatus
            });
        }
    }
}
