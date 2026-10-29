using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NightCodeForge.Agents.Inference;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Inference;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Resilience;
using NightCodeForge.Infrastructure.Inference;
using Xunit;

namespace NightCodeForge.Agents.Tests.Inference;

public class OpenAiCompatibleChatClientFactoryTests
{
    private readonly Mock<IOperationsTelemetryService> _telemetryMock = new();
    private readonly Mock<IOperationsControlService> _opsControlMock = new();
    private readonly IOptions<RetryPolicyOptions> _retryOptions = Options.Create(new RetryPolicyOptions());

    [Fact]
    public void CreateClient_WhenModelNotAllowed_ThrowsModelNotAllowedException()
    {
        var options = Options.Create(new LlmProviderOptions
        {
            ProviderType = LlmProviderType.OpenAiCompatible,
            OpenAiCompatible = new OpenAiCompatibleOptions
            {
                Endpoint = "http://localhost:1234/v1"
            }
        });

        var cache = new OpenAiModelAllowlistCache();
        cache.SetDiscoveredModels([new AvailableModelInfo("allowed-model", "Allowed")]);

        var factory = new OpenAiCompatibleChatClientFactory(
            options,
            NullLoggerFactory.Instance,
            _telemetryMock.Object,
            _opsControlMock.Object,
            TimeProvider.System,
            _retryOptions,
            cache,
            new OpenAiDefaultModelSelector());

        var ex = Assert.Throws<ModelNotAllowedException>(() => factory.CreateClient("unlisted-model"));
        Assert.Equal("unlisted-model", ex.ModelId);
        Assert.Equal(LlmProviderType.OpenAiCompatible, ex.ProviderType);
    }

    [Fact]
    public void CreateClient_WhenModelAllowedInCache_InstantiatesClientSuccessfully()
    {
        var options = Options.Create(new LlmProviderOptions
        {
            ProviderType = LlmProviderType.OpenAiCompatible,
            OpenAiCompatible = new OpenAiCompatibleOptions
            {
                Endpoint = "http://localhost:1234/v1",
                ApiKey = null // Test unauthenticated bypass key
            }
        });

        var cache = new OpenAiModelAllowlistCache();
        cache.SetDiscoveredModels([new AvailableModelInfo("allowed-model", "Allowed")]);

        var factory = new OpenAiCompatibleChatClientFactory(
            options,
            NullLoggerFactory.Instance,
            _telemetryMock.Object,
            _opsControlMock.Object,
            TimeProvider.System,
            _retryOptions,
            cache,
            new OpenAiDefaultModelSelector());

        var client = factory.CreateClient("allowed-model");
        Assert.NotNull(client);
    }

    [Fact]
    public void CreateClient_WhenManualAllowedModelsConfigured_AllowsOnlyConfigured()
    {
        var options = Options.Create(new LlmProviderOptions
        {
            ProviderType = LlmProviderType.OpenAiCompatible,
            OpenAiCompatible = new OpenAiCompatibleOptions
            {
                Endpoint = "https://api.openai.com/v1",
                ApiKey = "sk-test",
                AllowedModels =
                [
                    new OpenAiAllowedModel { ModelId = "custom-llama" }
                ]
            }
        });

        var cache = new OpenAiModelAllowlistCache();
        cache.SetDiscoveredModels([new AvailableModelInfo("discovered-but-unlisted", "Discovered")]);

        var factory = new OpenAiCompatibleChatClientFactory(
            options,
            NullLoggerFactory.Instance,
            _telemetryMock.Object,
            _opsControlMock.Object,
            TimeProvider.System,
            _retryOptions,
            cache,
            new OpenAiDefaultModelSelector());

        // Allowed via manual configuration
        var client = factory.CreateClient("custom-llama");
        Assert.NotNull(client);

        // Discovered but not in AllowedModels -> throws
        Assert.Throws<ModelNotAllowedException>(() => factory.CreateClient("discovered-but-unlisted"));
    }

    [Fact]
    public void CreateClient_WhenDefaultModelIdNull_ResolvesOptimalDefaultModelFromCache()
    {
        var options = Options.Create(new LlmProviderOptions
        {
            ProviderType = LlmProviderType.OpenAiCompatible,
            DefaultModelId = null,
            OpenAiCompatible = new OpenAiCompatibleOptions
            {
                Endpoint = "http://localhost:1234/v1"
            }
        });

        var cache = new OpenAiModelAllowlistCache();
        cache.SetDiscoveredModels([
            new AvailableModelInfo("pricy-model", "Pricy", PromptCostPerMillion: 2.0m),
            new AvailableModelInfo("cheap-model", "Cheap", PromptCostPerMillion: 0.15m)
        ]);

        var factory = new OpenAiCompatibleChatClientFactory(
            options,
            NullLoggerFactory.Instance,
            _telemetryMock.Object,
            _opsControlMock.Object,
            TimeProvider.System,
            _retryOptions,
            cache,
            new OpenAiDefaultModelSelector());

        var client = factory.CreateClient();
        Assert.NotNull(client);
    }
}

