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
    public void OllamaRouterChatClientFactory_CachesClients_AndResolvesProfileModel()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new LlmProviderOptions
        {
            Endpoint = "http://localhost:11434",
            ModelId = "default-coder:7b"
        });

        var mockTelemetry = new Moq.Mock<NightCodeForge.Core.Interfaces.IOperationsTelemetryService>().Object;
        var mockOps = new Moq.Mock<NightCodeForge.Core.Interfaces.IOperationsControlService>().Object;
        var retryOptions = Microsoft.Extensions.Options.Options.Create(new NightCodeForge.Core.Resilience.RetryPolicyOptions());
        var factory = new OllamaRouterChatClientFactory(
            options,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            mockTelemetry,
            mockOps,
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
    public void OllamaRouterChatClientFactory_CreateClient_WithRoutingTarget_CachesAndResolves()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new LlmProviderOptions
        {
            Endpoint = "http://localhost:11434",
            ModelId = "default-coder:7b"
        });

        var mockTelemetry = new Moq.Mock<NightCodeForge.Core.Interfaces.IOperationsTelemetryService>().Object;
        var mockOps = new Moq.Mock<NightCodeForge.Core.Interfaces.IOperationsControlService>().Object;
        var retryOptions = Microsoft.Extensions.Options.Options.Create(new NightCodeForge.Core.Resilience.RetryPolicyOptions());
        var factory = new OllamaRouterChatClientFactory(
            options,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            mockTelemetry,
            mockOps,
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

    [Fact]
    public async Task OllamaSharpChatClientAdapter_GetResponseAsync_PreservesThinkingOnOutgoingAndIncomingMessages()
    {
        string? capturedRequestJson = null;
        var handler = new CapturingHttpMessageHandler(async request =>
        {
            capturedRequestJson = await request.Content!.ReadAsStringAsync();
            const string responseJson = """
                {"model":"qwen3.5:cloud","created_at":"2026-11-01T19:45:00Z","message":{"role":"assistant","content":"Updated file.","thinking":"Now I will edit the specification."},"done":true,"done_reason":"stop","prompt_eval_count":120,"eval_count":45}
                """;
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, System.Text.Encoding.UTF8, "application/json")
            };
        });

        using var client = OllamaChatClientFactory.CreateOllama(
            new LlmProviderOptions { Endpoint = "http://localhost:11434", DefaultModelId = "qwen3.5:cloud" },
            modelId: "qwen3.5:cloud",
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            TimeProvider.System,
            new NightCodeForge.Core.Resilience.RetryPolicyOptions(),
            OllamaRoutingTarget.Auto,
            handler,
            _ => { });

        var assistantTurnWithThinkingAndToolCall = new ChatMessage(
            ChatRole.Assistant,
            new AIContent[]
            {
                new TextContent(string.Empty),
                new TextReasoningContent("Let me inspect the task specification file first."),
                new FunctionCallContent("call_1", "read_file", new Dictionary<string, object?> { ["path"] = "docs/features/24.md" })
            });

        var toolResultTurn = new ChatMessage(
            ChatRole.Tool,
            new AIContent[]
            {
                new FunctionResultContent("call_1", "# Task 24")
            });

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "You are a reviewer."),
            new(ChatRole.User, "Review task 24."),
            assistantTurnWithThinkingAndToolCall,
            toolResultTurn
        };

        var response = await client.GetResponseAsync(messages);

        Assert.NotNull(capturedRequestJson);
        using var doc = System.Text.Json.JsonDocument.Parse(capturedRequestJson!);
        var sentMessages = doc.RootElement.GetProperty("messages");
        Assert.Equal(4, sentMessages.GetArrayLength());

        var sentAssistant = sentMessages[2];
        Assert.Equal("assistant", sentAssistant.GetProperty("role").GetString());
        Assert.Equal("Let me inspect the task specification file first.", sentAssistant.GetProperty("thinking").GetString());
        Assert.Equal(1, sentAssistant.GetProperty("tool_calls").GetArrayLength());

        // Verify incoming response thinking is mapped back to TextReasoningContent
        var responseMessage = Assert.Single(response.Messages);
        var reasoning = Assert.Single(responseMessage.Contents.OfType<TextReasoningContent>());
        Assert.Equal("Now I will edit the specification.", reasoning.Text);
        Assert.Equal("Updated file.", responseMessage.Text);
    }

    [Fact]
    public async Task OllamaSharpChatClientAdapter_GetStreamingResponseAsync_PreservesThinkingInMultiTurnRoundTrip()
    {
        var capturedRequests = new List<string>();
        var callCount = 0;

        var handler = new CapturingHttpMessageHandler(async request =>
        {
            capturedRequests.Add(await request.Content!.ReadAsStringAsync());
            callCount++;

            var ndjson = callCount == 1
                ? string.Join("\n",
                    """{"model":"qwen3.5:cloud","created_at":"2026-11-01T19:45:00Z","message":{"role":"assistant","content":"","thinking":"Thinking part 1. "},"done":false}""",
                    """{"model":"qwen3.5:cloud","created_at":"2026-11-01T19:45:01Z","message":{"role":"assistant","content":"","thinking":"Thinking part 2.","tool_calls":[{"function":{"name":"read_file","arguments":{"path":"docs/features/24.md"}}}]},"done":false}""",
                    """{"model":"qwen3.5:cloud","created_at":"2026-11-01T19:45:02Z","message":{"role":"assistant","content":""},"done":true,"done_reason":"stop","prompt_eval_count":100,"eval_count":30}""")
                : string.Join("\n",
                    """{"model":"qwen3.5:cloud","created_at":"2026-11-01T19:45:05Z","message":{"role":"assistant","content":"Done editing.","thinking":"File looks clear."},"done":true,"done_reason":"stop","prompt_eval_count":150,"eval_count":20}""");

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(ndjson + "\n", System.Text.Encoding.UTF8, "application/x-ndjson")
            };
        });

        using var client = OllamaChatClientFactory.CreateOllama(
            new LlmProviderOptions { Endpoint = "http://localhost:11434", DefaultModelId = "qwen3.5:cloud" },
            modelId: "qwen3.5:cloud",
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            TimeProvider.System,
            new NightCodeForge.Core.Resilience.RetryPolicyOptions(),
            OllamaRoutingTarget.Auto,
            handler,
            _ => { });

        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "Please review task 24.")
        };

        // Turn 1: stream response and aggregate into ChatResponse using Microsoft.Extensions.AI's ToChatResponse
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(history))
        {
            updates.Add(update);
        }

        var turn1Response = updates.ToChatResponse();
        history.AddRange(turn1Response.Messages);

        var toolCall = turn1Response.Messages
            .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
            .Single();
        history.Add(new ChatMessage(ChatRole.Tool, new AIContent[] { new FunctionResultContent(toolCall.CallId, "file content") }));

        // Turn 2: send aggregated history back through streaming API
        await foreach (var _ in client.GetStreamingResponseAsync(history))
        {
        }

        Assert.Equal(2, capturedRequests.Count);
        using var turn2Doc = System.Text.Json.JsonDocument.Parse(capturedRequests[1]);
        var turn2Messages = turn2Doc.RootElement.GetProperty("messages");
        Assert.Equal(3, turn2Messages.GetArrayLength());

        var assistantMessageJson = turn2Messages[1];
        Assert.Equal("assistant", assistantMessageJson.GetProperty("role").GetString());
        Assert.Equal("Thinking part 1. Thinking part 2.", assistantMessageJson.GetProperty("thinking").GetString());
        Assert.Equal(1, assistantMessageJson.GetProperty("tool_calls").GetArrayLength());
    }

    private sealed class CapturingHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _responder;

        public CapturingHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _responder(request);
    }

    private class TestOllamaChatClientFactory : ILlmChatClientFactory
    {
        private readonly IChatClient _client;
        public TestOllamaChatClientFactory(IChatClient client) => _client = client;
        public IChatClient CreateClient(string? modelId = null) => _client;
        public IChatClient CreateClient(string? modelId, OllamaRoutingTarget routingTarget) => _client;
        public IChatClient CreateClient(Core.Domain.AgentProfile? profile) => _client;
        public IChatClient CreateClient(Core.Domain.AgentProfile? profile, string? sessionId) => _client;
        public IChatClient CreateClient(string? modelId, OllamaRoutingTarget routingTarget, string? sessionId) => _client;
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
