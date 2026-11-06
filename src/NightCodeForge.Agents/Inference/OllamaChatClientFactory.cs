using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
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
        Action<HttpClient> configureRouting) =>
        CreateOllama(options, modelId, logger, timeProvider, retryOptions, routingTarget, innerHandler, configureRouting, operationsControlService: null);

    public static IChatClient CreateOllama(
        LlmProviderOptions options,
        string? modelId,
        ILogger logger,
        TimeProvider timeProvider,
        RetryPolicyOptions retryOptions,
        OllamaRoutingTarget routingTarget,
        HttpMessageHandler innerHandler,
        Action<HttpClient> configureRouting,
        IOperationsControlService? operationsControlService)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(retryOptions);
        ArgumentNullException.ThrowIfNull(innerHandler);
        ArgumentNullException.ThrowIfNull(configureRouting);

        var endpoint = string.IsNullOrWhiteSpace(options.Endpoint) ? "http://localhost:11434" : options.Endpoint;
        var timeoutMinutes = Math.Max(1, options.RequestTimeoutMinutes);
        var effectiveRetryOptions = retryOptions.PerAttemptTimeout.HasValue
            ? retryOptions
            : new RetryPolicyOptions
            {
                MaxRetries = retryOptions.MaxRetries,
                BaseDelay = retryOptions.BaseDelay,
                MaxDelay = retryOptions.MaxDelay,
                MaxRetryDuration = retryOptions.MaxRetryDuration,
                MaxTimeoutRetries = retryOptions.MaxTimeoutRetries,
                PerAttemptTimeout = TimeSpan.FromMinutes(timeoutMinutes),
                OnRetry = retryOptions.OnRetry,
                OnRetryAsync = retryOptions.OnRetryAsync
            };
        var effectiveModel = !string.IsNullOrWhiteSpace(modelId)
            ? modelId.Trim()
            : (!string.IsNullOrWhiteSpace(options.DefaultModelId) ? options.DefaultModelId.Trim() : KnownDefaultModels.DefaultBaseline);

        var retryHandler = operationsControlService != null
            ? new TransientHttpErrorRetryHandler(innerHandler, logger, timeProvider, effectiveRetryOptions, operationsControlService)
            : new TransientHttpErrorRetryHandler(innerHandler, logger, timeProvider, effectiveRetryOptions);

        var httpClient = new HttpClient(retryHandler, disposeHandler: false)
        {
            BaseAddress = new Uri(endpoint),
            Timeout = effectiveRetryOptions.PerAttemptTimeout.HasValue ? Timeout.InfiniteTimeSpan : TimeSpan.FromMinutes(timeoutMinutes)
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
        var socketsHandler = OllamaHttpConstants.CreateSocketsHttpHandler();

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
}
