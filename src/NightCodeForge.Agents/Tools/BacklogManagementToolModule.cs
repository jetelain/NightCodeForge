using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Repositories;

namespace NightCodeForge.Agents.Tools;

/// <summary>
/// Composable tool module encapsulating backlog task proposals (<c>propose_tasks</c>) and host-level backlog
/// mutation tools (<c>get_task</c>, <c>edit_task</c>, <c>set_task_priority</c>, <c>rework_priorities</c>, <c>remove_task</c>).
/// Resolves <see cref="IBacklogMutationToolService"/> per tool invocation via <see cref="IServiceScopeFactory"/> when available
/// so warm sessions never hold a disposed <c>AppDbContext</c>.
/// </summary>
public sealed class BacklogManagementToolModule : IAgentToolModule
{
    public const string MissingModuleInstanceMessage = IAgentToolModule.MissingBoundModuleInstanceMessage;
    private const string GenericTaskTitle = "Task";

    private readonly string _workspaceRoot;
    private readonly int? _workspaceId;
    private readonly BacklogMutationSessionPolicy? _backlogMutationPolicy;
    private IBacklogMutationToolService? _defaultBacklogMutationService;
    private IBacklogMutationToolService? _explicitBacklogMutationService;
    private bool _hasExplicitBacklogMutationService;
    private bool _attachBacklogMutationService;

    public List<TaskProposal> PendingProposals { get; } = new();
    public List<int> CreatedTaskIds { get; } = new();
    public List<int> ModifiedTaskIds { get; } = new();
    public List<int> RemovedTaskIds { get; } = new();

    public IServiceScopeFactory? ScopeFactory { get; set; }

    public IBacklogMutationToolService? BacklogMutationService
    {
        get => _hasExplicitBacklogMutationService
            ? _explicitBacklogMutationService
            : (_attachBacklogMutationService ? _defaultBacklogMutationService : null);
        set
        {
            _explicitBacklogMutationService = value;
            _hasExplicitBacklogMutationService = true;
        }
    }

    public bool IsBacklogMutationAvailable =>
        _hasExplicitBacklogMutationService
            ? _explicitBacklogMutationService != null
            : (_attachBacklogMutationService && (_defaultBacklogMutationService != null || ScopeFactory != null));

    public BacklogManagementToolModule(CanonicalToolScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        _workspaceRoot = scope.ResolveNormalizedWorkspaceRoot();
        _workspaceId = scope.WorkspaceId;
        _backlogMutationPolicy = scope.BacklogMutationPolicy;
    }

    public BacklogManagementToolModule(CanonicalToolScope scope, IServiceScopeFactory scopeFactory, bool attachBacklogMutationService)
        : this(scope)
    {
        ScopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _attachBacklogMutationService = attachBacklogMutationService;
    }

    public void ConfigureBacklogMutation(
        IBacklogMutationToolService? defaultService,
        IServiceScopeFactory? scopeFactory,
        bool attachBacklogMutationService)
    {
        _defaultBacklogMutationService = defaultService;
        if (scopeFactory != null)
        {
            ScopeFactory = scopeFactory;
        }
        _attachBacklogMutationService = attachBacklogMutationService;
    }

    public IReadOnlyList<AIFunction> CreateFunctions() => CreateFunctions(this);

