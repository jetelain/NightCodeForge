using System.Diagnostics.CodeAnalysis;

namespace NightCodeForge.Core.Security;

/// <summary>
/// Canonicalizes HTTP/HTTPS endpoint URLs used as scope keys for OpenAI-compatible API keys.
/// </summary>
public static class ApiKeyEndpointNormalizer
{
    /// <summary>
    /// Attempts to canonicalize an OpenAI-compatible endpoint URL.
    /// Trims whitespace, lowercases scheme and host, omits default ports (80/443),
    /// strips trailing path slashes, and removes userinfo, query strings, and fragments.
    /// </summary>
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

    /// <summary>
    /// Canonicalizes an OpenAI-compatible endpoint URL, failing fast with <see cref="ArgumentException"/>
    /// if the value is empty, whitespace, or not a valid HTTP/HTTPS absolute URI.
    /// </summary>
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
