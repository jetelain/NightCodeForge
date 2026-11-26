using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Services;
using Xunit;

namespace NightCodeForge.Core.Tests.Services;

public class GitWorktreeMetadataSanitizerTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly Mock<IWorkspacePathResolver> _pathResolverMock = new();

    public GitWorktreeMetadataSanitizerTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "ncf_wt_sanitizer_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _pathResolverMock.Setup(p => p.ToHostPath(It.IsAny<string>())).Returns<string>(p => p);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors in unit tests
        }
    }

    [Fact]
    public void Sanitize_RepairsContainerVirtualGitDirCommonDirForwardPointerAndBackfillsLock()
    {
        var cloneDir = Path.Combine(_tempRoot, "clone");
        var worktreeDir = Path.Combine(_tempRoot, "task-171");
        var adminDir = Path.Combine(cloneDir, ".git", "worktrees", "task-171");
        Directory.CreateDirectory(adminDir);
        Directory.CreateDirectory(worktreeDir);

        var gitDirFile = Path.Combine(adminDir, GitWorktreeMetadataSanitizer.GitDirFileName);
        var commonDirFile = Path.Combine(adminDir, GitWorktreeMetadataSanitizer.CommonDirFileName);
        var lockedFile = Path.Combine(adminDir, GitWorktreeMetadataSanitizer.LockedFileName);
        var forwardGitFile = Path.Combine(worktreeDir, ".git");

        File.WriteAllText(gitDirFile, "/workspace/task-171/.git\n");
        File.WriteAllText(commonDirFile, "/workspace/clone/.git\n");
        File.WriteAllText(forwardGitFile, "gitdir: /workspace/clone/.git/worktrees/task-171\n");

        var repairs = GitWorktreeMetadataSanitizer.Sanitize(cloneDir, _pathResolverMock.Object, NullLogger.Instance);

        Assert.True(repairs >= 3);
        Assert.Equal(GitWorktreeMetadataSanitizer.ExpectedCommonDirContent, File.ReadAllText(commonDirFile).Trim());
        Assert.Equal($"{worktreeDir.Replace('\\', '/')}/.git", File.ReadAllText(gitDirFile).Trim());
        Assert.True(File.Exists(lockedFile));
        Assert.Equal(GitWorktreeMetadataSanitizer.ManagedWorktreeLockReason, File.ReadAllText(lockedFile).Trim());
        Assert.Equal("gitdir: ../clone/.git/worktrees/task-171", File.ReadAllText(forwardGitFile).Trim());
    }

    [Fact]
    public void Sanitize_RemovesStrayNonTaskWorktreeWithContainerPath()
    {
        var cloneDir = Path.Combine(_tempRoot, "clone");
        var strayWorkspaceEntry = Path.Combine(cloneDir, ".git", "worktrees", "task171-base");
        var strayTmpEntry = Path.Combine(cloneDir, ".git", "worktrees", "tmp-scratch");
        Directory.CreateDirectory(strayWorkspaceEntry);
        Directory.CreateDirectory(strayTmpEntry);

        File.WriteAllText(Path.Combine(strayWorkspaceEntry, GitWorktreeMetadataSanitizer.GitDirFileName), "/workspace/tmp-wt/.git\n");
        File.WriteAllText(Path.Combine(strayWorkspaceEntry, GitWorktreeMetadataSanitizer.CommonDirFileName), "/workspace/clone/.git\n");

        File.WriteAllText(Path.Combine(strayTmpEntry, GitWorktreeMetadataSanitizer.GitDirFileName), "/tmp/nonexistent-wt-123456/.git\n");
        File.WriteAllText(Path.Combine(strayTmpEntry, GitWorktreeMetadataSanitizer.CommonDirFileName), "../..\n");

        var repairs = GitWorktreeMetadataSanitizer.Sanitize(cloneDir, _pathResolverMock.Object, NullLogger.Instance);

        Assert.Equal(2, repairs);
        Assert.False(Directory.Exists(strayWorkspaceEntry));
        Assert.False(Directory.Exists(strayTmpEntry));
    }

    [Fact]
    public void Sanitize_RemovesOrphanedTaskWorktreeWithContainerPath_WhenDirectoryMissing()
    {
        var cloneDir = Path.Combine(_tempRoot, "clone");
        var orphanedEntry = Path.Combine(cloneDir, ".git", "worktrees", "task-999");
        Directory.CreateDirectory(orphanedEntry);

        File.WriteAllText(Path.Combine(orphanedEntry, GitWorktreeMetadataSanitizer.GitDirFileName), "/workspace/task-999/.git\n");
        File.WriteAllText(Path.Combine(orphanedEntry, GitWorktreeMetadataSanitizer.CommonDirFileName), "/workspace/clone/.git\n");

        var repairs = GitWorktreeMetadataSanitizer.Sanitize(cloneDir, _pathResolverMock.Object, NullLogger.Instance);

        Assert.Equal(1, repairs);
        Assert.False(Directory.Exists(orphanedEntry));
    }

    [Fact]
    public void Sanitize_PreservesValidRelativeGitDir_WhenRelativeSupportNotSpecified()
    {
        var cloneDir = Path.Combine(_tempRoot, "clone");
        var worktreeDir = Path.Combine(_tempRoot, "task-10");
        var adminDir = Path.Combine(cloneDir, ".git", "worktrees", "task-10");
        Directory.CreateDirectory(adminDir);
        Directory.CreateDirectory(worktreeDir);

        var gitDirFile = Path.Combine(adminDir, GitWorktreeMetadataSanitizer.GitDirFileName);
        var commonDirFile = Path.Combine(adminDir, GitWorktreeMetadataSanitizer.CommonDirFileName);
        var lockedFile = Path.Combine(adminDir, GitWorktreeMetadataSanitizer.LockedFileName);

        File.WriteAllText(gitDirFile, "../../../../task-10/.git\n");
        File.WriteAllText(commonDirFile, "../..\n");

        var repairs = GitWorktreeMetadataSanitizer.Sanitize(
            cloneDir,
            _pathResolverMock.Object,
            NullLogger.Instance,
            supportsRelativeWorktrees: null);

        Assert.Equal(0, repairs);
        Assert.Equal("../../../../task-10/.git", File.ReadAllText(gitDirFile).Trim());
        Assert.Equal("../..", File.ReadAllText(commonDirFile).Trim());
        Assert.True(File.Exists(lockedFile));
    }

    [Fact]
    public void Sanitize_WhenDirectoryMissingOrNoWorktrees_ReturnsZeroWithoutThrowing()
    {
        var missingPath = Path.Combine(_tempRoot, "does-not-exist");
        Assert.Equal(0, GitWorktreeMetadataSanitizer.Sanitize(missingPath, _pathResolverMock.Object, NullLogger.Instance));

        var repoWithoutWorktrees = Path.Combine(_tempRoot, "plain-repo");
        Directory.CreateDirectory(Path.Combine(repoWithoutWorktrees, ".git"));
        Assert.Equal(0, GitWorktreeMetadataSanitizer.Sanitize(repoWithoutWorktrees, _pathResolverMock.Object, NullLogger.Instance));
    }

    [Theory]
    [InlineData("/workspace", true)]
    [InlineData("/workspace/task-1/.git", true)]
    [InlineData("\\workspace\\clone\\.git", true)]
    [InlineData("/CLONE/repo", true)]
    [InlineData("/nightcodeforge/wks/1", true)]
    [InlineData("/workspace-other/task-1", false)]
    [InlineData("/home/user/workspace/clone", false)]
    [InlineData(null, false)]
    [InlineData("   ", false)]
    public void IsContainerVirtualPath_DistinguishesContainerPrefixesFromSimilarPaths(string? path, bool expected)
    {
        Assert.Equal(expected, GitWorktreeMetadataSanitizer.IsContainerVirtualPath(path));
    }

    [Fact]
    public void Sanitize_WithSupportsRelativeWorktreesTrue_NormalizesAbsoluteAndPoisonedGitDirToRelative()
    {
        var cloneDir = Path.Combine(_tempRoot, "clone");
        var worktreeDir = Path.Combine(_tempRoot, "task-41");
        var adminDir = Path.Combine(cloneDir, ".git", "worktrees", "task-41");
        Directory.CreateDirectory(adminDir);
        Directory.CreateDirectory(worktreeDir);

        var gitDirFile = Path.Combine(adminDir, GitWorktreeMetadataSanitizer.GitDirFileName);
        var commonDirFile = Path.Combine(adminDir, GitWorktreeMetadataSanitizer.CommonDirFileName);
        File.WriteAllText(gitDirFile, $"{worktreeDir.Replace('\\', '/')}/.git\n");
        File.WriteAllText(commonDirFile, "../..\n");

        var repairs = GitWorktreeMetadataSanitizer.Sanitize(
            cloneDir,
            _pathResolverMock.Object,
            NullLogger.Instance,
            supportsRelativeWorktrees: true);

        Assert.Equal(1, repairs);
        Assert.Equal("../../../../task-41/.git", File.ReadAllText(gitDirFile).Trim());
        Assert.True(File.Exists(Path.Combine(adminDir, GitWorktreeMetadataSanitizer.LockedFileName)));
    }

    [Fact]
    public void Sanitize_WithSupportsRelativeWorktreesFalse_NormalizesRelativeGitDirToAbsolute()
    {
        var cloneDir = Path.Combine(_tempRoot, "clone");
        var worktreeDir = Path.Combine(_tempRoot, "task-42");
        var adminDir = Path.Combine(cloneDir, ".git", "worktrees", "task-42");
        Directory.CreateDirectory(adminDir);
        Directory.CreateDirectory(worktreeDir);

        var gitDirFile = Path.Combine(adminDir, GitWorktreeMetadataSanitizer.GitDirFileName);
        var commonDirFile = Path.Combine(adminDir, GitWorktreeMetadataSanitizer.CommonDirFileName);
        File.WriteAllText(gitDirFile, "../../../../task-42/.git\n");
        File.WriteAllText(commonDirFile, "../..\n");

        var repairs = GitWorktreeMetadataSanitizer.Sanitize(
            cloneDir,
            _pathResolverMock.Object,
            NullLogger.Instance,
            supportsRelativeWorktrees: false);

        Assert.Equal(1, repairs);
        Assert.Equal($"{worktreeDir.Replace('\\', '/')}/.git", File.ReadAllText(gitDirFile).Trim());
    }

    [Fact]
    public void Sanitize_ChildLayoutRepo_RepairsPointersWithChildRelativePaths()
    {
        var repoDir = Path.Combine(_tempRoot, "my-project");
        var worktreeDir = Path.Combine(repoDir, "task-12");
        var adminDir = Path.Combine(repoDir, ".git", "worktrees", "task-12");
        Directory.CreateDirectory(adminDir);
        Directory.CreateDirectory(worktreeDir);

        var gitDirFile = Path.Combine(adminDir, GitWorktreeMetadataSanitizer.GitDirFileName);
        var commonDirFile = Path.Combine(adminDir, GitWorktreeMetadataSanitizer.CommonDirFileName);
        var forwardGitFile = Path.Combine(worktreeDir, ".git");

        File.WriteAllText(gitDirFile, "/workspace/task-12/.git\n");
        File.WriteAllText(commonDirFile, "/workspace/.git\n");
        File.WriteAllText(forwardGitFile, "gitdir: /workspace/.git/worktrees/task-12\n");

        var repairs = GitWorktreeMetadataSanitizer.Sanitize(
            repoDir,
            _pathResolverMock.Object,
            NullLogger.Instance,
            supportsRelativeWorktrees: true);

        Assert.True(repairs >= 3);
        Assert.Equal("../../../task-12/.git", File.ReadAllText(gitDirFile).Trim());
        Assert.Equal("../..", File.ReadAllText(commonDirFile).Trim());
        Assert.Equal("gitdir: ../.git/worktrees/task-12", File.ReadAllText(forwardGitFile).Trim());
    }

    [Fact]
    public void Sanitize_CustomWorktreeRootPath_RepairsPoisonedMetadataWithoutDeletingLiveWorktree()
    {
        var mirrorDir = Path.Combine(_tempRoot, "my-mirror");
        var worktreesRoot = Path.Combine(_tempRoot, "wks-7");
        var worktreeDir = Path.Combine(worktreesRoot, "task-43");
        var adminDir = Path.Combine(mirrorDir, ".git", "worktrees", "task-43");
        Directory.CreateDirectory(adminDir);
        Directory.CreateDirectory(worktreeDir);

        var gitDirFile = Path.Combine(adminDir, GitWorktreeMetadataSanitizer.GitDirFileName);
        var commonDirFile = Path.Combine(adminDir, GitWorktreeMetadataSanitizer.CommonDirFileName);
        var forwardGitFile = Path.Combine(worktreeDir, ".git");

        File.WriteAllText(gitDirFile, "/workspace/task-43/.git\n");
        File.WriteAllText(commonDirFile, "/workspace/my-mirror/.git\n");
        File.WriteAllText(forwardGitFile, "gitdir: /workspace/my-mirror/.git/worktrees/task-43\n");

        var repairs = GitWorktreeMetadataSanitizer.Sanitize(
            mirrorDir,
            _pathResolverMock.Object,
            NullLogger.Instance,
            supportsRelativeWorktrees: true,
            worktreesRootPath: worktreesRoot);

        Assert.True(repairs >= 3);
        Assert.True(Directory.Exists(adminDir));
        Assert.Equal($"{worktreeDir.Replace('\\', '/')}/.git", File.ReadAllText(gitDirFile).Trim());
        Assert.Equal("../..", File.ReadAllText(commonDirFile).Trim());
        Assert.Equal($"gitdir: {mirrorDir.Replace('\\', '/')}/.git/worktrees/task-43", File.ReadAllText(forwardGitFile).Trim());
    }

    [Fact]
    public void Sanitize_GitSuffixedTaskAdminDir_RepairsLiveWorktreeInsteadOfDeleting()
    {
        var cloneDir = Path.Combine(_tempRoot, "clone");
        var worktreeDir = Path.Combine(_tempRoot, "task-171");
        var suffixedAdminDir = Path.Combine(cloneDir, ".git", "worktrees", "task-1711");
        Directory.CreateDirectory(suffixedAdminDir);
        Directory.CreateDirectory(worktreeDir);

        var gitDirFile = Path.Combine(suffixedAdminDir, GitWorktreeMetadataSanitizer.GitDirFileName);
        var commonDirFile = Path.Combine(suffixedAdminDir, GitWorktreeMetadataSanitizer.CommonDirFileName);
        var forwardGitFile = Path.Combine(worktreeDir, ".git");

        File.WriteAllText(gitDirFile, "/workspace/task-171/.git\n");
        File.WriteAllText(commonDirFile, "/workspace/clone/.git\n");
        File.WriteAllText(forwardGitFile, "gitdir: /workspace/clone/.git/worktrees/task-1711\n");

        var repairs = GitWorktreeMetadataSanitizer.Sanitize(cloneDir, _pathResolverMock.Object, NullLogger.Instance);

        Assert.True(repairs >= 3);
        Assert.True(Directory.Exists(suffixedAdminDir));
        Assert.Equal($"{worktreeDir.Replace('\\', '/')}/.git", File.ReadAllText(gitDirFile).Trim());
        Assert.Equal("../..", File.ReadAllText(commonDirFile).Trim());
        Assert.Equal("gitdir: ../clone/.git/worktrees/task-1711", File.ReadAllText(forwardGitFile).Trim());
    }

    [Fact]
    public void Sanitize_PosixPathMappedViaToHostPath_WritesPosixExecutionPathInReverseGitDir()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var cloneDir = Path.Combine(_tempRoot, "clone");
        var worktreeDir = Path.Combine(_tempRoot, "task-55");
        var adminDir = Path.Combine(cloneDir, ".git", "worktrees", "task-55");
        Directory.CreateDirectory(adminDir);
        Directory.CreateDirectory(worktreeDir);

        const string posixClonePath = "/home/dev/.nightcodeforge/wks/1/clone";
        _pathResolverMock.Setup(p => p.ToHostPath(posixClonePath)).Returns(cloneDir);

        var gitDirFile = Path.Combine(adminDir, GitWorktreeMetadataSanitizer.GitDirFileName);
        var commonDirFile = Path.Combine(adminDir, GitWorktreeMetadataSanitizer.CommonDirFileName);
        File.WriteAllText(gitDirFile, "/workspace/task-55/.git\n");
        File.WriteAllText(commonDirFile, "../..\n");

        var repairs = GitWorktreeMetadataSanitizer.Sanitize(posixClonePath, _pathResolverMock.Object, NullLogger.Instance);

        Assert.Equal(1, repairs);
        Assert.Equal("/home/dev/.nightcodeforge/wks/1/task-55/.git", File.ReadAllText(gitDirFile).Trim());
    }

    [Fact]
    public void Sanitize_PreservesLegitimateNonTaskWorktrees_AndFailsSafeWhenHostResolutionThrows()
    {
        var cloneDir = Path.Combine(_tempRoot, "clone");
        var liveCustomWt = Path.Combine(_tempRoot, "custom-wt");
        var customAdminDir = Path.Combine(cloneDir, ".git", "worktrees", "custom-wt");
        var tmpFailSafeAdminDir = Path.Combine(cloneDir, ".git", "worktrees", "tmp-live");
        Directory.CreateDirectory(liveCustomWt);
        Directory.CreateDirectory(customAdminDir);
        Directory.CreateDirectory(tmpFailSafeAdminDir);

        var customGitFile = Path.Combine(liveCustomWt, ".git");
        File.WriteAllText(customGitFile, "gitdir: ../clone/.git/worktrees/custom-wt\n");
        File.WriteAllText(Path.Combine(customAdminDir, GitWorktreeMetadataSanitizer.GitDirFileName), $"{customGitFile.Replace('\\', '/')}\n");
        File.WriteAllText(Path.Combine(customAdminDir, GitWorktreeMetadataSanitizer.CommonDirFileName), "../..\n");

        const string tmpTarget = "/tmp/live-wt/.git";
        File.WriteAllText(Path.Combine(tmpFailSafeAdminDir, GitWorktreeMetadataSanitizer.GitDirFileName), $"{tmpTarget}\n");
        File.WriteAllText(Path.Combine(tmpFailSafeAdminDir, GitWorktreeMetadataSanitizer.CommonDirFileName), "../..\n");
        _pathResolverMock.Setup(p => p.ToHostPath(tmpTarget)).Throws(new InvalidOperationException("WSL unavailable"));

        var repairs = GitWorktreeMetadataSanitizer.Sanitize(cloneDir, _pathResolverMock.Object, NullLogger.Instance);

        Assert.Equal(0, repairs);
        Assert.True(Directory.Exists(customAdminDir));
        if (OperatingSystem.IsWindows())
        {
            Assert.True(Directory.Exists(tmpFailSafeAdminDir));
        }
    }

    [Fact]
    public void EnsureWorktreeCommonDirAndLock_RepairsCommonDirAndPreservesExistingLockReason()
    {
        var adminDir = Path.Combine(_tempRoot, "clone", ".git", "worktrees", "task-77");
        Directory.CreateDirectory(adminDir);

        var commonDirFile = Path.Combine(adminDir, GitWorktreeMetadataSanitizer.CommonDirFileName);
        var lockedFile = Path.Combine(adminDir, GitWorktreeMetadataSanitizer.LockedFileName);
        File.WriteAllText(commonDirFile, "/workspace/clone/.git\n");
        File.WriteAllText(lockedFile, "Custom user lock\n");

        GitWorktreeMetadataSanitizer.EnsureWorktreeCommonDirAndLock(adminDir, NullLogger.Instance);

        Assert.Equal(GitWorktreeMetadataSanitizer.ExpectedCommonDirContent, File.ReadAllText(commonDirFile).Trim());
        Assert.Equal("Custom user lock", File.ReadAllText(lockedFile).Trim());
    }

    [Fact]
    public void UnlockWorktreesForTask_RemovesLocksFromExactAndSuffixedAdminDirs()
    {
        var cloneDir = Path.Combine(_tempRoot, "clone");
        var exactAdminDir = Path.Combine(cloneDir, ".git", "worktrees", "task-88");
        var suffixedAdminDir = Path.Combine(cloneDir, ".git", "worktrees", "task-881");
        var otherAdminDir = Path.Combine(cloneDir, ".git", "worktrees", "task-89");
        Directory.CreateDirectory(exactAdminDir);
        Directory.CreateDirectory(suffixedAdminDir);
        Directory.CreateDirectory(otherAdminDir);

        File.WriteAllText(Path.Combine(exactAdminDir, GitWorktreeMetadataSanitizer.LockedFileName), "Managed\n");
        File.WriteAllText(Path.Combine(suffixedAdminDir, GitWorktreeMetadataSanitizer.GitDirFileName), "../../../../task-88/.git\n");
        File.WriteAllText(Path.Combine(suffixedAdminDir, GitWorktreeMetadataSanitizer.LockedFileName), "Managed\n");
        File.WriteAllText(Path.Combine(otherAdminDir, GitWorktreeMetadataSanitizer.LockedFileName), "Managed\n");

        var unlocked = GitWorktreeMetadataSanitizer.UnlockWorktreesForTask(cloneDir, 88, _pathResolverMock.Object, NullLogger.Instance);

        Assert.Equal(2, unlocked);
        Assert.False(File.Exists(Path.Combine(exactAdminDir, GitWorktreeMetadataSanitizer.LockedFileName)));
        Assert.False(File.Exists(Path.Combine(suffixedAdminDir, GitWorktreeMetadataSanitizer.LockedFileName)));
        Assert.True(File.Exists(Path.Combine(otherAdminDir, GitWorktreeMetadataSanitizer.LockedFileName)));
    }
}
