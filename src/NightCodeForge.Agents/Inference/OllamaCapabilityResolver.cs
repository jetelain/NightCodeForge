using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Agents.Inference;

/// <summary>
/// Capability resolver specialized for Ollama and OllamaRouter:
/// Configures thinking/reasoning properties using dynamic capability discovery (<see cref="ModelThinkingCapability"/>)
/// and maps reasoning effort to Ollama/Qwen supported levels ("low", "medium", "xhigh") via <see cref="OllamaThinkingEffortMapper"/>,
/// falling back to static model profiles when capabilities are uncached.
/// </summary>
public sealed class OllamaCapabilityResolver : ILlmCapabilityResolver
{
    private static readonly string[] EffortRank = ["minimal", "low", "medium", "high", "xhigh", "maximum"];
    private readonly IOptions<LlmProviderOptions> _options;
    private readonly ILlmContextWindowCache _capabilityCache;

    public OllamaCapabilityResolver(IOptions<LlmProviderOptions> options, ILlmContextWindowCache capabilityCache)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _capabilityCache = capabilityCache ?? throw new ArgumentNullException(nameof(capabilityCache));
    }

    public string ResolveReasoningEffort(string? modelId, string? requestedEffort = null)
    {
        if (!string.IsNullOrWhiteSpace(modelId)
            && _capabilityCache.TryGetThinkingCapability(modelId, out var capability)
            && ShouldUseDynamicCapability(modelId, capability))
        {
            if (capability.StringLevels.Count > 0)
            {
                var effortToResolve = !string.IsNullOrWhiteSpace(requestedEffort)
                    ? requestedEffort
                    : _options.Value.ReasoningEffort;
                return OllamaThinkingEffortMapper.ResolveReasoningEffort(effortToResolve, capability);
            }

            if (capability.SupportsBooleanOnly)
            {
                return "true";
            }

            if (!capability.SupportsThinking)
            {
                return string.Empty;
            }
        }

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
        if (!string.IsNullOrWhiteSpace(modelId)
            && _capabilityCache.TryGetThinkingCapability(modelId, out var capability)
            && ShouldUseDynamicCapability(modelId, capability))
        {
            if (!capability.SupportsThinking)
            {
                return new AdditionalPropertiesDictionary();
            }

            if (capability.SupportsBooleanOnly && capability.StringLevels.Count == 0)
            {
                bool enableThink = !string.Equals(requestedEffort?.Trim(), "false", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(requestedEffort?.Trim(), "none", StringComparison.OrdinalIgnoreCase);
                return new AdditionalPropertiesDictionary
                {
                    ["think"] = enableThink
                };
            }

            if (capability.StringLevels.Count > 0)
            {
                if (string.Equals(requestedEffort?.Trim(), "false", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(requestedEffort?.Trim(), "none", StringComparison.OrdinalIgnoreCase))
                {
                    return new AdditionalPropertiesDictionary
                    {
                        ["think"] = false
                    };
                }

                var actual = !string.IsNullOrWhiteSpace(requestedEffort)
                    ? requestedEffort
                    : _options.Value.ReasoningEffort;

                var resolved = OllamaThinkingEffortMapper.ResolveReasoningEffort(actual, capability);
                return new AdditionalPropertiesDictionary
                {
                    ["think"] = resolved
                };
            }
        }

        // https://docs.ollama.com/capabilities/thinking
        // think = false, true (default effort), or the effort

        var actualEffort = !string.IsNullOrWhiteSpace(requestedEffort)
            ? requestedEffort
            : _options.Value.ReasoningEffort;

        if (string.IsNullOrWhiteSpace(actualEffort))
        {
            return new AdditionalPropertiesDictionary
            {
                ["think"] = true
            };
        }

        return new AdditionalPropertiesDictionary
        {
            ["think"] = ResolveReasoningEffort(modelId, actualEffort)
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

        if (!string.IsNullOrWhiteSpace(effectiveModel)
            && _capabilityCache.TryGetThinkingCapability(effectiveModel, out var capability)
            && ShouldUseDynamicCapability(effectiveModel, capability))
        {
            if (profile != null)
            {
                var mapped = OllamaThinkingEffortMapper.MapThinkingEffort(profile.ThinkingEffort, capability);
                if (mapped != null)
                {
                    chatOptions.AdditionalProperties = new AdditionalPropertiesDictionary
                    {
                        ["think"] = mapped
                    };
                }
            }
            else
            {
                var props = BuildReasoningProperties(effectiveModel, null);
                if (props.Count > 0)
                {
                    chatOptions.AdditionalProperties = props;
                }
            }

            return chatOptions;
        }

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

    private bool ShouldUseDynamicCapability(string modelId, ModelThinkingCapability capability)
    {
        var staticProfile = ModelCapabilityResolver.FindProfile(_options.Value, modelId);
        bool hasStaticEfforts = staticProfile != null && staticProfile.SupportedEfforts.Count > 0;
        return !(capability.SupportsBooleanOnly && capability.StringLevels.Count == 0 && hasStaticEfforts);
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
