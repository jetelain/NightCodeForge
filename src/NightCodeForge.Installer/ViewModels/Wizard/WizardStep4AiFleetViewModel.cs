namespace NightCodeForge.Installer.ViewModels.Wizard;

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NightCodeForge.Installer.Core.Constants;
using NightCodeForge.Installer.Core.Exceptions;
using NightCodeForge.Installer.Core.Models;
using NightCodeForge.Installer.Core.Services;

/// <summary>
/// Step 4 ViewModel: Delegated OllamaRouter 5-Step Setup Wizard (<c>ollamarouter setup --wizard --wait</c>)
/// and <c>/api/router/recommendations</c> synchronization into <c>config/config.json</c>.
/// </summary>
public sealed partial class WizardStep4AiFleetViewModel : ObservableObject
{
    private readonly SetupWizardState _state;
    private readonly IOllamaRouterSetupHandoffService _setupHandoffService;
    private readonly IOllamaRouterRecommendationSyncService _recommendationSyncService;
    private readonly IUiDispatcher _dispatcher;

    public WizardStep4AiFleetViewModel(
        SetupWizardState state,
        IOllamaRouterSetupHandoffService setupHandoffService,
        IOllamaRouterRecommendationSyncService recommendationSyncService,
        IUiDispatcher dispatcher)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _setupHandoffService = setupHandoffService ?? throw new ArgumentNullException(nameof(setupHandoffService));
        _recommendationSyncService = recommendationSyncService ?? throw new ArgumentNullException(nameof(recommendationSyncService));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

        RoleAssignments = [];
        LogLines = [];

