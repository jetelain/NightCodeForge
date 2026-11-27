using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Security;

namespace NightCodeForge.Infrastructure.Git;

/// <summary>
/// Read-only Git repository browser backed exclusively by <see cref="IGitCliRunner"/> object queries.
/// </summary>
public sealed class GitRepositoryBrowserService : IGitRepositoryBrowserService
{
    private static readonly Encoding Utf8Strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    private readonly IWorkspaceRepository _workspaceRepository;
    private readonly ITaskRepository _taskRepository;
    private readonly IGitCliRunner _gitCliRunner;
    private readonly IWorkspacePathResolver _pathResolver;
    private readonly IWslMetadataService _wslMetadataService;
    private readonly ILogger<GitRepositoryBrowserService> _logger;

    public GitRepositoryBrowserService(
        IWorkspaceRepository workspaceRepository,
        ITaskRepository taskRepository,
        IGitCliRunner gitCliRunner,
        IWorkspacePathResolver pathResolver,
        IWslMetadataService wslMetadataService,
        ILogger<GitRepositoryBrowserService> logger)
    {
        _workspaceRepository = workspaceRepository ?? throw new ArgumentNullException(nameof(workspaceRepository));
        _taskRepository = taskRepository ?? throw new ArgumentNullException(nameof(taskRepository));
        _gitCliRunner = gitCliRunner ?? throw new ArgumentNullException(nameof(gitCliRunner));
        _pathResolver = pathResolver ?? throw new ArgumentNullException(nameof(pathResolver));
        _wslMetadataService = wslMetadataService ?? throw new ArgumentNullException(nameof(wslMetadataService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    internal Func<string, bool> DirectoryExistsCheck { get; set; } = Directory.Exists;

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListBranchesAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        var workspace = await GetWorkspaceOrThrowAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        var repoDir = ResolveRepositoryDirectory(workspace);
        var defaultBranch = ResolveDefaultBranchName(workspace);

        var result = await _gitCliRunner.ExecuteAsync(
            repoDir,
            ["for-each-ref", "--format=%(refname:short)", "refs/heads/"],
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var branches = result.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        branches.Sort((left, right) => CompareBranchNames(left, right, defaultBranch));
        return branches;
    }

    /// <inheritdoc />
    public async Task<GitResolvedRef> ResolveRefAsync(
        int workspaceId,
        string? requestedRef,
        int? taskIdHint = null,
        CancellationToken cancellationToken = default)
    {
        var workspace = await GetWorkspaceOrThrowAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        var repoDir = ResolveRepositoryDirectory(workspace);
        return await ResolveRefInternalAsync(workspace, repoDir, requestedRef, taskIdHint, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<GitDirectoryListing> ListDirectoryAsync(
        int workspaceId,
        string? requestedRef,
        string? directoryPath,
        int? taskIdHint = null,
        CancellationToken cancellationToken = default)
    {
        var workspace = await GetWorkspaceOrThrowAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        var repoDir = ResolveRepositoryDirectory(workspace);
        var resolved = await ResolveRefInternalAsync(workspace, repoDir, requestedRef, taskIdHint, cancellationToken).ConfigureAwait(false);
        var normalizedDir = GitBrowserInputValidator.NormalizeAndValidatePath(directoryPath);

        if (string.IsNullOrEmpty(normalizedDir))
        {
            var rootEntries = await ListDirectoryRawAsync(repoDir, resolved.CommitSha, string.Empty, cancellationToken).ConfigureAwait(false);
            return new GitDirectoryListing(resolved, string.Empty, rootEntries);
        }

        var targetEntry = await InspectSingleEntryAsync(repoDir, resolved.CommitSha, normalizedDir, cancellationToken).ConfigureAwait(false);
        if (targetEntry.Kind != GitTreeEntryKind.Directory)
        {
            throw new GitBrowserInvalidInputException($"Path '{normalizedDir}' is not a directory at ref '{resolved.ResolvedRef}'.");
        }

        var entries = await ListDirectoryRawAsync(repoDir, resolved.CommitSha, normalizedDir, cancellationToken).ConfigureAwait(false);
        return new GitDirectoryListing(resolved, normalizedDir, entries);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<GitTreeEntry>>> ListAncestorDirectoriesAsync(
        int workspaceId,
        string resolvedRef,
        string? targetPath,
        CancellationToken cancellationToken = default)
    {
        var workspace = await GetWorkspaceOrThrowAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        var repoDir = ResolveRepositoryDirectory(workspace);
        var validatedRef = GitBrowserInputValidator.ValidateRef(resolvedRef);
        var commitSha = await TryResolveCommitShaAsync(repoDir, validatedRef, cancellationToken).ConfigureAwait(false)
            ?? throw new GitBrowserRefNotFoundException(validatedRef);

        var normalizedPath = GitBrowserInputValidator.NormalizeAndValidatePath(targetPath);
        var rootEntries = await ListDirectoryRawAsync(repoDir, commitSha, string.Empty, cancellationToken).ConfigureAwait(false);
        var result = new Dictionary<string, IReadOnlyList<GitTreeEntry>>(StringComparer.Ordinal)
        {
            [string.Empty] = rootEntries
        };

        if (!string.IsNullOrEmpty(normalizedPath))
        {
            var currentEntries = rootEntries;
            var segments = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < segments.Length; i++)
            {
                var prefix = string.Join('/', segments[..(i + 1)]);
                var dirEntry = currentEntries.FirstOrDefault(e =>
                    e.Kind == GitTreeEntryKind.Directory &&
                    string.Equals(e.Path, prefix, StringComparison.Ordinal));
                if (dirEntry == null)
                {
                    break;
                }

                var childEntries = await ListDirectoryRawAsync(repoDir, commitSha, prefix, cancellationToken).ConfigureAwait(false);
                result[prefix] = childEntries;
                currentEntries = childEntries;
            }
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<GitPathInspectionResult> InspectPathAsync(
        int workspaceId,
        string? requestedRef,
        string? path,
        int? taskIdHint = null,
        CancellationToken cancellationToken = default)
    {
        var workspace = await GetWorkspaceOrThrowAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        var repoDir = ResolveRepositoryDirectory(workspace);
        var resolved = await ResolveRefInternalAsync(workspace, repoDir, requestedRef, taskIdHint, cancellationToken).ConfigureAwait(false);
        var normalizedPath = GitBrowserInputValidator.NormalizeAndValidatePath(path);

        if (string.IsNullOrEmpty(normalizedPath))
        {
            var rootEntries = await ListDirectoryRawAsync(repoDir, resolved.CommitSha, string.Empty, cancellationToken).ConfigureAwait(false);
            return new GitPathInspectionResult(
                ResolvedRef: resolved,
                Path: string.Empty,
                Name: string.Empty,
                ContentKind: GitFileContentKind.Directory,
                SizeBytes: null,
                TextContent: null,
                LineCount: null,
                SymlinkTarget: null,
                SubmoduleCommitSha: null,
                DirectoryEntries: rootEntries,
                BinaryReason: null,
                MaxPreviewBytes: null);
        }

        var entry = await InspectSingleEntryAsync(repoDir, resolved.CommitSha, normalizedPath, cancellationToken).ConfigureAwait(false);

        switch (entry.Kind)
        {
            case GitTreeEntryKind.Directory:
            {
                var childEntries = await ListDirectoryRawAsync(repoDir, resolved.CommitSha, normalizedPath, cancellationToken).ConfigureAwait(false);
                return new GitPathInspectionResult(
                    ResolvedRef: resolved,
                    Path: entry.Path,
                    Name: entry.Name,
                    ContentKind: GitFileContentKind.Directory,
                    SizeBytes: null,
                    TextContent: null,
                    LineCount: null,
                    SymlinkTarget: null,
                    SubmoduleCommitSha: null,
                    DirectoryEntries: childEntries,
                    BinaryReason: null,
                    MaxPreviewBytes: null);
            }

            case GitTreeEntryKind.Submodule:
                return new GitPathInspectionResult(
                    ResolvedRef: resolved,
                    Path: entry.Path,
                    Name: entry.Name,
                    ContentKind: GitFileContentKind.Submodule,
                    SizeBytes: null,
                    TextContent: null,
                    LineCount: null,
                    SymlinkTarget: null,
                    SubmoduleCommitSha: entry.ObjectSha,
                    DirectoryEntries: null,
                    BinaryReason: null,
                    MaxPreviewBytes: null);

            case GitTreeEntryKind.Symlink:
            {
                var linkSize = entry.SizeBytes ?? await GetBlobSizeAsync(repoDir, entry.ObjectSha, cancellationToken).ConfigureAwait(false);
                if (linkSize > GitRepositoryBrowserConstants.MaxSymlinkTargetBytes)
                {
                    return new GitPathInspectionResult(
                        ResolvedRef: resolved,
                        Path: entry.Path,
                        Name: entry.Name,
                        ContentKind: GitFileContentKind.Oversized,
                        SizeBytes: linkSize,
                        TextContent: null,
                        LineCount: null,
                        SymlinkTarget: null,
                        SubmoduleCommitSha: null,
                        DirectoryEntries: null,
                        BinaryReason: null,
                        MaxPreviewBytes: GitRepositoryBrowserConstants.MaxSymlinkTargetBytes);
                }

                var rawLink = await _gitCliRunner.ExecuteRawAsync(
                    repoDir,
                    ["show", "--no-textconv", $"{resolved.CommitSha}:{normalizedPath}"],
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                var targetText = Utf8Strict.GetString(rawLink.StandardOutputBytes).TrimEnd('\r', '\n');
                return new GitPathInspectionResult(
                    ResolvedRef: resolved,
                    Path: entry.Path,
                    Name: entry.Name,
                    ContentKind: GitFileContentKind.Symlink,
                    SizeBytes: rawLink.StandardOutputBytes.Length,
                    TextContent: null,
                    LineCount: null,
                    SymlinkTarget: targetText,
                    SubmoduleCommitSha: null,
                    DirectoryEntries: null,
                    BinaryReason: null,
                    MaxPreviewBytes: null);
            }

            case GitTreeEntryKind.File:
            default:
            {
                var blobSize = entry.SizeBytes ?? await GetBlobSizeAsync(repoDir, entry.ObjectSha, cancellationToken).ConfigureAwait(false);
                if (blobSize > GitRepositoryBrowserConstants.MaxPreviewFileBytes)
                {
                    return new GitPathInspectionResult(
                        ResolvedRef: resolved,
                        Path: entry.Path,
                        Name: entry.Name,
                        ContentKind: GitFileContentKind.Oversized,
                        SizeBytes: blobSize,
                        TextContent: null,
                        LineCount: null,
                        SymlinkTarget: null,
                        SubmoduleCommitSha: null,
                        DirectoryEntries: null,
                        BinaryReason: null,
                        MaxPreviewBytes: GitRepositoryBrowserConstants.MaxPreviewFileBytes);
                }

                var rawBlob = await _gitCliRunner.ExecuteRawAsync(
                    repoDir,
                    ["show", "--no-textconv", $"{resolved.CommitSha}:{normalizedPath}"],
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                if (!BinaryContentDetector.TryDecodeText(rawBlob.StandardOutputBytes, out var decodedText, out var inspection))
                {
                    return new GitPathInspectionResult(
                        ResolvedRef: resolved,
                        Path: entry.Path,
                        Name: entry.Name,
                        ContentKind: GitFileContentKind.Binary,
                        SizeBytes: rawBlob.StandardOutputBytes.Length,
                        TextContent: null,
                        LineCount: null,
                        SymlinkTarget: null,
                        SubmoduleCommitSha: null,
                        DirectoryEntries: null,
                        BinaryReason: inspection.Reason,
                        MaxPreviewBytes: null);
                }

                return new GitPathInspectionResult(
                    ResolvedRef: resolved,
                    Path: entry.Path,
                    Name: entry.Name,
                    ContentKind: GitFileContentKind.Text,
                    SizeBytes: rawBlob.StandardOutputBytes.Length,
                    TextContent: decodedText,
                    LineCount: CountLines(decodedText),
                    SymlinkTarget: null,
                    SubmoduleCommitSha: null,
                    DirectoryEntries: null,
                    BinaryReason: null,
                    MaxPreviewBytes: null);
            }
        }
    }

    private async Task<Workspace> GetWorkspaceOrThrowAsync(int workspaceId, CancellationToken cancellationToken)
    {
        var workspace = await _workspaceRepository.GetByIdAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        return workspace ?? throw new GitBrowserWorkspaceNotFoundException(workspaceId);
    }

    private string ResolveRepositoryDirectory(Workspace workspace)
    {
        if (!string.IsNullOrWhiteSpace(workspace.Ext4MirrorPath) && IsDirectoryAccessible(workspace.Ext4MirrorPath))
        {
            return workspace.Ext4MirrorPath;
        }

        if (workspace.ExecutionMode != ExecutionMode.BareHost)
        {
            var workingClone = _pathResolver.GetAgentWorkingClonePath(workspace.Id);
            if (!string.IsNullOrWhiteSpace(workingClone) && IsDirectoryAccessible(workingClone))
            {
                return workingClone;
            }
        }

        if (!string.IsNullOrWhiteSpace(workspace.RepositoryPath) && IsDirectoryAccessible(workspace.RepositoryPath))
        {
            return workspace.RepositoryPath;
        }

        throw new GitBrowserRepositoryNotFoundException(workspace.Id);
    }

    private bool IsDirectoryAccessible(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (DirectoryExistsCheck(path))
        {
            return true;
        }

        var hostPath = _pathResolver.ToHostPath(path);
        if (!string.Equals(hostPath, path, StringComparison.Ordinal) && DirectoryExistsCheck(hostPath))
        {
            return true;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
            (path.StartsWith('/') || path.StartsWith('~') || WslPathConverter.IsWslUncPath(path)))
        {
            var distro = _wslMetadataService.GetDefaultDistributionName();
            var user = _wslMetadataService.GetDefaultUserName();
            if (!string.IsNullOrWhiteSpace(distro) && !string.IsNullOrWhiteSpace(user))
            {
                var uncPath = WslPathConverter.ToWslUncPath(path, distro, user);
                return DirectoryExistsCheck(uncPath);
            }
        }

        return false;
    }

    private static string ResolveDefaultBranchName(Workspace workspace) =>
        string.IsNullOrWhiteSpace(workspace.DefaultBaseBranch)
            ? GitRepositoryBrowserConstants.DefaultBranchName
            : workspace.DefaultBaseBranch.Trim();

    private async Task<GitResolvedRef> ResolveRefInternalAsync(
        Workspace workspace,
        string repoDir,
        string? requestedRef,
        int? taskIdHint,
        CancellationToken cancellationToken)
    {
        var defaultBranch = ResolveDefaultBranchName(workspace);
        var effectiveRequestedRef = string.IsNullOrWhiteSpace(requestedRef) ? defaultBranch : requestedRef.Trim();
        var validatedRef = GitBrowserInputValidator.ValidateRef(effectiveRequestedRef);

        var directSha = await TryResolveCommitShaAsync(repoDir, validatedRef, cancellationToken).ConfigureAwait(false);
        if (directSha != null)
        {
            return new GitResolvedRef(validatedRef, directSha, null);
        }

        var isTaskBranch = GitBrowserInputValidator.TryExtractTaskBranchId(validatedRef, out var parsedTaskId);
        int? fallbackTaskId = isTaskBranch ? parsedTaskId : (taskIdHint is > 0 ? taskIdHint.Value : null);

        if (fallbackTaskId.HasValue)
        {
            var task = await _taskRepository.GetByIdAsync(fallbackTaskId.Value, cancellationToken).ConfigureAwait(false);
            if (task != null && task.WorkspaceId == workspace.Id)
            {
                var fallback = await TryResolveTaskFallbackAsync(repoDir, validatedRef, task, defaultBranch, cancellationToken).ConfigureAwait(false);
                if (fallback != null)
                {
                    _logger.LogInformation(
                        "Resolved missing task ref '{RequestedRef}' in workspace #{WorkspaceId} via {FallbackReason} -> '{ResolvedRef}' (Task #{TaskId})",
                        validatedRef, workspace.Id, fallback.Fallback!.Reason, fallback.ResolvedRef, task.Id);
                    return fallback;
                }
            }
        }

        throw new GitBrowserRefNotFoundException(
            validatedRef,
            $"Git ref '{validatedRef}' could not be resolved in workspace #{workspace.Id}.");
    }

    private async Task<GitResolvedRef?> TryResolveTaskFallbackAsync(
        string repoDir,
        string requestedRef,
        ForgeTask task,
        string workspaceDefaultBranch,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(task.SquashCommitSha))
        {
            var candidateSha = task.SquashCommitSha.Trim();
            if (TryValidateRefSilently(candidateSha, out var validatedSha))
            {
                var resolvedSha = await TryResolveCommitShaAsync(repoDir, validatedSha, cancellationToken).ConfigureAwait(false);
                if (resolvedSha != null)
                {
                    return new GitResolvedRef(
                        resolvedSha,
                        resolvedSha,
                        new GitRefFallbackInfo(requestedRef, resolvedSha, GitRefFallbackReason.SquashCommitSha, task.Id));
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(task.BaseBranch))
        {
            var candidateBase = task.BaseBranch.Trim();
            if (TryValidateRefSilently(candidateBase, out var validatedBase))
            {
                var resolvedBaseSha = await TryResolveCommitShaAsync(repoDir, validatedBase, cancellationToken).ConfigureAwait(false);
                if (resolvedBaseSha != null)
                {
                    return new GitResolvedRef(
                        validatedBase,
                        resolvedBaseSha,
                        new GitRefFallbackInfo(requestedRef, validatedBase, GitRefFallbackReason.TaskBaseBranch, task.Id));
                }
            }
        }

        if (TryValidateRefSilently(workspaceDefaultBranch, out var validatedDefault))
        {
            var resolvedDefaultSha = await TryResolveCommitShaAsync(repoDir, validatedDefault, cancellationToken).ConfigureAwait(false);
            if (resolvedDefaultSha != null)
            {
                return new GitResolvedRef(
                    validatedDefault,
                    resolvedDefaultSha,
                    new GitRefFallbackInfo(requestedRef, validatedDefault, GitRefFallbackReason.WorkspaceDefaultBranch, task.Id));
            }
        }

        return null;
    }

    private static bool TryValidateRefSilently(string candidate, out string validated)
    {
        try
        {
            validated = GitBrowserInputValidator.ValidateRef(candidate);
            return true;
        }
        catch (GitBrowserInvalidInputException)
        {
            validated = string.Empty;
            return false;
        }
    }

    private async Task<string?> TryResolveCommitShaAsync(string repoDir, string validatedRef, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _gitCliRunner.ExecuteAsync(
                repoDir,
                ["rev-parse", "--verify", "--quiet", $"{validatedRef}^{{commit}}"],
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var sha = result.StandardOutput.Trim();
            return string.IsNullOrEmpty(sha) ? null : sha;
        }
        catch (GitCommandException ex)
        {
            _logger.LogDebug(ex, "Git rev-parse failed for ref '{Ref}' in '{RepoDir}'", validatedRef, repoDir);
            return null;
        }
    }

    private async Task<GitTreeEntry> InspectSingleEntryAsync(
        string repoDir,
        string commitSha,
        string normalizedPath,
        CancellationToken cancellationToken)
    {
        var entries = await QueryLsTreeAsync(repoDir, commitSha, normalizedPath, cancellationToken).ConfigureAwait(false);
        var match = entries.FirstOrDefault(e => string.Equals(e.Path, normalizedPath, StringComparison.Ordinal));

        return match ?? throw new GitBrowserPathNotFoundException(normalizedPath);
    }

    private async Task<IReadOnlyList<GitTreeEntry>> ListDirectoryRawAsync(
        string repoDir,
        string commitSha,
        string normalizedDirectoryPath,
        CancellationToken cancellationToken)
    {
        var pathArg = string.IsNullOrEmpty(normalizedDirectoryPath)
            ? null
            : normalizedDirectoryPath + "/";

        var entries = await QueryLsTreeAsync(repoDir, commitSha, pathArg, cancellationToken).ConfigureAwait(false);
        return entries
            .OrderBy(e => e.Kind == GitTreeEntryKind.Directory ? 0 : 1)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Name, StringComparer.Ordinal)
            .ToList();
    }

    private async Task<IReadOnlyList<GitTreeEntry>> QueryLsTreeAsync(
        string repoDir,
        string commitSha,
        string? pathSpec,
        CancellationToken cancellationToken)
    {
        var args = string.IsNullOrEmpty(pathSpec)
            ? new[] { "ls-tree", "-l", "-z", commitSha }
            : new[] { "--literal-pathspecs", "ls-tree", "-l", "-z", commitSha, "--", pathSpec };

        var rawResult = await _gitCliRunner.ExecuteRawAsync(repoDir, args, cancellationToken: cancellationToken).ConfigureAwait(false);
        return ParseLsTreeOutput(rawResult.StandardOutputBytes);
    }

    private async Task<long> GetBlobSizeAsync(string repoDir, string objectSha, CancellationToken cancellationToken)
    {
        var sizeResult = await _gitCliRunner.ExecuteAsync(
            repoDir,
            ["cat-file", "-s", objectSha],
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!long.TryParse(sizeResult.StandardOutput.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var size))
        {
            throw new GitRepositoryBrowserException($"Unexpected output from git cat-file -s for object '{objectSha}': '{sizeResult.StandardOutput.Trim()}'.");
        }

        return size;
    }

    internal static IReadOnlyList<GitTreeEntry> ParseLsTreeOutput(byte[] rawBytes)
    {
        if (rawBytes.Length == 0)
        {
            return [];
        }

        var entries = new List<GitTreeEntry>();
        var start = 0;

        for (var i = 0; i <= rawBytes.Length; i++)
        {
            if (i < rawBytes.Length && rawBytes[i] != 0)
            {
                continue;
            }

            if (i > start)
            {
                var record = Utf8Strict.GetString(rawBytes, start, i - start);
                if (TryParseLsTreeRecord(record, out var entry))
                {
                    entries.Add(entry);
                }
            }

            start = i + 1;
        }

        return entries;
    }

    private static bool TryParseLsTreeRecord(string record, out GitTreeEntry entry)
    {
        entry = default!;
        var tabIndex = record.IndexOf('\t');
        if (tabIndex <= 0 || tabIndex >= record.Length - 1)
        {
            return false;
        }

        var header = record[..tabIndex];
        var rawPath = record[(tabIndex + 1)..].Replace('\\', '/').Trim('/');
        if (string.IsNullOrEmpty(rawPath))
        {
            return false;
        }

        var parts = header.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4)
        {
            return false;
        }

        var mode = parts[0];
        var objectType = parts[1];
        var objectSha = parts[2];
        var sizeToken = parts[3];

        var lastSlash = rawPath.LastIndexOf('/');
        var name = lastSlash >= 0 ? rawPath[(lastSlash + 1)..] : rawPath;

        long? sizeBytes = long.TryParse(sizeToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedSize)
            ? parsedSize
            : null;

        GitTreeEntryKind kind;
        if (string.Equals(mode, GitRepositoryBrowserConstants.GitModeSubmodule, StringComparison.Ordinal) ||
            string.Equals(objectType, GitRepositoryBrowserConstants.GitObjectTypeCommit, StringComparison.Ordinal))
        {
            kind = GitTreeEntryKind.Submodule;
            sizeBytes = null;
        }
        else if (string.Equals(mode, GitRepositoryBrowserConstants.GitModeDirectory, StringComparison.Ordinal) ||
                 string.Equals(objectType, GitRepositoryBrowserConstants.GitObjectTypeTree, StringComparison.Ordinal))
        {
            kind = GitTreeEntryKind.Directory;
            sizeBytes = null;
        }
        else if (string.Equals(mode, GitRepositoryBrowserConstants.GitModeSymlink, StringComparison.Ordinal))
        {
            kind = GitTreeEntryKind.Symlink;
        }
        else
        {
            kind = GitTreeEntryKind.File;
        }

        entry = new GitTreeEntry(kind, name, rawPath, sizeBytes, mode, objectSha);
        return true;
    }

    private static int CompareBranchNames(string left, string right, string defaultBranch)
    {
        var leftIsDefault = string.Equals(left, defaultBranch, StringComparison.Ordinal);
        var rightIsDefault = string.Equals(right, defaultBranch, StringComparison.Ordinal);
        if (leftIsDefault != rightIsDefault)
        {
            return leftIsDefault ? -1 : 1;
        }

        var leftIsTask = GitBrowserInputValidator.TryExtractTaskBranchSortKey(left, out var leftTaskId, out var leftRem);
        var rightIsTask = GitBrowserInputValidator.TryExtractTaskBranchSortKey(right, out var rightTaskId, out var rightRem);

        if (leftIsTask != rightIsTask)
        {
            return leftIsTask ? -1 : 1;
        }

        if (leftIsTask)
        {
            var idCmp = leftTaskId.CompareTo(rightTaskId);
            if (idCmp != 0)
            {
                return idCmp;
            }

            var remCmp = StringComparer.OrdinalIgnoreCase.Compare(leftRem, rightRem);
            if (remCmp != 0)
            {
                return remCmp;
            }
        }

        var ciCmp = StringComparer.OrdinalIgnoreCase.Compare(left, right);
        return ciCmp != 0 ? ciCmp : StringComparer.Ordinal.Compare(left, right);
    }

    private static int CountLines(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var normalized = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        if (normalized.EndsWith('\n'))
        {
            normalized = normalized[..^1];
        }

        if (normalized.Length == 0)
        {
            return 1;
        }

        var count = 1;
        foreach (var ch in normalized)
        {
            if (ch == '\n')
            {
                count++;
            }
        }

        return count;
    }
}
