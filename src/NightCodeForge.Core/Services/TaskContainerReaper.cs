using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;

namespace NightCodeForge.Core.Services;

/// <summary>
/// Reaps per-task sandbox containers whose task no longer exists or reached a terminal state (Closed / Rejected).
/// Containers of live tasks are left intact (their mounts are validated when execution resumes).
/// </summary>
public static class TaskContainerReaper
{
    /// <summary>
    /// Removes orphaned task containers of the workspace and returns the number of reaped containers.
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
        if (containerTaskIds.Count == 0)
        {
            return 0;
        }

        var tasks = (await taskRepository.GetByWorkspaceIdAsync(workspace.Id, cancellationToken))
            .ToDictionary(t => t.Id);

        var reaped = 0;
        foreach (var taskId in containerTaskIds)
        {
            TaskOverallStatus? status = tasks.TryGetValue(taskId, out var task) ? task.Status : null;
            if (status is not null and
                not TaskOverallStatus.Closed and
                not TaskOverallStatus.Rejected and
                not TaskOverallStatus.Cancelled)
            {
                continue;
            }

            var containerName = !string.IsNullOrWhiteSpace(workspace.ContainerName)
                ? $"{workspace.ContainerName}-task-{taskId}"
                : $"ncf-sandbox-{workspace.Id}-task-{taskId}";

            await environmentProvider.CleanupTaskContainerAsync(workspace, taskId, cancellationToken);
            logger.LogInformation("Reaped orphaned container {Container} for terminal task #{TaskId} (Status: {Status}).",
                containerName, taskId, status?.ToString() ?? "Missing");
            reaped++;
        }

        return reaped;
    }
}
