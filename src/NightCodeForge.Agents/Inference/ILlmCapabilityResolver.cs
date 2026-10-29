using Microsoft.Extensions.AI;
using NightCodeForge.Core.Domain;

namespace NightCodeForge.Agents.Inference;

/// <summary>
/// Resolves provider- and model-specific capabilities such as reasoning effort,
/// thinking parameters, and default chat options polymorphically.
/// Prevents provider-specific branching in agent harnesses and ideation services.
/// </summary>
public interface ILlmCapabilityResolver
{
    /// <summary>
    /// Resolves the provider-appropriate reasoning effort level for the given model.
    /// </summary>
    string ResolveReasoningEffort(string? modelId, string? requestedEffort = null);

    /// <summary>
    /// Builds provider-tailored reasoning properties (e.g. "think" flag for Ollama, "reasoning_effort" for OpenAI).
    /// </summary>
    AdditionalPropertiesDictionary BuildReasoningProperties(string? modelId, string? requestedEffort = null);

    /// <summary>
    /// Creates provider-tailored ChatOptions initialized with reasoning effort, temperature, and thinking parameters.
    /// </summary>
    ChatOptions CreateChatOptions(AgentProfile? profile, string? modelId = null);
}

