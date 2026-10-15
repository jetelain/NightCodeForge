using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Domain;

namespace NightCodeForge.Agents.Inference;

public class OllamaChatClientOptions
{
    public string BaseUrl { get; set; } = "http://localhost:11434";
    public string DefaultModel { get; set; } = "qwen2.5-coder:7b";
}

/// <summary>
/// IChatClient implementation for local Ollama and OllamaRouter endpoints supporting streaming and token usage metrics.
/// </summary>
public class OllamaChatClient : IChatClient
{
    private readonly HttpClient _httpClient;
    private readonly string _defaultModel;
    private readonly ILogger<OllamaChatClient> _logger;

    public ChatClientMetadata Metadata { get; }
    public OllamaRoutingTarget RoutingTarget { get; set; } = OllamaRoutingTarget.Auto;

    public OllamaChatClient(
        HttpClient httpClient,
        string defaultModel = "qwen2.5-coder:7b",
        string? baseUrl = null,
        ILogger<OllamaChatClient>? logger = null,
        OllamaRoutingTarget routingTarget = OllamaRoutingTarget.Auto)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _defaultModel = defaultModel;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<OllamaChatClient>.Instance;
        RoutingTarget = routingTarget;

        var host = baseUrl ?? "http://localhost:11434";
        if (_httpClient.BaseAddress == null)
        {
            _httpClient.BaseAddress = new Uri(host.TrimEnd('/') + "/");
        }

        Metadata = new ChatClientMetadata("Ollama", new Uri(host), defaultModel);
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var model = options?.ModelId ?? _defaultModel;
        var requestMessages = chatMessages.Select(m => new OllamaMessage
        {
            Role = m.Role.Value.ToLowerInvariant(),
            Content = m.Text ?? string.Empty
        }).ToList();

        var payload = new OllamaChatRequest
        {
            Model = model,
            Messages = requestMessages,
            Stream = false,
            Options = options?.Temperature.HasValue == true ? new Dictionary<string, object> { ["temperature"] = options.Temperature.Value } : null
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "api/chat")
        {
            Content = JsonContent.Create(payload)
        };

        var effectiveTarget = ResolveRoutingTarget(options);
        if (effectiveTarget != OllamaRoutingTarget.Auto)
        {
            var targetStr = effectiveTarget switch
            {
                OllamaRoutingTarget.Local => "Local",
                OllamaRoutingTarget.Remote => "Remote",
                OllamaRoutingTarget.Cloud => "Cloud",
                _ => null
            };
            if (targetStr != null)
            {
                request.Headers.Add("X-OllamaRouter-Target", targetStr);
            }
        }

        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        string? targetRouting = null;
        if (response.Headers.TryGetValues("x-ollama-router-target", out var targetVals))
        {
            targetRouting = targetVals.FirstOrDefault();
        }
        else if (response.Headers.TryGetValues("x-target-backend", out var backendVals))
        {
            targetRouting = backendVals.FirstOrDefault();
        }

        var result = await response.Content.ReadFromJsonAsync<OllamaChatResponse>(cancellationToken: cancellationToken);
        var replyText = result?.Message?.Content ?? string.Empty;

        var chatResponse = new ChatResponse(new ChatMessage(ChatRole.Assistant, replyText))
        {
            ModelId = model,
            Usage = result != null ? new UsageDetails
            {
                InputTokenCount = result.PromptEvalCount,
                OutputTokenCount = result.EvalCount,
                TotalTokenCount = result.PromptEvalCount + result.EvalCount
            } : null
        };

        if (!string.IsNullOrEmpty(targetRouting))
        {
            chatResponse.AdditionalProperties ??= new AdditionalPropertiesDictionary();
            chatResponse.AdditionalProperties["target_routing"] = targetRouting;
        }

        return chatResponse;
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var model = options?.ModelId ?? _defaultModel;
        var requestMessages = chatMessages.Select(m => new OllamaMessage
        {
            Role = m.Role.Value.ToLowerInvariant(),
            Content = m.Text ?? string.Empty
        }).ToList();

