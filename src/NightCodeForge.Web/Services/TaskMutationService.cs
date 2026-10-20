using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;

namespace NightCodeForge.Web.Services;

/// <summary>
/// Service interface for maintainer-initiated task mutations in the Web presentation layer,
/// encapsulating temporal history snapshot capture, attribution, and disk synchronization.
/// </summary>
public interface ITaskMutationService
{
    Task UpdateTitleAsync(ForgeTask task, string newTitle, IEnumerable<int>? prerequisiteIds = null, CancellationToken cancellationToken = default);
    Task UpdateDescriptionAsync(ForgeTask task, string newDescription, IEnumerable<int>? prerequisiteIds = null, CancellationToken cancellationToken = default);
    Task UpdatePriorityAsync(ForgeTask task, int newPriority, IEnumerable<int>? prerequisiteIds = null, CancellationToken cancellationToken = default);
    Task UpdateMergeModeAsync(ForgeTask task, MergeMode newMergeMode, IEnumerable<int>? prerequisiteIds = null, CancellationToken cancellationToken = default);
    Task UpdateDependenciesAsync(ForgeTask task, IEnumerable<int> prerequisiteIds, CancellationToken cancellationToken = default);
}

/// <summary>
/// Encapsulates task property mutations, audit history attribution to <see cref="AuditActors.User"/>,
/// and disk markdown synchronization to ensure clean separation of UI presentation from business logic.
/// </summary>
public class TaskMutationService : ITaskMutationService
{
    private readonly ITaskRepository _taskRepository;
    private readonly IWorkspaceRepository _workspaceRepository;
    private readonly ITaskMarkdownSyncService _markdownSyncService;
    private readonly ILogger<TaskMutationService> _logger;

