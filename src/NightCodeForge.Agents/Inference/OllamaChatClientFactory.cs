using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OllamaSharp;

namespace NightCodeForge.Agents.Inference;

/// <summary>
/// Factory for instantiating IChatClient via OllamaSharp's OllamaApiClient with tuned request timeouts.
/// </summary>
public static class OllamaChatClientFactory
{
    public static IChatClient CreateOllama(LlmProviderOptions options, ILogger? logger = null)
    {
        var endpoint = string.IsNullOrWhiteSpace(options.Endpoint) ? "http://localhost:11434" : options.Endpoint;
        var timeoutMinutes = Math.Max(1, options.RequestTimeoutMinutes);

        var httpClient = new HttpClient
        {
            BaseAddress = new Uri(endpoint),
            Timeout = TimeSpan.FromMinutes(timeoutMinutes)
        };

        return new OllamaApiClient(httpClient, options.ModelId, null);
    }
}
