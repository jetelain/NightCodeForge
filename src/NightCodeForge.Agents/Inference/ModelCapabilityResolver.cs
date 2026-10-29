using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using NightCodeForge.Agents.Compaction;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Agents.Inference;

/// <summary>
/// Model capability utilities for in-loop Crucible context compaction sizing and model profile matching.
/// Provider-specific reasoning formatting and chat options are resolved polymorphically via <see cref="ILlmCapabilityResolver"/>.
/// </summary>
public static class ModelCapabilityResolver
{
    /// <summary>
    /// Builds the Pi-style in-loop context compaction strategy sized for the agent profile's model and context window.
    /// In multi-node fleet environments, the context window should reflect the largest capability of the fleet
    /// to prevent premature compaction while capable larger nodes or cloud overflow can service the context.
    /// </summary>
    public static CompactionStrategy BuildCompactionStrategy(
        AgentProfile profile,
        LlmProviderOptions? options = null,
        int? explicitContextWindowTokens = null,
        int? explicitReserveTokens = null,
        int? explicitKeepRecentTokens = null,
        IChatClient? summarizerClient = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        int? windowTokens = explicitContextWindowTokens ?? profile.ContextWindowTokens;
        var modelId = !string.IsNullOrWhiteSpace(profile.ModelName) ? profile.ModelName : options?.DefaultModelId;
        return BuildCompactionStrategy(
            options,
            modelId,
            explicitContextWindowTokens: windowTokens,
            explicitReserveTokens: explicitReserveTokens,
            explicitKeepRecentTokens: explicitKeepRecentTokens,
            summarizerClient: summarizerClient);
    }

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
        string resolutionSource;
        int contextWindow;

        if (explicitContextWindowTokens.HasValue && explicitContextWindowTokens.Value > 0)
        {
            contextWindow = explicitContextWindowTokens.Value;
            resolutionSource = "AgentProfile";
        }
        else if (profile?.ContextWindowTokens is { } profileTokens && profileTokens > 0)
        {
            contextWindow = profileTokens;
            resolutionSource = "ModelCapabilityProfile";
        }
        else if (options?.ContextWindowTokens is { } optionsTokens && optionsTokens > 0)
        {
            contextWindow = optionsTokens;
            resolutionSource = "LlmProviderOptions (appsettings.json)";
        }
        else
        {
            contextWindow = LlmProviderOptions.DefaultContextWindowTokens;
            resolutionSource = "Default (128k fallback)";
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
            SummarizerModelId = modelId,
            ResolutionSource = resolutionSource
        };

        return new PiStyleCrucibleCompactionStrategy(compactionOptions, summarizerClient);
    }

    /// <summary>
    /// Legacy fallback method forwarding to <see cref="OllamaCapabilityResolver"/>.
    /// New code should inject <see cref="ILlmCapabilityResolver"/> polymorphically.
    /// </summary>
    public static string ResolveReasoningEffort(
        LlmProviderOptions? options,
        string? modelId,
        string? requestedEffort = null)
    {
        var resolver = new OllamaCapabilityResolver(Options.Create(options ?? new LlmProviderOptions()));
        return resolver.ResolveReasoningEffort(modelId, requestedEffort);
    }

    /// <summary>
    /// Legacy fallback method forwarding to <see cref="OllamaCapabilityResolver"/>.
    /// New code should inject <see cref="ILlmCapabilityResolver"/> polymorphically.
    /// </summary>
    public static AdditionalPropertiesDictionary BuildReasoningProperties(
        LlmProviderOptions? options,
        string? modelId,
        string? requestedEffort = null)
    {
        var resolver = new OllamaCapabilityResolver(Options.Create(options ?? new LlmProviderOptions()));
        return resolver.BuildReasoningProperties(modelId, requestedEffort);
    }
}
