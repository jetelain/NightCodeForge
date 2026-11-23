namespace NightCodeForge.Installer.Core.Abstractions;

using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using KeySharp;
using NightCodeForge.Installer.Core.Constants;
using NightCodeForge.Installer.Core.Exceptions;

internal enum InstallerKeyringErrorType
{
    NoError,
    NotFound,
    Unavailable,
    OtherError
}

internal sealed record InstallerKeyringReadResult(
    InstallerKeyringErrorType ErrorType,
    string? Value,
    string? ErrorMessage,
    Exception? Exception);

internal sealed record InstallerKeyringMutationResult(
    InstallerKeyringErrorType ErrorType,
    string? ErrorMessage,
    Exception? Exception);

internal interface IInstallerKeyringBackend
{
    bool IsAvailable();

    InstallerKeyringReadResult GetPassword(string package, string service, string username);

    InstallerKeyringMutationResult SetPassword(string package, string service, string username, string password);

    InstallerKeyringMutationResult DeletePassword(string package, string service, string username);
}

internal sealed class KeySharpInstallerKeyringBackend : IInstallerKeyringBackend
{
    public static readonly KeySharpInstallerKeyringBackend Instance = new();

    private KeySharpInstallerKeyringBackend()
    {
    }

    public bool IsAvailable() => Keyring.IsAvailable();

    public InstallerKeyringReadResult GetPassword(string package, string service, string username)
    {
        try
        {
            var value = Keyring.GetPassword(package, service, username);
            return new InstallerKeyringReadResult(InstallerKeyringErrorType.NoError, value, null, null);
        }
        catch (KeyringException ex)
        {
            return new InstallerKeyringReadResult(MapErrorType(ex.Type), null, ex.BackendMessage ?? ex.Message, ex);
        }
        catch (DllNotFoundException ex)
        {
            return new InstallerKeyringReadResult(InstallerKeyringErrorType.Unavailable, null, ex.Message, ex);
        }
        catch (PlatformNotSupportedException ex)
        {
            return new InstallerKeyringReadResult(InstallerKeyringErrorType.Unavailable, null, ex.Message, ex);
        }
    }

    public InstallerKeyringMutationResult SetPassword(string package, string service, string username, string password)
    {
        try
        {
            Keyring.SetPassword(package, service, username, password);
            return new InstallerKeyringMutationResult(InstallerKeyringErrorType.NoError, null, null);
        }
        catch (KeyringException ex)
        {
            return new InstallerKeyringMutationResult(MapErrorType(ex.Type), ex.BackendMessage ?? ex.Message, ex);
        }
        catch (DllNotFoundException ex)
        {
            return new InstallerKeyringMutationResult(InstallerKeyringErrorType.Unavailable, ex.Message, ex);
        }
        catch (PlatformNotSupportedException ex)
        {
            return new InstallerKeyringMutationResult(InstallerKeyringErrorType.Unavailable, ex.Message, ex);
        }
    }

    public InstallerKeyringMutationResult DeletePassword(string package, string service, string username)
    {
        try
        {
            Keyring.DeletePassword(package, service, username);
            return new InstallerKeyringMutationResult(InstallerKeyringErrorType.NoError, null, null);
        }
        catch (KeyringException ex)
        {
            return new InstallerKeyringMutationResult(MapErrorType(ex.Type), ex.BackendMessage ?? ex.Message, ex);
        }
        catch (DllNotFoundException ex)
        {
            return new InstallerKeyringMutationResult(InstallerKeyringErrorType.Unavailable, ex.Message, ex);
        }
        catch (PlatformNotSupportedException ex)
        {
            return new InstallerKeyringMutationResult(InstallerKeyringErrorType.Unavailable, ex.Message, ex);
        }
    }

    private static InstallerKeyringErrorType MapErrorType(ErrorType errorType) =>
        errorType switch
        {
            ErrorType.NoError => InstallerKeyringErrorType.NoError,
            ErrorType.NotFound => InstallerKeyringErrorType.NotFound,
            ErrorType.Unavailable => InstallerKeyringErrorType.Unavailable,
            _ => InstallerKeyringErrorType.OtherError
        };
}

