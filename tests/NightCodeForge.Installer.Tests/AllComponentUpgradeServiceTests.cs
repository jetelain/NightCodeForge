namespace NightCodeForge.Installer.Tests;

using Moq;
using NightCodeForge.Installer.Core.Abstractions;
using NightCodeForge.Installer.Core.Constants;
using NightCodeForge.Installer.Core.Exceptions;
using NightCodeForge.Installer.Core.Models;
using NightCodeForge.Installer.Core.Services;

public sealed class AllComponentUpgradeServiceTests
{
    [Theory]
    [InlineData("0.2.0", "0.1.0", true)]
    [InlineData("v1.0.0", "1.0.0-preview.5", true)]
    [InlineData("1.0.0", "1.0.0", false)]
    [InlineData("0.1.0", "0.2.0", false)]
    [InlineData("0.1.0-preview.12", "0.1.0-preview.9", true)]
    public void IsVersionNewer_ComparesSemVerCorrectly(string available, string installed, bool expected)
    {
        Assert.Equal(expected, AllComponentUpgradeService.IsVersionNewer(available, installed));
    }

    [Fact]
    public async Task CheckAvailableUpdatesAsync_AirGappedSameVersion_EnablesBundleReinstallAndReportsWarningOnMissingRouter()
    {
        var discovery = new InstallationDiscoveryResult(
            new InstalledComponentInfo(true, @"C:\NCF", "1.0.0", true, true, true, true, @"C:\NCF\NightCodeForge.Web.exe"),
            new InstalledComponentInfo(true, @"C:\OR", "1.0.0", false, true, true, true, @"C:\OR\ollamarouter-server.exe"),
            InstallSourceMode.AirGappedBundle,
            @"D:\bundle",
            ["NightCodeForge-Web-win-x64.zip"]);

        var detector = new Mock<IInstallationDetector>();
        detector.Setup(d => d.Detect(@"D:\bundle")).Returns(discovery);

        var releaseManager = new Mock<IReleaseManager>();
        releaseManager
            .Setup(r => r.ResolveLatestNightCodeForgeReleaseAsync(It.IsAny<InstallSourceOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComponentReleaseAsset("NightCodeForge-Web", "1.0.0", @"D:\bundle\NightCodeForge-Web-win-x64.zip", null, 100, true));
        releaseManager
            .Setup(r => r.ResolveLatestOllamaRouterReleaseAsync(It.IsAny<InstallSourceOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new BundleValidationException("OllamaRouter archive missing."));

        var sut = new AllComponentUpgradeService(
            detector.Object,
            releaseManager.Object,
            Mock.Of<ICompanionNuGetProvisioner>(),
            Mock.Of<IEnvironmentProvisioner>(),
            Mock.Of<IPlatformEnvironment>(p => p.Platform == HostPlatformType.Windows));

        var availability = await sut.CheckAvailableUpdatesAsync(
            new InstallSourceOptions(InstallSourceMode.AirGappedBundle, @"D:\bundle"),
            @"C:\NCF",
            CancellationToken.None);

        Assert.False(availability.IsNightCodeForgeUpdateAvailable);
        Assert.True(availability.CanUpgradeFromBundle);
        Assert.NotNull(availability.ResolutionWarning);
        Assert.Contains("OllamaRouter archive missing", availability.ResolutionWarning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpgradeAllAsync_DeploysWebOllamaRouterAndCompanionToolsWithResolvedWsl()
    {
        var discovery = new InstallationDiscoveryResult(
            new InstalledComponentInfo(true, @"C:\NCF", "0.1.0", true, true, true, true, @"C:\NCF\NightCodeForge.Web.exe"),
            new InstalledComponentInfo(true, @"C:\OR", "0.1.0", false, true, true, true, @"C:\OR\ollamarouter-server.exe"),
            InstallSourceMode.Online,
            null,
            []);

        var detector = new Mock<IInstallationDetector>();
        detector.Setup(d => d.Detect(It.IsAny<string?>())).Returns(discovery);

        var ncfAsset = new ComponentReleaseAsset("NightCodeForge-Web", "0.2.0", "https://example.com/ncf.zip", null, 100, false);
        var routerAsset = new ComponentReleaseAsset("OllamaRouter", "0.2.0", "https://example.com/or.zip", null, 100, false);

        var releaseManager = new Mock<IReleaseManager>();
        releaseManager.Setup(r => r.ResolveLatestNightCodeForgeReleaseAsync(It.IsAny<InstallSourceOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ncfAsset);
        releaseManager.Setup(r => r.ResolveLatestOllamaRouterReleaseAsync(It.IsAny<InstallSourceOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(routerAsset);
        releaseManager.Setup(r => r.DeployNightCodeForgeAsync(ncfAsset, @"C:\NCF", false, It.IsAny<IProgress<InstallerProgressEvent>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReleaseDeploymentResult("NightCodeForge-Web", "0.2.0", @"C:\NCF", true, true, true, true));
        releaseManager.Setup(r => r.DeployOllamaRouterAsync(routerAsset, null, It.IsAny<IProgress<InstallerProgressEvent>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReleaseDeploymentResult("OllamaRouter", "0.2.0", @"C:\OR", true, false, false, false));

        var envProvisioner = new Mock<IEnvironmentProvisioner>();
        envProvisioner.Setup(e => e.ProbeAsync(InstallerConstants.DefaultWslDistro, It.IsAny<CancellationToken>()))
            .ReturnsAsync(InstallerTestFactory.CreateHealthyWindowsProbe("Ubuntu-24.04"));

        var nuGetProvisioner = new Mock<ICompanionNuGetProvisioner>();
        nuGetProvisioner.Setup(n => n.ProvisionAllAsync(
                It.IsAny<InstallSourceOptions>(),
                "Ubuntu-24.04",
                It.IsAny<IProgress<InstallerProgressEvent>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([new CompanionToolInstallOutcome(CompanionToolCatalog.PackageNcf, "Host", ProvisioningStatus.Completed, "0.2.0", "OK")]);

        var sut = new AllComponentUpgradeService(
            detector.Object,
            releaseManager.Object,
            nuGetProvisioner.Object,
            envProvisioner.Object,
            Mock.Of<IPlatformEnvironment>(p => p.Platform == HostPlatformType.Windows));

        var result = await sut.UpgradeAllAsync(
            new InstallSourceOptions(InstallSourceMode.Online, null),
            @"C:\NCF",
            InstallerConstants.DefaultWslDistro,
            createDesktopShortcut: false,
            progress: null,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("0.2.0", result.NightCodeForgeDeployment?.Version);
        Assert.Equal("0.2.0", result.OllamaRouterDeployment?.Version);
        Assert.Single(result.CompanionToolOutcomes);
        nuGetProvisioner.VerifyAll();
    }

    [Fact]
    public async Task UpgradeAllAsync_WhenDeploymentThrows_ReturnsFailureResultAndReportsError()
    {
        var releaseManager = new Mock<IReleaseManager>();
        releaseManager.Setup(r => r.ResolveLatestNightCodeForgeReleaseAsync(It.IsAny<InstallSourceOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ReleaseDownloadException("GitHub rate limit exceeded"));

        var sut = new AllComponentUpgradeService(
            Mock.Of<IInstallationDetector>(),
            releaseManager.Object,
            Mock.Of<ICompanionNuGetProvisioner>(),
            Mock.Of<IEnvironmentProvisioner>(),
            Mock.Of<IPlatformEnvironment>(p => p.Platform == HostPlatformType.Windows));

        var reportedErrors = new List<InstallerProgressEvent>();
        var progress = new TestSyncProgress<InstallerProgressEvent>(e =>
        {
            if (e.Level == InstallerLogLevel.Error)
            {
                reportedErrors.Add(e);
            }
        });

        var result = await sut.UpgradeAllAsync(
            new InstallSourceOptions(InstallSourceMode.Online, null),
            @"C:\NCF",
            InstallerConstants.DefaultWslDistro,
            false,
            progress,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("GitHub rate limit", result.ErrorMessage, StringComparison.Ordinal);
        Assert.NotEmpty(reportedErrors);
    }

    private sealed class TestSyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
