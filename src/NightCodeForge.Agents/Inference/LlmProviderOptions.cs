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
}