/// <summary>
/// Canonicalizes HTTP/HTTPS endpoint URLs identically to <c>ApiKeyEndpointNormalizer</c>
/// so keys stored by the installer are resolved seamlessly by <c>NightCodeForge.Web</c>.
/// </summary>
internal static class InstallerEndpointNormalizer
{
    public static bool TryNormalizeEndpoint(string? endpoint, [NotNullWhen(true)] out string? normalizedEndpoint)
    {
        normalizedEndpoint = null;
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return false;
        }

        var trimmed = endpoint.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(uri.Host))
        {
            return false;
        }

        var scheme = uri.Scheme.ToLowerInvariant();
        var host = uri.Host.ToLowerInvariant();
        var portPart = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
        var path = uri.AbsolutePath.TrimEnd('/');

        normalizedEndpoint = $"{scheme}://{host}{portPart}{path}";
        return true;
    }

    public static string NormalizeEndpoint(string? endpoint)
    {
        if (!TryNormalizeEndpoint(endpoint, out var normalized))
        {
            throw new ArgumentException(
                "A valid HTTP or HTTPS OpenAI-compatible endpoint URL is required to scope API key credentials.",
                nameof(endpoint));
        }

        return normalized;
    }
}

/// <summary>
/// OS keyring secret store backed by <see cref="KeySharp.Keyring"/> (<c>Pmad.KeySharp</c>).
/// Never embeds raw secret values in exception messages.
/// </summary>
public sealed class KeySharpInstallerSecretStore : IInstallerSecretStore
{
    private readonly IPlatformEnvironment _platform;
    private readonly IInstallerKeyringBackend _backend;
    private readonly object _lock = new();
    private readonly Dictionary<string, string?> _openAiCache = new(StringComparer.Ordinal);
    private bool? _isAvailable;

    public KeySharpInstallerSecretStore(IPlatformEnvironment platform)
        : this(platform, KeySharpInstallerKeyringBackend.Instance)
    {
    }

    internal KeySharpInstallerSecretStore(
        IPlatformEnvironment platform,
        IInstallerKeyringBackend backend)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
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
        if (!InstallerEndpointNormalizer.TryNormalizeEndpoint(endpoint, out var normalizedEndpoint))
        {
            return null;
        }

