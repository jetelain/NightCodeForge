using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Infrastructure.Inference;

public class OllamaRouterGatewayService : IOllamaRouterGatewayService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<OllamaRouterGatewayService> _logger;
    private readonly string _baseUrl;

    public OllamaRouterGatewayService(
        HttpClient httpClient,
        IOptions<OllamaRouterOptions> options,
        ILogger<OllamaRouterGatewayService> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ArgumentNullException.ThrowIfNull(options);

        _baseUrl = !string.IsNullOrWhiteSpace(options.Value?.BaseUrl)
            ? options.Value.BaseUrl
            : "http://localhost:11434";

        if (_httpClient.BaseAddress == null)
        {
            _httpClient.BaseAddress = new Uri(_baseUrl.TrimEnd('/') + "/");
        }
    }

    public async Task<OllamaRouterStatus> GetGatewayStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));

            var response = await _httpClient.GetAsync("api/version", timeoutCts.Token);
            bool isConnected = response.IsSuccessStatusCode;
            bool isRouter = false;

            if (response.Headers.Contains("x-ollamarouter-version") || response.Headers.Contains("x-ollama-router-target"))
            {
                isRouter = true;
            }

            var targets = new List<TargetBackendStatus>
            {
                new("Local GPU Target", isConnected ? TargetBackendState.Active : TargetBackendState.Offline, "Host GPU (Developer Workstation). Fast on-premise execution when model/context fits host VRAM, or for concurrent interactive ideation."),
                new("Remote Server Target", isConnected ? TargetBackendState.Available : TargetBackendState.Offline, "Dedicated LAN GPU Server (Local Area Network). High-VRAM on-premise node powering ~95% of background dev-queue tasks."),
                new("Cloud Overflow Target", isConnected ? TargetBackendState.Active : TargetBackendState.Offline, "Cloud Overflow. Optional external endpoint for complex ideation or instant zero-VRAM diagnostics without waiting.")
            };

            return new OllamaRouterStatus(isConnected, isRouter, _baseUrl, targets);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "OllamaRouter gateway unreachable at {BaseAddress}", _httpClient.BaseAddress);
            var targets = new List<TargetBackendStatus>
            {
                new("Local GPU Target", TargetBackendState.Offline, "Host GPU (Developer Workstation). Fast on-premise execution when model/context fits host VRAM, or for concurrent interactive ideation."),
                new("Remote Server Target", TargetBackendState.Offline, "Dedicated LAN GPU Server (Local Area Network). High-VRAM on-premise node powering ~95% of background dev-queue tasks."),
                new("Cloud Overflow Target", TargetBackendState.Offline, "Cloud Overflow. Optional external endpoint for complex ideation or instant zero-VRAM diagnostics without waiting.")
            };
            return new OllamaRouterStatus(false, false, _baseUrl, targets);
        }
    }

    public async Task<IReadOnlyList<ResidentModelInfo>> GetResidentModelsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));

            var response = await _httpClient.GetFromJsonAsync<OllamaPsResponse>("api/ps", timeoutCts.Token);
            if (response?.Models == null || response.Models.Count == 0)
            {
                return Array.Empty<ResidentModelInfo>();
            }

            return response.Models.Select(m => new ResidentModelInfo(
                Name: m.Name ?? m.Model ?? string.Empty,
                Model: m.Model ?? m.Name ?? string.Empty,
                Size: m.Size,
                SizeVram: m.SizeVram > 0 ? m.SizeVram : m.Size,
                ExpiresAt: m.ExpiresAt,
                Digest: m.Digest
            )).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to query resident models from /api/ps at {BaseAddress}", _httpClient.BaseAddress);
            return Array.Empty<ResidentModelInfo>();
        }
    }

    public async Task<bool> ReclaimVramAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // 1. First attempt native OllamaRouter non-destructive reclaim endpoint
            var reclaimResponse = await _httpClient.PostAsync("monitor/reclaim-vram?disableTarget=false", null, cancellationToken);
            if (reclaimResponse.IsSuccessStatusCode)
            {
                _logger.LogInformation("Successfully triggered OllamaRouter VRAM reclaim via /monitor/reclaim-vram.");
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "OllamaRouter /monitor/reclaim-vram not available, falling back to standard model unload.");
        }

        try
        {
            // 2. Fallback: Query resident models and set keep_alive to 0 to unload them without restarting service
            var residentModels = await GetResidentModelsAsync(cancellationToken);
            if (residentModels.Count == 0)
            {
                return true;
            }

            bool anySuccess = false;
            foreach (var model in residentModels)
            {
                var payload = new
                {
                    model = model.Name,
                    keep_alive = "0s"
                };
                var unloadResponse = await _httpClient.PostAsJsonAsync("api/generate", payload, cancellationToken);
                if (unloadResponse.IsSuccessStatusCode)
                {
                    anySuccess = true;
                }
            }
            return anySuccess;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to reclaim VRAM via fallback model unload.");
            return false;
        }
    }

    private class OllamaPsResponse
    {
        [JsonPropertyName("models")]
        public List<OllamaPsModel>? Models { get; set; }
    }

    private class OllamaPsModel
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("model")]
        public string? Model { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("size_vram")]
        public long SizeVram { get; set; }

        [JsonPropertyName("expires_at")]
        public DateTimeOffset? ExpiresAt { get; set; }

        [JsonPropertyName("digest")]
        public string? Digest { get; set; }
    }
}
