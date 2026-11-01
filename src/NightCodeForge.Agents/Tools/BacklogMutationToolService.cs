using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Streaming;

namespace NightCodeForge.Agents.Tools;

/// <summary>Workspace a backlog mutation applies to, with the repository path used for markdown synchronization.</summary>
public sealed record BacklogToolScope(int WorkspaceId, string RepositoryPath);

/// <summary>Partial update of an unstarted task; null members are left untouched.</summary>
public sealed record TaskEditRequest(
    int TaskId,
    string? Title = null,
    string? Description = null,
    int? Priority = null,
    IReadOnlyList<int>? DependsOnIds = null,
    string? MergeMode = null,
    string? BaseBranch = null);

/// <summary>Single entry of a batched priority rework.</summary>
public sealed record TaskPriorityUpdate(int TaskId, int Priority);

/// <summary>
/// Host-level backlog tools of the ideation agent: scoped, audited task mutations that never touch the repository
/// source files (the ideation workspace stays read-only).
/// </summary>
public interface IBacklogMutationToolService
{
    Task<string> GetTaskAsync(BacklogToolScope scope, int taskId, CancellationToken cancellationToken);

    Task<string> EditTaskAsync(BacklogToolScope scope, TaskEditRequest request, CancellationToken cancellationToken);

    Task<string> SetTaskPriorityAsync(BacklogToolScope scope, int taskId, int priority, CancellationToken cancellationToken);

    Task<string> ReworkPrioritiesAsync(BacklogToolScope scope, IReadOnlyList<TaskPriorityUpdate> updates, CancellationToken cancellationToken);

