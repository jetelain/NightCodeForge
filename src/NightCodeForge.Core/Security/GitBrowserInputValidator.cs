using System.Text.RegularExpressions;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Core.Security;

/// <summary>
/// Strict input validator and normalizer for Git refs and repository-relative paths used by the read-only Git file browser.
/// Prevents Git CLI option injection, object-specifier injection, and path traversal.
/// </summary>
public static partial class GitBrowserInputValidator
{
    private const string RefLockSuffix = ".lock";

    [GeneratedRegex("^" + GitRepositoryBrowserConstants.TaskBranchPrefix + @"(\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TaskBranchExactRegex();

    [GeneratedRegex("^" + GitRepositoryBrowserConstants.TaskBranchPrefix + @"(\d+)(.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TaskBranchSortRegex();

    [GeneratedRegex(@"^[a-zA-Z]:([\\/]|$)")]
    private static partial Regex WindowsDrivePrefixRegex();

    /// <summary>
    /// Validates a Git branch name, tag, or commit SHA before passing it to Git CLI arguments.
    /// </summary>
    public static string ValidateRef(string? rawRef)
    {
        if (string.IsNullOrWhiteSpace(rawRef))
        {
            throw new GitBrowserInvalidInputException("Git ref cannot be empty.");
        }

        foreach (var ch in rawRef)
        {
            if (ch == '\0' || char.IsControl(ch))
            {
                throw new GitBrowserInvalidInputException("Git ref contains disallowed NUL or control characters.");
            }
        }

        var trimmed = rawRef.Trim();
        if (trimmed.StartsWith('-'))
        {
            throw new GitBrowserInvalidInputException($"Git ref '{trimmed}' cannot start with '-'.");
        }

        foreach (var ch in trimmed)
        {
            if (char.IsWhiteSpace(ch))
            {
                throw new GitBrowserInvalidInputException($"Git ref '{trimmed}' contains invalid whitespace characters.");
            }
        }

        if (string.Equals(trimmed, "@", StringComparison.Ordinal) ||
            trimmed.Contains(':') ||
            trimmed.Contains("..", StringComparison.Ordinal) ||
            trimmed.Contains("@{", StringComparison.Ordinal) ||
            trimmed.Contains('\\') ||
            trimmed.Contains('~') ||
            trimmed.Contains('^') ||
            trimmed.Contains('?') ||
            trimmed.Contains('*') ||
            trimmed.Contains('[') ||
            trimmed.Contains("//", StringComparison.Ordinal) ||
            trimmed.Contains("/.", StringComparison.Ordinal) ||
            trimmed.StartsWith('/') ||
            trimmed.EndsWith('/') ||
            trimmed.StartsWith('.') ||
            trimmed.EndsWith('.') ||
            trimmed.EndsWith(RefLockSuffix, StringComparison.OrdinalIgnoreCase))
        {
            throw new GitBrowserInvalidInputException($"Git ref '{trimmed}' contains disallowed characters or sequences.");
        }

        return trimmed;
    }

    /// <summary>
    /// Validates and normalizes a repository-relative path using forward slashes without leading or trailing slashes (or empty string for root).
    /// </summary>
    public static string NormalizeAndValidatePath(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return string.Empty;
        }

        foreach (var ch in rawPath)
        {
            if (ch == '\0' || char.IsControl(ch))
            {
                throw new GitBrowserInvalidInputException("Path contains disallowed NUL or control characters.");
            }
        }

        var trimmed = rawPath.Trim();

        if (trimmed.Contains("..\\", StringComparison.Ordinal) ||
            trimmed.Contains("\\..", StringComparison.Ordinal) ||
            trimmed.StartsWith(@"\\", StringComparison.Ordinal) ||
            trimmed.StartsWith("//", StringComparison.Ordinal) ||
            WindowsDrivePrefixRegex().IsMatch(trimmed))
        {
            throw new GitBrowserInvalidInputException($"Path '{trimmed}' is not a valid repository-relative path.");
        }

        var forwardSlashed = trimmed.Replace('\\', '/').Trim('/');
        if (string.IsNullOrEmpty(forwardSlashed))
        {
            return string.Empty;
        }

        var segments = forwardSlashed.Split('/', StringSplitOptions.None);
        var normalizedSegments = new List<string>(segments.Length);

        foreach (var segment in segments)
        {
            if (string.IsNullOrEmpty(segment) || segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                throw new GitBrowserInvalidInputException($"Path '{trimmed}' cannot contain parent directory traversal ('..').");
            }

            normalizedSegments.Add(segment);
        }

        return string.Join('/', normalizedSegments);
    }

    /// <summary>
    /// Extracts the numeric task ID from an exact <c>feature/task-{id}</c> branch name.
    /// </summary>
    public static bool TryExtractTaskBranchId(string? refName, out int taskId)
    {
        taskId = 0;
        if (string.IsNullOrWhiteSpace(refName))
        {
            return false;
        }

        var match = TaskBranchExactRegex().Match(refName.Trim());
        return match.Success && int.TryParse(match.Groups[1].Value, out taskId) && taskId > 0;
    }

    /// <summary>
    /// Extracts the numeric task ID and suffix for sorting <c>feature/task-*</c> branches.
    /// </summary>
    public static bool TryExtractTaskBranchSortKey(string? branchName, out int taskId, out string remainder)
    {
        taskId = 0;
        remainder = string.Empty;
        if (string.IsNullOrWhiteSpace(branchName))
        {
            return false;
        }

        var match = TaskBranchSortRegex().Match(branchName.Trim());
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out taskId))
        {
            return false;
        }

        remainder = match.Groups[2].Value;
        return true;
    }
}
