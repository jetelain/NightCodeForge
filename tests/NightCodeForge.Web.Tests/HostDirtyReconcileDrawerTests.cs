using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Web.Components.GitSync;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class HostDirtyReconcileDrawerTests : BunitContext
{
    private readonly WorkspaceStateService _workspaceState;
    private readonly ToastService _toastService;
    private readonly MockGitSyncService _gitSyncService;

    public HostDirtyReconcileDrawerTests()
    {
        _workspaceState = new WorkspaceStateService();
        _toastService = TestServices.CreateToastService();
        _gitSyncService = new MockGitSyncService();

        Services.AddSingleton(_workspaceState);
        Services.AddSingleton(_toastService);
        Services.AddSingleton<IGitSyncService>(_gitSyncService);
    }

    [Fact]
    public void RendersDrawer_WithRescueBranchNameAndStrategies()
    {
        var alert = new HostDirtyAlertEvent(22, "feature/task-22", DateTime.UtcNow);

        var cut = Render<HostDirtyReconcileDrawer>(parameters => parameters
            .Add(p => p.Alert, alert));

        Assert.NotNull(cut.Find("#host-dirty-reconcile-drawer"));
        Assert.Equal("feature/task-22-ncf", cut.Find("#rescue-branch-name").TextContent);
        Assert.Contains("git merge feature/task-22-ncf", cut.Find("#manual-merge-command").TextContent);

        Assert.NotNull(cut.Find("#strategy-stash-card"));
        Assert.NotNull(cut.Find("#strategy-manual-card"));
        Assert.NotNull(cut.Find("#strategy-dismiss-card"));
    }

    [Fact]
    public async Task WhenStashAndMergeClicked_ExecutesReconcileAndInvokesOnResolved()
    {
        var workspace = new Workspace
        {
            Id = 10,
            Name = "Active WS",
            RepositoryPath = @"C:\repos\active"
        };
        _workspaceState.SetActiveWorkspace(workspace);

        var alert = new HostDirtyAlertEvent(23, "feature/task-23", DateTime.UtcNow);
        var resolvedFired = false;

        var cut = Render<HostDirtyReconcileDrawer>(parameters => parameters
            .Add(p => p.Alert, alert)
            .Add(p => p.OnResolved, () => resolvedFired = true));

        var stashBtn = cut.Find("#strategy-stash-and-merge-btn");
        await stashBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.True(resolvedFired);
        Assert.Equal(23, _gitSyncService.LastReconciledTaskId);
        Assert.Equal("feature/task-23", _gitSyncService.LastReconciledBranchName);
        Assert.Equal("stash-and-merge", _gitSyncService.LastReconciledStrategy);
    }

    [Fact]
    public async Task WhenCopyCommandClicked_ShowsToastWithMergeCommand()
    {
        var alert = new HostDirtyAlertEvent(24, "feature/task-24", DateTime.UtcNow);

        var cut = Render<HostDirtyReconcileDrawer>(parameters => parameters
            .Add(p => p.Alert, alert));

        var copyBtn = cut.Find("#copy-merge-command-btn");
        await copyBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        var lastToast = _toastService.Toasts.LastOrDefault();
        Assert.NotNull(lastToast);
        Assert.Equal("Copied to Clipboard", lastToast.Title);
        Assert.Contains("git merge feature/task-24-ncf", lastToast.Message);
    }

    [Fact]
    public async Task WhenDismissClicked_InvokesOnResolved()
    {
        var alert = new HostDirtyAlertEvent(25, "feature/task-25", DateTime.UtcNow);
        var resolvedFired = false;

        var cut = Render<HostDirtyReconcileDrawer>(parameters => parameters
            .Add(p => p.Alert, alert)
            .Add(p => p.OnResolved, () => resolvedFired = true));

        var dismissBtn = cut.Find("#strategy-dismiss-btn");
        await dismissBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.True(resolvedFired);
    }

    [Fact]
    public async Task WhenCloseClicked_InvokesOnClosed()
    {
        var alert = new HostDirtyAlertEvent(26, "feature/task-26", DateTime.UtcNow);
        var closedFired = false;

        var cut = Render<HostDirtyReconcileDrawer>(parameters => parameters
            .Add(p => p.Alert, alert)
            .Add(p => p.OnClosed, () => closedFired = true));

        var closeBtn = cut.Find("#close-dirty-drawer-btn");
        await closeBtn.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.True(closedFired);
    }

    private class MockGitSyncService : IGitSyncService
    {
        public int? LastReconciledTaskId { get; private set; }
        public string? LastReconciledBranchName { get; private set; }
        public string? LastReconciledStrategy { get; private set; }

        public Task<MirrorStatusSnapshot> GetMirrorStatusAsync(Workspace workspace, CancellationToken cancellationToken = default) =>
            Task.FromResult(new MirrorStatusSnapshot(workspace.Id, "", "", "main", 0, 0, null, false));

        public Task<List<WorktreeSnapshot>> GetWorktreesAsync(Workspace workspace, CancellationToken cancellationToken = default) =>
            Task.FromResult(new List<WorktreeSnapshot>());

        public Task PruneWorktreesAsync(Workspace workspace, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<GitSyncResult> ReconcileHostBranchAsync(Workspace workspace, int taskId, string branchName, string strategy, CancellationToken cancellationToken = default)
        {
            LastReconciledTaskId = taskId;
            LastReconciledBranchName = branchName;
            LastReconciledStrategy = strategy;
            return Task.FromResult(new GitSyncResult(true, false, branchName, null));
        }

        public void RecordFetch(int workspaceId) { }
    }
}
