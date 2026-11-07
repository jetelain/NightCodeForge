using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace NightCodeForge.Agents.Tools;

/// <summary>
/// Formats file lines for the canonical read tool, enforcing a UTF-8 byte cap to prevent LLM context overflow.
/// </summary>
public static class ReadOutputFormatter
{
    public const string TruncatedMarker = "... [truncated]";
    public const string InvariantNumberFormat = "N0";
    public const char LineFeed = '\n';

    public static string BuildLineExceededFooter(int lineNum, int lineLength, int totalLines, int maxBytes)
    {
        int maxKb = Math.Max(1, maxBytes / 1024);
        string formattedLength = lineLength.ToString(InvariantNumberFormat, CultureInfo.InvariantCulture);
        return $"[Output truncated: line {lineNum} exceeded {maxKb} KB limit (line length: {formattedLength} characters, total file lines: {totalLines}). Use 'offset' and 'limit' or shell tools to inspect this file.]";
    }

    public static string BuildPagingFooter(int lastIncludedLine, int totalLines, int maxBytes)
    {
        int maxKb = Math.Max(1, maxBytes / 1024);
        return $"[Output truncated: reached {maxKb} KB limit at line {lastIncludedLine} of {totalLines}. Use 'offset' (e.g. offset: {lastIncludedLine + 1}) and 'limit' to read remaining lines.]";
    }

    public static string FormatCapped(
        IReadOnlyList<string> selectedLines,
        int startLine,
        int totalFileLines,
        int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(selectedLines);
        if (maxBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes), maxBytes, "maxBytes must be greater than zero.");
        }

        if (selectedLines.Count == 0)
        {
            return string.Empty;
        }

        // Pre-check if all requested lines fit within maxBytes.
        // If all requested lines fit, no truncation footer is required (even if at exact cap).
        int totalRequestedBytes = 0;
        bool allFit = true;
        for (int i = 0; i < selectedLines.Count; i++)
        {
            int lineNum = startLine + i;
            string formattedLine = $"{lineNum,5} | {selectedLines[i]}\n";
            totalRequestedBytes += Encoding.UTF8.GetByteCount(formattedLine);
            if (totalRequestedBytes > maxBytes)
            {
                allFit = false;
                break;
            }
        }

        if (allFit)
        {
            var fullSb = new StringBuilder();
            for (int i = 0; i < selectedLines.Count; i++)
            {
                int lineNum = startLine + i;
                fullSb.Append($"{lineNum,5} | {selectedLines[i]}\n");
            }
            return fullSb.ToString().TrimEnd();
        }

        // Truncation is required: compute exact footer budget for the worst-case line number length
        string sampleFooter = "\n\n" + BuildPagingFooter(totalFileLines, totalFileLines, maxBytes);
        int footerBudget = Encoding.UTF8.GetByteCount(sampleFooter);
        int lineBudget = Math.Max(0, maxBytes - footerBudget);

        var sb = new StringBuilder();
        int currentBytes = 0;
        int lastIncludedLine = startLine - 1;

        for (int i = 0; i < selectedLines.Count; i++)
        {
            int lineNum = startLine + i;
            string line = selectedLines[i];
            string formattedLine = $"{lineNum,5} | {line}\n";
            int lineBytes = Encoding.UTF8.GetByteCount(formattedLine);

            if (currentBytes + lineBytes <= lineBudget)
            {
                sb.Append(formattedLine);
                currentBytes += lineBytes;
                lastIncludedLine = lineNum;
            }
            else
            {
                break;
            }
        }

        if (lastIncludedLine < startLine)
        {
            // Not even the first requested line could fit into lineBudget.
            // Truncate the first line to fit within available budget alongside an appropriate footer.
            string firstLine = selectedLines[0];
            string footer = "\n\n" + BuildLineExceededFooter(startLine, firstLine.Length, totalFileLines, maxBytes);
            int footerBytes = Encoding.UTF8.GetByteCount(footer);

            // Adapt footer if maxBytes is smaller than the standard verbose footer
            if (footerBytes > maxBytes)
            {
                string compactFooter = $"\n\n[Truncated: {maxBytes}B limit]";
                if (Encoding.UTF8.GetByteCount(compactFooter) <= maxBytes)
                {
                    footer = compactFooter;
                }
                else
                {
                    const string minimal = "[Truncated]";
                    footer = Encoding.UTF8.GetByteCount(minimal) <= maxBytes
                        ? minimal
                        : TruncateStringToUtf8ByteCount(minimal, maxBytes);
                }
                footerBytes = Encoding.UTF8.GetByteCount(footer);
            }

            int availableForLine = Math.Max(0, maxBytes - footerBytes);
            string prefix = $"{startLine,5} | ";
            int prefixBytes = Encoding.UTF8.GetByteCount(prefix);
            int markerBytes = Encoding.UTF8.GetByteCount(TruncatedMarker);

            if (availableForLine >= prefixBytes + markerBytes)
            {
                int availableForContent = availableForLine - prefixBytes - markerBytes;
                string truncatedContent = TruncateStringToUtf8ByteCount(firstLine, availableForContent);
                sb.Append(prefix).Append(truncatedContent).Append(TruncatedMarker);
            }
            sb.Append(footer);
        }
        else
        {
            string footer = "\n\n" + BuildPagingFooter(lastIncludedLine, totalFileLines, maxBytes);
            sb.Append(footer);
        }

        var result = sb.ToString().TrimEnd();
        Debug.Assert(
            Encoding.UTF8.GetByteCount(result) <= maxBytes,
            $"ReadOutputFormatter budgeting bug: output {Encoding.UTF8.GetByteCount(result)} bytes exceeds maxBytes {maxBytes}.");

        return result;
    }

    public static string TruncateStringToUtf8ByteCount(string text, int maxBytes)
    {
        if (maxBytes <= 0 || string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        if (Encoding.UTF8.GetByteCount(text) <= maxBytes)
        {
            return text;
        }

        // Start from maxBytes chars (since in UTF-8 each char is at least 1 byte) and scan backward
        int index = Math.Min(text.Length, maxBytes);
        while (index > 0 && Encoding.UTF8.GetByteCount(text.AsSpan(0, index)) > maxBytes)
        {
            index--;
        }

        // Avoid splitting a surrogate pair (high surrogate followed by low surrogate)
        if (index > 0 && char.IsHighSurrogate(text[index - 1]))
        {
            index--;
        }

        return text[..index];
    }
}

