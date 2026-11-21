using NightCodeForge.Core.Security;

namespace NightCodeForge.Core.Interfaces;

/// <summary>
/// Manages external service API keys (OpenAI-compatible inference and Ollama Cloud Search)
/// in the native OS keyring via Pmad.KeySharp with thread-safe in-memory caching, live change
/// notifications, and headless environment variable fallback on reads.
/// </summary>
public interface IApiKeySecretStore
{
    /// <summary>
    /// Checks whether the platform OS credential store is available and functional.
    /// </summary>
    bool IsKeyringAvailable();

    /// <summary>
    /// Retrieves the stored OS keyring API key scoped to <paramref name="endpoint"/> without environment variable fallback.
    /// Returns <see langword="null"/> if absent, invalid endpoint, or keyring unavailable on read.
    /// </summary>
    string? GetOpenAiApiKey(string? endpoint);

    /// <summary>
    /// Resolves the effective OpenAI-compatible API key using precedence:
    /// 1. OS keyring entry matching canonicalized <paramref name="endpoint"/>,
    /// 2. <c>OPENAI_API_KEY</c> environment variable,
    /// 3. <see langword="null"/>.
    /// </summary>
    string? ResolveEffectiveOpenAiApiKey(string? endpoint);

    /// <summary>
    /// Returns <see langword="true"/> when an API key is stored in the OS keyring for <paramref name="endpoint"/>.
    /// </summary>
    bool HasOpenAiApiKey(string? endpoint);

    /// <summary>
    /// Stores or rotates the OpenAI-compatible API key in the OS keyring scoped to canonicalized <paramref name="endpoint"/>.
    /// Fails fast with <see cref="ArgumentException"/> on invalid endpoint/key or <see cref="Exceptions.SecretStoreException"/> on keyring failure.
    /// </summary>
    void SetOpenAiApiKey(string endpoint, string apiKey);

    /// <summary>
    /// Deletes the OpenAI-compatible API key for canonicalized <paramref name="endpoint"/> from the OS keyring (idempotent if absent).
    /// </summary>
    void DeleteOpenAiApiKey(string endpoint);

    /// <summary>
    /// Retrieves the stored Ollama Cloud Search API key from the OS keyring without environment variable fallback.
    /// </summary>
    string? GetOllamaWebSearchApiKey();

    /// <summary>
    /// Resolves the effective Ollama Cloud Search API key using precedence:
    /// 1. OS keyring entry,
    /// 2. <c>OLLAMA_API_KEY</c> environment variable,
    /// 3. <see langword="null"/>.
    /// </summary>
    string? ResolveEffectiveOllamaWebSearchApiKey();

    /// <summary>
    /// Returns <see langword="true"/> when an Ollama Cloud Search API key is stored in the OS keyring.
    /// </summary>
    bool HasOllamaWebSearchApiKey();

    /// <summary>
    /// Stores or rotates the Ollama Cloud Search API key in the OS keyring.
    /// </summary>
    void SetOllamaWebSearchApiKey(string apiKey);

    /// <summary>
    /// Deletes the Ollama Cloud Search API key from the OS keyring (idempotent if absent).
    /// </summary>
    void DeleteOllamaWebSearchApiKey();

    /// <summary>
    /// Raised whenever a credential is stored, rotated, or deleted in the OS keyring.
    /// </summary>
    event EventHandler<ApiKeySecretChangedEventArgs>? SecretChanged;
}
