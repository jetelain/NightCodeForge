using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Agents.Tools;

/// <summary>
/// Web search service backed by the official Ollama Web Search API (https://ollama.com/api/web_search).
/// Supports credentials via IWebSearchSettingsService or the OLLAMA_API_KEY environment variable.
/// </summary>
public class OllamaWebSearchService : IWebSearchService
{
    private readonly HttpClient _httpClient;
    private readonly IWebSearchSettingsService? _settingsService;
    private readonly ILogger<OllamaWebSearchService> _logger;
    private readonly string? _explicitApiKey;
    private readonly string? _explicitEndpoint;

    public string ProviderName => "Ollama";

    /// <summary>
    /// Indicates whether an API key is configured either in settings or via environment variable.
    /// </summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(GetEffectiveApiKey());

    public OllamaWebSearchService(
        HttpClient httpClient,
        IWebSearchSettingsService? settingsService = null,
        ILogger<OllamaWebSearchService>? logger = null,
        string? explicitApiKey = null,
        string? explicitEndpoint = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _settingsService = settingsService;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<OllamaWebSearchService>.Instance;
        _explicitApiKey = explicitApiKey;
        _explicitEndpoint = explicitEndpoint;
    }

    /// <inheritdoc />
    public async Task<WebSearchResponse> SearchAsync(string query, int maxResults = 5, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return new WebSearchResponse(Array.Empty<WebSearchResultItem>());
        }

        var apiKey = GetEffectiveApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("Ollama search attempted but OLLAMA_API_KEY is not configured.");
            throw new InvalidOperationException("OLLAMA_API_KEY is not configured. Please configure an API key in Settings or set the OLLAMA_API_KEY environment variable.");
        }

        var endpoint = GetEffectiveEndpoint();
        var clampedMax = Math.Clamp(maxResults, 1, 10);

        _logger.LogInformation("[Ollama Search] Searching for: {Query} (max results: {MaxResults})", query, clampedMax);

        var requestBody = new OllamaSearchRequestBody
        {
            Query = query,
            MaxResults = clampedMax
        };

        var requestJson = JsonSerializer.Serialize(requestBody);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");

        var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorText = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("[Ollama Search] API error {StatusCode}: {ErrorText}", (int)response.StatusCode, errorText);
            throw new HttpRequestException($"Ollama Search API error: {(int)response.StatusCode} {response.ReasonPhrase}. Details: {errorText}", null, response.StatusCode);
        }

        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        var searchResponse = JsonSerializer.Deserialize<OllamaSearchResponseBody>(responseJson, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        var results = searchResponse?.Results?
            .Select(r => new WebSearchResultItem(r.Title ?? string.Empty, r.Url ?? string.Empty, r.Content ?? string.Empty))
            .ToList() ?? new List<WebSearchResultItem>();

        _logger.LogInformation("[Ollama Search] Found {Count} results for '{Query}'", results.Count, query);

        return new WebSearchResponse(results);
    }

    /// <summary>
    /// Performs a lightweight connectivity test with the Ollama Web Search API.
    /// </summary>
    public async Task<(bool Success, string Message, int ResultCount, long ElapsedMilliseconds)> TestConnectionAsync(
        string testQuery = "Ollama",
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var response = await SearchAsync(testQuery, 2, cancellationToken);
            sw.Stop();
            return (true, $"Successfully connected to Ollama Web Search API ({response.Results.Count} results returned in {sw.ElapsedMilliseconds}ms).", response.Results.Count, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return (false, ex.Message, 0, sw.ElapsedMilliseconds);
        }
    }

    public string? GetEffectiveApiKey()
    {
        if (!string.IsNullOrWhiteSpace(_explicitApiKey)) return _explicitApiKey;
        var settingsKey = _settingsService?.GetSettings().ApiKey;
        if (!string.IsNullOrWhiteSpace(settingsKey)) return settingsKey;
        return Environment.GetEnvironmentVariable("OLLAMA_API_KEY");
    }

    public string GetEffectiveEndpoint()
    {
        if (!string.IsNullOrWhiteSpace(_explicitEndpoint)) return _explicitEndpoint;
        var settingsEndpoint = _settingsService?.GetSettings().Endpoint;
        if (!string.IsNullOrWhiteSpace(settingsEndpoint)) return settingsEndpoint;
        return WebSearchSettings.DefaultEndpoint;
    }

    private class OllamaSearchRequestBody
    {
        [JsonPropertyName("query")]
        public string Query { get; set; } = string.Empty;

        [JsonPropertyName("max_results")]
        public int MaxResults { get; set; }
    }

    private class OllamaSearchResponseBody
    {
        [JsonPropertyName("results")]
        public List<OllamaSearchItemDto>? Results { get; set; }
    }

    private class OllamaSearchItemDto
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("content")]
        public string? Content { get; set; }
    }
}
