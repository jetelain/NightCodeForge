using KeySharp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Security;

namespace NightCodeForge.Infrastructure.Security;

/// <summary>
/// Thread-safe, cached OS keyring secret store backed by <see cref="Keyring"/> (<c>Pmad.KeySharp</c>).
/// Stores OpenAI-compatible API keys scoped by canonicalized endpoint URL and the Ollama Cloud Search API key
/// under dedicated keyring coordinates. Never logs or embeds raw secret values in exceptions.
/// </summary>
public sealed class KeySharpApiKeySecretStore : IApiKeySecretStore
{
    private readonly ILogger<KeySharpApiKeySecretStore> _logger;
    private readonly string _package;
    private readonly string _openAiService;
    private readonly string _ollamaWebSearchService;
    private readonly string _ollamaWebSearchUsername;
    private readonly IKeyringBackend _backend;
    private readonly Func<string, string?> _getEnvironmentVariable;

    private readonly object _lock = new();
    private readonly Dictionary<string, string?> _openAiCache = new(StringComparer.Ordinal);
    private bool _ollamaKeyLoaded;
    private string? _ollamaKeyCache;
    private bool? _isAvailable;
    private int _unavailableWarningLogged;

    public event EventHandler<ApiKeySecretChangedEventArgs>? SecretChanged;

    [ActivatorUtilitiesConstructor]
    public KeySharpApiKeySecretStore(ILogger<KeySharpApiKeySecretStore> logger)
        : this(
            logger,
            ApiKeySecretConstants.DefaultPackage,
            ApiKeySecretConstants.OpenAiCompatibleService,
            ApiKeySecretConstants.OllamaWebSearchService,
            ApiKeySecretConstants.OllamaWebSearchUsername,
            KeySharpKeyringBackend.Instance,
            Environment.GetEnvironmentVariable)
    {
    }

    public KeySharpApiKeySecretStore(
        ILogger<KeySharpApiKeySecretStore> logger,
        string package,
        string openAiService,
        string ollamaWebSearchService,
        string ollamaWebSearchUsername)
        : this(
            logger,
            package,
            openAiService,
            ollamaWebSearchService,
            ollamaWebSearchUsername,
            KeySharpKeyringBackend.Instance,
            Environment.GetEnvironmentVariable)
    {
    }

    internal KeySharpApiKeySecretStore(
        ILogger<KeySharpApiKeySecretStore> logger,
        string package,
        string openAiService,
        string ollamaWebSearchService,
        string ollamaWebSearchUsername,
        IKeyringBackend backend,
        Func<string, string?> getEnvironmentVariable)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ArgumentException.ThrowIfNullOrWhiteSpace(package);
        ArgumentException.ThrowIfNullOrWhiteSpace(openAiService);
        ArgumentException.ThrowIfNullOrWhiteSpace(ollamaWebSearchService);
        ArgumentException.ThrowIfNullOrWhiteSpace(ollamaWebSearchUsername);
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _getEnvironmentVariable = getEnvironmentVariable ?? throw new ArgumentNullException(nameof(getEnvironmentVariable));

