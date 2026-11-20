using System.Globalization;
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
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Agents.Tools;

/// <summary>Workspace a backlog mutation applies to, with repository and optional session-policy context.</summary>
public sealed record BacklogToolScope(
    int WorkspaceId,
    string RepositoryPath,
    BacklogMutationMode Mode = BacklogMutationMode.Ideation,
    string? Actor = null,
    TaskOrigin? SessionOrigin = null,
    string? DefaultBaseBranch = null,
    string? ExternalSource = null,
    string? ExternalReferenceId = null,
    string? CloneDirectory = null,
    int? SessionTaskId = null)
{
    public bool IsTriage => Mode == BacklogMutationMode.Triage;

    public string EffectiveActor => !string.IsNullOrWhiteSpace(Actor)
        ? Actor
        : (IsTriage ? AuditActors.TriageAgent : AuditActors.IdeationAgent);

    public string EffectiveActorDisplayName => IsTriage ? "Triage agent" : "Ideation agent";

    public string EffectiveActivityStageId => IsTriage ? StandardStageIds.Triage : BacklogMutationToolService.ActivityStageId;

    public string EffectiveActivitySessionId => IsTriage
        ? (SessionTaskId is > 0
            ? $"{BacklogMutationToolService.TriageActivitySessionPrefix}-{WorkspaceId}-{SessionTaskId.Value}"
            : $"{BacklogMutationToolService.TriageActivitySessionPrefix}-{WorkspaceId}")
        : $"{BacklogMutationToolService.ActivitySessionPrefix}-{WorkspaceId}";
}

