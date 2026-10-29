using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Inference;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Agents.Inference;

/// <summary>
/// Forwarding utility to provider-specific default model selection strategies.
/// </summary>
public static class DefaultModelResolver
{
    private static readonly IDefaultModelSelector OpenAiSelector = new OpenAiDefaultModelSelector();
    private static readonly IDefaultModelSelector OllamaSelector = new OllamaDefaultModelSelector();

    /// <summary>
    /// Resolves the default model from a candidate list based on the active provider strategy or an explicit override.
    /// </summary>
    public static AvailableModelInfo? ResolveDefaultModel(
        IEnumerable<AvailableModelInfo> candidates,
        LlmProviderType providerType,
        string? explicitDefaultModelId = null)
    {
        var selector = providerType switch
        {
            LlmProviderType.OpenAiCompatible => OpenAiSelector,
            LlmProviderType.Ollama or LlmProviderType.OllamaRouter => OllamaSelector,
            _ => throw new ArgumentOutOfRangeException(nameof(providerType), providerType, "Unsupported LLM provider type.")
        };

        return selector.SelectDefaultModel(candidates, explicitDefaultModelId);
    }
}
