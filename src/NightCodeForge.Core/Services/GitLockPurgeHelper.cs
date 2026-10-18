using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Core.Services;

/// <summary>
/// Helper to purge dangling Git lock files (index.lock, HEAD.lock, refs/heads/*.lock) across
/// normal git directories, worktree gitdir pointers, and WSL Ext4 mirrors.
/// </summary>
public static class GitLockPurgeHelper
{
    /// <summary>
    /// Purges all dangling *.lock files located in the target repository or worktree .git directory.
    /// Resolves worktree gitdir pointer files and translates WSL paths if running on Windows.
    /// </summary>
    /// <returns>The total number of lock files purged.</returns>
    public static int PurgeDanglingLocks(
        string repoPath,
        int workspaceId,
        IWorkspacePathResolver pathResolver,
        ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoPath);
        ArgumentNullException.ThrowIfNull(pathResolver);
        ArgumentNullException.ThrowIfNull(logger);

        var purgedCount = 0;
        try
        {
            var effectivePath = repoPath;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && (repoPath.StartsWith('/') || repoPath.StartsWith('~')))
            {
                effectivePath = pathResolver.ResolveCruciblePath(workspaceId, string.Empty);
            }

            if (!Directory.Exists(effectivePath) && !File.Exists(effectivePath))
            {
                logger.LogDebug("Target repository path '{RepoPath}' does not exist; skipping lock purging.", effectivePath);
                return 0;
            }

            var gitDir = Path.Combine(effectivePath, ".git");
            if (File.Exists(gitDir))
            {
                // Worktree pointer file: "gitdir: <path>"
                try
                {
                    var lines = File.ReadAllLines(gitDir);
                    var gitDirLine = lines.FirstOrDefault(l => l.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase));
                    if (gitDirLine != null)
                    {
                        var targetGitDir = gitDirLine[7..].Trim();
                        if (!Path.IsPathRooted(targetGitDir))
                        {
                            targetGitDir = Path.GetFullPath(Path.Combine(effectivePath, targetGitDir));
                        }
                        if (Directory.Exists(targetGitDir))
                        {
                            gitDir = targetGitDir;
                        }
                        else
                        {
                            logger.LogWarning("Resolved gitdir directory '{TargetGitDir}' from '{GitDirFile}' does not exist.", targetGitDir, gitDir);
                        }
                    }
                    else
                    {
                        logger.LogWarning("File '{GitDirFile}' did not contain a valid 'gitdir:' pointer.", gitDir);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to read or parse gitdir reference from '{GitDirFile}' in workspace #{WorkspaceId}", gitDir, workspaceId);
                }
            }
            else if (!Directory.Exists(gitDir) && effectivePath.EndsWith(".git", StringComparison.OrdinalIgnoreCase) && Directory.Exists(effectivePath))
            {
                gitDir = effectivePath;
            }

            if (!Directory.Exists(gitDir))
            {
                return 0;
            }

            var lockFiles = Directory.GetFiles(gitDir, "*.lock", SearchOption.AllDirectories);
            foreach (var lockFile in lockFiles)
            {
                try
                {
                    File.Delete(lockFile);
                    purgedCount++;
                    logger.LogWarning("Purged stale Git lockfile: {LockFile}", lockFile);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to delete lockfile {LockFile}", lockFile);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error scanning for lock files in {RepoPath}", repoPath);
        }

        return purgedCount;
    }
}