/// <summary>Partial update of an unstarted task; null members are left untouched.</summary>
public sealed record TaskEditRequest(
    int TaskId,
    string? Title = null,
    string? Description = null,
    int? Priority = null,
    IReadOnlyList<int>? DependsOnIds = null,
    string? MergeMode = null,
    string? BaseBranch = null,
    string? Difficulty = null,
    IReadOnlyList<int>? AttachmentIds = null);

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

    Task<string> ProposeTasksAsync(
        BacklogToolScope scope,
        IReadOnlyList<TaskProposal> proposals,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class BacklogMutationToolService : IBacklogMutationToolService
{
    /// <summary>Stage identifier used for the activity events broadcast by ideation backlog mutations.</summary>
    public const string ActivityStageId = StandardStageIds.Ideation;

    /// <summary>Session identifier prefix used for the activity events broadcast by ideation backlog mutations.</summary>
    public const string ActivitySessionPrefix = "ideation-backlog";

    /// <summary>Session identifier prefix used for the activity events broadcast by triage backlog mutations.</summary>
    public const string TriageActivitySessionPrefix = "triage-backlog";

    /// <summary>Prefix used in <see cref="EditTaskAsync"/> responses when all requested values already match.</summary>
    public const string UnchangedResultPrefix = "unchanged:";

    public static bool IsUnchangedEditResult(int taskId, string? result) =>
        !string.IsNullOrWhiteSpace(result) &&
        result.StartsWith($"Task #{taskId} {UnchangedResultPrefix}", StringComparison.OrdinalIgnoreCase);

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
    private readonly IBacklogMergeModeValidator _mergeModeValidator;
    private readonly ITaskAttachmentRepository _attachmentRepository;
    private readonly ITaskLifecycleMutationCoordinator _lifecycleMutationCoordinator;
    private readonly ILogger<BacklogMutationToolService> _logger;

    public BacklogMutationToolService(
        ITaskRepository taskRepository,
        IDependencyResolutionEngine dependencyEngine,
        ITaskMarkdownSyncService markdownSyncService,
        IWorkspaceLockProvider lockProvider,
        IAgentActivityStreamer activityStreamer,
        IBacklogMergeModeValidator mergeModeValidator,
        ITaskAttachmentRepository attachmentRepository,
        ITaskLifecycleMutationCoordinator lifecycleMutationCoordinator,
        ILogger<BacklogMutationToolService> logger)
    {
        _taskRepository = taskRepository ?? throw new ArgumentNullException(nameof(taskRepository));
        _dependencyEngine = dependencyEngine ?? throw new ArgumentNullException(nameof(dependencyEngine));
        _markdownSyncService = markdownSyncService ?? throw new ArgumentNullException(nameof(markdownSyncService));
        _lockProvider = lockProvider ?? throw new ArgumentNullException(nameof(lockProvider));
        _activityStreamer = activityStreamer ?? throw new ArgumentNullException(nameof(activityStreamer));
        _mergeModeValidator = mergeModeValidator ?? throw new ArgumentNullException(nameof(mergeModeValidator));
        _attachmentRepository = attachmentRepository ?? throw new ArgumentNullException(nameof(attachmentRepository));
        _lifecycleMutationCoordinator = lifecycleMutationCoordinator ?? throw new ArgumentNullException(nameof(lifecycleMutationCoordinator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<string> GetTaskAsync(BacklogToolScope scope, int taskId, CancellationToken cancellationToken)
    {
        var task = await LoadTaskAsync(scope, taskId, cancellationToken);
        var prerequisites = await _taskRepository.GetDependenciesAsync(taskId, cancellationToken);
        var graph = await _taskRepository.GetWorkspaceDependenciesAsync(scope.WorkspaceId, cancellationToken);
        var attachmentIds = scope.IsTriage
            ? new List<int>()
            : (await _attachmentRepository.GetByTaskIdAsync(taskId, cancellationToken))
                .Select(a => a.Id)
                .OrderBy(id => id)
                .ToList();
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
            origin = task.Origin,
            hasExecutionRun = task.HasExecutionRun,
            priority = task.Priority,
            difficulty = task.Difficulty,
            mergeMode = task.MergeMode,
            baseBranch = task.BaseBranch,
            externalSource = task.ExternalSource,
            externalReferenceId = task.ExternalReferenceId,
            pullRequestNumber = task.PullRequestNumber,
            pullRequestUrl = task.PullRequestUrl,
            prerequisites = prerequisites.OrderBy(id => id).ToList(),
            dependentTaskIds = dependents,
            attachmentIds
        }, JsonOptions);
    }

    public async Task<string> EditTaskAsync(BacklogToolScope scope, TaskEditRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(request);

        ForgeTask task;
        List<string> changes;
        bool dependenciesUpdated;
        using (AcquireTaskLockOrThrow(scope.WorkspaceId, request.TaskId))
        {
            task = await LoadEditableTaskAsync(scope, request.TaskId, cancellationToken);

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
                mergeMode = await ParseAndValidateMergeModeAsync(scope, request.MergeMode, nameof(request), cancellationToken);
            }

            TaskDifficulty? difficulty = null;
            if (request.Difficulty != null)
            {
                difficulty = ParseDifficulty(request.Difficulty, nameof(request));
            }

            var baseBranch = request.BaseBranch?.Trim();
            if (baseBranch is { Length: 0 })
            {
                throw new ArgumentException("Base branch cannot be empty.", nameof(request));
            }

            changes = new List<string>();
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

            IReadOnlyList<int>? newAttachmentIds = null;
            if (scope.IsTriage)
            {
                if (request.AttachmentIds != null)
                {
                    throw new InvalidOperationException("Modifying task attachments is not permitted during untrusted intake triage.");
                }
            }
            else if (request.AttachmentIds != null || description != null)
            {
                var effectiveDescription = description ?? task.Description;
                IReadOnlyList<int> requestedAttachmentIds;
                if (request.AttachmentIds != null)
                {
                    var validExplicitIds = await ValidateAndResolveWorkspaceAttachmentIdsAsync(
                        scope.WorkspaceId,
                        request.AttachmentIds,
                        description: null,
                        cancellationToken);
                    var resolvedCitedIds = await ResolveExistingWorkspaceAttachmentIdsAsync(
                        scope.WorkspaceId,
                        explicitIds: null,
                        effectiveDescription,
                        cancellationToken);
                    requestedAttachmentIds = validExplicitIds
                        .Concat(resolvedCitedIds)
                        .Distinct()
                        .OrderBy(id => id)
                        .ToList();
                }
                else
                {
                    var currentAttachments = (await _attachmentRepository.GetByTaskIdAsync(task.Id, cancellationToken))
                        .Select(a => a.Id)
                        .ToList();
                    var oldCitedIds = AttachmentCitationParser.ExtractAttachmentIds(task.Description).ToHashSet();
                    var retainedExplicitIds = currentAttachments
                        .Where(id => !oldCitedIds.Contains(id))
                        .ToList();
                    requestedAttachmentIds = await ResolveExistingWorkspaceAttachmentIdsAsync(
                        scope.WorkspaceId,
                        retainedExplicitIds,
                        effectiveDescription,
                        cancellationToken);
                }

                var existingIds = (await _attachmentRepository.GetByTaskIdAsync(task.Id, cancellationToken))
                    .Select(a => a.Id)
                    .OrderBy(id => id)
                    .ToList();

                if (!existingIds.SequenceEqual(requestedAttachmentIds))
                {
                    newAttachmentIds = requestedAttachmentIds;
                    changes.Add($"attachments [{string.Join(", ", existingIds)}] -> [{string.Join(", ", requestedAttachmentIds)}]");
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

            if (difficulty.HasValue && task.Difficulty != difficulty.Value)
            {
                changes.Add($"difficulty {task.Difficulty} -> {difficulty.Value}");
                task.Difficulty = difficulty.Value;
            }

            if (baseBranch != null && !string.Equals(task.BaseBranch, baseBranch, StringComparison.Ordinal))
            {
                changes.Add($"baseBranch \"{task.BaseBranch}\" -> \"{baseBranch}\"");
                task.BaseBranch = baseBranch;
            }

            if (changes.Count == 0)
            {
                return $"Task #{task.Id} {UnchangedResultPrefix} the requested values already match.";
            }

            task.UpdatedAt = DateTime.UtcNow;
            var reason = $"{scope.EffectiveActorDisplayName} edited task: {string.Join("; ", changes)}";
            await _taskRepository.UpdateWithHistoryAsync(task, newDependencies, scope.EffectiveActor, reason, cancellationToken);
            if (newAttachmentIds != null)
            {
                await AttachmentCitationParser.SetExistingTaskAttachmentLinksAsync(
                    _attachmentRepository,
                    scope.WorkspaceId,
                    task.Id,
                    newAttachmentIds,
                    _logger,
                    cancellationToken);
            }

            await SyncMarkdownAsync(scope, task, cancellationToken);
            dependenciesUpdated = newDependencies != null;
        }

        if (dependenciesUpdated)
        {
            await _lifecycleMutationCoordinator.TryScheduleOpenTaskIfReadyAsync(task.Id, cancellationToken);
        }

        await BroadcastAsync(scope, task, $"{scope.EffectiveActorDisplayName} edited task #{task.Id}: {string.Join("; ", changes)}", CanonicalToolNames.EditTask, cancellationToken);

        return $"Task #{task.Id} updated ({string.Join("; ", changes)}).";
    }
    public async Task<string> SetTaskPriorityAsync(BacklogToolScope scope, int taskId, int priority, CancellationToken cancellationToken)
    {
        return await ReworkPrioritiesAsync(scope, [new TaskPriorityUpdate(taskId, priority)], cancellationToken);
    }

    public async Task<string> ReworkPrioritiesAsync(BacklogToolScope scope, IReadOnlyList<TaskPriorityUpdate> updates, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.IsTriage)
        {
            throw new InvalidOperationException("Priority rework tools are disabled during triage sessions.");
        }

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
                    scope.EffectiveActor,
                    $"{scope.EffectiveActorDisplayName} updated priority from P{oldPriority} to P{priority}",
                    cancellationToken);
                await SyncMarkdownAsync(scope, task, cancellationToken);
                await BroadcastAsync(scope, task, $"{scope.EffectiveActorDisplayName} set task #{task.Id} priority P{oldPriority} -> P{priority}", CanonicalToolNames.SetTaskPriority, cancellationToken);
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
                .Where(t => t.Status is not (TaskOverallStatus.Cancelled or TaskOverallStatus.Rejected or TaskOverallStatus.Triage))
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
            ? $"{scope.EffectiveActorDisplayName} removed (cancelled) task"
            : $"{scope.EffectiveActorDisplayName} removed (cancelled) task: {trimmedReason}";
        await _taskRepository.UpdateWithHistoryAsync(task, Array.Empty<int>(), scope.EffectiveActor, changeReason, cancellationToken);

        await SyncMarkdownAsync(scope, task, cancellationToken);
        await ArchiveMarkdownAsync(scope, task, cancellationToken);
        await BroadcastAsync(scope, task, $"{scope.EffectiveActorDisplayName} removed task #{task.Id}" + (trimmedReason == null ? string.Empty : $": {trimmedReason}"), CanonicalToolNames.RemoveTask, cancellationToken);

        return $"Task #{task.Id} removed (status Cancelled).";
    }

    public async Task<string> ProposeTasksAsync(
        BacklogToolScope scope,
        IReadOnlyList<TaskProposal> proposals,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (proposals == null || proposals.Count == 0)
        {
            throw new ArgumentException("At least one task proposal must be provided.", nameof(proposals));
        }

        // Pre-validate every entry before mutating anything so a batch fails fast without partial writes.
        foreach (var proposal in proposals)
        {
            if (proposal == null || string.IsNullOrWhiteSpace(proposal.Title))
            {
                throw new ArgumentException("Task title cannot be empty.", nameof(proposals));
            }

            ValidatePriority(proposal.Priority);

            if (!string.IsNullOrWhiteSpace(proposal.MergeMode))
            {
                await ParseAndValidateMergeModeAsync(scope, proposal.MergeMode, nameof(proposals), cancellationToken);
            }

            if (!string.IsNullOrWhiteSpace(proposal.Difficulty))
            {
                ParseDifficulty(proposal.Difficulty, nameof(proposals));
            }

            if (proposal.DependsOnIds is { Count: > 0 })
            {
                var distinctIds = proposal.DependsOnIds.Distinct().OrderBy(id => id).ToList();
                await ValidateDependenciesAsync(scope, taskId: 0, distinctIds, cancellationToken);
            }

            if (scope.IsTriage)
            {
                if (proposal.AttachmentIds is { Count: > 0 })
                {
                    throw new InvalidOperationException("Linking workspace attachments is not permitted during untrusted intake triage.");
                }
            }
            else
            {
                await ValidateAndResolveWorkspaceAttachmentIdsAsync(
                    scope.WorkspaceId,
                    proposal.AttachmentIds,
                    proposal.Description,
                    cancellationToken);
            }
        }

        IReadOnlyList<ForgeTask>? workspaceTasks = proposals.Any(p => p.DependsOnTitles is { Count: > 0 })
            ? await _taskRepository.GetByWorkspaceIdAsync(scope.WorkspaceId, cancellationToken)
            : null;

        if (proposals.Count == 1)
        {
            var single = proposals[0];
            var singleDeps = ResolveProposalDependencies(single, workspaceTasks, Array.Empty<ForgeTask>());
            var (_, singleJson) = await ProposeSingleTaskAsync(
                scope,
                single.Title,
                single.Description,
                single.Priority,
                singleDeps,
                single.MergeMode,
                single.Difficulty,
                single.AttachmentIds,
                cancellationToken);
            return singleJson;
        }

        var createdTasks = new List<JsonElement>(proposals.Count);
        var createdEntities = new List<ForgeTask>(proposals.Count);

        foreach (var p in proposals)
        {
            var deps = ResolveProposalDependencies(p, workspaceTasks, createdEntities);

            var (created, singleResultJson) = await ProposeSingleTaskAsync(
                scope,
                p.Title,
                p.Description,
                p.Priority,
                deps,
                p.MergeMode,
                p.Difficulty,
                p.AttachmentIds,
                cancellationToken);

            createdEntities.Add(created);
            createdTasks.Add(JsonSerializer.Deserialize<JsonElement>(singleResultJson));
        }

        return JsonSerializer.Serialize(createdTasks, JsonOptions);
    }

    private static IReadOnlyList<int>? ResolveProposalDependencies(
        TaskProposal proposal,
        IReadOnlyList<ForgeTask>? workspaceTasks,
        IReadOnlyList<ForgeTask> createdBatchTasks)
    {
        var deps = new List<int>();
        if (proposal.DependsOnIds is { Count: > 0 })
        {
            deps.AddRange(proposal.DependsOnIds);
        }

        if (proposal.DependsOnTitles is { Count: > 0 })
        {
            var activeCandidates = createdBatchTasks
                .Concat(workspaceTasks?.Where(t => t.Status is not (TaskOverallStatus.Cancelled or TaskOverallStatus.Rejected or TaskOverallStatus.Triage))
                    ?? Enumerable.Empty<ForgeTask>())
                .ToList();

            foreach (var rawTitle in proposal.DependsOnTitles!)
            {
                if (string.IsNullOrWhiteSpace(rawTitle))
                {
                    continue;
                }

                var trimmed = rawTitle.Trim();
                if (int.TryParse(trimmed.TrimStart('#'), NumberStyles.None, CultureInfo.InvariantCulture, out var parsedId) && parsedId > 0)
                {
                    deps.Add(parsedId);
                    continue;
                }

                var exact = activeCandidates.FirstOrDefault(t => string.Equals(t.Title, trimmed, StringComparison.OrdinalIgnoreCase));
                if (exact != null)
                {
                    deps.Add(exact.Id);
                    continue;
                }

                var partial = activeCandidates.FirstOrDefault(t =>
                    t.Title.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
                    trimmed.Contains(t.Title, StringComparison.OrdinalIgnoreCase));
                if (partial != null)
                {
                    deps.Add(partial.Id);
                }
            }
        }

        return deps.Count > 0 ? deps.Distinct().OrderBy(id => id).ToList() : null;
    }

    private async Task<(ForgeTask Created, string Json)> ProposeSingleTaskAsync(
        BacklogToolScope scope,
        string title,
        string description,
        int? priority = null,
        IReadOnlyList<int>? dependsOnIds = null,
        string? mergeMode = null,
        string? difficulty = null,
        IReadOnlyList<int>? attachmentIds = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var trimmedTitle = title?.Trim();
        if (string.IsNullOrWhiteSpace(trimmedTitle))
        {
            throw new ArgumentException("Task title cannot be empty.", nameof(title));
        }

        var effPriority = priority.HasValue ? priority.Value : TaskPriority.Normal;
        ValidatePriority(effPriority);

        var effMergeMode = !string.IsNullOrWhiteSpace(mergeMode)
            ? await ParseAndValidateMergeModeAsync(scope, mergeMode, nameof(mergeMode), cancellationToken)
            : MergeMode.ManualReviewRequired;

        var effDifficulty = !string.IsNullOrWhiteSpace(difficulty)
            ? ParseDifficulty(difficulty, nameof(difficulty))
            : TaskDifficulty.Standard;

        IReadOnlyList<int>? validDependencies = null;
        if (dependsOnIds != null && dependsOnIds.Count > 0)
        {
            var distinct = dependsOnIds.Distinct().OrderBy(id => id).ToList();
            await ValidateDependenciesAsync(scope, taskId: 0, distinct, cancellationToken);
            validDependencies = distinct;
        }

        var trimmedDescription = description?.Trim() ?? string.Empty;
        IReadOnlyList<int> resolvedAttachmentIds;
        if (scope.IsTriage)
        {
            if (attachmentIds is { Count: > 0 })
            {
                throw new InvalidOperationException("Linking workspace attachments is not permitted during untrusted intake triage.");
            }
            resolvedAttachmentIds = Array.Empty<int>();
        }
        else
        {
            resolvedAttachmentIds = await ValidateAndResolveWorkspaceAttachmentIdsAsync(
                scope.WorkspaceId,
                attachmentIds,
                trimmedDescription,
                cancellationToken);
        }

        var task = new ForgeTask
        {
            WorkspaceId = scope.WorkspaceId,
            Title = trimmedTitle,
            Description = trimmedDescription,
            Status = scope.IsTriage ? TaskOverallStatus.Suggested : TaskOverallStatus.Draft,
            Priority = effPriority,
            Difficulty = effDifficulty,
            MergeMode = effMergeMode,
            BaseBranch = !string.IsNullOrWhiteSpace(scope.DefaultBaseBranch)
                ? scope.DefaultBaseBranch
                : WorkflowDefaults.DefaultBaseBranch,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        if (scope.IsTriage)
        {
            task.Origin = scope.SessionOrigin ?? TaskOrigin.UntrustedIntake;
            task.ExternalSource = scope.ExternalSource;
            task.ExternalReferenceId = scope.ExternalReferenceId;
        }

        var createdReason = scope.IsTriage
            ? "Proposed suggested task from untrusted intake triage"
            : "Proposed draft task from ideation";

        var created = await _taskRepository.CreateWithBaselineAsync(
            task,
            validDependencies,
            scope.EffectiveActor,
            createdReason,
            cancellationToken);

        if (resolvedAttachmentIds.Count > 0)
        {
            await _attachmentRepository.SetTaskAttachmentLinksAsync(created.Id, resolvedAttachmentIds, cancellationToken);
        }

        await SyncMarkdownAsync(scope, created, cancellationToken);
        var broadcastMessage = scope.IsTriage
            ? $"{scope.EffectiveActorDisplayName} proposed suggested task #{created.Id}: {created.Title}"
            : $"Ideation agent proposed draft task #{created.Id}: {created.Title}";
        await BroadcastAsync(scope, created, broadcastMessage, CanonicalToolNames.ProposeTasks, cancellationToken);

        var json = await GetTaskAsync(scope, created.Id, cancellationToken);
        return (created, json);
    }

    private async Task<MergeMode> ParseAndValidateMergeModeAsync(
        BacklogToolScope scope,
        string rawMergeMode,
        string paramName,
        CancellationToken cancellationToken)
    {
        var parsed = await _mergeModeValidator.ParseAndValidateAsync(scope.WorkspaceId, rawMergeMode, paramName, cancellationToken);
        if (scope.IsTriage && parsed == MergeMode.Automatic)
        {
            throw new InvalidOperationException(
                "MergeMode 'Automatic' is not permitted during untrusted intake triage; use ManualReviewRequired or ExternalPullRequest.");
        }

        return parsed;
    }

    private Task<IReadOnlyList<int>> ValidateAndResolveWorkspaceAttachmentIdsAsync(
        int workspaceId,
        IReadOnlyList<int>? explicitIds,
        string? description,
        CancellationToken cancellationToken) =>
        AttachmentCitationParser.ValidateAndResolveWorkspaceIdsAsync(
            _attachmentRepository,
            workspaceId,
            explicitIds,
            description,
            cancellationToken);

    private Task<IReadOnlyList<int>> ResolveExistingWorkspaceAttachmentIdsAsync(
        int workspaceId,
        IReadOnlyList<int>? explicitIds,
        string? description,
        CancellationToken cancellationToken) =>
        AttachmentCitationParser.ResolveExistingWorkspaceIdsAsync(
            _attachmentRepository,
            workspaceId,
            explicitIds,
            description,
            _logger,
            cancellationToken);

    private static TaskDifficulty ParseDifficulty(string rawDifficulty, string paramName)
    {
        var normalizedDifficulty = rawDifficulty.Trim();
        if (string.IsNullOrEmpty(normalizedDifficulty) ||
            int.TryParse(normalizedDifficulty, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) ||
            !Enum.TryParse<TaskDifficulty>(normalizedDifficulty, ignoreCase: true, out var parsedDifficulty) ||
            !Enum.IsDefined(parsedDifficulty))
        {
            throw new ArgumentException(
                $"Invalid difficulty '{rawDifficulty}'. Expected one of: {string.Join(", ", Enum.GetNames<TaskDifficulty>())}.",
                paramName);
        }

        return parsedDifficulty;
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

        if (scope.IsTriage)
        {
            if (task.Status == TaskOverallStatus.Triage ||
                (scope.SessionTaskId.HasValue && task.Id == scope.SessionTaskId.Value))
            {
                throw new TaskCannotBeModifiedException(taskId, "it is a triage execution session task and cannot be modified or removed.");
            }

            if (task.Status is not (TaskOverallStatus.Suggested or TaskOverallStatus.Open))
            {
                throw new TaskCannotBeModifiedException(
                    taskId,
                    $"its status is {task.Status}; only Suggested or Open tasks can be modified or removed during triage.");
            }

            if (task.Origin is not (TaskOrigin.UntrustedIntake or TaskOrigin.GitHubIssue))
            {
                throw new TaskCannotBeModifiedException(
                    taskId,
                    $"its origin is {task.Origin}; triage can only modify or remove tasks originated from UntrustedIntake or GitHubIssue.");
            }

            if (task.HasExecutionRun)
            {
                throw new TaskCannotBeModifiedException(taskId, "it has already entered pipeline execution.");
            }

            return task;
        }

        if (task.Status is not (TaskOverallStatus.Draft or TaskOverallStatus.Suggested or TaskOverallStatus.Open))
        {
            throw new TaskCannotBeModifiedException(taskId, $"its status is {task.Status}; only Draft, Suggested or Open tasks can be changed from ideation.");
        }

        if (task.HasExecutionRun)
        {
            throw new TaskCannotBeModifiedException(taskId, "it has already entered pipeline execution.");
        }

        return task;
    }

    private async Task ValidateDependenciesAsync(BacklogToolScope scope, int taskId, IReadOnlyList<int> dependsOnIds, CancellationToken cancellationToken)
    {
        if (taskId > 0 && dependsOnIds.Contains(taskId))
        {
            throw new CircularDependencyException($"Task #{taskId} cannot depend on itself.", [taskId, taskId]);
        }

        foreach (var prerequisiteId in dependsOnIds)
        {
            var prerequisite = await LoadTaskAsync(scope, prerequisiteId, cancellationToken);
            if (prerequisite.Status is TaskOverallStatus.Cancelled or TaskOverallStatus.Rejected or TaskOverallStatus.Triage)
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
        if (!string.IsNullOrWhiteSpace(scope.CloneDirectory))
        {
            await _markdownSyncService.SyncTaskToDiskAsync(
                task,
                scope.RepositoryPath,
                dependencies,
                agentProfileId: null,
                cloneDirectory: scope.CloneDirectory,
                cancellationToken: cancellationToken);
        }
        else
        {
            await _markdownSyncService.SyncTaskToDiskAsync(
                task,
                scope.RepositoryPath,
                dependencies,
                cancellationToken: cancellationToken);
        }
    }

    private Task ArchiveMarkdownAsync(BacklogToolScope scope, ForgeTask task, CancellationToken cancellationToken) =>
        !string.IsNullOrWhiteSpace(scope.CloneDirectory)
            ? _markdownSyncService.ArchiveOrCleanupTaskFileAsync(
                task,
                scope.RepositoryPath,
                archive: true,
                cloneDirectory: scope.CloneDirectory,
                cancellationToken: cancellationToken)
            : _markdownSyncService.ArchiveOrCleanupTaskFileAsync(
                task,
                scope.RepositoryPath,
                archive: true,
                cancellationToken: cancellationToken);

    private async Task BroadcastAsync(BacklogToolScope scope, ForgeTask task, string message, string toolName, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Backlog mutation by {Actor} on task #{TaskId}: {Message}", scope.EffectiveActor, task.Id, message);
        await _activityStreamer.BroadcastAsync(
            new AgentActivityEvent(
                scope.EffectiveActivitySessionId,
                task.Id,
                scope.EffectiveActivityStageId,
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
