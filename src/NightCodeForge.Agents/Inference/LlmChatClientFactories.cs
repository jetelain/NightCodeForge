using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Resilience;

namespace NightCodeForge.Agents.Inference;

/// <summary>
/// Base for the provider-specific chat client factories. Caches session-less clients by
/// (model, routing target), shares one <see cref="SocketsHttpHandler"/>, and wraps clients with
/// telemetry and pause gating. Subclasses create provider-specific raw chat client instances.
/// </summary>
public abstract class LlmChatClientFactoryBase : ILlmChatClientFactory, IDisposable
{
    protected readonly IOptions<LlmProviderOptions> _options;
    protected readonly ILoggerFactory _loggerFactory;
    protected readonly IOperationsTelemetryService _telemetryService;
    protected readonly IOperationsControlService _operationsControlService;
    protected readonly TimeProvider _timeProvider;
    protected readonly IOptions<RetryPolicyOptions> _retryOptions;
    private readonly ConcurrentDictionary<string, IChatClient> _clientCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SocketsHttpHandler _sharedSocketsHandler;
    private bool _disposed;

    protected LlmChatClientFactoryBase(
        IOptions<LlmProviderOptions> options,
        ILoggerFactory loggerFactory,
        IOperationsTelemetryService telemetryService,
        IOperationsControlService operationsControlService,
        TimeProvider timeProvider,
        IOptions<RetryPolicyOptions> retryOptions)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _telemetryService = telemetryService ?? throw new ArgumentNullException(nameof(telemetryService));
        _operationsControlService = operationsControlService ?? throw new ArgumentNullException(nameof(operationsControlService));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _retryOptions = retryOptions ?? throw new ArgumentNullException(nameof(retryOptions));

        _sharedSocketsHandler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = OllamaHttpConstants.DefaultConnectionLifetime,
            PooledConnectionIdleTimeout = OllamaHttpConstants.DefaultConnectionIdleTimeout,
            KeepAlivePingDelay = OllamaHttpConstants.DefaultKeepAlivePingDelay,
            KeepAlivePingTimeout = OllamaHttpConstants.DefaultKeepAlivePingTimeout
        };
    }

    public IChatClient CreateClient(string? modelId = null) =>
        CreateClient(modelId, OllamaRoutingTarget.Auto, sessionId: null);

    public IChatClient CreateClient(string? modelId, OllamaRoutingTarget routingTarget) =>
        CreateClient(modelId, routingTarget, sessionId: null);

    public IChatClient CreateClient(AgentProfile? profile) =>
        CreateClient(profile, sessionId: null);

    public IChatClient CreateClient(AgentProfile? profile, string? sessionId) =>
        CreateClient(profile?.ModelName, profile?.RoutingTarget ?? OllamaRoutingTarget.Auto, sessionId);

    public IChatClient CreateClient(string? modelId, OllamaRoutingTarget routingTarget, string? sessionId)
    {
        var options = _options.Value;
        var effectiveModel = ResolveEffectiveModelId(modelId, options);

        // Session-bound clients are not cached: session ids are unbounded (one per task stage).
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            return BuildClient(options, effectiveModel, routingTarget, sessionId);
        }

        var cacheKey = $"{effectiveModel}:{routingTarget}";
        return _clientCache.GetOrAdd(cacheKey, _ => BuildClient(options, effectiveModel, routingTarget, null));
    }

    /// <summary>
    /// Resolves the effective model identifier to instantiate. Subclasses can specialize model resolution
    /// according to provider-specific strategies (e.g. dynamic discovery / allowlist cache).
    /// </summary>
    protected virtual string ResolveEffectiveModelId(string? modelId, LlmProviderOptions options)
    {
        if (!string.IsNullOrWhiteSpace(modelId))
        {
            return modelId.Trim();
        }

        if (!string.IsNullOrWhiteSpace(options.DefaultModelId))
        {
            return options.DefaultModelId.Trim();
        }

        return KnownDefaultModels.DefaultBaseline;
    }

    private IChatClient BuildClient(LlmProviderOptions options, string effectiveModel, OllamaRoutingTarget routingTarget, string? sessionId)
    {
        var rawClient = CreateRawChatClient(options, effectiveModel, routingTarget, sessionId, _sharedSocketsHandler);
        var targetLabel = options.ProviderType == LlmProviderType.OpenAiCompatible ? "OpenAI Compatible" : routingTarget.ToString();
        var telemetryClient = new TelemetryChatClient(rawClient, _telemetryService, effectiveModel, targetLabel, _loggerFactory.CreateLogger<TelemetryChatClient>(), options.EffectiveEndpoint);
        return new OperationsPauseGatingChatClient(telemetryClient, _operationsControlService);
    }

    /// <summary>
    /// Creates the provider-specific raw IChatClient instance (OllamaApiClient or OpenAIClient).
    /// </summary>
    protected abstract IChatClient CreateRawChatClient(
        LlmProviderOptions options,
        string effectiveModel,
        OllamaRoutingTarget routingTarget,
        string? sessionId,
        SocketsHttpHandler socketsHandler);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var client in _clientCache.Values)
        {
            if (client is IDisposable d)
            {
                d.Dispose();
            }
        }
        _clientCache.Clear();
        _sharedSocketsHandler.Dispose();
    }
}

