using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Infrastructure.Git;

namespace NightCodeForge.Infrastructure.Tests.Git;

public class GitMirrorSyncServiceTests : IDisposable
{
    private readonly TestGitRepository _hostRepo;
    private readonly string _mirrorDir;
    private readonly GitCliRunner _runner;
    private readonly TestAlertNotifier _alertNotifier;
    private readonly GitMirrorSyncService _syncService;
    private readonly Workspace _workspace;

    public GitMirrorSyncServiceTests()
    {
        _hostRepo = new TestGitRepository("main");
        _hostRepo.CreateFile("README.md", "# Host Repository");
        _hostRepo.CommitAll("Initial commit on host");

        _mirrorDir = Path.Combine(Path.GetTempPath(), "ncf_mirror_" + Guid.NewGuid().ToString("N"));
        _runner = new GitCliRunner();
        _alertNotifier = new TestAlertNotifier();
        _syncService = new GitMirrorSyncService(_runner, _alertNotifier);

        _workspace = new Workspace
        {
            Id = 99,
            Name = "Mirror Test Workspace",
            RepositoryPath = _hostRepo.RepositoryPath,
            DefaultBaseBranch = "main"
        };
    }

    [Fact]
    public async Task ConfigureHostRepositoryAsync_SetsUpdateInsteadConfig()
    {
        // Act
        await _syncService.ConfigureHostRepositoryAsync(_hostRepo.RepositoryPath);

        // Assert
        var configResult = await _runner.ExecuteAsync(_hostRepo.RepositoryPath, ["config", "receive.denyCurrentBranch"]);
        Assert.Equal("updateInstead", configResult.StandardOutput.Trim());
    }

    [Fact]
    public async Task InitializeMirrorAsync_ClonesRepo_AndConfiguresHostOriginRemote()
    {
        // Act
        var mirrorPath = await _syncService.InitializeMirrorAsync(_workspace, _mirrorDir);

        // Assert
        Assert.Equal(_mirrorDir, mirrorPath);
        Assert.Equal(_mirrorDir, _workspace.Ext4MirrorPath);
        Assert.True(Directory.Exists(_mirrorDir));

        // Verify host-origin remote is configured
        var remoteResult = await _runner.ExecuteAsync(_mirrorDir, ["remote", "get-url", "host-origin"]);
        Assert.Equal(_hostRepo.RepositoryPath, remoteResult.StandardOutput.Trim());

        // Verify host config was applied
        var configResult = await _runner.ExecuteAsync(_hostRepo.RepositoryPath, ["config", "receive.denyCurrentBranch"]);
        Assert.Equal("updateInstead", configResult.StandardOutput.Trim());
    }

    [Fact]
    public async Task FetchFromHostAsync_FetchesNewCommitsFromHostOrigin()
    {
        // Arrange
        await _syncService.InitializeMirrorAsync(_workspace, _mirrorDir);

        // Host makes a new commit
        _hostRepo.CreateFile("host_update.txt", "New commit on host");
        _hostRepo.CommitAll("Host update commit");

        // Act: Fetch from host
        await _syncService.PullFromHostAsync(_mirrorDir, "main");

        // Assert: host-origin/main on mirror contains the new commit
        var logResult = await _runner.ExecuteAsync(_mirrorDir, ["log", "host-origin/main", "-1", "--pretty=%B"]);
        Assert.Contains("Host update commit", logResult.StandardOutput);
    }

