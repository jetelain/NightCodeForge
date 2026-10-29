using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightCodeForge.Agents.Inference;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Infrastructure.Inference;

/// <summary>
/// Shared model-catalog base for LLM providers (Ollama, OllamaRouter, OpenAI-Compatible).
/// Default model selection is resolved polymorphically via <see cref="IDefaultModelSelector"/>.
/// </summary>
public abstract class LlmModelCatalogServiceBase : ILlmModelCatalogService
{
    protected static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);
    private readonly IDefaultModelSelector _defaultModelSelector;

    protected LlmModelCatalogServiceBase(
        HttpClient httpClient,
        IOptions<LlmEndpointOptions> options,
        IDefaultModelSelector defaultModelSelector,
        ILogger logger)
    {
        HttpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _defaultModelSelector = defaultModelSelector ?? throw new ArgumentNullException(nameof(defaultModelSelector));
        ArgumentNullException.ThrowIfNull(options);
        DefaultModelId = options.Value.DefaultModelId;
        LlmEndpointHttpClient.Configure(httpClient, options);
    }

    protected LlmModelCatalogServiceBase(
        HttpClient httpClient,
        IOptions<LlmProviderOptions> options,
        IDefaultModelSelector defaultModelSelector,
        ILogger logger)
    {
        HttpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _defaultModelSelector = defaultModelSelector ?? throw new ArgumentNullException(nameof(defaultModelSelector));
        ArgumentNullException.ThrowIfNull(options);
        DefaultModelId = options.Value.DefaultModelId;
        var effective = options.Value.EffectiveEndpoint;
        if (!string.IsNullOrWhiteSpace(effective) && httpClient.BaseAddress == null)
        {
            httpClient.BaseAddress = new Uri(effective.TrimEnd('/') + "/");
        }
    }

    protected HttpClient HttpClient { get; }

    protected ILogger Logger { get; }

    protected string? DefaultModelId { get; }

    protected abstract LlmProviderType ProviderType { get; }

    public abstract Task<IReadOnlyList<AvailableModelInfo>> GetAvailableModelsAsync(CancellationToken cancellationToken = default);

    public virtual async Task<AvailableModelInfo?> GetDefaultModelAsync(CancellationToken cancellationToken = default)
    {
        var models = await GetAvailableModelsAsync(cancellationToken);
        return _defaultModelSelector.SelectDefaultModel(models, DefaultModelId);
    }

    public virtual async Task<EndpointVerificationResult> VerifyEndpointAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var models = await GetAvailableModelsAsync(cancellationToken);
            if (models.Count > 0)
            {
                return new EndpointVerificationResult(
                    IsSuccess: true,
                    IsDiscoveryImplemented: true,
                    DiscoveredCount: models.Count,
                    VerifiedModels: models);
            }

            return new EndpointVerificationResult(
                IsSuccess: false,
                IsDiscoveryImplemented: true,
                DiscoveredCount: 0,
                VerifiedModels: [],
                ErrorMessage: $"Failed to discover models from {HttpClient.BaseAddress}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            Logger.LogWarning(ex, "Endpoint verification failed for {BaseAddress}", HttpClient.BaseAddress);
            return new EndpointVerificationResult(
                IsSuccess: false,
                IsDiscoveryImplemented: false,
                DiscoveredCount: 0,
                ErrorMessage: $"Connection failed: {ex.Message}");
        }
    }

    public virtual Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
