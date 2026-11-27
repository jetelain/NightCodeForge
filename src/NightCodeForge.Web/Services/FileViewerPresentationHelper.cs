using System.Globalization;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Web.Services;

/// <summary>
/// Single clickable segment in the repository file viewer breadcrumb bar.
/// </summary>
public sealed record FileBreadcrumbSegment(string Label, string Path, bool IsCurrent);

/// <summary>
/// Flattened directory tree row rendered in the file viewer sidebar.
/// </summary>
public sealed record FileTreeNodeViewModel(
    GitTreeEntry Entry,
    int Depth,
    bool IsExpanded,
    bool IsActive,
    bool IsAncestorOfActive);

/// <summary>
/// Pure presentation helpers for <c>Files.razor</c>: URL construction, breadcrumbs, tree flattening, language labels, and formatting.
/// </summary>
public static class FileViewerPresentationHelper
{
    private const int ShortShaLength = 8;

    private static readonly Dictionary<string, string> ExtensionLanguageMap = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = "C#",
        [".csx"] = "C# Script",
        [".razor"] = "Razor",
        [".cshtml"] = "Razor",
        [".ts"] = "TypeScript",
        [".tsx"] = "TypeScript JSX",
        [".js"] = "JavaScript",
        [".jsx"] = "JavaScript JSX",
        [".py"] = "Python",
        [".json"] = "JSON",
        [".xml"] = "XML",
        [".csproj"] = "MSBuild",
        [".props"] = "MSBuild",
        [".targets"] = "MSBuild",
        [".slnx"] = "Solution XML",
        [".sln"] = "Solution",
        [".html"] = "HTML",
        [".htm"] = "HTML",
        [".css"] = "CSS",
        [".scss"] = "SCSS",
        [".md"] = "Markdown",
        [".markdown"] = "Markdown",
        [".sh"] = "Shell",
        [".bash"] = "Bash",
        [".ps1"] = "PowerShell",
        [".cmd"] = "Batch",
        [".bat"] = "Batch",
        [".yml"] = "YAML",
        [".yaml"] = "YAML",
        [".sql"] = "SQL",
        [".txt"] = "Plain Text"
    };

    /// <summary>
    /// Builds a canonical workspace-scoped URL to the repository file viewer.
    /// </summary>
    public static string BuildFileViewerUrl(
        int? workspaceId,
        string? gitRef = null,
        string? path = null,
        int? taskId = null,
        FileLineRange? lineRange = null)
    {
        var queryParts = new List<string>(3);

        if (!string.IsNullOrWhiteSpace(gitRef))
        {
            queryParts.Add($"{FilesUiConstants.RefQueryParameter}={Uri.EscapeDataString(gitRef.Trim())}");
        }

        var normalizedPath = NormalizeDisplayPath(path);
        if (!string.IsNullOrEmpty(normalizedPath))
        {
            queryParts.Add($"{FilesUiConstants.PathQueryParameter}={Uri.EscapeDataString(normalizedPath)}");
        }

        if (taskId.HasValue && taskId.Value > 0)
        {
            queryParts.Add($"{FilesUiConstants.TaskIdQueryParameter}={taskId.Value.ToString(CultureInfo.InvariantCulture)}");
        }

        var fragment = lineRange != null ? FileLineRangeHelper.FormatFragment(lineRange) : string.Empty;
        var subPath = queryParts.Count > 0
            ? $"{FilesUiConstants.RouteSegment}?{string.Join('&', queryParts)}{fragment}"
            : $"{FilesUiConstants.RouteSegment}{fragment}";

        return WorkspaceUrlHelper.BuildPath(workspaceId, subPath);
    }

    /// <summary>
    /// Splits a repository-relative path into clickable breadcrumb segments starting at the repository root.
    /// </summary>
    public static IReadOnlyList<FileBreadcrumbSegment> BuildBreadcrumbs(string? normalizedPath, string rootLabel)
    {
        var effectiveRoot = string.IsNullOrWhiteSpace(rootLabel) ? "repository" : rootLabel.Trim();
        var cleanPath = NormalizeDisplayPath(normalizedPath);

        if (string.IsNullOrEmpty(cleanPath))
        {
            return [new FileBreadcrumbSegment(effectiveRoot, string.Empty, IsCurrent: true)];
        }

        var parts = cleanPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var result = new List<FileBreadcrumbSegment>(parts.Length + 1)
        {
            new(effectiveRoot, string.Empty, IsCurrent: false)
        };

        for (var i = 0; i < parts.Length; i++)
        {
            var cumulative = string.Join('/', parts[..(i + 1)]);
            result.Add(new FileBreadcrumbSegment(parts[i], cumulative, IsCurrent: i == parts.Length - 1));
        }

        return result;
    }

    /// <summary>
    /// Returns the ordered list of parent directory paths (starting with <c>""</c> for root) leading to <paramref name="normalizedPath"/>.
    /// </summary>
    public static IReadOnlyList<string> GetAncestorDirectoryPaths(string? normalizedPath)
    {
        var cleanPath = NormalizeDisplayPath(normalizedPath);
        if (string.IsNullOrEmpty(cleanPath))
        {
            return [string.Empty];
        }

        var parts = cleanPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var ancestors = new List<string>(parts.Length) { string.Empty };
        for (var i = 0; i < parts.Length - 1; i++)
        {
            ancestors.Add(string.Join('/', parts[..(i + 1)]));
        }

        return ancestors;
    }

    /// <summary>
    /// Flattens loaded directories into visible tree rows according to <paramref name="expandedDirs"/>.
    /// </summary>
    public static IReadOnlyList<FileTreeNodeViewModel> BuildVisibleTreeNodes(
        IReadOnlyDictionary<string, IReadOnlyList<GitTreeEntry>> loadedDirs,
        IReadOnlySet<string> expandedDirs,
        string? activePath)
    {
        ArgumentNullException.ThrowIfNull(loadedDirs);
        ArgumentNullException.ThrowIfNull(expandedDirs);

        if (!loadedDirs.TryGetValue(string.Empty, out var rootEntries) || rootEntries.Count == 0)
        {
            return [];
        }

        var cleanActivePath = NormalizeDisplayPath(activePath);
        var result = new List<FileTreeNodeViewModel>();
        AppendTreeNodes(rootEntries, depth: 0, loadedDirs, expandedDirs, cleanActivePath, result);
        return result;
    }

    private static void AppendTreeNodes(
        IReadOnlyList<GitTreeEntry> entries,
        int depth,
        IReadOnlyDictionary<string, IReadOnlyList<GitTreeEntry>> loadedDirs,
        IReadOnlySet<string> expandedDirs,
        string cleanActivePath,
        List<FileTreeNodeViewModel> output)
    {
        foreach (var entry in entries)
        {
            var isDirectory = entry.Kind == GitTreeEntryKind.Directory;
            var isExpanded = isDirectory && expandedDirs.Contains(entry.Path);
            var isActive = string.Equals(entry.Path, cleanActivePath, StringComparison.Ordinal);
            var isAncestor = !string.IsNullOrEmpty(cleanActivePath) &&
                             cleanActivePath.StartsWith(entry.Path + "/", StringComparison.Ordinal);

            output.Add(new FileTreeNodeViewModel(entry, depth, isExpanded, isActive, isAncestor));

            if (isExpanded && loadedDirs.TryGetValue(entry.Path, out var children) && children.Count > 0)
            {
                AppendTreeNodes(children, depth + 1, loadedDirs, expandedDirs, cleanActivePath, output);
            }
        }
    }

    public static bool IsMarkdownFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var ext = Path.GetExtension(path.Trim());
        return string.Equals(ext, ".md", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(ext, ".markdown", StringComparison.OrdinalIgnoreCase);
    }

    public static string DetectLanguageLabel(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "Plain Text";
        }

        var ext = Path.GetExtension(path.Trim());
        if (string.IsNullOrEmpty(ext))
        {
            return "Plain Text";
        }

        return ExtensionLanguageMap.TryGetValue(ext, out var label)
            ? label
            : ext.TrimStart('.').ToUpperInvariant();
    }

    public static string FormatByteSize(long? bytes)
    {
        if (!bytes.HasValue || bytes.Value < 0)
        {
            return "—";
        }

        var value = bytes.Value;
        if (value < 1024)
        {
            return $"{value.ToString(CultureInfo.InvariantCulture)} B";
        }

        if (value < 1024 * 1024)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{value / 1024.0:F1} KB");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{value / (1024.0 * 1024.0):F2} MB");
    }

    /// <summary>
    /// Splits file text into 1-indexed display lines without creating a phantom trailing empty line after a final newline.
    /// </summary>
    public static IReadOnlyList<string> SplitLines(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return [];
        }

        var normalized = content
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

        if (normalized.EndsWith('\n'))
        {
            normalized = normalized[..^1];
        }

        return normalized.Length == 0
            ? [string.Empty]
            : normalized.Split('\n');
    }

    public static string GetEntryIcon(GitTreeEntryKind kind, bool isExpanded = false) => kind switch
    {
        GitTreeEntryKind.Directory => isExpanded ? "📂" : "📁",
        GitTreeEntryKind.Symlink => "🔗",
        GitTreeEntryKind.Submodule => "📦",
        _ => "📄"
    };

    public static string ShortSha(string? sha)
    {
        if (string.IsNullOrWhiteSpace(sha))
        {
            return string.Empty;
        }

        var trimmed = sha.Trim();
        return trimmed.Length <= ShortShaLength ? trimmed : trimmed[..ShortShaLength];
    }

    public static string FormatFallbackMessage(GitRefFallbackInfo fallback)
    {
        ArgumentNullException.ThrowIfNull(fallback);
        return fallback.Reason switch
        {
            GitRefFallbackReason.SquashCommitSha =>
                $"Branch '{fallback.RequestedRef}' is no longer present; showing Task #{fallback.TaskId} squash commit {ShortSha(fallback.ResolvedRef)}.",
            GitRefFallbackReason.TaskBaseBranch =>
                $"Branch '{fallback.RequestedRef}' is no longer present; showing Task #{fallback.TaskId} base branch '{fallback.ResolvedRef}'.",
            GitRefFallbackReason.WorkspaceDefaultBranch =>
                $"Branch '{fallback.RequestedRef}' is no longer present; showing workspace default branch '{fallback.ResolvedRef}'.",
            _ =>
                $"Requested ref '{fallback.RequestedRef}' resolved to '{fallback.ResolvedRef}'."
        };
    }

    public static string NormalizeDisplayPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        return path.Trim().Replace('\\', '/').Trim('/');
    }

    public static string ResolveEffectiveRef(string? refQuery, string? workspaceDefaultBranch) =>
        !string.IsNullOrWhiteSpace(refQuery)
            ? refQuery.Trim()
            : (string.IsNullOrWhiteSpace(workspaceDefaultBranch)
                ? GitRepositoryBrowserConstants.DefaultBranchName
                : workspaceDefaultBranch.Trim());

    public static string BuildTargetQueryKey(int? workspaceId, string? gitRef, string? path, int? taskId) =>
        $"{workspaceId?.ToString(CultureInfo.InvariantCulture) ?? "none"}|{gitRef ?? string.Empty}|{path ?? string.Empty}|{taskId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty}";

    public static string StripFragment(string? uri)
    {
        if (string.IsNullOrEmpty(uri))
        {
            return string.Empty;
        }

        var hashIdx = uri.IndexOf('#');
        return hashIdx >= 0 ? uri[..hashIdx] : uri;
    }

    public static string FormatTreeRowIndentStyle(int depth)
    {
        var clampedDepth = Math.Max(0, depth);
        var indentRem = FilesUiConstants.TreeBaseIndentRem + (clampedDepth * FilesUiConstants.TreeDepthIndentRem);
        return string.Create(CultureInfo.InvariantCulture, $"padding-left: {indentRem:0.##}rem;");
    }

    public static string FormatLineCountLabel(int lineCount) =>
        lineCount == 1
            ? "1 line"
            : string.Create(CultureInfo.InvariantCulture, $"{lineCount} lines");
}
