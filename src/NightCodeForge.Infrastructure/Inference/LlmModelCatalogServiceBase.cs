using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Infrastructure.Inference;

/// <summary>
/// Shared model-catalog base for LLM providers (Ollama, OllamaRouter).
/// </summary>
public abstract class LlmModelCatalogServiceBase : ILlmModelCatalogService
{
    protected static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    protected LlmModelCatalogServiceBase(HttpClient httpClient, IOptions<LlmEndpointOptions> options, ILogger logger)
    {
        HttpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        LlmEndpointHttpClient.Configure(httpClient, options);
    }

    protected HttpClient HttpClient { get; }

    protected ILogger Logger { get; }

    protected abstract LlmProviderType ProviderType { get; }

    public abstract Task<IReadOnlyList<AvailableModelInfo>> GetAvailableModelsAsync(CancellationToken cancellationToken = default);

    public virtual Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
