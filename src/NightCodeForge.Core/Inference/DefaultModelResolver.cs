using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Agents.Inference;

/// <summary>
/// Pure static utility to determine the optimal default model according to provider strategies.
/// </summary>
public static class DefaultModelResolver
{
    /// <summary>
    /// Resolves the default model from a candidate list based on the active provider strategy or an explicit override.
    /// </summary>
    public static AvailableModelInfo? ResolveDefaultModel(
        IEnumerable<AvailableModelInfo> candidates,
        LlmProviderType providerType,
        string? explicitDefaultModelId = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var list = candidates
            .Where(c => c != null && !string.IsNullOrWhiteSpace(c.ModelId))
            .Select(c =>
            {
                var trimmed = c.ModelId.Trim();
                return c.ModelId == trimmed ? c : c with { ModelId = trimmed };
            })
            .ToList();
        if (list.Count == 0)
        {
            return null;
        }

        // 1. Explicit model check: if specified and present in candidates, select it.
        if (!string.IsNullOrWhiteSpace(explicitDefaultModelId))
        {
            var trimmedExplicit = explicitDefaultModelId.Trim();
            var matched = list.FirstOrDefault(m => string.Equals(m.ModelId, trimmedExplicit, StringComparison.OrdinalIgnoreCase));
            if (matched != null)
            {
                return matched;
            }
        }

        // 2. OpenAI-Compatible: lowest prompt cost first, then completion cost, then alphabetical
        if (providerType == LlmProviderType.OpenAiCompatible)
        {
            return list
                .OrderBy(m => m.PromptCostPerMillion.HasValue ? 0 : 1)
                .ThenBy(m => m.PromptCostPerMillion ?? decimal.MaxValue)
                .ThenBy(m => m.CompletionCostPerMillion ?? decimal.MaxValue)
                .ThenBy(m => m.ModelId, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }

        // 3. Ollama / OllamaRouter: largest context first, Dense over MoE, size, alphabetical
        if (providerType is LlmProviderType.Ollama or LlmProviderType.OllamaRouter)
        {
            return list
                .OrderByDescending(m => Math.Max(0, m.ContextWindowTokens ?? 0))
                .ThenBy(m => m.IsMoE ? 1 : 0) // Dense (false = 0) prioritized over MoE (true = 1)
                .ThenByDescending(m => Math.Max(0L, m.SizeBytes ?? 0))
                .ThenBy(m => m.ModelId, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }

        throw new ArgumentOutOfRangeException(nameof(providerType), providerType, $"Unsupported LLM provider type: {providerType}");
    }
}
