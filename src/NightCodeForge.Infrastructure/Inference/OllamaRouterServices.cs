using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Infrastructure.Inference;

public class OllamaRouterOptions
{
    public string BaseUrl { get; set; } = "http://localhost:11434";
    public int TimeoutSeconds { get; set; } = 30;
}

public class OllamaModelCatalogService : IOllamaModelCatalogService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<OllamaModelCatalogService> _logger;

    public OllamaModelCatalogService(
        HttpClient httpClient,
        IOptions<OllamaRouterOptions>? options = null,
        ILogger<OllamaModelCatalogService>? logger = null)
    {
        _httpClient = httpClient;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<OllamaModelCatalogService>.Instance;

        var baseUrl = options?.Value.BaseUrl ?? "http://localhost:11434";
        if (_httpClient.BaseAddress == null)
        {
            _httpClient.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        }
    }

    public async Task<IReadOnlyList<OllamaModelInfo>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _httpClient.GetFromJsonAsync<OllamaTagsResponse>("api/tags", cancellationToken);
            if (response?.Models == null)
            {
                return Array.Empty<OllamaModelInfo>();
            }

            return response.Models.Select(m => new OllamaModelInfo(
                Name: m.Name ?? string.Empty,
                Model: m.Model,
                Size: m.Size,
                ModifiedAt: m.ModifiedAt,
                Digest: m.Digest,
                Details: m.Details != null ? JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(m.Details)) : null
            )).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query OllamaRouter model tags from {BaseAddress}", _httpClient.BaseAddress);
            return Array.Empty<OllamaModelInfo>();
        }
    }

    private class OllamaTagsResponse
    {
        [JsonPropertyName("models")]
        public List<OllamaTagItem>? Models { get; set; }
    }

    private class OllamaTagItem
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("model")]
        public string? Model { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("digest")]
        public string? Digest { get; set; }

        [JsonPropertyName("modified_at")]
        public DateTime? ModifiedAt { get; set; }

        [JsonPropertyName("details")]
        public JsonElement? Details { get; set; }
    }
}

public class OllamaModelPreloadService : IOllamaModelPreloadService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<OllamaModelPreloadService> _logger;

    public OllamaModelPreloadService(
        HttpClient httpClient,
        IOptions<OllamaRouterOptions>? options = null,
        ILogger<OllamaModelPreloadService>? logger = null)
    {
        _httpClient = httpClient;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<OllamaModelPreloadService>.Instance;

        var baseUrl = options?.Value.BaseUrl ?? "http://localhost:11434";
        if (_httpClient.BaseAddress == null)
        {
            _httpClient.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        }
    }

    public async Task<bool> PreloadModelAsync(string modelName, string keepAlive = "15m", CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return false;
        }

        try
        {
            _logger.LogInformation("Triggering JIT model preload for '{ModelName}' with keep_alive={KeepAlive}", modelName, keepAlive);
            var payload = new
            {
                model = modelName,
                keep_alive = keepAlive
            };

            var response = await _httpClient.PostAsJsonAsync("api/generate", payload, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "JIT model preload failed for '{ModelName}'", modelName);
            return false;
        }
    }
}