    public static IReadOnlyList<AIFunction> CreateFunctions(BacklogManagementToolModule? module) =>
    [
        AIFunctionFactory.Create(
            ([Description("List of structured backlog task proposals, or omit for single task")] object? tasks = null,
             [Description("Task title if single proposal")] string? title = null,
             [Description("Task description with functional scope, acceptance criteria, and testing plan if single proposal")] string? description = null,
             [Description("Task priority (1 Highest - 5 Lowest) if single proposal")] int? priority = null,
             [Description("Prerequisite task IDs")] IReadOnlyList<int>? depends_on_ids = null,
             [Description("Merge mode if single proposal: Automatic, ManualReviewRequired, or ExternalPullRequest")] string? merge_mode = null,
             [Description("Task difficulty if single proposal: standard, difficult, or expert")] string? difficulty = null,
             [Description("Linked workspace attachment IDs")] IReadOnlyList<int>? attachment_ids = null,
             CancellationToken ct = default) =>
                module != null
                    ? module.ProposeTasksAsync(tasks, title, description, priority, depends_on_ids, merge_mode, difficulty, attachment_ids, ct)
                    : throw new InvalidOperationException(MissingModuleInstanceMessage),
            CanonicalToolNames.ProposeTasks,
            "Proposes structured backlog tasks for ideation (Draft) or triage (Suggested). Returns the created task(s) with assigned numeric ID(s). Each task represents a full Pull Request / Merge Request unit of work with build, test, and code review gates. Subsequent calls to edit_task, remove_task, or propose_tasks (depends_on_ids) can reference the returned numeric IDs."),

        AIFunctionFactory.Create(
            ([Description("Numeric ID of the backlog task")] int task_id,
             CancellationToken ct = default) =>
                module != null
                    ? module.GetTaskAsync(task_id, ct)
                    : throw new InvalidOperationException(MissingModuleInstanceMessage),
            CanonicalToolNames.GetTask,
            "Fetches the complete details of one backlog task (title, description, status, origin, hasExecutionRun, priority, difficulty, prerequisites, dependents) on demand."),

        AIFunctionFactory.Create(
            ([Description("Numeric ID of the unstarted task to edit")] int task_id,
             [Description("New title")] string? title = null,
             [Description("New full description (replaces the existing one)")] string? description = null,
             [Description("New priority (1 Critical - 5 Trivial)")] int? priority = null,
             [Description("Complete new list of prerequisite task IDs (replaces the existing list; cycles are rejected)")] int[]? depends_on_ids = null,
             [Description("New merge mode: Automatic, ManualReviewRequired, or ExternalPullRequest")] string? merge_mode = null,
             [Description("New base branch")] string? base_branch = null,
             [Description("New task difficulty: standard, difficult, or expert")] string? difficulty = null,
             [Description("Complete new list of linked workspace attachment IDs")] int[]? attachment_ids = null,
             CancellationToken ct = default) =>
                module != null
                    ? module.EditTaskAsync(task_id, title, description, priority, depends_on_ids, merge_mode, base_branch, difficulty, attachment_ids, ct)
                    : throw new InvalidOperationException(MissingModuleInstanceMessage),
            CanonicalToolNames.EditTask,
            "Edits an unstarted (Suggested or Open, never executed) backlog task. Omitted arguments are left unchanged. Only functionally resolved changes agreed with the maintainer should be applied."),

        AIFunctionFactory.Create(
            ([Description("Numeric ID of the unstarted task")] int task_id,
             [Description("New priority (1 Critical - 5 Trivial)")] int priority,
             CancellationToken ct = default) =>
                module != null
                    ? module.SetTaskPriorityAsync(task_id, priority, ct)
                    : throw new InvalidOperationException(MissingModuleInstanceMessage),
            CanonicalToolNames.SetTaskPriority,
            "Changes the priority tier of one unstarted backlog task."),

        AIFunctionFactory.Create(
            ([Description("Array of { task_id, priority } entries (priority 1 Critical - 5 Trivial)")] IReadOnlyList<TaskPriorityUpdate> updates,
             CancellationToken ct = default) =>
                module != null
                    ? module.ReworkPrioritiesAsync(updates, ct)
                    : throw new InvalidOperationException(MissingModuleInstanceMessage),
            CanonicalToolNames.ReworkPriorities,
            "Changes the priority tier of several unstarted backlog tasks in one call."),

        AIFunctionFactory.Create(
            ([Description("Numeric ID of the unstarted task to remove")] int task_id,
             [Description("Why the task is removed")] string? reason = null,
             CancellationToken ct = default) =>
                module != null
                    ? module.RemoveTaskAsync(task_id, reason, ct)
                    : throw new InvalidOperationException(MissingModuleInstanceMessage),
            CanonicalToolNames.RemoveTask,
            "Removes (cancels) an unstarted backlog task. Fails when active tasks still depend on it.")
    ];

