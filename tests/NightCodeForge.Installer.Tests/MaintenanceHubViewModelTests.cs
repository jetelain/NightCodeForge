namespace NightCodeForge.Installer.Tests;

using Moq;
using NightCodeForge.Installer.Core.Constants;
using NightCodeForge.Installer.Core.Models;
using NightCodeForge.Installer.Core.Services;
using NightCodeForge.Installer.ViewModels;

public sealed class MaintenanceHubViewModelTests
{
    private static InstallationDiscoveryResult CreateInstalledDiscovery(bool isLegacy = true) =>
        new(
            new InstalledComponentInfo(true, @"C:\NCF", "0.9.0", isLegacy, true, true, true, @"C:\NCF\NightCodeForge.Web.exe"),
            new InstalledComponentInfo(true, @"C:\OR", "0.9.0", false, true, true, true, @"C:\OR\ollamarouter-server.exe"),
            InstallSourceMode.Online,
            null,
            []);

    [Fact]
    public async Task MaintenanceHub_RefreshUpgradeDoctorAndModeTransitions_WorkEndToEnd()
    {
        var discovery = CreateInstalledDiscovery(isLegacy: true);
        var detector = new Mock<IInstallationDetector>();
        detector.Setup(d => d.Detect(It.IsAny<string?>(), It.IsAny<string?>())).Returns(discovery);

        var envProvisioner = new Mock<IEnvironmentProvisioner>();
        envProvisioner.Setup(e => e.ProbeAsync(InstallerConstants.DefaultWslDistro, It.IsAny<CancellationToken>()))
            .ReturnsAsync(InstallerTestFactory.CreateHealthyWindowsProbe());

        var toolStatuses = CompanionToolCatalog.All
            .Select(s => new CompanionToolStatus(s, true, "0.9.0", s.InstallInWsl, s.InstallInWsl ? "0.9.0" : null))
            .ToList();
        var nuGetProvisioner = new Mock<ICompanionNuGetProvisioner>();
        nuGetProvisioner.Setup(n => n.ProbeToolsAsync(InstallerConstants.DefaultWslDistro, It.IsAny<CancellationToken>()))
            .ReturnsAsync(toolStatuses);
        nuGetProvisioner.Setup(n => n.ProvisionAllAsync(
                It.IsAny<InstallSourceOptions>(),
                InstallerConstants.DefaultWslDistro,
                It.IsAny<IProgress<InstallerProgressEvent>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([new CompanionToolInstallOutcome(CompanionToolCatalog.PackageNcf, "Host", ProvisioningStatus.Completed, "1.0.0", "OK")]);

        var availability = new UpgradeAvailabilityResult(
            discovery,
            new InstallSourceOptions(InstallSourceMode.Online, null),
            new ComponentReleaseAsset("NightCodeForge-Web", "1.0.0", "https://example.com/ncf.zip", null, 100, false),
            new ComponentReleaseAsset("OllamaRouter", "1.0.0", "https://example.com/or.zip", null, 100, false),
            IsNightCodeForgeUpdateAvailable: true,
            IsOllamaRouterUpdateAvailable: true,
            CanUpgradeFromBundle: false,
            ResolutionWarning: null);

        var upgradeService = new Mock<IAllComponentUpgradeService>();
        upgradeService.Setup(u => u.CheckAvailableUpdatesAsync(It.IsAny<InstallSourceOptions>(), @"C:\NCF", It.IsAny<CancellationToken>()))
            .ReturnsAsync(availability);
        upgradeService.Setup(u => u.UpgradeAllAsync(
                It.IsAny<InstallSourceOptions>(),
                @"C:\NCF",
                InstallerConstants.DefaultWslDistro,
                false,
                It.IsAny<IProgress<InstallerProgressEvent>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AllComponentUpgradeResult(
                true,
                new ReleaseDeploymentResult("NightCodeForge-Web", "1.0.0", @"C:\NCF", true, true, true, true),
                new ReleaseDeploymentResult("OllamaRouter", "1.0.0", @"C:\OR", true, false, false, false),
                [],
                null));

        var hostControl = new Mock<IHostAppControlService>();
        hostControl.Setup(h => h.GetConfiguredWebUrl(@"C:\NCF")).Returns("http://localhost:5270");
        hostControl.Setup(h => h.IsWebServerRunning()).Returns(false);
        hostControl.Setup(h => h.StartWebServerAsync(@"C:\NCF", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        hostControl.Setup(h => h.StopWebServerAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        hostControl.Setup(h => h.OpenWebUiInBrowserAsync("http://localhost:5270", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        hostControl.Setup(h => h.OpenCliTerminalAsync(@"C:\NCF", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        hostControl.Setup(h => h.OpenOllamaRouterAppAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        hostControl.Setup(h => h.LaunchNightCodeAssayAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var doctor = new Mock<IForgeDoctorService>();
        doctor.Setup(d => d.RunDiagnosticsAsync(@"C:\NCF", InstallerConstants.DefaultWslDistro, InstallerConstants.DefaultOllamaRouterEndpoint, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ForgeDoctorReport(
                DateTimeOffset.UtcNow,
                [new ForgeDoctorCheckItem(InstallerConstants.DoctorCheckWsl2, DoctorCheckStatus.Pass, "OK", null)],
                PassedCount: 1,
                WarningCount: 0,
                FailedCount: 0));

        var handoff = new Mock<IOllamaRouterSetupHandoffService>();
        handoff.Setup(h => h.RunSetupWizardAndSyncAsync(
                @"C:\NCF",
                InstallerConstants.DefaultOllamaRouterEndpoint,
                It.IsAny<IProgress<InstallerProgressEvent>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OllamaRouterWizardHandoffResult(
                true,
                0,
                null,
                new RouterRoleRecommendationsSummary("OllamaRouter", InstallerConstants.DefaultOllamaRouterEndpoint, "qwen3.8:27b", "qwen3.8:27b", "qwen3.8:27b", 262144, null, @"C:\NCF\config\config.json", [])));

        using var container = InstallerTestFactory.CreateContainer(
            environmentProvisioner: envProvisioner.Object,
            nuGetProvisioner: nuGetProvisioner.Object,
            installationDetector: detector.Object,
            setupHandoff: handoff.Object,
            upgradeService: upgradeService.Object,
            hostAppControl: hostControl.Object,
            forgeDoctor: doctor.Object);

        var plan = InstallerTestFactory.CreateStartupPlan(discovery, ["upgrade"]);
        var mainVm = new MainWindowViewModel(container, plan, Mock.Of<IInstallerUiHost>(), new ImmediateUiDispatcher());
        var hub = mainVm.MaintenanceHub;

        Assert.Equal(InstallerUiMode.MaintenanceHub, mainVm.CurrentMode);
        Assert.True(hub.AutoFocusUpgradeBanner);
        Assert.True(hub.IsLegacyPublishInstall);

        await mainVm.InitializeAsync();
        Assert.Equal("v0.9.0", hub.InstalledVersion);
        Assert.Equal("v1.0.0", hub.AvailableVersion);
        Assert.Equal(MaintenanceHubViewModel.OnlineUpgradeLabel, hub.UpgradeButtonLabel);
        Assert.True(hub.CanExecuteUpgrade);
        Assert.Equal(5, hub.CompanionTools.Count);

        // Quick actions
        await hub.StartServerCommand.ExecuteAsync(null);
        Assert.True(hub.IsWebServerRunning);
        await hub.OpenWebUiCommand.ExecuteAsync(null);
        await hub.OpenCliTerminalCommand.ExecuteAsync(null);
        await hub.StopServerCommand.ExecuteAsync(null);
        Assert.False(hub.IsWebServerRunning);

        // 1-Click All-Component Upgrade
        await hub.UpgradeAllCommand.ExecuteAsync(null);
        upgradeService.Verify(u => u.UpgradeAllAsync(
            It.Is<InstallSourceOptions>(s => s.Mode == InstallSourceMode.Online),
            @"C:\NCF",
            InstallerConstants.DefaultWslDistro,
            false,
            It.IsAny<IProgress<InstallerProgressEvent>?>(),
            It.IsAny<CancellationToken>()), Times.Once);

        // Companion tools & OllamaRouter actions
        await hub.UpdateAllToolsCommand.ExecuteAsync(null);
        await hub.LaunchNightCodeAssayCommand.ExecuteAsync(null);
        await hub.OpenOllamaRouterAppCommand.ExecuteAsync(null);
        await hub.RunOllamaRouterWizardCommand.ExecuteAsync(null);
        Assert.Contains("qwen3.8:27b", hub.LastSyncedModelsSummary, StringComparison.Ordinal);

        // Forge Doctor
        await hub.RunForgeDoctorCommand.ExecuteAsync(null);
        Assert.True(hub.HasDoctorReport);
        Assert.Single(hub.DoctorChecks);
        Assert.Contains("1 passed", hub.DoctorSummaryText, StringComparison.Ordinal);

        // Mode transitions: Hub -> Uninstaller -> Hub -> Upgrade Wizard
        hub.OpenUninstallerCommand.Execute(null);
        Assert.Equal(InstallerUiMode.InteractiveUninstaller, mainVm.CurrentMode);
        mainVm.Uninstaller.CancelCommand.Execute(null);
        Assert.Equal(InstallerUiMode.MaintenanceHub, mainVm.CurrentMode);

        hub.RerunSetupWizardCommand.Execute(null);
        Assert.Equal(InstallerUiMode.OobeWizard, mainVm.CurrentMode);
        Assert.True(mainVm.Wizard.Step1.IsUpgradeMode);
    }

    [Fact]
    public async Task MaintenanceHub_AirGappedLocalBundleSwitch_EnablesBundleUpgradeButton()
    {
        using var workspace = new TempTestWorkspace();
        var bundleDir = workspace.CreateSubdirectory("local-bundle");
        await File.WriteAllTextAsync(Path.Combine(bundleDir, "NightCodeForge-Web-win-x64.zip"), "zip");

        var discovery = CreateInstalledDiscovery(isLegacy: false);
        var hostControl = new Mock<IHostAppControlService>();
        hostControl.Setup(h => h.GetConfiguredWebUrl(@"C:\NCF")).Returns("http://localhost:5270");
        hostControl.Setup(h => h.ScanBundleDirectoryAssets(bundleDir))
            .Returns(["NightCodeForge-Web-win-x64.zip"]);

        var releaseManager = new Mock<IReleaseManager>();
        releaseManager.Setup(r => r.ResolveLatestNightCodeForgeReleaseAsync(
                It.Is<InstallSourceOptions>(s => s.Mode == InstallSourceMode.AirGappedBundle && s.BundleDirectory == bundleDir),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComponentReleaseAsset("NightCodeForge-Web", "0.9.0", Path.Combine(bundleDir, "NightCodeForge-Web-win-x64.zip"), null, 100, true));

        var uiHost = new Mock<IInstallerUiHost>();
        uiHost.Setup(u => u.PickBundleFolderAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(bundleDir);

        using var container = InstallerTestFactory.CreateContainer(
            installationDetector: Mock.Of<IInstallationDetector>(d => d.Detect(It.IsAny<string?>(), It.IsAny<string?>()) == discovery),
            releaseManager: releaseManager.Object,
            hostAppControl: hostControl.Object);

        var plan = InstallerTestFactory.CreateStartupPlan(discovery);
        var mainVm = new MainWindowViewModel(container, plan, uiHost.Object, new ImmediateUiDispatcher());

        await mainVm.MaintenanceHub.BrowseBundleFolderCommand.ExecuteAsync(null);
        Assert.True(mainVm.MaintenanceHub.IsAirGappedSource);
        Assert.Equal(MaintenanceHubViewModel.LocalBundleUpgradeLabel, mainVm.MaintenanceHub.UpgradeButtonLabel);
        Assert.True(mainVm.MaintenanceHub.CanUpgradeFromBundle);
        Assert.True(mainVm.MaintenanceHub.CanExecuteUpgrade);
    }

    [Fact]
    public async Task MaintenanceHub_ExplicitInstallDirPreserved_SourceSwitchClearsStaleUpdates_AndFailedStopKeepsRunningState()
    {
        var explicitDir = OperatingSystem.IsWindows() ? @"C:\CustomNCF" : "/opt/custom-ncf";
        var discovery = CreateInstalledDiscovery(isLegacy: false);
        var detector = new Mock<IInstallationDetector>();
        detector.Setup(d => d.Detect(It.IsAny<string?>(), It.IsAny<string?>())).Returns(discovery);

        var releaseManager = new Mock<IReleaseManager>();
        releaseManager.Setup(r => r.ResolveLatestNightCodeForgeReleaseAsync(It.IsAny<InstallSourceOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComponentReleaseAsset("NightCodeForge-Web", "2.0.0", "https://example.com/ncf.zip", null, 100, false));
        releaseManager.Setup(r => r.ResolveLatestOllamaRouterReleaseAsync(It.IsAny<InstallSourceOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComponentReleaseAsset("OllamaRouter", "2.0.0", "https://example.com/or.zip", null, 100, false));

        var hostControl = new Mock<IHostAppControlService>();
        hostControl.Setup(h => h.GetConfiguredWebUrl(It.IsAny<string>())).Returns("http://localhost:5270");
        hostControl.Setup(h => h.IsWebServerRunning()).Returns(true);
        hostControl.Setup(h => h.StopWebServerAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);

        using var container = InstallerTestFactory.CreateContainer(
            installationDetector: detector.Object,
            releaseManager: releaseManager.Object,
            hostAppControl: hostControl.Object);

        var plan = InstallerTestFactory.CreateStartupPlan(discovery, ["upgrade", "--install-dir", explicitDir]);
        var mainVm = new MainWindowViewModel(container, plan, Mock.Of<IInstallerUiHost>(), new ImmediateUiDispatcher());
        var hub = mainVm.MaintenanceHub;

        await mainVm.InitializeAsync();
        Assert.Equal(explicitDir, hub.InstallDirectory);
        detector.Verify(d => d.Detect(null, explicitDir), Times.AtLeastOnce);
        Assert.True(hub.IsNightCodeForgeUpdateAvailable);
        Assert.True(hub.IsOllamaRouterUpdateAvailable);
        Assert.Equal("v2.0.0", hub.AvailableVersion);
        Assert.True(hub.UpgradeAllCommand.CanExecute(null));

        // Switching Online -> Air-Gapped with empty bundle clears stale updates
        hub.IsAirGappedSource = true;
        Assert.False(hub.IsNightCodeForgeUpdateAvailable);
        Assert.False(hub.IsOllamaRouterUpdateAvailable);
        Assert.False(hub.CanUpgradeFromBundle);
        Assert.Equal("Click Refresh Status to check", hub.AvailableVersion);
        Assert.False(hub.UpgradeAllCommand.CanExecute(null));

        // Failed stop keeps IsWebServerRunning true
        await hub.StopServerCommand.ExecuteAsync(null);
        Assert.True(hub.IsWebServerRunning);
        Assert.Contains("could not be stopped", hub.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }
}
