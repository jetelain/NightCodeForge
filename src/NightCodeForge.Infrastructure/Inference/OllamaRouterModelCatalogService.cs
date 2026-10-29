using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Infrastructure.Inference;

public record RouterModelSummary(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("architecture")] string? Architecture = null,
    [property: JsonPropertyName("parameterCount")] long? ParameterCount = null,
    [property: JsonPropertyName("nativeContextWindow")] int NativeContextWindow = 0,
    [property: JsonPropertyName("recommendedContextWindow")] int RecommendedContextWindow = 0,
    [property: JsonPropertyName("cloudOverflowAvailable")] bool CloudOverflowAvailable = false)
{
    public int? EffectiveContextWindow =>
        RecommendedContextWindow > 0 ? RecommendedContextWindow
        : NativeContextWindow > 0 ? NativeContextWindow
        : null;
}

public record RouterModelsResponse(
    [property: JsonPropertyName("models")] IReadOnlyList<RouterModelSummary>? Models);

/// <summary>
/// Model catalog of an OllamaRouter fleet: queries <c>GET /api/router/models</c> directly,
/// populating models with the fleet's recommended and native context windows.
/// </summary>
public sealed class OllamaRouterModelCatalogService : LlmModelCatalogServiceBase
{
    public OllamaRouterModelCatalogService(
        HttpClient httpClient,
        IOptions<LlmEndpointOptions> options,
        ILogger<OllamaRouterModelCatalogService> logger)
        : base(httpClient, options, logger)
    {
    }

    protected override LlmProviderType ProviderType => LlmProviderType.OllamaRouter;

    public override async Task<IReadOnlyList<AvailableModelInfo>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(ProbeTimeout);

            var response = await HttpClient.GetFromJsonAsync<RouterModelsResponse>(OllamaApiPaths.RouterModels, timeoutCts.Token);
            if (response?.Models == null || response.Models.Count == 0)
            {
                return Array.Empty<AvailableModelInfo>();
            }

            var result = new List<AvailableModelInfo>(response.Models.Count);
            foreach (var model in response.Models)
            {
                if (string.IsNullOrWhiteSpace(model.Name))
                {
                    continue;
                }

                result.Add(new AvailableModelInfo(
                    ModelId: model.Name,
                    DisplayName: model.Name,
                    ContextWindowTokens: model.EffectiveContextWindow,
                    SizeBytes: null,
                    ProviderType: ProviderType,
                    SupportsReasoning: true));
            }

            return result;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            Logger.LogWarning(ex, "Failed to read router models from {BaseAddress}", HttpClient.BaseAddress);
            return Array.Empty<AvailableModelInfo>();
        }
    }
}
