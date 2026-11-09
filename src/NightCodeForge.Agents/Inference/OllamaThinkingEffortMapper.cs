using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Agents.Inference;

/// <summary>
/// Deterministic mapper translating NightCodeForge <see cref="ThinkingEffort"/> and string effort levels
/// to Ollama's native <c>think</c> parameter values based on dynamically discovered <see cref="ModelThinkingCapability"/>.
/// </summary>
public static class OllamaThinkingEffortMapper
{
    private static readonly string[] CanonicalRank = ["minimal", "low", "medium", "high", "xhigh", "maximum"];

    /// <summary>
    /// Maps a <see cref="ThinkingEffort"/> enum value to the model's native <c>think</c> parameter value.
    /// Returns <c>null</c> when thinking is unsupported (to omit the parameter),
    /// a <see cref="bool"/> (<c>false</c> or <c>true</c>) for boolean toggles,
    /// or a <see cref="string"/> level (e.g. "low", "medium", "xhigh").
    /// </summary>
    public static object? MapThinkingEffort(ThinkingEffort effort, ModelThinkingCapability capability)
    {
        ArgumentNullException.ThrowIfNull(capability);

        if (!capability.SupportsThinking)
        {
            return null;
        }

        if (effort == ThinkingEffort.None)
        {
            return false;
        }

        var validLevels = GetNormalizedLevels(capability.StringLevels);

        if (validLevels.Count == 0)
        {
            return capability.SupportsBooleanOnly ? true : null;
        }

        var orderedLevels = OrderByCanonicalRank(validLevels);

        return effort switch
        {
            ThinkingEffort.Low => ResolveLow(validLevels, orderedLevels),
            ThinkingEffort.Medium => ResolveMedium(capability.DefaultLevel, validLevels, orderedLevels),
            ThinkingEffort.High or ThinkingEffort.CustomBudget => ResolveHigh(validLevels, orderedLevels),
            _ => ResolveMedium(capability.DefaultLevel, validLevels, orderedLevels)
        };
    }

    /// <summary>
    /// Resolves a requested string effort against the model's supported levels.
    /// </summary>
    public static string ResolveReasoningEffort(string? requestedEffort, ModelThinkingCapability capability)
    {
        ArgumentNullException.ThrowIfNull(capability);

        if (!capability.SupportsThinking)
        {
            return string.Empty;
        }

        var validLevels = GetNormalizedLevels(capability.StringLevels);
        if (validLevels.Count == 0)
        {
            return capability.SupportsBooleanOnly ? "true" : string.Empty;
        }

        var orderedLevels = OrderByCanonicalRank(validLevels);

        if (string.IsNullOrWhiteSpace(requestedEffort))
        {
            return ResolveMedium(capability.DefaultLevel, validLevels, orderedLevels);
        }

        var requested = requestedEffort.Trim().ToLowerInvariant();

        // "high", "xhigh", "maximum" requesting high effort:
        // if requested is "xhigh" or "maximum" and directly present, return it;
        // otherwise map to the highest supported level (e.g. "xhigh" if present, else "high", else highest).
        if (IsHighRank(requested))
        {
            if (requested is "xhigh" or "maximum" && validLevels.Contains(requested))
            {
                return requested;
            }

            return ResolveHigh(validLevels, orderedLevels);
        }

        if (validLevels.Contains(requested))
        {
            return requested;
        }

        int target = Array.IndexOf(CanonicalRank, requested);
        if (target < 0)
        {
            return orderedLevels[0];
        }

        return orderedLevels
            .Select(s => (Level: s, Rank: Array.IndexOf(CanonicalRank, s)))
            .Where(x => x.Rank >= 0)
            .OrderBy(x => Math.Abs(x.Rank - target))
            .ThenByDescending(x => x.Rank)
            .Select(x => x.Level)
            .DefaultIfEmpty(orderedLevels[0])
            .First();
    }

    private static bool IsHighRank(string effort) =>
        effort is "high" or "xhigh" or "maximum";

    private static string ResolveLow(List<string> validLevels, List<string> orderedLevels)
    {
        if (validLevels.Contains("low"))
        {
            return "low";
        }

        return orderedLevels[0];
    }

    private static string ResolveMedium(string? defaultLevel, List<string> validLevels, List<string> orderedLevels)
    {
        if (!string.IsNullOrWhiteSpace(defaultLevel))
        {
            var def = defaultLevel.Trim().ToLowerInvariant();
            if (validLevels.Contains(def))
            {
                return def;
            }
        }

        if (validLevels.Contains("medium"))
        {
            return "medium";
        }

        int medianIndex = (orderedLevels.Count - 1) / 2;
        return orderedLevels[medianIndex];
    }

    private static string ResolveHigh(List<string> validLevels, List<string> orderedLevels)
    {
        // Prefer highest canonical rank ("maximum" > "xhigh" > "high"), otherwise highest in orderedLevels
        for (int i = CanonicalRank.Length - 1; i >= 0; i--)
        {
            var rankLevel = CanonicalRank[i];
            if (validLevels.Contains(rankLevel))
            {
                return rankLevel;
            }
        }

        return orderedLevels[^1];
    }

    private static List<string> GetNormalizedLevels(IReadOnlyList<string>? levels)
    {
        if (levels == null || levels.Count == 0)
        {
            return [];
        }

        return levels
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim().ToLowerInvariant())
            .Distinct()
            .ToList();
    }

    private static List<string> OrderByCanonicalRank(List<string> validLevels)
    {
        return validLevels
            .OrderBy(s =>
            {
                var r = Array.IndexOf(CanonicalRank, s);
                return r >= 0 ? r : CanonicalRank.Length;
            })
            .ToList();
    }
}

