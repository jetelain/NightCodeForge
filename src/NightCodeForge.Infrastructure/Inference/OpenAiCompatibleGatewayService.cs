using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightCodeForge.Agents.Inference;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Inference;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Infrastructure.Inference;

/// <summary>
/// Dedicated gateway service for OpenAI-compatible LLM endpoints (vLLM, LM Studio, OpenRouter, DeepSeek, OpenAI).
/// GPU VRAM reclamation is a no-op since remote/cloud gateways do not support client-initiated VRAM eviction.
/// Context windows are resolved deterministically from manual models, discovered cache, or default context tokens.
/// </summary>
public sealed class OpenAiCompatibleGatewayService : ILlmGatewayService
{
    private readonly HttpClient _httpClient;
    private readonly IOptions<LlmProviderOptions> _options;
    private readonly IOpenAiModelAllowlistCache _allowlistCache;
    private readonly IDefaultModelSelector _defaultModelSelector;
    private readonly ILogger<OpenAiCompatibleGatewayService> _logger;

    public OpenAiCompatibleGatewayService(
        HttpClient httpClient,
        IOptions<LlmProviderOptions> options,
        IOpenAiModelAllowlistCache allowlistCache,
        IDefaultModelSelector defaultModelSelector,
        ILogger<OpenAiCompatibleGatewayService> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _allowlistCache = allowlistCache ?? throw new ArgumentNullException(nameof(allowlistCache));
        _defaultModelSelector = defaultModelSelector ?? throw new ArgumentNullException(nameof(defaultModelSelector));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public LlmProviderType ProviderType => LlmProviderType.OpenAiCompatible;

    public Task<LlmProviderType?> DetectProviderAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<LlmProviderType?>(LlmProviderType.OpenAiCompatible);

    public Task<GatewayStatusReport> GetGatewayStatusAsync(CancellationToken cancellationToken = default)
    {
        var endpoint = _options.Value.EffectiveEndpoint;
        var authority = "openai-compatible";
        if (Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            authority = uri.Authority;
        }

        var target = new FleetTargetStatus(
            Name: authority,
            Url: endpoint,
            Enabled: true,
            Liveness: FleetLiveness.Online,
            LatencyMs: null,
            Hardware: new FleetTargetHardware(null, "Cloud / External Gateway", [], null, null),
            Execution: new FleetTargetExecution(0, null, null));

        var report = new GatewayStatusReport(
            IsConnected: true,
            ProviderType: ProviderType,
            BaseUrl: endpoint,
            Version: "v1",
            FleetTargets: [target]);

        return Task.FromResult(report);
    }

    public Task<IReadOnlyList<ResidentModelInfo>> GetResidentModelsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ResidentModelInfo>>(Array.Empty<ResidentModelInfo>());

    public Task<int> ResolveContextWindowAsync(string modelName, CancellationToken cancellationToken = default)
    {
        string effectiveModel;
        if (!string.IsNullOrWhiteSpace(modelName))
        {
            effectiveModel = modelName.Trim();
        }
        else if (!string.IsNullOrWhiteSpace(_options.Value.DefaultModelId))
        {
            effectiveModel = _options.Value.DefaultModelId.Trim();
        }
        else
        {
            var discovered = _allowlistCache.GetDiscoveredModels();
            var selected = discovered.Count > 0 ? _defaultModelSelector.SelectDefaultModel(discovered) : null;
            if (selected != null && !string.IsNullOrWhiteSpace(selected.ModelId))
            {
                effectiveModel = selected.ModelId;
            }
            else if (_options.Value.OpenAiCompatible?.AllowedModels is { Count: > 0 } allowed)
            {
                var candidates = allowed
                    .Where(m => !string.IsNullOrWhiteSpace(m?.ModelId))
                    .Select(m => new AvailableModelInfo(
                        ModelId: m.ModelId.Trim(),
                        DisplayName: m.DisplayName ?? m.ModelId.Trim(),
                        ContextWindowTokens: m.ContextWindowTokens,
                        PromptCostPerMillion: m.PromptCostPerMillion,
                        CompletionCostPerMillion: m.CompletionCostPerMillion))
                    .ToList();
                selected = _defaultModelSelector.SelectDefaultModel(candidates);
                effectiveModel = selected?.ModelId ?? KnownDefaultModels.DefaultBaseline;
            }
            else
            {
                effectiveModel = KnownDefaultModels.DefaultBaseline;
            }
        }

        // 1. Manual AllowedModels override
        var manual = _options.Value.OpenAiCompatible?.AllowedModels?
            .FirstOrDefault(m => string.Equals(m?.ModelId?.Trim(), effectiveModel, StringComparison.OrdinalIgnoreCase));
        if (manual != null && manual.ContextWindowTokens > 0)
        {
            return Task.FromResult(manual.ContextWindowTokens);
        }

        // 2. Discovered cache
        if (_allowlistCache.TryGetModel(effectiveModel, out var discoveredModel) && discoveredModel?.ContextWindowTokens is { } tokens && tokens > 0)
        {
            return Task.FromResult(tokens);
        }

        // 3. Default discovered tokens
        var defaultTokens = _options.Value.OpenAiCompatible?.DefaultDiscoveredContextTokens ?? LlmProviderOptions.DefaultContextWindowTokens;
        return Task.FromResult(defaultTokens > 0 ? defaultTokens : LlmProviderOptions.DefaultContextWindowTokens);
    }

    public Task<bool> ReclaimVramAsync(CancellationToken cancellationToken = default) =>
        ReclaimVramAsync(null, cancellationToken);

    public Task<bool> ReclaimVramAsync(string? targetName, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("VRAM reclaim invoked for OpenAI-compatible endpoint; operation is a no-op.");
        return Task.FromResult(true);
    }
}

