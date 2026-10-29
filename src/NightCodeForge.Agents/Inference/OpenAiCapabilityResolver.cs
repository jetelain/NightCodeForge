using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Agents.Inference;

/// <summary>
/// Capability resolver specialized for OpenAI-compatible inference endpoints (vLLM, OpenRouter, OpenAI):
/// Configures reasoning effort adhering strictly to the standard <c>reasoning_effort</c> parameter
/// ("high", "medium", "low") without emitting Ollama-specific <c>think: true</c> tags.
/// </summary>
public sealed class OpenAiCapabilityResolver : ILlmCapabilityResolver
{
    private readonly IOptions<LlmProviderOptions> _options;

    public OpenAiCapabilityResolver(IOptions<LlmProviderOptions> options)
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

        var val = string.IsNullOrWhiteSpace(requested) ? "high" : requested.Trim().ToLowerInvariant();

        // OpenAI reasoning_effort standard: normalize xhigh to high, minimal to low
        if (val == "xhigh")
        {
            return "high";
        }
        if (val == "minimal")
        {
            return "low";
        }

        if (profile?.EffortMap.TryGetValue(val, out var mapped) == true && !string.IsNullOrWhiteSpace(mapped))
        {
            var mappedVal = mapped.Trim().ToLowerInvariant();
            if (mappedVal == "xhigh") return "high";
            if (mappedVal == "minimal") return "low";
            return mappedVal is "low" or "medium" or "high" ? mappedVal : "high";
        }

        return val is "low" or "medium" or "high" ? val : "high";
    }

    public AdditionalPropertiesDictionary BuildReasoningProperties(string? modelId, string? requestedEffort = null)
    {
        return new AdditionalPropertiesDictionary
        {
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

        // If profile explicitly requests ThinkingEffort.None, do not emit reasoning properties
        if (profile?.ThinkingEffort == ThinkingEffort.None)
        {
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