        var payload = new OllamaChatRequest
        {
            Model = model,
            Messages = requestMessages,
            Stream = true,
            Options = options?.Temperature.HasValue == true ? new Dictionary<string, object> { ["temperature"] = options.Temperature.Value } : null
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "api/chat")
        {
            Content = JsonContent.Create(payload)
        };

        var effectiveTarget = ResolveRoutingTarget(options);
        if (effectiveTarget != OllamaRoutingTarget.Auto)
        {
            var targetStr = effectiveTarget switch
            {
                OllamaRoutingTarget.Local => "Local",
                OllamaRoutingTarget.Remote => "Remote",
                OllamaRoutingTarget.Cloud => "Cloud",
                _ => null
            };
            if (targetStr != null)
            {
                request.Headers.Add("X-OllamaRouter-Target", targetStr);
            }
        }

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        string? targetRouting = null;
        if (response.Headers.TryGetValues("x-ollama-router-target", out var targetVals))
        {
            targetRouting = targetVals.FirstOrDefault();
        }
        else if (response.Headers.TryGetValues("x-target-backend", out var backendVals))
        {
            targetRouting = backendVals.FirstOrDefault();
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            OllamaStreamChunk? chunk = null;
            try
            {
                chunk = JsonSerializer.Deserialize<OllamaStreamChunk>(line);
            }
            catch
            {
                // ignore malformed chunk
            }

            if (chunk == null) continue;

            var update = new ChatResponseUpdate(ChatRole.Assistant, chunk.Message?.Content);

            if (!string.IsNullOrEmpty(targetRouting))
            {
                update.AdditionalProperties ??= new AdditionalPropertiesDictionary();
                update.AdditionalProperties["target_routing"] = targetRouting;
            }

            if (chunk.Done && (chunk.PromptEvalCount > 0 || chunk.EvalCount > 0))
            {
                update.Contents.Add(new UsageContent(new UsageDetails
                {
                    InputTokenCount = chunk.PromptEvalCount,
                    OutputTokenCount = chunk.EvalCount,
                    TotalTokenCount = chunk.PromptEvalCount + chunk.EvalCount
                }));
            }

            yield return update;

            if (chunk.Done)
            {
                break;
            }
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        return serviceType == typeof(HttpClient) ? _httpClient : null;
    }

    private OllamaRoutingTarget ResolveRoutingTarget(ChatOptions? options)
    {
        if (options?.AdditionalProperties != null)
        {
            if (options.AdditionalProperties.TryGetValue("X-OllamaRouter-Target", out var headerVal) && headerVal is string hStr && Enum.TryParse<OllamaRoutingTarget>(hStr, true, out var parsedH))
            {
                return parsedH;
            }
            if (options.AdditionalProperties.TryGetValue("routing_target", out var targetVal))
            {
                if (targetVal is OllamaRoutingTarget ort) return ort;
                if (targetVal is string tStr && Enum.TryParse<OllamaRoutingTarget>(tStr, true, out var parsedT)) return parsedT;
            }
        }
        return RoutingTarget;
    }

    public void Dispose()
    {
    }

    private class OllamaChatRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("messages")]
        public List<OllamaMessage> Messages { get; set; } = new();

        [JsonPropertyName("stream")]
        public bool Stream { get; set; }

        [JsonPropertyName("options")]
        public Dictionary<string, object>? Options { get; set; }
    }

    private class OllamaMessage
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = string.Empty;

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;
    }

    private class OllamaChatResponse
    {
        [JsonPropertyName("message")]
        public OllamaMessage? Message { get; set; }

        [JsonPropertyName("prompt_eval_count")]
        public long PromptEvalCount { get; set; }

        [JsonPropertyName("eval_count")]
        public long EvalCount { get; set; }
    }

    private class OllamaStreamChunk
    {
        [JsonPropertyName("message")]
        public OllamaMessage? Message { get; set; }

        [JsonPropertyName("done")]
        public bool Done { get; set; }

        [JsonPropertyName("prompt_eval_count")]
        public long PromptEvalCount { get; set; }

        [JsonPropertyName("eval_count")]
        public long EvalCount { get; set; }
    }
}
