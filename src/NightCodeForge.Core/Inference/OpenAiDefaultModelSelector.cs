using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Core.Inference;

/// <summary>
/// Default model selection strategy for OpenAI-compatible endpoints:
/// Prioritizes economic efficiency: lowest prompt pricing first, then completion pricing,
/// and alphabetical order as tie-breaker.
/// </summary>
public sealed class OpenAiDefaultModelSelector : IDefaultModelSelector
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
            .OrderBy(m => (m.PromptCostPerMillion.HasValue || m.CompletionCostPerMillion.HasValue) ? 0 : 1)
            .ThenBy(m => m.PromptCostPerMillion ?? decimal.MaxValue)
            .ThenBy(m => m.CompletionCostPerMillion ?? decimal.MaxValue)
            .ThenBy(m => m.ModelId, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }
}

