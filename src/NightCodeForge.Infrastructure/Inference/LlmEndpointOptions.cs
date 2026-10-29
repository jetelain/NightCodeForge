using Microsoft.Extensions.Options;

namespace NightCodeForge.Infrastructure.Inference;

/// <summary>
/// Connection settings of the configured LLM inference endpoint (standalone Ollama or OllamaRouter).
/// Bound by key name from the <c>LlmProvider</c> configuration section, the single source of truth
/// shared with the chat client factory.
/// </summary>
public class LlmEndpointOptions
{
    public const string DefaultEndpoint = "http://localhost:11434";
    public const int DefaultContextWindowTokens = 131072;

    /// <summary>Base HTTP/HTTPS endpoint of the Ollama or OllamaRouter server.</summary>
    public string Endpoint { get; set; } = DefaultEndpoint;

    /// <summary>Baseline context window (tokens) used when no model name is available to probe.</summary>
    public int ContextWindowTokens { get; set; } = DefaultContextWindowTokens;

    /// <summary>Default model identifier configured in LlmProvider section.</summary>
    public string? DefaultModelId { get; set; }

    /// <summary>Legacy ModelId property forwarding to DefaultModelId for backwards compatibility.</summary>
    public string? ModelId
    {
        get => DefaultModelId;
        set => DefaultModelId = value;
    }
}

/// <summary>Shared HTTP plumbing for services talking to the configured LLM endpoint.</summary>
internal static class LlmEndpointHttpClient
{
    /// <summary>
    /// Validates the configured endpoint and points the typed <see cref="HttpClient"/> at it
    /// when no base address was supplied by the host. Returns the endpoint URL.
    /// </summary>
    public static string Configure(HttpClient httpClient, IOptions<LlmEndpointOptions> options)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);

        var endpoint = options.Value.Endpoint;
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            throw new ArgumentException("The LlmProvider endpoint must be configured.", nameof(options));
        }

        if (httpClient.BaseAddress == null)
        {
            httpClient.BaseAddress = new Uri(endpoint.TrimEnd('/') + "/");
        }

        return endpoint;
    }
}

/// <summary>Relative paths of the Ollama and OllamaRouter HTTP APIs.</summary>
internal static class OllamaApiPaths
{
    public const string Version = "api/version";
    public const string Tags = "api/tags";
    public const string Show = "api/show";
    public const string Ps = "api/ps";
    public const string Generate = "api/generate";
    public const string RouterInfo = "api/router/info";
    public const string RouterFleet = "api/router/fleet";
    public const string RouterModels = "api/router/models";
    public const string RouterVramReclaim = "api/router/vram/reclaim";
}