    [Fact]
    public async Task PushToHostAsync_CleanHostWorkingTree_PushesSuccessfullyToCheckedOutBranch()
    {
        // Arrange
        await _syncService.InitializeMirrorAsync(_workspace, _mirrorDir);

        // Host creates and checks out feature/task-301
        await _runner.ExecuteAsync(_hostRepo.RepositoryPath, ["checkout", "-B", "feature/task-301", "main"]);

        // Mirror also creates feature/task-301 and makes a commit
        await _runner.ExecuteAsync(_mirrorDir, ["checkout", "-B", "feature/task-301"]);
        File.WriteAllText(Path.Combine(_mirrorDir, "agent_solution.cs"), "public class Solution {}");
        await _runner.ExecuteAsync(_mirrorDir, ["add", "-A"]);
        await _runner.ExecuteAsync(_mirrorDir, ["commit", "-m", "Agent commit from mirror", "--no-verify"]);

        // Act: Push to host
        var result = await _syncService.PushToHostAsync(_mirrorDir, 301);

        // Assert
        Assert.True(result.Success);
        Assert.False(result.FallbackUsed);
        Assert.Equal("feature/task-301", result.BranchPushed);
        Assert.Null(result.WarningMessage);
        Assert.Empty(_alertNotifier.Alerts);

        // Host working tree should have been updated via updateInstead
        Assert.True(File.Exists(Path.Combine(_hostRepo.RepositoryPath, "agent_solution.cs")));
        var hostLog = await _runner.ExecuteAsync(_hostRepo.RepositoryPath, ["log", "-1", "--pretty=%B"]);
        Assert.Contains("Agent commit from mirror", hostLog.StandardOutput);
    }

    [Fact]
    public async Task PushToHostAsync_DirtyHostWorkingTree_TriggersFallbackToNcfBranchAndAlert()
    {
        // Arrange
        await _syncService.InitializeMirrorAsync(_workspace, _mirrorDir);

        // Host creates tracked file and branch
        _hostRepo.CreateFile("shared_model.cs", "public class Model { public int V1; }");
        _hostRepo.CommitAll("Add shared model");

        // Host checks out feature/task-302
        await _runner.ExecuteAsync(_hostRepo.RepositoryPath, ["checkout", "-B", "feature/task-302"]);

        // Mirror checks out feature/task-302 and commits changes to shared_model.cs
        await _runner.ExecuteAsync(_mirrorDir, ["fetch", "host-origin", "feature/task-302"]);
        await _runner.ExecuteAsync(_mirrorDir, ["checkout", "-B", "feature/task-302", "host-origin/feature/task-302"]);
        File.WriteAllText(Path.Combine(_mirrorDir, "shared_model.cs"), "public class Model { public int V1; public int V2; }");
        await _runner.ExecuteAsync(_mirrorDir, ["add", "-A"]);
        await _runner.ExecuteAsync(_mirrorDir, ["commit", "-m", "Agent modified shared_model", "--no-verify"]);

        // NOW developer introduces UNCOMMITTED changes to shared_model.cs on host (dirty working tree!)
        File.WriteAllText(Path.Combine(_hostRepo.RepositoryPath, "shared_model.cs"), "public class Model { /* DIRTY DEVELOPER EDITS */ }");

        // Act: Mirror attempts to push to host
        var result = await _syncService.PushToHostAsync(_mirrorDir, 302);

        // Assert: Push must catch updateInstead rejection and fallback to feature/task-302-ncf
        Assert.True(result.Success);
        Assert.True(result.FallbackUsed);
        Assert.Equal("feature/task-302-ncf", result.BranchPushed);
        Assert.NotNull(result.WarningMessage);
        Assert.Contains("feature/task-302-ncf", result.WarningMessage);

        // Verify alert was raised
        Assert.Single(_alertNotifier.Alerts);
        Assert.Contains("feature/task-302-ncf", _alertNotifier.Alerts[0].Message);

        // Verify that host now has the fallback branch refs/heads/feature/task-302-ncf with agent's commit
        var hostBranchLog = await _runner.ExecuteAsync(_hostRepo.RepositoryPath, ["log", "feature/task-302-ncf", "-1", "--pretty=%B"]);
        Assert.Contains("Agent modified shared_model", hostBranchLog.StandardOutput);

        // Developer's uncommitted edits on host remain intact and were NOT overwritten
        Assert.Contains("DIRTY DEVELOPER EDITS", File.ReadAllText(Path.Combine(_hostRepo.RepositoryPath, "shared_model.cs")));
    }

