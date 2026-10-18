using System.Text.RegularExpressions;

namespace NightCodeForge.Core.Execution;

/// <summary>
/// Provides secure shell-quoting and escaping utilities to prevent command and argument injection.
/// </summary>
public static class ShellQuoting
{
    private static readonly Regex ValidIdentifierRegex = new(@"^[a-zA-Z_][a-zA-Z0-9_]*$", RegexOptions.Compiled);

    /// <summary>
    /// Securely quotes a string literal for POSIX-compliant shells (sh, bash, zsh, dash).
    /// Encloses the string in single quotes and escapes embedded single quotes via '\''.
    /// </summary>
    /// <param name="value">The raw string to quote.</param>
    /// <returns>A safely quoted string for shell execution.</returns>
    public static string QuotePosix(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "''";
        }

        // In POSIX shell, everything inside single quotes is literal (including $, `, \, ", newlines).
        // A single quote is escaped by closing the quote, adding an escaped single quote, and reopening.
        return "'" + value.Replace("'", @"'\''") + "'";
    }

    /// <summary>
    /// Validates whether a string is a valid POSIX environment variable identifier ([a-zA-Z_][a-zA-Z0-9_]*).
    /// </summary>
    public static bool IsValidVariableName(string? name)
    {
        return !string.IsNullOrWhiteSpace(name) && ValidIdentifierRegex.IsMatch(name);
    }
}
