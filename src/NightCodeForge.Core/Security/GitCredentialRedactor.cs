using System.Text.RegularExpressions;

namespace NightCodeForge.Core.Security;

/// <summary>
/// Redacts Git HTTP authentication headers, OAuth tokens, and embedded URL credentials
/// from CLI arguments, standard output/error streams, log messages, and exception messages.
/// </summary>
public static partial class GitCredentialRedactor
{
    public const string RedactedPlaceholder = "[REDACTED]";

    [GeneratedRegex(@"(?i)(http(?:\.[^\s=]+)?\.extraheader\s*=\s*(?:""|')?\s*authorization\s*:\s*(?:bearer|basic|token)?\s*)([^\s""']+)", RegexOptions.CultureInvariant)]
    private static partial Regex ExtraHeaderAuthRegex();

    [GeneratedRegex(@"(?i)(http(?:\.[^\s=]+)?\.extraheader\s*=\s*)([^\s""']+)", RegexOptions.CultureInvariant)]
    private static partial Regex ExtraHeaderGenericRegex();

    [GeneratedRegex(@"(?i)(authorization\s*:\s*(?:bearer|basic|token)\s+)([^\s""']+)", RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationHeaderRegex();

    [GeneratedRegex(@"(?i)(https?://[^/\s:@]+:)([^@\s/]+)(@)", RegexOptions.CultureInvariant)]
    private static partial Regex UrlUserPasswordRegex();

    [GeneratedRegex(@"(?i)(https?://)([^/\s:@]+)(@github\.com\b)", RegexOptions.CultureInvariant)]
    private static partial Regex UrlTokenAtGitHubRegex();

    [GeneratedRegex(@"\b(gh[pousr]_[A-Za-z0-9_]+|github_pat_[A-Za-z0-9_]+)\b", RegexOptions.CultureInvariant)]
    private static partial Regex GitHubTokenPrefixRegex();

    /// <summary>
    /// Redacts sensitive credential patterns from an arbitrary string.
    /// </summary>
    public static string Redact(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input ?? string.Empty;
        }

        var redacted = ExtraHeaderAuthRegex().Replace(input, $"$1{RedactedPlaceholder}");
        redacted = AuthorizationHeaderRegex().Replace(redacted, $"$1{RedactedPlaceholder}");
        redacted = ExtraHeaderGenericRegex().Replace(redacted, match =>
        {
            var value = match.Groups[2].Value;
            if (value.Contains(RedactedPlaceholder, StringComparison.Ordinal))
            {
                return match.Value;
            }
            return $"{match.Groups[1].Value}{RedactedPlaceholder}";
        });
        redacted = UrlUserPasswordRegex().Replace(redacted, $"$1{RedactedPlaceholder}$3");
        redacted = UrlTokenAtGitHubRegex().Replace(redacted, $"$1{RedactedPlaceholder}$3");
        redacted = GitHubTokenPrefixRegex().Replace(redacted, RedactedPlaceholder);

        return redacted;
    }

    /// <summary>
    /// Returns a copy of the argument list with all sensitive credential values redacted.
    /// </summary>
    public static IReadOnlyList<string> RedactArguments(IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return arguments.Select(Redact).ToList();
    }
}

