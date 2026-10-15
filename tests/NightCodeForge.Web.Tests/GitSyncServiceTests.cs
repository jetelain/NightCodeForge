using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class GitSyncServiceTests
{
    private readonly MockGitCliRunner _gitCliRunner;
    private readonly MockGitWorkspaceManager _gitWorkspaceManager;
    private readonly MockGitMirrorSyncService _mirrorSyncService;
    private readonly InMemoryTaskRepository _taskRepository;
    private readonly GitSyncService _service;

    public GitSyncServiceTests()
    {
        _gitCliRunner = new MockGitCliRunner();
        _gitWorkspaceManager = new MockGitWorkspaceManager();
        _mirrorSyncService = new MockGitMirrorSyncService();
        _taskRepository = new InMemoryTaskRepository();

        _service = new GitSyncService(
            _gitCliRunner,
            _gitWorkspaceManager,
            _mirrorSyncService,
            _taskRepository);
    }

    [Fact]
    public async Task GetMirrorStatusAsync_ReturnsSnapshotWithAheadBehindAndDirtyState()
    {
        var tempHost = Path.Combine(Path.GetTempPath(), "ncf_test_host_" + Guid.NewGuid().ToString("N"));
        var tempMirror = Path.Combine(Path.GetTempPath(), "ncf_test_mirror_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempHost);
        Directory.CreateDirectory(tempMirror);

        try
        {
            var ws = new Workspace
            {
                Id = 11,
                Name = "Test Workspace",
                RepositoryPath = tempHost,
                Ext4MirrorPath = tempMirror,
                DefaultBaseBranch = "main"
            };

            _gitCliRunner.Outputs[("status", "--porcelain")] = " M modified.txt\n";
            _gitCliRunner.Outputs[("remote", "get-url")] = "file://" + tempHost;
            _gitCliRunner.Outputs[("rev-list", "ahead")] = "4\n";
            _gitCliRunner.Outputs[("rev-list", "behind")] = "2\n";

            _service.RecordFetch(ws.Id);

            var status = await _service.GetMirrorStatusAsync(ws);

            Assert.Equal(11, status.WorkspaceId);
            Assert.Equal(tempMirror, status.MirrorPath);
            Assert.Equal(tempHost, status.HostRepoPath);
            Assert.Equal("main", status.BaseBranch);
            Assert.True(status.IsHostDirty);
            Assert.Equal(4, status.CommitsAhead);
            Assert.Equal(2, status.CommitsBehind);
            Assert.NotNull(status.LastFetchedAt);
        }
        finally
        {
            try { Directory.Delete(tempHost, true); } catch { }
            try { Directory.Delete(tempMirror, true); } catch { }
        }
    }

    [Fact]
    public async Task GetWorktreesAsync_ParsesPorcelainOutputAndMatchesTaskStatus()
    {
        var tempMirror = Path.Combine(Path.GetTempPath(), "ncf_test_wt_mirror_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempMirror);

        try
        {
            var ws = new Workspace
            {
                Id = 12,
                Name = "Test WS",
                RepositoryPath = tempMirror,
                Ext4MirrorPath = tempMirror,
                DefaultBaseBranch = "main"
            };

            await _taskRepository.CreateAsync(new ForgeTask
            {
                Id = 301,
                WorkspaceId = 12,
                Title = "Active Task",
                Status = TaskOverallStatus.Open
            });

            await _taskRepository.CreateAsync(new ForgeTask
            {
                Id = 302,
                WorkspaceId = 12,
                Title = "Closed Task",
                Status = TaskOverallStatus.Closed
            });

            var porcelain = string.Join("\n",
                "worktree /home/user/.nightcodeforge/wks/12/clone",
                "HEAD abcdef",
                "branch refs/heads/main",
                "",
                "worktree /home/user/.nightcodeforge/wks/12/task-301",
                "HEAD 111111",
                "branch refs/heads/feature/task-301",
                "",
                "worktree /home/user/.nightcodeforge/wks/12/task-302",
                "HEAD 222222",
                "branch refs/heads/feature/task-302",
                "");

            _gitCliRunner.Outputs[("worktree", "list")] = porcelain;

            var worktrees = await _service.GetWorktreesAsync(ws);

            Assert.Equal(2, worktrees.Count);

            var wt1 = worktrees.FirstOrDefault(w => w.TaskId == 301);
            Assert.NotNull(wt1);
            Assert.Equal("feature/task-301", wt1.BranchName);
            Assert.True(wt1.IsActive);

            var wt2 = worktrees.FirstOrDefault(w => w.TaskId == 302);
            Assert.NotNull(wt2);
            Assert.Equal("feature/task-302", wt2.BranchName);
            Assert.False(wt2.IsActive);
        }
        finally
        {
            try { Directory.Delete(tempMirror, true); } catch { }
        }
    }

    [Fact]
    public async Task ReconcileHostBranchAsync_StashAndMerge_ExecutesStashCheckoutAndMerge()
    {
        var tempHost = Path.Combine(Path.GetTempPath(), "ncf_test_reconcile_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempHost);

        try
        {
            var ws = new Workspace
            {
                Id = 13,
                Name = "Reconcile WS",
                RepositoryPath = tempHost
            };

            var result = await _service.ReconcileHostBranchAsync(ws, 401, "feature/task-401", "stash-and-merge");

            Assert.True(result.Success);
            Assert.False(result.FallbackUsed);
            Assert.Equal("feature/task-401", result.BranchPushed);

            // Verify CLI invocations
            Assert.Contains(_gitCliRunner.ExecutedCommands, cmd => cmd.Args.Contains("stash"));
            Assert.Contains(_gitCliRunner.ExecutedCommands, cmd => cmd.Args.Contains("checkout") && cmd.Args.Contains("feature/task-401"));
            Assert.Contains(_gitCliRunner.ExecutedCommands, cmd => cmd.Args.Contains("merge") && cmd.Args.Contains("feature/task-401-ncf"));
        }
        finally
        {
            try { Directory.Delete(tempHost, true); } catch { }
        }
    }

    [Fact]
    public async Task ReconcileHostBranchAsync_ForceSync_ExecutesResetHard()
    {
        var tempHost = Path.Combine(Path.GetTempPath(), "ncf_test_forcesync_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempHost);

        try
        {
            var ws = new Workspace
            {
                Id = 14,
                Name = "ForceSync WS",
                RepositoryPath = tempHost
            };

            var result = await _service.ReconcileHostBranchAsync(ws, 402, "feature/task-402", "force-sync");

            Assert.True(result.Success);
            Assert.Contains(_gitCliRunner.ExecutedCommands, cmd => cmd.Args.Contains("reset") && cmd.Args.Contains("--hard") && cmd.Args.Contains("feature/task-402-ncf"));
        }
        finally
        {
            try { Directory.Delete(tempHost, true); } catch { }
        }
    }

    private class MockGitCliRunner : IGitCliRunner
    {
        public List<(string WorkingDirectory, List<string> Args)> ExecutedCommands { get; } = new();
        public Dictionary<(string, string), string> Outputs { get; } = new();

        public Task<GitCommandResult> ExecuteAsync(string workingDirectory, IEnumerable<string> argumentsList, int? timeoutSeconds = null, CancellationToken cancellationToken = default)
        {
            var arguments = argumentsList.ToList();
            ExecutedCommands.Add((workingDirectory, arguments));

            var stdout = "";

            if (arguments.Count == 1 && arguments[0] == "remote")
            {
                stdout = "host-origin\norigin\n";
            }
            else if (arguments.Contains("branch") && arguments.Contains("-r"))
            {
                stdout = "host-origin/main\n";
            }
            else if (arguments.Count >= 2 && Outputs.TryGetValue((arguments[0], arguments[1]), out var specificOutput))
            {
                stdout = specificOutput;
            }
            else if (arguments.Contains("rev-list") && arguments.Any(a => a.Contains("host-origin") && a.StartsWith("host-origin")))
            {
                stdout = Outputs.TryGetValue(("rev-list", "ahead"), out var a) ? a : "0\n";
            }
            else if (arguments.Contains("rev-list") && arguments.Any(a => a.Contains("host-origin") && a.EndsWith("..host-origin/main")))
            {
                stdout = Outputs.TryGetValue(("rev-list", "behind"), out var b) ? b : "0\n";
            }

            return Task.FromResult(new GitCommandResult(0, stdout, "", TimeSpan.Zero));
        }
    }

    private class MockGitWorkspaceManager : IGitWorkspaceManager
    {
        public Task EnsureRepositoryValidAsync(string repoPath, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CheckoutTaskBranchAsync(Workspace workspace, int taskId, string baseBranch, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string> CreateWipCheckpointAsync(Workspace workspace, int taskId, string stageName, string summary, CancellationToken cancellationToken = default) => Task.FromResult("abcdef1234567890abcdef1234567890abcdef12");
        public Task<string> GenerateDiffAsync(Workspace workspace, int taskId, string baseBranch, CancellationToken cancellationToken = default) => Task.FromResult("");
        public Task SquashAndMergeAsync(Workspace workspace, int taskId, string baseBranch, string commitMessage, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string> EnsureWorktreeAsync(Workspace workspace, int taskId, string baseBranch, CancellationToken cancellationToken = default) => Task.FromResult($"/wks/task-{taskId}");
        public Task RemoveWorktreeAsync(Workspace workspace, int taskId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public string GetWorktreePath(Workspace workspace, int taskId) => $"/wks/task-{taskId}";
        public Task RevertOrCleanAsync(Workspace workspace, int? taskId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private class MockGitMirrorSyncService : IGitMirrorSyncService
    {
        public Task ConfigureHostRepositoryAsync(string hostRepoPath, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string> InitializeMirrorAsync(Workspace workspace, string mirrorDirectory, CancellationToken cancellationToken = default) => Task.FromResult(mirrorDirectory);
        public Task PullFromHostAsync(string mirrorRepoPath, string baseBranch, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<GitSyncResult> PushToHostAsync(string mirrorRepoPath, int taskId, CancellationToken cancellationToken = default) => Task.FromResult(new GitSyncResult(true, false, $"feature/task-{taskId}", null));
        public Task<GitSyncResult> PushBaseBranchToHostAsync(string mirrorRepoPath, string baseBranch, CancellationToken cancellationToken = default) => Task.FromResult(new GitSyncResult(true, false, baseBranch, null));
    }
}
