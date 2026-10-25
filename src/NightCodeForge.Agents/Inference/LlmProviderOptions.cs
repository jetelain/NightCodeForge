namespace NightCodeForge.Agents.Inference;

/// <summary>
/// Configuration options for local or remote LLM inference providers (Ollama / OllamaRouter).
/// </summary>
public class LlmProviderOptions
{
    public const string SectionName = "LlmProvider";

    /// <summary>
    /// Base HTTP/HTTPS endpoint of the Ollama or OllamaRouter server (defaults to http://localhost:11434).
    /// </summary>
    public string Endpoint { get; set; } = "http://localhost:11434";

    /// <summary>
    /// Default model identifier (e.g. qwen2.5-coder:7b or qwen2.5-coder:32b).
    /// </summary>
    public string ModelId { get; set; } = "qwen2.5-coder:7b";

    /// <summary>
    /// HTTP client request timeout in minutes to accommodate lengthy code generation and reasoning (default: 15 minutes).
    /// </summary>
    public double RequestTimeoutMinutes { get; set; } = 15;

    /// <summary>
    /// Reasoning effort level for models supporting internal chain-of-thought (e.g. "xhigh", "high", "medium", "low").
    /// Defaults to "xhigh" for Qwen 3.8 models. When targeting OpenAI endpoints that only support "high", automatically mapped.
    /// </summary>
    public string? ReasoningEffort { get; set; } = "xhigh";

    public const int DefaultContextWindowTokens = 131072;
    public const int DefaultMaxOutputTokens = 8192;

    /// <summary>
    /// Default model context window in tokens, used to size MAF in-loop compaction.
    /// </summary>
    public int ContextWindowTokens { get; set; } = DefaultContextWindowTokens;

    /// <summary>
    /// Output token budget reserved from the context window when sizing MAF in-loop compaction.
    /// </summary>
    public int MaxOutputTokens { get; set; } = DefaultMaxOutputTokens;

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
            ["qwen-3.8"] = Qwen38()
        };
    }
}