        ArchitectModelDisplay = "(not synced yet)";
        CoderModelDisplay = "(not synced yet)";
        DefaultModelDisplay = "(not synced yet)";
        ContextWindowDisplay = "-";
        ConfigPathDisplay = "-";
        PrimaryButtonLabel = "Launch OllamaRouter Setup Wizard";
        StatusMessage = "Delegate local Ollama, fleet benchmarking, and role assignment to OllamaRouter's Setup Wizard.";
        ErrorMessage = string.Empty;
    }

    public event EventHandler? SkipRequested;

    public ObservableCollection<RouterRoleAssignmentSummary> RoleAssignments { get; }

    public ObservableCollection<string> LogLines { get; }

    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial bool HasSyncedRecommendations { get; set; }

    [ObservableProperty]
    public partial bool IsSkipped { get; set; }

    [ObservableProperty]
    public partial string StatusMessage { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string ErrorMessage { get; set; }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    [ObservableProperty]
    public partial string ArchitectModelDisplay { get; set; }

    [ObservableProperty]
    public partial string CoderModelDisplay { get; set; }

    [ObservableProperty]
    public partial string DefaultModelDisplay { get; set; }

    [ObservableProperty]
    public partial string ContextWindowDisplay { get; set; }

    [ObservableProperty]
    public partial string ConfigPathDisplay { get; set; }

    [ObservableProperty]
    public partial string PrimaryButtonLabel { get; set; }

    public void ResetState()
    {
        IsRunning = false;
        HasSyncedRecommendations = false;
        IsSkipped = false;
        ErrorMessage = string.Empty;
        ArchitectModelDisplay = "(not synced yet)";
        CoderModelDisplay = "(not synced yet)";
        DefaultModelDisplay = "(not synced yet)";
        ContextWindowDisplay = "-";
        ConfigPathDisplay = "-";
        PrimaryButtonLabel = "Launch OllamaRouter Setup Wizard";
        StatusMessage = "Delegate local Ollama, fleet benchmarking, and role assignment to OllamaRouter's Setup Wizard.";
        RoleAssignments.Clear();
        LogLines.Clear();
    }

    partial void OnIsRunningChanged(bool value) => SkipAiSetupCommand.NotifyCanExecuteChanged();

    private bool CanSkipAiSetup() => !IsRunning;

    [RelayCommand]
    public async Task LaunchOllamaRouterWizardAsync()
    {
        if (IsRunning)
        {
            return;
        }

        _dispatcher.Post(() =>
        {
            IsRunning = true;
            IsSkipped = false;
            _state.AiFleetSkipped = false;
            ErrorMessage = string.Empty;
            StatusMessage = "Running OllamaRouter Setup Wizard (waiting for completion)...";
            LogLines.Add("[OllamaRouter] Launching 'ollamarouter setup --wizard --wait'...");
        });

        var progress = new UiDispatchedProgress<InstallerProgressEvent>(_dispatcher, ev =>
        {
            LogLines.Add($"[{ev.Step}] {ev.Message}");
            StatusMessage = ev.Message;
        });

        try
        {
            var result = await _setupHandoffService.RunSetupWizardAndSyncAsync(
                _state.InstallDirectory,
                InstallerConstants.DefaultOllamaRouterEndpoint,
                progress,
                _state.LifetimeToken).ConfigureAwait(false);

            _dispatcher.Post(() =>
            {
                PrimaryButtonLabel = "Re-run OllamaRouter Wizard";

                if (result.Succeeded && result.SyncedRecommendations is not null)
                {
                    ApplyRecommendations(result.SyncedRecommendations);
                    StatusMessage = "OllamaRouter Setup Wizard completed and NightCodeForge role recommendations synced.";
                }
                else
                {
                    ErrorMessage = result.ErrorMessage ?? $"OllamaRouter wizard exited with code {result.ExitCode}.";
                    StatusMessage = ErrorMessage;
                }
            });
        }
        catch (OperationCanceledException)
        {
            _dispatcher.Post(() => StatusMessage = "OllamaRouter wizard launch cancelled.");
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() =>
            {
                ErrorMessage = ex.Message;
                StatusMessage = ex.Message;
                LogLines.Add($"[Error] {ex.Message}");
            });
        }
        finally
        {
            _dispatcher.Post(() => IsRunning = false);
        }
    }

    [RelayCommand]
    public async Task SyncRecommendationsOnlyAsync()
    {
        if (IsRunning)
        {
            return;
        }

        _dispatcher.Post(() =>
        {
            IsRunning = true;
            IsSkipped = false;
            _state.AiFleetSkipped = false;
            ErrorMessage = string.Empty;
            StatusMessage = "Querying OllamaRouter /api/router/recommendations...";
        });

        var progress = new UiDispatchedProgress<InstallerProgressEvent>(_dispatcher, ev =>
        {
            LogLines.Add($"[{ev.Step}] {ev.Message}");
            StatusMessage = ev.Message;
        });

        try
        {
            var summary = await _recommendationSyncService.SyncRecommendationsToConfigAsync(
                _state.InstallDirectory,
                InstallerConstants.DefaultOllamaRouterEndpoint,
                progress,
                _state.LifetimeToken).ConfigureAwait(false);

            _dispatcher.Post(() =>
            {
                ApplyRecommendations(summary);
                PrimaryButtonLabel = "Re-run OllamaRouter Wizard";
                StatusMessage = "Synced benchmark-calibrated role models into NightCodeForge config.json.";
            });
        }
        catch (OperationCanceledException)
        {
            _dispatcher.Post(() => StatusMessage = "Role recommendation sync cancelled.");
        }
        catch (OllamaRouterSyncException ex)
        {
            _dispatcher.Post(() =>
            {
                ErrorMessage = ex.Message;
                StatusMessage = ex.Message;
                LogLines.Add($"[Error] {ex.Message}");
            });
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() =>
            {
                ErrorMessage = ex.Message;
                StatusMessage = $"Recommendation sync error: {ex.Message}";
                LogLines.Add($"[Error] {ex.Message}");
            });
        }
        finally
        {
            _dispatcher.Post(() => IsRunning = false);
        }
    }

    [RelayCommand(CanExecute = nameof(CanSkipAiSetup))]
    public void SkipAiSetup()
    {
        if (IsRunning)
        {
            return;
        }

        IsSkipped = true;
        _state.AiFleetSkipped = true;
        ErrorMessage = string.Empty;
        StatusMessage = "Skipped local AI fleet setup. Configure Cloud, ACP, or OllamaRouter later in Web UI.";
        SkipRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyRecommendations(RouterRoleRecommendationsSummary summary)
    {
        _state.SyncedRecommendations = summary;
        HasSyncedRecommendations = true;
        ErrorMessage = string.Empty;

        ArchitectModelDisplay = summary.ArchitectModelId ?? "(inherit)";
        CoderModelDisplay = summary.CoderModelId ?? "(inherit)";
        DefaultModelDisplay = summary.DefaultModelId ?? "(auto)";
        ContextWindowDisplay = string.Format(CultureInfo.InvariantCulture, "{0:N0} tokens", summary.ContextWindowTokens);
        ConfigPathDisplay = summary.ConfigFilePath;

        RoleAssignments.Clear();
        foreach (var role in summary.Roles)
        {
            RoleAssignments.Add(role);
        }
    }
}
