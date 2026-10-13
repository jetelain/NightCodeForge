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

        // Assert: current branch should be feature/issue-101
        var result = await _runner.ExecuteAsync(_repo.RepositoryPath, "branch --show-current");
        Assert.Equal("feature/issue-101", result.StandardOutput.Trim());
    }

    [Fact]
    public async Task CheckoutIssueBranchAsync_WhenBranchAlreadyExists_DoesNotResetCommits()
    {
        // Arrange: Create branch and commit a file
        await _manager.CheckoutIssueBranchAsync(_workspace, 102, "main");
        _repo.CreateFile("file_on_branch.txt", "Some work on branch");
        var sha1 = await _manager.CreateWipCheckpointAsync(_workspace, 102, "Stage1", "Checkpoint 1");

        // Switch back to main
        await _runner.ExecuteAsync(_repo.RepositoryPath, "checkout main");

        // Act: Re-checkout issue branch
        await _manager.CheckoutIssueBranchAsync(_workspace, 102, "main");

        // Assert: file and commit should still be preserved
        var revParse = await _runner.ExecuteAsync(_repo.RepositoryPath, "rev-parse HEAD");
        Assert.Equal(sha1, revParse.StandardOutput.Trim());
        Assert.True(File.Exists(Path.Combine(_repo.RepositoryPath, "file_on_branch.txt")));
    }

    [Fact]
    public async Task CreateWipCheckpointAsync_StagesAllFilesAndCreatesNoVerifyCommit()
    {
        // Arrange
        await _manager.CheckoutIssueBranchAsync(_workspace, 103, "main");
        _repo.CreateFile("src/App.cs", "public class App {}");
        _repo.CreateFile("docs/notes.md", "WIP notes");

        // Act
        var sha = await _manager.CreateWipCheckpointAsync(_workspace, 103, "Implementation", "Added App.cs and notes");

        // Assert
        Assert.NotNull(sha);
        Assert.Equal(40, sha.Length);

        // Verify commit log message format
        var logResult = await _runner.ExecuteAsync(_repo.RepositoryPath, "log -1 --pretty=%B");
        Assert.Contains("WIP: [Issue #103][Stage: Implementation] Added App.cs and notes", logResult.StandardOutput);

        // Verify status is clean
        var status = await _runner.ExecuteAsync(_repo.RepositoryPath, "status --porcelain");
        Assert.Empty(status.StandardOutput.Trim());
    }

    [Fact]
    public async Task CreateWipCheckpointAsync_WhenClean_CreatesEmptyCheckpointCommit()
    {
        // Arrange: clean repository
        await _manager.CheckoutIssueBranchAsync(_workspace, 104, "main");

        // Act
        var sha = await _manager.CreateWipCheckpointAsync(_workspace, 104, "Review", "Clean checkpoint");

        // Assert
        Assert.NotNull(sha);
        Assert.Equal(40, sha.Length);

        var logResult = await _runner.ExecuteAsync(_repo.RepositoryPath, "log -1 --pretty=%B");
        Assert.Contains("WIP: [Issue #104][Stage: Review] Clean checkpoint", logResult.StandardOutput);
    }

    [Fact]
    public async Task SquashAndMergeAsync_SquashesWipCommitsIntoBaseBranch()
    {
        // Arrange: create branch with multiple WIP commits
        await _manager.CheckoutIssueBranchAsync(_workspace, 105, "main");

        _repo.CreateFile("feature1.txt", "Part 1");
        await _manager.CreateWipCheckpointAsync(_workspace, 105, "Stage1", "WIP 1");

        _repo.CreateFile("feature2.txt", "Part 2");
        await _manager.CreateWipCheckpointAsync(_workspace, 105, "Stage2", "WIP 2");

        // Act: Squash and merge into main
        var finalMessage = "feat: Completed issue #105 with multi-part feature";
        await _manager.SquashAndMergeAsync(_workspace, 105, "main", finalMessage);

        // Assert: on main branch
        var currentBranch = await _runner.ExecuteAsync(_repo.RepositoryPath, "branch --show-current");
        Assert.Equal("main", currentBranch.StandardOutput.Trim());

        // Both files exist on main
        Assert.True(File.Exists(Path.Combine(_repo.RepositoryPath, "feature1.txt")));
        Assert.True(File.Exists(Path.Combine(_repo.RepositoryPath, "feature2.txt")));

        // Last commit message matches approved final message
        var log = await _runner.ExecuteAsync(_repo.RepositoryPath, "log -1 --pretty=%B");
        Assert.Contains(finalMessage, log.StandardOutput);
    }

    [Fact]
    public async Task RevertOrCleanAsync_RestoresDirtyWorkingTreeToCleanState()
    {
        // Arrange
        await _manager.CheckoutIssueBranchAsync(_workspace, 106, "main");
        _repo.CreateFile("dirty_tracked.txt", "Original content");
        await _manager.CreateWipCheckpointAsync(_workspace, 106, "Stage1", "Committed file");

        // Introduce dirty changes and untracked file
        File.WriteAllText(Path.Combine(_repo.RepositoryPath, "dirty_tracked.txt"), "Dirty modified content");
        _repo.CreateFile("untracked_file.tmp", "Untracked trash");

        var statusBefore = await _runner.ExecuteAsync(_repo.RepositoryPath, "status --porcelain");
        Assert.NotEmpty(statusBefore.StandardOutput.Trim());

        // Act
        await _manager.RevertOrCleanAsync(_workspace);

        // Assert: working tree is clean
        var statusAfter = await _runner.ExecuteAsync(_repo.RepositoryPath, "status --porcelain");
        Assert.Empty(statusAfter.StandardOutput.Trim());
        Assert.False(File.Exists(Path.Combine(_repo.RepositoryPath, "untracked_file.tmp")));
        Assert.Equal("Original content", File.ReadAllText(Path.Combine(_repo.RepositoryPath, "dirty_tracked.txt")));
    }

    public void Dispose()
    {
        _repo.Dispose();
    }
}