    [Fact]
    public async Task PushBaseBranchToHostAsync_CleanWorkingTree_PushesBaseBranchDirectly()
    {
        // Arrange
        await _syncService.InitializeMirrorAsync(_workspace, _mirrorDir);

        // Mirror commits a new squash change on main
        await _runner.ExecuteAsync(_mirrorDir, ["checkout", "main"]);
        var featureFile = Path.Combine(_mirrorDir, "squash_feature.txt");
        await File.WriteAllTextAsync(featureFile, "Squashed feature implementation");
        await _runner.ExecuteAsync(_mirrorDir, ["add", "-A"]);
        await _runner.ExecuteAsync(_mirrorDir, ["commit", "-m", "Squash: ForgeTask #500 feature complete", "--no-verify"]);

        // Act
        var result = await _syncService.PushBaseBranchToHostAsync(_mirrorDir, "main");

        // Assert
        Assert.True(result.Success);
        Assert.False(result.FallbackUsed);
        Assert.Equal("main", result.BranchPushed);
        Assert.Null(result.WarningMessage);

        // Verify host repo main branch received commit
        var hostLog = await _runner.ExecuteAsync(_hostRepo.RepositoryPath, ["log", "main", "-1", "--pretty=%B"]);
        Assert.Contains("Squash: ForgeTask #500 feature complete", hostLog.StandardOutput);
    }

    [Fact]
    public async Task PushBaseBranchToHostAsync_DirtyWorkingTreeOnHost_FallsBackToNcfBranchAndAlerts()
    {
        // Arrange
        await _syncService.InitializeMirrorAsync(_workspace, _mirrorDir);

        // Create tracked file on host and mirror
        _hostRepo.CreateFile("base_tracked.txt", "Original base line");
        _hostRepo.CommitAll("Add base tracked file");

        // Fetch to mirror and make commit
        await _runner.ExecuteAsync(_mirrorDir, ["fetch", "host-origin", "main"]);
        await _runner.ExecuteAsync(_mirrorDir, ["checkout", "main"]);
        await _runner.ExecuteAsync(_mirrorDir, ["reset", "--hard", "host-origin/main"]);
        await File.AppendAllTextAsync(Path.Combine(_mirrorDir, "base_tracked.txt"), "\nAgent added line");
        await _runner.ExecuteAsync(_mirrorDir, ["add", "-A"]);
        await _runner.ExecuteAsync(_mirrorDir, ["commit", "-m", "Agent base branch update", "--no-verify"]);

        // Introduce uncommitted edits on host main
        await File.AppendAllTextAsync(Path.Combine(_hostRepo.RepositoryPath, "base_tracked.txt"), "\nDeveloper dirty edit on main");

        // Act
        var result = await _syncService.PushBaseBranchToHostAsync(_mirrorDir, "main");

        // Assert
        Assert.True(result.Success);
        Assert.True(result.FallbackUsed);
        Assert.Equal("main-ncf", result.BranchPushed);
        Assert.NotNull(result.WarningMessage);
        Assert.Contains("main-ncf", result.WarningMessage);

        // Verify alert was sent
        Assert.Single(_alertNotifier.Alerts);
        Assert.Contains("main-ncf", _alertNotifier.Alerts[0].Message);

        // Verify fallback branch exists on host with agent's commit
        var hostBranchLog = await _runner.ExecuteAsync(_hostRepo.RepositoryPath, ["log", "main-ncf", "-1", "--pretty=%B"]);
        Assert.Contains("Agent base branch update", hostBranchLog.StandardOutput);
    }

    public void Dispose()
    {
        _hostRepo.Dispose();
        TestGitRepository.DeleteDirectorySafe(_mirrorDir);
    }

    private class TestAlertNotifier : IGitAlertNotifier
    {
        public List<(string Title, string Message)> Alerts { get; } = new();

        public Task NotifyAlertAsync(string title, string message, CancellationToken cancellationToken = default)
        {
            Alerts.Add((title, message));
            return Task.CompletedTask;
        }
    }
}
