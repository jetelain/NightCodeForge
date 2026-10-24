using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Resilience;
using OllamaSharp;

namespace NightCodeForge.Agents.Inference;

/// <summary>
/// Factory for instantiating IChatClient via OllamaSharp's OllamaApiClient with tuned request timeouts.
/// </summary>
public static class OllamaChatClientFactory
{
    public static IChatClient CreateOllama(
        LlmProviderOptions options,
        string? modelId,
        ILogger logger,
        TimeProvider timeProvider,
        RetryPolicyOptions retryOptions,
        OllamaRoutingTarget routingTarget,
        HttpMessageHandler innerHandler)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(retryOptions);
        ArgumentNullException.ThrowIfNull(innerHandler);

        var endpoint = string.IsNullOrWhiteSpace(options.Endpoint) ? "http://localhost:11434" : options.Endpoint;
        var timeoutMinutes = Math.Max(1, options.RequestTimeoutMinutes);
        var effectiveModel = !string.IsNullOrWhiteSpace(modelId) ? modelId : options.ModelId;

        var retryHandler = new TransientHttpErrorRetryHandler(
            innerHandler,
            logger,
            timeProvider,
            retryOptions);

        var httpClient = new HttpClient(retryHandler)
        {
            BaseAddress = new Uri(endpoint),
            Timeout = TimeSpan.FromMinutes(timeoutMinutes)
        };

        if (routingTarget != OllamaRoutingTarget.Auto)
        {
            var targetStr = routingTarget switch
            {
                OllamaRoutingTarget.Local => "Local",
                OllamaRoutingTarget.Remote => "Remote",
                OllamaRoutingTarget.Cloud => "Cloud",
                _ => null
            };
            if (targetStr != null)
            {
                httpClient.DefaultRequestHeaders.Add("X-OllamaRouter-Target", targetStr);
            }
        }

        return new OllamaApiClient(httpClient, effectiveModel, null);
    }

    public static IChatClient CreateOllama(
        LlmProviderOptions options,
        string? modelId,
        ILogger logger,
        TimeProvider timeProvider,
        RetryPolicyOptions retryOptions,
        OllamaRoutingTarget routingTarget)
    {
        var socketsHandler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = OllamaHttpConstants.DefaultConnectionLifetime,
            PooledConnectionIdleTimeout = OllamaHttpConstants.DefaultConnectionIdleTimeout,
            KeepAlivePingDelay = OllamaHttpConstants.DefaultKeepAlivePingDelay,
            KeepAlivePingTimeout = OllamaHttpConstants.DefaultKeepAlivePingTimeout
        };

        return CreateOllama(options, modelId, logger, timeProvider, retryOptions, routingTarget, socketsHandler);
    }

    public static IChatClient CreateOllama(
        LlmProviderOptions options,
        AgentProfile profile,
        ILogger logger,
        TimeProvider timeProvider,
        RetryPolicyOptions retryOptions)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return CreateOllama(options, profile.ModelName, logger, timeProvider, retryOptions, profile.RoutingTarget);
    }

    public static ChatOptions CreateDefaultChatOptions(
        string? modelId = null,
        float? temperature = null,
        string? reasoningEffort = null,
        bool isOpenAi = false,
        LlmProviderOptions? providerOptions = null)
    {
        return new ChatOptions
        {
            ModelId = modelId,
            Temperature = temperature,
            AdditionalProperties = ModelCapabilityResolver.BuildReasoningProperties(providerOptions, modelId, reasoningEffort, isOpenAi)
        };
    }

    public static string NormalizeReasoningEffort(string? effort, bool isOpenAi = false)
    {
        var val = string.IsNullOrWhiteSpace(effort) ? "xhigh" : effort.Trim().ToLowerInvariant();
        if (isOpenAi && val == "xhigh")
        {
            return "high";
        }
        return val;
    }
}

/// <summary>
/// Default implementation of IOllamaChatClientFactory caching clients by model identifier
/// while falling back to configured LlmProviderOptions (from appsettings.json).
/// </summary>
public class DefaultOllamaChatClientFactory : IOllamaChatClientFactory
{
    private readonly IOptions<LlmProviderOptions> _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly NightCodeForge.Core.Interfaces.IOperationsTelemetryService _telemetryService;
    private readonly NightCodeForge.Core.Interfaces.IOperationsControlService _operationsControlService;
    private readonly TimeProvider _timeProvider;
    private readonly IOptions<RetryPolicyOptions> _retryOptions;
    private readonly ConcurrentDictionary<string, IChatClient> _clientCache = new(StringComparer.OrdinalIgnoreCase);

    public DefaultOllamaChatClientFactory(
        IOptions<LlmProviderOptions> options,
        ILoggerFactory loggerFactory,
        NightCodeForge.Core.Interfaces.IOperationsTelemetryService telemetryService,
        NightCodeForge.Core.Interfaces.IOperationsControlService operationsControlService,
        TimeProvider timeProvider,
        IOptions<RetryPolicyOptions> retryOptions)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _telemetryService = telemetryService ?? throw new ArgumentNullException(nameof(telemetryService));
        _operationsControlService = operationsControlService ?? throw new ArgumentNullException(nameof(operationsControlService));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _retryOptions = retryOptions ?? throw new ArgumentNullException(nameof(retryOptions));
    }

    public IChatClient CreateClient(string? modelId = null) =>
        CreateClient(modelId, OllamaRoutingTarget.Auto);

    public IChatClient CreateClient(string? modelId, OllamaRoutingTarget routingTarget)
    {
        var defaultOpts = _options.Value;
        var effectiveModel = !string.IsNullOrWhiteSpace(modelId) ? modelId : defaultOpts.ModelId;
        var cacheKey = $"{effectiveModel}:{routingTarget}";

        return _clientCache.GetOrAdd(cacheKey, _ =>
        {
            var rawClient = OllamaChatClientFactory.CreateOllama(
                defaultOpts,
                effectiveModel,
                _loggerFactory.CreateLogger("OllamaApiClient"),
                _timeProvider,
                _retryOptions.Value,
                routingTarget);

            var telemetryClient = new TelemetryChatClient(rawClient, _telemetryService, effectiveModel, routingTarget.ToString(), _loggerFactory.CreateLogger<TelemetryChatClient>());
            return new OperationsPauseGatingChatClient(telemetryClient, _operationsControlService);
        });
    }

    public IChatClient CreateClient(AgentProfile? profile) =>
        CreateClient(profile?.ModelName, profile?.RoutingTarget ?? OllamaRoutingTarget.Auto);
}
