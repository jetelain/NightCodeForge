using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Core.Memory;

/// <summary>Tier of an agent memory file.</summary>
public enum AgentMemoryScope
{
    None = 0,
    /// <summary>Tier 2: <c>~/.nightcodeforge/wks/{id}/memory/*.md</c> (out of Git).</summary>
    LocalWorkspace = 1,
    /// <summary>Tier 1: <c>AGENTS.md</c> and <c>.agents/memory/*.md</c> (in Git).</summary>
    SharedRepository = 2
}

public enum AgentMemoryChangeKind
{
    Created = 0,
    Updated = 1,
    Deleted = 2
}

/// <summary>A detected memory file change.</summary>
public record AgentMemoryChangeEvent(
    int WorkspaceId,
    AgentMemoryScope Scope,
    string RelativePath,
    AgentMemoryChangeKind Kind,
    string? Sha256);

public static class AgentMemoryConstants
{
    public const string PinnedMemoryFileName = "MEMORY.md";
    public const string MarkdownExtension = ".md";
    public const string LocalMemoryDirectoryName = "memory";
    public const string VirtualLocalMemoryPath = "/workspace/memory";
    public const string SharedMemoryDirectory = ".agents/memory";
    public const string RootRulesFileName = "AGENTS.md";
    public const string AgentsRulesFilePath = ".agents/AGENTS.md";

    public const int MaxPinnedMemoryLines = 150;
    public const int MaxPinnedMemoryBytes = 12_288;
    public const int MaxCatalogEntries = 50;
    public const int MaxCatalogDescriptionLength = 140;

    public const string ScopeLocalMetadataValue = "Local";
    public const string ScopeSharedGitMetadataValue = "SharedGit";

    public static string ToMetadataValue(AgentMemoryScope scope) => scope switch
    {
        AgentMemoryScope.LocalWorkspace => ScopeLocalMetadataValue,
        AgentMemoryScope.SharedRepository => ScopeSharedGitMetadataValue,
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Memory scope has no metadata representation.")
    };
}

/// <summary>
/// Canonical dangling-symlink-safe reparse-point check shared across Core, Infrastructure, Agents and Web.
/// </summary>
public static class AgentMemoryReparseGuard
{
    /// <summary>
    /// True when <paramref name="path"/> is an existing symlink or reparse point (including dangling symlinks).
    /// Returns false for non-existent paths.
    /// </summary>
    public static bool IsReparsePoint(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var info = new FileInfo(path);
        if (info.LinkTarget != null)
        {
            return true;
        }

        var attributes = info.Attributes;
        return (int)attributes != -1 && attributes.HasFlag(FileAttributes.ReparsePoint);
    }

    /// <summary>
    /// True when <paramref name="path"/> or any directory between <paramref name="root"/> and the path
    /// is a symbolic link or reparse point (including dangling symlinks). Paths outside <paramref name="root"/> are always rejected.
    /// </summary>
    public static bool HasReparsePointInChain(string root, string path, bool includeRoot = false)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (includeRoot && IsReparsePoint(fullRoot))
        {
            return true;
        }

        var current = fullRoot;
        var relative = fullPath[fullRoot.Length..].Trim(Path.DirectorySeparatorChar);
        if (relative.Length == 0)
        {
            return false;
        }

        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (IsReparsePoint(current))
            {
                return true;
            }

            if (!File.Exists(current) && !Directory.Exists(current))
            {
                return false;
            }
        }

        return false;
    }
}

/// <summary>Classifies virtual / repo-relative / host paths into memory tiers.</summary>
public static class AgentMemoryPathClassifier
{
    private const string LocalHostMarker = ".nightcodeforge/wks/";
    private const string WorkspaceSegment = "workspace";
    private const string CloneSegment = "clone";
    private const string TaskPrefix = "task-";

    public static AgentMemoryScope Classify(string? path) => Classify(path, repositoryRoot: null);

