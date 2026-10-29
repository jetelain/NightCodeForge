using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Agents.Inference;

/// <summary>
/// Validates that requested model identifiers are permitted under the OpenAI-compatible configuration
/// and the discovered endpoint allowlist cache.
/// </summary>
public static class OpenAiModelAllowlistValidator
{
    /// <summary>
    /// Validates that <paramref name="modelId"/> is permitted under the active <paramref name="options"/>
    /// and <paramref name="allowlistCache"/>.
    /// Throws <see cref="ModelNotAllowedException"/> if the model is not allowed.
    /// </summary>
    public static void ValidateModelAllowed(
        LlmProviderOptions options,
        IOpenAiModelAllowlistCache allowlistCache,
        string modelId)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(allowlistCache);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        var trimmedModelId = modelId.Trim();

        // 1. If manual AllowedModels is configured, the model MUST exist in AllowedModels
        if (options.OpenAiCompatible?.AllowedModels is { Count: > 0 } allowedModels)
        {
            if (!allowedModels.Any(m => m != null && string.Equals(m.ModelId?.Trim(), trimmedModelId, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ModelNotAllowedException(modelId, LlmProviderType.OpenAiCompatible);
            }
            return;
        }

        // 2. Otherwise model must be discovered in allowlist cache
        if (!allowlistCache.IsModelAllowed(trimmedModelId))
        {
            throw new ModelNotAllowedException(modelId, LlmProviderType.OpenAiCompatible);
        }
    }
}

