using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Core.Services;

/// <summary>
/// Sanitizes Git worktree administrative metadata (<c>.git/worktrees/*</c>) on the host/WSL repository
/// to recover from container-virtual paths (such as <c>/workspace/...</c>) written by sandboxed agents,
/// normalizes <c>commondir</c>, and backfills <c>locked</c> guards against accidental <c>git worktree prune</c>.
/// </summary>
public static partial class GitWorktreeMetadataSanitizer
{
    public const string ManagedWorktreeLockReason = "Managed by NightCodeForge";
    public const string ExpectedCommonDirContent = "../..";
    public const string GitDirFileName = "gitdir";
    public const string CommonDirFileName = "commondir";
    public const string LockedFileName = "locked";
    public const string WorktreePruneExpireConfigKey = "gc.worktreePruneExpire";
    public const string WorktreePruneExpireNeverValue = "never";
    public const string GitDirectoryName = ".git";
    public const string WorktreesDirectoryName = "worktrees";

    private const string GitDirPrefix = "gitdir:";
    private const string TmpRootPath = "/tmp";
    private const string TmpPrefix = "/tmp/";

    private static readonly string[] ContainerVirtualPrefixes = ["/workspace", "/clone", "/nightcodeforge"];
    private static readonly string[] SiblingCanonicalDirNames = ["clone", "repo", "mirror"];

    private enum WorktreeLayoutKind
    {
        Sibling,
        Child,
        CustomRoot
    }

