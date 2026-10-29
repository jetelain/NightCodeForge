using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Inference;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Resilience;
using OpenAI;
using OpenAI.Chat;

namespace NightCodeForge.Agents.Inference;

/// <summary>
/// Chat client factory for OpenAI-compatible inference endpoints (vLLM, LM Studio, OpenRouter, DeepSeek, OpenAI).
/// Validates models synchronously against the allowlist cache and manual model configuration before construction.
/// Uses standard Bearer token authentication and enforces clean header hygiene (suppressing X-OllamaRouter-* headers).
/// </summary>
public sealed class OpenAiCompatibleChatClientFactory : LlmChatClientFactoryBase
{
    private readonly IOpenAiModelAllowlistCache _allowlistCache;
    private readonly IDefaultModelSelector _defaultModelSelector;

    public OpenAiCompatibleChatClientFactory(
        IOptions<LlmProviderOptions> options,
        ILoggerFactory loggerFactory,
        IOperationsTelemetryService telemetryService,
        IOperationsControlService operationsControlService,
        TimeProvider timeProvider,
        IOptions<RetryPolicyOptions> retryOptions,
        IOpenAiModelAllowlistCache allowlistCache,
        IDefaultModelSelector defaultModelSelector)
        : base(options, loggerFactory, telemetryService, operationsControlService, timeProvider, retryOptions)
    {
        _allowlistCache = allowlistCache ?? throw new ArgumentNullException(nameof(allowlistCache));
        _defaultModelSelector = defaultModelSelector ?? throw new ArgumentNullException(nameof(defaultModelSelector));
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

        // 2. Unauthenticated local endpoints handling:
        // ApiKeyCredential throws ArgumentException on null/empty strings.
        // For local endpoints without auth (e.g. LM Studio, local vLLM), inject KnownDefaultModels.LocalAuthBypassKey.
        var apiKey = !string.IsNullOrWhiteSpace(options.OpenAiCompatible?.ApiKey)
            ? options.OpenAiCompatible.ApiKey.Trim()
            : KnownDefaultModels.LocalAuthBypassKey;

        var clientOptions = new OpenAIClientOptions
        {
            Endpoint = new Uri(options.EffectiveEndpoint.TrimEnd('/') + "/")
        };

        if (!string.IsNullOrWhiteSpace(options.OpenAiCompatible?.OrganizationId))
        {
            clientOptions.OrganizationId = options.OpenAiCompatible.OrganizationId.Trim();
        }

        // 3. Pipeline transport using shared SocketsHttpHandler and TransientHttpErrorRetryHandler
        var retryHandler = new TransientHttpErrorRetryHandler(
            socketsHandler,
            _loggerFactory.CreateLogger("OpenAiApiClient"),
            _timeProvider,
            _retryOptions.Value);

        var httpClient = new HttpClient(retryHandler, disposeHandler: false)
        {
            Timeout = TimeSpan.FromMinutes(Math.Max(1, options.RequestTimeoutMinutes))
        };

        // Strict header hygiene: suppress X-OllamaRouter-* headers
        clientOptions.Transport = new HttpClientPipelineTransport(httpClient);

        var openAiClient = new OpenAIClient(new ApiKeyCredential(apiKey), clientOptions);
        return openAiClient.GetChatClient(effectiveModel).AsIChatClient();
    }
}

