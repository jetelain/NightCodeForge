using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Inference;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Resilience;
using NightCodeForge.Core.Security;
using NightCodeForge.Core.Streaming;
using OpenAI;
using OpenAI.Chat;

namespace NightCodeForge.Agents.Inference;

/// <summary>
/// Chat client factory for OpenAI-compatible inference endpoints (vLLM, LM Studio, OpenRouter, DeepSeek, OpenAI).
/// Validates models synchronously against the allowlist cache and manual model configuration before construction,
/// resolves endpoint-scoped API keys via <see cref="IApiKeySecretStore"/> (with <c>OPENAI_API_KEY</c> fallback),
/// evicts cached session-less clients on credential change, and enforces clean header hygiene.
/// </summary>
public sealed class OpenAiCompatibleChatClientFactory : LlmChatClientFactoryBase
{
    private readonly IOpenAiModelAllowlistCache _allowlistCache;
    private readonly IDefaultModelSelector _defaultModelSelector;
    private readonly IApiKeySecretStore _secretStore;

    public OpenAiCompatibleChatClientFactory(
        IOptions<LlmProviderOptions> options,
        ILoggerFactory loggerFactory,
        IOperationsTelemetryService telemetryService,
        IOperationsControlService operationsControlService,
        TimeProvider timeProvider,
        IOptions<LlmRetryPolicyOptions> retryOptions,
        IOpenAiModelAllowlistCache allowlistCache,
        IDefaultModelSelector defaultModelSelector,
        IAgentActivityStreamer activityStreamer,
        IApiKeySecretStore secretStore)
        : base(options, loggerFactory, telemetryService, operationsControlService, timeProvider, retryOptions, activityStreamer)
    {
        _allowlistCache = allowlistCache ?? throw new ArgumentNullException(nameof(allowlistCache));
        _defaultModelSelector = defaultModelSelector ?? throw new ArgumentNullException(nameof(defaultModelSelector));
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _secretStore.SecretChanged += OnSecretChanged;
    }

    private void OnSecretChanged(object? sender, ApiKeySecretChangedEventArgs e)
    {
        if (e.Kind == ApiKeySecretKind.OpenAiCompatible)
        {
            InvalidateClientCache();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _secretStore.SecretChanged -= OnSecretChanged;
        }

        base.Dispose(disposing);
    }

    protected override string ResolveEffectiveModelId(string? modelId, LlmProviderOptions options)
    {
        if (!string.IsNullOrWhiteSpace(modelId))
        {
            return modelId.Trim();
        }

        if (!string.IsNullOrWhiteSpace(options.DefaultModelId))
        {
            return options.DefaultModelId.Trim();
        }

        var discovered = _allowlistCache.GetDiscoveredModels();
        if (discovered.Count > 0)
        {
            var selected = _defaultModelSelector.SelectDefaultModel(discovered);
            if (selected != null && !string.IsNullOrWhiteSpace(selected.ModelId))
            {
                return selected.ModelId;
            }
        }

        if (options.OpenAiCompatible?.AllowedModels is { Count: > 0 } allowed)
        {
            var candidates = allowed
                .Where(m => !string.IsNullOrWhiteSpace(m.ModelId))
                .Select(m => new AvailableModelInfo(
                    ModelId: m.ModelId.Trim(),
                    DisplayName: m.DisplayName ?? m.ModelId.Trim(),
                    PromptCostPerMillion: m.PromptCostPerMillion,
                    CompletionCostPerMillion: m.CompletionCostPerMillion))
                .ToList();

            var selected = _defaultModelSelector.SelectDefaultModel(candidates);
            if (selected != null && !string.IsNullOrWhiteSpace(selected.ModelId))
            {
                return selected.ModelId;
            }
        }

        return KnownDefaultModels.DefaultBaseline;
    }

    protected override IChatClient CreateRawChatClient(
        LlmProviderOptions options,
        string effectiveModel,
        OllamaRoutingTarget routingTarget,
        string? sessionId,
        SocketsHttpHandler socketsHandler)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(effectiveModel);

        // 1. Fail-fast allowlist check
        OpenAiModelAllowlistValidator.ValidateModelAllowed(options, _allowlistCache, effectiveModel);

        // 2. Resolve endpoint-scoped key from OS keyring (or OPENAI_API_KEY env fallback);
        //    ApiKeyCredential throws ArgumentException on null/empty strings, so unauthenticated
        //    local endpoints (LM Studio, local vLLM) fall back to KnownDefaultModels.LocalAuthBypassKey.
        var resolvedKey = _secretStore.ResolveEffectiveOpenAiApiKey(options.EffectiveEndpoint);
        var apiKey = !string.IsNullOrWhiteSpace(resolvedKey)
            ? resolvedKey
            : KnownDefaultModels.LocalAuthBypassKey;

        // 3. Pipeline transport using shared SocketsHttpHandler and TransientHttpErrorRetryHandler
        var effectiveRetryOptions = CreateEffectiveRetryOptions(sessionId);
        var timeoutMinutes = Math.Max(1, options.RequestTimeoutMinutes);
        effectiveRetryOptions.PerAttemptTimeout ??= TimeSpan.FromMinutes(timeoutMinutes);

        var clientOptions = new OpenAIClientOptions
        {
            Endpoint = new Uri(options.EffectiveEndpoint.TrimEnd('/') + "/"),
            RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
            NetworkTimeout = effectiveRetryOptions.PerAttemptTimeout.HasValue ? Timeout.InfiniteTimeSpan : TimeSpan.FromMinutes(timeoutMinutes)
        };

        if (!string.IsNullOrWhiteSpace(options.OpenAiCompatible?.OrganizationId))
        {
            clientOptions.OrganizationId = options.OpenAiCompatible.OrganizationId.Trim();
        }

        var retryHandler = new TransientHttpErrorRetryHandler(
            socketsHandler,
            _loggerFactory.CreateLogger("OpenAiApiClient"),
            _timeProvider,
            effectiveRetryOptions,
            _operationsControlService);

        var httpClient = new HttpClient(retryHandler, disposeHandler: false)
        {
            Timeout = effectiveRetryOptions.PerAttemptTimeout.HasValue ? Timeout.InfiniteTimeSpan : TimeSpan.FromMinutes(timeoutMinutes)
        };

        LlmCorrelationHeaders.Apply(httpClient, sessionId);

        // Strict header hygiene: suppress X-OllamaRouter-* headers
        clientOptions.Transport = new HttpClientPipelineTransport(httpClient);

        var openAiClient = new OpenAIClient(new ApiKeyCredential(apiKey), clientOptions);
        return openAiClient.GetChatClient(effectiveModel).AsIChatClient();
    }
}
