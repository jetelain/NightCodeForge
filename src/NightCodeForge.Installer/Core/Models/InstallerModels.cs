namespace NightCodeForge.Installer.Core.Models;

using System.Text.Json.Serialization;

public enum HostPlatformType
{
    Windows,
    Linux,
    MacOS
}

public enum InstallSourceMode
{
    Online,
    AirGappedBundle
}

public enum ProvisioningStatus
{
    NotStarted,
    InProgress,
    Completed,
    RebootRequired,
    Failed,
    Skipped
}

public enum InstallerLogLevel
{
    Info,
    Warning,
    Error
}

public sealed record ComponentReleaseAsset(
    string ComponentName,
    string Version,
    string SourceLocation,
    string? Sha256Checksum,
    long FileSizeBytes,
    bool IsLocalBundle);

public sealed record CompanionNuGetToolSpec(
    string ToolCommand,
    string PackageId,
    bool InstallOnHost,
    bool InstallInWsl,
    string Description);

public sealed record UninstallOptions(
    bool KeepData,
    bool RemoveCompanionTools,
    bool RemoveOllamaRouter,
    bool Quiet);

public sealed record InstallerProgressEvent(
    string Step,
    string Message,
    InstallerLogLevel Level,
    double? ProgressPercent);

public sealed record EnvironmentProbeResult(
    HostPlatformType Platform,
    string RuntimeIdentifier,
    bool WslInstalled,
    bool WslDistroInstalled,
    string? ResolvedWslDistroName,
    bool HostDotNet10Installed,
    bool HostGitInstalled,
    bool HostDockerAvailable,
    bool HostRipgrepInstalled,
    bool WslDotNet10Installed,
    bool WslDockerInstalled,
    bool WslRipgrepInstalled,
    IReadOnlyList<string> DiagnosticNotes);

public sealed record ProvisioningOptions(
    string WslDistroName,
    bool ProvisionWsl,
    bool ProvisionHostDotNet,
    bool ProvisionLinuxOrWslToolchain,
    bool SkipDocker);

public sealed record ProvisioningStepResult(
    string StepName,
    ProvisioningStatus Status,
    string Message);

public sealed record ProvisioningReport(
    ProvisioningStatus OverallStatus,
    IReadOnlyList<ProvisioningStepResult> Steps,
    EnvironmentProbeResult PostProbe);

public sealed record InstalledComponentInfo(
    bool IsInstalled,
    string InstallDirectory,
    string? Version,
    bool IsLegacyPublishInstall,
    bool HasConfigDirectory,
    bool HasDataOrStateDirectory,
    bool HasLogsDirectory,
    string? PrimaryBinaryPath);

public sealed record InstallationDiscoveryResult(
    InstalledComponentInfo NightCodeForge,
    InstalledComponentInfo OllamaRouter,
    InstallSourceMode RecommendedSourceMode,
    string? DetectedBundleDirectory,
    IReadOnlyList<string> DetectedBundleFiles);

public sealed record InstallSourceOptions(
    InstallSourceMode Mode,
    string? BundleDirectory);

public sealed record ReleaseDeploymentResult(
    string ComponentName,
    string Version,
    string InstallDirectory,
    bool WasUpgrade,
    bool WasPreviouslyRunning,
    bool RestartedAfterSwap,
    bool SelfCopiedInstaller);

public sealed record CompanionToolStatus(
    CompanionNuGetToolSpec Spec,
    bool InstalledOnHost,
    string? HostVersion,
    bool InstalledInWsl,
    string? WslVersion);

public sealed record CompanionToolInstallOutcome(
    string PackageId,
    string TargetEnvironment,
    ProvisioningStatus Status,
    string? Version,
    string Message);

public sealed record WindowsUninstallRegistryPlan(
    string RegistryKeyPath,
    string DisplayName,
    string DisplayVersion,
    string Publisher,
    string InstallLocation,
    string DisplayIcon,
    string UninstallString,
    string QuietUninstallString,
    string ModifyPath,
    int NoModify,
    int NoRepair,
    int EstimatedSizeKb,
    string InstallDate);

public sealed record WindowsProtocolRegistrationPlan(
    string SchemeKeyPath,
    string DisplayName,
    string UrlProtocolValue,
    string IconKeyPath,
    string IconValue,
    string CommandKeyPath,
    string CommandValue);

public sealed record LinuxProtocolRegistrationPlan(
    string DesktopEntryPath,
    string DesktopEntryContent,
    string MimeType,
    string? XdgMimeCommand);

public sealed record LinuxDesktopEntryPlan(
    string WebAppDesktopPath,
    string WebAppDesktopContent,
    string InstallerDesktopPath,
    string InstallerDesktopContent);

public sealed record MacOsAppWrapperPlan(
    string AppBundleDirectory,
    string InfoPlistPath,
    string InfoPlistContent,
    string LauncherScriptPath,
    string LauncherScriptContent);

public sealed record UninstallManifest(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("installDirectory")] string InstallDirectory,
    [property: JsonPropertyName("installedAtUtc")] DateTimeOffset InstalledAtUtc,
    [property: JsonPropertyName("ollamaRouterDirectory")] string? OllamaRouterDirectory,
    [property: JsonPropertyName("platform")] string Platform);

public sealed record UninstallStepResult(
    string StepName,
    ProvisioningStatus Status,
    string Message);

public sealed record UninstallResult(
    bool Succeeded,
    bool DeferredSelfCleanupScheduled,
    IReadOnlyList<UninstallStepResult> Steps,
    string? ErrorMessage);

public sealed record RouterNodeCeilingSummary(
    string Target,
    int ContextCeiling,
    int? BaselineContext,
    double TokensPerSecond,
    int? VramMb);

public sealed record RouterRoleAssignmentSummary(
    string Role,
    string Model,
    int RecommendedContextWindow,
    string? Description,
    IReadOnlyList<RouterNodeCeilingSummary> Nodes);

public sealed record RouterRoleRecommendationsSummary(
    string ProviderType,
    string Endpoint,
    string? ArchitectModelId,
    string? CoderModelId,
    string? DefaultModelId,
    int ContextWindowTokens,
    DateTimeOffset? GeneratedAt,
    string ConfigFilePath,
    IReadOnlyList<RouterRoleAssignmentSummary> Roles);
