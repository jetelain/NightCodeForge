using NightCodeForge.Core.Domain;

namespace NightCodeForge.Agents.Inference;

/// <summary>
/// Configuration for an explicitly permitted model on an OpenAI-compatible endpoint.
/// </summary>
public class OpenAiAllowedModel
{
    /// <summary>
    /// Exact model identifier required by the remote endpoint (e.g. "deepseek-chat", "gpt-4o", "meta-llama/llama-3.1-70b-instruct").
    /// </summary>
    public string ModelId { get; set; } = string.Empty;

    /// <summary>
    /// Human-friendly display label rendered in UI dropdowns and badges (e.g. "DeepSeek-V3 (64k)").
    /// If null or empty, defaults to ModelId.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Safe context window in tokens, used directly to size Crucible in-loop compaction.
    /// </summary>
    public int ContextWindowTokens { get; set; } = 131072;

    /// <summary>
    /// Maximum output token budget reserved from the context window (default: 8192).
    /// </summary>
    public int MaxOutputTokens { get; set; } = 8192;

    /// <summary>
    /// Indicates whether the model supports chain-of-thought or reasoning parameters (e.g. OpenAI o1/o3-mini, DeepSeek-R1).
    /// </summary>
    public bool SupportsReasoning { get; set; } = false;

    /// <summary>
    /// Default thinking effort level if reasoning is enabled (Low, Medium, High).
    /// </summary>
    public ThinkingEffort DefaultThinkingEffort { get; set; } = ThinkingEffort.Medium;

    /// <summary>
    /// Optional prompt pricing per million tokens (e.g. 0.14 for $0.14/M tokens), used to determine lowest cost default model.
    /// </summary>
    public decimal? PromptCostPerMillion { get; set; }

    /// <summary>
    /// Optional completion pricing per million tokens, used for cost evaluation.
    /// </summary>
    public decimal? CompletionCostPerMillion { get; set; }
}

/// <summary>
/// Configuration settings for the OpenAI-compatible inference provider.
/// </summary>
public class OpenAiCompatibleOptions
{
    /// <summary>
    /// Base URL of the OpenAI-compatible API (e.g. "http://localhost:1234/v1", "https://openrouter.ai/api/v1", "https://api.openai.com/v1").
    /// </summary>
    public string Endpoint { get; set; } = "https://api.openai.com/v1";

    /// <summary>
    /// Optional API key for authenticated endpoints. Can be injected via environment variable (e.g. OPENAI_API_KEY).
    /// May be omitted or empty for unauthenticated local endpoints (e.g. LM Studio, local vLLM).
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Optional organization identifier for multi-tenant OpenAI accounts.
    /// </summary>
    public string? OrganizationId { get; set; }

    /// <summary>
    /// Explicit allowlist / manual models configuration.
    /// Only required if the remote endpoint does not implement /api/v1/models (HTTP 404/501),
    /// or when an administrator explicitly restricts the discovered models or overrides context parameters.
    /// </summary>
    public List<OpenAiAllowedModel> AllowedModels { get; set; } = [];

    /// <summary>
    /// Default context window tokens used for discovered models if the endpoint returns model IDs
    /// without context_length metadata and no manual override is configured (default: 131,072).
    /// </summary>
    public int DefaultDiscoveredContextTokens { get; set; } = 131072;
}
