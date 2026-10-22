using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Execution;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Infrastructure.Execution;
using NightCodeForge.Infrastructure.Git;

namespace NightCodeForge.Infrastructure.Tests.Git;

public class GitWorkspaceManagerTests : IDisposable
{
    private readonly TestGitRepository _repo;
    private readonly GitCliRunner _runner;
    private readonly GitWorkspaceManager _manager;
    private readonly Workspace _workspace;

    public GitWorkspaceManagerTests()
    {
        var wslMeta = new Mock<IWslMetadataService>().Object;
        var pathResolver = new WorkspacePathResolver(wslMeta, Microsoft.Extensions.Options.Options.Create(new ExecutionEnvironmentProviderOptions()));
        _repo = new TestGitRepository("main");
        _runner = new GitCliRunner(
            new SystemProcessRunner(NullLogger<SystemProcessRunner>.Instance),
            pathResolver,
            wslMeta,
            NullLogger<GitCliRunner>.Instance);
        _manager = new GitWorkspaceManager(
            _runner,
            pathResolver,
            wslMeta,
            NullLogger<GitWorkspaceManager>.Instance);

        // Initial base commit on main
        _repo.CreateFile("README.md", "# Test Project\nInitial commit.");
        _repo.CommitAll("Initial commit on main");

        _workspace = new Workspace
        {
            Id = 42,
            Name = "Test Workspace",
            RepositoryPath = _repo.RepositoryPath,
            DefaultBaseBranch = "main"
        };
    }

    [Fact]
    public async Task EnsureRepositoryValidAsync_WithValidRepo_Succeeds()
    {
        await _manager.EnsureRepositoryValidAsync(_repo.RepositoryPath);
    }

    [Fact]
    public async Task EnsureRepositoryValidAsync_WithNonExistentPath_ThrowsDirectoryNotFoundException()
    {
        var nonExistentPath = Path.Combine(Path.GetTempPath(), "non_existent_" + Guid.NewGuid().ToString("N"));

        await Assert.ThrowsAsync<DirectoryNotFoundException>(async () =>
        {
            await _manager.EnsureRepositoryValidAsync(nonExistentPath);
        });
    }

