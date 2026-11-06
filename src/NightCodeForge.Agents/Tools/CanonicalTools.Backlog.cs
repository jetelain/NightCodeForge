using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;

namespace NightCodeForge.Agents.Tools;

public partial class CanonicalTools
{
    /// <summary>
    /// Host-level backlog mutation service backing get_task, edit_task, set_task_priority, rework_priorities and
    /// remove_task. Attached by ideation only; the tools fail fast when it is missing.
    /// </summary>
    public IBacklogMutationToolService? BacklogMutationService { get; set; }

    private (IBacklogMutationToolService Service, BacklogToolScope Scope) RequireBacklogScope(string toolName)
    {
        if (BacklogMutationService == null)
        {
            throw new InvalidOperationException($"Tool '{toolName}' is not available in this session: no backlog mutation service is attached.");
        }

        if (!_workspaceId.HasValue)
        {
            throw new InvalidOperationException($"Tool '{toolName}' requires a workspace-scoped session.");
        }

        return (BacklogMutationService, new BacklogToolScope(_workspaceId.Value, _workspaceRoot));
    }

    private static async Task<string> ExecuteBacklogActionAsync(Func<Task<string>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception ex) when (ex is TaskCannotBeModifiedException or TaskNotFoundException or CircularDependencyException or InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            return $"Error: {ex.Message}";
        }
    }

    /// <summary>get_task: fetches the complete details of a backlog task on demand.</summary>
    public Task<string> GetTaskAsync(int taskId, CancellationToken cancellationToken = default)
    {
        var (service, scope) = RequireBacklogScope(CanonicalToolNames.GetTask);
        return ExecuteBacklogActionAsync(() => service.GetTaskAsync(scope, taskId, cancellationToken));
    }

    /// <summary>edit_task: updates an unstarted backlog task (null arguments are left unchanged).</summary>
    public Task<string> EditTaskAsync(
        int taskId,
        string? title = null,
        string? description = null,
        int? priority = null,
        IReadOnlyList<int>? dependsOnIds = null,
        string? mergeMode = null,
        string? baseBranch = null,
        CancellationToken cancellationToken = default)
    {
        var (service, scope) = RequireBacklogScope(CanonicalToolNames.EditTask);
        return ExecuteBacklogActionAsync(() => service.EditTaskAsync(
            scope,
            new TaskEditRequest(taskId, title, description, priority, dependsOnIds, mergeMode, baseBranch),
            cancellationToken));
    }

    /// <summary>set_task_priority: changes the priority tier of an unstarted backlog task.</summary>
    public Task<string> SetTaskPriorityAsync(int taskId, int priority, CancellationToken cancellationToken = default)
    {
        var (service, scope) = RequireBacklogScope(CanonicalToolNames.SetTaskPriority);
        return ExecuteBacklogActionAsync(() => service.SetTaskPriorityAsync(scope, taskId, priority, cancellationToken));
    }

    /// <summary>rework_priorities: changes the priority tier of several unstarted backlog tasks at once.</summary>
    public Task<string> ReworkPrioritiesAsync(IReadOnlyList<TaskPriorityUpdate> updates, CancellationToken cancellationToken = default)
    {
        var (service, scope) = RequireBacklogScope(CanonicalToolNames.ReworkPriorities);
        return ExecuteBacklogActionAsync(() => service.ReworkPrioritiesAsync(scope, updates, cancellationToken));
    }

    /// <summary>remove_task: cancels an unstarted backlog task that has no active dependents.</summary>
    public Task<string> RemoveTaskAsync(int taskId, string? reason = null, CancellationToken cancellationToken = default)
    {
        var (service, scope) = RequireBacklogScope(CanonicalToolNames.RemoveTask);
        return ExecuteBacklogActionAsync(() => service.RemoveTaskAsync(scope, taskId, reason, cancellationToken));
    }
}

