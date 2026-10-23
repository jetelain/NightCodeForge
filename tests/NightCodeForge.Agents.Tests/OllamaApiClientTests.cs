using Microsoft.Extensions.AI;
using NightCodeForge.Agents.Inference;
using NightCodeForge.Core.Domain;
using OllamaSharp;

namespace NightCodeForge.Agents.Tests;

public class OllamaApiClientTests
{
    [Fact]
    public void OllamaApiClient_Implements_IChatClient()
    {
        var httpClient = new HttpClient
        {
            BaseAddress = new Uri("http://localhost:11434"),
            Timeout = TimeSpan.FromMinutes(15)
        };

        var client = new OllamaApiClient(httpClient, "qwen2.5-coder:7b", null);
        Assert.NotNull(client);
        Assert.IsAssignableFrom<IChatClient>(client);
    }

    private static IChatClient CreateTestClient(LlmProviderOptions options, string? modelId = null) =>
        OllamaChatClientFactory.CreateOllama(
            options,
            modelId,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            TimeProvider.System,
            new NightCodeForge.Core.Resilience.RetryPolicyOptions(),
            OllamaRoutingTarget.Auto);

    [Fact]
    public void OllamaChatClientFactory_CreatesClient_WithCustomTimeoutAndEndpoint()
    {
        var options = new LlmProviderOptions
        {
            Endpoint = "http://192.168.1.100:11434",
            ModelId = "deepseek-coder:33b",
            RequestTimeoutMinutes = 25
        };

        var client = CreateTestClient(options);
        Assert.NotNull(client);
        Assert.IsAssignableFrom<IChatClient>(client);
    }

    [Fact]
    public void OllamaChatClientFactory_SupportsProfileModel_WithFallbackToDefault()
    {
        var defaultOptions = new LlmProviderOptions
        {
            Endpoint = "http://localhost:11434",
            ModelId = "qwen2.5-coder:7b"
        };

        // When profile specifies a model
        var profileWithModel = new Core.Domain.AgentProfile
        {
            Name = "Reviewer",
            ModelName = "deepseek-r1:14b"
        };

        var clientForProfile = CreateTestClient(defaultOptions, profileWithModel.ModelName);
        Assert.NotNull(clientForProfile);

        // When profile does NOT specify a model (empty/null), fallback to appsettings ModelId
        var profileWithoutModel = new Core.Domain.AgentProfile
        {
            Name = "Triage",
            ModelName = ""
        };

        var clientDefault = CreateTestClient(defaultOptions, profileWithoutModel.ModelName);
        Assert.NotNull(clientDefault);
    }

    [Fact]
    public void DefaultOllamaChatClientFactory_CachesClients_AndResolvesProfileModel()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new LlmProviderOptions
        {
            Endpoint = "http://localhost:11434",
            ModelId = "default-coder:7b"
        });

        var mockTelemetry = new Moq.Mock<NightCodeForge.Core.Interfaces.IOperationsTelemetryService>().Object;
        var retryOptions = Microsoft.Extensions.Options.Options.Create(new NightCodeForge.Core.Resilience.RetryPolicyOptions());
        var factory = new DefaultOllamaChatClientFactory(
            options,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            mockTelemetry,
            TimeProvider.System,
            retryOptions);

        var profileCoder = new Core.Domain.AgentProfile { Name = "Coder", ModelName = "qwen2.5-coder:32b" };
        var client1 = factory.CreateClient(profileCoder);
        var client2 = factory.CreateClient(profileCoder);

        // Same model resolves cached instance
        Assert.Same(client1, client2);

        // Profile without model resolves default model
        var profileDefault = new Core.Domain.AgentProfile { Name = "DefaultAgent", ModelName = "" };
        var clientDefault = factory.CreateClient(profileDefault);
        Assert.NotSame(client1, clientDefault);

        var clientDefaultExplicit = factory.CreateClient();
        Assert.Same(clientDefault, clientDefaultExplicit);
    }

    [Fact]
    public void DefaultOllamaChatClientFactory_CreateClient_WithRoutingTarget_CachesAndResolves()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new LlmProviderOptions
        {
            Endpoint = "http://localhost:11434",
            ModelId = "default-coder:7b"
        });

        var mockTelemetry = new Moq.Mock<NightCodeForge.Core.Interfaces.IOperationsTelemetryService>().Object;
        var retryOptions = Microsoft.Extensions.Options.Options.Create(new NightCodeForge.Core.Resilience.RetryPolicyOptions());
        var factory = new DefaultOllamaChatClientFactory(
            options,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            mockTelemetry,
            TimeProvider.System,
            retryOptions);

        var clientCloud1 = factory.CreateClient("model-x", OllamaRoutingTarget.Cloud);
        var clientCloud2 = factory.CreateClient("model-x", OllamaRoutingTarget.Cloud);
        var clientLocal = factory.CreateClient("model-x", OllamaRoutingTarget.Local);

        Assert.NotNull(clientCloud1);
        Assert.Same(clientCloud1, clientCloud2);
        Assert.NotSame(clientCloud1, clientLocal);

        // Null model falls back to default model
        var clientDefaultCloud = factory.CreateClient(null, OllamaRoutingTarget.Cloud);
        Assert.NotNull(clientDefaultCloud);
        Assert.NotSame(clientCloud1, clientDefaultCloud);
    }

    [Fact]
    public async Task CrucibleAgentHarness_UsesProfileModel_FromChatClientFactory()
    {
        string? capturedModelId = null;

        var mockChatClient = new TestCapturingChatClient((opts) =>
        {
            capturedModelId = opts?.ModelId;
        });

        var harness = new Fixtures.TestCrucibleHarnessBuilder
        {
            BaseChatClient = mockChatClient,
            PathResolver = Fixtures.TestPathResolver.Create()
        }.Build();

        var profile = new Core.Domain.AgentProfile
        {
            Id = "prof_custom",
            Name = "CustomCoder",
            ModelName = "deepseek-coder:67b"
        };

        var sessionContext = new Core.Domain.AgentSessionContext
        {
            Profile = profile,
            StageId = "stage-2",
            ForgeTask = new Core.Domain.ForgeTask { Id = 1, Title = "Test ForgeTask", Description = "Implement feature" },
            Workspace = new Core.Domain.Workspace { Id = 1, RepositoryPath = "C:\\test" }
        };

        var result = await harness.RunStageAsync(sessionContext);

        Assert.NotNull(result);
        Assert.Equal("deepseek-coder:67b", capturedModelId);
    }

    private class TestOllamaChatClientFactory : IOllamaChatClientFactory
    {
        private readonly IChatClient _client;
        public TestOllamaChatClientFactory(IChatClient client) => _client = client;
        public IChatClient CreateClient(string? modelId = null) => _client;
        public IChatClient CreateClient(string? modelId, OllamaRoutingTarget routingTarget) => _client;
        public IChatClient CreateClient(Core.Domain.AgentProfile? profile) => _client;
    }

    private class TestCapturingChatClient : IChatClient
    {
        private readonly Action<ChatOptions?> _onCall;
        public TestCapturingChatClient(Action<ChatOptions?> onCall) => _onCall = onCall;
        public void Dispose() { }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            _onCall(options);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done")));
        }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _onCall(options);
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "Done");
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }
}