    [Fact]
    public async Task EnsureRepositoryValidAsync_WithNonGitDirectory_ThrowsInvalidOperationException()
    {
        var nonGitDir = Path.Combine(Path.GetTempPath(), "non_git_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(nonGitDir);

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await _manager.EnsureRepositoryValidAsync(nonGitDir);
            });
        }
        finally
        {
            TestGitRepository.DeleteDirectorySafe(nonGitDir);
        }
    }

    [Fact]
    public async Task CheckoutTaskBranchAsync_CreatesAndChecksOutTaskBranch()
    {
        // Act
        await _manager.CheckoutTaskBranchAsync(_workspace, 101, "main");
        var wtPath = _manager.GetWorktreePath(_workspace, 101);

        // Assert: canonical repo remains on "main" for ideation and triage
        var canonicalBranch = await _runner.ExecuteAsync(_repo.RepositoryPath, ["branch", "--show-current"]);
        Assert.Equal("main", canonicalBranch.StandardOutput.Trim());

        // Worktree directory is on feature/task-101
        var wtBranch = await _runner.ExecuteAsync(wtPath, ["branch", "--show-current"]);
        Assert.Equal("feature/task-101", wtBranch.StandardOutput.Trim());
    }

    [Fact]
    public async Task CheckoutTaskBranchAsync_WhenBranchAlreadyExists_DoesNotResetCommits()
    {
        // Arrange: Create branch worktree and commit a file in the worktree
        var wtPath = await _manager.EnsureWorktreeAsync(_workspace, 102, "main");
        _repo.CreateFile("file_on_branch.txt", "Some work on branch", wtPath);
        var sha1 = await _manager.CreateWipCheckpointAsync(_workspace, 102, "Stage1", "Checkpoint 1");

        // Act: Re-checkout / ensure task branch worktree
        var wtPath2 = await _manager.EnsureWorktreeAsync(_workspace, 102, "main");
        Assert.Equal(wtPath, wtPath2);

        // Assert: file and commit should still be preserved in the worktree
        var revParse = await _runner.ExecuteAsync(wtPath, ["rev-parse", "HEAD"]);
        Assert.Equal(sha1, revParse.StandardOutput.Trim());
        Assert.True(File.Exists(Path.Combine(wtPath, "file_on_branch.txt")));

        // Canonical repo still remains on main
        var canonicalBranch = await _runner.ExecuteAsync(_repo.RepositoryPath, ["branch", "--show-current"]);
        Assert.Equal("main", canonicalBranch.StandardOutput.Trim());
    }

    [Fact]
    public async Task CreateWipCheckpointAsync_StagesAllFilesAndCreatesNoVerifyCommit()
    {
        // Arrange
        var wtPath = await _manager.EnsureWorktreeAsync(_workspace, 103, "main");
        _repo.CreateFile("src/App.cs", "public class App {}", wtPath);
        _repo.CreateFile("docs/notes.md", "WIP notes", wtPath);

        // Act
        var sha = await _manager.CreateWipCheckpointAsync(_workspace, 103, "Implementation", "Added App.cs and notes");

        // Assert
        Assert.NotNull(sha);
        Assert.Equal(40, sha.Length);

        // Verify commit log message format in the worktree
        var logResult = await _runner.ExecuteAsync(wtPath, ["log", "-1", "--pretty=%B"]);
        Assert.Contains("WIP: [ForgeTask #103][Stage: Implementation] Added App.cs and notes", logResult.StandardOutput);

        // Verify status in worktree is clean
        var status = await _runner.ExecuteAsync(wtPath, ["status", "--porcelain"]);
        Assert.Empty(status.StandardOutput.Trim());
    }

    [Fact]
    public async Task CreateWipCheckpointAsync_WhenClean_CreatesEmptyCheckpointCommit()
    {
        // Arrange: clean repository worktree
        var wtPath = await _manager.EnsureWorktreeAsync(_workspace, 104, "main");

        // Act
        var sha = await _manager.CreateWipCheckpointAsync(_workspace, 104, "Review", "Clean checkpoint");

        // Assert
        Assert.NotNull(sha);
        Assert.Equal(40, sha.Length);

        var logResult = await _runner.ExecuteAsync(wtPath, ["log", "-1", "--pretty=%B"]);
        Assert.Contains("WIP: [ForgeTask #104][Stage: Review] Clean checkpoint", logResult.StandardOutput);
    }

    [Fact]
    public async Task SquashAndMergeAsync_SquashesWipCommitsIntoBaseBranch()
    {
        // Arrange: create branch with multiple WIP commits in its worktree
        var wtPath = await _manager.EnsureWorktreeAsync(_workspace, 105, "main");

        _repo.CreateFile("feature1.txt", "Part 1", wtPath);
        await _manager.CreateWipCheckpointAsync(_workspace, 105, "Stage1", "WIP 1");

        _repo.CreateFile("feature2.txt", "Part 2", wtPath);
        await _manager.CreateWipCheckpointAsync(_workspace, 105, "Stage2", "WIP 2");

        // Act: Squash and merge into main
        var finalMessage = "feat: Completed task #105 with multi-part feature";
        var squashSha = await _manager.SquashAndMergeAsync(_workspace, 105, "main", finalMessage);

        // Assert: on main branch
        Assert.False(string.IsNullOrWhiteSpace(squashSha));
        Assert.Equal(40, squashSha.Length);

        var currentBranch = await _runner.ExecuteAsync(_repo.RepositoryPath, ["branch", "--show-current"]);
        Assert.Equal("main", currentBranch.StandardOutput.Trim());

        // Both files exist on main
        Assert.True(File.Exists(Path.Combine(_repo.RepositoryPath, "feature1.txt")));
        Assert.True(File.Exists(Path.Combine(_repo.RepositoryPath, "feature2.txt")));

        // Last commit message matches approved final message
        var log = await _runner.ExecuteAsync(_repo.RepositoryPath, ["log", "-1", "--pretty=%B"]);
        Assert.Contains(finalMessage, log.StandardOutput);

        // Commit diff via GetCommitDiffAsync matches squashed files
        var commitDiff = await _manager.GetCommitDiffAsync(_workspace, squashSha);
        Assert.Contains("feature1.txt", commitDiff);
        Assert.Contains("feature2.txt", commitDiff);

        // Ephemeral worktree has been removed
        Assert.False(Directory.Exists(wtPath));
    }

    [Fact]
    public async Task SquashAndMergeAsync_WithDirtyCanonicalClone_CleansAndMergesSuccessfully()
    {
        // Arrange: create branch with WIP commit in worktree
        var wtPath = await _manager.EnsureWorktreeAsync(_workspace, 107, "main");
        _repo.CreateFile("feature_clean.txt", "Clean content", wtPath);
        await _manager.CreateWipCheckpointAsync(_workspace, 107, "Stage1", "WIP on feature");

        // Make canonical repository dirty (modified tracked file and untracked file)
        File.WriteAllText(Path.Combine(_repo.RepositoryPath, "README.md"), "Corrupted canonical README");
        _repo.CreateFile("canonical_trash.tmp", "Dirty trash", _repo.RepositoryPath);

        // Act: Squash merge with commit message containing double quotes
        var messageWithQuotes = "feat: added \"awesome\" capability to task #107";
        var squashSha = await _manager.SquashAndMergeAsync(_workspace, 107, "main", messageWithQuotes);

        // Assert: Squash succeeded, dirty changes removed, and quotes are NOT escaped with backslashes
        Assert.False(string.IsNullOrWhiteSpace(squashSha));
        Assert.Equal(40, squashSha.Length);
        Assert.False(File.Exists(Path.Combine(_repo.RepositoryPath, "canonical_trash.tmp")));

        var log = await _runner.ExecuteAsync(_repo.RepositoryPath, ["log", "-1", "--pretty=%B"]);
        Assert.Contains(messageWithQuotes, log.StandardOutput);
        Assert.DoesNotContain("\\\"", log.StandardOutput);
    }

    [Fact]
    public async Task SquashAndMergeAsync_WithMissingBaseBranch_ThrowsBaseBranchNotFoundException()
    {
        // Arrange
        await _manager.EnsureWorktreeAsync(_workspace, 108, "main");
        _repo.CreateFile("feature_base.txt", "Some work", _manager.GetWorktreePath(_workspace, 108));
        await _manager.CreateWipCheckpointAsync(_workspace, 108, "Stage1", "WIP");

        // Act & Assert: Target missing base branch without allowCreateBaseBranch
        var ex = await Assert.ThrowsAsync<BaseBranchNotFoundException>(() =>
            _manager.SquashAndMergeAsync(_workspace, 108, "nonexistent-base-branch", "Commit message"));

        Assert.Equal("nonexistent-base-branch", ex.BranchName);
        Assert.Contains("nonexistent-base-branch", ex.Message);
    }

    [Fact]
    public async Task SquashAndMergeAsync_WithMissingFeatureBranch_ThrowsFeatureBranchNotFoundException()
    {
        // Act & Assert: Attempting to squash merge a nonexistent task (no local or remote branch)
        var ex = await Assert.ThrowsAsync<FeatureBranchNotFoundException>(() =>
            _manager.SquashAndMergeAsync(_workspace, 999, "main", "Commit message"));

        Assert.Equal("feature/task-999", ex.BranchName);
        Assert.Contains("feature/task-999", ex.Message);
    }

    [Fact]
    public async Task RevertOrCleanAsync_WithTaskId_RestoresDirtyWorktreeToCleanState()
    {
        // Arrange
        var wtPath = await _manager.EnsureWorktreeAsync(_workspace, 106, "main");
        _repo.CreateFile("dirty_tracked.txt", "Original content", wtPath);
        await _manager.CreateWipCheckpointAsync(_workspace, 106, "Stage1", "Committed file");

        // Introduce dirty changes and untracked file into the worktree
        File.WriteAllText(Path.Combine(wtPath, "dirty_tracked.txt"), "Dirty modified content");
        _repo.CreateFile("untracked_file.tmp", "Untracked trash", wtPath);

        var statusBefore = await _runner.ExecuteAsync(wtPath, ["status", "--porcelain"]);
        Assert.NotEmpty(statusBefore.StandardOutput.Trim());

        // Act: Revert worktree for task 106
        await _manager.RevertOrCleanAsync(_workspace, 106);

        // Assert: task worktree is clean
        var statusAfter = await _runner.ExecuteAsync(wtPath, ["status", "--porcelain"]);
        Assert.Empty(statusAfter.StandardOutput.Trim());
        Assert.False(File.Exists(Path.Combine(wtPath, "untracked_file.tmp")));
        Assert.Equal("Original content", File.ReadAllText(Path.Combine(wtPath, "dirty_tracked.txt")));
    }

    [Fact]
    public async Task RevertOrCleanAsync_CanonicalRepo_RestoresDirtyWorkingTreeToCleanState()
    {
        // Arrange
        _repo.CreateFile("dirty_canonical.txt", "Original canonical");
        _repo.CommitAll("Commit canonical file");

        File.WriteAllText(Path.Combine(_repo.RepositoryPath, "dirty_canonical.txt"), "Dirty canonical content");
        _repo.CreateFile("untracked_canonical.tmp", "Untracked trash");

        var statusBefore = await _runner.ExecuteAsync(_repo.RepositoryPath, ["status", "--porcelain"]);
        Assert.NotEmpty(statusBefore.StandardOutput.Trim());

        // Act
        await _manager.RevertOrCleanAsync(_workspace);

        // Assert: canonical repo is clean
        var statusAfter = await _runner.ExecuteAsync(_repo.RepositoryPath, ["status", "--porcelain"]);
        Assert.Empty(statusAfter.StandardOutput.Trim());
        Assert.False(File.Exists(Path.Combine(_repo.RepositoryPath, "untracked_canonical.tmp")));
        Assert.Equal("Original canonical", File.ReadAllText(Path.Combine(_repo.RepositoryPath, "dirty_canonical.txt")));
    }

    [Fact]
    public async Task EnsureWorktreeAsync_WritesRelativeForwardGitdir()
    {
        // Act
        var wtPath = await _manager.EnsureWorktreeAsync(_workspace, 201, "main");

        // Assert
        var gitFile = Path.Combine(wtPath, ".git");
        Assert.True(File.Exists(gitFile));

        var content = (await File.ReadAllTextAsync(gitFile)).Trim();
        Assert.StartsWith("gitdir: ../", content);
        Assert.DoesNotContain(":\\", content);
        Assert.DoesNotContain(":/", content);
        Assert.EndsWith(".git/worktrees/task-201", content);
    }

    [Fact]
    public async Task EnsureWorktreeAsync_WritesRelativeReverseGitdir()
    {
        // Act
        var wtPath = await _manager.EnsureWorktreeAsync(_workspace, 202, "main");

        // Assert
        var reverseGitDir = Path.Combine(_repo.RepositoryPath, ".git", "worktrees", "task-202", "gitdir");
        Assert.True(File.Exists(reverseGitDir));

        var content = (await File.ReadAllTextAsync(reverseGitDir)).Trim();
        Assert.StartsWith("../", content);
        Assert.DoesNotContain(":\\", content);
        Assert.DoesNotContain(":/", content);
        Assert.Equal("../../../task-202/.git", content);

        // Git commands from both worktree and canonical repo succeed
        var wtBranch = await _runner.ExecuteAsync(wtPath, ["branch", "--show-current"]);
        Assert.Equal("feature/task-202", wtBranch.StandardOutput.Trim());

        var wtList = await _runner.ExecuteAsync(_repo.RepositoryPath, ["worktree", "list", "--porcelain"]);
        Assert.Contains("task-202", wtList.StandardOutput);
    }

    [Fact]
    public async Task EnsureWorktreeAsync_WhenAlreadyRegisteredWithAbsolutePointer_MigratesToRelative()
    {
        // Arrange
        var wtPath = await _manager.EnsureWorktreeAsync(_workspace, 203, "main");
        var gitFile = Path.Combine(wtPath, ".git");
        var reverseGitDir = Path.Combine(_repo.RepositoryPath, ".git", "worktrees", "task-203", "gitdir");

        // Overwrite both with synthetic absolute paths
        var fakeAbsForward = "gitdir: C:/synthetic/abs/path/clone/.git/worktrees/task-203\n";
        var fakeAbsReverse = "C:/synthetic/abs/path/wks/task-203/.git\n";
        File.SetAttributes(gitFile, FileAttributes.Normal);
        File.SetAttributes(reverseGitDir, FileAttributes.Normal);
        await File.WriteAllTextAsync(gitFile, fakeAbsForward);
        await File.WriteAllTextAsync(reverseGitDir, fakeAbsReverse);

        // Act: Re-run EnsureWorktreeAsync (simulating already-registered worktree check)
        var resultPath = await _manager.EnsureWorktreeAsync(_workspace, 203, "main");
        Assert.Equal(wtPath, resultPath);

        // Assert: Both forward and reverse pointers were migrated to relative
        var migratedForward = (await File.ReadAllTextAsync(gitFile)).Trim();
        Assert.StartsWith("gitdir: ../", migratedForward);
        Assert.DoesNotContain("synthetic", migratedForward);

        var migratedReverse = (await File.ReadAllTextAsync(reverseGitDir)).Trim();
        Assert.Equal("../../../task-203/.git", migratedReverse);
        Assert.DoesNotContain("synthetic", migratedReverse);
    }

    [Fact]
    public async Task EnsureWorktreeRelativePathsAsync_HandlesWslUncPathsOnWindows()
    {
        // Arrange: Workspace configured with a POSIX WSL-like Ext4MirrorPath
        var wslMetaMock = new Mock<IWslMetadataService>();
        wslMetaMock.Setup(w => w.GetDefaultDistributionName()).Returns("Ubuntu");
        wslMetaMock.Setup(w => w.GetDefaultUserName()).Returns("testuser");

        var pathResolver = new WorkspacePathResolver(wslMetaMock.Object, Microsoft.Extensions.Options.Options.Create(new ExecutionEnvironmentProviderOptions()));
        var customManager = new GitWorkspaceManager(
            _runner,
            pathResolver,
            wslMetaMock.Object,
            NullLogger<GitWorkspaceManager>.Instance);

        var wslWorkspace = new Workspace
        {
            Id = 99,
            Name = "WSL Test",
            Ext4MirrorPath = "/home/testuser/.nightcodeforge/wks/99/clone",
            RepositoryPath = _repo.RepositoryPath
        };

        // Act & Assert: Calling EnsureWorktreeRelativePathsAsync with nonexistent paths returns gracefully without throwing
        var ex = await Record.ExceptionAsync(() => customManager.EnsureWorktreeRelativePathsAsync(wslWorkspace, 999));
        Assert.Null(ex);
    }

    [Fact]
    public async Task GitCommands_SucceedInWorktreeWithRelativePointers()
    {
        // Arrange
        var wtPath = await _manager.EnsureWorktreeAsync(_workspace, 205, "main");

        // Act & Assert: Git status, branch, and log commands succeed without "fatal: not a git repository"
        var status = await _runner.ExecuteAsync(wtPath, ["status", "--porcelain"]);
        Assert.Equal(0, status.ExitCode);
        Assert.DoesNotContain("fatal", status.StandardError, StringComparison.OrdinalIgnoreCase);

        var branch = await _runner.ExecuteAsync(wtPath, ["branch", "-a"]);
        Assert.Equal(0, branch.ExitCode);
        Assert.Contains("feature/task-205", branch.StandardOutput);

        var log = await _runner.ExecuteAsync(wtPath, ["log", "-1", "--oneline"]);
        Assert.Equal(0, log.ExitCode);
        Assert.Contains("Initial commit", log.StandardOutput);
    }

    [Fact]
    public async Task EnsureWorktreeAsync_WithSiblingLayout_WritesFourDotDotsReverseGitdir()
    {
        // Arrange a repo in a folder named "clone" so GetWorktreePath uses sibling layout
        var tempRoot = Path.Combine(Path.GetTempPath(), "ncf_sibling_test_" + Guid.NewGuid().ToString("N"));
        var cloneDir = Path.Combine(tempRoot, "clone");
        Directory.CreateDirectory(cloneDir);

        try
        {
            using (var proc1 = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", "init -b main")
            {
                WorkingDirectory = cloneDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }))
            {
                proc1?.WaitForExit();
            }

            using (var proc2 = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", "commit --allow-empty -m init --no-verify")
            {
                WorkingDirectory = cloneDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }))
            {
                proc2?.WaitForExit();
            }

            var siblingWorkspace = new Workspace
            {
                Id = 88,
                Name = "Sibling Workspace",
                RepositoryPath = cloneDir,
                DefaultBaseBranch = "main"
            };

            // Act
            var wtPath = await _manager.EnsureWorktreeAsync(siblingWorkspace, 206, "main");

            // Assert
            var forwardContent = (await File.ReadAllTextAsync(Path.Combine(wtPath, ".git"))).Trim();
            Assert.Equal("gitdir: ../clone/.git/worktrees/task-206", forwardContent);

            var reverseGitDir = Path.Combine(cloneDir, ".git", "worktrees", "task-206", "gitdir");
            Assert.True(File.Exists(reverseGitDir));
            var reverseContent = (await File.ReadAllTextAsync(reverseGitDir)).Trim();
            Assert.Equal("../../../../task-206/.git", reverseContent);

            // Git operations succeed in both
            var status = await _runner.ExecuteAsync(wtPath, ["status", "--porcelain"]);
            Assert.Equal(0, status.ExitCode);
        }
        finally
        {
            TestGitRepository.DeleteDirectorySafe(tempRoot);
        }
    }

    public void Dispose()
    {
        _repo.Dispose();
    }
}
