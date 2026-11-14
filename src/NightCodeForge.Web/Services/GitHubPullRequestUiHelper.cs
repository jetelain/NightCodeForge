using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Security;

namespace NightCodeForge.Web.Services;

/// <summary>
/// Presentation helper for evaluating whether GitHub Pull Request delivery is available for a workspace
/// and formatting Pull Request action labels.
/// </summary>
public static class GitHubPullRequestUiHelper
{
    public static async Task<bool> IsPullRequestDeliveryAvailableAsync(
        IServiceProvider serviceProvider,
        Workspace? workspace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        var securityOptions = serviceProvider.GetService<IOptions<SecurityOptions>>()?.Value;
        if (securityOptions?.AirGappedMode == true)
        {
            return false;
        }

        if (workspace?.GitHubSettings is not { Enabled: true, PullRequestDeliveryEnabled: true })
        {
            return false;
        }

        var prDeliveryService = serviceProvider.GetService<IGitHubPullRequestDeliveryService>();
        if (prDeliveryService != null)
        {
            return await prDeliveryService.IsPullRequestDeliveryAvailableAsync(workspace, cancellationToken);
        }

        var tokenStore = serviceProvider.GetService<IGitHubTokenStore>();
        if (tokenStore != null)
        {
            return await tokenStore.HasTokenAsync(cancellationToken);
        }

        var reviewService = serviceProvider.GetService<ITaskReviewService>();
        if (reviewService != null && workspace.Id > 0)
        {
            return await reviewService.IsPullRequestDeliveryAvailableAsync(workspace.Id, cancellationToken);
        }

        return false;
    }

    public static async Task<bool> IsPullRequestDeliveryAvailableAsync(
        IServiceProvider serviceProvider,
        int workspaceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        if (workspaceId <= 0)
        {
            return false;
        }

        var workspaceState = serviceProvider.GetService<WorkspaceStateService>();
        var workspace = workspaceState?.ActiveWorkspace?.Id == workspaceId
            ? workspaceState.ActiveWorkspace
            : null;

        if (workspace == null)
        {
            var workspaceRepo = serviceProvider.GetService<IWorkspaceRepository>();
            if (workspaceRepo != null)
            {
                workspace = await workspaceRepo.GetByIdAsync(workspaceId, cancellationToken);
            }
        }

        return await IsPullRequestDeliveryAvailableAsync(serviceProvider, workspace, cancellationToken);
    }

    public static string GetOpenPullRequestLabel(int? pullRequestNumber) =>
        pullRequestNumber.HasValue
            ? $"Open GitHub Pull Request (#{pullRequestNumber.Value})"
            : "Open GitHub Pull Request";

    public static string GetCreateOrSyncPullRequestLabel(string? pullRequestUrl, int? pullRequestNumber) =>
        !string.IsNullOrWhiteSpace(pullRequestUrl) || pullRequestNumber.HasValue
            ? "Sync Pull Request"
            : "Create GitHub Pull Request";

    public static async Task MirrorTaskOpenedSafeAsync(
        IServiceProvider serviceProvider,
        ForgeTask task,
        int workspaceId,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(logger);

        if (workspaceId <= 0 || task.Status != TaskOverallStatus.Open)
        {
            return;
        }

        var workspace = await ResolveWorkspaceAsync(serviceProvider, workspaceId, cancellationToken);
        await MirrorTaskOpenedSafeAsync(serviceProvider, task, workspace, logger, cancellationToken);
    }

    public static async Task MirrorTaskOpenedSafeAsync(
        IServiceProvider serviceProvider,
        ForgeTask task,
        Workspace? workspace,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(logger);

        if (workspace == null || task.Status != TaskOverallStatus.Open)
        {
            return;
        }

        var mirrorService = serviceProvider.GetService<IGitHubTaskMirrorService>();
        if (mirrorService == null)
        {
            return;
        }

        try
        {
            await mirrorService.MirrorTaskOpenedAsync(task, workspace, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to mirror opened state to GitHub for Task #{TaskId}.", task.Id);
        }
    }

    private static async Task<Workspace?> ResolveWorkspaceAsync(
        IServiceProvider serviceProvider,
        int workspaceId,
        CancellationToken cancellationToken)
    {
        var workspaceState = serviceProvider.GetService<WorkspaceStateService>();
        if (workspaceState?.ActiveWorkspace?.Id == workspaceId)
        {
            return workspaceState.ActiveWorkspace;
        }

        var workspaceRepo = serviceProvider.GetService<IWorkspaceRepository>();
        return workspaceRepo != null
            ? await workspaceRepo.GetByIdAsync(workspaceId, cancellationToken)
            : null;
    }
}