    private BacklogToolScope RequireBacklogScope(string toolName)
    {
        if (!IsBacklogMutationAvailable)
        {
            throw new InvalidOperationException($"Tool '{toolName}' is not available in this session: no backlog mutation service is attached.");
        }

        if (!_workspaceId.HasValue)
        {
            throw new InvalidOperationException($"Tool '{toolName}' requires a workspace-scoped session.");
        }

        if (_backlogMutationPolicy == null)
        {
            return new BacklogToolScope(_workspaceId.Value, _workspaceRoot);
        }

        var repositoryPath = !string.IsNullOrWhiteSpace(_backlogMutationPolicy.HostRepositoryPath)
            ? _backlogMutationPolicy.HostRepositoryPath
            : _workspaceRoot;

        return new BacklogToolScope(
            _workspaceId.Value,
            repositoryPath,
            Mode: _backlogMutationPolicy.Mode,
            Actor: _backlogMutationPolicy.Actor,
            SessionOrigin: _backlogMutationPolicy.SessionOrigin,
            DefaultBaseBranch: _backlogMutationPolicy.DefaultBaseBranch,
            ExternalSource: _backlogMutationPolicy.ExternalSource,
            ExternalReferenceId: _backlogMutationPolicy.ExternalReferenceId,
            CloneDirectory: _backlogMutationPolicy.CloneDirectory,
            SessionTaskId: _backlogMutationPolicy.SessionTaskId);
    }

    private async Task<string> ExecuteWithBacklogServiceAsync(
        string toolName,
        BacklogToolScope backlogScope,
        Func<IBacklogMutationToolService, BacklogToolScope, Task<string>> action)
    {
        if (_hasExplicitBacklogMutationService)
        {
            if (_explicitBacklogMutationService == null)
            {
                throw new InvalidOperationException($"Tool '{toolName}' is not available in this session: no backlog mutation service is attached.");
            }

            return await ExecuteBacklogActionAsync(() => action(_explicitBacklogMutationService, backlogScope));
        }

        if (_attachBacklogMutationService && ScopeFactory != null)
        {
            await using var serviceScope = ScopeFactory.CreateAsyncScope();
            var scopedService = serviceScope.ServiceProvider.GetRequiredService<IBacklogMutationToolService>();
            return await ExecuteBacklogActionAsync(() => action(scopedService, backlogScope));
        }

        if (_attachBacklogMutationService && _defaultBacklogMutationService != null)
        {
            return await ExecuteBacklogActionAsync(() => action(_defaultBacklogMutationService, backlogScope));
        }

        throw new InvalidOperationException($"Tool '{toolName}' is not available in this session: no backlog mutation service is attached.");
    }

