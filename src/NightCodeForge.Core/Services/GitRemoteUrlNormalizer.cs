using System.Text.RegularExpressions;

namespace NightCodeForge.Core.Services;

/// <summary>
/// Normalizes Git remote URLs across HTTPS, HTTP, SSH URL, and SCP-style SSH syntaxes into a canonical
/// lowercase <c>host/owner/repo</c> key for local-vs-remote repository matching.
/// </summary>
public static partial class GitRemoteUrlNormalizer
{
    private const string GitSuffix = ".git";

    // Matches SCP-style SSH remotes such as git@github.com:Owner/Repo.git or github.com:Owner/Repo
    [GeneratedRegex(@"^(?:([^@\s/\\]+)@)?([^:/\\\s]+):(.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex ScpStyleSshRegex();

    /// <summary>
    /// Normalizes a Git remote URL to lowercase <c>host/path</c> with trailing slashes and <c>.git</c> stripped.
    /// Returns null if the input is blank or cannot be parsed as a repository location.
    /// </summary>
    public static string? Normalize(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var trimmed = url.Trim();
        if (trimmed.StartsWith('/') || trimmed.StartsWith('\\'))
        {
            return null;
        }

        if (trimmed.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
            {
                return null;
            }

            var path = TrimRepositoryPath(uri.AbsolutePath);
            return string.IsNullOrEmpty(path)
                ? null
                : $"{uri.Host.ToLowerInvariant()}/{path.ToLowerInvariant()}";
        }

        var scpMatch = ScpStyleSshRegex().Match(trimmed);
        if (scpMatch.Success)
        {
            var hasUserPrefix = scpMatch.Groups[1].Success;
            var host = scpMatch.Groups[2].Value.Trim();
            var rawPath = scpMatch.Groups[3].Value;

            if (!hasUserPrefix &&
                host.Length == 1 &&
                char.IsAsciiLetter(host[0]) &&
                (rawPath.StartsWith('/') || rawPath.StartsWith('\\')))
            {
                return null;
            }

            var path = TrimRepositoryPath(rawPath);
            return string.IsNullOrEmpty(host) || string.IsNullOrEmpty(path)
                ? null
                : $"{host.ToLowerInvariant()}/{path.ToLowerInvariant()}";
        }

        return null;
    }

    /// <summary>
    /// Returns true if two Git remote URLs refer to the same canonical repository host and path.
    /// </summary>
    public static bool AreSameRepository(string? leftUrl, string? rightUrl)
    {
        var left = Normalize(leftUrl);
        var right = Normalize(rightUrl);

        return left is not null &&
               right is not null &&
               string.Equals(left, right, StringComparison.Ordinal);
    }

    private static string TrimRepositoryPath(string rawPath)
    {
        var normalized = rawPath.Replace('\\', '/').Trim('/');
        if (normalized.EndsWith(GitSuffix, StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^GitSuffix.Length];
        }

        return normalized.Trim('/');
    }
}