    [GeneratedRegex(@"^task-(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex TaskWorktreeNameRegex();

    [GeneratedRegex(@"(?:^|[/\\])(task-\d+)[/\\]\.git$", RegexOptions.IgnoreCase)]
    private static partial Regex TaskGitDirTargetRegex();

    [GeneratedRegex(@"^[/\\]{2}wsl(?:\.localhost|\$)?[/\\]([^/\\]+)(?:[/\\](.*))?$", RegexOptions.IgnoreCase)]
    private static partial Regex WslUncPathRegex();

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="dirName"/> is one of the canonical sibling repository
    /// directory names (<c>clone</c>, <c>repo</c>, or <c>mirror</c>) whose task worktrees sit beside it.
    /// </summary>
    public static bool IsSiblingCanonicalDirName(string? dirName) =>
        !string.IsNullOrWhiteSpace(dirName) &&
        SiblingCanonicalDirNames.Contains(dirName, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="path"/> points inside a container-only virtual mount
    /// (<c>/workspace</c>, <c>/clone</c>, or <c>/nightcodeforge</c>) that does not exist on the host or WSL filesystem.
    /// </summary>
    public static bool IsContainerVirtualPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var normalized = path.Trim().Replace('\\', '/');
        foreach (var prefix in ContainerVirtualPrefixes)
        {
            if (normalized.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Ensures <c>commondir</c> equals <see cref="ExpectedCommonDirContent"/> and creates the <c>locked</c>
    /// guard file if absent inside <paramref name="worktreeAdminDir"/>.
    /// </summary>
    public static void EnsureWorktreeCommonDirAndLock(string worktreeAdminDir, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        if (string.IsNullOrWhiteSpace(worktreeAdminDir) || !Directory.Exists(worktreeAdminDir))
        {
            return;
        }

        var commonDirPath = Path.Combine(worktreeAdminDir, CommonDirFileName);
        var currentCommonDir = TryReadTrimmedText(commonDirPath);
        if (!string.Equals(currentCommonDir, ExpectedCommonDirContent, StringComparison.Ordinal))
        {
            WriteNormalizedFile(commonDirPath, $"{ExpectedCommonDirContent}\n", logger);
            logger.LogWarning(
                "Repaired worktree commondir in '{WorktreeAdminDir}' from '{PreviousValue}' to '{ExpectedValue}'.",
                worktreeAdminDir,
                currentCommonDir ?? "<missing>",
                ExpectedCommonDirContent);
        }

        EnsureLockFileExists(worktreeAdminDir, logger);
    }

    /// <summary>
    /// Removes <c>locked</c> guard files from any <c>.git/worktrees/*</c> administrative directory associated
    /// with <paramref name="taskId"/> (exact <c>task-{taskId}</c> or Git-suffixed directories pointing to it).
    /// </summary>
    /// <returns>The number of lock files removed.</returns>
    public static int UnlockWorktreesForTask(
        string canonicalRepoPath,
        int taskId,
        IWorkspacePathResolver pathResolver,
        ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalRepoPath);
        ArgumentNullException.ThrowIfNull(pathResolver);
        ArgumentNullException.ThrowIfNull(logger);

        var (hostCanonicalPath, _) = ResolvePaths(canonicalRepoPath, pathResolver, logger);
        if (string.IsNullOrWhiteSpace(hostCanonicalPath) || !Directory.Exists(hostCanonicalPath))
        {
            return 0;
        }

        var worktreesDir = Path.Combine(hostCanonicalPath, GitDirectoryName, WorktreesDirectoryName);
        if (!Directory.Exists(worktreesDir))
        {
            return 0;
        }

        var taskDirName = $"task-{taskId}";
        var unlockedCount = 0;

        foreach (var subDir in Directory.EnumerateDirectories(worktreesDir))
        {
            var entryName = Path.GetFileName(subDir);
            var isMatch = string.Equals(entryName, taskDirName, StringComparison.OrdinalIgnoreCase);

            if (!isMatch)
            {
                var currentGitDir = TryReadTrimmedText(Path.Combine(subDir, GitDirFileName));
                if (!string.IsNullOrEmpty(currentGitDir))
                {
                    var normalizedGitDir = currentGitDir.Replace('\\', '/').TrimEnd('/');
                    isMatch = normalizedGitDir.EndsWith($"/{taskDirName}/{GitDirectoryName}", StringComparison.OrdinalIgnoreCase) ||
                              normalizedGitDir.Equals($"{taskDirName}/{GitDirectoryName}", StringComparison.OrdinalIgnoreCase);
                }
            }

            if (!isMatch)
            {
                continue;
            }

            var lockFilePath = Path.Combine(subDir, LockedFileName);
            if (!File.Exists(lockFilePath))
            {
                continue;
            }

            try
            {
                File.SetAttributes(lockFilePath, FileAttributes.Normal);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Failed to clear attributes on worktree lock file '{LockFilePath}'", lockFilePath);
            }

            File.Delete(lockFilePath);
            unlockedCount++;
            logger.LogDebug("Removed worktree lock file '{LockFilePath}' for task #{TaskId}.", lockFilePath, taskId);
        }

        return unlockedCount;
    }

    /// <summary>
    /// Scans <c>&lt;canonicalRepoPath&gt;/.git/worktrees</c> and repairs any container-virtual <c>gitdir</c>,
    /// <c>commondir</c>, or forward <c>.git</c> pointers, removes orphaned or stray container-path entries,
    /// and backfills <c>locked</c> files on active managed task worktrees.
    /// </summary>
    /// <returns>The number of worktree metadata artifacts repaired or removed.</returns>
    public static int Sanitize(
        string canonicalRepoPath,
        IWorkspacePathResolver pathResolver,
        ILogger logger,
        bool? supportsRelativeWorktrees = null,
        string? worktreesRootPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalRepoPath);
        ArgumentNullException.ThrowIfNull(pathResolver);
        ArgumentNullException.ThrowIfNull(logger);

        var (hostCanonicalPath, executionCanonicalPath) = ResolvePaths(canonicalRepoPath, pathResolver, logger);
        if (string.IsNullOrWhiteSpace(hostCanonicalPath) || !Directory.Exists(hostCanonicalPath))
        {
            return 0;
        }

        var worktreesDir = Path.Combine(hostCanonicalPath, GitDirectoryName, WorktreesDirectoryName);
        if (!Directory.Exists(worktreesDir))
        {
            return 0;
        }

        string? hostWorktreesRoot = null;
        string? normExecWorktreesRoot = null;
        if (!string.IsNullOrWhiteSpace(worktreesRootPath))
        {
            var (resolvedHostRoot, resolvedExecRoot) = ResolvePaths(worktreesRootPath, pathResolver, logger);
            if (!string.IsNullOrWhiteSpace(resolvedHostRoot))
            {
                hostWorktreesRoot = resolvedHostRoot.TrimEnd('/', '\\');
                normExecWorktreesRoot = resolvedExecRoot.Replace('\\', '/').TrimEnd('/');
            }
        }

        var normExecCanonical = executionCanonicalPath.Replace('\\', '/').TrimEnd('/');
        var normHostCanonical = hostCanonicalPath.TrimEnd('/', '\\');
        var cloneDirName = Path.GetFileName(normExecCanonical);
        var execParent = GetPosixParent(normExecCanonical);
        var hostParent = Path.GetDirectoryName(normHostCanonical);
        var isSiblingLayout = !string.IsNullOrEmpty(execParent) &&
                              !string.IsNullOrEmpty(hostParent) &&
                              IsSiblingCanonicalDirName(cloneDirName);

        var repairedCount = 0;

        foreach (var entryDir in Directory.EnumerateDirectories(worktreesDir))
        {
            try
            {
                repairedCount += SanitizeWorktreeEntry(
                    worktreesDir,
                    entryDir,
                    cloneDirName,
                    execParent,
                    hostParent,
                    normExecCanonical,
                    normHostCanonical,
                    isSiblingLayout,
                    hostWorktreesRoot,
                    normExecWorktreesRoot,
                    supportsRelativeWorktrees,
                    pathResolver,
                    logger);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Failed to sanitize worktree metadata entry '{EntryDir}'", entryDir);
            }
        }

        return repairedCount;
    }

    private static int SanitizeWorktreeEntry(
        string worktreesDir,
        string entryDir,
        string cloneDirName,
        string? execParent,
        string? hostParent,
        string normExecCanonical,
        string normHostCanonical,
        bool isSiblingLayout,
        string? hostWorktreesRoot,
        string? normExecWorktreesRoot,
        bool? supportsRelativeWorktrees,
        IWorkspacePathResolver pathResolver,
        ILogger logger)
    {
        var entryName = Path.GetFileName(entryDir);
        var gitDirFilePath = Path.Combine(entryDir, GitDirFileName);
        var commonDirFilePath = Path.Combine(entryDir, CommonDirFileName);
        var currentGitDir = TryReadTrimmedText(gitDirFilePath);
        var currentCommonDir = TryReadTrimmedText(commonDirFilePath);

        if (TaskWorktreeNameRegex().IsMatch(entryName))
        {
            var (worktreeDirName, hostWorktreeDir, execWorktreeDir, layoutKind) = ResolveTaskWorktreeLocation(
                worktreesDir,
                entryName,
                currentGitDir,
                execParent,
                hostParent,
                normExecCanonical,
                normHostCanonical,
                isSiblingLayout,
                hostWorktreesRoot,
                normExecWorktreesRoot);

            // Git always expects forward slashes in .git/worktrees/<name>/gitdir, even on Windows.
            var expectedAbsoluteReverse = $"{execWorktreeDir.Replace('\\', '/').TrimEnd('/')}/{GitDirectoryName}\n";
            var expectedRelativeReverse = layoutKind switch
            {
                WorktreeLayoutKind.Sibling => $"../../../../{worktreeDirName}/{GitDirectoryName}\n",
                WorktreeLayoutKind.Child => $"../../../{worktreeDirName}/{GitDirectoryName}\n",
                WorktreeLayoutKind.CustomRoot => expectedAbsoluteReverse,
                _ => expectedAbsoluteReverse
            };
            var expectedRelativeForward = layoutKind switch
            {
                WorktreeLayoutKind.Sibling => $"gitdir: ../{cloneDirName}/{GitDirectoryName}/{WorktreesDirectoryName}/{entryName}\n",
                WorktreeLayoutKind.Child => $"gitdir: ../{GitDirectoryName}/{WorktreesDirectoryName}/{entryName}\n",
                WorktreeLayoutKind.CustomRoot => $"gitdir: {normExecCanonical}/{GitDirectoryName}/{WorktreesDirectoryName}/{entryName}\n",
                _ => $"gitdir: ../{GitDirectoryName}/{WorktreesDirectoryName}/{entryName}\n"
            };

            if (Directory.Exists(hostWorktreeDir))
            {
                var repairs = 0;

                if (!string.Equals(currentCommonDir, ExpectedCommonDirContent, StringComparison.Ordinal))
                {
                    WriteNormalizedFile(commonDirFilePath, $"{ExpectedCommonDirContent}\n", logger);
                    repairs++;
                    logger.LogWarning(
                        "Repaired worktree commondir in '{EntryDir}' from '{PreviousValue}' to '{ExpectedValue}'.",
                        entryDir,
                        currentCommonDir ?? "<missing>",
                        ExpectedCommonDirContent);
                }

                var (shouldRepairGitDir, targetGitDirContent) = EvaluateReverseGitDir(
                    currentGitDir,
                    expectedAbsoluteReverse,
                    expectedRelativeReverse,
                    supportsRelativeWorktrees);

                if (shouldRepairGitDir)
                {
                    WriteNormalizedFile(gitDirFilePath, targetGitDirContent, logger);
                    repairs++;
                    logger.LogWarning(
                        "Repaired worktree gitdir in '{EntryDir}' from '{PreviousValue}' to '{ExpectedValue}'.",
                        entryDir,
                        currentGitDir ?? "<missing>",
                        targetGitDirContent.Trim());
                }

                EnsureLockFileExists(entryDir, logger);

                var forwardGitFile = Path.Combine(hostWorktreeDir, GitDirectoryName);
                if (File.Exists(forwardGitFile))
                {
                    var currentForwardTarget = ExtractGitDirPointerTarget(TryReadTrimmedText(forwardGitFile));
                    if (IsContainerVirtualPath(currentForwardTarget))
                    {
                        WriteNormalizedFile(forwardGitFile, expectedRelativeForward, logger);
                        repairs++;
                        logger.LogWarning(
                            "Repaired forward worktree .git pointer in '{ForwardGitFile}' from '{PreviousValue}' to '{ExpectedValue}'.",
                            forwardGitFile,
                            currentForwardTarget,
                            expectedRelativeForward.Trim());
                    }
                }

                return repairs;
            }

            if (IsContainerVirtualPath(currentGitDir) || IsContainerVirtualPath(currentCommonDir))
            {
                DeleteDirectorySafe(entryDir, logger);
                logger.LogWarning(
                    "Removed orphaned task worktree metadata '{EntryDir}' with container-virtual pointer (gitdir: '{GitDir}', commondir: '{CommonDir}').",
                    entryDir,
                    currentGitDir ?? "<missing>",
                    currentCommonDir ?? "<missing>");
                return 1;
            }

            return 0;
        }

        if (IsContainerVirtualPath(currentGitDir) ||
            IsContainerVirtualPath(currentCommonDir) ||
            (IsTmpPath(currentGitDir) && !TargetExistsOnHost(currentGitDir!, pathResolver, logger)))
        {
            DeleteDirectorySafe(entryDir, logger);
            logger.LogWarning(
                "Removed stray worktree metadata '{EntryDir}' (gitdir: '{GitDir}', commondir: '{CommonDir}').",
                entryDir,
                currentGitDir ?? "<missing>",
                currentCommonDir ?? "<missing>");
            return 1;
        }

        return 0;
    }

    private static (string WorktreeDirName, string HostWorktreeDir, string ExecWorktreeDir, WorktreeLayoutKind LayoutKind)
        ResolveTaskWorktreeLocation(
            string worktreesDir,
            string entryName,
            string? currentGitDir,
            string? execParent,
            string? hostParent,
            string normExecCanonical,
            string normHostCanonical,
            bool isSiblingLayout,
            string? hostWorktreesRoot,
            string? normExecWorktreesRoot)
    {
        (string HostDir, string ExecDir, WorktreeLayoutKind Layout) GetPrimaryLocation(string dirName)
        {
            if (!string.IsNullOrEmpty(hostWorktreesRoot) && !string.IsNullOrEmpty(normExecWorktreesRoot))
            {
                return (Path.Combine(hostWorktreesRoot, dirName), $"{normExecWorktreesRoot}/{dirName}", WorktreeLayoutKind.CustomRoot);
            }

            if (isSiblingLayout)
            {
                return (Path.Combine(hostParent!, dirName), $"{execParent}/{dirName}", WorktreeLayoutKind.Sibling);
            }

            return (Path.Combine(normHostCanonical, dirName), $"{normExecCanonical}/{dirName}", WorktreeLayoutKind.Child);
        }

        var primary = GetPrimaryLocation(entryName);
        if (Directory.Exists(primary.HostDir))
        {
            return (entryName, primary.HostDir, primary.ExecDir, primary.Layout);
        }

        // Handle Git-suffixed admin dirs (e.g., ".git/worktrees/task-1711" pointing to "task-171/.git")
        var gitDirTaskMatch = !string.IsNullOrWhiteSpace(currentGitDir)
            ? TaskGitDirTargetRegex().Match(currentGitDir.Trim())
            : Match.Empty;
        var gitDirTaskName = gitDirTaskMatch.Success ? gitDirTaskMatch.Groups[1].Value : null;

        if (!string.IsNullOrEmpty(gitDirTaskName) &&
            !string.Equals(gitDirTaskName, entryName, StringComparison.OrdinalIgnoreCase))
        {
            var alt = GetPrimaryLocation(gitDirTaskName);
            if (Directory.Exists(alt.HostDir) && IsWorktreeOwnedByAdminEntry(worktreesDir, entryName, gitDirTaskName, alt.HostDir))
            {
                return (gitDirTaskName, alt.HostDir, alt.ExecDir, alt.Layout);
            }
        }

        // Non-sibling fallback: if canonical repo has a custom folder name and no explicit worktreesRootPath was supplied,
        // check whether a sibling directory exists whose .git forward pointer targets this admin entry.
        if (hostWorktreesRoot == null &&
            !isSiblingLayout &&
            !string.IsNullOrEmpty(hostParent) &&
            !string.IsNullOrEmpty(execParent))
        {
            foreach (var candidateDirName in GetDistinctCandidates(entryName, gitDirTaskName))
            {
                var siblingHostDir = Path.Combine(hostParent, candidateDirName);
                if (Directory.Exists(siblingHostDir) && ForwardPointerTargetsEntry(siblingHostDir, entryName))
                {
                    return (candidateDirName, siblingHostDir, $"{execParent}/{candidateDirName}", WorktreeLayoutKind.Sibling);
                }
            }
        }

        return (entryName, primary.HostDir, primary.ExecDir, primary.Layout);
    }

    private static IEnumerable<string> GetDistinctCandidates(string primaryName, string? secondaryName)
    {
        yield return primaryName;
        if (!string.IsNullOrEmpty(secondaryName) &&
            !string.Equals(secondaryName, primaryName, StringComparison.OrdinalIgnoreCase))
        {
            yield return secondaryName;
        }
    }

    private static bool IsWorktreeOwnedByAdminEntry(
        string worktreesDir,
        string entryName,
        string targetTaskName,
        string hostWorktreeDir)
    {
        if (!Directory.Exists(Path.Combine(worktreesDir, targetTaskName)))
        {
            return true;
        }

        return ForwardPointerTargetsEntry(hostWorktreeDir, entryName);
    }

    private static bool ForwardPointerTargetsEntry(string hostWorktreeDir, string entryName)
    {
        var forwardGitFile = Path.Combine(hostWorktreeDir, GitDirectoryName);
        var forwardTarget = ExtractGitDirPointerTarget(TryReadTrimmedText(forwardGitFile));
        if (string.IsNullOrEmpty(forwardTarget))
        {
            return false;
        }

        var normalizedForward = forwardTarget.Replace('\\', '/').TrimEnd('/');
        return normalizedForward.EndsWith($"/{WorktreesDirectoryName}/{entryName}", StringComparison.OrdinalIgnoreCase);
    }

    private static (bool ShouldRepair, string TargetContent) EvaluateReverseGitDir(
        string? currentGitDir,
        string expectedAbsoluteReverse,
        string expectedRelativeReverse,
        bool? supportsRelativeWorktrees)
    {
        if (supportsRelativeWorktrees == true)
        {
            var expectedTrimmed = expectedRelativeReverse.Trim();
            return (!string.Equals(currentGitDir, expectedTrimmed, StringComparison.Ordinal), expectedRelativeReverse);
        }

        if (supportsRelativeWorktrees == false)
        {
            var expectedTrimmed = expectedAbsoluteReverse.Trim();
            return (!PathsEqualNormalized(currentGitDir, expectedTrimmed), expectedAbsoluteReverse);
        }

        if (string.IsNullOrWhiteSpace(currentGitDir) || IsContainerVirtualPath(currentGitDir))
        {
            return (true, expectedAbsoluteReverse);
        }

        if (!currentGitDir.StartsWith("..", StringComparison.Ordinal) &&
            !PathsEqualNormalized(currentGitDir, expectedAbsoluteReverse.Trim()))
        {
            return (true, expectedAbsoluteReverse);
        }

        return (false, expectedAbsoluteReverse);
    }

    private static (string HostCanonicalPath, string ExecutionCanonicalPath) ResolvePaths(
        string canonicalRepoPath,
        IWorkspacePathResolver pathResolver,
        ILogger logger)
    {
        if (TryExtractWslExecutionPath(canonicalRepoPath, out var uncExecPath))
        {
            return (canonicalRepoPath, uncExecPath);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
            (canonicalRepoPath.StartsWith('/') || canonicalRepoPath.StartsWith('~')))
        {
            var hostPath = pathResolver.ToHostPath(canonicalRepoPath);
            var execPath = TryExtractWslExecutionPath(hostPath, out var mappedExecPath)
                ? mappedExecPath
                : ExpandPosixTilde(canonicalRepoPath, pathResolver, logger);
            return (hostPath, execPath);
        }

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && IsTildePath(canonicalRepoPath))
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(userProfile))
            {
                var expanded = CombinePosixTildeBase(userProfile.Replace('\\', '/').TrimEnd('/'), canonicalRepoPath);
                return (expanded, expanded);
            }
        }

        return (canonicalRepoPath, canonicalRepoPath);
    }