    Task<string> RemoveTaskAsync(BacklogToolScope scope, int taskId, string? reason, CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed class BacklogMutationToolService : IBacklogMutationToolService
{
    /// <summary>Stage identifier used for the activity events broadcast by backlog mutations.</summary>
    public const string ActivityStageId = StandardStageIds.Ideation;

    /// <summary>Session identifier used for the activity events broadcast by backlog mutations.</summary>
    public const string ActivitySessionPrefix = "ideation-backlog";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ITaskRepository _taskRepository;
    private readonly IDependencyResolutionEngine _dependencyEngine;
    private readonly ITaskMarkdownSyncService _markdownSyncService;
    private readonly IWorkspaceLockProvider _lockProvider;
    private readonly IAgentActivityStreamer _activityStreamer;
    private readonly ILogger<BacklogMutationToolService> _logger;

    public BacklogMutationToolService(
        ITaskRepository taskRepository,
        IDependencyResolutionEngine dependencyEngine,
        ITaskMarkdownSyncService markdownSyncService,
        IWorkspaceLockProvider lockProvider,
        IAgentActivityStreamer activityStreamer,
        ILogger<BacklogMutationToolService> logger)
    {
        _taskRepository = taskRepository ?? throw new ArgumentNullException(nameof(taskRepository));
        _dependencyEngine = dependencyEngine ?? throw new ArgumentNullException(nameof(dependencyEngine));
        _markdownSyncService = markdownSyncService ?? throw new ArgumentNullException(nameof(markdownSyncService));
        _lockProvider = lockProvider ?? throw new ArgumentNullException(nameof(lockProvider));
        _activityStreamer = activityStreamer ?? throw new ArgumentNullException(nameof(activityStreamer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<string> GetTaskAsync(BacklogToolScope scope, int taskId, CancellationToken cancellationToken)
    {
        var task = await LoadTaskAsync(scope, taskId, cancellationToken);
        var prerequisites = await _taskRepository.GetDependenciesAsync(taskId, cancellationToken);
        var graph = await _taskRepository.GetWorkspaceDependenciesAsync(scope.WorkspaceId, cancellationToken);
        var dependents = graph
            .Where(kv => kv.Value.Contains(taskId))
            .Select(kv => kv.Key)
            .OrderBy(id => id)
            .ToList();

        return JsonSerializer.Serialize(new
        {
            id = task.Id,
            title = task.Title,
            description = task.Description,
            status = task.Status,
            hasExecutionRun = task.HasExecutionRun,
            priority = task.Priority,
            mergeMode = task.MergeMode,
            baseBranch = task.BaseBranch,
            prerequisites = prerequisites.OrderBy(id => id).ToList(),
            dependentTaskIds = dependents
        }, JsonOptions);
    }

    public async Task<string> EditTaskAsync(BacklogToolScope scope, TaskEditRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(request);

        using var taskLock = AcquireTaskLockOrThrow(scope.WorkspaceId, request.TaskId);
        var task = await LoadEditableTaskAsync(scope, request.TaskId, cancellationToken);

        // Validate every requested value before touching the (possibly tracked) entity.
        var title = request.Title?.Trim();
        if (title is { Length: 0 })
        {
            throw new ArgumentException("Task title cannot be empty.", nameof(request));
        }

        var description = request.Description?.Trim();

        if (request.Priority.HasValue)
        {
            ValidatePriority(request.Priority.Value);
        }

        MergeMode? mergeMode = null;
        if (request.MergeMode != null)
        {
            if (!Enum.TryParse<MergeMode>(request.MergeMode, ignoreCase: true, out var parsedMergeMode) || !Enum.IsDefined(parsedMergeMode))
            {
                throw new ArgumentException(
                    $"Invalid mergeMode '{request.MergeMode}'. Expected one of: {string.Join(", ", Enum.GetNames<MergeMode>())}.",
                    nameof(request));
            }

            mergeMode = parsedMergeMode;
        }

        var baseBranch = request.BaseBranch?.Trim();
        if (baseBranch is { Length: 0 })
        {
            throw new ArgumentException("Base branch cannot be empty.", nameof(request));
        }

        var changes = new List<string>();
        IReadOnlyList<int>? newDependencies = null;
        if (request.DependsOnIds != null)
        {
            var distinct = request.DependsOnIds.Distinct().OrderBy(id => id).ToList();
            var current = (await _taskRepository.GetDependenciesAsync(task.Id, cancellationToken)).OrderBy(id => id).ToList();
            if (!current.SequenceEqual(distinct))
            {
                await ValidateDependenciesAsync(scope, task.Id, distinct, cancellationToken);
                newDependencies = distinct;
                changes.Add($"dependencies [{string.Join(", ", current)}] -> [{string.Join(", ", distinct)}]");
            }
        }

        if (title != null && !string.Equals(task.Title, title, StringComparison.Ordinal))
        {
            changes.Add($"title \"{task.Title}\" -> \"{title}\"");
            task.Title = title;
        }

        if (description != null && !string.Equals(task.Description, description, StringComparison.Ordinal))
        {
            changes.Add("description");
            task.Description = description;
        }

        if (request.Priority.HasValue && task.Priority != request.Priority.Value)
        {
            changes.Add($"priority P{task.Priority} -> P{request.Priority.Value}");
            task.Priority = request.Priority.Value;
        }

        if (mergeMode.HasValue && task.MergeMode != mergeMode.Value)
        {
            changes.Add($"mergeMode {task.MergeMode} -> {mergeMode.Value}");
            task.MergeMode = mergeMode.Value;
        }

        if (baseBranch != null && !string.Equals(task.BaseBranch, baseBranch, StringComparison.Ordinal))
        {
            changes.Add($"baseBranch \"{task.BaseBranch}\" -> \"{baseBranch}\"");
            task.BaseBranch = baseBranch;
        }

        if (changes.Count == 0)
        {
            return $"Task #{task.Id} unchanged: the requested values already match.";
        }

        task.UpdatedAt = DateTime.UtcNow;
        var reason = $"Ideation agent edited task: {string.Join("; ", changes)}";
        await _taskRepository.UpdateWithHistoryAsync(task, newDependencies, AuditActors.IdeationAgent, reason, cancellationToken);
        await SyncMarkdownAsync(scope, task, cancellationToken);
        await BroadcastAsync(task, $"Ideation agent edited task #{task.Id}: {string.Join("; ", changes)}", CanonicalToolNames.EditTask, cancellationToken);

        return $"Task #{task.Id} updated ({string.Join("; ", changes)}).";
    }
    public async Task<string> SetTaskPriorityAsync(BacklogToolScope scope, int taskId, int priority, CancellationToken cancellationToken)
    {
        return await ReworkPrioritiesAsync(scope, [new TaskPriorityUpdate(taskId, priority)], cancellationToken);
    }

    public async Task<string> ReworkPrioritiesAsync(BacklogToolScope scope, IReadOnlyList<TaskPriorityUpdate> updates, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (updates == null || updates.Count == 0)
        {
            throw new ArgumentException("At least one priority update must be provided.", nameof(updates));
        }

        var deduplicated = updates.GroupBy(u => u.TaskId).Select(g => g.Last()).ToList();
        var acquiredLocks = new List<IDisposable>(deduplicated.Count);
        try
        {
            foreach (var taskId in deduplicated.Select(u => u.TaskId).OrderBy(id => id))
            {
                acquiredLocks.Add(AcquireTaskLockOrThrow(scope.WorkspaceId, taskId));
            }

            // Validate every entry before mutating anything so a batch is applied atomically from the agent's viewpoint.
            var targets = new List<(ForgeTask Task, int Priority)>(deduplicated.Count);
            foreach (var update in deduplicated)
            {
                ValidatePriority(update.Priority);
                targets.Add((await LoadEditableTaskAsync(scope, update.TaskId, cancellationToken), update.Priority));
            }

            var summary = new List<string>();
            foreach (var (task, priority) in targets)
            {
                if (task.Priority == priority)
                {
                    summary.Add($"#{task.Id}: already P{priority}");
                    continue;
                }

                var oldPriority = task.Priority;
                task.Priority = priority;
                task.UpdatedAt = DateTime.UtcNow;

                await _taskRepository.UpdateWithHistoryAsync(
                    task,
                    null,
                    AuditActors.IdeationAgent,
                    $"Ideation agent updated priority from P{oldPriority} to P{priority}",
                    cancellationToken);
                await SyncMarkdownAsync(scope, task, cancellationToken);
                await BroadcastAsync(task, $"Ideation agent set task #{task.Id} priority P{oldPriority} -> P{priority}", CanonicalToolNames.SetTaskPriority, cancellationToken);
                summary.Add($"#{task.Id}: P{oldPriority} -> P{priority}");
            }

            return $"Priorities updated for {targets.Count} task(s): {string.Join("; ", summary)}.";
        }
        finally
        {
            for (var i = acquiredLocks.Count - 1; i >= 0; i--)
            {
                acquiredLocks[i].Dispose();
            }
        }
    }

    public async Task<string> RemoveTaskAsync(BacklogToolScope scope, int taskId, string? reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        using var taskLock = AcquireTaskLockOrThrow(scope.WorkspaceId, taskId);
        var task = await LoadEditableTaskAsync(scope, taskId, cancellationToken);

        var graph = await _taskRepository.GetWorkspaceDependenciesAsync(scope.WorkspaceId, cancellationToken);
        var dependentIds = graph.Where(kv => kv.Value.Contains(taskId)).Select(kv => kv.Key).ToList();
        if (dependentIds.Count > 0)
        {
            var dependents = await _taskRepository.GetByIdsAsync(dependentIds, cancellationToken);
            var active = dependents
                .Where(t => t.Status is not (TaskOverallStatus.Cancelled or TaskOverallStatus.Rejected))
                .Select(t => t.Id)
                .OrderBy(id => id)
                .ToList();
            if (active.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Task #{taskId} cannot be removed because active task(s) depend on it: {string.Join(", ", active.Select(id => $"#{id}"))}. " +
                    "Remove or re-link those dependents first.");
            }
        }

        var trimmedReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        task.Status = TaskOverallStatus.Cancelled;
        task.IsWaitingForHuman = false;
        task.UpdatedAt = DateTime.UtcNow;

        var changeReason = trimmedReason == null
            ? "Ideation agent removed (cancelled) task"
            : $"Ideation agent removed (cancelled) task: {trimmedReason}";
        await _taskRepository.UpdateWithHistoryAsync(task, Array.Empty<int>(), AuditActors.IdeationAgent, changeReason, cancellationToken);

        await SyncMarkdownAsync(scope, task, cancellationToken);
        await _markdownSyncService.ArchiveOrCleanupTaskFileAsync(task, scope.RepositoryPath, archive: true, cancellationToken);
        await BroadcastAsync(task, $"Ideation agent removed task #{task.Id}" + (trimmedReason == null ? string.Empty : $": {trimmedReason}"), CanonicalToolNames.RemoveTask, cancellationToken);

        return $"Task #{task.Id} removed (status Cancelled).";
    }

    private IDisposable AcquireTaskLockOrThrow(int workspaceId, int taskId)
    {
        if (!_lockProvider.TryAcquireTaskLock(workspaceId, taskId, out var releaser) || releaser == null)
        {
            throw new TaskCannotBeModifiedException(taskId, "it is currently locked by a running pipeline stage.");
        }

        return releaser;
    }

    private async Task<ForgeTask> LoadTaskAsync(BacklogToolScope scope, int taskId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var task = await _taskRepository.GetByIdAsync(taskId, cancellationToken);
        if (task == null || task.WorkspaceId != scope.WorkspaceId)
        {
            throw new TaskNotFoundException(taskId);
        }

        return task;
    }

    private async Task<ForgeTask> LoadEditableTaskAsync(BacklogToolScope scope, int taskId, CancellationToken cancellationToken)
    {
        var task = await LoadTaskAsync(scope, taskId, cancellationToken);

        if (task.Status is not (TaskOverallStatus.Suggested or TaskOverallStatus.Open))
        {
            throw new TaskCannotBeModifiedException(taskId, $"its status is {task.Status}; only Suggested or Open tasks can be changed from ideation.");
        }

        if (task.HasExecutionRun)
        {
            throw new TaskCannotBeModifiedException(taskId, "it has already entered pipeline execution.");
        }

        return task;
    }

    private async Task ValidateDependenciesAsync(BacklogToolScope scope, int taskId, IReadOnlyList<int> dependsOnIds, CancellationToken cancellationToken)
    {
        if (dependsOnIds.Contains(taskId))
        {
            throw new CircularDependencyException($"Task #{taskId} cannot depend on itself.", [taskId, taskId]);
        }

        foreach (var prerequisiteId in dependsOnIds)
        {
            var prerequisite = await LoadTaskAsync(scope, prerequisiteId, cancellationToken);
            if (prerequisite.Status is TaskOverallStatus.Cancelled or TaskOverallStatus.Rejected)
            {
                throw new InvalidOperationException(
                    $"Task #{taskId} cannot depend on task #{prerequisiteId} because its status is {prerequisite.Status}.");
            }
        }

        var graph = await _taskRepository.GetWorkspaceDependenciesAsync(scope.WorkspaceId, cancellationToken);
        var edges = graph
            .Where(kv => kv.Key != taskId)
            .SelectMany(kv => kv.Value.Select(dep => (TaskId: kv.Key, DependsOnTaskId: dep)))
            .Concat(dependsOnIds.Select(dep => (TaskId: taskId, DependsOnTaskId: dep)))
            .ToList();

        if (_dependencyEngine.HasCycle(scope.WorkspaceId, edges))
        {
            throw new CircularDependencyException(
                $"Setting prerequisites [{string.Join(", ", dependsOnIds)}] on task #{taskId} would create a circular dependency.");
        }
    }

    private static void ValidatePriority(int priority)
    {
        if (priority < TaskPriority.Critical || priority > TaskPriority.Trivial)
        {
            throw new ArgumentOutOfRangeException(
                nameof(priority),
                priority,
                $"Priority must be between {TaskPriority.Critical} (Critical) and {TaskPriority.Trivial} (Trivial).");
        }
    }

    private async Task SyncMarkdownAsync(BacklogToolScope scope, ForgeTask task, CancellationToken cancellationToken)
    {
        var dependencies = await _taskRepository.GetDependenciesAsync(task.Id, cancellationToken);
        await _markdownSyncService.SyncTaskToDiskAsync(task, scope.RepositoryPath, dependencies, cancellationToken: cancellationToken);
    }

    private async Task BroadcastAsync(ForgeTask task, string message, string toolName, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Backlog mutation by ideation agent on task #{TaskId}: {Message}", task.Id, message);
        await _activityStreamer.BroadcastAsync(
            new AgentActivityEvent(
                $"{ActivitySessionPrefix}-{task.WorkspaceId}",
                task.Id,
                ActivityStageId,
                AgentActivityKind.Notice,
                message,
                DateTime.UtcNow,
                new Dictionary<string, object?>
                {
                    [AgentActivityMetadataKeys.TaskTitle] = task.Title,
                    [AgentActivityMetadataKeys.Priority] = task.Priority,
                    [AgentActivityMetadataKeys.ToolName] = toolName
                }),
            cancellationToken);
    }
}
