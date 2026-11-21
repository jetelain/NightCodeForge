namespace NightCodeForge.Installer.ViewModels.Wizard;

using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NightCodeForge.Installer.Core.Constants;
using NightCodeForge.Installer.Core.Models;

/// <summary>
/// Root ViewModel coordinating the 5-step OOBE Setup Wizard (Welcome, Host Provisioning,
/// Software &amp; Tools Installation, Delegated OllamaRouter AI Setup, and Ready to Forge).
/// </summary>
public sealed partial class SetupWizardViewModel : ObservableObject
{
    public const int LastStepIndex = (int)WizardStep.Ready;

    private readonly SetupWizardState _state;
    private readonly IUiDispatcher _dispatcher;

    public SetupWizardViewModel(
        SetupWizardState state,
        WizardStep1WelcomeViewModel step1,
        WizardStep2ProvisioningViewModel step2,
        WizardStep3SoftwareViewModel step3,
        WizardStep4AiFleetViewModel step4,
        WizardStep5ReadyViewModel step5,
        IUiDispatcher dispatcher)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        Step1 = step1 ?? throw new ArgumentNullException(nameof(step1));
        Step2 = step2 ?? throw new ArgumentNullException(nameof(step2));
        Step3 = step3 ?? throw new ArgumentNullException(nameof(step3));
        Step4 = step4 ?? throw new ArgumentNullException(nameof(step4));
        Step5 = step5 ?? throw new ArgumentNullException(nameof(step5));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

        SelectedStep = (int)WizardStep.Welcome;

        BackCommand = new AsyncRelayCommand(() => GoToAsync(SelectedStep - 1), () => CanGoBack);
        NextCommand = new AsyncRelayCommand(() => GoToAsync(SelectedStep + 1), () => CanGoNext);
        FinishCommand = new RelayCommand(() => Step5.FinishCommand.Execute(null), () => CanFinish);

