using System.Text.RegularExpressions;

namespace NightCodeForge.Core.Workflow.Filtering;

/// <summary>
/// Relativizes absolute paths in CLI diagnostics and strips MSBuild prefixes/suffixes.
/// </summary>
public static partial class CliOutputPathRelativizer
{
    [GeneratedRegex(@"^\s*\d+(?:[:>]\d+)?>\s*")]
    private static partial Regex MsBuildNodePrefixRegex();

    [GeneratedRegex(@"\s+\[[^\r\n\[\]]+\.(?:csproj|fsproj|vbproj|vcxproj|proj|targets)\]\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingProjectAnnotationRegex();

    [GeneratedRegex(@"(?<=^|[\s""'(]|in\s+)(?<path>(?:[A-Za-z]:[\\/]|\\\\wsl(?:\.localhost|\$)[\\/][^\\/]+[\\/]|/(?:workspace|clone|home|mnt|tmp|Users|var|opt|srv|root)/)[^\r\n()"":]+?)(?=(?:\(\d+(?:,\d+){0,3}\)|:\d+(?::\d+)?(?=:|\s|$|\))|:line\s+\d+|\s*:\s*(?:fatal\s+)?(?:error|warning)\s+[A-Za-z]+\d+\s*:))", RegexOptions.IgnoreCase)]
    private static partial Regex EmbeddedAbsoluteFilePathRegex();

    [GeneratedRegex(@"^/(?:workspace/(?:task-\d+|clone)|home/[^/]+/\.nightcodeforge/wks/\d+/(?:task-\d+|clone)|workspace|clone)/", RegexOptions.IgnoreCase)]
    private static partial Regex ContainerOrWorktreePrefixRegex();

    [GeneratedRegex(@"^.*[/\\]\.nightcodeforge[/\\]wks[/\\]\d+[/\\](?:task-\d+|clone)[/\\]", RegexOptions.IgnoreCase)]
    private static partial Regex HostWorktreePrefixRegex();

    /// <summary>
    /// Normalizes an MSBuild diagnostic line, stripping node prefixes and project tags.
    /// </summary>
    public static string NormalizeMsBuildDiagnosticLine(string line, string workspaceRoot, bool relativizePaths)
    {
        var cleaned = MsBuildNodePrefixRegex().Replace(line.Trim(), string.Empty);
        cleaned = TrailingProjectAnnotationRegex().Replace(cleaned, string.Empty).TrimEnd();
        return relativizePaths
            ? RelativizeLinePaths(cleaned, workspaceRoot)
            : cleaned;
    }

    /// <summary>
    /// Relativizes absolute host, WSL, UNC, or container paths embedded in CLI output.
    /// </summary>
    public static string RelativizeLinePaths(string line, string workspaceRoot)
    {
        if (string.IsNullOrEmpty(line))
        {
            return line;
        }

        var rootCandidates = BuildWorkspaceRootCandidates(workspaceRoot);
        return EmbeddedAbsoluteFilePathRegex().Replace(line, match =>
        {
            var rawPath = match.Groups["path"].Value;
            return RelativizePath(rawPath, rootCandidates);
        });
    }

    /// <summary>
    /// Relativizes a single path against <paramref name="workspaceRoot"/> and sandbox roots.
    /// </summary>
    public static string RelativizePath(string path, string workspaceRoot) =>
        RelativizePath(path, BuildWorkspaceRootCandidates(workspaceRoot));

    private static string RelativizePath(string path, IReadOnlyList<string> rootCandidates)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        var slashNorm = path.Trim().Replace('\\', '/');
        var wslUnwrapped = TryStripWslUncPrefix(slashNorm);

        foreach (var root in rootCandidates)
        {
            if (TryStripPrefix(slashNorm, root, out var rel))
            {
                return NormalizeRelativeResult(rel);
            }

            if (wslUnwrapped != null && TryStripPrefix(wslUnwrapped, root, out rel))
            {
                return NormalizeRelativeResult(rel);
            }
        }

        var worktreeMatch = HostWorktreePrefixRegex().Match(slashNorm);
        if (worktreeMatch.Success)
        {
            return NormalizeRelativeResult(slashNorm[worktreeMatch.Length..]);
        }

        var containerMatch = ContainerOrWorktreePrefixRegex().Match(slashNorm);
        if (containerMatch.Success)
        {
            return NormalizeRelativeResult(slashNorm[containerMatch.Length..]);
        }

        if (wslUnwrapped != null)
        {
            var wslContainerMatch = ContainerOrWorktreePrefixRegex().Match(wslUnwrapped);
            if (wslContainerMatch.Success)
            {
                return NormalizeRelativeResult(wslUnwrapped[wslContainerMatch.Length..]);
            }
        }

        return slashNorm;
    }

    private static IReadOnlyList<string> BuildWorkspaceRootCandidates(string? workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            return Array.Empty<string>();
        }

        var candidates = new List<string>();
        void AddCandidate(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var norm = value.Trim().Replace('\\', '/').TrimEnd('/');
            if (!string.IsNullOrEmpty(norm) && !candidates.Contains(norm, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(norm);
            }
        }

        var normalizedRoot = workspaceRoot.Trim().Replace('\\', '/').TrimEnd('/');
        AddCandidate(normalizedRoot);

        // Windows drive root C:/... -> /mnt/c/...
        if (normalizedRoot.Length >= 2 && char.IsAsciiLetter(normalizedRoot[0]) && normalizedRoot[1] == ':' &&
            (normalizedRoot.Length == 2 || normalizedRoot[2] == '/'))
        {
            var drive = char.ToLowerInvariant(normalizedRoot[0]);
            var rest = normalizedRoot[2..].TrimStart('/');
            AddCandidate(string.IsNullOrEmpty(rest) ? $"/mnt/{drive}" : $"/mnt/{drive}/{rest}");
        }

        // /mnt/c/... -> C:/...
        if (normalizedRoot.StartsWith("/mnt/", StringComparison.OrdinalIgnoreCase) &&
            normalizedRoot.Length >= 6 &&
            char.IsAsciiLetter(normalizedRoot[5]) &&
            (normalizedRoot.Length == 6 || normalizedRoot[6] == '/'))
        {
            var drive = char.ToUpperInvariant(normalizedRoot[5]);
            var rest = normalizedRoot.Length > 6 ? normalizedRoot[6..].TrimStart('/') : string.Empty;
            AddCandidate(string.IsNullOrEmpty(rest) ? $"{drive}:" : $"{drive}:/{rest}");
        }

        var unwrapped = TryStripWslUncPrefix(normalizedRoot);
        AddCandidate(unwrapped);

        return candidates.OrderByDescending(c => c.Length).ToList();
    }

    private static string? TryStripWslUncPrefix(string slashNorm)
    {
        if (!slashNorm.StartsWith("//wsl", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var converted = Interfaces.VirtualMountEntry.ToDefaultWslPath(slashNorm);
        return !string.Equals(converted, slashNorm, StringComparison.Ordinal) && converted.StartsWith('/')
            ? converted
            : null;
    }

    private static bool TryStripPrefix(string candidatePath, string prefix, out string remainder)
    {
        if (candidatePath.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
        {
            remainder = candidatePath[(prefix.Length + 1)..];
            return true;
        }

        if (string.Equals(candidatePath, prefix, StringComparison.OrdinalIgnoreCase))
        {
            remainder = string.Empty;
            return true;
        }

        remainder = string.Empty;
        return false;
    }

    private static string NormalizeRelativeResult(string rel)
    {
        var trimmed = rel.TrimStart('/');
        while (trimmed.StartsWith("./", StringComparison.Ordinal))
        {
            trimmed = trimmed[2..];
        }
        return trimmed;
    }
}
