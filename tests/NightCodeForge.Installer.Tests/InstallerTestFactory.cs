namespace NightCodeForge.Installer.Tests;

using Moq;
using NightCodeForge.Installer.Cli;
using NightCodeForge.Installer.Core.Abstractions;
using NightCodeForge.Installer.Core.Constants;
using NightCodeForge.Installer.Core.Models;
using NightCodeForge.Installer.Core.Services;

internal static class InstallerTestFactory
{
    public static InstallerServiceContainer CreateContainer(
        HttpClient? httpClient = null,
        IPlatformEnvironment? platform = null,
        IProcessRunner? processRunner = null,
        IProcessControlService? processControl = null,
        IWindowsRegistryAccessor? registry = null,
        IEnvironmentProvisioner? environmentProvisioner = null,
        ICompanionNuGetProvisioner? nuGetProvisioner = null,
        IInstallationDetector? installationDetector = null,
        IOsIntegrationService? osIntegration = null,
        IReleaseManager? releaseManager = null,
        IUninstallService? uninstallService = null,
        IOllamaRouterRecommendationSyncService? recommendationSync = null,
        IOllamaRouterSetupHandoffService? setupHandoff = null,
        IAllComponentUpgradeService? upgradeService = null,
        IHostAppControlService? hostAppControl = null,
        IForgeDoctorService? forgeDoctor = null)
    {
        var resolvedPlatform = platform ?? Mock.Of<IPlatformEnvironment>(p =>
            p.Platform == HostPlatformType.Windows &&
            p.CurrentRuntimeIdentifier == "win-x64" &&
            p.UserHomeDirectory == @"C:\Users\Test" &&
            p.LocalApplicationDataDirectory == @"C:\Users\Test\AppData\Local" &&
            p.ApplicationDataDirectory == @"C:\Users\Test\AppData\Roaming" &&
            p.DesktopDirectory == @"C:\Users\Test\Desktop" &&
            p.InstallerBaseDirectory == @"C:\Installer");

        var resolvedHttp = httpClient ?? new HttpClient(new StubHttpMessageHandler((_, _) =>
            Task.FromResult(TestArchiveHelper.JsonResponse("{}"))));
        var resolvedRunner = processRunner ?? Mock.Of<IProcessRunner>();
        var resolvedControl = processControl ?? Mock.Of<IProcessControlService>();
        var resolvedRegistry = registry ?? Mock.Of<IWindowsRegistryAccessor>();

        var resolvedEnv = environmentProvisioner ?? CreateDefaultEnvironmentProvisioner();
        var resolvedNuGet = nuGetProvisioner ?? CreateDefaultNuGetProvisioner();
        var resolvedDetector = installationDetector ?? Mock.Of<IInstallationDetector>();
        var resolvedOs = osIntegration ?? Mock.Of<IOsIntegrationService>();
        var resolvedRelease = releaseManager ?? Mock.Of<IReleaseManager>();
        var resolvedUninstall = uninstallService ?? Mock.Of<IUninstallService>();
        var resolvedSync = recommendationSync ?? Mock.Of<IOllamaRouterRecommendationSyncService>();
        var resolvedHandoff = setupHandoff ?? Mock.Of<IOllamaRouterSetupHandoffService>();

        var resolvedUpgrade = upgradeService ?? new AllComponentUpgradeService(
            resolvedDetector,
            resolvedRelease,
            resolvedNuGet,
            resolvedEnv,
            resolvedPlatform);
        var resolvedHostControl = hostAppControl ?? new HostAppControlService(
            resolvedPlatform,
            resolvedRunner,
            resolvedControl,
            resolvedDetector);
        var resolvedDoctor = forgeDoctor ?? new ForgeDoctorService(
            resolvedHttp,
            resolvedPlatform,
            resolvedEnv,
            resolvedNuGet,
            resolvedDetector,
            resolvedControl);

        return new InstallerServiceContainer(
            new InstallerCoreAbstractions(resolvedHttp, resolvedPlatform, resolvedRunner, resolvedControl, resolvedRegistry),
            new InstallerProvisioningServices(resolvedEnv, resolvedNuGet, resolvedDetector, resolvedOs, resolvedRelease, resolvedUninstall),
            new InstallerOllamaRouterServices(resolvedSync, resolvedHandoff),
            new InstallerMaintenanceServices(resolvedUpgrade, resolvedHostControl, resolvedDoctor));
    }

    public static InstallerStartupPlan CreateStartupPlan(
        InstallationDiscoveryResult discovery,
        string[]? args = null,
        IPlatformEnvironment? platform = null)
    {
        var cli = InstallerCliParser.Parse(args ?? []);
        var env = platform ?? Mock.Of<IPlatformEnvironment>(p =>
            p.Platform == HostPlatformType.Windows &&
            p.CurrentRuntimeIdentifier == "win-x64" &&
            p.InstallerBaseDirectory == @"C:\Installer");
        return InstallerStartupRouter.Resolve(cli, discovery, env);
    }

    public static EnvironmentProbeResult CreateHealthyWindowsProbe(string distro = InstallerConstants.DefaultWslDistro) =>
        new(
            Platform: HostPlatformType.Windows,
            RuntimeIdentifier: "win-x64",
            WslInstalled: true,
            WslDistroInstalled: true,
            ResolvedWslDistroName: distro,
            HostDotNet10Installed: true,
            HostGitInstalled: true,
            HostDockerAvailable: true,
            HostRipgrepInstalled: true,
            WslDotNet10Installed: true,
            WslDockerInstalled: true,
            WslRipgrepInstalled: true,
            DiagnosticNotes: []);

    private static IEnvironmentProvisioner CreateDefaultEnvironmentProvisioner()
    {
        var mock = new Mock<IEnvironmentProvisioner>();
        mock.Setup(e => e.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateHealthyWindowsProbe());
        return mock.Object;
    }

    private static ICompanionNuGetProvisioner CreateDefaultNuGetProvisioner()
    {
        var mock = new Mock<ICompanionNuGetProvisioner>();
        mock.Setup(n => n.ProbeToolsAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        return mock.Object;
    }
}
