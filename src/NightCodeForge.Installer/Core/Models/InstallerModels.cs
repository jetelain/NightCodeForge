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
    Verified,
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
    double? ProgressPercent,
    string? ItemKey = null,
    ProvisioningStatus? ItemStatus = null);

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

public enum InstallerUiMode
{
    OobeWizard,
    MaintenanceHub,
    InteractiveUninstaller
}

public enum DoctorCheckStatus
{
    Pass,
    Warning,
    Fail
}

public sealed record ForgeDoctorCheckItem(
    string Name,
    DoctorCheckStatus Status,
    string Summary,
    string? Detail);

public sealed record ForgeDoctorReport(
    DateTimeOffset CheckedAtUtc,
    IReadOnlyList<ForgeDoctorCheckItem> Checks,
    int PassedCount,
    int WarningCount,
    int FailedCount);

public sealed record UpgradeAvailabilityResult(
    InstallationDiscoveryResult Discovery,
    InstallSourceOptions Source,
    ComponentReleaseAsset? NightCodeForgeRelease,
    ComponentReleaseAsset? OllamaRouterRelease,
    bool IsNightCodeForgeUpdateAvailable,
    bool IsOllamaRouterUpdateAvailable,
    bool CanUpgradeFromBundle,
    string? ResolutionWarning);

public sealed record AllComponentUpgradeResult(
    bool Succeeded,
    ReleaseDeploymentResult? NightCodeForgeDeployment,
    ReleaseDeploymentResult? OllamaRouterDeployment,
    IReadOnlyList<CompanionToolInstallOutcome> CompanionToolOutcomes,
    string? ErrorMessage);

public enum AiSetupMode
{
    OllamaRouter,
    OpenAiCompatible
}

public enum OpenAiAllowedModelsPersistenceMode
{
    PreserveExisting,
    ClearForDynamicDiscovery,
    WriteManualModels
}

public sealed record OpenAiProviderPreset(
    string Id,
    string Name,
    string DefaultEndpoint,
    bool RequiresApiKey,
    bool SupportsOrganizationId,
    string Description);

public sealed record OpenAiDiscoveredModel(
    string ModelId,
    string DisplayName,
    int? ContextWindowTokens,
    decimal? PromptCostPerMillion,
    decimal? CompletionCostPerMillion)
{
    public string DisplayLabel
    {
        get
        {
            var baseName = string.IsNullOrWhiteSpace(DisplayName) ||
                           string.Equals(DisplayName, ModelId, StringComparison.OrdinalIgnoreCase)
                ? ModelId
                : $"{DisplayName} ({ModelId})";

            var parts = new List<string>();
            if (ContextWindowTokens is > 0)
            {
                parts.Add(ContextWindowTokens.Value >= 1000
                    ? $"{ContextWindowTokens.Value / 1000}k ctx"
                    : $"{ContextWindowTokens.Value} ctx");
            }

            if (PromptCostPerMillion.HasValue && CompletionCostPerMillion.HasValue)
            {
                parts.Add(string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"${PromptCostPerMillion.Value:0.##}/${CompletionCostPerMillion.Value:0.##}/M"));
            }

            return parts.Count > 0 ? $"{baseName} [{string.Join(", ", parts)}]" : baseName;
        }
    }
}

public sealed record OpenAiModelDiscoveryResult(
    bool IsSuccess,
    bool IsDiscoveryImplemented,
    IReadOnlyList<OpenAiDiscoveredModel> Models,
    string? Message,
    string? ErrorMessage);

public sealed record OpenAiProviderSaveRequest(
    string? InstallDirectory,
    string ProviderName,
    string Endpoint,
    string? ApiKeyInput,
    string? OrganizationId,
    string DefaultModelId,
    string? ArchitectModelId,
    string? CoderModelId,
    int ContextWindowTokens,
    OpenAiAllowedModelsPersistenceMode AllowedModelsMode);

public sealed record OpenAiProviderConfigurationSummary(
    string ProviderName,
    string Endpoint,
    string DefaultModelId,
    string? ArchitectModelId,
    string? CoderModelId,
    int ContextWindowTokens,
    bool ApiKeyStoredInKeyring,
    string ConfigFilePath);

public sealed record ExistingOpenAiProviderConfig(
    string Endpoint,
    string? OrganizationId,
    string? DefaultModelId,
    string? ArchitectModelId,
    string? CoderModelId,
    int? ContextWindowTokens,
    bool HasManualAllowedModels,
    string ConfigFilePath);
