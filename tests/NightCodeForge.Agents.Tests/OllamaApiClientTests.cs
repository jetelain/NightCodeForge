using Microsoft.Extensions.AI;
using NightCodeForge.Agents.Inference;
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

    [Fact]
    public void OllamaChatClientFactory_CreatesClient_WithCustomTimeoutAndEndpoint()
    {
        var options = new LlmProviderOptions
        {
            Endpoint = "http://192.168.1.100:11434",
            ModelId = "deepseek-coder:33b",
            RequestTimeoutMinutes = 25
        };

        var client = OllamaChatClientFactory.CreateOllama(options);
        Assert.NotNull(client);
        Assert.IsAssignableFrom<IChatClient>(client);
    }
}
