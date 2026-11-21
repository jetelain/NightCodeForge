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
/// Composition root constructing the Native AOT installer core services without reflection-based DI containers.
/// </summary>
public sealed class InstallerServiceContainer : IDisposable
{
    public InstallerServiceContainer(
        InstallerCoreAbstractions abstractions,
        IEnvironmentProvisioner environmentProvisioner,
        ICompanionNuGetProvisioner nuGetProvisioner,
        IInstallationDetector installationDetector,
        IOsIntegrationService osIntegration,
        IReleaseManager releaseManager,
        IUninstallService uninstallService,
        IOllamaRouterRecommendationSyncService ollamaRouterRecommendationSync,
        IOllamaRouterSetupHandoffService ollamaRouterSetupHandoff)
    {
        ArgumentNullException.ThrowIfNull(abstractions);
        HttpClient = abstractions.HttpClient;
        Platform = abstractions.Platform;
        ProcessRunner = abstractions.ProcessRunner;
        ProcessControl = abstractions.ProcessControl;
        Registry = abstractions.Registry;
        EnvironmentProvisioner = environmentProvisioner ?? throw new ArgumentNullException(nameof(environmentProvisioner));
        NuGetProvisioner = nuGetProvisioner ?? throw new ArgumentNullException(nameof(nuGetProvisioner));
        InstallationDetector = installationDetector ?? throw new ArgumentNullException(nameof(installationDetector));
        OsIntegration = osIntegration ?? throw new ArgumentNullException(nameof(osIntegration));
        ReleaseManager = releaseManager ?? throw new ArgumentNullException(nameof(releaseManager));
        UninstallService = uninstallService ?? throw new ArgumentNullException(nameof(uninstallService));
        OllamaRouterRecommendationSync = ollamaRouterRecommendationSync ?? throw new ArgumentNullException(nameof(ollamaRouterRecommendationSync));
        OllamaRouterSetupHandoff = ollamaRouterSetupHandoff ?? throw new ArgumentNullException(nameof(ollamaRouterSetupHandoff));
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
            osIntegration);
        var recommendationSync = new OllamaRouterRecommendationSyncService(httpClient, installationDetector);
        var setupHandoff = new OllamaRouterSetupHandoffService(
            platform,
            processRunner,
            installationDetector,
            recommendationSync);

        return new InstallerServiceContainer(
            abstractions,
            environmentProvisioner,
            nuGetProvisioner,
            installationDetector,
            osIntegration,
            releaseManager,
            uninstallService,
            recommendationSync,
            setupHandoff);
    }

    public void Dispose()
    {
        HttpClient.Dispose();
    }
}
