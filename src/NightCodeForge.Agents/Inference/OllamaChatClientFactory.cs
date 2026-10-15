using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightCodeForge.Core.Domain;
using OllamaSharp;

namespace NightCodeForge.Agents.Inference;

/// <summary>
/// Factory for instantiating IChatClient via OllamaSharp's OllamaApiClient with tuned request timeouts.
/// </summary>
public static class OllamaChatClientFactory
{
    public static IChatClient CreateOllama(LlmProviderOptions options, string? modelId = null, ILogger? logger = null, OllamaRoutingTarget routingTarget = OllamaRoutingTarget.Auto)
    {
        var endpoint = string.IsNullOrWhiteSpace(options.Endpoint) ? "http://localhost:11434" : options.Endpoint;
        var timeoutMinutes = Math.Max(1, options.RequestTimeoutMinutes);
        var effectiveModel = !string.IsNullOrWhiteSpace(modelId) ? modelId : options.ModelId;

        var httpClient = new HttpClient
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

    public static IChatClient CreateOllama(LlmProviderOptions options, AgentProfile? profile, ILogger? logger = null)
    {
        var model = !string.IsNullOrWhiteSpace(profile?.ModelName) ? profile.ModelName : options.ModelId;
        var routingTarget = profile?.RoutingTarget ?? OllamaRoutingTarget.Auto;
        return CreateOllama(options, model, logger, routingTarget);
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
    private readonly ILoggerFactory? _loggerFactory;
    private readonly ConcurrentDictionary<string, IChatClient> _clientCache = new(StringComparer.OrdinalIgnoreCase);

    public DefaultOllamaChatClientFactory(
        IOptions<LlmProviderOptions> options,
        ILoggerFactory? loggerFactory = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _loggerFactory = loggerFactory;
    }

    public IChatClient CreateClient(string? modelId = null)
    {
        var defaultOpts = _options.Value;
        var effectiveModel = !string.IsNullOrWhiteSpace(modelId) ? modelId : defaultOpts.ModelId;
        var cacheKey = $"{effectiveModel}:{OllamaRoutingTarget.Auto}";

        return _clientCache.GetOrAdd(cacheKey, _ =>
            OllamaChatClientFactory.CreateOllama(defaultOpts, effectiveModel, _loggerFactory?.CreateLogger("OllamaApiClient"), OllamaRoutingTarget.Auto));
    }

    public IChatClient CreateClient(AgentProfile? profile)
    {
        var defaultOpts = _options.Value;
        var effectiveModel = !string.IsNullOrWhiteSpace(profile?.ModelName) ? profile.ModelName : defaultOpts.ModelId;
        var routingTarget = profile?.RoutingTarget ?? OllamaRoutingTarget.Auto;
        var cacheKey = $"{effectiveModel}:{routingTarget}";

        return _clientCache.GetOrAdd(cacheKey, _ =>
            OllamaChatClientFactory.CreateOllama(defaultOpts, effectiveModel, _loggerFactory?.CreateLogger("OllamaApiClient"), routingTarget));
    }
}
