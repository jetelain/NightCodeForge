using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Infrastructure.Inference;

/// <summary>Model catalog of a standalone Ollama server.</summary>
public sealed class OllamaModelCatalogService : LlmModelCatalogServiceBase
{
    private const int MaxParallelShowCalls = 4;

    public OllamaModelCatalogService(HttpClient httpClient, IOptions<LlmEndpointOptions> options, ILogger<OllamaModelCatalogService> logger)
        : base(httpClient, options, logger)
    {
    }

    protected override LlmProviderType ProviderType => LlmProviderType.Ollama;

    public override async Task<IReadOnlyList<AvailableModelInfo>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var tagsCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            tagsCts.CancelAfter(ProbeTimeout);

            var tags = await HttpClient.GetFromJsonAsync<TagsResponse>(OllamaApiPaths.Tags, tagsCts.Token);
            if (tags?.Models == null)
            {
                return Array.Empty<AvailableModelInfo>();
            }

            var distinctModels = tags.Models
                .Select(m => m.Name ?? m.Model ?? string.Empty)
                .Where(n => n.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var results = new ConcurrentBag<AvailableModelInfo>();
            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = MaxParallelShowCalls,
                CancellationToken = cancellationToken
            };

            await Parallel.ForEachAsync(distinctModels, parallelOptions, async (name, ct) =>
            {
                var matchingTag = tags.Models.First(m => string.Equals(m.Name ?? m.Model, name, StringComparison.OrdinalIgnoreCase));
                var modelInfo = await BuildModelInfoAsync(matchingTag, name, ct);
                results.Add(modelInfo);
            });

            return results.ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            Logger.LogWarning(ex, "Failed to query model catalog from {BaseAddress}", HttpClient.BaseAddress);
            return Array.Empty<AvailableModelInfo>();
        }
    }

    private async Task<AvailableModelInfo> BuildModelInfoAsync(TagItem tag, string name, CancellationToken cancellationToken)
    {
        int? context = null;
        var supportsReasoning = false;

        try
        {
            using var showCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            showCts.CancelAfter(ProbeTimeout);

            using var response = await HttpClient.PostAsJsonAsync(OllamaApiPaths.Show, new { model = name }, showCts.Token);
            if (response.IsSuccessStatusCode)
            {
                using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(showCts.Token), cancellationToken: showCts.Token);
                context = OllamaShowParser.ParseContextWindow(doc.RootElement);
                supportsReasoning = OllamaShowParser.SupportsThinking(doc.RootElement);
            }
            else
            {
                Logger.LogWarning("Model details for '{Model}' unavailable (HTTP {Status}).", name, (int)response.StatusCode);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            Logger.LogWarning(ex, "Failed to read model details for '{Model}'.", name);
        }

        return new AvailableModelInfo(
            ModelId: name,
            DisplayName: name,
            ContextWindowTokens: context,
            SizeBytes: tag.Size > 0 ? tag.Size : null,
            ProviderType: ProviderType,
            SupportsReasoning: supportsReasoning);
    }

    private sealed class TagsResponse
    {
        [JsonPropertyName("models")]
        public List<TagItem>? Models { get; set; }
    }

    private sealed class TagItem
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("model")]
        public string? Model { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }
    }
}
