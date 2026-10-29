using Microsoft.Extensions.AI;
using NightCodeForge.Core.Domain;

namespace NightCodeForge.Agents.Inference;

/// <summary>
/// Factory for resolving or creating IChatClient instances per agent profile and session,
/// using LlmProviderOptions as default configuration. The session id drives backend affinity
/// and KV-cache reuse on OllamaRouter; callers running multi-turn conversations must provide it.
/// </summary>
public interface ILlmChatClientFactory
{
    /// <summary>Creates or resolves a session-less IChatClient for the specified model identifier.</summary>
    IChatClient CreateClient(string? modelId = null);

    /// <summary>Creates or resolves a session-less IChatClient for the model identifier and routing target.</summary>
    IChatClient CreateClient(string? modelId, OllamaRoutingTarget routingTarget);

    /// <summary>Creates or resolves a session-less IChatClient for the specified AgentProfile.</summary>
    IChatClient CreateClient(AgentProfile? profile);

    /// <summary>Creates an IChatClient for the model identifier, routing target and session identifier.</summary>
    IChatClient CreateClient(string? modelId, OllamaRoutingTarget routingTarget, string? sessionId);

    /// <summary>Creates an IChatClient for the specified AgentProfile and session identifier.</summary>
    IChatClient CreateClient(AgentProfile? profile, string? sessionId);
}
