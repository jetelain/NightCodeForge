using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Infrastructure.Inference;

/// <summary>
/// Behaviour shared by the standalone Ollama and OllamaRouter gateways: resident model query
/// (<c>/api/ps</c>), context window resolution (<c>/api/show</c> fallback) and provider detection.
/// Provider-specific behaviour (status, reclaim, router context metadata) lives in the subclasses;
/// the implementation is chosen once at startup, so nothing here branches on the provider type.
/// </summary>
public abstract class OllamaGatewayServiceBase : IOllamaRouterGatewayService
{
    /// <summary>Context window Ollama applies when a model declares none (its documented default <c>num_ctx</c>).</summary>
    public const int OllamaDefaultContextTokens = 4096;

    protected static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);
    protected static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(3);

    private readonly ILlmContextWindowCache _contextWindowCache;
    private readonly LlmEndpointOptions _options;

    protected OllamaGatewayServiceBase(
        HttpClient httpClient,
        IOptions<LlmEndpointOptions> options,
        ILlmContextWindowCache contextWindowCache,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        Http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        Logger = logger;
        _contextWindowCache = contextWindowCache ?? throw new ArgumentNullException(nameof(contextWindowCache));
        BaseUrl = LlmEndpointHttpClient.Configure(httpClient, options);
        _options = options.Value;
    }

    protected HttpClient Http { get; }

    protected ILogger Logger { get; }

    protected string BaseUrl { get; }

    public abstract LlmProviderType ProviderType { get; }

    public abstract Task<GatewayStatusReport> GetGatewayStatusAsync(CancellationToken cancellationToken = default);

    public abstract Task<bool> ReclaimVramAsync(string? targetName, CancellationToken cancellationToken = default);

    public Task<bool> ReclaimVramAsync(CancellationToken cancellationToken = default) =>
        ReclaimVramAsync(null, cancellationToken);

    public async Task<LlmProviderType?> DetectProviderAsync(CancellationToken cancellationToken = default)
    {
        var info = await TryGetJsonAsync<RouterInfoProbe>(OllamaApiPaths.RouterInfo, ProbeTimeout, cancellationToken);
        if (info is { IsRouter: true })
        {
            return LlmProviderType.OllamaRouter;
        }

        var version = await TryGetJsonAsync<OllamaVersionDto>(OllamaApiPaths.Version, ProbeTimeout, cancellationToken);
        return version is null ? null : LlmProviderType.Ollama;
    }

    public async Task<IReadOnlyList<ResidentModelInfo>> GetResidentModelsAsync(CancellationToken cancellationToken = default)
    {
        var response = await TryGetJsonAsync<OllamaPsResponse>(OllamaApiPaths.Ps, StatusTimeout, cancellationToken);
        if (response?.Models == null || response.Models.Count == 0)
        {
            return Array.Empty<ResidentModelInfo>();
        }

        return response.Models.Select(m => new ResidentModelInfo(
            Name: m.Name ?? m.Model ?? string.Empty,
            Model: m.Model ?? m.Name ?? string.Empty,
            Size: m.Size,
            SizeVram: m.SizeVram,
            ExpiresAt: m.ExpiresAt,
            Digest: m.Digest)).ToList();
    }

    public async Task<int> ResolveContextWindowAsync(string modelName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return _options.ContextWindowTokens;
        }

        var normalizedModelName = modelName.Trim();
        if (_contextWindowCache.TryGetContextWindow(normalizedModelName, out var cached))
        {
            return cached;
        }

        var discovered = await ProbeProviderContextWindowAsync(normalizedModelName, cancellationToken)
            ?? await ProbeShowContextWindowAsync(normalizedModelName, cancellationToken);

        if (discovered is { } tokens && tokens > 0)
        {
            _contextWindowCache.SetContextWindow(normalizedModelName, tokens);
            return tokens;
        }

        // Discovery failed (not cached so a later call can retry): Ollama's own default applies.
        Logger.LogWarning("Could not discover the context window of '{Model}'; assuming Ollama default {Tokens} tokens.", modelName, OllamaDefaultContextTokens);
        return OllamaDefaultContextTokens;
    }

    /// <summary>Provider-specific context window metadata; null falls through to <c>/api/show</c>.</summary>
    protected virtual Task<int?> ProbeProviderContextWindowAsync(string modelName, CancellationToken cancellationToken) =>
        Task.FromResult<int?>(null);

    /// <summary>GET a JSON document; transport/parse failures are logged and yield null (caller cancellation propagates).</summary>
    protected async Task<T?> TryGetJsonAsync<T>(string path, TimeSpan timeout, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);

            using var response = await Http.GetAsync(path, timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                Logger.LogDebug("GET {Path} returned HTTP {Status}", path, (int)response.StatusCode);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<T>(timeoutCts.Token);
        }
        catch (Exception ex) when (IsRecoverableProbeFailure(ex, cancellationToken))
        {
            Logger.LogDebug(ex, "GET {Path} failed at {BaseAddress}", path, Http.BaseAddress);
            return null;
        }
    }

    protected static bool IsRecoverableProbeFailure(Exception ex, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested
        && ex is HttpRequestException or OperationCanceledException or JsonException or NotSupportedException;

    private async Task<int?> ProbeShowContextWindowAsync(string modelName, CancellationToken cancellationToken)
    {
        try
        {
            using var showCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            showCts.CancelAfter(ProbeTimeout);

            using var response = await Http.PostAsJsonAsync(OllamaApiPaths.Show, new { model = modelName }, showCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                Logger.LogDebug("POST {Path} for {Model} returned HTTP {Status}", OllamaApiPaths.Show, modelName, (int)response.StatusCode);
                return null;
            }

            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(showCts.Token), cancellationToken: showCts.Token);
            return OllamaShowParser.ParseContextWindow(document.RootElement);
        }
        catch (Exception ex) when (IsRecoverableProbeFailure(ex, cancellationToken))
        {
            Logger.LogDebug(ex, "POST {Path} failed for {Model}", OllamaApiPaths.Show, modelName);
            return null;
        }
    }

    private sealed class RouterInfoProbe
    {
        [JsonPropertyName("isRouter")]
        public bool IsRouter { get; init; }
    }

    private sealed class OllamaVersionDto
    {
        [JsonPropertyName("version")]
        public string? Version { get; init; }
    }

    private sealed class OllamaPsResponse
    {
        [JsonPropertyName("models")]
        public List<OllamaPsModel>? Models { get; init; }
    }

    private sealed class OllamaPsModel
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("model")]
        public string? Model { get; init; }

        [JsonPropertyName("size")]
        public long Size { get; init; }

        [JsonPropertyName("size_vram")]
        public long SizeVram { get; init; }

        [JsonPropertyName("expires_at")]
        public DateTimeOffset? ExpiresAt { get; init; }

        [JsonPropertyName("digest")]
        public string? Digest { get; init; }
    }

    /// <summary>Version of the Ollama daemon (<c>GET /api/version</c>), or null when unreachable.</summary>
    protected async Task<string?> TryGetOllamaVersionAsync(CancellationToken cancellationToken)
    {
        var dto = await TryGetJsonAsync<OllamaVersionDto>(OllamaApiPaths.Version, StatusTimeout, cancellationToken);
        return dto is null ? null : dto.Version ?? string.Empty;
    }
}
