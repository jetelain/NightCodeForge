using NightCodeForge.Core.Security;

namespace NightCodeForge.Core.Interfaces;

/// <summary>
/// Named constants for read-only Git repository browsing, object modes, and preview size limits.
/// </summary>
public static class GitRepositoryBrowserConstants
{
    public const string DefaultBranchName = "main";
    public const string TaskBranchPrefix = "feature/task-";
    public const int MaxPreviewFileBytes = 512 * 1024;
    public const int MaxSymlinkTargetBytes = 4 * 1024;

    public const string GitModeDirectory = "040000";
    public const string GitModeSymlink = "120000";
    public const string GitModeSubmodule = "160000";

    public const string GitObjectTypeTree = "tree";
    public const string GitObjectTypeBlob = "blob";
    public const string GitObjectTypeCommit = "commit";
}

/// <summary>
/// Kind of entry in a Git tree listing.
/// </summary>
public enum GitTreeEntryKind
{
    Directory = 0,
    File = 1,
    Symlink = 2,
    Submodule = 3
}

/// <summary>
/// Reason a requested task branch ref fell back to another ref or commit.
/// </summary>
public enum GitRefFallbackReason
{
    None = 0,
    SquashCommitSha = 1,
    TaskBaseBranch = 2,
    WorkspaceDefaultBranch = 3
}

/// <summary>
/// Result kind for inspecting a path at a Git ref.
/// </summary>
public enum GitFileContentKind
{
    Text = 0,
    Binary = 1,
    Oversized = 2,
    Directory = 3,
    Symlink = 4,
    Submodule = 5
}

/// <summary>
/// Metadata describing a task branch fallback resolution.
/// </summary>
public sealed record GitRefFallbackInfo(
    string RequestedRef,
    string ResolvedRef,
    GitRefFallbackReason Reason,
    int TaskId);

/// <summary>
/// Resolved Git ref and commit SHA, including optional task fallback details.
/// </summary>
public sealed record GitResolvedRef(
    string ResolvedRef,
    string CommitSha,
    GitRefFallbackInfo? Fallback);

/// <summary>
/// Single entry inside a Git tree directory listing.
/// </summary>
public sealed record GitTreeEntry(
    GitTreeEntryKind Kind,
    string Name,
    string Path,
    long? SizeBytes,
    string Mode,
    string ObjectSha);

/// <summary>
/// Directory listing at a resolved Git ref.
/// </summary>
public sealed record GitDirectoryListing(
    GitResolvedRef ResolvedRef,
    string DirectoryPath,
    IReadOnlyList<GitTreeEntry> Entries);

/// <summary>
/// Result of inspecting a repository path (directory, text file, binary file, oversized file, symlink, or submodule) at a Git ref.
/// </summary>
public sealed record GitPathInspectionResult(
    GitResolvedRef ResolvedRef,
    string Path,
    string Name,
    GitFileContentKind ContentKind,
    long? SizeBytes,
    string? TextContent,
    int? LineCount,
    string? SymlinkTarget,
    string? SubmoduleCommitSha,
    IReadOnlyList<GitTreeEntry>? DirectoryEntries,
    BinaryDetectionReason? BinaryReason,
    long? MaxPreviewBytes);

/// <summary>
/// Read-only service for enumerating branches, browsing directory trees, and inspecting committed file objects in a workspace's Git repository.
/// </summary>
public interface IGitRepositoryBrowserService
{
    /// <summary>
    /// Lists local branches in deterministic display order (default branch first, numeric feature/task-* branches next, remaining branches alphabetically).
    /// </summary>
    Task<IReadOnlyList<string>> ListBranchesAsync(int workspaceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates and resolves a requested ref (or workspace default branch when omitted) to a commit SHA, applying workspace-scoped task branch fallback when applicable.
    /// </summary>
    Task<GitResolvedRef> ResolveRefAsync(
        int workspaceId,
        string? requestedRef,
        int? taskIdHint = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the direct children of a directory at the resolved Git ref.
    /// </summary>
    Task<GitDirectoryListing> ListDirectoryAsync(
        int workspaceId,
        string? requestedRef,
        string? directoryPath,
        int? taskIdHint = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists root and all ancestor directory contents needed to render an expanded tree down to <paramref name="targetPath"/>.
    /// </summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<GitTreeEntry>>> ListAncestorDirectoriesAsync(
        int workspaceId,
        string resolvedRef,
        string? targetPath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Inspects a repository path at a Git ref, dispatching directories, submodules, symlinks, size guards, binary detection, and UTF-8 text decoding.
    /// </summary>
    Task<GitPathInspectionResult> InspectPathAsync(
        int workspaceId,
        string? requestedRef,
        string? path,
        int? taskIdHint = null,
        CancellationToken cancellationToken = default);
}