        _package = package;
        _openAiService = openAiService;
        _ollamaWebSearchService = ollamaWebSearchService;
        _ollamaWebSearchUsername = ollamaWebSearchUsername;
    }

    public bool IsKeyringAvailable()
    {
        lock (_lock)
        {
            return _isAvailable ??= _backend.IsAvailable();
        }
    }

    public string? GetOpenAiApiKey(string? endpoint)
    {
        if (!ApiKeyEndpointNormalizer.TryNormalizeEndpoint(endpoint, out var normalizedEndpoint))
        {
            return null;
        }

        lock (_lock)
        {
            if (_openAiCache.TryGetValue(normalizedEndpoint, out var cachedValue))
            {
                return cachedValue;
            }

            if (TryReadFromBackendLocked(_openAiService, normalizedEndpoint, "OpenAI-compatible endpoint", out var value))
            {
                _openAiCache[normalizedEndpoint] = value;
            }

            return value;
        }
    }

    public string? ResolveEffectiveOpenAiApiKey(string? endpoint)
    {
        var keyringKey = GetOpenAiApiKey(endpoint);
        if (!string.IsNullOrWhiteSpace(keyringKey))
        {
            return keyringKey;
        }

        return NormalizeOptionalSecret(_getEnvironmentVariable(ApiKeySecretConstants.OpenAiApiKeyEnvironmentVariable));
    }

    public bool HasOpenAiApiKey(string? endpoint) =>
        !string.IsNullOrWhiteSpace(GetOpenAiApiKey(endpoint));

    public void SetOpenAiApiKey(string endpoint, string apiKey)
    {
        var normalizedEndpoint = ApiKeyEndpointNormalizer.NormalizeEndpoint(endpoint);
        var trimmedKey = ValidateAndNormalizeApiKey(apiKey, nameof(apiKey));

        lock (_lock)
        {
            EnsureAvailableForMutationLocked("save OpenAI-compatible API key");
            var result = _backend.SetPassword(_package, _openAiService, normalizedEndpoint, trimmedKey);
            if (result.ErrorType != ErrorType.NoError)
            {
                if (result.ErrorType == ErrorType.Unavailable)
                {
                    _isAvailable = false;
                }

                throw CreateAndLogKeyringException("save", "in", "OpenAI-compatible API key", result.ErrorType, result.ErrorMessage, result.Exception);
            }

            _isAvailable = true;
            _openAiCache[normalizedEndpoint] = trimmedKey;
        }

        RaiseSecretChanged(ApiKeySecretKind.OpenAiCompatible, normalizedEndpoint);
    }

    public void DeleteOpenAiApiKey(string endpoint)
    {
        var normalizedEndpoint = ApiKeyEndpointNormalizer.NormalizeEndpoint(endpoint);

        lock (_lock)
        {
            EnsureAvailableForMutationLocked("delete OpenAI-compatible API key");
            var result = _backend.DeletePassword(_package, _openAiService, normalizedEndpoint);
            if (result.ErrorType != ErrorType.NoError && result.ErrorType != ErrorType.NotFound)
            {
                if (result.ErrorType == ErrorType.Unavailable)
                {
                    _isAvailable = false;
                }

                throw CreateAndLogKeyringException("delete", "in", "OpenAI-compatible API key", result.ErrorType, result.ErrorMessage, result.Exception);
            }

            _isAvailable = true;
            _openAiCache[normalizedEndpoint] = null;
        }

        RaiseSecretChanged(ApiKeySecretKind.OpenAiCompatible, normalizedEndpoint);
    }

    public string? GetOllamaWebSearchApiKey()
    {
        lock (_lock)
        {
            if (_ollamaKeyLoaded)
            {
                return _ollamaKeyCache;
            }

            if (TryReadFromBackendLocked(_ollamaWebSearchService, _ollamaWebSearchUsername, "Ollama Cloud Search", out var value))
            {
                _ollamaKeyCache = value;
                _ollamaKeyLoaded = true;
            }

            return value;
        }
    }

    public string? ResolveEffectiveOllamaWebSearchApiKey()
    {
        var keyringKey = GetOllamaWebSearchApiKey();
        if (!string.IsNullOrWhiteSpace(keyringKey))
        {
            return keyringKey;
        }

        return NormalizeOptionalSecret(_getEnvironmentVariable(ApiKeySecretConstants.OllamaApiKeyEnvironmentVariable));
    }

    public bool HasOllamaWebSearchApiKey() =>
        !string.IsNullOrWhiteSpace(GetOllamaWebSearchApiKey());

    public void SetOllamaWebSearchApiKey(string apiKey)
    {
        var trimmedKey = ValidateAndNormalizeApiKey(apiKey, nameof(apiKey));

        lock (_lock)
        {
            EnsureAvailableForMutationLocked("save Ollama Cloud Search API key");
            var result = _backend.SetPassword(_package, _ollamaWebSearchService, _ollamaWebSearchUsername, trimmedKey);
            if (result.ErrorType != ErrorType.NoError)
            {
                if (result.ErrorType == ErrorType.Unavailable)
                {
                    _isAvailable = false;
                }

                throw CreateAndLogKeyringException("save", "in", "Ollama Cloud Search API key", result.ErrorType, result.ErrorMessage, result.Exception);
            }

            _isAvailable = true;
            _ollamaKeyCache = trimmedKey;
            _ollamaKeyLoaded = true;
        }

        RaiseSecretChanged(ApiKeySecretKind.OllamaWebSearch, normalizedEndpoint: null);
    }

    public void DeleteOllamaWebSearchApiKey()
    {
        lock (_lock)
        {
            EnsureAvailableForMutationLocked("delete Ollama Cloud Search API key");
            var result = _backend.DeletePassword(_package, _ollamaWebSearchService, _ollamaWebSearchUsername);
            if (result.ErrorType != ErrorType.NoError && result.ErrorType != ErrorType.NotFound)
            {
                if (result.ErrorType == ErrorType.Unavailable)
                {
                    _isAvailable = false;
                }

                throw CreateAndLogKeyringException("delete", "in", "Ollama Cloud Search API key", result.ErrorType, result.ErrorMessage, result.Exception);
            }

            _isAvailable = true;
            _ollamaKeyCache = null;
            _ollamaKeyLoaded = true;
        }

        RaiseSecretChanged(ApiKeySecretKind.OllamaWebSearch, normalizedEndpoint: null);
    }

    private bool TryReadFromBackendLocked(string service, string username, string credentialDescription, out string? value)
    {
        _isAvailable ??= _backend.IsAvailable();
        if (!_isAvailable.Value)
        {
            LogUnavailableWarningOnce();
            value = null;
            return false;
        }

        var result = _backend.GetPassword(_package, service, username);
        switch (result.ErrorType)
        {
            case ErrorType.NoError:
                value = NormalizeOptionalSecret(result.Value);
                return true;
            case ErrorType.NotFound:
                value = null;
                return true;
            case ErrorType.Unavailable:
                _isAvailable = false;
                LogUnavailableWarningOnce();
                value = null;
                return false;
            default:
                throw CreateAndLogKeyringException("read", "from", credentialDescription, result.ErrorType, result.ErrorMessage, result.Exception);
        }
    }

    private void EnsureAvailableForMutationLocked(string operationDescription)
    {
        _isAvailable = _backend.IsAvailable();
        if (_isAvailable.Value)
        {
            return;
        }

        _logger.LogError(
            "OS keyring is unavailable; cannot {Operation}. Plaintext fallback is prohibited.",
            operationDescription);
        throw new SecretStoreException(
            $"Native OS keyring is unavailable on this system; cannot {operationDescription}. Configure credentials via environment variables ({ApiKeySecretConstants.OpenAiApiKeyEnvironmentVariable} / {ApiKeySecretConstants.OllamaApiKeyEnvironmentVariable}) on headless hosts.");
    }

    private SecretStoreException CreateAndLogKeyringException(
        string operation,
        string preposition,
        string credentialDescription,
        ErrorType errorType,
        string? errorMessage,
        Exception? exception)
    {
        var details = string.IsNullOrWhiteSpace(errorMessage) ? "Unknown keyring error." : errorMessage;
        _logger.LogError(
            exception,
            "Failed to {Operation} {CredentialDescription} {Preposition} OS keyring ({ErrorType}): {BackendMessage}",
            operation,
            credentialDescription,
            preposition,
            errorType,
            details);
        var message = $"Failed to {operation} {credentialDescription} {preposition} OS keyring ({errorType}): {details}";
        return exception != null
            ? new SecretStoreException(message, exception)
            : new SecretStoreException(message);
    }

    private static string ValidateAndNormalizeApiKey(string apiKey, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey, paramName);
        var trimmed = apiKey.Trim();
        if (trimmed.Any(char.IsControl))
        {
            throw new ArgumentException("API key must not contain control characters or line breaks.", paramName);
        }

        return trimmed;
    }

    private void LogUnavailableWarningOnce()
    {
        if (Interlocked.Exchange(ref _unavailableWarningLogged, 1) == 0)
        {
            _logger.LogWarning(
                "Native OS keyring is unavailable on this host. Stored keyring API keys cannot be read; falling back to process environment variables ({OpenAiEnv} / {OllamaEnv}) for runtime authentication.",
                ApiKeySecretConstants.OpenAiApiKeyEnvironmentVariable,
                ApiKeySecretConstants.OllamaApiKeyEnvironmentVariable);
        }
    }

    private void RaiseSecretChanged(ApiKeySecretKind kind, string? normalizedEndpoint)
    {
        var handler = SecretChanged;
        if (handler == null)
        {
            return;
        }

        var args = new ApiKeySecretChangedEventArgs(kind, normalizedEndpoint);
        foreach (var listener in handler.GetInvocationList().Cast<EventHandler<ApiKeySecretChangedEventArgs>>())
        {
            try
            {
                listener(this, args);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception in IApiKeySecretStore.SecretChanged subscriber for {Kind}.", kind);
            }
        }
    }

    private static string? NormalizeOptionalSecret(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
