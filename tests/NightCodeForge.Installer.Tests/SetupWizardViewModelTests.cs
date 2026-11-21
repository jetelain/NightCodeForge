namespace NightCodeForge.Installer.Tests;

using Moq;
using NightCodeForge.Installer.Core.Abstractions;
using NightCodeForge.Installer.Core.Constants;
using NightCodeForge.Installer.Core.Exceptions;
using NightCodeForge.Installer.Core.Models;
using NightCodeForge.Installer.Core.Services;
using NightCodeForge.Installer.ViewModels;
using NightCodeForge.Installer.ViewModels.Wizard;

public sealed class SetupWizardViewModelTests
{
    private static InstallationDiscoveryResult CreateFreshDiscovery() =>
        new(
            new InstalledComponentInfo(false, @"C:\NCF", null, false, false, false, false, null),
            new InstalledComponentInfo(false, @"C:\OR", null, false, false, false, false, null),
            InstallSourceMode.Online,
            null,
            []);

    [Fact]
    public async Task Wizard_Step1ToStep5Navigation_SourceSwitchingAndRebootHandling()
    {
        using var workspace = new TempTestWorkspace();
        var bundleDir = workspace.CreateSubdirectory("bundle");
        await File.WriteAllTextAsync(Path.Combine(bundleDir, "NightCodeForge-Web-win-x64.zip"), "zip");
        await File.WriteAllTextAsync(Path.Combine(bundleDir, "NightCodeForge.Cli.1.0.0.nupkg"), "nupkg");

        var discovery = CreateFreshDiscovery();
        var platform = TestArchiveHelper.CreateMockPlatform(HostPlatformType.Windows, "win-x64", workspace.RootPath);

        var envProvisioner = new Mock<IEnvironmentProvisioner>();
        var initialProbe = new EnvironmentProbeResult(
            HostPlatformType.Windows, "win-x64",
            WslInstalled: false, WslDistroInstalled: false, ResolvedWslDistroName: null,
            HostDotNet10Installed: false, HostGitInstalled: true, HostDockerAvailable: false,
            HostRipgrepInstalled: true, WslDotNet10Installed: false, WslDockerInstalled: false,
            WslRipgrepInstalled: false, DiagnosticNotes: []);
        envProvisioner.Setup(e => e.ProbeAsync(InstallerConstants.DefaultWslDistro, It.IsAny<CancellationToken>()))
            .ReturnsAsync(initialProbe);
        envProvisioner.Setup(e => e.ProvisionAsync(
                It.IsAny<ProvisioningOptions>(),
                It.IsAny<IProgress<InstallerProgressEvent>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProvisioningReport(
                ProvisioningStatus.RebootRequired,
                [new ProvisioningStepResult("WSL2", ProvisioningStatus.RebootRequired, "Reboot required")],
                initialProbe));

        var nuGetProvisioner = new Mock<ICompanionNuGetProvisioner>();
        nuGetProvisioner.Setup(n => n.ProbeToolsAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var uiHost = new Mock<IInstallerUiHost>();
        uiHost.Setup(h => h.PickBundleFolderAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(bundleDir);

        var detector = new Mock<IInstallationDetector>();
        detector.Setup(d => d.Detect(It.IsAny<string?>())).Returns(discovery);

        using var container = InstallerTestFactory.CreateContainer(
            platform: platform.Object,
            environmentProvisioner: envProvisioner.Object,
            nuGetProvisioner: nuGetProvisioner.Object,
            installationDetector: detector.Object);

        var plan = InstallerTestFactory.CreateStartupPlan(discovery, platform: platform.Object);
        var mainVm = new MainWindowViewModel(container, plan, uiHost.Object, new ImmediateUiDispatcher());
        var wizard = mainVm.Wizard;

        Assert.Equal(InstallerUiMode.OobeWizard, mainVm.CurrentMode);
        Assert.Equal(0, wizard.SelectedStep);
        Assert.False(wizard.CanGoBack);
        Assert.True(wizard.CanGoNext);
        Assert.False(wizard.CanFinish);

        await wizard.InitializeAsync();
        Assert.True(wizard.Step1.IsWindowsHost);

        // Browse selects air-gapped folder and scans assets
        await wizard.Step1.BrowseBundleFolderCommand.ExecuteAsync(null);
        Assert.True(wizard.Step1.IsAirGappedSource);
        Assert.Equal(bundleDir, wizard.Step1.BundleDirectory);
        Assert.Equal(2, wizard.Step1.DetectedBundleAssets.Count);

        // Navigate to Step 2 -> triggers provisioning & surfaces 3010 reboot prompt
        await wizard.GoToAsync(1);
        Assert.Equal(1, wizard.SelectedStep);
        Assert.True(wizard.Step2.IsRebootRequired);

        wizard.Step2.CloseForRebootCommand.Execute(null);
        uiHost.Verify(h => h.RequestExit(InstallerConstants.ExitCodeRebootRequired), Times.Once);
    }

    [Fact]
    public async Task Wizard_Step3Deployment_Step4OllamaRouterHandoff_AndStep5LaunchActions()
    {
        var discovery = new InstallationDiscoveryResult(
            new InstalledComponentInfo(true, @"C:\LegacyNCF", "0.8.0", true, true, true, true, @"C:\LegacyNCF\NightCodeForge.Web.exe"),
            new InstalledComponentInfo(false, @"C:\OR", null, false, false, false, false, null),
            InstallSourceMode.Online,
            null,
            []);

        var envProvisioner = new Mock<IEnvironmentProvisioner>();
        envProvisioner.Setup(e => e.ProbeAsync(InstallerConstants.DefaultWslDistro, It.IsAny<CancellationToken>()))
            .ReturnsAsync(InstallerTestFactory.CreateHealthyWindowsProbe());

        var ncfAsset = new ComponentReleaseAsset("NightCodeForge-Web", "1.0.0", "https://example.com/ncf.zip", null, 100, false);
        var routerAsset = new ComponentReleaseAsset("OllamaRouter", "1.0.0", "https://example.com/or.zip", null, 100, false);
        var releaseManager = new Mock<IReleaseManager>();
        releaseManager.Setup(r => r.ResolveLatestNightCodeForgeReleaseAsync(It.IsAny<InstallSourceOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ncfAsset);
        releaseManager.Setup(r => r.ResolveLatestOllamaRouterReleaseAsync(It.IsAny<InstallSourceOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(routerAsset);
        releaseManager.Setup(r => r.DeployNightCodeForgeAsync(ncfAsset, @"C:\LegacyNCF", It.IsAny<bool>(), It.IsAny<IProgress<InstallerProgressEvent>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReleaseDeploymentResult("NightCodeForge-Web", "1.0.0", @"C:\LegacyNCF", true, false, false, true));
        releaseManager.Setup(r => r.DeployOllamaRouterAsync(routerAsset, null, It.IsAny<IProgress<InstallerProgressEvent>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReleaseDeploymentResult("OllamaRouter", "1.0.0", @"C:\OR", false, false, false, false));

        var nuGetProvisioner = new Mock<ICompanionNuGetProvisioner>();
        nuGetProvisioner.Setup(n => n.ProbeToolsAsync(InstallerConstants.DefaultWslDistro, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        nuGetProvisioner.Setup(n => n.ProvisionAllAsync(It.IsAny<InstallSourceOptions>(), InstallerConstants.DefaultWslDistro, It.IsAny<IProgress<InstallerProgressEvent>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new CompanionToolInstallOutcome("NightCodeForge.Cli", "Host", ProvisioningStatus.Completed, "1.0.0", "OK")]);

        var syncedSummary = new RouterRoleRecommendationsSummary(
            "OllamaRouter",
            InstallerConstants.DefaultOllamaRouterEndpoint,
            ArchitectModelId: "qwen3.8:27b",
            CoderModelId: "qwen3.8:27b",
            DefaultModelId: "qwen3.8:27b",
            ContextWindowTokens: 262144,
            GeneratedAt: DateTimeOffset.UtcNow,
            ConfigFilePath: @"C:\LegacyNCF\config\config.json",
            Roles:
            [
                new RouterRoleAssignmentSummary("software_architect", "qwen3.8:27b", 262144, "Arch",
                    [new RouterNodeCeilingSummary("Server", 262144, null, 42.8, 98304)])
            ]);

        var handoffService = new Mock<IOllamaRouterSetupHandoffService>();
        handoffService.SetupSequence(h => h.RunSetupWizardAndSyncAsync(
                @"C:\LegacyNCF",
                InstallerConstants.DefaultOllamaRouterEndpoint,
                It.IsAny<IProgress<InstallerProgressEvent>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OllamaRouterWizardHandoffResult(false, 1, "User cancelled", null))
            .ReturnsAsync(new OllamaRouterWizardHandoffResult(true, 0, null, syncedSummary));

        var hostAppControl = new Mock<IHostAppControlService>();
        hostAppControl.Setup(c => c.IsWebServerRunning()).Returns(false);
        hostAppControl.Setup(c => c.StartWebServerAsync(@"C:\LegacyNCF", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        hostAppControl.Setup(c => c.GetConfiguredWebUrl(@"C:\LegacyNCF")).Returns("http://localhost:5270");
        hostAppControl.Setup(c => c.OpenWebUiInBrowserAsync("http://localhost:5270", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        hostAppControl.Setup(c => c.LaunchNightCodeAssayAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var uiHost = new Mock<IInstallerUiHost>();
        var detector = new Mock<IInstallationDetector>();
        detector.Setup(d => d.Detect(It.IsAny<string?>())).Returns(discovery);

        using var container = InstallerTestFactory.CreateContainer(
            environmentProvisioner: envProvisioner.Object,
            nuGetProvisioner: nuGetProvisioner.Object,
            installationDetector: detector.Object,
            releaseManager: releaseManager.Object,
            setupHandoff: handoffService.Object,
            hostAppControl: hostAppControl.Object);

        var plan = InstallerTestFactory.CreateStartupPlan(discovery, ["--wizard"]);
        var mainVm = new MainWindowViewModel(container, plan, uiHost.Object, new ImmediateUiDispatcher());
        var wizard = mainVm.Wizard;

        Assert.True(wizard.Step1.IsUpgradeMode);
        Assert.Contains("legacy publish.ps1", wizard.Step1.UpgradeBannerText, StringComparison.OrdinalIgnoreCase);

        await wizard.InitializeAsync();
        await wizard.GoToAsync(1);
        Assert.True(wizard.Step2.IsCompleted); // All healthy -> skipped provisioning cleanly

        await wizard.GoToAsync(2);
        Assert.True(wizard.Step3.IsCompleted);
        Assert.Equal(3, wizard.Step3.SummaryLines.Count);

        await wizard.GoToAsync(3);
        // First wizard run returns exit code 1
        await wizard.Step4.LaunchOllamaRouterWizardCommand.ExecuteAsync(null);
        Assert.False(wizard.Step4.HasSyncedRecommendations);
        Assert.Contains("User cancelled", wizard.Step4.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal("Re-run OllamaRouter Wizard", wizard.Step4.PrimaryButtonLabel);

        // Retry succeeds and populates role recommendations
        await wizard.Step4.LaunchOllamaRouterWizardCommand.ExecuteAsync(null);
        Assert.True(wizard.Step4.HasSyncedRecommendations);
        Assert.Equal("qwen3.8:27b", wizard.Step4.ArchitectModelDisplay);
        Assert.Single(wizard.Step4.RoleAssignments);

        await wizard.GoToAsync(4);
        Assert.True(wizard.CanFinish);
        Assert.Equal("v1.0.0", wizard.Step5.InstalledVersionDisplay);

        await wizard.Step5.LaunchNightCodeForgeWebCommand.ExecuteAsync(null);
        hostAppControl.Verify(c => c.StartWebServerAsync(@"C:\LegacyNCF", It.IsAny<CancellationToken>()), Times.Once);
        hostAppControl.Verify(c => c.OpenWebUiInBrowserAsync("http://localhost:5270", It.IsAny<CancellationToken>()), Times.Once);

        await wizard.Step5.LaunchNightCodeAssayCommand.ExecuteAsync(null);
        hostAppControl.Verify(c => c.LaunchNightCodeAssayAsync(It.IsAny<CancellationToken>()), Times.Once);

        wizard.FinishCommand.Execute(null);
        uiHost.Verify(h => h.RequestExit(InstallerConstants.ExitCodeSuccess), Times.Once);
    }

    [Fact]
    public async Task Wizard_Step4SkipAndErrorSurfacing_WorkCleanly()
    {
        var discovery = CreateFreshDiscovery();
        var syncService = new Mock<IOllamaRouterRecommendationSyncService>();
        syncService.Setup(s => s.SyncRecommendationsToConfigAsync(
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<IProgress<InstallerProgressEvent>?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OllamaRouterSyncException("Router unreachable"));

        using var container = InstallerTestFactory.CreateContainer(
            installationDetector: Mock.Of<IInstallationDetector>(d => d.Detect(It.IsAny<string?>()) == discovery),
            recommendationSync: syncService.Object);

        var plan = InstallerTestFactory.CreateStartupPlan(discovery);
        var mainVm = new MainWindowViewModel(container, plan, Mock.Of<IInstallerUiHost>(), new ImmediateUiDispatcher());

        await mainVm.Wizard.GoToAsync(3);
        await mainVm.Wizard.Step4.SyncRecommendationsOnlyCommand.ExecuteAsync(null);
        Assert.Contains("Router unreachable", mainVm.Wizard.Step4.ErrorMessage, StringComparison.Ordinal);

        mainVm.Wizard.Step4.SkipAiSetupCommand.Execute(null);
        Assert.True(mainVm.Wizard.Step4.IsSkipped);
        Assert.Equal(4, mainVm.Wizard.SelectedStep);
    }
}
