using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using NightCodeForge.Infrastructure.Git;

namespace NightCodeForge.Agents.Tools.EditorConfig;

/// <summary>
/// Enforces EditorConfig formatting rules (EOL, indentation, trailing whitespace, final newline, charset).
/// </summary>
public partial class EditorConfigFileApplier : IEditorConfigFileApplier
{
    [GeneratedRegex(@"\r\n|\r|\n")]
    private static partial Regex NewlineRegex();

    public string ApplyFormatting(string content, EditorConfigSettings settings, string? existingContent = null, string? targetPath = null)
    {
        if (content == null)
        {
            return string.Empty;
        }

        // 1. Determine target line ending
        string targetEol = ResolveTargetEol(settings.EndOfLine, existingContent ?? content, targetPath);

        // 2. Split content into lines preserving empty lines
        var rawLines = NewlineRegex().Split(content);
        var processedLines = new List<string>(rawLines.Length);

        int indentSize = settings.IndentSize ?? settings.TabWidth ?? 4;
        if (indentSize <= 0) indentSize = 4;

        foreach (var rawLine in rawLines)
        {
            var line = rawLine;

            // Trim trailing whitespace if enabled
            if (settings.TrimTrailingWhitespace == true)
            {
                line = line.TrimEnd(' ', '\t');
            }

            // Indentation conversion
            if (settings.IndentStyle == IndentStyle.Tab)
            {
                line = ConvertSpacesToTabs(line, indentSize);
            }
            else if (settings.IndentStyle == IndentStyle.Space)
            {
                line = ConvertTabsToSpaces(line, indentSize);
            }

            processedLines.Add(line);
        }

        // Remove trailing empty lines if final newline is false, or trim down to single empty end
        if (settings.InsertFinalNewline == false)
        {
            while (processedLines.Count > 0 && string.IsNullOrEmpty(processedLines[^1]))
            {
                processedLines.RemoveAt(processedLines.Count - 1);
            }
            return string.Join(targetEol, processedLines);
        }

        // Default or InsertFinalNewline == true: ensure terminating newline
        while (processedLines.Count > 0 && string.IsNullOrEmpty(processedLines[^1]))
        {
            processedLines.RemoveAt(processedLines.Count - 1);
        }

        if (processedLines.Count == 0)
        {
            return settings.InsertFinalNewline == true ? targetEol : string.Empty;
        }

        var result = string.Join(targetEol, processedLines);
        if (settings.InsertFinalNewline == true || settings.InsertFinalNewline == null)
        {
            result += targetEol;
        }

        return result;
    }

    public byte[] ApplyFormattingAndEncode(string content, EditorConfigSettings settings, string? existingContent = null, string? targetPath = null)
    {
        var formatted = ApplyFormatting(content, settings, existingContent, targetPath);
        var encoding = settings.Charset ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        return encoding.GetBytes(formatted);
    }

    public static string ResolveTargetEol(EndOfLine? setting, string sample, string? targetPath = null)
    {
        if (!string.IsNullOrWhiteSpace(targetPath))
        {
            var trimmedPath = targetPath.Trim();
            // If targetPath is a WSL path (POSIX path starting with / or ~, or Windows UNC path matching WslPathConverter.IsWslUncPath),
            // unconditionally force \n (LF), overriding any CRLF auto-detection from existing content.
            if (trimmedPath.StartsWith('/') || trimmedPath.StartsWith('~') || WslPathConverter.IsWslUncPath(trimmedPath))
            {
                return "\n";
            }

            // If setting is not specified by .editorconfig and the environment is on Windows targeting a workspace mirror,
            // default to \n rather than auto-detecting CRLF.
            if (!setting.HasValue && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var norm = trimmedPath.Replace('/', '\\');
                if (norm.Contains(@"\.nightcodeforge\", StringComparison.OrdinalIgnoreCase) ||
                    norm.Contains(@"\wks\", StringComparison.OrdinalIgnoreCase) ||
                    norm.Contains(@"\wt\", StringComparison.OrdinalIgnoreCase) ||
                    norm.Contains(@"\clone\", StringComparison.OrdinalIgnoreCase) ||
                    norm.Contains("task-", StringComparison.OrdinalIgnoreCase))
                {
                    return "\n";
                }
            }
        }

        if (setting.HasValue)
        {
            return setting.Value switch
            {
                EndOfLine.CrLf => "\r\n",
                EndOfLine.Cr => "\r",
                _ => "\n"
            };
        }

        // Auto-detect dominant EOL
        if (!string.IsNullOrEmpty(sample))
        {
            int crlfCount = 0;
            int lfCount = 0;
            for (int i = 0; i < sample.Length; i++)
            {
                if (sample[i] == '\r' && i + 1 < sample.Length && sample[i + 1] == '\n')
                {
                    crlfCount++;
                    i++;
                }
                else if (sample[i] == '\n')
                {
                    lfCount++;
                }
            }

            if (crlfCount > lfCount) return "\r\n";
            if (lfCount > 0) return "\n";
        }

        return "\n";
    }

    private static string ConvertSpacesToTabs(string line, int indentSize)
    {
        int spaceCount = 0;
        int i = 0;
        while (i < line.Length && line[i] == ' ')
        {
            spaceCount++;
            i++;
        }

        if (spaceCount == 0) return line;

        int tabs = spaceCount / indentSize;
        int remainingSpaces = spaceCount % indentSize;
        return new string('\t', tabs) + new string(' ', remainingSpaces) + line.Substring(i);
    }

    private static string ConvertTabsToSpaces(string line, int indentSize)
    {
        int tabCount = 0;
        int i = 0;
        while (i < line.Length && line[i] == '\t')
        {
            tabCount++;
            i++;
        }

        if (tabCount == 0) return line;

        return new string(' ', tabCount * indentSize) + line.Substring(i);
    }
}
