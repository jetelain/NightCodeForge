namespace NightCodeForge.Installer.ViewModels.Wizard;

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
    public const int LastStepIndex = InstallerConstants.WizardStepCount - 1;

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

        SelectedStep = 0;

        BackCommand = new AsyncRelayCommand(() => GoToAsync(SelectedStep - 1), () => CanGoBack);
        NextCommand = new AsyncRelayCommand(() => GoToAsync(SelectedStep + 1), () => CanGoNext);
        FinishCommand = new RelayCommand(() => Step5.FinishCommand.Execute(null), () => CanFinish);

        Step4.SkipRequested += (_, _) => _ = GoToAsync(LastStepIndex);
    }

    public WizardStep1WelcomeViewModel Step1 { get; }
    public WizardStep2ProvisioningViewModel Step2 { get; }
    public WizardStep3SoftwareViewModel Step3 { get; }
    public WizardStep4AiFleetViewModel Step4 { get; }
    public WizardStep5ReadyViewModel Step5 { get; }

    [ObservableProperty]
    public partial int SelectedStep { get; set; }

    public bool CanGoBack => SelectedStep > 0;
    public bool CanGoNext => SelectedStep < LastStepIndex;
    public bool CanFinish => SelectedStep == LastStepIndex;

    public IAsyncRelayCommand BackCommand { get; }
    public IAsyncRelayCommand NextCommand { get; }
    public IRelayCommand FinishCommand { get; }

    public Task InitializeAsync() => Step1.RescanEnvironmentAsync();

    public void EnterUpgradeMode(InstallationDiscoveryResult discovery)
    {
        ArgumentNullException.ThrowIfNull(discovery);

        _dispatcher.Post(() =>
        {
            Step1.ApplyUpgradeMode(isUpgradeMode: true, discovery);
            Step2.IsCompleted = false;
            Step2.IsRebootRequired = false;
            Step2.OverallStatus = ProvisioningStatus.NotStarted;
            Step3.IsCompleted = false;
            Step3.HasError = false;
            SelectedStep = 0;
        });

        _ = InitializeAsync();
    }

    public async Task GoToAsync(int stepIndex)
    {
        if (stepIndex < 0 || stepIndex > LastStepIndex || stepIndex == SelectedStep)
        {
            return;
        }

        _dispatcher.Post(() => SelectedStep = stepIndex);

        switch (stepIndex)
        {
            case 1:
                await Step2.OnStepEnteredAsync().ConfigureAwait(false);
                break;
            case 2:
                await Step3.OnStepEnteredAsync().ConfigureAwait(false);
                break;
            case 4:
                _dispatcher.Post(() => Step5.RefreshSummary());
                break;
        }
    }

    partial void OnSelectedStepChanged(int value)
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(CanFinish));
        BackCommand.NotifyCanExecuteChanged();
        NextCommand.NotifyCanExecuteChanged();
        FinishCommand.NotifyCanExecuteChanged();
    }
}