/// <summary>
/// Common base for Ollama-based chat client factories (standalone and router).
/// </summary>
public abstract class OllamaChatClientFactoryBase : LlmChatClientFactoryBase
{
    protected OllamaChatClientFactoryBase(
        IOptions<LlmProviderOptions> options,
        ILoggerFactory loggerFactory,
        IOperationsTelemetryService telemetryService,
        IOperationsControlService operationsControlService,
        TimeProvider timeProvider,
        IOptions<RetryPolicyOptions> retryOptions)
        : base(options, loggerFactory, telemetryService, operationsControlService, timeProvider, retryOptions)
    {
    }

    protected override IChatClient CreateRawChatClient(
        LlmProviderOptions options,
        string effectiveModel,
        OllamaRoutingTarget routingTarget,
        string? sessionId,
        SocketsHttpHandler socketsHandler)
    {
        return OllamaChatClientFactory.CreateOllama(
            options,
            effectiveModel,
            _loggerFactory.CreateLogger("OllamaApiClient"),
            _timeProvider,
            _retryOptions.Value,
            routingTarget,
            socketsHandler,
            httpClient => ConfigureRouting(httpClient, routingTarget, sessionId));
    }

    /// <summary>Attaches the provider-specific routing/session headers to the client.</summary>
    protected abstract void ConfigureRouting(HttpClient httpClient, OllamaRoutingTarget routingTarget, string? sessionId);
}

/// <summary>Chat client factory for OllamaRouter: pins targets and sets the session id for affinity/KV cache.</summary>
public sealed class OllamaRouterChatClientFactory : OllamaChatClientFactoryBase
{
    public OllamaRouterChatClientFactory(
        IOptions<LlmProviderOptions> options,
        ILoggerFactory loggerFactory,
        IOperationsTelemetryService telemetryService,
        IOperationsControlService operationsControlService,
        TimeProvider timeProvider,
        IOptions<RetryPolicyOptions> retryOptions)
        : base(options, loggerFactory, telemetryService, operationsControlService, timeProvider, retryOptions)
    {
    }

    protected override void ConfigureRouting(HttpClient httpClient, OllamaRoutingTarget routingTarget, string? sessionId) =>
        OllamaRoutingHeaders.Apply(httpClient, routingTarget, sessionId);
}

/// <summary>Chat client factory for a standalone Ollama server: no router headers are sent.</summary>
public sealed class StandaloneOllamaChatClientFactory : OllamaChatClientFactoryBase
{
    public StandaloneOllamaChatClientFactory(
        IOptions<LlmProviderOptions> options,
        ILoggerFactory loggerFactory,
        IOperationsTelemetryService telemetryService,
        IOperationsControlService operationsControlService,
        TimeProvider timeProvider,
        IOptions<RetryPolicyOptions> retryOptions)
        : base(options, loggerFactory, telemetryService, operationsControlService, timeProvider, retryOptions)
    {
    }

    protected override void ConfigureRouting(HttpClient httpClient, OllamaRoutingTarget routingTarget, string? sessionId)
    {
        // Standalone Ollama has no routing or session-affinity concept.
    }
}
