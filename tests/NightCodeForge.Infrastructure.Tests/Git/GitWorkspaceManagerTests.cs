using NightCodeForge.Core.Domain;
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
        _repo = new TestGitRepository("main");
        _runner = new GitCliRunner();
        _manager = new GitWorkspaceManager(_runner);

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
    public async Task CheckoutIssueBranchAsync_CreatesAndChecksOutIssueBranch()
    {
        // Act
        await _manager.CheckoutIssueBranchAsync(_workspace, 101, "main");
        var wtPath = _manager.GetWorktreePath(_workspace, 101);

        // Assert: canonical repo remains on "main" for ideation and triage
        var canonicalBranch = await _runner.ExecuteAsync(_repo.RepositoryPath, ["branch", "--show-current"]);
        Assert.Equal("main", canonicalBranch.StandardOutput.Trim());

        // Worktree directory is on feature/issue-101
        var wtBranch = await _runner.ExecuteAsync(wtPath, ["branch", "--show-current"]);
        Assert.Equal("feature/issue-101", wtBranch.StandardOutput.Trim());
    }

    [Fact]
    public async Task CheckoutIssueBranchAsync_WhenBranchAlreadyExists_DoesNotResetCommits()
    {
        // Arrange: Create branch worktree and commit a file in the worktree
        var wtPath = await _manager.EnsureWorktreeAsync(_workspace, 102, "main");
        _repo.CreateFile("file_on_branch.txt", "Some work on branch", wtPath);
        var sha1 = await _manager.CreateWipCheckpointAsync(_workspace, 102, "Stage1", "Checkpoint 1");

        // Act: Re-checkout / ensure issue branch worktree
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
        Assert.Contains("WIP: [Issue #103][Stage: Implementation] Added App.cs and notes", logResult.StandardOutput);

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
        Assert.Contains("WIP: [Issue #104][Stage: Review] Clean checkpoint", logResult.StandardOutput);
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
        var finalMessage = "feat: Completed issue #105 with multi-part feature";
        await _manager.SquashAndMergeAsync(_workspace, 105, "main", finalMessage);

        // Assert: on main branch
        var currentBranch = await _runner.ExecuteAsync(_repo.RepositoryPath, ["branch", "--show-current"]);
        Assert.Equal("main", currentBranch.StandardOutput.Trim());

        // Both files exist on main
        Assert.True(File.Exists(Path.Combine(_repo.RepositoryPath, "feature1.txt")));
        Assert.True(File.Exists(Path.Combine(_repo.RepositoryPath, "feature2.txt")));

        // Last commit message matches approved final message
        var log = await _runner.ExecuteAsync(_repo.RepositoryPath, ["log", "-1", "--pretty=%B"]);
        Assert.Contains(finalMessage, log.StandardOutput);

        // Ephemeral worktree has been removed
        Assert.False(Directory.Exists(wtPath));
    }

    [Fact]
    public async Task RevertOrCleanAsync_WithIssueId_RestoresDirtyWorktreeToCleanState()
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

        // Act: Revert worktree for issue 106
        await _manager.RevertOrCleanAsync(_workspace, 106);

        // Assert: issue worktree is clean
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

    public void Dispose()
    {
        _repo.Dispose();
    }
}