    public TaskMutationService(
        ITaskRepository taskRepository,
        IWorkspaceRepository workspaceRepository,
        ITaskMarkdownSyncService markdownSyncService,
        ILogger<TaskMutationService> logger)
    {
        _taskRepository = taskRepository ?? throw new ArgumentNullException(nameof(taskRepository));
        _workspaceRepository = workspaceRepository ?? throw new ArgumentNullException(nameof(workspaceRepository));
        _markdownSyncService = markdownSyncService ?? throw new ArgumentNullException(nameof(markdownSyncService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task UpdateTitleAsync(
        ForgeTask task,
        string newTitle,
        IEnumerable<int>? prerequisiteIds = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        var trimmed = newTitle?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new ArgumentException("Task title cannot be empty.", nameof(newTitle));
        }

        if (string.Equals(task.Title, trimmed, StringComparison.Ordinal))
        {
            return;
        }

        var oldTitle = task.Title;
        task.Title = trimmed;
        task.UpdatedAt = DateTime.UtcNow;

        await _taskRepository.UpdateWithHistoryAsync(
            task,
            prerequisiteIds,
            AuditActors.User,
            $"Renamed task from \"{oldTitle}\" to \"{trimmed}\"",
            cancellationToken);

        await SyncMarkdownToDiskAsync(task, prerequisiteIds, cancellationToken);
    }

    public async Task UpdateDescriptionAsync(
        ForgeTask task,
        string newDescription,
        IEnumerable<int>? prerequisiteIds = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        var trimmed = newDescription?.Trim() ?? string.Empty;
        if (string.Equals(task.Description, trimmed, StringComparison.Ordinal))
        {
            return;
        }

        task.Description = trimmed;
        task.UpdatedAt = DateTime.UtcNow;

        await _taskRepository.UpdateWithHistoryAsync(
            task,
            prerequisiteIds,
            AuditActors.User,
            "Updated task description and specification",
            cancellationToken);

        await SyncMarkdownToDiskAsync(task, prerequisiteIds, cancellationToken);
    }

    public async Task UpdatePriorityAsync(
        ForgeTask task,
        int newPriority,
        IEnumerable<int>? prerequisiteIds = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (newPriority < TaskPriority.Critical || newPriority > TaskPriority.Trivial)
        {
            throw new ArgumentOutOfRangeException(nameof(newPriority), $"Priority must be between {TaskPriority.Critical} and {TaskPriority.Trivial}.");
        }

        if (task.Priority == newPriority)
        {
            return;
        }

        var oldPriority = task.Priority;
        task.Priority = newPriority;
        task.UpdatedAt = DateTime.UtcNow;

        await _taskRepository.UpdateWithHistoryAsync(
            task,
            prerequisiteIds,
            AuditActors.User,
            $"Updated Priority from P{oldPriority} to P{newPriority}",
            cancellationToken);

        await SyncMarkdownToDiskAsync(task, prerequisiteIds, cancellationToken);
    }

    public async Task UpdateMergeModeAsync(
        ForgeTask task,
        MergeMode newMergeMode,
        IEnumerable<int>? prerequisiteIds = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (!Enum.IsDefined(newMergeMode))
        {
            throw new ArgumentOutOfRangeException(nameof(newMergeMode), $"Invalid merge mode: {newMergeMode}");
        }

        if (task.MergeMode == newMergeMode)
        {
            return;
        }

        var oldMode = task.MergeMode;
        task.MergeMode = newMergeMode;
        task.UpdatedAt = DateTime.UtcNow;

        await _taskRepository.UpdateWithHistoryAsync(
            task,
            prerequisiteIds,
            AuditActors.User,
            $"Updated MergeMode from {oldMode} to {newMergeMode}",
            cancellationToken);

        await SyncMarkdownToDiskAsync(task, prerequisiteIds, cancellationToken);
    }

    public async Task UpdateDependenciesAsync(
        ForgeTask task,
        IEnumerable<int> prerequisiteIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(prerequisiteIds);

        var newDeps = prerequisiteIds.Distinct().OrderBy(x => x).ToList();
        if (newDeps.Contains(task.Id))
        {
            throw new ArgumentException($"Task #{task.Id} cannot depend on itself.", nameof(prerequisiteIds));
        }

        var currentDeps = task.Dependencies?.Select(d => d.DependsOnTaskId).OrderBy(x => x).ToList() ?? new List<int>();
        if (currentDeps.SequenceEqual(newDeps))
        {
            return;
        }

        task.UpdatedAt = DateTime.UtcNow;

        await _taskRepository.UpdateWithHistoryAsync(
            task,
            newDeps,
            AuditActors.User,
            $"Updated dependencies ({newDeps.Count} prerequisite(s))",
            cancellationToken);

        if (task.Dependencies != null)
        {
            task.Dependencies.Clear();
            foreach (var depId in newDeps)
            {
                task.Dependencies.Add(new TaskDependency { TaskId = task.Id, DependsOnTaskId = depId });
            }
        }

        await SyncMarkdownToDiskAsync(task, newDeps, cancellationToken);
    }

    private async Task SyncMarkdownToDiskAsync(
        ForgeTask task,
        IEnumerable<int>? prerequisiteIds,
        CancellationToken cancellationToken)
    {
        try
        {
            var workspace = await _workspaceRepository.GetByIdAsync(task.WorkspaceId, cancellationToken);
            if (workspace != null && !string.IsNullOrWhiteSpace(workspace.RepositoryPath) && Directory.Exists(workspace.RepositoryPath))
            {
                IReadOnlyList<int> deps;
                if (prerequisiteIds != null)
                {
                    deps = prerequisiteIds.ToList();
                }
                else if (task.Dependencies != null && task.Dependencies.Count > 0)
                {
                    deps = task.Dependencies.Select(d => d.DependsOnTaskId).ToList();
                }
                else
                {
                    deps = await _taskRepository.GetDependenciesAsync(task.Id, cancellationToken);
                }

                await _markdownSyncService.SyncTaskToDiskAsync(task, workspace.RepositoryPath, deps, cancellationToken: cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to sync task #{TaskId} markdown to repository disk", task.Id);
        }
    }
}
