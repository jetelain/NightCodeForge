namespace NightCodeForge.Installer.Core.Services;

using NightCodeForge.Installer.Core.Models;

/// <summary>
/// Discovers models from OpenAI-compatible endpoints and persists provider/model settings
/// into <c>&lt;InstallDir&gt;/config/config.json</c> with credentials stored in the OS keyring.
/// </summary>
public interface IOpenAiProviderConfigurationService
{
    Task<OpenAiModelDiscoveryResult> DiscoverModelsAsync(
        string endpoint,
        string? apiKeyOverride,
        string? organizationId,
        CancellationToken cancellationToken = default);

    Task<OpenAiProviderConfigurationSummary> SaveConfigurationAsync(
        OpenAiProviderSaveRequest request,
        CancellationToken cancellationToken = default);

    ExistingOpenAiProviderConfig? TryReadExistingConfiguration(string? installDirectory);
}
