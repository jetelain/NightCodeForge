using NightCodeForge.Core.Domain;

namespace NightCodeForge.Agents.Tools;

public partial class CanonicalTools
{
    /// <summary>
    /// Host-level backlog mutation service backing get_task, edit_task, set_task_priority, rework_priorities and
    /// remove_task. Attached by ideation only; the tools fail fast when it is missing.
    /// </summary>
    public IBacklogMutationToolService? BacklogMutationService
    {
        get => BacklogModule.BacklogMutationService;
        set => BacklogModule.BacklogMutationService = value;
    }

    public bool IsBacklogMutationAvailable => BacklogModule.IsBacklogMutationAvailable;

    public string ProposeTasks(IReadOnlyList<TaskProposal> tasks) =>
        BacklogModule.ProposeTasks(tasks);

    public string ProposeTasks(
        object? tasks = null,
        string? title = null,
        string? description = null,
        int? priority = null) =>
        BacklogModule.ProposeTasks(tasks, title, description, priority);

    public string ProposeTasks(
        object? tasks = null,
        string? title = null,
        string? description = null,
        int? priority = null,
        IReadOnlyList<int>? dependsOnIds = null) =>
        BacklogModule.ProposeTasks(tasks, title, description, priority, dependsOnIds);

    public Task<string> ProposeTasksAsync(
        object? tasks = null,
        string? title = null,
        string? description = null,
        int? priority = null,
        IReadOnlyList<int>? dependsOnIds = null,
        CancellationToken cancellationToken = default) =>
        BacklogModule.ProposeTasksAsync(tasks, title, description, priority, dependsOnIds, cancellationToken);

    public Task<string> GetTaskAsync(int taskId, CancellationToken cancellationToken = default) =>
        BacklogModule.GetTaskAsync(taskId, cancellationToken);

    public Task<string> EditTaskAsync(
        int taskId,
        string? title = null,
        string? description = null,
        int? priority = null,
        IReadOnlyList<int>? dependsOnIds = null,
        string? mergeMode = null,
        string? baseBranch = null,
        CancellationToken cancellationToken = default) =>
        BacklogModule.EditTaskAsync(taskId, title, description, priority, dependsOnIds, mergeMode, baseBranch, cancellationToken);

    public Task<string> SetTaskPriorityAsync(int taskId, int priority, CancellationToken cancellationToken = default) =>
        BacklogModule.SetTaskPriorityAsync(taskId, priority, cancellationToken);

    public Task<string> ReworkPrioritiesAsync(IReadOnlyList<TaskPriorityUpdate> updates, CancellationToken cancellationToken = default) =>
        BacklogModule.ReworkPrioritiesAsync(updates, cancellationToken);

    public Task<string> RemoveTaskAsync(int taskId, string? reason = null, CancellationToken cancellationToken = default) =>
        BacklogModule.RemoveTaskAsync(taskId, reason, cancellationToken);
}
