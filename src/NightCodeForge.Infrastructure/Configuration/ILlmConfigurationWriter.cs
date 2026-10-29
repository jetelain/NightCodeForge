using NightCodeForge.Agents.Inference;

namespace NightCodeForge.Infrastructure.Configuration;

/// <summary>
/// Service responsible for persisting LLM provider and model configuration to the user-owned
/// config/config.json file, honoring the layered configuration pattern.
/// </summary>
public interface ILlmConfigurationWriter
{
    /// <summary>
    /// Mutates the active LLM provider options and updates the "LlmProvider" section in config/config.json.
    /// </summary>
    Task SaveProviderConfigurationAsync(Action<LlmProviderOptions> updateAction, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves the collection of manually configured allowed models for the OpenAI-compatible provider.
    /// </summary>
    Task SaveAllowedModelsAsync(IEnumerable<OpenAiAllowedModel> allowedModels, CancellationToken cancellationToken = default);
}