    public static AgentMemoryScope Classify(string? path, string? repositoryRoot)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return AgentMemoryScope.None;
        }

        var p = StripLeadingDotSlash(path.Trim().Replace('\\', '/'));

        // Virtual /workspace/... or workspace/...
        if (TryExtractAfterWorkspacePrefix(p, out var afterWorkspace))
        {
            if (!SandboxPathResolver.TryNormalizeRelativeSegments(afterWorkspace, out var collapsedWorkspace))
            {
                return AgentMemoryScope.None;
            }

            var localPrefix = AgentMemoryConstants.LocalMemoryDirectoryName + "/";
            if (collapsedWorkspace.StartsWith(localPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return IsFlatMarkdown(collapsedWorkspace[localPrefix.Length..])
                    ? AgentMemoryScope.LocalWorkspace
                    : AgentMemoryScope.None;
            }

            if (string.Equals(collapsedWorkspace, AgentMemoryConstants.LocalMemoryDirectoryName, StringComparison.OrdinalIgnoreCase))
            {
                return AgentMemoryScope.None;
            }

            var workspaceParts = collapsedWorkspace.Split('/', 2);
            if (workspaceParts.Length == 2 && IsRepositorySubTreeSegment(workspaceParts[0]))
            {
                return TryGetCanonicalSharedFromRelative(workspaceParts[1], out _)
                    ? AgentMemoryScope.SharedRepository
                    : AgentMemoryScope.None;
            }

            return AgentMemoryScope.None;
        }

        // Host/WSL: .../.nightcodeforge/wks/{id}/(memory/*.md | clone/... | task-{id}/...)
        var wksIdx = p.IndexOf(LocalHostMarker, StringComparison.OrdinalIgnoreCase);
        if (wksIdx >= 0)
        {
            var afterMarker = p[(wksIdx + LocalHostMarker.Length)..];
            var slashIdx = afterMarker.IndexOf('/');
            if (slashIdx > 0 && int.TryParse(afterMarker[..slashIdx], out _))
            {
                var afterWksId = afterMarker[(slashIdx + 1)..];
                if (!SandboxPathResolver.TryNormalizeRelativeSegments(afterWksId, out var collapsedUnderWks))
                {
                    return AgentMemoryScope.None;
                }

                var wksParts = collapsedUnderWks.Split('/', 2);
                if (wksParts.Length == 2)
                {
                    if (string.Equals(wksParts[0], AgentMemoryConstants.LocalMemoryDirectoryName, StringComparison.OrdinalIgnoreCase))
                    {
                        return IsFlatMarkdown(wksParts[1]) ? AgentMemoryScope.LocalWorkspace : AgentMemoryScope.None;
                    }

                    if (IsRepositorySubTreeSegment(wksParts[0]))
                    {
                        return TryGetCanonicalSharedFromRelative(wksParts[1], out _)
                            ? AgentMemoryScope.SharedRepository
                            : AgentMemoryScope.None;
                    }
                }
            }

            return AgentMemoryScope.None;
        }

        // Explicit repository root (BareHost / worktree absolute paths)
        if (!string.IsNullOrWhiteSpace(repositoryRoot) &&
            TryExtractRelativeToRepositoryRoot(p, path, repositoryRoot, out var repoRelative))
        {
            return SandboxPathResolver.TryNormalizeRelativeSegments(repoRelative, out var collapsedRepoRel) &&
                   TryGetCanonicalSharedFromRelative(collapsedRepoRel, out _)
                ? AgentMemoryScope.SharedRepository
                : AgentMemoryScope.None;
        }

        // Repo-relative paths (must not be rooted paths outside the repository)
        if (IsRootedOrUncPath(p))
        {
            return AgentMemoryScope.None;
        }

        return SandboxPathResolver.TryNormalizeRelativeSegments(p, out var cleanRel) &&
               TryGetCanonicalSharedFromRelative(cleanRel, out _)
            ? AgentMemoryScope.SharedRepository
            : AgentMemoryScope.None;
    }

    /// <summary>
    /// Extracts the canonical repository-relative path (<c>AGENTS.md</c>, <c>.agents/AGENTS.md</c>, or
    /// <c>.agents/memory/&lt;name&gt;.md</c>) when <paramref name="path"/> targets shared repository memory.
    /// </summary>
    public static bool TryGetCanonicalSharedRelativePath(
        string? path,
        string? repositoryRoot,
        out string canonicalRelativePath)
    {
        canonicalRelativePath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var p = StripLeadingDotSlash(path.Trim().Replace('\\', '/'));

        if (TryExtractAfterWorkspacePrefix(p, out var afterWorkspace))
        {
            if (!SandboxPathResolver.TryNormalizeRelativeSegments(afterWorkspace, out var collapsedWorkspace))
            {
                return false;
            }

            var parts = collapsedWorkspace.Split('/', 2);
            return parts.Length == 2 &&
                   IsRepositorySubTreeSegment(parts[0]) &&
                   TryGetCanonicalSharedFromRelative(parts[1], out canonicalRelativePath);
        }

        var wksIdx = p.IndexOf(LocalHostMarker, StringComparison.OrdinalIgnoreCase);
        if (wksIdx >= 0)
        {
            var afterMarker = p[(wksIdx + LocalHostMarker.Length)..];
            var slashIdx = afterMarker.IndexOf('/');
            if (slashIdx <= 0 || !int.TryParse(afterMarker[..slashIdx], out _))
            {
                return false;
            }

            if (!SandboxPathResolver.TryNormalizeRelativeSegments(afterMarker[(slashIdx + 1)..], out var collapsedUnderWks))
            {
                return false;
            }

            var parts = collapsedUnderWks.Split('/', 2);
            return parts.Length == 2 &&
                   IsRepositorySubTreeSegment(parts[0]) &&
                   TryGetCanonicalSharedFromRelative(parts[1], out canonicalRelativePath);
        }

        if (!string.IsNullOrWhiteSpace(repositoryRoot) &&
            TryExtractRelativeToRepositoryRoot(p, path, repositoryRoot, out var repoRelative))
        {
            return SandboxPathResolver.TryNormalizeRelativeSegments(repoRelative, out var collapsedRepoRel) &&
                   TryGetCanonicalSharedFromRelative(collapsedRepoRel, out canonicalRelativePath);
        }

        if (IsRootedOrUncPath(p))
        {
            return false;
        }

        return SandboxPathResolver.TryNormalizeRelativeSegments(p, out var cleanRel) &&
               TryGetCanonicalSharedFromRelative(cleanRel, out canonicalRelativePath);
    }

    private static bool TryGetCanonicalSharedFromRelative(string rel, out string canonical)
    {
        canonical = string.Empty;
        if (string.Equals(rel, AgentMemoryConstants.RootRulesFileName, StringComparison.OrdinalIgnoreCase))
        {
            canonical = AgentMemoryConstants.RootRulesFileName;
            return true;
        }

        if (string.Equals(rel, AgentMemoryConstants.AgentsRulesFilePath, StringComparison.OrdinalIgnoreCase))
        {
            canonical = AgentMemoryConstants.AgentsRulesFilePath;
            return true;
        }

        var sharedPrefix = AgentMemoryConstants.SharedMemoryDirectory + "/";
        if (rel.StartsWith(sharedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var fileName = rel[sharedPrefix.Length..];
            if (IsFlatMarkdown(fileName))
            {
                canonical = $"{AgentMemoryConstants.SharedMemoryDirectory}/{fileName}";
                return true;
            }
        }

        return false;
    }

    private static bool TryExtractAfterWorkspacePrefix(string normalized, out string remainder)
    {
        const string slashWorkspace = "/" + WorkspaceSegment + "/";
        const string bareWorkspace = WorkspaceSegment + "/";
        if (normalized.StartsWith(slashWorkspace, StringComparison.OrdinalIgnoreCase))
        {
            remainder = normalized[slashWorkspace.Length..];
            return true;
        }

        if (normalized.StartsWith(bareWorkspace, StringComparison.OrdinalIgnoreCase))
        {
            remainder = normalized[bareWorkspace.Length..];
            return true;
        }

        remainder = string.Empty;
        return false;
    }

    private static bool TryExtractRelativeToRepositoryRoot(
        string normalizedPath,
        string rawPath,
        string repositoryRoot,
        out string relativePath)
    {
        relativePath = string.Empty;
        var normInput = TryFullPathSlashNormalized(rawPath, normalizedPath);
        var normRoot = TryFullPathSlashNormalized(repositoryRoot, repositoryRoot.Trim().Replace('\\', '/')).TrimEnd('/');
        if (string.IsNullOrEmpty(normRoot))
        {
            return false;
        }

        if (!normInput.StartsWith(normRoot + "/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        relativePath = normInput[(normRoot.Length + 1)..];
        return true;
    }

    private static string TryFullPathSlashNormalized(string raw, string slashNormalized)
    {
        if (slashNormalized.StartsWith("//", StringComparison.Ordinal) ||
            slashNormalized.StartsWith('~') ||
            !Path.IsPathRooted(raw))
        {
            return slashNormalized;
        }

        try
        {
            return Path.GetFullPath(raw).Replace('\\', '/');
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return slashNormalized;
        }
    }

    private static bool IsRepositorySubTreeSegment(string segment) =>
        segment.Equals(CloneSegment, StringComparison.OrdinalIgnoreCase) ||
        segment.StartsWith(TaskPrefix, StringComparison.OrdinalIgnoreCase);

    private static string StripLeadingDotSlash(string value)
    {
        while (value.StartsWith("./", StringComparison.Ordinal))
        {
            value = value[2..];
        }

        return value;
    }

    private static bool IsRootedOrUncPath(string slashNormalized) =>
        slashNormalized.StartsWith('/') ||
        slashNormalized.StartsWith('~') ||
        (slashNormalized.Length >= 2 && char.IsLetter(slashNormalized[0]) && slashNormalized[1] == ':');

    private static bool IsFlatMarkdown(string name) =>
        name.Length > AgentMemoryConstants.MarkdownExtension.Length &&
        !name.Contains('/') &&
        !name.StartsWith('.') &&
        name.EndsWith(AgentMemoryConstants.MarkdownExtension, StringComparison.OrdinalIgnoreCase);
}
