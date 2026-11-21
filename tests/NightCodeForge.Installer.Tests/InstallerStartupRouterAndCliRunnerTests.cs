namespace NightCodeForge.Installer.Tests;

using Moq;
using NightCodeForge.Installer.Cli;
using NightCodeForge.Installer.Core.Abstractions;
using NightCodeForge.Installer.Core.Constants;
using NightCodeForge.Installer.Core.Models;
using NightCodeForge.Installer.Core.Services;

public sealed class InstallerStartupRouterAndCliRunnerTests
{
    private static InstallationDiscoveryResult CreateDiscovery(
        bool ncfInstalled,
        bool isLegacy = false,
        string? detectedBundleDir = null)
    {
        return new InstallationDiscoveryResult(
            NightCodeForge: new InstalledComponentInfo(
                IsInstalled: ncfInstalled,
                InstallDirectory: @"C:\Apps\NightCodeForge",
                Version: ncfInstalled ? "0.9.0" : null,
                IsLegacyPublishInstall: isLegacy,
                HasConfigDirectory: ncfInstalled,
                HasDataOrStateDirectory: ncfInstalled,
                HasLogsDirectory: ncfInstalled,
                PrimaryBinaryPath: ncfInstalled ? @"C:\Apps\NightCodeForge\NightCodeForge.Web.exe" : null),
            OllamaRouter: new InstalledComponentInfo(
                IsInstalled: false,
                InstallDirectory: @"C:\Apps\OllamaRouter",
                Version: null,
                IsLegacyPublishInstall: false,
                HasConfigDirectory: false,
                HasDataOrStateDirectory: false,
                HasLogsDirectory: false,
                PrimaryBinaryPath: null),
            RecommendedSourceMode: detectedBundleDir is null ? InstallSourceMode.Online : InstallSourceMode.AirGappedBundle,
            DetectedBundleDirectory: detectedBundleDir,
            DetectedBundleFiles: detectedBundleDir is null ? [] : ["NightCodeForge-Web-win-x64.zip"]);
    }

    private static Mock<IPlatformEnvironment> CreatePlatformMock()
    {
        var mock = new Mock<IPlatformEnvironment>();
        mock.SetupGet(p => p.Platform).Returns(HostPlatformType.Windows);
        mock.SetupGet(p => p.CurrentRuntimeIdentifier).Returns("win-x64");
        mock.SetupGet(p => p.InstallerBaseDirectory).Returns(@"C:\Downloads\Setup");
        return mock;
    }

    [Fact]
    public void Resolve_FreshHostDefault_StartsOobeWizardOnline()
    {
        var cli = InstallerCliParser.Parse([]);
        var plan = InstallerStartupRouter.Resolve(cli, CreateDiscovery(ncfInstalled: false), CreatePlatformMock().Object);

        Assert.False(plan.RunHeadless);
        Assert.Equal(InstallerUiMode.OobeWizard, plan.InitialUiMode);
        Assert.False(plan.IsUpgradeWizardMode);
        Assert.Equal(InstallSourceMode.Online, plan.EffectiveSource.Mode);
    }

    [Fact]
    public void Resolve_ExistingOrLegacyInstall_StartsMaintenanceHub()
    {
        var cli = InstallerCliParser.Parse([]);
        var plan = InstallerStartupRouter.Resolve(
            cli,
            CreateDiscovery(ncfInstalled: true, isLegacy: true),
            CreatePlatformMock().Object);

        Assert.False(plan.RunHeadless);
        Assert.Equal(InstallerUiMode.MaintenanceHub, plan.InitialUiMode);
        Assert.True(plan.Discovery.NightCodeForge.IsLegacyPublishInstall);
        Assert.False(plan.AutoFocusUpgrade);
    }

    [Fact]
    public void Resolve_UpgradeAndWizardFlags_RouteCorrectly()
    {
        var upgradeCli = InstallerCliParser.Parse(["upgrade"]);
        var upgradePlan = InstallerStartupRouter.Resolve(
            upgradeCli,
            CreateDiscovery(ncfInstalled: true),
            CreatePlatformMock().Object);

        Assert.False(upgradePlan.RunHeadless);
        Assert.Equal(InstallerUiMode.MaintenanceHub, upgradePlan.InitialUiMode);
        Assert.True(upgradePlan.AutoFocusUpgrade);

        var wizardCli = InstallerCliParser.Parse(["--wizard", "--air-gapped"]);
        var wizardPlan = InstallerStartupRouter.Resolve(
            wizardCli,
            CreateDiscovery(ncfInstalled: true),
            CreatePlatformMock().Object);

        Assert.False(wizardPlan.RunHeadless);
        Assert.Equal(InstallerUiMode.OobeWizard, wizardPlan.InitialUiMode);
        Assert.True(wizardPlan.IsUpgradeWizardMode);
        Assert.Equal(InstallSourceMode.AirGappedBundle, wizardPlan.EffectiveSource.Mode);
        Assert.Equal(@"C:\Downloads\Setup\bundle", wizardPlan.EffectiveSource.BundleDirectory);
    }

