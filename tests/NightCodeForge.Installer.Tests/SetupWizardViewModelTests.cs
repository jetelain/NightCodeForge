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
        detector.Setup(d => d.Detect(It.IsAny<string?>(), It.IsAny<string?>())).Returns(discovery);

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

        // Navigate to Step 2 -> pre-populates all 5 Windows items & triggers provisioning -> surfaces 3010 reboot prompt
        await wizard.GoToAsync(1);
        Assert.Equal(1, wizard.SelectedStep);
        Assert.True(wizard.Step2.IsRebootRequired);
        Assert.Equal(5, wizard.Step2.Steps.Count);
        Assert.Contains(wizard.Step2.Steps, s => s.Key == ProvisioningStepNames.Wsl2 && s.Status == ProvisioningStatus.RebootRequired);
        Assert.Contains(wizard.Step2.Steps, s => s.Key == ProvisioningStepNames.HostDotNet && s.Status == ProvisioningStatus.NotStarted);

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
        detector.Setup(d => d.Detect(It.IsAny<string?>(), It.IsAny<string?>())).Returns(discovery);
        var osIntegration = new Mock<IOsIntegrationService>();

        using var container = InstallerTestFactory.CreateContainer(
            environmentProvisioner: envProvisioner.Object,
            nuGetProvisioner: nuGetProvisioner.Object,
            installationDetector: detector.Object,
            osIntegration: osIntegration.Object,
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
        Assert.True(wizard.Step2.IsCompleted); // All healthy -> verifies all prerequisites upfront
        Assert.Equal(5, wizard.Step2.Steps.Count);
        Assert.All(wizard.Step2.Steps, s =>
        {
            Assert.Equal(ProvisioningStatus.Verified, s.Status);
            Assert.True(s.IsChecked);
        });

        await wizard.GoToAsync(2);
        Assert.True(wizard.Step3.IsCompleted);
        Assert.Equal(7, wizard.Step3.Components.Count);
        Assert.All(wizard.Step3.Components, c =>
        {
            Assert.Equal(ProvisioningStatus.Completed, c.Status);
            Assert.True(c.IsChecked);
        });

        await wizard.GoToAsync(3);
        Assert.True(wizard.Step4.IsOllamaRouterMode);
        Assert.False(wizard.Step4.IsOpenAiCompatibleMode);
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

        wizard.Step5.CreateStartMenuShortcut = false;
        wizard.Step5.CreateDesktopShortcut = true;
        await wizard.Step5.ApplyShortcutsCommand.ExecuteAsync(null);
        osIntegration.Verify(
            o => o.ApplyShortcutPreferencesAsync(@"C:\LegacyNCF", false, true, It.IsAny<CancellationToken>()),
            Times.Once);

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
            installationDetector: Mock.Of<IInstallationDetector>(d => d.Detect(It.IsAny<string?>(), It.IsAny<string?>()) == discovery),
            recommendationSync: syncService.Object);

        var plan = InstallerTestFactory.CreateStartupPlan(discovery);
        var mainVm = new MainWindowViewModel(container, plan, Mock.Of<IInstallerUiHost>(), new ImmediateUiDispatcher());

        await mainVm.Wizard.GoToAsync(3);
        await mainVm.Wizard.Step4.SyncRecommendationsOnlyCommand.ExecuteAsync(null);
        Assert.Contains("Router unreachable", mainVm.Wizard.Step4.ErrorMessage, StringComparison.Ordinal);
        Assert.True(mainVm.Wizard.Step4.HasError);
        Assert.False(mainVm.Wizard.CanGoNext);

        mainVm.Wizard.Step4.SkipAiSetupCommand.Execute(null);
        Assert.True(mainVm.Wizard.Step4.IsSkipped);
        Assert.Equal(4, mainVm.Wizard.SelectedStep);
    }

    [Fact]
    public async Task Wizard_Step2AndStep3_CheckOffItemsInRealTimeAndRespectDisabledOptions()
    {
        var discovery = CreateFreshDiscovery();
        var envProvisioner = new Mock<IEnvironmentProvisioner>();
        var probe = new EnvironmentProbeResult(
            HostPlatformType.Windows, "win-x64",
            WslInstalled: false, WslDistroInstalled: false, ResolvedWslDistroName: null,
            HostDotNet10Installed: false, HostGitInstalled: true, HostDockerAvailable: false,
            HostRipgrepInstalled: true, WslDotNet10Installed: false, WslDockerInstalled: false,
            WslRipgrepInstalled: false, DiagnosticNotes: []);
        envProvisioner.Setup(e => e.ProbeAsync(InstallerConstants.DefaultWslDistro, It.IsAny<CancellationToken>()))
            .ReturnsAsync(probe);

        var observedStep2MidRun = new List<(string Key, ProvisioningStatus Status)>();
        WizardStep2ProvisioningViewModel? step2Ref = null;
        envProvisioner.Setup(e => e.ProvisionAsync(
                It.IsAny<ProvisioningOptions>(),
                It.IsAny<IProgress<InstallerProgressEvent>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProvisioningOptions _, IProgress<InstallerProgressEvent>? progress, CancellationToken _) =>
            {
                // All 5 items must already be populated before provisioning completes!
                Assert.NotNull(step2Ref);
                Assert.Equal(5, step2Ref!.Steps.Count);
                Assert.Equal(ProvisioningStatus.Skipped, step2Ref.Steps.Single(s => s.Key == ProvisioningStepNames.Wsl2).Status);
                Assert.Equal(ProvisioningStatus.NotStarted, step2Ref.Steps.Single(s => s.Key == ProvisioningStepNames.HostDotNet).Status);

                progress?.Report(new InstallerProgressEvent(
                    ProvisioningStepNames.HostDotNet,
                    "Installing .NET 10 SDK...",
                    InstallerLogLevel.Info,
                    40,
                    ProvisioningStepNames.HostDotNet,
                    ProvisioningStatus.InProgress));
                observedStep2MidRun.Add((ProvisioningStepNames.HostDotNet, step2Ref.Steps.Single(s => s.Key == ProvisioningStepNames.HostDotNet).Status));

                progress?.Report(new InstallerProgressEvent(
                    ProvisioningStepNames.HostDotNet,
                    "Installed .NET 10 SDK.",
                    InstallerLogLevel.Info,
                    100,
                    ProvisioningStepNames.HostDotNet,
                    ProvisioningStatus.Completed));
                observedStep2MidRun.Add((ProvisioningStepNames.HostDotNet, step2Ref.Steps.Single(s => s.Key == ProvisioningStepNames.HostDotNet).Status));

                return new ProvisioningReport(
                    ProvisioningStatus.Completed,
                    [
                        new ProvisioningStepResult(ProvisioningStepNames.Wsl2, ProvisioningStatus.Skipped, "Skipped"),
                        new ProvisioningStepResult(ProvisioningStepNames.Wsl2Distro, ProvisioningStatus.Skipped, "Skipped"),
                        new ProvisioningStepResult(ProvisioningStepNames.HostDotNet, ProvisioningStatus.Completed, "Installed .NET 10 SDK."),
                        new ProvisioningStepResult(ProvisioningStepNames.SubsystemToolchain, ProvisioningStatus.Skipped, "Skipped"),
                        new ProvisioningStepResult(ProvisioningStepNames.DockerEngine, ProvisioningStatus.Skipped, "Skipped")
                    ],
                    probe);
            });

        var ncfAsset = new ComponentReleaseAsset("NightCodeForge-Web", "1.0.0", "https://example.com/ncf.zip", null, 100, false);
        var releaseManager = new Mock<IReleaseManager>();
        releaseManager.Setup(r => r.ResolveLatestNightCodeForgeReleaseAsync(It.IsAny<InstallSourceOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ncfAsset);

        WizardStep3SoftwareViewModel? step3Ref = null;
        var observedStep3MidRun = new List<(string Key, ProvisioningStatus Status)>();
        releaseManager.Setup(r => r.DeployNightCodeForgeAsync(ncfAsset, It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<IProgress<InstallerProgressEvent>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ComponentReleaseAsset asset, string? dir, bool _, IProgress<InstallerProgressEvent>? progress, CancellationToken _) =>
            {
                Assert.NotNull(step3Ref);
                Assert.Equal(7, step3Ref!.Components.Count);
                Assert.Equal(ProvisioningStatus.Skipped, step3Ref.Components.Single(c => c.Key == DeploymentComponentKeys.OllamaRouter).Status);
                observedStep3MidRun.Add((DeploymentComponentKeys.NightCodeForgeWeb, step3Ref.Components.Single(c => c.Key == DeploymentComponentKeys.NightCodeForgeWeb).Status));
                return new ReleaseDeploymentResult(asset.ComponentName, asset.Version, dir ?? @"C:\NCF", false, false, false, false);
            });

        var nuGet = new Mock<ICompanionNuGetProvisioner>();
        nuGet.Setup(n => n.ProbeToolsAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        nuGet.Setup(n => n.ProvisionAllAsync(It.IsAny<InstallSourceOptions>(), It.IsAny<string?>(), It.IsAny<IProgress<InstallerProgressEvent>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((InstallSourceOptions _, string? _, IProgress<InstallerProgressEvent>? progress, CancellationToken _) =>
            {
                // NightCodeForge must already be checked off before companion tools run!
                observedStep3MidRun.Add((DeploymentComponentKeys.NightCodeForgeWeb, step3Ref!.Components.Single(c => c.Key == DeploymentComponentKeys.NightCodeForgeWeb).Status));

                var ncfKey = DeploymentComponentKeys.ForCompanionTool(CompanionToolCatalog.PackageNcf);
                progress?.Report(new InstallerProgressEvent("Tools", "Installing ncf...", InstallerLogLevel.Info, 20, ncfKey, ProvisioningStatus.InProgress));
                observedStep3MidRun.Add((ncfKey, step3Ref.Components.Single(c => c.Key == ncfKey).Status));

                progress?.Report(new InstallerProgressEvent("Tools", "Installed ncf.", InstallerLogLevel.Info, 40, ncfKey, ProvisioningStatus.Completed));
                observedStep3MidRun.Add((ncfKey, step3Ref.Components.Single(c => c.Key == ncfKey).Status));

                return [new CompanionToolInstallOutcome(CompanionToolCatalog.PackageNcf, "Host", ProvisioningStatus.Completed, "1.0.0", "OK")];
            });

        using var container = InstallerTestFactory.CreateContainer(
            environmentProvisioner: envProvisioner.Object,
            nuGetProvisioner: nuGet.Object,
            installationDetector: Mock.Of<IInstallationDetector>(d => d.Detect(It.IsAny<string?>(), It.IsAny<string?>()) == discovery),
            releaseManager: releaseManager.Object);

        var plan = InstallerTestFactory.CreateStartupPlan(discovery);
        var mainVm = new MainWindowViewModel(container, plan, Mock.Of<IInstallerUiHost>(), new ImmediateUiDispatcher());
        var wizard = mainVm.Wizard;
        step2Ref = wizard.Step2;
        step3Ref = wizard.Step3;

        wizard.Step1.ProvisionWsl = false;
        wizard.Step1.ProvisionLinuxOrWslToolchain = false;
        wizard.Step1.IncludeDocker = false;
        wizard.Step1.InstallOllamaRouter = false;

        await wizard.GoToAsync(1);
        Assert.Equal([(ProvisioningStepNames.HostDotNet, ProvisioningStatus.InProgress), (ProvisioningStepNames.HostDotNet, ProvisioningStatus.Completed)], observedStep2MidRun);
        Assert.True(wizard.Step2.Steps.Single(s => s.Key == ProvisioningStepNames.HostDotNet).IsChecked);
        Assert.Equal(ProvisioningStatus.Skipped, wizard.Step2.Steps.Single(s => s.Key == ProvisioningStepNames.DockerEngine).Status);

        await wizard.GoToAsync(2);
        Assert.Contains((DeploymentComponentKeys.NightCodeForgeWeb, ProvisioningStatus.InProgress), observedStep3MidRun);
        Assert.Contains((DeploymentComponentKeys.NightCodeForgeWeb, ProvisioningStatus.Completed), observedStep3MidRun);
        Assert.Contains((DeploymentComponentKeys.ForCompanionTool(CompanionToolCatalog.PackageNcf), ProvisioningStatus.InProgress), observedStep3MidRun);
        Assert.Contains((DeploymentComponentKeys.ForCompanionTool(CompanionToolCatalog.PackageNcf), ProvisioningStatus.Completed), observedStep3MidRun);
        Assert.Equal(ProvisioningStatus.Skipped, wizard.Step3.Components.Single(c => c.Key == DeploymentComponentKeys.OllamaRouter).Status);
        Assert.True(wizard.Step3.Components.Single(c => c.Key == DeploymentComponentKeys.NightCodeForgeWeb).IsChecked);
    }

    [Fact]
    public async Task Wizard_Step4OpenAiCompatibleMode_DiscoversFiltersSavesToKeyringAndConfigAndPopulatesStep5()
    {
        using var workspace = new TempTestWorkspace();
        var installDir = workspace.CreateSubdirectory("ncf");
        var discovery = new InstallationDiscoveryResult(
            new InstalledComponentInfo(true, installDir, "1.0.0", false, true, true, true, null),
            new InstalledComponentInfo(false, @"C:\OR", null, false, false, false, false, null),
            InstallSourceMode.Online,
            null,
            []);

        const string modelsJson = """
            {
              "data": [
                { "id": "anthropic/claude-sonnet-4", "display_name": "Claude Sonnet 4", "context_length": 200000 },
                { "id": "deepseek/deepseek-chat", "display_name": "DeepSeek Chat", "context_length": 131072 },
                { "id": "openai/o3", "display_name": "OpenAI o3", "context_length": 200000 }
              ]
            }
            """;

        using var httpClient = new HttpClient(new StubHttpMessageHandler((req, _) =>
        {
            Assert.Equal("https://openrouter.ai/api/v1/models", req.RequestUri?.ToString());
            Assert.Equal("Bearer sk-or-live", req.Headers.Authorization?.ToString());
            return Task.FromResult(TestArchiveHelper.JsonResponse(modelsJson));
        }));

        var secretStore = new Mock<IInstallerSecretStore>();
        var storedKeys = new Dictionary<string, string>(StringComparer.Ordinal);
        secretStore.Setup(s => s.IsKeyringAvailable()).Returns(true);
        secretStore.Setup(s => s.HasOpenAiApiKey(It.IsAny<string?>()))
            .Returns((string? ep) => ep is not null && storedKeys.ContainsKey(ep.Trim().TrimEnd('/')));
        secretStore.Setup(s => s.GetOpenAiApiKey(It.IsAny<string?>()))
            .Returns((string? ep) => ep is not null && storedKeys.TryGetValue(ep.Trim().TrimEnd('/'), out var val) ? val : null);
        secretStore.Setup(s => s.ResolveEffectiveOpenAiApiKey(It.IsAny<string?>()))
            .Returns((string? ep) => ep is not null && storedKeys.TryGetValue(ep.Trim().TrimEnd('/'), out var val) ? val : null);
        secretStore.Setup(s => s.SetOpenAiApiKey(It.IsAny<string>(), It.IsAny<string>()))
            .Callback((string ep, string key) => storedKeys[ep.Trim().TrimEnd('/')] = key.Trim());

        var detector = new Mock<IInstallationDetector>();
        detector.Setup(d => d.Detect(It.IsAny<string?>(), It.IsAny<string?>())).Returns(discovery);

        using var container = InstallerTestFactory.CreateContainer(
            httpClient: httpClient,
            secretStore: secretStore.Object,
            installationDetector: detector.Object);

        var plan = InstallerTestFactory.CreateStartupPlan(discovery, ["--wizard"]);
        var mainVm = new MainWindowViewModel(container, plan, Mock.Of<IInstallerUiHost>(), new ImmediateUiDispatcher());
        var wizard = mainVm.Wizard;

        // If user opted out of OllamaRouter in Step 1, Step 4 defaults straight to OpenAI Compatible mode.
        wizard.Step1.InstallOllamaRouter = false;
        await wizard.GoToAsync(3);
        Assert.True(wizard.Step4.IsOpenAiCompatibleMode);
        Assert.False(wizard.Step4.IsOllamaRouterMode);

        // Selecting OpenRouter updates endpoint URL
        wizard.Step4.SelectedOpenAiProvider = OpenAiProviderPresetCatalog.OpenRouter;
        Assert.Equal("https://openrouter.ai/api/v1", wizard.Step4.OpenAiEndpointUrl);

        wizard.Step4.OpenAiApiKeyInput = "sk-or-live";
        await wizard.Step4.TestAndDiscoverOpenAiModelsCommand.ExecuteAsync(null);
        Assert.True(wizard.Step4.IsOpenAiDiscoverySuccess);
        Assert.True(wizard.Step4.HasDiscoveredOpenAiModels);
        Assert.Equal(3, wizard.Step4.FilteredDiscoveredModels.Count);

        // Model filtering keeps matching entries and preserves selected default model
        wizard.Step4.OpenAiDefaultModelId = "anthropic/claude-sonnet-4";
        wizard.Step4.OpenAiModelFilter = "deepseek";
        Assert.Single(wizard.Step4.FilteredDiscoveredModels);
        Assert.Equal("anthropic/claude-sonnet-4", wizard.Step4.OpenAiDefaultModelId);

        wizard.Step4.OpenAiModelFilter = string.Empty;
        wizard.Step4.DifferentiateOpenAiRoles = true;
        wizard.Step4.OpenAiArchitectModelId = "openai/o3";
        wizard.Step4.OpenAiCoderModelId = "deepseek/deepseek-chat";
        Assert.True(wizard.Step4.HasUnsavedOpenAiChanges);

        // Clicking Next automatically persists unsaved OpenAI configuration before advancing to Step 5
        await wizard.GoToAsync(4);
        Assert.Equal(4, wizard.SelectedStep);
        Assert.False(wizard.Step4.HasUnsavedOpenAiChanges);
        Assert.True(wizard.Step4.HasSavedOpenAiConfiguration);
        Assert.Equal(string.Empty, wizard.Step4.OpenAiApiKeyInput);
        Assert.Equal("sk-or-live", storedKeys["https://openrouter.ai/api/v1"]);
        Assert.Contains("OpenRouter", wizard.Step5.AiModelsSummary, StringComparison.Ordinal);
        Assert.Contains("anthropic/claude-sonnet-4", wizard.Step5.AiModelsSummary, StringComparison.Ordinal);
        Assert.Contains("openai/o3", wizard.Step5.AiModelsSummary, StringComparison.Ordinal);
        Assert.Contains("deepseek/deepseek-chat", wizard.Step5.AiModelsSummary, StringComparison.Ordinal);

        // Fresh wizard instance detects existing OpenAI config from disk when entering Step 4
        var mainVm2 = new MainWindowViewModel(container, plan, Mock.Of<IInstallerUiHost>(), new ImmediateUiDispatcher());
        await mainVm2.Wizard.GoToAsync(3);
        Assert.True(mainVm2.Wizard.Step4.IsOpenAiCompatibleMode);
        Assert.Equal(OpenAiProviderPresetCatalog.OpenRouter, mainVm2.Wizard.Step4.SelectedOpenAiProvider);
        Assert.Equal("anthropic/claude-sonnet-4", mainVm2.Wizard.Step4.OpenAiDefaultModelId);
        Assert.True(mainVm2.Wizard.Step4.DifferentiateOpenAiRoles);
        Assert.True(mainVm2.Wizard.Step4.HasStoredOpenAiApiKey);
        Assert.False(mainVm2.Wizard.Step4.HasUnsavedOpenAiChanges);
    }

    [Fact]
    public async Task Wizard_Step4OpenAiErrorRecovery_ClearsErrorOnInputEditAndReEnablesNext()
    {
        using var workspace = new TempTestWorkspace();
        var installDir = workspace.CreateSubdirectory("ncf-err");
        var discovery = new InstallationDiscoveryResult(
            new InstalledComponentInfo(true, installDir, "1.0.0", false, true, true, true, null),
            new InstalledComponentInfo(false, @"C:\OR", null, false, false, false, false, null),
            InstallSourceMode.Online,
            null,
            []);

        var detector = new Mock<IInstallationDetector>();
        detector.Setup(d => d.Detect(It.IsAny<string?>(), It.IsAny<string?>())).Returns(discovery);

        using var container = InstallerTestFactory.CreateContainer(installationDetector: detector.Object);
        var plan = InstallerTestFactory.CreateStartupPlan(discovery, ["--wizard"]);
        var mainVm = new MainWindowViewModel(container, plan, Mock.Of<IInstallerUiHost>(), new ImmediateUiDispatcher());
        var wizard = mainVm.Wizard;

        wizard.Step1.InstallOllamaRouter = false;
        await wizard.GoToAsync(3);
        wizard.Step4.OpenAiApiKeyInput = "sk-entered";

        // Auto-save on Next fails because OpenAiDefaultModelId is empty
        await wizard.GoToAsync(4);
        Assert.Equal(3, wizard.SelectedStep);
        Assert.True(wizard.Step4.HasError);
        Assert.False(wizard.CanGoNext);

        // Entering a model ID clears ErrorMessage and re-enables Next
        wizard.Step4.OpenAiDefaultModelId = "gpt-4o";
        Assert.Equal(string.Empty, wizard.Step4.ErrorMessage);
        Assert.False(wizard.Step4.HasError);
        Assert.True(wizard.CanGoNext);

        await wizard.GoToAsync(4);
        Assert.Equal(4, wizard.SelectedStep);
        Assert.Contains("gpt-4o", wizard.Step5.AiModelsSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Wizard_Step4ExistingOpenAiConfig_PopulatesStep5UnchangedAndPreservesContextAndAllowedModelsOnKeyRotation()
    {
        using var workspace = new TempTestWorkspace();
        var installDir = workspace.CreateSubdirectory("ncf-existing");
        var configDir = Directory.CreateDirectory(Path.Combine(installDir, InstallerConstants.ConfigDirectoryName)).FullName;
        var configPath = Path.Combine(configDir, InstallerConstants.UserConfigFileName);

        await File.WriteAllTextAsync(configPath, """
            {
              "LlmProvider": {
                "ProviderType": "OpenAiCompatible",
                "DefaultModelId": "custom-arch",
                "ContextWindowTokens": 200000,
                "OpenAiCompatible": {
                  "Endpoint": "https://api.openai.com:443/v1/",
                  "AllowedModels": [
                    {
                      "ModelId": "custom-arch",
                      "DisplayName": "Custom Arch",
                      "ContextWindowTokens": 200000,
                      "SupportsVision": true
                    }
                  ]
                }
              }
            }
            """);

        var discovery = new InstallationDiscoveryResult(
            new InstalledComponentInfo(true, installDir, "1.0.0", false, true, true, true, null),
            new InstalledComponentInfo(false, @"C:\OR", null, false, false, false, false, null),
            InstallSourceMode.Online,
            null,
            []);

        var detector = new Mock<IInstallationDetector>();
        detector.Setup(d => d.Detect(It.IsAny<string?>(), It.IsAny<string?>())).Returns(discovery);

        using var container = InstallerTestFactory.CreateContainer(installationDetector: detector.Object);
        var plan = InstallerTestFactory.CreateStartupPlan(discovery, ["--wizard"]);

        // Case 1: Enter Step 4 and click Next unchanged -> Step 5 shows loaded summary
        var mainVm1 = new MainWindowViewModel(container, plan, Mock.Of<IInstallerUiHost>(), new ImmediateUiDispatcher());
        await mainVm1.Wizard.GoToAsync(3);
        Assert.Equal(OpenAiProviderPresetCatalog.OpenAi, mainVm1.Wizard.Step4.SelectedOpenAiProvider);
        Assert.True(mainVm1.Wizard.Step4.HasSavedOpenAiConfiguration);
        Assert.False(mainVm1.Wizard.Step4.HasUnsavedOpenAiChanges);

        await mainVm1.Wizard.GoToAsync(4);
        Assert.Equal(4, mainVm1.Wizard.SelectedStep);
        Assert.Contains("OpenAI", mainVm1.Wizard.Step5.AiModelsSummary, StringComparison.Ordinal);
        Assert.Contains("custom-arch", mainVm1.Wizard.Step5.AiModelsSummary, StringComparison.Ordinal);

        // Case 2: Enter Step 4, rotate only API key, advance -> preserves ContextWindowTokens and rich AllowedModels
        var mainVm2 = new MainWindowViewModel(container, plan, Mock.Of<IInstallerUiHost>(), new ImmediateUiDispatcher());
        await mainVm2.Wizard.GoToAsync(3);
        mainVm2.Wizard.Step4.OpenAiApiKeyInput = "sk-rotated";
        await mainVm2.Wizard.GoToAsync(4);
        Assert.Equal(4, mainVm2.Wizard.SelectedStep);

        var updatedJson = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(configPath))!.AsObject();
        var llm = updatedJson["LlmProvider"]!.AsObject();
        Assert.Equal(200000, llm["ContextWindowTokens"]?.GetValue<int>());
        var allowed = llm["OpenAiCompatible"]!["AllowedModels"]!.AsArray();
        var model = Assert.Single(allowed)!.AsObject();
        Assert.Equal("custom-arch", model["ModelId"]?.GetValue<string>());
        Assert.True(model["SupportsVision"]?.GetValue<bool>());
    }

    [Fact]
    public async Task Wizard_Step4OpenAi_Http200ZeroModels_WritesManualAllowedModelsOnSave()
    {
        using var workspace = new TempTestWorkspace();
        var installDir = workspace.CreateSubdirectory("ncf-empty-models");
        var discovery = new InstallationDiscoveryResult(
            new InstalledComponentInfo(true, installDir, "1.0.0", false, true, true, true, null),
            new InstalledComponentInfo(false, @"C:\OR", null, false, false, false, false, null),
            InstallSourceMode.Online,
            null,
            []);

        using var httpClient = new HttpClient(new StubHttpMessageHandler((_, _) =>
            Task.FromResult(TestArchiveHelper.JsonResponse("""{"data":[]}"""))));
        var detector = new Mock<IInstallationDetector>();
        detector.Setup(d => d.Detect(It.IsAny<string?>(), It.IsAny<string?>())).Returns(discovery);

        using var container = InstallerTestFactory.CreateContainer(
            httpClient: httpClient,
            installationDetector: detector.Object);
        var plan = InstallerTestFactory.CreateStartupPlan(discovery, ["--wizard"]);
        var mainVm = new MainWindowViewModel(container, plan, Mock.Of<IInstallerUiHost>(), new ImmediateUiDispatcher());

        mainVm.Wizard.Step1.InstallOllamaRouter = false;
        await mainVm.Wizard.GoToAsync(3);
        await mainVm.Wizard.Step4.TestAndDiscoverOpenAiModelsCommand.ExecuteAsync(null);
        Assert.True(mainVm.Wizard.Step4.IsOpenAiDiscoverySuccess);
        Assert.False(mainVm.Wizard.Step4.HasDiscoveredOpenAiModels);

        mainVm.Wizard.Step4.OpenAiDefaultModelId = "manual-model";
        await mainVm.Wizard.GoToAsync(4);

        var configPath = Path.Combine(installDir, InstallerConstants.ConfigDirectoryName, InstallerConstants.UserConfigFileName);
        var root = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(configPath))!.AsObject();
        var allowed = root["LlmProvider"]!["OpenAiCompatible"]!["AllowedModels"]!.AsArray();
        var entry = Assert.Single(allowed)!.AsObject();
        Assert.Equal("manual-model", entry["ModelId"]?.GetValue<string>());
    }

    [Fact]
    public async Task Wizard_Step4OpenAi_PresetSwitchAndManualEndpointEdit_ClearSecretsModelsAndSyncPreset()
    {
        var discovery = CreateFreshDiscovery();
        using var httpClient = new HttpClient(new StubHttpMessageHandler((_, _) =>
            Task.FromResult(TestArchiveHelper.JsonResponse("""{"data":[{"id":"gpt-4o"}]}"""))));

        using var container = InstallerTestFactory.CreateContainer(
            httpClient: httpClient,
            installationDetector: Mock.Of<IInstallationDetector>(d => d.Detect(It.IsAny<string?>(), It.IsAny<string?>()) == discovery));
        var plan = InstallerTestFactory.CreateStartupPlan(discovery, ["--wizard"]);
        var mainVm = new MainWindowViewModel(container, plan, Mock.Of<IInstallerUiHost>(), new ImmediateUiDispatcher());
        var step4 = mainVm.Wizard.Step4;

        mainVm.Wizard.Step1.InstallOllamaRouter = false;
        await mainVm.Wizard.GoToAsync(3);
        Assert.Equal(OpenAiProviderPresetCatalog.OpenAi, step4.SelectedOpenAiProvider);
        Assert.True(step4.ShowOpenAiOrganizationId);

        step4.OpenAiApiKeyInput = "sk-openai-only";
        step4.OpenAiOrganizationId = "org-openai";
        await step4.TestAndDiscoverOpenAiModelsCommand.ExecuteAsync(null);
        Assert.True(step4.HasDiscoveredOpenAiModels);

        step4.SelectedOpenAiProvider = OpenAiProviderPresetCatalog.DeepSeek;
        Assert.Equal(string.Empty, step4.OpenAiApiKeyInput);
        Assert.Equal(string.Empty, step4.OpenAiOrganizationId);
        Assert.False(step4.ShowOpenAiOrganizationId);
        Assert.False(step4.HasDiscoveredOpenAiModels);

        await step4.TestAndDiscoverOpenAiModelsCommand.ExecuteAsync(null);
        Assert.True(step4.HasDiscoveredOpenAiModels);

        step4.OpenAiEndpointUrl = "https://api.openai.com/v1";
        Assert.False(step4.HasDiscoveredOpenAiModels);
        Assert.Equal(OpenAiProviderPresetCatalog.OpenAi, step4.SelectedOpenAiProvider);
        Assert.True(step4.ShowOpenAiOrganizationId);
    }

    [Fact]
    public async Task Wizard_Step4OpenAi_CredentialStatusReReadsAvailabilityAndUsesEnvCheck()
    {
        var discovery = CreateFreshDiscovery();
        var available = true;
        var secretStore = new Mock<IInstallerSecretStore>(MockBehavior.Strict);
        secretStore.Setup(s => s.IsKeyringAvailable()).Returns(() => available);
        secretStore.Setup(s => s.HasOpenAiApiKey(It.IsAny<string?>())).Returns(() =>
        {
            available = false;
            return false;
        });
        secretStore.Setup(s => s.HasEnvironmentOpenAiApiKey()).Returns(false);

        using var container = InstallerTestFactory.CreateContainer(
            secretStore: secretStore.Object,
            installationDetector: Mock.Of<IInstallationDetector>(d => d.Detect(It.IsAny<string?>(), It.IsAny<string?>()) == discovery));
        var plan = InstallerTestFactory.CreateStartupPlan(discovery, ["--wizard"]);
        var mainVm = new MainWindowViewModel(container, plan, Mock.Of<IInstallerUiHost>(), new ImmediateUiDispatcher());

        mainVm.Wizard.Step1.InstallOllamaRouter = false;
        await mainVm.Wizard.GoToAsync(3);

        Assert.Contains("unavailable", mainVm.Wizard.Step4.OpenAiCredentialStatusText, StringComparison.OrdinalIgnoreCase);
        secretStore.Verify(s => s.ResolveEffectiveOpenAiApiKey(It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task Wizard_Step2EarlyFailure_UpdatesRemainingNotStartedChecklistItems()
    {
        var discovery = CreateFreshDiscovery();
        var probe = new EnvironmentProbeResult(
            HostPlatformType.Windows, "win-x64",
            WslInstalled: false, WslDistroInstalled: false, ResolvedWslDistroName: null,
            HostDotNet10Installed: false, HostGitInstalled: true, HostDockerAvailable: false,
            HostRipgrepInstalled: true, WslDotNet10Installed: false, WslDockerInstalled: false,
            WslRipgrepInstalled: false, DiagnosticNotes: []);

        var envProvisioner = new Mock<IEnvironmentProvisioner>();
        envProvisioner.Setup(e => e.ProbeAsync(InstallerConstants.DefaultWslDistro, It.IsAny<CancellationToken>()))
            .ReturnsAsync(probe);
        envProvisioner.Setup(e => e.ProvisionAsync(
                It.IsAny<ProvisioningOptions>(),
                It.IsAny<IProgress<InstallerProgressEvent>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProvisioningReport(
                ProvisioningStatus.Failed,
                [new ProvisioningStepResult(ProvisioningStepNames.Wsl2, ProvisioningStatus.Failed, "WSL2 install failed")],
                probe));

        using var container = InstallerTestFactory.CreateContainer(
            environmentProvisioner: envProvisioner.Object,
            installationDetector: Mock.Of<IInstallationDetector>(d => d.Detect(It.IsAny<string?>(), It.IsAny<string?>()) == discovery));
        var plan = InstallerTestFactory.CreateStartupPlan(discovery, ["--wizard"]);
        var mainVm = new MainWindowViewModel(container, plan, Mock.Of<IInstallerUiHost>(), new ImmediateUiDispatcher());

        await mainVm.Wizard.GoToAsync(1);
        Assert.Equal(ProvisioningStatus.Failed, mainVm.Wizard.Step2.OverallStatus);
        var wslItem = mainVm.Wizard.Step2.Steps.Single(s => s.Key == ProvisioningStepNames.Wsl2);
        Assert.Equal(ProvisioningStatus.Failed, wslItem.Status);

        var dotNetItem = mainVm.Wizard.Step2.Steps.Single(s => s.Key == ProvisioningStepNames.HostDotNet);
        Assert.Equal(ProvisioningStatus.NotStarted, dotNetItem.Status);
        Assert.Equal("Not run due to an earlier failure.", dotNetItem.Detail);
    }
}
