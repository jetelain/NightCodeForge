namespace NightCodeForge.Core.Domain;

/// <summary>
/// Configuration settings for web search providers (e.g. Ollama Web Search API).
/// Non-secret parameters are persisted in the SQLite SystemSettings table; API keys are stored
/// separately in the native OS keyring via <see cref="Interfaces.IApiKeySecretStore"/> or via <c>OLLAMA_API_KEY</c>.
/// </summary>
public class WebSearchSettings
{
    public const string DefaultProvider = "Ollama";
    public const string DefaultEndpoint = "https://ollama.com/api/web_search";
    public const string OllamaApiKeyEnvironmentVariable = "OLLAMA_API_KEY";
    public const int DefaultMaxResultsCount = 5;
    public const int MinMaxResultsCount = 1;
    public const int MaxMaxResultsCount = 10;

    /// <summary>
    /// Search provider name (default: "Ollama").
    /// </summary>
    public string Provider { get; set; } = DefaultProvider;

    /// <summary>
    /// Web search API endpoint URL (default: "https://ollama.com/api/web_search").
    /// </summary>
    public string Endpoint { get; set; } = DefaultEndpoint;

    /// <summary>
    /// Default maximum results returned per search query (1 to 10).
    /// </summary>
    public int DefaultMaxResults { get; set; } = DefaultMaxResultsCount;

    public WebSearchSettings Clone() => new()
    {
        Provider = Provider,
        Endpoint = Endpoint,
        DefaultMaxResults = DefaultMaxResults
    };
}
