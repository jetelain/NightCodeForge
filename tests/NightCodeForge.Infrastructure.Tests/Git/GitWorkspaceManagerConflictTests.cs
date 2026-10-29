using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Execution;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Infrastructure.Execution;
using NightCodeForge.Infrastructure.Git;
using Xunit;

namespace NightCodeForge.Infrastructure.Tests.Git;

public class GitWorkspaceManagerConflictTests : IDisposable
{
    private readonly TestGitRepository _repo;
    private readonly GitCliRunner _runner;
    private readonly GitWorkspaceManager _manager;
    private readonly Workspace _workspace;

    public GitWorkspaceManagerConflictTests()
    {
        var wslMeta = new Mock<IWslMetadataService>().Object;
        var pathResolver = new WorkspacePathResolver(wslMeta, Microsoft.Extensions.Options.Options.Create(new ExecutionEnvironmentProviderOptions()));
        _repo = new TestGitRepository("main");
        var processRunner = new SystemProcessRunner(NullLogger<SystemProcessRunner>.Instance);
        _runner = new GitCliRunner(
            processRunner,
            pathResolver,
            wslMeta,
            NullLogger<GitCliRunner>.Instance);

        var mirrorSyncMock = new Mock<IGitMirrorSyncService>();
        var lockProviderMock = new Mock<IWorkspaceLockProvider>();
        lockProviderMock.Setup(l => l.AcquireLockAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Mock<IDisposable>().Object);
        lockProviderMock.Setup(l => l.AcquireTaskLockAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Mock<IDisposable>().Object);

        _manager = new GitWorkspaceManager(
            _runner,
            pathResolver,
            wslMeta,
            mirrorSyncMock.Object,
            lockProviderMock.Object,
            processRunner,
            NullLogger<GitWorkspaceManager>.Instance);

        // Initial commit on main
        _repo.CreateFile("shared.txt", "line 1\nline 2\nline 3\n");
        _repo.CommitAll("Initial commit on main");

        _workspace = new Workspace
        {
            Id = 401,
            Name = "Conflict Test Workspace",
            RepositoryPath = _repo.RepositoryPath,
            DefaultBaseBranch = "main"
        };
    }

    [Fact]
    public async Task ValidateGitVersionAsync_SucceedsOnCurrentGitVersion()
    {
        // Git CLI installed in runner is >= 2.38
        await _manager.ValidateGitVersionAsync();
    }

    private GitWorkspaceManager CreateManagerWithGitVersionOutput(string versionOutput)
    {
        var wslMeta = new Mock<IWslMetadataService>().Object;
        var pathResolver = new WorkspacePathResolver(wslMeta, Microsoft.Extensions.Options.Options.Create(new ExecutionEnvironmentProviderOptions()));
        var runner = new Mock<IGitCliRunner>();
        runner.Setup(r => r.ExecuteAsync(It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitCommandResult(0, versionOutput, "", TimeSpan.Zero));
        return new GitWorkspaceManager(
            runner.Object,
            pathResolver,
            wslMeta,
            new Mock<IGitMirrorSyncService>().Object,
            new Mock<IWorkspaceLockProvider>().Object,
            new Mock<IProcessRunner>().Object,
            NullLogger<GitWorkspaceManager>.Instance);
    }

    [Fact]
    public async Task ValidateGitVersionAsync_UnparsableOutput_ThrowsInsteadOfSilentlyPassing()
    {
        var manager = CreateManagerWithGitVersionOutput("garbage output");
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ValidateGitVersionAsync());
    }

