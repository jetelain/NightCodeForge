using System.Text.Json.Serialization;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Agents.Inference;

/// <summary>
/// Configuration options for local or remote LLM inference providers (Ollama / OllamaRouter / OpenAI-Compatible).
/// </summary>
public class LlmProviderOptions : ILlmProviderOptions
{
    public const string SectionName = "LlmProvider";

    /// <summary>
    /// LLM inference provider type: Standalone Ollama (single host), OllamaRouter (multi-node fleet), or OpenAiCompatible.
    /// </summary>
    public LlmProviderType ProviderType { get; set; } = LlmProviderType.OllamaRouter;

    /// <summary>
    /// Base HTTP/HTTPS endpoint of the Ollama or OllamaRouter server (defaults to http://localhost:11434).
    /// </summary>
    public string Endpoint { get; set; } = "http://localhost:11434";

    /// <summary>
    /// HTTP client request timeout in minutes to accommodate lengthy code generation and reasoning (default: 15 minutes).
    /// </summary>
    public double RequestTimeoutMinutes { get; set; } = 15;

    public const int DefaultContextWindowTokens = 131072;
    public const int DefaultMaxOutputTokens = 8192;

    /// <summary>
    /// Baseline context window in tokens (used as fallback when provider discovery is unavailable).
    /// </summary>
    public int ContextWindowTokens { get; set; } = DefaultContextWindowTokens;

    /// <summary>
    /// Output token budget reserved from the context window.
    /// </summary>
    public int MaxOutputTokens { get; set; } = DefaultMaxOutputTokens;

    /// <summary>
    /// Resolves the effective base endpoint URI based on the active provider.
    /// Prevents endpoint configuration disconnect between root and provider-specific sections.
    /// </summary>
    [JsonIgnore]
    public string EffectiveEndpoint =>
        ProviderType == LlmProviderType.OpenAiCompatible && !string.IsNullOrWhiteSpace(OpenAiCompatible?.Endpoint)
            ? OpenAiCompatible.Endpoint.Trim()
            : (Endpoint?.Trim() ?? string.Empty);

    /// <summary>
    /// Standardized default model identifier across pipeline stages.
    /// Optional: if omitted (null or empty), the system automatically selects the default model via DefaultModelResolver.
    /// </summary>
    public string? DefaultModelId { get; set; }

    /// <summary>
    /// Default thinking/reasoning effort across pipeline stages.
    /// </summary>
    public ThinkingEffort DefaultThinkingEffort { get; set; } = ThinkingEffort.Medium;

    /// <summary>
    /// Legacy ThinkingEffort property forwarding to DefaultThinkingEffort for backwards compatibility.
    /// </summary>
    [JsonIgnore]
    public ThinkingEffort ThinkingEffort
    {
        get => DefaultThinkingEffort;
        set => DefaultThinkingEffort = value;
    }

    /// <summary>
    /// Legacy ModelId property forwarding to DefaultModelId for backwards compatibility.
    /// </summary>
    [JsonIgnore]
    public string? ModelId
    {
        get => DefaultModelId;
        set => DefaultModelId = value;
    }

    /// <summary>
    /// Reasoning effort level for models supporting internal chain-of-thought (e.g. "xhigh", "high", "medium", "low").
    /// Defaults to "xhigh" for Qwen 3.8 models. When targeting OpenAI endpoints that only support "high", automatically mapped.
    /// </summary>
    public string? ReasoningEffort { get; set; } = "xhigh";

    /// <summary>
    /// Coder role model identifier for Stage 3 (implementation).
    /// </summary>
    public string? CoderModelId { get; set; }

    /// <summary>
    /// Coder role thinking effort for Stage 3 (implementation).
    /// </summary>
    public ThinkingEffort? CoderThinkingEffort { get; set; }

    /// <summary>
    /// Architect role model identifier for Stage 0 (triage), Stage 1 (task-review), Stage 5 (code-review-fix), and ideation.
    /// </summary>
    public string? ArchitectModelId { get; set; }

    /// <summary>
    /// Architect role thinking effort for Stage 0, Stage 1, Stage 5, and ideation.
    /// </summary>
    public ThinkingEffort? ArchitectThinkingEffort { get; set; }

    /// <summary>
    /// Provider-specific settings for OpenAI-compatible endpoints.
    /// </summary>
    public OpenAiCompatibleOptions OpenAiCompatible { get; set; } = new();

    /// <summary>
    /// Per-model capability overrides keyed by a case-insensitive substring of the model id
    /// (longest match wins). Configurable under LlmProvider:ModelProfiles in appsettings.json.
    /// </summary>
    public Dictionary<string, ModelCapabilityProfile> ModelProfiles { get; set; } = CreateDefaultModelProfiles();

    /// <summary>
    /// Built-in profiles. Qwen 3.8 accepts low/medium/xhigh only, so a requested "high" is mapped to "xhigh".
    /// </summary>
    public static Dictionary<string, ModelCapabilityProfile> CreateDefaultModelProfiles()
    {
        ModelCapabilityProfile Qwen38() => new()
        {
            SupportedEfforts = ["low", "medium", "xhigh"],
            EffortMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["high"] = "xhigh" },
            DefaultEffort = "xhigh"
        };

        return new Dictionary<string, ModelCapabilityProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["qwen3.8"] = Qwen38(),
            ["qwen-3.8"] = Qwen38(),
            ["Qwen3.8-27B:latest"] = Qwen38()
        };
    }
}
