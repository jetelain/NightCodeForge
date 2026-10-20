namespace NightCodeForge.Core.Services;

using NightCodeForge.Core.Domain;

/// <summary>
/// Helper for formatting and merging deterministic, tool-managed text blocks
/// demarcated by header and footer comments into files without disturbing user edits.
/// </summary>
public static class ManagedBlockHelper
{
    public static string ApplyManagedBlock(string? existingContent, string blockContent)
    {
        var normalizedBlock = blockContent.Replace("\r\n", "\n").Trim();
        var fullBlock = $"{ManagedBlockConstants.Header}\n{normalizedBlock}\n{ManagedBlockConstants.Footer}";

        if (string.IsNullOrWhiteSpace(existingContent))
        {
            return fullBlock + "\n";
        }

        var normalizedExisting = existingContent.Replace("\r\n", "\n");
        var startIndex = normalizedExisting.IndexOf(ManagedBlockConstants.Header, StringComparison.Ordinal);
        var endIndex = startIndex >= 0
            ? normalizedExisting.IndexOf(ManagedBlockConstants.Footer, startIndex, StringComparison.Ordinal)
            : -1;

        if (startIndex >= 0 && endIndex >= startIndex)
        {
            var before = normalizedExisting.Substring(0, startIndex);
            var afterIndex = endIndex + ManagedBlockConstants.Footer.Length;
            var after = afterIndex < normalizedExisting.Length ? normalizedExisting.Substring(afterIndex) : string.Empty;

            var trimmedBefore = before.TrimEnd();
            var trimmedAfter = after.TrimStart('\n');

            var result = string.Empty;
            if (!string.IsNullOrEmpty(trimmedBefore))
            {
                result += trimmedBefore + "\n\n";
            }
            result += fullBlock + "\n";
            if (!string.IsNullOrEmpty(trimmedAfter))
            {
                result += "\n" + trimmedAfter.TrimEnd() + "\n";
            }
            return result;
        }

        var existingTrimmed = normalizedExisting.TrimEnd();
        return $"{existingTrimmed}\n\n{fullBlock}\n";
    }

    public static string? ExtractManagedBlock(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;

        var normalized = content.Replace("\r\n", "\n");
        var startIndex = normalized.IndexOf(ManagedBlockConstants.Header, StringComparison.Ordinal);
        if (startIndex < 0) return null;

        startIndex += ManagedBlockConstants.Header.Length;
        var endIndex = normalized.IndexOf(ManagedBlockConstants.Footer, startIndex, StringComparison.Ordinal);
        if (endIndex < 0) return null;

        return normalized.Substring(startIndex, endIndex - startIndex).Trim();
    }
}
