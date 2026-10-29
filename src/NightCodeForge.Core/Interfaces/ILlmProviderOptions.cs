using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Interfaces;

/// <summary>
/// Core configuration options interface for LLM inference providers.
/// </summary>
public interface ILlmProviderOptions
{
    LlmProviderType ProviderType { get; }
    string DefaultModelId { get; }
    ThinkingEffort DefaultThinkingEffort { get; }
    string? CoderModelId { get; }
    ThinkingEffort? CoderThinkingEffort { get; }
    string? ArchitectModelId { get; }
    ThinkingEffort? ArchitectThinkingEffort { get; }
}

/// <summary>
/// Default or custom implementation of <see cref="ILlmProviderOptions"/> for setup, convenience, and testing.
/// </summary>
public record DefaultLlmProviderOptions(
    string DefaultModelId = "Qwen3.8-27B:latest",
    ThinkingEffort DefaultThinkingEffort = ThinkingEffort.Medium,
    string? CoderModelId = null,
    ThinkingEffort? CoderThinkingEffort = null,
    string? ArchitectModelId = null,
    ThinkingEffort? ArchitectThinkingEffort = null,
    LlmProviderType ProviderType = LlmProviderType.OllamaRouter) : ILlmProviderOptions;
