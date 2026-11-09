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
/// populating models with the fleet's recommended and native context windows and probing
/// <c>POST /api/show</c> to discover native thinking capabilities.
/// </summary>
public sealed class OllamaRouterModelCatalogService : LlmModelCatalogServiceBase
{
    private const int MaxParallelShowCalls = 4;
    private readonly ILlmContextWindowCache _contextWindowCache;

    public OllamaRouterModelCatalogService(
        HttpClient httpClient,
        IOptions<LlmEndpointOptions> options,
        IDefaultModelSelector defaultModelSelector,
        ILlmContextWindowCache contextWindowCache,
        ILogger<OllamaRouterModelCatalogService> logger)
        : base(httpClient, options, defaultModelSelector, logger)
    {
        _contextWindowCache = contextWindowCache ?? throw new ArgumentNullException(nameof(contextWindowCache));
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

            var validModels = response.Models
                .Where(m => !string.IsNullOrWhiteSpace(m?.Name))
                .DistinctBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var results = new AvailableModelInfo[validModels.Count];
            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = MaxParallelShowCalls,
                CancellationToken = cancellationToken
            };

            await Parallel.ForEachAsync(Enumerable.Range(0, validModels.Count), parallelOptions, async (i, ct) =>
            {
                var model = validModels[i];
                results[i] = await BuildRouterModelInfoAsync(model, ct);
            });

            return results.Where(r => r != null).ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            Logger.LogWarning(ex, "Failed to read router models from {BaseAddress}", HttpClient.BaseAddress);
            return Array.Empty<AvailableModelInfo>();
        }
    }

    private async Task<AvailableModelInfo> BuildRouterModelInfoAsync(RouterModelSummary model, CancellationToken cancellationToken)
    {
        int? context = model.EffectiveContextWindow;
        ModelThinkingCapability? thinking = null;
        var supportsReasoning = true;

        try
        {
            using var showCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            showCts.CancelAfter(ProbeTimeout);

            using var response = await HttpClient.PostAsJsonAsync(OllamaApiPaths.Show, new { model = model.Name }, showCts.Token);
            if (response.IsSuccessStatusCode)
            {
                using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(showCts.Token), cancellationToken: showCts.Token);
                var showContext = OllamaShowParser.ParseContextWindow(doc.RootElement);
                thinking = OllamaShowParser.ParseThinkingCapability(doc.RootElement);
                supportsReasoning = thinking.SupportsThinking;
                context ??= showContext;

                _contextWindowCache.SetThinkingCapability(model.Name, thinking);
            }
            else
            {
                Logger.LogDebug("Router model details for '{Model}' unavailable (HTTP {Status}).", model.Name, (int)response.StatusCode);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            Logger.LogDebug(ex, "Failed to read router model details for '{Model}'.", model.Name);
        }

        if (context is { } tokens && tokens > 0)
        {
            _contextWindowCache.SetContextWindow(model.Name, tokens);
        }

        return new AvailableModelInfo(
            ModelId: model.Name,
            DisplayName: model.Name,
            ContextWindowTokens: context,
            SizeBytes: null,
            ProviderType: ProviderType,
            SupportsReasoning: supportsReasoning,
            ThinkingCapability: thinking);
    }
}
