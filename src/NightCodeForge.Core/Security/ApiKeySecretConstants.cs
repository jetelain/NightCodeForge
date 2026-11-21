using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Security;

/// <summary>
/// Central constants for OS keyring coordinates, environment variable fallbacks,
/// and legacy plaintext configuration detection/scrubbing.
/// </summary>
public static class ApiKeySecretConstants
{
    public const string DefaultPackage = "NightCodeForge";
    public const string OpenAiCompatibleService = "OpenAiCompatibleApiKey";
    public const string OllamaWebSearchService = "OllamaWebSearchApiKey";
    public const string OllamaWebSearchUsername = "ollama-web-search-api-key";

    public const string OpenAiApiKeyEnvironmentVariable = "OPENAI_API_KEY";
    public const string OllamaApiKeyEnvironmentVariable = WebSearchSettings.OllamaApiKeyEnvironmentVariable;

    public const string LegacyApiKeyJsonPropertyName = "ApiKey";
    public const string LegacyOpenAiApiKeyConfigPath = "LlmProvider:OpenAiCompatible:ApiKey";
    public const string LegacyWebSearchOllamaApiKeyConfigPath = "WebSearch:Ollama:ApiKey";
    public const string WebSearchOllamaEndpointConfigPath = "WebSearch:Ollama:Endpoint";
}
