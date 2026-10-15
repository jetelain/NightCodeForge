using System.Net;
using Microsoft.Extensions.AI;
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
