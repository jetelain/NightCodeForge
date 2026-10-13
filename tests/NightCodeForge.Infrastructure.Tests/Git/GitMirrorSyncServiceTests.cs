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
        var configResult = await _runner.ExecuteAsync(_hostRepo.RepositoryPath, "config receive.denyCurrentBranch");
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
        var remoteResult = await _runner.ExecuteAsync(_mirrorDir, "remote get-url host-origin");
        Assert.Equal(_hostRepo.RepositoryPath, remoteResult.StandardOutput.Trim());

        // Verify host config was applied
        var configResult = await _runner.ExecuteAsync(_hostRepo.RepositoryPath, "config receive.denyCurrentBranch");
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
        await _syncService.FetchFromHostAsync(_mirrorDir, "main");

        // Assert: host-origin/main on mirror contains the new commit
        var logResult = await _runner.ExecuteAsync(_mirrorDir, "log host-origin/main -1 --pretty=%B");
        Assert.Contains("Host update commit", logResult.StandardOutput);
    }

    [Fact]
    public async Task PushToHostAsync_CleanHostWorkingTree_PushesSuccessfullyToCheckedOutBranch()
    {
        // Arrange
        await _syncService.InitializeMirrorAsync(_workspace, _mirrorDir);

        // Host creates and checks out feature/issue-301
        await _runner.ExecuteAsync(_hostRepo.RepositoryPath, "checkout -B feature/issue-301 main");

        // Mirror also creates feature/issue-301 and makes a commit
        await _runner.ExecuteAsync(_mirrorDir, "checkout -B feature/issue-301");
        File.WriteAllText(Path.Combine(_mirrorDir, "agent_solution.cs"), "public class Solution {}");
        await _runner.ExecuteAsync(_mirrorDir, "add -A");
        await _runner.ExecuteAsync(_mirrorDir, "commit -m \"Agent commit from mirror\" --no-verify");

        // Act: Push to host
        var result = await _syncService.PushToHostAsync(_mirrorDir, 301);

        // Assert
        Assert.True(result.Success);
        Assert.False(result.FallbackUsed);
        Assert.Equal("feature/issue-301", result.BranchPushed);
        Assert.Null(result.WarningMessage);
        Assert.Empty(_alertNotifier.Alerts);

        // Host working tree should have been updated via updateInstead
        Assert.True(File.Exists(Path.Combine(_hostRepo.RepositoryPath, "agent_solution.cs")));
        var hostLog = await _runner.ExecuteAsync(_hostRepo.RepositoryPath, "log -1 --pretty=%B");
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

        // Host checks out feature/issue-302
        await _runner.ExecuteAsync(_hostRepo.RepositoryPath, "checkout -B feature/issue-302");

        // Mirror checks out feature/issue-302 and commits changes to shared_model.cs
        await _runner.ExecuteAsync(_mirrorDir, "fetch host-origin feature/issue-302");
        await _runner.ExecuteAsync(_mirrorDir, "checkout -B feature/issue-302 host-origin/feature/issue-302");
        File.WriteAllText(Path.Combine(_mirrorDir, "shared_model.cs"), "public class Model { public int V1; public int V2; }");
        await _runner.ExecuteAsync(_mirrorDir, "add -A");
        await _runner.ExecuteAsync(_mirrorDir, "commit -m \"Agent modified shared_model\" --no-verify");

        // NOW developer introduces UNCOMMITTED changes to shared_model.cs on host (dirty working tree!)
        File.WriteAllText(Path.Combine(_hostRepo.RepositoryPath, "shared_model.cs"), "public class Model { /* DIRTY DEVELOPER EDITS */ }");

        // Act: Mirror attempts to push to host
        var result = await _syncService.PushToHostAsync(_mirrorDir, 302);

        // Assert: Push must catch updateInstead rejection and fallback to feature/issue-302-ncf
        Assert.True(result.Success);
        Assert.True(result.FallbackUsed);
        Assert.Equal("feature/issue-302-ncf", result.BranchPushed);
        Assert.NotNull(result.WarningMessage);
        Assert.Contains("feature/issue-302-ncf", result.WarningMessage);

        // Verify alert was raised
        Assert.Single(_alertNotifier.Alerts);
        Assert.Contains("feature/issue-302-ncf", _alertNotifier.Alerts[0].Message);

        // Verify that host now has the fallback branch refs/heads/feature/issue-302-ncf with agent's commit
        var hostBranchLog = await _runner.ExecuteAsync(_hostRepo.RepositoryPath, "log feature/issue-302-ncf -1 --pretty=%B");
        Assert.Contains("Agent modified shared_model", hostBranchLog.StandardOutput);

        // Developer's uncommitted edits on host remain intact and were NOT overwritten
        Assert.Contains("DIRTY DEVELOPER EDITS", File.ReadAllText(Path.Combine(_hostRepo.RepositoryPath, "shared_model.cs")));
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
