namespace NightCodeForge.Core.Domain;

/// <summary>
/// Configuration settings for web search providers (e.g. Ollama Web Search API).
/// Persisted in the SQLite SystemSettings table or inherited from environment variables.
/// </summary>
public class WebSearchSettings
{
    public const string DefaultEndpoint = "https://ollama.com/api/web_search";

    /// <summary>
    /// Search provider name (default: "Ollama").
    /// </summary>
    public string Provider { get; set; } = "Ollama";

    /// <summary>
    /// API key for authentication with the search provider (e.g. Ollama cloud API key).
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Web search API endpoint URL (default: "https://ollama.com/api/web_search").
    /// </summary>
    public string Endpoint { get; set; } = DefaultEndpoint;

    /// <summary>
    /// Default maximum results returned per search query (1 to 10).
    /// </summary>
    public int DefaultMaxResults { get; set; } = 5;

    public WebSearchSettings Clone() => new()
    {
        Provider = Provider,
        ApiKey = ApiKey,
        Endpoint = Endpoint,
        DefaultMaxResults = DefaultMaxResults
    };
}
