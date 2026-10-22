using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using NightCodeForge.Agents.Compaction;

namespace NightCodeForge.Agents.Inference;

/// <summary>
/// Resolves model-aware reasoning effort and context-window settings from <see cref="LlmProviderOptions"/>,
/// so that agents (Ideation, Crucible) never hardcode provider- or model-specific values.
/// </summary>
public static class ModelCapabilityResolver
{
    private static readonly string[] EffortRank = ["minimal", "low", "medium", "high", "xhigh"];

    /// <summary>
    /// Finds the profile whose key is the longest case-insensitive substring of <paramref name="modelId"/>.
    /// </summary>
    public static ModelCapabilityProfile? FindProfile(LlmProviderOptions? options, string? modelId)
    {
        if (options?.ModelProfiles == null || string.IsNullOrWhiteSpace(modelId)) return null;

        ModelCapabilityProfile? best = null;
        int bestLength = -1;
        foreach (var (key, profile) in options.ModelProfiles)
        {
            if (string.IsNullOrWhiteSpace(key) || key.Length <= bestLength) continue;
            if (modelId.Contains(key, StringComparison.OrdinalIgnoreCase))
            {
                best = profile;
                bestLength = key.Length;
            }
        }
        return best;
    }

    /// <summary>
    /// Resolves the reasoning effort to send for a model: requested/default effort, then the model's
    /// explicit <see cref="ModelCapabilityProfile.EffortMap"/>, then nearest supported level (ties go higher),
    /// otherwise the generic OpenAI normalization.
    /// </summary>
    public static string ResolveReasoningEffort(
        LlmProviderOptions? options,
        string? modelId,
        string? requestedEffort = null,
        bool isOpenAi = false)
    {
        var profile = FindProfile(options, modelId);
        var requested = !string.IsNullOrWhiteSpace(requestedEffort)
            ? requestedEffort
            : (!string.IsNullOrWhiteSpace(options?.ReasoningEffort) ? options!.ReasoningEffort : profile?.DefaultEffort);

        var effort = OllamaChatClientFactory.NormalizeReasoningEffort(requested, isOpenAi);
        if (profile == null) return effort;

        if (profile.EffortMap.TryGetValue(effort, out var mapped) && !string.IsNullOrWhiteSpace(mapped))
        {
            return mapped.Trim().ToLowerInvariant();
        }

        var supported = profile.SupportedEfforts
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim().ToLowerInvariant())
            .ToList();
        if (supported.Count == 0 || supported.Contains(effort)) return effort;

        int target = Array.IndexOf(EffortRank, effort);
        if (target < 0) return supported[0];

        return supported
            .Select(s => (Level: s, Rank: Array.IndexOf(EffortRank, s)))
            .Where(x => x.Rank >= 0)
            .OrderBy(x => Math.Abs(x.Rank - target))
            .ThenByDescending(x => x.Rank)
            .Select(x => x.Level)
            .DefaultIfEmpty(supported[0])
            .First();
    }

    /// <summary>
    /// Builds <see cref="ChatOptions.AdditionalProperties"/> enabling thinking with a model-aware reasoning effort.
    /// </summary>
    public static AdditionalPropertiesDictionary BuildReasoningProperties(
        LlmProviderOptions? options,
        string? modelId,
        string? requestedEffort = null,
        bool isOpenAi = false)
    {
        return new AdditionalPropertiesDictionary
        {
            ["think"] = true,
            ["reasoning_effort"] = ResolveReasoningEffort(options, modelId, requestedEffort, isOpenAi)
        };
    }

    /// <summary>
    /// Builds the Pi-style in-loop context compaction strategy sized for the model's context window.
    /// </summary>
    public static CompactionStrategy BuildCompactionStrategy(
        LlmProviderOptions? options,
        string? modelId,
        int? explicitContextWindowTokens = null,
        int? explicitReserveTokens = null,
        int? explicitKeepRecentTokens = null,
        IChatClient? summarizerClient = null)
    {
        var profile = FindProfile(options, modelId);
        int contextWindow = explicitContextWindowTokens
            ?? profile?.ContextWindowTokens
            ?? options?.ContextWindowTokens
            ?? LlmProviderOptions.DefaultContextWindowTokens;

        if (contextWindow <= 0)
        {
            contextWindow = LlmProviderOptions.DefaultContextWindowTokens;
        }

        bool isSmallWindow = contextWindow <= CrucibleCompactionOptions.SmallContextWindowTokens;

        int defaultReserve = isSmallWindow
            ? Math.Max(CrucibleCompactionOptions.MinSmallWindowBudgetTokens, contextWindow / 4)
            : CrucibleCompactionOptions.DefaultReserveTokens;

        // The reserve must always leave room for context: never let it reach the window itself.
        int reserveTokens = explicitReserveTokens ?? defaultReserve;
        if (reserveTokens >= contextWindow)
        {
            reserveTokens = Math.Max(1, contextWindow / 4);
        }

        int usableTokens = Math.Max(1, contextWindow - reserveTokens);

        int defaultKeepRecent = isSmallWindow
            ? Math.Max(CrucibleCompactionOptions.MinSmallWindowBudgetTokens, usableTokens / 2)
            : CrucibleCompactionOptions.DefaultKeepRecentTokens;

        int keepRecentTokens = explicitKeepRecentTokens ?? defaultKeepRecent;
        if (keepRecentTokens >= usableTokens)
        {
            keepRecentTokens = Math.Max(1, usableTokens / 2);
        }

        var compactionOptions = new CrucibleCompactionOptions
        {
            ContextWindowTokens = contextWindow,
            ReserveTokens = reserveTokens,
            KeepRecentTokens = keepRecentTokens,
            SummarizerModelId = modelId
        };

        return new PiStyleCrucibleCompactionStrategy(compactionOptions, summarizerClient);
    }
}
