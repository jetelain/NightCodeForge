using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Workflow;

/// <summary>
/// Inspects a git diff stat to decide whether an implementation stage produced real repository changes
/// (anything other than scratch probes, task specification documents or transcripts).
/// </summary>
public static class ImplementationDiffEvaluator
{
    private const string StatPathSeparator = " | ";
    private const string StatTruncationMarker = "more files";
    private const string RenameArrow = "=>";

    private static readonly string[] NonProductionPathMarkers =
    [
        ".scratch/",
        "docs/tasks/",
        "docs/transcripts/"
    ];

    /// <summary>Returns true when the diff stat lists at least one file outside scratch, task-spec and transcript locations.</summary>
    public static bool HasProductionChanges(string? diffStat)
    {
        if (string.IsNullOrWhiteSpace(diffStat) ||
            string.Equals(diffStat.Trim(), GitDiffConstants.NoChanges, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var rawLine in diffStat.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (rawLine.Contains(StatTruncationMarker, StringComparison.OrdinalIgnoreCase) && rawLine.StartsWith("...", StringComparison.Ordinal))
            {
                // The stat was truncated after the maximum number of files: more files exist than are listed.
                return true;
            }

            var separatorIndex = rawLine.LastIndexOf(StatPathSeparator, StringComparison.Ordinal);
            if (separatorIndex <= 0)
            {
                continue; // summary line ("N files changed ...")
            }

            var path = rawLine[..separatorIndex].Trim().Replace('\\', '/');
            if (path.Contains(RenameArrow, StringComparison.Ordinal))
            {
                var parts = path.Split(RenameArrow, StringSplitOptions.TrimEntries);
                if (parts.Any(p => !IsNonProductionPath(p)))
                {
                    return true;
                }
                continue;
            }

            if (!IsNonProductionPath(path))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsNonProductionPath(string path)
    {
        var normalized = path.Replace("{", string.Empty, StringComparison.Ordinal).Replace("}", string.Empty, StringComparison.Ordinal);
        foreach (var marker in NonProductionPathMarkers)
        {
            if (normalized.StartsWith(marker, StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("/" + marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}

