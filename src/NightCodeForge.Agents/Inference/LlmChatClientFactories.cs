using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Resilience;
using NightCodeForge.Core.Streaming;

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
    protected readonly IOptions<LlmRetryPolicyOptions> _retryOptions;
    protected readonly IAgentActivityStreamer _activityStreamer;
    private readonly ConcurrentDictionary<string, IChatClient> _clientCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SocketsHttpHandler _sharedSocketsHandler;
    private bool _disposed;

    protected LlmChatClientFactoryBase(
        IOptions<LlmProviderOptions> options,
        ILoggerFactory loggerFactory,
        IOperationsTelemetryService telemetryService,
        IOperationsControlService operationsControlService,
        TimeProvider timeProvider,
        IOptions<LlmRetryPolicyOptions> retryOptions,
        IAgentActivityStreamer activityStreamer)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _telemetryService = telemetryService ?? throw new ArgumentNullException(nameof(telemetryService));
        _operationsControlService = operationsControlService ?? throw new ArgumentNullException(nameof(operationsControlService));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _retryOptions = retryOptions ?? throw new ArgumentNullException(nameof(retryOptions));
        _activityStreamer = activityStreamer ?? throw new ArgumentNullException(nameof(activityStreamer));

        _sharedSocketsHandler = OllamaHttpConstants.CreateSocketsHttpHandler();
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
        var telemetryClient = new TelemetryChatClient(rawClient, _telemetryService, effectiveModel, targetLabel, _loggerFactory.CreateLogger<TelemetryChatClient>(), options.EffectiveEndpoint, sessionId);
        return new OperationsPauseGatingChatClient(telemetryClient, _operationsControlService);
    }

    /// <summary>
    /// Creates effective retry options configured with an activity streamer callback.
    /// </summary>
    protected LlmRetryPolicyOptions CreateEffectiveRetryOptions(string? sessionId)
    {
        var baseOptions = _retryOptions.Value;
        return new LlmRetryPolicyOptions
        {
            MaxRetries = baseOptions.MaxRetries,
            BaseDelay = baseOptions.BaseDelay,
            MaxDelay = baseOptions.MaxDelay,
            MaxRetryDuration = baseOptions.MaxRetryDuration,
            MaxTimeoutRetries = baseOptions.MaxTimeoutRetries,
            PerAttemptTimeout = baseOptions.PerAttemptTimeout,
            OnRetry = baseOptions.OnRetry,
            OnRetryAsync = async (retryCtx, ct) =>
            {
                if (baseOptions.OnRetryAsync != null)
                {
                    await baseOptions.OnRetryAsync(retryCtx, ct);
                }
                await BroadcastRetryNoticeAsync(retryCtx, sessionId, ct);
            }
        };
    }

    /// <summary>
    /// Broadcasts an activity notice when an inference endpoint is unreachable and retrying.
    /// </summary>
    protected async Task BroadcastRetryNoticeAsync(
        RetryNotificationContext retryCtx,
        string? sessionId,
        CancellationToken cancellationToken)
    {
        if (_operationsControlService.IsPaused)
        {
            throw new OperationsPausedException();
        }

        // 1. Resolve correlation from HTTP request headers
        string? effectiveSessionId = null;
        int effectiveTaskId = 0;
        string? effectiveStageId = null;

        if (retryCtx.Headers != null)
        {
            if (retryCtx.Headers.TryGetValues(LlmCorrelationHeaders.SessionId, out var sessionVals))
            {
                effectiveSessionId = sessionVals.FirstOrDefault();
            }
            else if (retryCtx.Headers.TryGetValues(OllamaRoutingHeaders.SessionId, out var routerSessionVals))
            {
                effectiveSessionId = routerSessionVals.FirstOrDefault();
            }

            if (retryCtx.Headers.TryGetValues(LlmCorrelationHeaders.TaskId, out var taskVals)
                && int.TryParse(taskVals.FirstOrDefault(), out var parsedTaskId))
            {
                effectiveTaskId = parsedTaskId;
            }

            if (retryCtx.Headers.TryGetValues(LlmCorrelationHeaders.StageId, out var stageVals))
            {
                effectiveStageId = stageVals.FirstOrDefault();
            }
        }

        // 2. Fallback to session-bound client's captured sessionId
        if (string.IsNullOrWhiteSpace(effectiveSessionId) && !string.IsNullOrWhiteSpace(sessionId))
        {
            effectiveSessionId = sessionId;
        }

        if (effectiveTaskId == 0 && !string.IsNullOrWhiteSpace(effectiveSessionId))
        {
            var (parsedId, parsedStage) = LlmCorrelationHeaders.ParseSessionId(effectiveSessionId);
            if (parsedId.HasValue) effectiveTaskId = parsedId.Value;
            if (!string.IsNullOrWhiteSpace(parsedStage) && string.IsNullOrWhiteSpace(effectiveStageId))
            {
                effectiveStageId = parsedStage;
            }
        }

        // 3. Fallback to active task from telemetry service
        if (effectiveTaskId == 0)
        {
            var activeTask = _telemetryService.GetActiveTask();
            if (activeTask != null)
            {
                effectiveTaskId = activeTask.TaskId;
                if (string.IsNullOrWhiteSpace(effectiveStageId) || effectiveStageId == LlmCorrelationHeaders.DefaultStageId)
                {
                    effectiveStageId = activeTask.CurrentStageId;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(effectiveSessionId))
        {
            effectiveSessionId = $"task-{effectiveTaskId}";
        }

        if (string.IsNullOrWhiteSpace(effectiveStageId))
        {
            effectiveStageId = LlmCorrelationHeaders.DefaultStageId;
        }

        _telemetryService.RecordLlmCallRetry(
            retryCtx,
            effectiveTaskId > 0 ? effectiveTaskId : null,
            effectiveStageId,
            effectiveSessionId);

        var endpointStr = retryCtx.RequestUri?.ToString() ?? _options.Value.EffectiveEndpoint;
        var statusOrError = retryCtx.StatusCode.HasValue
            ? $"HTTP {(int)retryCtx.StatusCode.Value} {retryCtx.StatusCode.Value} at {endpointStr}"
            : $"{retryCtx.ErrorMessage} at {endpointStr}";

        var message = $"Inference endpoint unreachable ({statusOrError}). Retrying attempt {retryCtx.Attempt}/{retryCtx.MaxRetries} in {Math.Round(retryCtx.Delay.TotalSeconds, 1)}s (elapsed {Math.Round(retryCtx.ElapsedDuration.TotalSeconds, 0)}s / {Math.Round(retryCtx.MaxDuration.TotalSeconds, 0)}s)...";

        var metadata = new Dictionary<string, object?>
        {
            [AgentActivityMetadataKeys.Attempt] = retryCtx.Attempt,
            [AgentActivityMetadataKeys.MaxRetries] = retryCtx.MaxRetries
        };

        var activityEvent = new AgentActivityEvent(
            effectiveSessionId,
            effectiveTaskId,
            effectiveStageId,
            AgentActivityKind.Notice,
            message,
            _timeProvider.GetUtcNow().UtcDateTime,
            metadata);

        await _activityStreamer.BroadcastAsync(activityEvent, cancellationToken);
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
        IOptions<LlmRetryPolicyOptions> retryOptions,
        IAgentActivityStreamer activityStreamer)
        : base(options, loggerFactory, telemetryService, operationsControlService, timeProvider, retryOptions, activityStreamer)
    {
    }

    protected override IChatClient CreateRawChatClient(
        LlmProviderOptions options,
        string effectiveModel,
        OllamaRoutingTarget routingTarget,
        string? sessionId,
        SocketsHttpHandler socketsHandler)
    {
        var effectiveRetryOptions = CreateEffectiveRetryOptions(sessionId);

        return OllamaChatClientFactory.CreateOllama(
            options,
            effectiveModel,
            _loggerFactory.CreateLogger("OllamaApiClient"),
            _timeProvider,
            effectiveRetryOptions,
            routingTarget,
            socketsHandler,
            httpClient =>
            {
                ConfigureRouting(httpClient, routingTarget, sessionId);
                LlmCorrelationHeaders.Apply(httpClient, sessionId);
            },
            _operationsControlService);
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
        IOptions<LlmRetryPolicyOptions> retryOptions,
        IAgentActivityStreamer activityStreamer)
        : base(options, loggerFactory, telemetryService, operationsControlService, timeProvider, retryOptions, activityStreamer)
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
        IOptions<LlmRetryPolicyOptions> retryOptions,
        IAgentActivityStreamer activityStreamer)
        : base(options, loggerFactory, telemetryService, operationsControlService, timeProvider, retryOptions, activityStreamer)
    {
    }

    protected override void ConfigureRouting(HttpClient httpClient, OllamaRoutingTarget routingTarget, string? sessionId)
    {
        // Standalone Ollama has no routing or session-affinity concept.
    }
}
