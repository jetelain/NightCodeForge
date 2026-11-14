using System.Text.RegularExpressions;

namespace NightCodeForge.Core.Security;

/// <summary>
/// Redacts Git HTTP authentication headers, OAuth tokens, and embedded URL credentials
/// from CLI arguments, standard output/error streams, log messages, and exception messages.
/// </summary>
public static partial class GitCredentialRedactor
{
    public const string RedactedPlaceholder = "[REDACTED]";

    // Matches quoted: "http.extraHeader=..." or 'http.extraHeader=...'
    [GeneratedRegex(@"(?i)("")(http(?:\.[^\s=""']+)?\.extraheader\s*=\s*)([^""\r\n]+)("")", RegexOptions.CultureInvariant)]
    private static partial Regex DoubleQuotedFullExtraHeaderRegex();

    [GeneratedRegex(@"(?i)(')(http(?:\.[^\s=""']+)?\.extraheader\s*=\s*)([^'\r\n]+)(')", RegexOptions.CultureInvariant)]
    private static partial Regex SingleQuotedFullExtraHeaderRegex();

    // Matches quoted value: http.extraHeader="..." or http.extraHeader='...'
    [GeneratedRegex(@"(?i)(http(?:\.[^\s=""']+)?\.extraheader\s*=\s*"")([^""\r\n]+)("")", RegexOptions.CultureInvariant)]
    private static partial Regex DoubleQuotedValueExtraHeaderRegex();

    [GeneratedRegex(@"(?i)(http(?:\.[^\s=""']+)?\.extraheader\s*=\s*')([^'\r\n]+)(')", RegexOptions.CultureInvariant)]
    private static partial Regex SingleQuotedValueExtraHeaderRegex();

    // Matches standalone entire-argument extraHeader=HeaderName: Value (including custom schemes or arbitrary header values)
    [GeneratedRegex(@"(?is)^(http(?:\.[^\s=]+)?\.extraheader\s*=\s*(?:authorization\s*:[ \t]*(?:[A-Za-z0-9_-]+[ \t]+)?|[^:\r\n]+:[ \t]*)?)(.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex WholeArgumentExtraHeaderRegex();

    // Matches unquoted in multi-token strings: http.extraHeader=Authorization: <scheme> <secret> or http.extraHeader=Header: <secret>
    [GeneratedRegex(@"(?i)(http(?:\.[^\s=]+)?\.extraheader\s*=\s*(?:authorization\s*:[ \t]*(?:[A-Za-z0-9_-]+[ \t]+)?|[A-Za-z0-9-]+\s*:[ \t]*))([^\s""']+)", RegexOptions.CultureInvariant)]
    private static partial Regex ExtraHeaderAuthRegex();

    [GeneratedRegex(@"(?i)(http(?:\.[^\s=]+)?\.extraheader\s*=\s*)(?!\[REDACTED\]|[^:\s""']+\s*:[ \t]*(?:[A-Za-z0-9_-]+[ \t]+)?\[REDACTED\])([^\s""']+)", RegexOptions.CultureInvariant)]
    private static partial Regex ExtraHeaderGenericRegex();

    // Matches Authorization headers with arbitrary scheme + secret or single-token Authorization: <secret>
    [GeneratedRegex(@"(?i)(authorization\s*:[ \t]*)(?:[^\s""']+[ \t]+)?([^\s""']+)", RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationHeaderRegex();

    [GeneratedRegex(@"(?i)(https?://[^/\s:@]+:)([^@\s/]+)(@)", RegexOptions.CultureInvariant)]
    private static partial Regex UrlUserPasswordRegex();

    [GeneratedRegex(@"(?i)(https?://)([^/\s:@]+)(@[^\s/]+)", RegexOptions.CultureInvariant)]
    private static partial Regex UrlTokenAtHostRegex();

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

        var redacted = DoubleQuotedFullExtraHeaderRegex().Replace(input, $"$1$2{RedactedPlaceholder}$4");
        redacted = SingleQuotedFullExtraHeaderRegex().Replace(redacted, $"$1$2{RedactedPlaceholder}$4");
        redacted = DoubleQuotedValueExtraHeaderRegex().Replace(redacted, $"$1{RedactedPlaceholder}$3");
        redacted = SingleQuotedValueExtraHeaderRegex().Replace(redacted, $"$1{RedactedPlaceholder}$3");
        redacted = ExtraHeaderAuthRegex().Replace(redacted, match =>
        {
            var value = match.Groups[2].Value;
            return value.Contains(RedactedPlaceholder, StringComparison.Ordinal)
                ? match.Value
                : $"{match.Groups[1].Value}{RedactedPlaceholder}";
        });
        redacted = AuthorizationHeaderRegex().Replace(redacted, match =>
        {
            var value = match.Groups[2].Value;
            return value.Contains(RedactedPlaceholder, StringComparison.Ordinal)
                ? match.Value
                : $"{match.Groups[1].Value}{RedactedPlaceholder}";
        });
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
        redacted = UrlTokenAtHostRegex().Replace(redacted, match =>
        {
            var token = match.Groups[2].Value;
            return token.Contains(RedactedPlaceholder, StringComparison.Ordinal)
                ? match.Value
                : $"{match.Groups[1].Value}{RedactedPlaceholder}{match.Groups[3].Value}";
        });
        redacted = GitHubTokenPrefixRegex().Replace(redacted, RedactedPlaceholder);

        return redacted;
    }

    /// <summary>
    /// Redacts a single CLI argument, preserving awareness that the entire argument belongs to one option.
    /// </summary>
    public static string RedactArgument(string? argument)
    {
        if (string.IsNullOrEmpty(argument))
        {
            return argument ?? string.Empty;
        }

        var match = WholeArgumentExtraHeaderRegex().Match(argument);
        if (match.Success)
        {
            return Redact($"{match.Groups[1].Value}{RedactedPlaceholder}");
        }

        return Redact(argument);
    }

    /// <summary>
    /// Returns a copy of the argument list with all sensitive credential values redacted.
    /// </summary>
    public static IReadOnlyList<string> RedactArguments(IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return arguments.Select(RedactArgument).ToList();
    }
}

