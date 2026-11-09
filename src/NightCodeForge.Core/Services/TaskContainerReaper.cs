using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;

namespace NightCodeForge.Core.Services;

/// <summary>
/// Reaps per-task sandbox containers whose task no longer exists, reached a terminal state (Closed / Rejected / Cancelled / Failed),
/// or reached an idle state awaiting human action (Ready, WaitingForHuman, DeferredByPause).
/// Containers of live tasks are left intact (their mounts are validated when execution resumes).
/// </summary>
public static class TaskContainerReaper
{
    public const int DefaultIdleWorkspaceContainerTimeoutMinutes = 30;
    public static readonly TimeSpan DefaultIdleWorkspaceContainerTimeout = TimeSpan.FromMinutes(DefaultIdleWorkspaceContainerTimeoutMinutes);

    /// <summary>
    /// Determines whether a task's per-task sandbox container should be reaped because the task is missing,
    /// in a terminal state, or idle awaiting human action.
    /// </summary>
    public static bool ShouldReapTaskContainer(ForgeTask? task) =>
        task == null
        || task.Status is TaskOverallStatus.Draft
            or TaskOverallStatus.Suggested
            or TaskOverallStatus.Closed
            or TaskOverallStatus.Rejected
            or TaskOverallStatus.Cancelled
            or TaskOverallStatus.Failed
            or TaskOverallStatus.Ready
        || task.IsWaitingForHuman
        || task.IsDeferredByPause;

    /// <summary>
    /// Removes orphaned or idle task containers of the workspace and evicts idle workspace-level containers.
    /// Returns the number of reaped task containers.
    /// </summary>
    public static async Task<int> ReapAsync(
        IExecutionEnvironmentProvider environmentProvider,
        ITaskRepository taskRepository,
        Workspace workspace,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environmentProvider);
        ArgumentNullException.ThrowIfNull(taskRepository);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(logger);

        var containerTaskIds = await environmentProvider.ListTaskContainerIdsAsync(workspace, cancellationToken);
        var reaped = 0;

        if (containerTaskIds.Count > 0)
        {
            var tasks = (await taskRepository.GetByWorkspaceIdAsync(workspace.Id, cancellationToken))
                .ToDictionary(t => t.Id);

            foreach (var taskId in containerTaskIds)
            {
                tasks.TryGetValue(taskId, out var task);
                if (!ShouldReapTaskContainer(task))
                {
                    continue;
                }

                var containerName = !string.IsNullOrWhiteSpace(workspace.ContainerName)
                    ? $"{workspace.ContainerName}-task-{taskId}"
                    : $"ncf-sandbox-{workspace.Id}-task-{taskId}";

                await environmentProvider.CleanupTaskContainerAsync(workspace, taskId, cancellationToken);
                logger.LogInformation("Reaped idle/terminal container {Container} for task #{TaskId} (Status: {Status}, WaitingForHuman: {Waiting}, Deferred: {Deferred}).",
                    containerName, taskId, task?.Status.ToString() ?? "Missing", task?.IsWaitingForHuman ?? false, task?.IsDeferredByPause ?? false);
                reaped++;
            }
        }

        try
        {
            await environmentProvider.EvictIdleWorkspaceContainersAsync(workspace, DefaultIdleWorkspaceContainerTimeout, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to evict idle workspace containers for workspace #{WorkspaceId}", workspace.Id);
        }

        return reaped;
    }
}