    private static async Task<string> ExecuteBacklogActionAsync(Func<Task<string>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception ex) when (ex is not AttachmentNotFoundException &&
            ex is TaskCannotBeModifiedException or TaskNotFoundException or CircularDependencyException or InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            return $"Error: {ex.Message}";
        }
    }

    public Task<string> GetTaskAsync(int taskId, CancellationToken cancellationToken = default)
    {
        var scope = RequireBacklogScope(CanonicalToolNames.GetTask);
        return ExecuteWithBacklogServiceAsync(
            CanonicalToolNames.GetTask,
            scope,
            (service, s) => service.GetTaskAsync(s, taskId, cancellationToken));
    }

    public Task<string> EditTaskAsync(
        int taskId,
        string? title,
        string? description,
        int? priority,
        IReadOnlyList<int>? dependsOnIds,
        string? mergeMode,
        string? baseBranch,
        CancellationToken cancellationToken) =>
        EditTaskAsync(taskId, title, description, priority, dependsOnIds, mergeMode, baseBranch, null, cancellationToken);

    public Task<string> EditTaskAsync(
        int taskId,
        string? title,
        string? description,
        int? priority,
        IReadOnlyList<int>? dependsOnIds,
        string? mergeMode,
        string? baseBranch,
        string? difficulty,
        CancellationToken cancellationToken) =>
        EditTaskAsync(taskId, title, description, priority, dependsOnIds, mergeMode, baseBranch, difficulty, null, cancellationToken);

    public Task<string> EditTaskAsync(
        int taskId,
        string? title = null,
        string? description = null,
        int? priority = null,
        IReadOnlyList<int>? dependsOnIds = null,
        string? mergeMode = null,
        string? baseBranch = null,
        string? difficulty = null,
        IReadOnlyList<int>? attachmentIds = null,
        CancellationToken cancellationToken = default)
    {
        var scope = RequireBacklogScope(CanonicalToolNames.EditTask);
        var request = new TaskEditRequest(taskId, title, description, priority, dependsOnIds, mergeMode, baseBranch, difficulty, attachmentIds);
        return ExecuteWithBacklogServiceAsync(
            CanonicalToolNames.EditTask,
            scope,
            async (service, s) =>
            {
                var result = await service.EditTaskAsync(s, request, cancellationToken);
                if (!BacklogMutationToolService.IsUnchangedEditResult(taskId, result) &&
                    !ModifiedTaskIds.Contains(taskId))
                {
                    ModifiedTaskIds.Add(taskId);
                }

                return result;
            });
    }

    public Task<string> SetTaskPriorityAsync(int taskId, int priority, CancellationToken cancellationToken = default)
    {
        var scope = RequireBacklogScope(CanonicalToolNames.SetTaskPriority);
        return ExecuteWithBacklogServiceAsync(
            CanonicalToolNames.SetTaskPriority,
            scope,
            (service, s) => service.SetTaskPriorityAsync(s, taskId, priority, cancellationToken));
    }

    public Task<string> ReworkPrioritiesAsync(IReadOnlyList<TaskPriorityUpdate> updates, CancellationToken cancellationToken = default)
    {
        var scope = RequireBacklogScope(CanonicalToolNames.ReworkPriorities);
        return ExecuteWithBacklogServiceAsync(
            CanonicalToolNames.ReworkPriorities,
            scope,
            (service, s) => service.ReworkPrioritiesAsync(s, updates, cancellationToken));
    }

    public Task<string> RemoveTaskAsync(int taskId, string? reason = null, CancellationToken cancellationToken = default)
    {
        var scope = RequireBacklogScope(CanonicalToolNames.RemoveTask);
        return ExecuteWithBacklogServiceAsync(
            CanonicalToolNames.RemoveTask,
            scope,
            async (service, s) =>
            {
                var result = await service.RemoveTaskAsync(s, taskId, reason, cancellationToken);
                if (!RemovedTaskIds.Contains(taskId))
                {
                    RemovedTaskIds.Add(taskId);
                }

                CreatedTaskIds.Remove(taskId);
                ModifiedTaskIds.Remove(taskId);
                PendingProposals.RemoveAll(p => p.TaskId == taskId);
                return result;
            });
    }

    public string ProposeTasks(IReadOnlyList<TaskProposal> tasks) =>
        ProposeTasks((object?)tasks, null, null, null, null);

    public string ProposeTasks(
        object? tasks = null,
        string? title = null,
        string? description = null,
        int? priority = null) =>
        ProposeTasks(tasks, title, description, priority, null);

    public string ProposeTasks(
        object? tasks = null,
        string? title = null,
        string? description = null,
        int? priority = null,
        IReadOnlyList<int>? dependsOnIds = null) =>
        ProposeTasksAsync(tasks, title, description, priority, dependsOnIds).GetAwaiter().GetResult();

    public Task<string> ProposeTasksAsync(
        object? tasks,
        string? title,
        string? description,
        int? priority,
        IReadOnlyList<int>? dependsOnIds,
        CancellationToken cancellationToken) =>
        ProposeTasksAsync(tasks, title, description, priority, dependsOnIds, null, null, cancellationToken);

    public Task<string> ProposeTasksAsync(
        object? tasks,
        string? title,
        string? description,
        int? priority,
        IReadOnlyList<int>? dependsOnIds,
        string? mergeMode,
        CancellationToken cancellationToken) =>
        ProposeTasksAsync(tasks, title, description, priority, dependsOnIds, mergeMode, null, cancellationToken);

    public Task<string> ProposeTasksAsync(
        object? tasks,
        string? title,
        string? description,
        int? priority,
        IReadOnlyList<int>? dependsOnIds,
        string? mergeMode,
        string? difficulty,
        CancellationToken cancellationToken) =>
        ProposeTasksAsync(tasks, title, description, priority, dependsOnIds, mergeMode, difficulty, null, cancellationToken);

    public async Task<string> ProposeTasksAsync(
        object? tasks = null,
        string? title = null,
        string? description = null,
        int? priority = null,
        IReadOnlyList<int>? dependsOnIds = null,
        string? mergeMode = null,
        string? difficulty = null,
        IReadOnlyList<int>? attachmentIds = null,
        CancellationToken cancellationToken = default)
    {
        var isTriageMode = _backlogMutationPolicy?.Mode == BacklogMutationMode.Triage;
        var discovered = ExtractTaskProposals(
            tasks,
            title,
            description,
            priority,
            dependsOnIds,
            mergeMode,
            difficulty,
            attachmentIds,
            extractDescriptionCitations: !isTriageMode);
        if (discovered.Count == 0)
        {
            return "No tasks proposed.";
        }

        for (int i = 0; i < discovered.Count; i++)
        {
            var task = discovered[i];
            var deps = task.DependsOnTitles?.ToList() ?? new List<string>();
            var resolvedAttachmentIds = ResolveProposalAttachmentIds(task.AttachmentIds, task.Description, !isTriageMode);

            discovered[i] = task with
            {
                DependsOnTitles = deps,
                AttachmentIds = resolvedAttachmentIds
            };
        }

        if (IsBacklogMutationAvailable && _workspaceId.HasValue)
        {
            var backlogScope = RequireBacklogScope(CanonicalToolNames.ProposeTasks);
            return await ExecuteWithBacklogServiceAsync(
                CanonicalToolNames.ProposeTasks,
                backlogScope,
                async (service, scope) =>
                {
                    var result = await service.ProposeTasksAsync(scope, discovered, cancellationToken);
                    if (string.IsNullOrWhiteSpace(result))
                    {
                        PendingProposals.AddRange(discovered);
                        return JsonSerializer.Serialize(discovered);
                    }

                    try
                    {
                        using var doc = JsonDocument.Parse(result);
                        if (doc.RootElement.ValueKind == JsonValueKind.Object)
                        {
                            var id = TryReadIntProperty(doc.RootElement, "id");
                            var prereqs = TryReadIntArrayProperty(doc.RootElement, "prerequisites") ?? discovered[0].DependsOnIds;
                            PendingProposals.Add(discovered[0] with { TaskId = id, DependsOnIds = prereqs });
                            if (id is > 0)
                            {
                                TrackCreatedTaskId(id.Value);
                            }
                        }
                        else if (doc.RootElement.ValueKind == JsonValueKind.Array)
                        {
                            var arr = doc.RootElement.EnumerateArray().ToList();
                            for (int i = 0; i < discovered.Count; i++)
                            {
                                int? id = null;
                                IReadOnlyList<int>? prereqs = discovered[i].DependsOnIds;
                                if (i < arr.Count && arr[i].ValueKind == JsonValueKind.Object)
                                {
                                    id = TryReadIntProperty(arr[i], "id");
                                    prereqs = TryReadIntArrayProperty(arr[i], "prerequisites") ?? prereqs;
                                }
                                PendingProposals.Add(discovered[i] with { TaskId = id, DependsOnIds = prereqs });
                                if (id is > 0)
                                {
                                    TrackCreatedTaskId(id.Value);
                                }
                            }
                        }
                        else
                        {
                            PendingProposals.AddRange(discovered);
                        }
                    }
                    catch (JsonException)
                    {
                        // Fallback when a stubbed or custom service returns a non-JSON payload.
                        PendingProposals.AddRange(discovered);
                    }

                    return result;
                });
        }

        if (!isTriageMode && ScopeFactory != null && _workspaceId.HasValue)
        {
            await using var serviceScope = ScopeFactory.CreateAsyncScope();
            var attachmentRepository = serviceScope.ServiceProvider.GetService<ITaskAttachmentRepository>();
            if (attachmentRepository != null)
            {
                foreach (var proposal in discovered)
                {
                    await AttachmentCitationParser.ValidateAndResolveWorkspaceIdsAsync(
                        attachmentRepository,
                        _workspaceId.Value,
                        proposal.AttachmentIds,
                        proposal.Description,
                        cancellationToken);
                }
            }
        }

        for (int i = 0; i < discovered.Count; i++)
        {
            PendingProposals.Add(discovered[i]);
        }

        if (discovered.Count == 1)
        {
            return JsonSerializer.Serialize(PendingProposals.Last(), new JsonSerializerOptions { WriteIndented = true });
        }

        var json = JsonSerializer.Serialize(PendingProposals, new JsonSerializerOptions { WriteIndented = true });
        return $"Proposed {discovered.Count} backlog task(s):\n{json}";
    }

    private static List<TaskProposal> ExtractTaskProposals(
        object? tasks,
        string? title,
        string? description,
        int? priority,
        IReadOnlyList<int>? dependsOnIds,
        string? mergeMode,
        string? difficulty,
        IReadOnlyList<int>? attachmentIds,
        bool extractDescriptionCitations)
    {
        var discovered = new List<TaskProposal>();

        if (!string.IsNullOrWhiteSpace(title) || !string.IsNullOrWhiteSpace(description))
        {
            var effectiveTitle = !IsGenericOrEmptyTitle(title)
                ? title!.Trim()
                : DeriveTitleFromDescription(description);
            var resolvedAttachments = ResolveProposalAttachmentIds(attachmentIds, description, extractDescriptionCitations);
            discovered.Add(new TaskProposal(
                effectiveTitle,
                description ?? string.Empty,
                priority: priority ?? TaskPriority.Normal,
                dependsOnIds: dependsOnIds,
                mergeMode: mergeMode,
                difficulty: difficulty,
                attachmentIds: resolvedAttachments));
        }

        if (tasks != null)
        {
            var batchProposals = new List<TaskProposal>();
            if (tasks is IEnumerable<TaskProposal> enumProposals)
            {
                batchProposals.AddRange(enumProposals);
            }
            else if (tasks is JsonElement je)
            {
                batchProposals.AddRange(ParseJsonElementTasks(je, extractDescriptionCitations));
            }
            else if (tasks is string str)
            {
                if (!string.IsNullOrWhiteSpace(str))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(str);
                        batchProposals.AddRange(ParseJsonElementTasks(doc.RootElement, extractDescriptionCitations));
                    }
                    catch (JsonException)
                    {
                        // Ignore non-JSON string input
                    }
                }
            }
            else
            {
                try
                {
                    var jsonStr = JsonSerializer.Serialize(tasks);
                    using var doc = JsonDocument.Parse(jsonStr);
                    batchProposals.AddRange(ParseJsonElementTasks(doc.RootElement, extractDescriptionCitations));
                }
                catch (JsonException)
                {
                    // Ignore non-JSON serialized input
                }
            }

            if (!string.IsNullOrWhiteSpace(mergeMode) || !string.IsNullOrWhiteSpace(difficulty) || attachmentIds is { Count: > 0 })
            {
                var fallbackMergeMode = string.IsNullOrWhiteSpace(mergeMode) ? null : mergeMode;
                var fallbackDifficulty = string.IsNullOrWhiteSpace(difficulty) ? null : difficulty;
                for (int i = 0; i < batchProposals.Count; i++)
                {
                    var proposal = batchProposals[i];
                    var effectiveMergeMode = proposal.MergeMode ?? fallbackMergeMode;
                    var effectiveDifficulty = proposal.Difficulty ?? fallbackDifficulty;
                    IReadOnlyList<int>? effectiveAttachmentIds = proposal.AttachmentIds;
                    if (attachmentIds is { Count: > 0 })
                    {
                        IReadOnlyList<int> combinedExplicit = proposal.AttachmentIds != null
                            ? proposal.AttachmentIds.Concat(attachmentIds).ToList()
                            : attachmentIds;
                        effectiveAttachmentIds = ResolveProposalAttachmentIds(combinedExplicit, proposal.Description, extractDescriptionCitations);
                    }

                    if (!string.Equals(effectiveMergeMode, proposal.MergeMode, StringComparison.Ordinal) ||
                        !string.Equals(effectiveDifficulty, proposal.Difficulty, StringComparison.Ordinal) ||
                        !ReferenceEquals(effectiveAttachmentIds, proposal.AttachmentIds))
                    {
                        batchProposals[i] = proposal with
                        {
                            MergeMode = effectiveMergeMode,
                            Difficulty = effectiveDifficulty,
                            AttachmentIds = effectiveAttachmentIds
                        };
                    }
                }
            }

            discovered.AddRange(batchProposals);
        }

        return discovered;
    }

    private static List<TaskProposal> ParseJsonElementTasks(JsonElement element, bool extractDescriptionCitations)
    {
        var list = new List<TaskProposal>();
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var p = ParseSingleTaskElement(item, extractDescriptionCitations);
                if (p != null) list.Add(p);
            }
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("tasks", out var tasksProp))
            {
                list.AddRange(ParseJsonElementTasks(tasksProp, extractDescriptionCitations));
            }
            else
            {
                var p = ParseSingleTaskElement(element, extractDescriptionCitations);
                if (p != null) list.Add(p);
            }
        }
        else if (element.ValueKind == JsonValueKind.String)
        {
            var str = element.GetString();
            if (!string.IsNullOrWhiteSpace(str) && str.TrimStart().StartsWith("[", StringComparison.Ordinal))
            {
                try
                {
                    using var innerDoc = JsonDocument.Parse(str);
                    list.AddRange(ParseJsonElementTasks(innerDoc.RootElement, extractDescriptionCitations));
                }
                catch (JsonException)
                {
                    // String starting with '[' was not valid JSON; fallback to other parsing heuristics
                }
            }
        }
        return list;
    }

    private static TaskProposal? ParseSingleTaskElement(JsonElement item, bool extractDescriptionCitations)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;

        string title = string.Empty;
        if (item.TryGetProperty("title", out var t) || item.TryGetProperty("name", out t))
        {
            title = t.GetString() ?? string.Empty;
        }

        string desc = string.Empty;
        if (item.TryGetProperty("description", out var d) || item.TryGetProperty("desc", out d))
        {
            desc = d.GetString() ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(desc)) return null;

        if (IsGenericOrEmptyTitle(title))
        {
            title = DeriveTitleFromDescription(desc);
        }

        int priority = TaskPriority.Normal;
        if (item.TryGetProperty("priority", out var p) && p.TryGetInt32(out var pVal))
        {
            priority = pVal;
        }

        List<string>? deps = null;
        if ((item.TryGetProperty("dependsOnTitles", out var depsProp) || item.TryGetProperty("depends_on_titles", out depsProp)) &&
            depsProp.ValueKind == JsonValueKind.Array)
        {
            deps = new List<string>();
            foreach (var dep in depsProp.EnumerateArray())
            {
                if (dep.ValueKind == JsonValueKind.String)
                {
                    var depStr = dep.GetString();
                    if (!string.IsNullOrWhiteSpace(depStr)) deps.Add(depStr.Trim());
                }
                else if (dep.ValueKind == JsonValueKind.Number && dep.TryGetInt32(out var depNum))
                {
                    deps.Add(depNum.ToString(CultureInfo.InvariantCulture));
                }
            }
        }

        List<int>? depIds = null;
        if ((item.TryGetProperty("depends_on_ids", out var depIdsProp) || item.TryGetProperty("dependsOnIds", out depIdsProp)) && depIdsProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var dep in depIdsProp.EnumerateArray())
            {
                if (dep.ValueKind == JsonValueKind.Number && dep.TryGetInt32(out var parsedId))
                {
                    depIds ??= new List<int>();
                    depIds.Add(parsedId);
                }
                else if (dep.ValueKind == JsonValueKind.String)
                {
                    var raw = dep.GetString()?.Trim();
                    if (int.TryParse(raw?.TrimStart('#'), NumberStyles.None, CultureInfo.InvariantCulture, out var parsedStrId))
                    {
                        depIds ??= new List<int>();
                        depIds.Add(parsedStrId);
                    }
                    else if (!string.IsNullOrWhiteSpace(raw))
                    {
                        deps ??= new List<string>();
                        if (!deps.Contains(raw, StringComparer.OrdinalIgnoreCase))
                        {
                            deps.Add(raw);
                        }
                    }
                }
            }
        }

        string type = "feature";
        if (item.TryGetProperty("type", out var ty))
        {
            type = ty.GetString() ?? "feature";
        }

        string? mergeMode = null;
        if ((item.TryGetProperty("merge_mode", out var mm) || item.TryGetProperty("mergeMode", out mm)) && mm.ValueKind == JsonValueKind.String)
        {
            mergeMode = mm.GetString();
        }

        string? difficulty = null;
        if ((item.TryGetProperty("difficulty", out var diffProp) ||
             item.TryGetProperty("task_difficulty", out diffProp) ||
             item.TryGetProperty("taskDifficulty", out diffProp) ||
             item.TryGetProperty("complexity", out diffProp)) &&
            diffProp.ValueKind == JsonValueKind.String)
        {
            difficulty = diffProp.GetString();
        }

        List<int>? explicitAttachmentIds = null;
        if ((item.TryGetProperty("attachment_ids", out var attProp) ||
             item.TryGetProperty("attachmentIds", out attProp) ||
             item.TryGetProperty("AttachmentIds", out attProp)) &&
            attProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in attProp.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var parsedId))
                {
                    explicitAttachmentIds ??= new List<int>();
                    explicitAttachmentIds.Add(parsedId);
                }
                else if (el.ValueKind == JsonValueKind.String)
                {
                    var rawId = el.GetString()?.Trim();
                    if (int.TryParse(rawId?.TrimStart('#'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedStrId))
                    {
                        explicitAttachmentIds ??= new List<int>();
                        explicitAttachmentIds.Add(parsedStrId);
                    }
                    else if (!string.IsNullOrWhiteSpace(rawId))
                    {
                        explicitAttachmentIds ??= new List<int>();
                        explicitAttachmentIds.Add(-1);
                    }
                }
                else if (el.ValueKind != JsonValueKind.Null && el.ValueKind != JsonValueKind.Undefined)
                {
                    explicitAttachmentIds ??= new List<int>();
                    explicitAttachmentIds.Add(-1);
                }
            }
        }

        var resolvedAttachmentIds = ResolveProposalAttachmentIds(explicitAttachmentIds, desc, extractDescriptionCitations);
        return new TaskProposal(
            title,
            desc,
            type,
            null,
            null,
            priority,
            deps,
            depIds,
            null,
            mergeMode,
            difficulty,
            resolvedAttachmentIds);
    }

    private static IReadOnlyList<int>? ResolveProposalAttachmentIds(
        IReadOnlyList<int>? explicitAttachmentIds,
        string? description,
        bool extractDescriptionCitations)
    {
        if (extractDescriptionCitations)
        {
            var unionAttachmentIds = AttachmentCitationParser.UnionAttachmentIds(explicitAttachmentIds, description);
            return unionAttachmentIds.Count > 0 ? unionAttachmentIds : null;
        }

        if (explicitAttachmentIds is not { Count: > 0 })
        {
            return null;
        }

        return explicitAttachmentIds.Distinct().ToList();
    }

    private void TrackCreatedTaskId(int taskId)
    {
        if (taskId > 0 && !CreatedTaskIds.Contains(taskId))
        {
            CreatedTaskIds.Add(taskId);
        }
    }

    private static int? TryReadIntProperty(JsonElement obj, string propertyName) =>
        obj.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var val)
            ? val
            : null;

    private static IReadOnlyList<int>? TryReadIntArrayProperty(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var prop) || prop.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var list = new List<int>();
        foreach (var el in prop.EnumerateArray())
        {
            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var id))
            {
                list.Add(id);
            }
        }

        return list.Count > 0 ? list : null;
    }

    private static bool IsGenericOrEmptyTitle(string? title) =>
        string.IsNullOrWhiteSpace(title) || string.Equals(title.Trim(), GenericTaskTitle, StringComparison.OrdinalIgnoreCase);

    private static string DeriveTitleFromDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return GenericTaskTitle;
        var firstLine = description.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? GenericTaskTitle;
        firstLine = firstLine.TrimStart('#', ' ', '-', '*', '1', '2', '3', '4', '5', '6', '7', '8', '9', '.', ':');
        if (firstLine.Length > 80)
        {
            var truncated = firstLine[..80];
            var lastSpace = truncated.LastIndexOf(' ');
            if (lastSpace > 30) truncated = truncated[..lastSpace];
            return truncated;
        }
        return string.IsNullOrWhiteSpace(firstLine) ? GenericTaskTitle : firstLine;
    }
}

