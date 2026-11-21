using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Security;

namespace NightCodeForge.Web.Tests.Fixtures;

/// <summary>
/// Isolated in-memory fake of <see cref="IApiKeySecretStore"/> so unit and bUnit component tests
/// never touch the host operating system keyring.
/// </summary>
public sealed class FakeApiKeySecretStore : IApiKeySecretStore
{
    private readonly Dictionary<string, string> _openAiKeys = new(StringComparer.Ordinal);
    private string? _ollamaKey;

    public bool KeyringAvailable { get; set; } = true;
    public bool BecomeUnavailableOnRead { get; set; }
    public Exception? ThrowOnRead { get; set; }
    public Exception? ThrowOnWrite { get; set; }
    public Exception? ThrowOnDelete { get; set; }
    public string? OpenAiEnvFallback { get; set; }
    public string? OllamaEnvFallback { get; set; }

    public int OpenAiHasCalls { get; private set; }
    public int OpenAiResolveCalls { get; private set; }
    public int OllamaHasCalls { get; private set; }
    public int OllamaResolveCalls { get; private set; }
    public int SetOpenAiCalls { get; private set; }
    public int DeleteOpenAiCalls { get; private set; }
    public int SetOllamaCalls { get; private set; }
    public int DeleteOllamaCalls { get; private set; }

    public event EventHandler<ApiKeySecretChangedEventArgs>? SecretChanged;

    public bool IsKeyringAvailable() => KeyringAvailable;

    public string? GetOpenAiApiKey(string? endpoint)
    {
        if (!KeyringAvailable)
        {
            return null;
        }

        if (BecomeUnavailableOnRead)
        {
            KeyringAvailable = false;
            return null;
        }

        if (ThrowOnRead != null)
        {
            throw ThrowOnRead;
        }

        if (!ApiKeyEndpointNormalizer.TryNormalizeEndpoint(endpoint, out var normalized))
        {
            return null;
        }

        return _openAiKeys.GetValueOrDefault(normalized);
    }

    public string? ResolveEffectiveOpenAiApiKey(string? endpoint)
    {
        OpenAiResolveCalls++;
        var keyringKey = GetOpenAiApiKey(endpoint);
        if (!string.IsNullOrWhiteSpace(keyringKey))
        {
            return keyringKey;
        }

        return string.IsNullOrWhiteSpace(OpenAiEnvFallback) ? null : OpenAiEnvFallback.Trim();
    }

    public bool HasOpenAiApiKey(string? endpoint)
    {
        OpenAiHasCalls++;
        return !string.IsNullOrWhiteSpace(GetOpenAiApiKey(endpoint));
    }

    public void SetOpenAiApiKey(string endpoint, string apiKey)
    {
        SetOpenAiCalls++;
        if (ThrowOnWrite != null)
        {
            throw ThrowOnWrite;
        }
        var normalized = ApiKeyEndpointNormalizer.NormalizeEndpoint(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        _openAiKeys[normalized] = apiKey.Trim();
        SecretChanged?.Invoke(this, new ApiKeySecretChangedEventArgs(ApiKeySecretKind.OpenAiCompatible, normalized));
    }

    public void DeleteOpenAiApiKey(string endpoint)
    {
        DeleteOpenAiCalls++;
        if (ThrowOnDelete != null)
        {
            throw ThrowOnDelete;
        }
        var normalized = ApiKeyEndpointNormalizer.NormalizeEndpoint(endpoint);
        _openAiKeys.Remove(normalized);
        SecretChanged?.Invoke(this, new ApiKeySecretChangedEventArgs(ApiKeySecretKind.OpenAiCompatible, normalized));
    }

    public string? GetOllamaWebSearchApiKey()
    {
        if (!KeyringAvailable)
        {
            return null;
        }

        if (BecomeUnavailableOnRead)
        {
            KeyringAvailable = false;
            return null;
        }

        if (ThrowOnRead != null)
        {
            throw ThrowOnRead;
        }

        return string.IsNullOrWhiteSpace(_ollamaKey) ? null : _ollamaKey;
    }

    public string? ResolveEffectiveOllamaWebSearchApiKey()
    {
        OllamaResolveCalls++;
        var keyringKey = GetOllamaWebSearchApiKey();
        if (!string.IsNullOrWhiteSpace(keyringKey))
        {
            return keyringKey;
        }

        return string.IsNullOrWhiteSpace(OllamaEnvFallback) ? null : OllamaEnvFallback.Trim();
    }

    public bool HasOllamaWebSearchApiKey()
    {
        OllamaHasCalls++;
        return !string.IsNullOrWhiteSpace(GetOllamaWebSearchApiKey());
    }

    public void SetOllamaWebSearchApiKey(string apiKey)
    {
        SetOllamaCalls++;
        if (ThrowOnWrite != null)
        {
            throw ThrowOnWrite;
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        _ollamaKey = apiKey.Trim();
        SecretChanged?.Invoke(this, new ApiKeySecretChangedEventArgs(ApiKeySecretKind.OllamaWebSearch, null));
    }

    public void DeleteOllamaWebSearchApiKey()
    {
        DeleteOllamaCalls++;
        if (ThrowOnDelete != null)
        {
            throw ThrowOnDelete;
        }
        _ollamaKey = null;
        SecretChanged?.Invoke(this, new ApiKeySecretChangedEventArgs(ApiKeySecretKind.OllamaWebSearch, null));
    }

    public void SeedOpenAiKey(string endpoint, string apiKey)
    {
        var normalized = ApiKeyEndpointNormalizer.NormalizeEndpoint(endpoint);
        _openAiKeys[normalized] = apiKey.Trim();
    }

    public void SeedOllamaKey(string? apiKey)
    {
        _ollamaKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
    }

    public string? PeekRawOpenAiKey(string endpoint)
    {
        var normalized = ApiKeyEndpointNormalizer.NormalizeEndpoint(endpoint);
        return _openAiKeys.GetValueOrDefault(normalized);
    }

    public string? PeekRawOllamaKey() => _ollamaKey;
}