    private static bool TryExtractWslExecutionPath(string? path, out string executionPath)
    {
        executionPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var uncMatch = WslUncPathRegex().Match(path);
        if (!uncMatch.Success)
        {
            return false;
        }

        var rest = uncMatch.Groups[2].Value.Replace('\\', '/').TrimStart('/');
        executionPath = string.IsNullOrEmpty(rest) ? "/" : $"/{rest}";
        return true;
    }

    private static bool IsTildePath(string path) =>
        path == "~" ||
        path.StartsWith("~/", StringComparison.Ordinal) ||
        path.StartsWith("~\\", StringComparison.Ordinal);

    private static string CombinePosixTildeBase(string homeBase, string tildePath)
    {
        if (tildePath.Length == 1)
        {
            return homeBase;
        }

        var rel = tildePath[1..].TrimStart('/', '\\').Replace('\\', '/');
        return string.IsNullOrEmpty(rel) ? homeBase : $"{homeBase}/{rel}";
    }

    private static string ExpandPosixTilde(string path, IWorkspacePathResolver pathResolver, ILogger logger)
    {
        if (!IsTildePath(path))
        {
            return path;
        }

        try
        {
            var wslUser = pathResolver.GetWslUserName();
            if (string.IsNullOrWhiteSpace(wslUser))
            {
                return path;
            }

            return CombinePosixTildeBase($"/home/{wslUser}", path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Failed to expand tilde path '{Path}' via WSL user lookup.", path);
            return path;
        }
    }

    private static string? GetPosixParent(string normalizedPath)
    {
        var trimmed = normalizedPath.TrimEnd('/');
        var lastSlash = trimmed.LastIndexOf('/');
        return lastSlash > 0 ? trimmed[..lastSlash] : null;
    }

    private static bool PathsEqualNormalized(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        var normLeft = left.Trim().Replace('\\', '/');
        var normRight = right.Trim().Replace('\\', '/');
        var comparison = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
                         !normLeft.StartsWith('/') &&
                         !normRight.StartsWith('/')
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return string.Equals(normLeft, normRight, comparison);
    }

    private static bool IsTmpPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var normalized = path.Trim().Replace('\\', '/');
        return normalized.Equals(TmpRootPath, StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith(TmpPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TargetExistsOnHost(string targetPath, IWorkspacePathResolver pathResolver, ILogger logger)
    {
        if (File.Exists(targetPath) || Directory.Exists(targetPath))
        {
            return true;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && targetPath.StartsWith('/'))
        {
            try
            {
                var hostPath = pathResolver.ToHostPath(targetPath);
                if (!string.IsNullOrWhiteSpace(hostPath) &&
                    !string.Equals(hostPath, targetPath, StringComparison.OrdinalIgnoreCase) &&
                    (File.Exists(hostPath) || Directory.Exists(hostPath)))
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(
                    ex,
                    "Failed to resolve host path for worktree target '{TargetPath}'; treating target as present to avoid deleting metadata.",
                    targetPath);
                return true;
            }
        }

        return false;
    }

    private static string? ExtractGitDirPointerTarget(string? fileContent)
    {
        if (string.IsNullOrWhiteSpace(fileContent))
        {
            return null;
        }

        foreach (var line in fileContent.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith(GitDirPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return line[GitDirPrefix.Length..].Trim();
            }
        }

        return null;
    }

    private static void EnsureLockFileExists(string worktreeAdminDir, ILogger logger)
    {
        var lockFilePath = Path.Combine(worktreeAdminDir, LockedFileName);
        if (File.Exists(lockFilePath))
        {
            return;
        }

        WriteNormalizedFile(lockFilePath, $"{ManagedWorktreeLockReason}\n", logger);
        logger.LogDebug("Backfilled worktree lock file at '{LockFilePath}'.", lockFilePath);
    }

    private static string? TryReadTrimmedText(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return null;
        }

        return File.ReadAllText(filePath).Trim();
    }

    private static void WriteNormalizedFile(string filePath, string content, ILogger logger)
    {
        if (File.Exists(filePath))
        {
            try
            {
                File.SetAttributes(filePath, FileAttributes.Normal);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Failed to clear attributes on '{FilePath}'", filePath);
            }
        }

        File.WriteAllText(filePath, content);
    }

    private static void DeleteDirectorySafe(string directoryPath, ILogger logger)
    {
        if (!Directory.Exists(directoryPath))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories))
        {
            try
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Failed to clear attributes on '{FilePath}' before deletion.", file);
            }
        }

        Directory.Delete(directoryPath, recursive: true);
    }
}
