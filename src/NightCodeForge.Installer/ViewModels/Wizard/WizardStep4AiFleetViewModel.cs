namespace NightCodeForge.Installer.ViewModels.Wizard;

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NightCodeForge.Installer.Core.Abstractions;
using NightCodeForge.Installer.Core.Constants;
using NightCodeForge.Installer.Core.Exceptions;
using NightCodeForge.Installer.Core.Models;
using NightCodeForge.Installer.Core.Services;

/// <summary>
/// Step 4 ViewModel: Supports choosing between OllamaRouter (delegated setup wizard + recommendation sync)
/// and an OpenAI-compatible provider (preset/custom endpoint, OS keyring API key, model discovery, and role selection).
/// </summary>
public sealed partial class WizardStep4AiFleetViewModel : ObservableObject
{
    private readonly SetupWizardState _state;
    private readonly IOllamaRouterSetupHandoffService _setupHandoffService;
    private readonly IOllamaRouterRecommendationSyncService _recommendationSyncService;
    private readonly IOpenAiProviderConfigurationService _openAiProviderService;
    private readonly IInstallerSecretStore _secretStore;
    private readonly IUiDispatcher _dispatcher;

    private readonly List<OpenAiDiscoveredModel> _allDiscoveredModels = [];
    private ExistingOpenAiProviderConfig? _loadedExistingOpenAiConfig;
    private bool _synchronizingMode;
    private bool _synchronizingProvider;
    private bool _synchronizingModels;
    private bool _suppressOpenAiTracking;
    private bool _initializedOnEntry;

    public WizardStep4AiFleetViewModel(
        SetupWizardState state,
        IOllamaRouterSetupHandoffService setupHandoffService,
        IOllamaRouterRecommendationSyncService recommendationSyncService,
        IOpenAiProviderConfigurationService openAiProviderService,
        IInstallerSecretStore secretStore,
        IUiDispatcher dispatcher)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _setupHandoffService = setupHandoffService ?? throw new ArgumentNullException(nameof(setupHandoffService));
        _recommendationSyncService = recommendationSyncService ?? throw new ArgumentNullException(nameof(recommendationSyncService));
        _openAiProviderService = openAiProviderService ?? throw new ArgumentNullException(nameof(openAiProviderService));
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

        RoleAssignments = [];
        LogLines = [];
        FilteredDiscoveredModels = [];

        _synchronizingMode = true;
        IsOllamaRouterMode = _state.SelectedAiSetupMode == AiSetupMode.OllamaRouter;
        IsOpenAiCompatibleMode = _state.SelectedAiSetupMode == AiSetupMode.OpenAiCompatible;
        _synchronizingMode = false;

        _suppressOpenAiTracking = true;
        _synchronizingProvider = true;
        SelectedOpenAiProvider = OpenAiProviderPresetCatalog.OpenAi;
        OpenAiEndpointUrl = OpenAiProviderPresetCatalog.OpenAi.DefaultEndpoint;
        OpenAiApiKeyInput = string.Empty;
        OpenAiOrganizationId = string.Empty;
        OpenAiModelFilter = string.Empty;
        OpenAiDefaultModelId = string.Empty;
        OpenAiArchitectModelId = string.Empty;
        OpenAiCoderModelId = string.Empty;
        _synchronizingProvider = false;
        _suppressOpenAiTracking = false;

