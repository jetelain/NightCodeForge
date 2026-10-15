using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Web.Components.Pages;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class GitSyncPageTests : BunitContext
{
    private readonly WorkspaceStateService _workspaceState;
    private readonly OrchestratorHubClientConnection _hubConnection;
    private readonly ToastService _toastService;
    private readonly MockGitMirrorSyncService _mirrorSyncService;
    private readonly MockGitSyncService _gitSyncService;

    public GitSyncPageTests()
    {
        _workspaceState = new WorkspaceStateService();
        _hubConnection = new OrchestratorHubClientConnection("/hubs/orchestrator");
        _toastService = new ToastService();
        _mirrorSyncService = new MockGitMirrorSyncService();
        _gitSyncService = new MockGitSyncService();

        Services.AddSingleton(_workspaceState);
        Services.AddSingleton(_hubConnection);
        Services.AddSingleton(_toastService);
        Services.AddSingleton<IGitMirrorSyncService>(_mirrorSyncService);
        Services.AddSingleton<IGitSyncService>(_gitSyncService);
    }

    [Fact]
    public void WhenNoActiveWorkspace_RendersEmptyHero()
    {
        var cut = Render<GitSync>();

        Assert.NotEmpty(cut.FindAll(".workspace-empty-hero"));
        Assert.Empty(cut.FindAll("#mirror-status-card"));
        Assert.Empty(cut.FindAll("#worktrees-card"));
    }

    [Fact]
    public void WhenActiveWorkspace_RendersMirrorStatusAndWorktrees()
    {
        var workspace = new Workspace
        {
            Id = 1,
            Name = "Forge Core",
            RepositoryPath = @"C:\repos\forge-core",
            Ext4MirrorPath = "/home/user/.nightcodeforge/mirror/1",
            DefaultBaseBranch = "develop"
        };
        _workspaceState.SetActiveWorkspace(workspace);

        _gitSyncService.Snapshot = new MirrorStatusSnapshot(
            WorkspaceId: 1,
            MirrorPath: "/home/user/.nightcodeforge/mirror/1",
            HostOriginPath: @"C:\repos\forge-core",
            BaseBranch: "develop",
            CommitsAhead: 3,
            CommitsBehind: 1,
            LastFetchedAt: new DateTime(2026, 10, 15, 8, 30, 0, DateTimeKind.Utc),
            IsHostDirty: false);

        _gitSyncService.Worktrees = new List<WorktreeSnapshot>
        {
            new WorktreeSnapshot("/home/user/.nightcodeforge/wks/1/task-101", 101, "feature/task-101", true, DateTime.UtcNow),
            new WorktreeSnapshot("/home/user/.nightcodeforge/wks/1/task-102", 102, "feature/task-102", false, DateTime.UtcNow)
        };

        var cut = Render<GitSync>();

        Assert.NotNull(cut.Find("#mirror-status-card"));
        Assert.NotNull(cut.Find("#worktrees-card"));
        Assert.Equal("/home/user/.nightcodeforge/mirror/1", cut.Find("#mirror-path").TextContent);
        Assert.Equal(@"C:\repos\forge-core", cut.Find("#host-origin-path").TextContent);
        Assert.Equal("develop", cut.Find("#base-branch").TextContent);
        Assert.Contains("Ahead: 3", cut.Find("#divergence-ahead").TextContent);
        Assert.Contains("Behind: 1", cut.Find("#divergence-behind").TextContent);
        Assert.Contains("CLEAN", cut.Find("#host-tree-status").TextContent);

        // Check worktrees
        Assert.NotNull(cut.Find("#worktree-item-101"));
        Assert.Contains("ForgeTask #101", cut.Find("#worktree-item-101").TextContent);
        Assert.Contains("ACTIVE", cut.Find("#worktree-item-101").TextContent);

        Assert.NotNull(cut.Find("#worktree-item-102"));
        Assert.Contains("ForgeTask #102", cut.Find("#worktree-item-102").TextContent);
        Assert.Contains("IDLE", cut.Find("#worktree-item-102").TextContent);
    }

    [Fact]
    public void WhenHostDirtyAlertReceived_DisplaysBannerAndRescueBranch()
    {
        var workspace = new Workspace
        {
            Id = 2,
            Name = "Forge Repo",
            RepositoryPath = @"C:\repos\repo",
            Ext4MirrorPath = "/mirror/2",
            DefaultBaseBranch = "main"
        };
        _workspaceState.SetActiveWorkspace(workspace);

        var cut = Render<GitSync>();

        // Initially no dirty banner
        Assert.Empty(cut.FindAll("#dirty-alert-banner"));

        // Trigger SignalR Host Dirty Alert
        _hubConnection.TriggerHostDirtyAlert(42, "feature/task-42");

        // Verify banner appears
        var banner = cut.Find("#dirty-alert-banner");
        Assert.NotNull(banner);
        Assert.Contains("feature/task-42-ncf", cut.Find("#dirty-banner-desc").TextContent);
        Assert.Contains("Developer Host Working Tree is Dirty!", cut.Find("#dirty-banner-title").TextContent);
    }

    [Fact]
    public void WhenClickResolveReconcile_OpensReconcileDrawer()
    {
        var workspace = new Workspace { Id = 3, Name = "Repo", RepositoryPath = @"C:\repos\repo" };
        _workspaceState.SetActiveWorkspace(workspace);

        var cut = Render<GitSync>();

        _hubConnection.TriggerHostDirtyAlert(99, "feature/task-99");
        var openBtn = cut.Find("#btn-open-reconcile-drawer");
        openBtn.Click();

        // Drawer modal should now be present
        Assert.NotNull(cut.Find("#host-dirty-reconcile-drawer"));
        Assert.Contains("feature/task-99-ncf", cut.Find("#rescue-branch-name").TextContent);
    }

    [Fact]
    public async Task WhenFetchFromHostClicked_CallsFetchAndRefreshesStatus()
    {
        var workspace = new Workspace
        {
            Id = 4,
            Name = "Repo",
            RepositoryPath = @"C:\repos\repo",
            Ext4MirrorPath = "/mirror/4",
            DefaultBaseBranch = "main"
        };
        _workspaceState.SetActiveWorkspace(workspace);

        var cut = Render<GitSync>();

        var fetchBtn = cut.Find("#btn-fetch-host");
        await fetchBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Equal("/mirror/4", _mirrorSyncService.LastFetchedMirrorRepoPath);
        Assert.Equal("main", _mirrorSyncService.LastFetchedBaseBranch);
        Assert.Equal(1, _gitSyncService.RecordFetchCallCount);
    }

    [Fact]
    public async Task WhenPruneStaleClicked_CallsPruneWorktreesAsync()
    {
        var workspace = new Workspace { Id = 5, Name = "Repo", RepositoryPath = @"C:\repos\repo" };
        _workspaceState.SetActiveWorkspace(workspace);

        var cut = Render<GitSync>();

        var pruneBtn = cut.Find("#btn-prune-worktrees");
        await pruneBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Equal(1, _gitSyncService.PruneWorktreesCallCount);
    }

    private class MockGitMirrorSyncService : IGitMirrorSyncService
    {
        public string? LastFetchedMirrorRepoPath { get; private set; }
        public string? LastFetchedBaseBranch { get; private set; }

        public Task ConfigureHostRepositoryAsync(string hostRepoPath, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string> InitializeMirrorAsync(Workspace workspace, string mirrorDirectory, CancellationToken cancellationToken = default) => Task.FromResult(mirrorDirectory);
        public Task FetchFromHostAsync(string mirrorRepoPath, string baseBranch, CancellationToken cancellationToken = default)
        {
            LastFetchedMirrorRepoPath = mirrorRepoPath;
            LastFetchedBaseBranch = baseBranch;
            return Task.CompletedTask;
        }
        public Task<GitSyncResult> PushToHostAsync(string mirrorRepoPath, int taskId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GitSyncResult(true, false, $"feature/task-{taskId}", null));
        public Task<GitSyncResult> PushBaseBranchToHostAsync(string mirrorRepoPath, string baseBranch, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GitSyncResult(true, false, baseBranch, null));
    }

    private class MockGitSyncService : IGitSyncService
    {
        public MirrorStatusSnapshot? Snapshot { get; set; }
        public List<WorktreeSnapshot> Worktrees { get; set; } = new();
        public int PruneWorktreesCallCount { get; private set; }
        public int RecordFetchCallCount { get; private set; }

        public Task<MirrorStatusSnapshot> GetMirrorStatusAsync(Workspace workspace, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Snapshot ?? new MirrorStatusSnapshot(
                WorkspaceId: workspace.Id,
                MirrorPath: workspace.Ext4MirrorPath ?? "",
                HostOriginPath: workspace.RepositoryPath ?? "",
                BaseBranch: workspace.DefaultBaseBranch ?? "main",
                CommitsAhead: 0,
                CommitsBehind: 0,
                LastFetchedAt: null,
                IsHostDirty: false));
        }

        public Task<List<WorktreeSnapshot>> GetWorktreesAsync(Workspace workspace, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Worktrees);
        }

        public Task PruneWorktreesAsync(Workspace workspace, CancellationToken cancellationToken = default)
        {
            PruneWorktreesCallCount++;
            return Task.CompletedTask;
        }

        public Task<GitSyncResult> ReconcileHostBranchAsync(Workspace workspace, int taskId, string branchName, string strategy, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new GitSyncResult(true, false, branchName, null));
        }

        public void RecordFetch(int workspaceId)
        {
            RecordFetchCallCount++;
        }
    }
}
