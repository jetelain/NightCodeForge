using Microsoft.Extensions.AI;
using NightCodeForge.Core.Domain;

namespace NightCodeForge.Agents.Inference;

/// <summary>
/// Factory for resolving or creating IChatClient instances per agent profile,
/// using LlmProviderOptions (configured via appsettings.json) as default configuration.
/// </summary>
public interface IOllamaChatClientFactory
{
    /// <summary>
    /// Creates or resolves an IChatClient for the specified model identifier,
    /// falling back to the default configured ModelId if null or empty.
    /// </summary>
    IChatClient CreateClient(string? modelId = null);

    /// <summary>
    /// Creates or resolves an IChatClient for the specified model identifier and routing target,
    /// falling back to the default configured ModelId if null or empty.
    /// </summary>
    IChatClient CreateClient(string? modelId, OllamaRoutingTarget routingTarget);

    /// <summary>
    /// Creates or resolves an IChatClient for the specified AgentProfile,
    /// using profile.ModelName if provided, or falling back to the default configured ModelId.
    /// </summary>
    IChatClient CreateClient(AgentProfile? profile);
}
