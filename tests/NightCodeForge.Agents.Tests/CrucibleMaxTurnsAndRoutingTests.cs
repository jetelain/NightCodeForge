using System.Net;
using Microsoft.Extensions.AI;
using Moq;
using NightCodeForge.Agents.Crucible;
using NightCodeForge.Agents.Inference;
using NightCodeForge.Core.Domain;

namespace NightCodeForge.Agents.Tests;

public class CrucibleMaxTurnsAndRoutingTests
{
    [Fact]
    public void CrucibleAgentHarness_ResolveMaxTurns_ProfileTakesPrecedenceOverHarnessConfig()
    {
        var config = new CrucibleHarnessConfig { MaxTurns = 500 };
        var profile = new AgentProfile { MaxTurns = 350 };

        var resolved = CrucibleAgentHarness.ResolveMaxTurns(config, profile);
        Assert.Equal(350, resolved);
    }

    [Fact]
    public void CrucibleAgentHarness_ResolveMaxTurns_FallsBackToHarnessConfig_WhenProfileNull()
    {
        var config = new CrucibleHarnessConfig { MaxTurns = 450 };
        var profile = new AgentProfile { MaxTurns = null };

        var resolved = CrucibleAgentHarness.ResolveMaxTurns(config, profile);
        Assert.Equal(450, resolved);
    }

    [Fact]
    public void CrucibleAgentHarness_ResolveMaxTurns_FallsBackToCrucibleDefault_WhenBothZeroOrNull()
    {
        var config = new CrucibleHarnessConfig { MaxTurns = 0 };
        var profile = new AgentProfile { MaxTurns = 0 };

        var resolved = CrucibleAgentHarness.ResolveMaxTurns(config, profile);
        Assert.Equal(CrucibleHarnessConfig.DefaultMaxTurns, resolved);
    }

    [Fact]
    public async Task OllamaChatClient_AttachesRoutingTargetHeader_WhenNotAuto()
    {
        HttpRequestMessage? capturedRequest = null;

        var handler = new TestHandler(req =>
        {
            capturedRequest = req;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"message":{"role":"assistant","content":"Hello"},"done":true}""")
            };
        });

        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:11434")
        };

        var client = new OllamaChatClient(httpClient, "qwen2.5-coder:7b", routingTarget: OllamaRoutingTarget.Local);

        var messages = new List<ChatMessage> { new(ChatRole.User, "Hi") };
        await client.GetResponseAsync(messages);

        Assert.NotNull(capturedRequest);
        Assert.True(capturedRequest.Headers.Contains("X-OllamaRouter-Target"));
        Assert.Equal("Local", capturedRequest.Headers.GetValues("X-OllamaRouter-Target").First());
    }

    [Fact]
    public async Task OllamaChatClient_OverridesRoutingTarget_ViaChatOptions()
    {
        HttpRequestMessage? capturedRequest = null;

        var handler = new TestHandler(req =>
        {
            capturedRequest = req;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"message":{"role":"assistant","content":"Hello"},"done":true}""")
            };
        });

        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:11434")
        };

        var client = new OllamaChatClient(httpClient, "qwen2.5-coder:7b", routingTarget: OllamaRoutingTarget.Local);

        var chatOptions = new ChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["X-OllamaRouter-Target"] = "Cloud"
            }
        };

        var messages = new List<ChatMessage> { new(ChatRole.User, "Hi") };
        await client.GetResponseAsync(messages, chatOptions);

        Assert.NotNull(capturedRequest);
        Assert.True(capturedRequest.Headers.Contains("X-OllamaRouter-Target"));
        Assert.Equal("Cloud", capturedRequest.Headers.GetValues("X-OllamaRouter-Target").First());
    }

    [Fact]
    public async Task CrucibleAgentHarness_RunStageAsync_WhenContextWindowNotSetOnProfile_ResolvesDynamicContextWindowFromGateway()
    {
        var mockGateway = new Moq.Mock<NightCodeForge.Core.Interfaces.ILlmGatewayService>();
        mockGateway.Setup(g => g.ResolveContextWindowAsync("qwen3.8:27b", It.IsAny<CancellationToken>()))
            .ReturnsAsync(262144);

        var recordingClient = new FakeRecordingClient();
        var harness = new NightCodeForge.Agents.Tests.Fixtures.TestCrucibleHarnessBuilder
        {
            BaseChatClient = recordingClient,
            GatewayService = mockGateway.Object,
            PathResolver = NightCodeForge.Agents.Tests.Fixtures.TestPathResolver.Create()
        }.Build();

        var task = new ForgeTask { Id = 301, Title = "Context Window Dynamic Discovery", Description = "Test" };
        var workspace = new Workspace { Id = 1, RepositoryPath = Directory.GetCurrentDirectory() };
        var context = new AgentSessionContext
        {
            ForgeTask = task,
            Workspace = workspace,
            Profile = new AgentProfile { Id = "test-agent", ModelName = "qwen3.8:27b", ContextWindowTokens = null, Mode = AgentMode.Full },
            StageId = StandardStageIds.Implementation,
            RequiredMode = AgentMode.Full,
            WorkspacePath = Directory.GetCurrentDirectory()
        };

        var result = await harness.RunStageAsync(context);

        Assert.NotNull(result);
        mockGateway.Verify(g => g.ResolveContextWindowAsync("qwen3.8:27b", It.IsAny<CancellationToken>()), Moq.Times.AtLeastOnce);
    }

    [Fact]
    public async Task CrucibleAgentHarness_RunStageAsync_WhenContextWindowSetOnProfile_SkipsDynamicResolution()
    {
        var mockGateway = new Moq.Mock<NightCodeForge.Core.Interfaces.ILlmGatewayService>();
        mockGateway.Setup(g => g.ResolveContextWindowAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(262144);

        var recordingClient = new FakeRecordingClient();
        var harness = new NightCodeForge.Agents.Tests.Fixtures.TestCrucibleHarnessBuilder
        {
            BaseChatClient = recordingClient,
            GatewayService = mockGateway.Object,
            PathResolver = NightCodeForge.Agents.Tests.Fixtures.TestPathResolver.Create()
        }.Build();

        var task = new ForgeTask { Id = 302, Title = "Context Window Explicit", Description = "Test" };
        var workspace = new Workspace { Id = 1, RepositoryPath = Directory.GetCurrentDirectory() };
        var context = new AgentSessionContext
        {
            ForgeTask = task,
            Workspace = workspace,
            Profile = new AgentProfile { Id = "test-agent", ModelName = "qwen3.8:27b", ContextWindowTokens = 65536, Mode = AgentMode.Full },
            StageId = StandardStageIds.Implementation,
            RequiredMode = AgentMode.Full,
            WorkspacePath = Directory.GetCurrentDirectory()
        };

        var result = await harness.RunStageAsync(context);

        Assert.NotNull(result);
        mockGateway.Verify(g => g.ResolveContextWindowAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Moq.Times.Never);
    }

    private class FakeRecordingClient : IChatClient
    {
        public ChatClientMetadata Metadata => new("FakeRecordingClient");
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done.")));
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "Done.");
            await Task.CompletedTask;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private class TestHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
        public TestHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request));
        }
    }
}
