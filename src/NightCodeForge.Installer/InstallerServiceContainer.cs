namespace NightCodeForge.Installer;

using NightCodeForge.Installer.Core.Abstractions;
using NightCodeForge.Installer.Core.Constants;
using NightCodeForge.Installer.Core.Services;

/// <summary>
/// Cohesive group of low-level OS/process/HTTP abstractions used by installer services.
/// </summary>
public sealed record InstallerCoreAbstractions(
    HttpClient HttpClient,
    IPlatformEnvironment Platform,
    IProcessRunner ProcessRunner,
    IProcessControlService ProcessControl,
    IWindowsRegistryAccessor Registry);

/// <summary>
/// Cohesive group of host provisioning, release delivery, and OS integration services.
/// </summary>
public sealed record InstallerProvisioningServices(
    IEnvironmentProvisioner EnvironmentProvisioner,
    ICompanionNuGetProvisioner NuGetProvisioner,
    IInstallationDetector InstallationDetector,
    IOsIntegrationService OsIntegration,
    IReleaseManager ReleaseManager,
    IUninstallService UninstallService);

/// <summary>
/// Cohesive group of OllamaRouter handoff and recommendation sync services.
/// </summary>
public sealed record InstallerOllamaRouterServices(
    IOllamaRouterRecommendationSyncService RecommendationSync,
    IOllamaRouterSetupHandoffService SetupHandoff);

/// <summary>
/// Cohesive group of Maintenance Hub upgrade, host process control, and Forge Doctor services.
/// </summary>
public sealed record InstallerMaintenanceServices(
    IAllComponentUpgradeService UpgradeService,
    IHostAppControlService HostAppControl,
    IForgeDoctorService ForgeDoctor);

/// <summary>
/// Composition root constructing the Native AOT installer core services without reflection-based DI containers.
/// </summary>
public sealed class InstallerServiceContainer : IDisposable
{
    public InstallerServiceContainer(
        InstallerCoreAbstractions abstractions,
        InstallerProvisioningServices provisioning,
        InstallerOllamaRouterServices ollamaRouter,
        InstallerMaintenanceServices maintenance)
    {
        ArgumentNullException.ThrowIfNull(abstractions);
        ArgumentNullException.ThrowIfNull(provisioning);
        ArgumentNullException.ThrowIfNull(ollamaRouter);
        ArgumentNullException.ThrowIfNull(maintenance);

        HttpClient = abstractions.HttpClient;
        Platform = abstractions.Platform;
        ProcessRunner = abstractions.ProcessRunner;
        ProcessControl = abstractions.ProcessControl;
        Registry = abstractions.Registry;

        EnvironmentProvisioner = provisioning.EnvironmentProvisioner;
        NuGetProvisioner = provisioning.NuGetProvisioner;
        InstallationDetector = provisioning.InstallationDetector;
        OsIntegration = provisioning.OsIntegration;
        ReleaseManager = provisioning.ReleaseManager;
        UninstallService = provisioning.UninstallService;

        OllamaRouterRecommendationSync = ollamaRouter.RecommendationSync;
        OllamaRouterSetupHandoff = ollamaRouter.SetupHandoff;

        UpgradeService = maintenance.UpgradeService;
        HostAppControl = maintenance.HostAppControl;
        ForgeDoctor = maintenance.ForgeDoctor;
    }

    public HttpClient HttpClient { get; }
    public IPlatformEnvironment Platform { get; }
    public IProcessRunner ProcessRunner { get; }
    public IProcessControlService ProcessControl { get; }
    public IWindowsRegistryAccessor Registry { get; }
    public IEnvironmentProvisioner EnvironmentProvisioner { get; }
    public ICompanionNuGetProvisioner NuGetProvisioner { get; }
    public IInstallationDetector InstallationDetector { get; }
    public IOsIntegrationService OsIntegration { get; }
    public IReleaseManager ReleaseManager { get; }
    public IUninstallService UninstallService { get; }
    public IOllamaRouterRecommendationSyncService OllamaRouterRecommendationSync { get; }
    public IOllamaRouterSetupHandoffService OllamaRouterSetupHandoff { get; }
    public IAllComponentUpgradeService UpgradeService { get; }
    public IHostAppControlService HostAppControl { get; }
    public IForgeDoctorService ForgeDoctor { get; }

    public static InstallerServiceContainer CreateDefault()
    {
        var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(InstallerConstants.DefaultHttpTimeoutSeconds)
        };
        var platform = new SystemPlatformEnvironment();
        var processRunner = new SystemProcessRunner();
        var processControl = new SystemProcessControlService();
        var registry = new SystemWindowsRegistryAccessor();
        var abstractions = new InstallerCoreAbstractions(
            httpClient,
            platform,
            processRunner,
            processControl,
            registry);

        var environmentProvisioner = new EnvironmentProvisioner(platform, processRunner);
        var nuGetProvisioner = new CompanionNuGetProvisioner(platform, processRunner);
        var installationDetector = new InstallationDetector(platform, registry);
        var osIntegration = new OsIntegrationService(platform, registry, processRunner, TimeProvider.System);
        var releaseManager = new ReleaseManager(
            httpClient,
            platform,
            processRunner,
            processControl,
            installationDetector,
            osIntegration);
        var uninstallService = new UninstallService(
            platform,
            processRunner,
            processControl,
            installationDetector,
            nuGetProvisioner,
            osIntegration,
            environmentProvisioner);
        var provisioning = new InstallerProvisioningServices(
            environmentProvisioner,
            nuGetProvisioner,
            installationDetector,
            osIntegration,
            releaseManager,
            uninstallService);

        var recommendationSync = new OllamaRouterRecommendationSyncService(httpClient, installationDetector);
        var setupHandoff = new OllamaRouterSetupHandoffService(
            platform,
            processRunner,
            installationDetector,
            recommendationSync);
        var ollamaRouter = new InstallerOllamaRouterServices(recommendationSync, setupHandoff);

        var upgradeService = new AllComponentUpgradeService(
            installationDetector,
            releaseManager,
            nuGetProvisioner,
            environmentProvisioner,
            platform);
        var hostAppControl = new HostAppControlService(
            platform,
            processRunner,
            processControl,
            installationDetector);
        var forgeDoctor = new ForgeDoctorService(
            httpClient,
            platform,
            environmentProvisioner,
            nuGetProvisioner,
            installationDetector,
            processControl);
        var maintenance = new InstallerMaintenanceServices(upgradeService, hostAppControl, forgeDoctor);

        return new InstallerServiceContainer(abstractions, provisioning, ollamaRouter, maintenance);
    }

    public void Dispose()
    {
        HttpClient.Dispose();
    }
}