    [Fact]
    public async Task ValidateGitVersionAsync_OldGit_Throws()
    {
        var manager = CreateManagerWithGitVersionOutput("git version 2.30.1");
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ValidateGitVersionAsync());
    }


    [Fact]
    public async Task CheckMergeableAsync_CleanBranches_ReturnsIsMergeableTrue()
    {
        // Arrange
        var wtPath = await _manager.EnsureWorktreeAsync(_workspace, 501, "main");
        _repo.CreateFile("clean_feature.txt", "New feature content", wtPath);
        await _manager.CreateWipCheckpointAsync(_workspace, 501, "Stage3", "Clean feature commit");

        // Act
        var result = await _manager.CheckMergeableAsync(_workspace, 501, "main");

        // Assert
        Assert.True(result.IsMergeable);
        Assert.Empty(result.ConflictedFiles);
    }

    [Fact]
    public async Task CheckMergeableAsync_ConflictedBranches_ReturnsIsMergeableFalseWithConflictedFiles()
    {
        // Arrange
        var wtPath = await _manager.EnsureWorktreeAsync(_workspace, 502, "main");

        // Edit shared.txt in feature worktree
        _repo.CreateFile("shared.txt", "line 1\nfeature edit\nline 3\n", wtPath);
        await _manager.CreateWipCheckpointAsync(_workspace, 502, "Stage3", "Feature edit to shared.txt");

        // Advance main with conflicting edit
        _repo.CreateFile("shared.txt", "line 1\nmain conflicting edit\nline 3\n", _repo.RepositoryPath);
        _repo.CommitAll("Main conflicting edit to shared.txt");

        // Act
        var result = await _manager.CheckMergeableAsync(_workspace, 502, "main");

        // Assert
        Assert.False(result.IsMergeable);
        Assert.Contains("shared.txt", result.ConflictedFiles);
    }

    [Fact]
    public async Task SyncFromBaseBranchAsync_DirtyWorktree_ThrowsGitCommandException()
    {
        // Arrange
        var wtPath = await _manager.EnsureWorktreeAsync(_workspace, 503, "main");
        // Leave uncommitted edit in worktree
        File.WriteAllText(Path.Combine(wtPath, "shared.txt"), "dirty uncommitted edit");

        // Act & Assert
        await Assert.ThrowsAsync<GitCommandException>(() =>
            _manager.SyncFromBaseBranchAsync(_workspace, 503, "main"));
    }

    [Fact]
    public async Task SyncFromBaseBranchAsync_Conflict_LeavesMergeInProgressAndCanBeAborted()
    {
        // Arrange
        var wtPath = await _manager.EnsureWorktreeAsync(_workspace, 504, "main");
        _repo.CreateFile("shared.txt", "line 1\nfeature edit 504\nline 3\n", wtPath);
        await _manager.CreateWipCheckpointAsync(_workspace, 504, "Stage3", "Feature edit 504");

        _repo.CreateFile("shared.txt", "line 1\nmain edit 504\nline 3\n", _repo.RepositoryPath);
        _repo.CommitAll("Main edit 504");

        // Act
        var syncResult = await _manager.SyncFromBaseBranchAsync(_workspace, 504, "main");

        // Assert
        Assert.False(syncResult.Success);
        Assert.True(syncResult.HasConflict);
        Assert.Contains("shared.txt", syncResult.ConflictedFiles);

        // MERGE_HEAD should exist in the worktree
        var gitDirStatus = await _runner.ExecuteAsync(wtPath, ["rev-parse", "--verify", "MERGE_HEAD"]);
        Assert.Equal(0, gitDirStatus.ExitCode);

        // Conflict markers should include diff3 ancestor markers
        var conflictedContent = await File.ReadAllTextAsync(Path.Combine(wtPath, "shared.txt"));
        Assert.Contains(ConflictResolutionConstants.MarkerStart, conflictedContent);
        Assert.Contains(ConflictResolutionConstants.MarkerAncestor, conflictedContent);
        Assert.Contains(ConflictResolutionConstants.MarkerEnd, conflictedContent);

        // AbortMergeAsync resets cleanly
        await _manager.AbortMergeAsync(_workspace, 504);
        await Assert.ThrowsAsync<GitCommandException>(() =>
            _runner.ExecuteAsync(wtPath, ["rev-parse", "--verify", "MERGE_HEAD"]));
    }

    [Fact]
    public async Task SquashAndMergeAsync_ConflictRollback_PreservesUntrackedFilesAndWorktree()
    {
        // Arrange
        var wtPath = await _manager.EnsureWorktreeAsync(_workspace, 505, "main");
        _repo.CreateFile("shared.txt", "line 1\nfeature edit 505\nline 3\n", wtPath);
        await _manager.CreateWipCheckpointAsync(_workspace, 505, "Stage3", "Feature edit 505");

        // Advance main so it conflicts
        _repo.CreateFile("shared.txt", "line 1\nmain conflicting edit 505\nline 3\n", _repo.RepositoryPath);
        _repo.CommitAll("Main conflict 505");

        // Add untracked maintainer file in canonical repo that must not be deleted by clean
        var untrackedFile = Path.Combine(_repo.RepositoryPath, "maintainer_notes.txt");
        await File.WriteAllTextAsync(untrackedFile, "Do not delete me!");

        // Act & Assert
        var ex = await Assert.ThrowsAsync<MergeConflictException>(() =>
            _manager.SquashAndMergeAsync(_workspace, 505, "main", "Squash conflict test"));

        Assert.Contains("shared.txt", ex.ConflictedFiles);

        // Canonical repo: untracked file preserved
        Assert.True(File.Exists(untrackedFile));

        // Worktree preserved
        Assert.True(Directory.Exists(wtPath));

        // Canonical clone is clean (no unmerged entries)
        var status = await _runner.ExecuteAsync(_repo.RepositoryPath, ["status", "--porcelain"]);
        Assert.DoesNotContain("U ", status.StandardOutput);
    }

    [Fact]
    public async Task CheckMergeableAsync_GitError_ThrowsGitCommandException()
    {
        // Target an invalid non-existent base branch so git merge-tree fails with code 128
        await Assert.ThrowsAsync<GitCommandException>(() =>
            _manager.CheckMergeableAsync(_workspace, 501, "non-existent-base-branch-xyz"));
    }

    [Fact]
    public async Task SyncFromBaseBranchAsync_CleanSync_SucceedsWithCommitSha()
    {
        // Arrange
        var wtPath = await _manager.EnsureWorktreeAsync(_workspace, 506, "main");
        _repo.CreateFile("feature_file.txt", "Feature content 506", wtPath);
        await _manager.CreateWipCheckpointAsync(_workspace, 506, "Stage3", "Commit feature 506");

        // Advance main with non-conflicting change
        _repo.CreateFile("main_other_file.txt", "Main independent content 506", _repo.RepositoryPath);
        _repo.CommitAll("Main non-conflicting commit 506");

        // Act
        var syncResult = await _manager.SyncFromBaseBranchAsync(_workspace, 506, "main");

        // Assert
        Assert.True(syncResult.Success);
        Assert.False(syncResult.HasConflict);
        Assert.Empty(syncResult.ConflictedFiles);
        Assert.NotNull(syncResult.MergeCommitSha);

        // Verify content from both sides is present
        Assert.True(File.Exists(Path.Combine(wtPath, "main_other_file.txt")));
        Assert.True(File.Exists(Path.Combine(wtPath, "feature_file.txt")));
    }

    [Fact]
    public async Task SyncFromBaseBranchAsync_Idempotent_WhenAlreadyUpToDate_ReturnsSuccessWithUnchangedSha()
    {
        // Arrange
        var wtPath = await _manager.EnsureWorktreeAsync(_workspace, 507, "main");
        var rev1 = await _runner.ExecuteAsync(wtPath, ["rev-parse", "HEAD"]);
        var initialSha = rev1.StandardOutput.Trim();

        // Act: Sync when base has not advanced
        var syncResult = await _manager.SyncFromBaseBranchAsync(_workspace, 507, "main");

        // Assert
        Assert.True(syncResult.Success);
        Assert.False(syncResult.HasConflict);
        Assert.Equal(initialSha, syncResult.MergeCommitSha);
    }

    [Fact]
    public async Task SyncFromBaseBranchAsync_Resume_WhenMergeHeadAlreadyPresent_ReturnsConflictedFilesWithoutReRunningPull()
    {
        // Arrange
        var wtPath = await _manager.EnsureWorktreeAsync(_workspace, 508, "main");
        _repo.CreateFile("shared.txt", "line 1\nfeature edit 508\nline 3\n", wtPath);
        await _manager.CreateWipCheckpointAsync(_workspace, 508, "Stage3", "Feature edit 508");

        _repo.CreateFile("shared.txt", "line 1\nmain edit 508\nline 3\n", _repo.RepositoryPath);
        _repo.CommitAll("Main edit 508");

        // First sync triggers conflict and leaves MERGE_HEAD
        var firstResult = await _manager.SyncFromBaseBranchAsync(_workspace, 508, "main");
        Assert.True(firstResult.HasConflict);

        // Leave uncommitted changes (dirty worktree) which would normally fail clean-tree check
        // but because MERGE_HEAD exists, it is a resume and clean-tree check does not apply!
        var resumeResult = await _manager.SyncFromBaseBranchAsync(_workspace, 508, "main");

        // Assert
        Assert.False(resumeResult.Success);
        Assert.True(resumeResult.HasConflict);
        Assert.Contains("shared.txt", resumeResult.ConflictedFiles);

        // Cleanup
        await _manager.AbortMergeAsync(_workspace, 508);
    }

    public void Dispose()
    {
        _repo.Dispose();
    }
}