    [Fact]
    public void Resolve_UninstallInteractiveAndQuiet_RouteCorrectly()
    {
        var interactivePlan = InstallerStartupRouter.Resolve(
            InstallerCliParser.Parse(["uninstall"]),
            CreateDiscovery(ncfInstalled: true),
            CreatePlatformMock().Object);
        Assert.False(interactivePlan.RunHeadless);
        Assert.Equal(InstallerUiMode.InteractiveUninstaller, interactivePlan.InitialUiMode);

        var quietPlan = InstallerStartupRouter.Resolve(
            InstallerCliParser.Parse(["uninstall", "--quiet", "--keep-data"]),
            CreateDiscovery(ncfInstalled: true),
            CreatePlatformMock().Object);
        Assert.True(quietPlan.RunHeadless);
        Assert.Equal(InstallerLaunchMode.Uninstall, quietPlan.HeadlessAction);
    }

    [Fact]
    public async Task RunHeadlessAsync_HelpUninstallAndUpgrade_ReturnExpectedExitCodes()
    {
        var platform = CreatePlatformMock().Object;
        var uninstallService = new Mock<IUninstallService>();
        var upgradeService = new Mock<IAllComponentUpgradeService>();

        using var services = InstallerTestFactory.CreateContainer(
            platform: platform,
            uninstallService: uninstallService.Object,
            upgradeService: upgradeService.Object);

        // Help
        var helpPlan = InstallerStartupRouter.Resolve(
            InstallerCliParser.Parse(["--help"]),
            CreateDiscovery(ncfInstalled: false),
            platform);
        using var outWriter = new StringWriter();
        using var errWriter = new StringWriter();
        var helpExit = await InstallerCliRunner.RunHeadlessAsync(helpPlan, services, outWriter, errWriter, CancellationToken.None);
        Assert.Equal(InstallerConstants.ExitCodeSuccess, helpExit);
        Assert.Contains("upgrade", outWriter.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--wizard", outWriter.ToString(), StringComparison.OrdinalIgnoreCase);

        // Quiet Uninstall failure -> exit 1
        uninstallService
            .Setup(s => s.UninstallAsync(
                It.IsAny<UninstallOptions>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<IProgress<InstallerProgressEvent>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UninstallResult(false, false, [], "Locked file"));

        var uninstallPlan = InstallerStartupRouter.Resolve(
            InstallerCliParser.Parse(["uninstall", "--quiet"]),
            CreateDiscovery(ncfInstalled: true),
            platform);
        var uninstallExit = await InstallerCliRunner.RunHeadlessAsync(uninstallPlan, services, outWriter, errWriter, CancellationToken.None);
        Assert.Equal(InstallerConstants.ExitCodeError, uninstallExit);
        Assert.Contains("Locked file", errWriter.ToString(), StringComparison.Ordinal);

        // Quiet Upgrade success -> exit 0
        upgradeService
            .Setup(s => s.UpgradeAllAsync(
                It.IsAny<InstallSourceOptions>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                false,
                It.IsAny<IProgress<InstallerProgressEvent>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AllComponentUpgradeResult(
                true,
                new ReleaseDeploymentResult("NightCodeForge-Web", "1.2.0", @"C:\Apps\NightCodeForge", true, false, false, false),
                null,
                [],
                null));

        var upgradePlan = InstallerStartupRouter.Resolve(
            InstallerCliParser.Parse(["upgrade", "--quiet", "--bundle", @"D:\bundle"]),
            CreateDiscovery(ncfInstalled: true),
            platform);
        var upgradeExit = await InstallerCliRunner.RunHeadlessAsync(upgradePlan, services, outWriter, errWriter, CancellationToken.None);
        Assert.Equal(InstallerConstants.ExitCodeSuccess, upgradeExit);
        Assert.Contains("1.2.0", outWriter.ToString(), StringComparison.Ordinal);
    }
}