        Step1.PropertyChanged += OnStepStatePropertyChanged;
        Step2.PropertyChanged += OnStepStatePropertyChanged;
        Step3.PropertyChanged += OnStepStatePropertyChanged;
        Step4.PropertyChanged += OnStepStatePropertyChanged;
        Step5.PropertyChanged += OnStepStatePropertyChanged;
        Step1.ConfigurationChanged += (_, _) => ResetDownstreamStepsOnConfigurationChange();
        Step4.SkipRequested += (_, _) => _ = ObserveTaskAsync(GoToAsync(LastStepIndex));
    }

    public WizardStep1WelcomeViewModel Step1 { get; }
    public WizardStep2ProvisioningViewModel Step2 { get; }
    public WizardStep3SoftwareViewModel Step3 { get; }
    public WizardStep4AiFleetViewModel Step4 { get; }
    public WizardStep5ReadyViewModel Step5 { get; }

    public InstallSourceOptions CurrentSourceOptions => _state.ToInstallSourceOptions();
    public string CurrentInstallDirectory => _state.InstallDirectory;

    [ObservableProperty]
    public partial int SelectedStep { get; set; }

    public bool IsAnyStepBusy =>
        Step1.IsProbing ||
        Step2.IsRunning ||
        Step3.IsRunning ||
        Step4.IsRunning ||
        Step5.IsBusy;

    public bool CanGoBack =>
        SelectedStep > (int)WizardStep.Welcome &&
        !IsAnyStepBusy &&
        !Step2.IsRebootRequired;

    public bool CanGoNext =>
        SelectedStep < LastStepIndex &&
        !IsAnyStepBusy &&
        !Step2.IsRebootRequired &&
        (SelectedStep != (int)WizardStep.Welcome || Step1.IsConfigurationValid) &&
        (SelectedStep != (int)WizardStep.Provisioning || Step2.IsCompleted) &&
        (SelectedStep != (int)WizardStep.Software || (Step3.IsCompleted && !Step3.HasError)) &&
        (SelectedStep != (int)WizardStep.AiFleet || !Step4.HasError);

    public bool CanFinish =>
        SelectedStep == LastStepIndex &&
        !IsAnyStepBusy &&
        !Step2.IsRebootRequired &&
        !Step3.HasError;

    public IAsyncRelayCommand BackCommand { get; }
    public IAsyncRelayCommand NextCommand { get; }
    public IRelayCommand FinishCommand { get; }

    public Task InitializeAsync() => Step1.RescanEnvironmentAsync();

    public void EnterUpgradeMode(
        InstallationDiscoveryResult discovery,
        InstallSourceOptions? sourceOptions = null,
        string? installDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(discovery);

        _dispatcher.Post(() =>
        {
            if (sourceOptions is not null)
            {
                Step1.ApplyConfiguration(sourceOptions, installDirectory);
            }
            else if (!string.IsNullOrWhiteSpace(installDirectory))
            {
                Step1.InstallDirectory = installDirectory.Trim();
            }

            Step1.ApplyUpgradeMode(isUpgradeMode: true, discovery);
            ResetDownstreamSteps();
            SelectedStep = (int)WizardStep.Welcome;
            NotifyNavigationStateChanged();
        });

        _ = ObserveTaskAsync(InitializeAsync());
    }

    public async Task GoToAsync(int stepIndex)
    {
        if (stepIndex < (int)WizardStep.Welcome ||
            stepIndex > LastStepIndex ||
            stepIndex == SelectedStep ||
            IsAnyStepBusy ||
            Step2.IsRebootRequired)
        {
            return;
        }

        if (SelectedStep == (int)WizardStep.AiFleet &&
            stepIndex > SelectedStep &&
            Step4.IsOpenAiCompatibleMode &&
            Step4.HasUnsavedOpenAiChanges)
        {
            var saved = await Step4.SaveOpenAiConfigurationAsync().ConfigureAwait(false);
            if (!saved)
            {
                return;
            }
        }

        _dispatcher.Post(() => SelectedStep = stepIndex);

        switch ((WizardStep)stepIndex)
        {
            case WizardStep.Provisioning:
                await Step2.OnStepEnteredAsync().ConfigureAwait(false);
                break;
            case WizardStep.Software:
                await Step3.OnStepEnteredAsync().ConfigureAwait(false);
                break;
            case WizardStep.AiFleet:
                _dispatcher.Post(() => Step4.OnStepEntered());
                break;
            case WizardStep.Ready:
                _dispatcher.Post(() => Step5.RefreshSummary());
                break;
        }
    }

    partial void OnSelectedStepChanged(int value) => NotifyNavigationStateChanged();

    private void OnStepStatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is
            nameof(WizardStep1WelcomeViewModel.IsProbing) or
            nameof(WizardStep1WelcomeViewModel.IsConfigurationValid) or
            nameof(WizardStep2ProvisioningViewModel.IsRunning) or
            nameof(WizardStep2ProvisioningViewModel.IsCompleted) or
            nameof(WizardStep2ProvisioningViewModel.IsRebootRequired) or
            nameof(WizardStep2ProvisioningViewModel.OverallStatus) or
            nameof(WizardStep3SoftwareViewModel.IsRunning) or
            nameof(WizardStep3SoftwareViewModel.IsCompleted) or
            nameof(WizardStep3SoftwareViewModel.HasError) or
            nameof(WizardStep4AiFleetViewModel.IsRunning) or
            nameof(WizardStep4AiFleetViewModel.HasError) or
            nameof(WizardStep4AiFleetViewModel.IsSkipped) or
            nameof(WizardStep5ReadyViewModel.IsBusy))
        {
            NotifyNavigationStateChanged();
        }
    }

    private void ResetDownstreamStepsOnConfigurationChange()
    {
        if (SelectedStep == (int)WizardStep.Welcome)
        {
            ResetDownstreamSteps();
        }
    }

    private void ResetDownstreamSteps()
    {
        _state.NightCodeForgeDeployment = null;
        _state.OllamaRouterDeployment = null;
        _state.CompanionOutcomes = [];
        _state.SyncedRecommendations = null;
        _state.ConfiguredOpenAiProvider = null;
        _state.AiFleetSkipped = false;
        _state.AppliedStartMenuShortcut = false;
        _state.AppliedDesktopShortcut = false;

        Step2.ResetState();
        Step3.ResetState();
        Step4.ResetState();
        NotifyNavigationStateChanged();
    }

    private void NotifyNavigationStateChanged()
    {
        OnPropertyChanged(nameof(IsAnyStepBusy));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(CanFinish));
        BackCommand.NotifyCanExecuteChanged();
        NextCommand.NotifyCanExecuteChanged();
        FinishCommand.NotifyCanExecuteChanged();
    }

    private async Task ObserveTaskAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Justification: Lifetime cancellation during shutdown is expected.
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => Step1.StatusMessage = $"Wizard navigation warning: {ex.Message}");
        }
    }
}
