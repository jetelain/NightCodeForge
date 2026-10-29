using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Agents.Inference;

/// <summary>
/// Capability resolver specialized for Ollama and OllamaRouter:
/// Configures thinking/reasoning properties using Ollama's <c>think: true</c> parameter
/// and maps reasoning effort to Ollama/Qwen supported levels ("low", "medium", "xhigh").
/// </summary>
public sealed class OllamaCapabilityResolver : ILlmCapabilityResolver
{
    private static readonly string[] EffortRank = ["minimal", "low", "medium", "high", "xhigh"];
    private readonly IOptions<LlmProviderOptions> _options;

    public OllamaCapabilityResolver(IOptions<LlmProviderOptions> options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public string ResolveReasoningEffort(string? modelId, string? requestedEffort = null)
    {
        var options = _options.Value;
        var profile = ModelCapabilityResolver.FindProfile(options, modelId);
        var requested = !string.IsNullOrWhiteSpace(requestedEffort)
            ? requestedEffort
            : (!string.IsNullOrWhiteSpace(options.ReasoningEffort) ? options.ReasoningEffort : profile?.DefaultEffort);

        var val = string.IsNullOrWhiteSpace(requested) ? "xhigh" : requested.Trim().ToLowerInvariant();

        if (profile == null)
        {
            return val;
        }

        if (profile.EffortMap.TryGetValue(val, out var mapped) && !string.IsNullOrWhiteSpace(mapped))
        {
            return mapped.Trim().ToLowerInvariant();
        }

        var supported = profile.SupportedEfforts
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim().ToLowerInvariant())
            .ToList();

        if (supported.Count == 0 || supported.Contains(val))
        {
            return val;
        }

        int target = Array.IndexOf(EffortRank, val);
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

    public AdditionalPropertiesDictionary BuildReasoningProperties(string? modelId, string? requestedEffort = null)
    {
        return new AdditionalPropertiesDictionary
        {
            ["think"] = true,
            ["reasoning_effort"] = ResolveReasoningEffort(modelId, requestedEffort)
        };
    }

    public ChatOptions CreateChatOptions(AgentProfile? profile, string? modelId = null)
    {
        var effectiveModel = !string.IsNullOrWhiteSpace(modelId)
            ? modelId.Trim()
            : (!string.IsNullOrWhiteSpace(profile?.ModelName) ? profile.ModelName.Trim() : _options.Value.DefaultModelId?.Trim());

        var chatOptions = new ChatOptions
        {
            ModelId = effectiveModel,
            Temperature = (float?)profile?.Temperature
        };

        if (profile?.ThinkingEffort == ThinkingEffort.None)
        {
            chatOptions.AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["think"] = false
            };
            return chatOptions;
        }

        var requestedEffort = profile != null
            ? MapThinkingEffort(profile.ThinkingEffort)
            : null;

        chatOptions.AdditionalProperties = BuildReasoningProperties(effectiveModel, requestedEffort);
        return chatOptions;
    }

    private static string? MapThinkingEffort(ThinkingEffort effort) => effort switch
    {
        ThinkingEffort.Low => "low",
        ThinkingEffort.Medium => "medium",
        ThinkingEffort.High => "high",
        ThinkingEffort.CustomBudget => "high",
        _ => null
    };
}