        lock (_lock)
        {
            if (_openAiCache.TryGetValue(normalizedEndpoint, out var cachedValue))
            {
                return cachedValue;
            }

            _isAvailable ??= _backend.IsAvailable();
            if (!_isAvailable.Value)
            {
                return null;
            }

            var result = _backend.GetPassword(
                InstallerConstants.KeyringPackageName,
                InstallerConstants.KeyringOpenAiCompatibleServiceName,
                normalizedEndpoint);

            switch (result.ErrorType)
            {
                case InstallerKeyringErrorType.NoError:
                    var normalizedValue = string.IsNullOrWhiteSpace(result.Value) ? null : result.Value.Trim();
                    _openAiCache[normalizedEndpoint] = normalizedValue;
                    return normalizedValue;
                case InstallerKeyringErrorType.NotFound:
                    _openAiCache[normalizedEndpoint] = null;
                    return null;
                case InstallerKeyringErrorType.Unavailable:
                    _isAvailable = false;
                    return null;
                default:
                    throw CreateKeyringException("read", "from", result.ErrorType, result.ErrorMessage, result.Exception);
            }
        }
    }

    public string? ResolveEffectiveOpenAiApiKey(string? endpoint)
    {
        var stored = GetOpenAiApiKey(endpoint);
        if (!string.IsNullOrWhiteSpace(stored))
        {
            return stored;
        }

        var env = _platform.GetEnvironmentVariable(InstallerConstants.EnvOpenAiApiKey);
        return string.IsNullOrWhiteSpace(env) ? null : env.Trim();
    }

    public bool HasOpenAiApiKey(string? endpoint) =>
        !string.IsNullOrWhiteSpace(GetOpenAiApiKey(endpoint));

    public bool HasEnvironmentOpenAiApiKey() =>
        !string.IsNullOrWhiteSpace(_platform.GetEnvironmentVariable(InstallerConstants.EnvOpenAiApiKey));

    public void SetOpenAiApiKey(string endpoint, string apiKey)
    {
        var normalizedEndpoint = InstallerEndpointNormalizer.NormalizeEndpoint(endpoint);
        var trimmedKey = ValidateAndNormalizeApiKey(apiKey, nameof(apiKey));

        lock (_lock)
        {
            EnsureAvailableForMutationLocked("save OpenAI-compatible API key");
            var result = _backend.SetPassword(
                InstallerConstants.KeyringPackageName,
                InstallerConstants.KeyringOpenAiCompatibleServiceName,
                normalizedEndpoint,
                trimmedKey);

            if (result.ErrorType != InstallerKeyringErrorType.NoError)
            {
                if (result.ErrorType == InstallerKeyringErrorType.Unavailable)
                {
                    _isAvailable = false;
                }

                throw CreateKeyringException("save", "in", result.ErrorType, result.ErrorMessage, result.Exception);
            }

            _isAvailable = true;
            _openAiCache[normalizedEndpoint] = trimmedKey;
        }
    }

    public void DeleteOpenAiApiKey(string endpoint)
    {
        var normalizedEndpoint = InstallerEndpointNormalizer.NormalizeEndpoint(endpoint);

        lock (_lock)
        {
            EnsureAvailableForMutationLocked("delete OpenAI-compatible API key");
            var result = _backend.DeletePassword(
                InstallerConstants.KeyringPackageName,
                InstallerConstants.KeyringOpenAiCompatibleServiceName,
                normalizedEndpoint);

            if (result.ErrorType is not (InstallerKeyringErrorType.NoError or InstallerKeyringErrorType.NotFound))
            {
                if (result.ErrorType == InstallerKeyringErrorType.Unavailable)
                {
                    _isAvailable = false;
                }

                throw CreateKeyringException("delete", "in", result.ErrorType, result.ErrorMessage, result.Exception);
            }

            _isAvailable = true;
            _openAiCache[normalizedEndpoint] = null;
        }
    }

    internal static bool TryValidateApiKeyHeaderValue(string? apiKey, out string trimmedKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            trimmedKey = string.Empty;
            return false;
        }

        var candidate = apiKey.Trim();
        if (candidate.Any(static c => char.IsControl(c) || char.IsWhiteSpace(c) || c > '\x7f'))
        {
            trimmedKey = string.Empty;
            return false;
        }

        if (!AuthenticationHeaderValue.TryParse($"Bearer {candidate}", out var parsed) ||
            !string.Equals(parsed.Parameter, candidate, StringComparison.Ordinal))
        {
            trimmedKey = string.Empty;
            return false;
        }

        trimmedKey = candidate;
        return true;
    }

    private void EnsureAvailableForMutationLocked(string operationDescription)
    {
        _isAvailable = _backend.IsAvailable();
        if (_isAvailable.Value)
        {
            return;
        }

        throw new InstallerSecretStoreException(
            $"Native OS keyring is unavailable on this system; cannot {operationDescription}. Configure credentials via {InstallerConstants.EnvOpenAiApiKey} instead.");
    }

    private static InstallerSecretStoreException CreateKeyringException(
        string operation,
        string preposition,
        InstallerKeyringErrorType errorType,
        string? errorMessage,
        Exception? exception)
    {
        var details = string.IsNullOrWhiteSpace(errorMessage) ? "Unknown keyring error." : errorMessage;
        var message = $"Failed to {operation} OpenAI-compatible API key {preposition} OS keyring ({errorType}): {details}";
        return exception is not null
            ? new InstallerSecretStoreException(message, exception)
            : new InstallerSecretStoreException(message);
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
}
