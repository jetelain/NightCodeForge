using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Interfaces;

public record AvailableModelInfo(
    string ModelId,
    string DisplayName,
    int? ContextWindowTokens = null,
    long? SizeBytes = null,
    LlmProviderType ProviderType = LlmProviderType.Ollama,
    bool SupportsReasoning = false,
    IReadOnlyDictionary<string, object?>? Metadata = null,
    decimal? PromptCostPerMillion = null,
    decimal? CompletionCostPerMillion = null,
    bool IsMoE = false)
{
    /// <summary>Formatted badge for UI dropdowns (e.g. "19.8 GB" or "128k ctx").</summary>
    public string FormattedBadge => ContextWindowTokens.HasValue
        ? $"{ContextWindowTokens.Value / 1024}k ctx"
        : (SizeBytes.HasValue ? $"{SizeBytes.Value / (1024.0 * 1024 * 1024):F1} GB" : string.Empty);
}

/// <summary>
/// Result of probing an inference endpoint for model discovery and connectivity.
/// </summary>
public record EndpointVerificationResult(
    bool IsSuccess,
    bool IsDiscoveryImplemented,
    int DiscoveredCount = 0,
    IReadOnlyList<AvailableModelInfo>? VerifiedModels = null,
    string? WarningMessage = null,
    string? ErrorMessage = null);

public interface ILlmModelCatalogService
{
    Task<IReadOnlyList<AvailableModelInfo>> GetAvailableModelsAsync(CancellationToken cancellationToken = default);
    Task<AvailableModelInfo?> GetDefaultModelAsync(CancellationToken cancellationToken = default);
    Task<EndpointVerificationResult> VerifyEndpointAsync(CancellationToken cancellationToken = default);
    Task RefreshAsync(CancellationToken cancellationToken = default);
}
