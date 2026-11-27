using System.Globalization;
using System.Text.RegularExpressions;

namespace NightCodeForge.Web.Services;

/// <summary>
/// Inclusive 1-based line range within a repository file.
/// </summary>
public sealed record FileLineRange(int StartLine, int EndLine);

/// <summary>
/// Pure helper for parsing, normalizing, clamping, and formatting file line anchors (#L42, #L42-L58) and query fallbacks (?lines=42-58).
/// </summary>
public static partial class FileLineRangeHelper
{
    [GeneratedRegex(@"^[Ll](\d+)(?:-[Ll]?(\d+))?$")]
    private static partial Regex FragmentLineRegex();

    [GeneratedRegex(@"^[Ll]?(\d+)(?:-[Ll]?(\d+))?$")]
    private static partial Regex QueryLineRegex();

    /// <summary>
    /// Parses a line or line range from a URL or fragment string (e.g. <c>#L42</c>, <c>#l42-L58</c>, <c>#L42-58</c>).
    /// </summary>
    public static bool TryParseFragment(string? uriOrFragment, out FileLineRange? range)
    {
        range = null;
        if (string.IsNullOrWhiteSpace(uriOrFragment))
        {
            return false;
        }

        var trimmed = uriOrFragment.Trim();
        var hashIndex = trimmed.IndexOf('#');
        var fragment = hashIndex >= 0 ? trimmed[(hashIndex + 1)..].Trim() : trimmed;
        if (string.IsNullOrEmpty(fragment))
        {
            return false;
        }

        return TryParseWithRegex(FragmentLineRegex(), fragment, out range);
    }

    /// <summary>
    /// Parses a line or line range from a <c>lines</c> query parameter value (e.g. <c>42</c>, <c>42-58</c>, <c>L42-L58</c>).
    /// </summary>
    public static bool TryParseQueryValue(string? linesQuery, out FileLineRange? range)
    {
        range = null;
        if (string.IsNullOrWhiteSpace(linesQuery))
        {
            return false;
        }

        return TryParseWithRegex(QueryLineRegex(), linesQuery.Trim(), out range);
    }

    /// <summary>
    /// Resolves the active line range from the URI fragment (preferred) or <c>lines</c> query parameter, clamped safely to <paramref name="totalLines"/>.
    /// </summary>
    public static FileLineRange? ResolveEffectiveRange(string? uri, string? linesQuery, int totalLines)
    {
        if (totalLines <= 0)
        {
            return null;
        }

        if (!TryParseFragment(uri, out var parsed) && !TryParseQueryValue(linesQuery, out parsed))
        {
            return null;
        }

        if (parsed == null)
        {
            return null;
        }

        var clampedStart = Math.Clamp(parsed.StartLine, 1, totalLines);
        var clampedEnd = Math.Clamp(parsed.EndLine, 1, totalLines);
        if (clampedEnd < clampedStart)
        {
            (clampedStart, clampedEnd) = (clampedEnd, clampedStart);
        }

        return new FileLineRange(clampedStart, clampedEnd);
    }

    /// <summary>
    /// Returns true if either the URI fragment or <c>lines</c> query parameter targets a specific line or line range.
    /// </summary>
    public static bool HasLineTarget(string? uri, string? linesQuery) =>
        TryParseFragment(uri, out _) || TryParseQueryValue(linesQuery, out _);

    /// <summary>
    /// Formats a line range as a canonical URL fragment (<c>#L42</c> or <c>#L42-L58</c>).
    /// </summary>
    public static string FormatFragment(FileLineRange range)
    {
        ArgumentNullException.ThrowIfNull(range);
        var start = Math.Max(1, range.StartLine);
        var end = Math.Max(start, range.EndLine);
        return start == end ? $"#L{start}" : $"#L{start}-L{end}";
    }

    /// <summary>
    /// Computes the next line selection when a user clicks or Shift+clicks a line number.
    /// </summary>
    public static FileLineRange ComputeClickedRange(FileLineRange? currentRange, int clickedLine, bool isShiftKey)
    {
        var line = Math.Max(1, clickedLine);
        if (isShiftKey && currentRange != null)
        {
            var anchor = Math.Max(1, currentRange.StartLine);
            var start = Math.Min(anchor, line);
            var end = Math.Max(anchor, line);
            return new FileLineRange(start, end);
        }

        return new FileLineRange(line, line);
    }

    private static bool TryParseWithRegex(Regex regex, string input, out FileLineRange? range)
    {
        range = null;
        var match = regex.Match(input);
        if (!match.Success)
        {
            return false;
        }

        if (!int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var startLine) ||
            startLine <= 0)
        {
            return false;
        }

        var endLine = startLine;
        if (match.Groups[2].Success)
        {
            if (!int.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out endLine) ||
                endLine <= 0)
            {
                return false;
            }
        }

        if (endLine < startLine)
        {
            (startLine, endLine) = (endLine, startLine);
        }

        range = new FileLineRange(startLine, endLine);
        return true;
    }
}
