using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Core.Inference;

/// <summary>
/// Default model selection strategy for Ollama and OllamaRouter:
/// Prioritizes capability: largest context window first, Dense architecture over Mixture of Experts (MoE),
/// model size, and alphabetical order as tie-breaker.
/// </summary>
public sealed class OllamaDefaultModelSelector : IDefaultModelSelector
{
    public AvailableModelInfo? SelectDefaultModel(IEnumerable<AvailableModelInfo> candidates, string? explicitDefaultModelId = null)
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

        if (!string.IsNullOrWhiteSpace(explicitDefaultModelId))
        {
            var trimmedExplicit = explicitDefaultModelId.Trim();
            var matched = list.FirstOrDefault(m => string.Equals(m.ModelId, trimmedExplicit, StringComparison.OrdinalIgnoreCase));
            if (matched != null)
            {
                return matched;
            }
        }

        return list
            .OrderByDescending(m => Math.Max(0, m.ContextWindowTokens ?? 0))
            .ThenBy(m => m.IsMoE ? 1 : 0) // Dense (false = 0) prioritized over MoE (true = 1)
            .ThenByDescending(m => Math.Max(0L, m.SizeBytes ?? 0))
            .ThenBy(m => m.ModelId, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }
}

