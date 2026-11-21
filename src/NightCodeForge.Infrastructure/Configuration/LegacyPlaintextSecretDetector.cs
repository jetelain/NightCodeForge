using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Security;

namespace NightCodeForge.Infrastructure.Configuration;

/// <summary>
/// Detects deprecated plaintext API keys left in JSON configuration or SQLite payloads
/// and emits prominent redacted operator warnings without loading or echoing secret values.
/// </summary>
public static class LegacyPlaintextSecretDetector
{
    public static bool DetectAndWarnConfigurationSecrets(IConfiguration configuration, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);

        var detected = false;

        if (!string.IsNullOrWhiteSpace(configuration[ApiKeySecretConstants.LegacyOpenAiApiKeyConfigPath]))
        {
            detected = true;
            logger.LogWarning(
                "SECURITY WARNING: Legacy plaintext API key detected at configuration path '{ConfigPath}'. Plaintext API keys in configuration files are ignored and no longer loaded. Re-save the key in Settings (stored in the OS keyring via Pmad.KeySharp) or supply '{EnvVar}' and remove '{ConfigPath}' from disk.",
                ApiKeySecretConstants.LegacyOpenAiApiKeyConfigPath,
                ApiKeySecretConstants.OpenAiApiKeyEnvironmentVariable,
                ApiKeySecretConstants.LegacyOpenAiApiKeyConfigPath);
        }

        if (!string.IsNullOrWhiteSpace(configuration[ApiKeySecretConstants.LegacyWebSearchOllamaApiKeyConfigPath]))
        {
            detected = true;
            logger.LogWarning(
                "SECURITY WARNING: Legacy plaintext API key detected at configuration path '{ConfigPath}'. Plaintext API keys in configuration files are ignored and no longer loaded. Re-save the key in Settings (stored in the OS keyring via Pmad.KeySharp) or supply '{EnvVar}' and remove '{ConfigPath}' from disk.",
                ApiKeySecretConstants.LegacyWebSearchOllamaApiKeyConfigPath,
                ApiKeySecretConstants.OllamaApiKeyEnvironmentVariable,
                ApiKeySecretConstants.LegacyWebSearchOllamaApiKeyConfigPath);
        }

        if (configuration is IConfigurationRoot configRoot)
        {
            foreach (var jsonProvider in configRoot.Providers.OfType<JsonConfigurationProvider>())
            {
                WarnIfJsonKeyPresent(
                    jsonProvider,
                    ApiKeySecretConstants.OpenAiApiKeyEnvironmentVariable,
                    ApiKeySecretConstants.OpenAiApiKeyEnvironmentVariable,
                    logger,
                    ref detected);
                WarnIfJsonKeyPresent(
                    jsonProvider,
                    ApiKeySecretConstants.OllamaApiKeyEnvironmentVariable,
                    ApiKeySecretConstants.OllamaApiKeyEnvironmentVariable,
                    logger,
                    ref detected);
            }
        }

        return detected;
    }

    private static void WarnIfJsonKeyPresent(
        JsonConfigurationProvider provider,
        string jsonKey,
        string envVarName,
        ILogger logger,
        ref bool detected)
    {
        if (provider.TryGet(jsonKey, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            detected = true;
            var sourcePath = string.IsNullOrWhiteSpace(provider.Source.Path) ? "JSON configuration" : provider.Source.Path;
            logger.LogWarning(
                "SECURITY WARNING: Legacy plaintext API key detected at root key '{ConfigKey}' in '{SourcePath}'. Plaintext API keys in JSON configuration files are ignored and no longer loaded. Re-save the key in Settings (stored in the OS keyring via Pmad.KeySharp) or supply '{EnvVar}' as a process environment variable and remove '{ConfigKey}' from '{SourcePath}'.",
                jsonKey,
                sourcePath,
                envVarName,
                jsonKey,
                sourcePath);
        }
    }

    public static bool ContainsLegacyApiKeyProperty(string? jsonPayload)
    {
        if (string.IsNullOrWhiteSpace(jsonPayload))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(jsonPayload);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, ApiKeySecretConstants.LegacyApiKeyJsonPropertyName, StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(property.Value.GetString()))
                {
                    return true;
                }
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
