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
        HttpMessageHandler innerHandler,
        Action<HttpClient> configureRouting)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(retryOptions);
        ArgumentNullException.ThrowIfNull(innerHandler);
        ArgumentNullException.ThrowIfNull(configureRouting);

        var endpoint = string.IsNullOrWhiteSpace(options.Endpoint) ? "http://localhost:11434" : options.Endpoint;
        var timeoutMinutes = Math.Max(1, options.RequestTimeoutMinutes);
        var effectiveModel = !string.IsNullOrWhiteSpace(modelId)
            ? modelId.Trim()
            : (!string.IsNullOrWhiteSpace(options.DefaultModelId) ? options.DefaultModelId.Trim() : KnownDefaultModels.DefaultBaseline);

        var retryHandler = new TransientHttpErrorRetryHandler(
            innerHandler,
            logger,
            timeProvider,
            retryOptions);

        var httpClient = new HttpClient(retryHandler, disposeHandler: false)
        {
            BaseAddress = new Uri(endpoint),
            Timeout = TimeSpan.FromMinutes(timeoutMinutes)
        };

        configureRouting(httpClient);

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

        return CreateOllama(options, modelId, logger, timeProvider, retryOptions, routingTarget, socketsHandler, client => OllamaRoutingHeaders.Apply(client, routingTarget, sessionId: null));
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