        ArchitectModelDisplay = "(not synced yet)";
        CoderModelDisplay = "(not synced yet)";
        DefaultModelDisplay = "(not synced yet)";
        ContextWindowDisplay = "-";
        ConfigPathDisplay = "-";
        PrimaryButtonLabel = "Launch OllamaRouter Setup Wizard";
        StatusMessage = "Choose OllamaRouter for local/fleet inference or configure an OpenAI-compatible API provider.";
        ErrorMessage = string.Empty;
        OpenAiDiscoveryStatusText = string.Empty;
        OpenAiSavedSummaryText = string.Empty;
        OpenAiCredentialStatusText = string.Empty;
        OpenAiApiKeyPlaceholder = "Enter API key (stored securely in OS keyring)";
        RefreshOpenAiCredentialStatus();
    }

    public event EventHandler? SkipRequested;

    public IReadOnlyList<OpenAiProviderPreset> OpenAiProviders => OpenAiProviderPresetCatalog.All;

    public ObservableCollection<RouterRoleAssignmentSummary> RoleAssignments { get; }

    public ObservableCollection<string> LogLines { get; }

    public ObservableCollection<OpenAiDiscoveredModel> FilteredDiscoveredModels { get; }

    [ObservableProperty]
    public partial bool IsOllamaRouterMode { get; set; }

    [ObservableProperty]
    public partial bool IsOpenAiCompatibleMode { get; set; }

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOpenAiOrganizationId))]
    public partial OpenAiProviderPreset? SelectedOpenAiProvider { get; set; }

    public bool ShowOpenAiOrganizationId => SelectedOpenAiProvider?.SupportsOrganizationId ?? true;

    [ObservableProperty]
    public partial string OpenAiEndpointUrl { get; set; }

    [ObservableProperty]
    public partial string OpenAiApiKeyInput { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowApiKeyButtonLabel))]
    public partial bool ShowApiKey { get; set; }

    public string ShowApiKeyButtonLabel => ShowApiKey ? "Hide" : "Show";

    [ObservableProperty]
    public partial string OpenAiOrganizationId { get; set; }

    [ObservableProperty]
    public partial string OpenAiCredentialStatusText { get; set; }

    [ObservableProperty]
    public partial string OpenAiApiKeyPlaceholder { get; set; }

    [ObservableProperty]
    public partial bool HasStoredOpenAiApiKey { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOpenAiDiscoveryStatus))]
    public partial string OpenAiDiscoveryStatusText { get; set; }

    public bool HasOpenAiDiscoveryStatus => !string.IsNullOrWhiteSpace(OpenAiDiscoveryStatusText);

    [ObservableProperty]
    public partial bool IsOpenAiDiscoverySuccess { get; set; }

    [ObservableProperty]
    public partial bool IsOpenAiDiscoveryUnsupported { get; set; }

    [ObservableProperty]
    public partial string OpenAiModelFilter { get; set; }

    public bool HasDiscoveredOpenAiModels => _allDiscoveredModels.Count > 0;

    public string DiscoveredModelCountDisplay =>
        _allDiscoveredModels.Count == 0
            ? string.Empty
            : $"{FilteredDiscoveredModels.Count} / {_allDiscoveredModels.Count} models shown";

    [ObservableProperty]
    public partial OpenAiDiscoveredModel? SelectedDefaultDiscoveredModel { get; set; }

    [ObservableProperty]
    public partial string OpenAiDefaultModelId { get; set; }

    [ObservableProperty]
    public partial bool DifferentiateOpenAiRoles { get; set; }

    [ObservableProperty]
    public partial OpenAiDiscoveredModel? SelectedArchitectDiscoveredModel { get; set; }

    [ObservableProperty]
    public partial string OpenAiArchitectModelId { get; set; }

    [ObservableProperty]
    public partial OpenAiDiscoveredModel? SelectedCoderDiscoveredModel { get; set; }

    [ObservableProperty]
    public partial string OpenAiCoderModelId { get; set; }

    [ObservableProperty]
    public partial bool HasSavedOpenAiConfiguration { get; set; }

    [ObservableProperty]
    public partial string OpenAiSavedSummaryText { get; set; }

    [ObservableProperty]
    public partial bool HasUnsavedOpenAiChanges { get; set; }

    public void ResetState()
    {
        _initializedOnEntry = false;
        _loadedExistingOpenAiConfig = null;
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
        StatusMessage = "Choose OllamaRouter for local/fleet inference or configure an OpenAI-compatible API provider.";
        RoleAssignments.Clear();
        LogLines.Clear();

        _synchronizingMode = true;
        _state.SelectedAiSetupMode = AiSetupMode.OllamaRouter;
        IsOllamaRouterMode = true;
        IsOpenAiCompatibleMode = false;
        _synchronizingMode = false;

        _suppressOpenAiTracking = true;
        _synchronizingProvider = true;
        _synchronizingModels = true;
        try
        {
            SelectedOpenAiProvider = OpenAiProviderPresetCatalog.OpenAi;
            OpenAiEndpointUrl = OpenAiProviderPresetCatalog.OpenAi.DefaultEndpoint;
            OpenAiApiKeyInput = string.Empty;
            ShowApiKey = false;
            OpenAiOrganizationId = string.Empty;
            OpenAiModelFilter = string.Empty;
            OpenAiDefaultModelId = string.Empty;
            OpenAiArchitectModelId = string.Empty;
            OpenAiCoderModelId = string.Empty;
            SelectedDefaultDiscoveredModel = null;
            SelectedArchitectDiscoveredModel = null;
            SelectedCoderDiscoveredModel = null;
            DifferentiateOpenAiRoles = false;
            _allDiscoveredModels.Clear();
            FilteredDiscoveredModels.Clear();
            OpenAiDiscoveryStatusText = string.Empty;
            IsOpenAiDiscoverySuccess = false;
            IsOpenAiDiscoveryUnsupported = false;
            HasSavedOpenAiConfiguration = false;
            OpenAiSavedSummaryText = string.Empty;
            HasUnsavedOpenAiChanges = false;
        }
        finally
        {
            _synchronizingModels = false;
            _synchronizingProvider = false;
            _suppressOpenAiTracking = false;
        }

        OnPropertyChanged(nameof(HasDiscoveredOpenAiModels));
        OnPropertyChanged(nameof(DiscoveredModelCountDisplay));
        RefreshOpenAiCredentialStatus();
    }

    public void OnStepEntered()
    {
        if (_initializedOnEntry)
        {
            RefreshOpenAiCredentialStatus();
            return;
        }

        _initializedOnEntry = true;
        var existing = _openAiProviderService.TryReadExistingConfiguration(_state.InstallDirectory);
        if (existing is not null)
        {
            ApplyExistingOpenAiConfiguration(existing);
            return;
        }

        if (!_state.InstallOllamaRouter &&
            !_state.Discovery.OllamaRouter.IsInstalled &&
            _state.OllamaRouterDeployment is null)
        {
            IsOpenAiCompatibleMode = true;
        }
        else
        {
            IsOllamaRouterMode = true;
        }

        RefreshOpenAiCredentialStatus();
    }

    partial void OnIsRunningChanged(bool value) => SkipAiSetupCommand.NotifyCanExecuteChanged();

    partial void OnIsOllamaRouterModeChanged(bool value)
    {
        if (_synchronizingMode)
        {
            return;
        }

        _synchronizingMode = true;
        try
        {
            if (value)
            {
                IsOpenAiCompatibleMode = false;
                _state.SelectedAiSetupMode = AiSetupMode.OllamaRouter;
                ErrorMessage = string.Empty;
                StatusMessage = "Delegate local Ollama, fleet benchmarking, and role assignment to OllamaRouter's Setup Wizard.";
            }
            else if (!IsOpenAiCompatibleMode)
            {
                IsOpenAiCompatibleMode = true;
                _state.SelectedAiSetupMode = AiSetupMode.OpenAiCompatible;
            }
        }
        finally
        {
            _synchronizingMode = false;
        }
    }

    partial void OnIsOpenAiCompatibleModeChanged(bool value)
    {
        if (_synchronizingMode)
        {
            return;
        }

        _synchronizingMode = true;
        try
        {
            if (value)
            {
                IsOllamaRouterMode = false;
                _state.SelectedAiSetupMode = AiSetupMode.OpenAiCompatible;
                ErrorMessage = string.Empty;
                StatusMessage = "Select an OpenAI-compatible provider, provide credentials if required, and choose your models.";
                RefreshOpenAiCredentialStatus();
            }
            else if (!IsOllamaRouterMode)
            {
                IsOllamaRouterMode = true;
                _state.SelectedAiSetupMode = AiSetupMode.OllamaRouter;
            }
        }
        finally
        {
            _synchronizingMode = false;
        }
    }

    partial void OnSelectedOpenAiProviderChanged(OpenAiProviderPreset? value)
    {
        if (_synchronizingProvider || value is null)
        {
            return;
        }

        _synchronizingProvider = true;
        _suppressOpenAiTracking = true;
        try
        {
            if (!string.IsNullOrWhiteSpace(value.DefaultEndpoint))
            {
                OpenAiEndpointUrl = value.DefaultEndpoint;
            }

            OpenAiApiKeyInput = string.Empty;
            ShowApiKey = false;
            if (!value.SupportsOrganizationId)
            {
                OpenAiOrganizationId = string.Empty;
            }
        }
        finally
        {
            _suppressOpenAiTracking = false;
            _synchronizingProvider = false;
        }

        ClearDiscoveredModels();
        RefreshOpenAiCredentialStatus();
        MarkOpenAiInputChanged();
    }

    partial void OnOpenAiEndpointUrlChanged(string value)
    {
        if (!_synchronizingProvider && !_suppressOpenAiTracking)
        {
            ClearDiscoveredModels();

            var matchedPreset = OpenAiProviderPresetCatalog.FindByEndpoint(value);
            if (!Equals(matchedPreset, SelectedOpenAiProvider))
            {
                _synchronizingProvider = true;
                try
                {
                    SelectedOpenAiProvider = matchedPreset;
                }
                finally
                {
                    _synchronizingProvider = false;
                }
            }
        }

        if (!_synchronizingProvider)
        {
            RefreshOpenAiCredentialStatus();
        }

        MarkOpenAiInputChanged();
    }

    partial void OnOpenAiApiKeyInputChanged(string value) =>
        MarkOpenAiInputChanged(markUnsaved: !string.IsNullOrEmpty(value));

    partial void OnOpenAiOrganizationIdChanged(string value) =>
        MarkOpenAiInputChanged();

    partial void OnOpenAiModelFilterChanged(string value) => ApplyModelFilter();

    partial void OnSelectedDefaultDiscoveredModelChanged(OpenAiDiscoveredModel? value)
    {
        if (_synchronizingModels || value is null)
        {
            return;
        }

        _synchronizingModels = true;
        try
        {
            OpenAiDefaultModelId = value.ModelId;
        }
        finally
        {
            _synchronizingModels = false;
        }

        MarkOpenAiInputChanged();
    }

    partial void OnOpenAiDefaultModelIdChanged(string value)
    {
        MarkOpenAiInputChanged();

        if (_synchronizingModels)
        {
            return;
        }

        _synchronizingModels = true;
        try
        {
            SelectedDefaultDiscoveredModel = FindMatchingModel(value);
        }
        finally
        {
            _synchronizingModels = false;
        }
    }

    partial void OnDifferentiateOpenAiRolesChanged(bool value) =>
        MarkOpenAiInputChanged();

    partial void OnSelectedArchitectDiscoveredModelChanged(OpenAiDiscoveredModel? value)
    {
        if (_synchronizingModels || value is null)
        {
            return;
        }

        _synchronizingModels = true;
        try
        {
            OpenAiArchitectModelId = value.ModelId;
        }
        finally
        {
            _synchronizingModels = false;
        }

        MarkOpenAiInputChanged();
    }

    partial void OnOpenAiArchitectModelIdChanged(string value)
    {
        MarkOpenAiInputChanged();

        if (_synchronizingModels)
        {
            return;
        }

        _synchronizingModels = true;
        try
        {
            SelectedArchitectDiscoveredModel = FindMatchingModel(value);
        }
        finally
        {
            _synchronizingModels = false;
        }
    }

    partial void OnSelectedCoderDiscoveredModelChanged(OpenAiDiscoveredModel? value)
    {
        if (_synchronizingModels || value is null)
        {
            return;
        }

        _synchronizingModels = true;
        try
        {
            OpenAiCoderModelId = value.ModelId;
        }
        finally
        {
            _synchronizingModels = false;
        }

        MarkOpenAiInputChanged();
    }

    partial void OnOpenAiCoderModelIdChanged(string value)
    {
        MarkOpenAiInputChanged();

        if (_synchronizingModels)
        {
            return;
        }

        _synchronizingModels = true;
        try
        {
            SelectedCoderDiscoveredModel = FindMatchingModel(value);
        }
        finally
        {
            _synchronizingModels = false;
        }
    }

    private bool CanSkipAiSetup() => !IsRunning;

    [RelayCommand]
    private void ToggleShowApiKey() => ShowApiKey = !ShowApiKey;

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

    [RelayCommand]
    public async Task TestAndDiscoverOpenAiModelsAsync()
    {
        if (IsRunning)
        {
            return;
        }

        var endpoint = OpenAiEndpointUrl;
        var apiKeyOverride = string.IsNullOrWhiteSpace(OpenAiApiKeyInput) ? null : OpenAiApiKeyInput;
        var orgId = ShowOpenAiOrganizationId && !string.IsNullOrWhiteSpace(OpenAiOrganizationId) ? OpenAiOrganizationId : null;

        _dispatcher.Post(() =>
        {
            IsRunning = true;
            ErrorMessage = string.Empty;
            StatusMessage = $"Connecting to {endpoint} and querying available models...";
            OpenAiDiscoveryStatusText = "Querying /models endpoint...";
            IsOpenAiDiscoverySuccess = false;
            IsOpenAiDiscoveryUnsupported = false;
        });

        try
        {
            var result = await _openAiProviderService.DiscoverModelsAsync(
                endpoint,
                apiKeyOverride,
                orgId,
                _state.LifetimeToken).ConfigureAwait(false);

            _dispatcher.Post(() =>
            {
                _allDiscoveredModels.Clear();
                _allDiscoveredModels.AddRange(result.Models);
                if (!string.IsNullOrEmpty(OpenAiModelFilter))
                {
                    OpenAiModelFilter = string.Empty;
                }
                else
                {
                    ApplyModelFilter();
                }

                IsOpenAiDiscoverySuccess = result.IsSuccess;
                IsOpenAiDiscoveryUnsupported = result.IsSuccess && !result.IsDiscoveryImplemented;

                if (result.IsSuccess)
                {
                    OpenAiDiscoveryStatusText = result.Message ?? $"Discovered {result.Models.Count} model(s).";
                    StatusMessage = OpenAiDiscoveryStatusText;
                    if (string.IsNullOrWhiteSpace(OpenAiDefaultModelId) && result.Models.Count > 0)
                    {
                        _synchronizingModels = true;
                        try
                        {
                            SelectedDefaultDiscoveredModel = result.Models[0];
                            OpenAiDefaultModelId = result.Models[0].ModelId;
                        }
                        finally
                        {
                            _synchronizingModels = false;
                        }

                        HasUnsavedOpenAiChanges = true;
                    }
                }
                else
                {
                    OpenAiDiscoveryStatusText = result.ErrorMessage ?? "Connection test failed.";
                    ErrorMessage = OpenAiDiscoveryStatusText;
                    StatusMessage = OpenAiDiscoveryStatusText;
                }
            });
        }
        catch (OperationCanceledException)
        {
            _dispatcher.Post(() => StatusMessage = "Model discovery cancelled.");
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() =>
            {
                IsOpenAiDiscoverySuccess = false;
                OpenAiDiscoveryStatusText = ex.Message;
                ErrorMessage = ex.Message;
                StatusMessage = ex.Message;
            });
        }
        finally
        {
            _dispatcher.Post(() => IsRunning = false);
        }
    }

    [RelayCommand]
    public void ClearStoredOpenAiApiKey()
    {
        if (IsRunning)
        {
            return;
        }

        try
        {
            _secretStore.DeleteOpenAiApiKey(OpenAiEndpointUrl);
            OpenAiApiKeyInput = string.Empty;
            ShowApiKey = false;
            ErrorMessage = string.Empty;
            RefreshOpenAiCredentialStatus();
            StatusMessage = "Deleted stored API key from OS keyring for this endpoint.";
        }
        catch (Exception ex) when (ex is InstallerSecretStoreException or ArgumentException)
        {
            ErrorMessage = ex.Message;
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    public async Task<bool> SaveOpenAiConfigurationAsync()
    {
        if (IsRunning)
        {
            return false;
        }

        var providerName = SelectedOpenAiProvider?.Name ?? OpenAiProviderPresetCatalog.CustomPreset.Name;
        var endpoint = OpenAiEndpointUrl;
        var apiKeyInput = OpenAiApiKeyInput;
        var orgId = ShowOpenAiOrganizationId ? OpenAiOrganizationId : null;
        var defaultModelId = OpenAiDefaultModelId;
        var architectModelId = DifferentiateOpenAiRoles ? OpenAiArchitectModelId : null;
        var coderModelId = DifferentiateOpenAiRoles ? OpenAiCoderModelId : null;
        var contextWindowTokens = ResolveSelectedContextWindowTokens(endpoint, defaultModelId, architectModelId);
        var allowedModelsMode = ResolveAllowedModelsPersistenceMode(endpoint, defaultModelId, architectModelId, coderModelId);

        _dispatcher.Post(() =>
        {
            IsRunning = true;
            IsSkipped = false;
            _state.AiFleetSkipped = false;
            ErrorMessage = string.Empty;
            StatusMessage = "Saving OpenAI-compatible provider configuration...";
        });

        try
        {
            var summary = await _openAiProviderService.SaveConfigurationAsync(
                new OpenAiProviderSaveRequest(
                    InstallDirectory: _state.InstallDirectory,
                    ProviderName: providerName,
                    Endpoint: endpoint,
                    ApiKeyInput: apiKeyInput,
                    OrganizationId: orgId,
                    DefaultModelId: defaultModelId,
                    ArchitectModelId: architectModelId,
                    CoderModelId: coderModelId,
                    ContextWindowTokens: contextWindowTokens,
                    AllowedModelsMode: allowedModelsMode),
                _state.LifetimeToken).ConfigureAwait(false);

            _dispatcher.Post(() =>
            {
                _suppressOpenAiTracking = true;
                try
                {
                    OpenAiApiKeyInput = string.Empty;
                    ShowApiKey = false;
                    OpenAiEndpointUrl = summary.Endpoint;
                    OpenAiDefaultModelId = summary.DefaultModelId;
                    OpenAiArchitectModelId = summary.ArchitectModelId ?? string.Empty;
                    OpenAiCoderModelId = summary.CoderModelId ?? string.Empty;
                }
                finally
                {
                    _suppressOpenAiTracking = false;
                }

                var preservedManualAllowedModels = allowedModelsMode == OpenAiAllowedModelsPersistenceMode.WriteManualModels ||
                    (allowedModelsMode == OpenAiAllowedModelsPersistenceMode.PreserveExisting &&
                     _loadedExistingOpenAiConfig?.HasManualAllowedModels == true);

                _loadedExistingOpenAiConfig = new ExistingOpenAiProviderConfig(
                    Endpoint: summary.Endpoint,
                    OrganizationId: string.IsNullOrWhiteSpace(orgId) ? null : orgId.Trim(),
                    DefaultModelId: summary.DefaultModelId,
                    ArchitectModelId: summary.ArchitectModelId,
                    CoderModelId: summary.CoderModelId,
                    ContextWindowTokens: summary.ContextWindowTokens,
                    HasManualAllowedModels: preservedManualAllowedModels,
                    ConfigFilePath: summary.ConfigFilePath);

                _state.ConfiguredOpenAiProvider = summary;
                _state.SyncedRecommendations = null;
                _state.AiFleetSkipped = false;
                HasSyncedRecommendations = false;
                HasSavedOpenAiConfiguration = true;
                HasUnsavedOpenAiChanges = false;
                ErrorMessage = string.Empty;
                RefreshOpenAiCredentialStatus();

                OpenAiSavedSummaryText = FormatOpenAiSavedSummary(summary);
                StatusMessage = $"Saved {summary.ProviderName} configuration to '{summary.ConfigFilePath}'.";
            });

            return true;
        }
        catch (OperationCanceledException)
        {
            _dispatcher.Post(() => StatusMessage = "Saving OpenAI provider configuration cancelled.");
            return false;
        }
        catch (Exception ex) when (ex is OpenAiConfigurationException or InstallerSecretStoreException or ArgumentException)
        {
            _dispatcher.Post(() =>
            {
                ErrorMessage = ex.Message;
                StatusMessage = ex.Message;
            });
            return false;
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
        HasUnsavedOpenAiChanges = false;
        ErrorMessage = string.Empty;
        StatusMessage = "Skipped AI provider setup. Configure Cloud, ACP, or OllamaRouter later in Web UI.";
        SkipRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyExistingOpenAiConfiguration(ExistingOpenAiProviderConfig existing)
    {
        _loadedExistingOpenAiConfig = existing;
        _synchronizingMode = true;
        _suppressOpenAiTracking = true;
        _synchronizingProvider = true;
        _synchronizingModels = true;
        try
        {
            _state.SelectedAiSetupMode = AiSetupMode.OpenAiCompatible;
            IsOpenAiCompatibleMode = true;
            IsOllamaRouterMode = false;

            SelectedOpenAiProvider = OpenAiProviderPresetCatalog.FindByEndpoint(existing.Endpoint);
            OpenAiEndpointUrl = existing.Endpoint;
            OpenAiOrganizationId = existing.OrganizationId ?? string.Empty;
            OpenAiDefaultModelId = existing.DefaultModelId ?? string.Empty;
            OpenAiArchitectModelId = existing.ArchitectModelId ?? string.Empty;
            OpenAiCoderModelId = existing.CoderModelId ?? string.Empty;
            DifferentiateOpenAiRoles = !string.IsNullOrWhiteSpace(existing.ArchitectModelId) ||
                                       !string.IsNullOrWhiteSpace(existing.CoderModelId);
            HasUnsavedOpenAiChanges = false;
            StatusMessage = "Loaded existing OpenAI-compatible provider settings from config/config.json.";
        }
        finally
        {
            _synchronizingModels = false;
            _synchronizingProvider = false;
            _suppressOpenAiTracking = false;
            _synchronizingMode = false;
        }

        RefreshOpenAiCredentialStatus();

        if (!string.IsNullOrWhiteSpace(existing.DefaultModelId))
        {
            var effectiveEndpoint = InstallerEndpointNormalizer.TryNormalizeEndpoint(existing.Endpoint, out var normalized)
                ? normalized
                : existing.Endpoint.Trim();
            var summary = new OpenAiProviderConfigurationSummary(
                ProviderName: SelectedOpenAiProvider?.Name ?? OpenAiProviderPresetCatalog.CustomPreset.Name,
                Endpoint: effectiveEndpoint,
                DefaultModelId: existing.DefaultModelId,
                ArchitectModelId: existing.ArchitectModelId,
                CoderModelId: existing.CoderModelId,
                ContextWindowTokens: existing.ContextWindowTokens ?? InstallerConstants.DefaultContextWindowTokens,
                ApiKeyStoredInKeyring: HasStoredOpenAiApiKey,
                ConfigFilePath: existing.ConfigFilePath);

            _state.ConfiguredOpenAiProvider = summary;
            _state.SyncedRecommendations = null;
            HasSavedOpenAiConfiguration = true;
            OpenAiSavedSummaryText = FormatOpenAiSavedSummary(summary);
        }
    }

    private void ApplyRecommendations(RouterRoleRecommendationsSummary summary)
    {
        _state.SyncedRecommendations = summary;
        _state.ConfiguredOpenAiProvider = null;
        HasSyncedRecommendations = true;
        HasSavedOpenAiConfiguration = false;
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

    private void RefreshOpenAiCredentialStatus()
    {
        if (!InstallerEndpointNormalizer.TryNormalizeEndpoint(OpenAiEndpointUrl, out _))
        {
            HasStoredOpenAiApiKey = false;
            OpenAiCredentialStatusText = "Enter a valid HTTP/HTTPS endpoint URL.";
            OpenAiApiKeyPlaceholder = "Enter API key";
            return;
        }

        try
        {
            var isKeyringAvailable = _secretStore.IsKeyringAvailable();
            var hasKeyring = isKeyringAvailable && _secretStore.HasOpenAiApiKey(OpenAiEndpointUrl);
            isKeyringAvailable = _secretStore.IsKeyringAvailable();
            var hasEnvFallback = !hasKeyring && _secretStore.HasEnvironmentOpenAiApiKey();

            HasStoredOpenAiApiKey = hasKeyring;
            if (hasKeyring)
            {
                OpenAiCredentialStatusText = "Stored in OS Keyring (leave empty to keep existing key, or enter a new key to replace).";
                OpenAiApiKeyPlaceholder = "Key stored in OS keyring — leave blank to preserve";
            }
            else if (hasEnvFallback)
            {
                OpenAiCredentialStatusText = $"Inherited from {InstallerConstants.EnvOpenAiApiKey} environment variable (or enter a key to store in OS Keyring).";
                OpenAiApiKeyPlaceholder = $"Inherited from {InstallerConstants.EnvOpenAiApiKey}";
            }
            else if (!isKeyringAvailable)
            {
                OpenAiCredentialStatusText = $"OS Keyring unavailable — set {InstallerConstants.EnvOpenAiApiKey} or use an unauthenticated local runtime.";
                OpenAiApiKeyPlaceholder = "OS Keyring unavailable";
            }
            else
            {
                OpenAiCredentialStatusText = "No API key stored in OS Keyring for this endpoint yet.";
                OpenAiApiKeyPlaceholder = SelectedOpenAiProvider?.RequiresApiKey == false
                    ? "Optional for local servers (leave blank if unauthenticated)"
                    : "sk-... (stored securely in OS keyring, never in config.json)";
            }
        }
        catch (InstallerSecretStoreException ex)
        {
            HasStoredOpenAiApiKey = false;
            OpenAiCredentialStatusText = ex.Message;
            OpenAiApiKeyPlaceholder = "OS Keyring access error";
        }
    }

    private void MarkOpenAiInputChanged(bool markUnsaved = true)
    {
        if (_suppressOpenAiTracking)
        {
            return;
        }

        ErrorMessage = string.Empty;
        if (markUnsaved)
        {
            HasUnsavedOpenAiChanges = true;
        }
    }

    private void ClearDiscoveredModels()
    {
        _allDiscoveredModels.Clear();
        _synchronizingModels = true;
        try
        {
            FilteredDiscoveredModels.Clear();
            SelectedDefaultDiscoveredModel = null;
            SelectedArchitectDiscoveredModel = null;
            SelectedCoderDiscoveredModel = null;
            OpenAiDiscoveryStatusText = string.Empty;
            IsOpenAiDiscoverySuccess = false;
            IsOpenAiDiscoveryUnsupported = false;
        }
        finally
        {
            _synchronizingModels = false;
        }

        OnPropertyChanged(nameof(HasDiscoveredOpenAiModels));
        OnPropertyChanged(nameof(DiscoveredModelCountDisplay));
    }

    private void ApplyModelFilter()
    {
        var filter = OpenAiModelFilter?.Trim();
        _synchronizingModels = true;
        try
        {
            FilteredDiscoveredModels.Clear();
            foreach (var model in _allDiscoveredModels)
            {
                if (string.IsNullOrWhiteSpace(filter) ||
                    model.ModelId.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    model.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    FilteredDiscoveredModels.Add(model);
                }
            }

            SelectedDefaultDiscoveredModel = FindMatchingModel(OpenAiDefaultModelId);
            SelectedArchitectDiscoveredModel = FindMatchingModel(OpenAiArchitectModelId);
            SelectedCoderDiscoveredModel = FindMatchingModel(OpenAiCoderModelId);
        }
        finally
        {
            _synchronizingModels = false;
        }

        OnPropertyChanged(nameof(HasDiscoveredOpenAiModels));
        OnPropertyChanged(nameof(DiscoveredModelCountDisplay));
    }

    private OpenAiDiscoveredModel? FindMatchingModel(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return null;
        }

        var trimmed = modelId.Trim();
        return FilteredDiscoveredModels.FirstOrDefault(m =>
            string.Equals(m.ModelId, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    private int ResolveSelectedContextWindowTokens(string? endpoint, string? defaultModelId, string? architectModelId)
    {
        if (FindDiscoveredContextWindow(architectModelId) is { } archTokens)
        {
            return archTokens;
        }

        if (FindDiscoveredContextWindow(defaultModelId) is { } defaultTokens)
        {
            return defaultTokens;
        }

        if (_loadedExistingOpenAiConfig?.ContextWindowTokens is > 0 &&
            AreSameEndpoint(_loadedExistingOpenAiConfig.Endpoint, endpoint))
        {
            return _loadedExistingOpenAiConfig.ContextWindowTokens.Value;
        }

        return InstallerConstants.DefaultContextWindowTokens;
    }

    private int? FindDiscoveredContextWindow(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return null;
        }

        var trimmed = modelId.Trim();
        var matched = _allDiscoveredModels.FirstOrDefault(m =>
            string.Equals(m.ModelId, trimmed, StringComparison.OrdinalIgnoreCase));
        return matched?.ContextWindowTokens is > 0 ? matched.ContextWindowTokens.Value : null;
    }

    private OpenAiAllowedModelsPersistenceMode ResolveAllowedModelsPersistenceMode(
        string? endpoint,
        string defaultModelId,
        string? architectModelId,
        string? coderModelId)
    {
        if (IsOpenAiDiscoverySuccess)
        {
            if (IsOpenAiDiscoveryUnsupported || _allDiscoveredModels.Count == 0)
            {
                return OpenAiAllowedModelsPersistenceMode.WriteManualModels;
            }

            return AreConfiguredModelsInDiscoveredCatalog(defaultModelId, architectModelId, coderModelId)
                ? OpenAiAllowedModelsPersistenceMode.ClearForDynamicDiscovery
                : OpenAiAllowedModelsPersistenceMode.WriteManualModels;
        }

        if (_loadedExistingOpenAiConfig is not null &&
            AreSameEndpoint(_loadedExistingOpenAiConfig.Endpoint, endpoint))
        {
            if (!_loadedExistingOpenAiConfig.HasManualAllowedModels)
            {
                return OpenAiAllowedModelsPersistenceMode.PreserveExisting;
            }

            return AreSelectedModelIdsUnchanged(_loadedExistingOpenAiConfig, defaultModelId, architectModelId, coderModelId)
                ? OpenAiAllowedModelsPersistenceMode.PreserveExisting
                : OpenAiAllowedModelsPersistenceMode.WriteManualModels;
        }

        return OpenAiAllowedModelsPersistenceMode.ClearForDynamicDiscovery;
    }

    private bool AreConfiguredModelsInDiscoveredCatalog(
        string defaultModelId,
        string? architectModelId,
        string? coderModelId) =>
        IsModelInDiscoveredCatalog(defaultModelId) &&
        (string.IsNullOrWhiteSpace(architectModelId) || IsModelInDiscoveredCatalog(architectModelId)) &&
        (string.IsNullOrWhiteSpace(coderModelId) || IsModelInDiscoveredCatalog(coderModelId));

    private bool IsModelInDiscoveredCatalog(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return false;
        }

        var trimmed = modelId.Trim();
        return _allDiscoveredModels.Any(m =>
            string.Equals(m.ModelId, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    private static bool AreSameEndpoint(string? left, string? right)
    {
        if (InstallerEndpointNormalizer.TryNormalizeEndpoint(left, out var normalizedLeft) &&
            InstallerEndpointNormalizer.TryNormalizeEndpoint(right, out var normalizedRight))
        {
            return string.Equals(normalizedLeft, normalizedRight, StringComparison.Ordinal);
        }

        return string.Equals(left?.Trim().TrimEnd('/'), right?.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }

    private static bool AreSelectedModelIdsUnchanged(
        ExistingOpenAiProviderConfig existing,
        string defaultModelId,
        string? architectModelId,
        string? coderModelId) =>
        OptionalIdsEqual(existing.DefaultModelId, defaultModelId) &&
        OptionalIdsEqual(existing.ArchitectModelId, architectModelId) &&
        OptionalIdsEqual(existing.CoderModelId, coderModelId);

    private static bool OptionalIdsEqual(string? left, string? right)
    {
        var normLeft = string.IsNullOrWhiteSpace(left) ? null : left.Trim();
        var normRight = string.IsNullOrWhiteSpace(right) ? null : right.Trim();
        return string.Equals(normLeft, normRight, StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatOpenAiSavedSummary(OpenAiProviderConfigurationSummary summary)
    {
        var keyStatus = summary.ApiKeyStoredInKeyring ? "API key stored in OS keyring" : "no keyring key stored";
        return $"Saved {summary.ProviderName} ({summary.Endpoint}) — Model: {summary.DefaultModelId} ({keyStatus}) → {summary.ConfigFilePath}";
    }
}
