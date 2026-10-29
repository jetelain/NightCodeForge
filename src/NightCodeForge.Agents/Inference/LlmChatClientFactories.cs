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
/// telemetry and pause gating. Subclasses decide which HTTP headers the provider understands.
/// </summary>
public abstract class LlmChatClientFactoryBase : ILlmChatClientFactory, IDisposable
{
    private readonly IOptions<LlmProviderOptions> _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IOperationsTelemetryService _telemetryService;
    private readonly IOperationsControlService _operationsControlService;
    private readonly TimeProvider _timeProvider;
    private readonly IOptions<RetryPolicyOptions> _retryOptions;
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

    /// <summary>Attaches the provider-specific routing/session headers to the client.</summary>
    protected abstract void ConfigureRouting(HttpClient httpClient, OllamaRoutingTarget routingTarget, string? sessionId);

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
        var effectiveModel = !string.IsNullOrWhiteSpace(modelId) ? modelId : options.DefaultModelId;

        // Session-bound clients are not cached: session ids are unbounded (one per task stage).
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            return BuildClient(options, effectiveModel, routingTarget, sessionId);
        }

        var cacheKey = $"{effectiveModel}:{routingTarget}";
        return _clientCache.GetOrAdd(cacheKey, _ => BuildClient(options, effectiveModel, routingTarget, null));
    }

    private IChatClient BuildClient(LlmProviderOptions options, string effectiveModel, OllamaRoutingTarget routingTarget, string? sessionId)
    {
        var rawClient = OllamaChatClientFactory.CreateOllama(
            options,
            effectiveModel,
            _loggerFactory.CreateLogger("OllamaApiClient"),
            _timeProvider,
            _retryOptions.Value,
            routingTarget,
            _sharedSocketsHandler,
            httpClient => ConfigureRouting(httpClient, routingTarget, sessionId));

        var telemetryClient = new TelemetryChatClient(rawClient, _telemetryService, effectiveModel, routingTarget.ToString(), _loggerFactory.CreateLogger<TelemetryChatClient>(), options.Endpoint);
        return new OperationsPauseGatingChatClient(telemetryClient, _operationsControlService);
    }

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

/// <summary>Chat client factory for OllamaRouter: pins targets and sets the session id for affinity/KV cache.</summary>
public sealed class OllamaRouterChatClientFactory : LlmChatClientFactoryBase
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
public sealed class StandaloneOllamaChatClientFactory : LlmChatClientFactoryBase
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
