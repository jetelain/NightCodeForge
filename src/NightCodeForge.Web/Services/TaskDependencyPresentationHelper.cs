using NightCodeForge.Core.Domain;

namespace NightCodeForge.Web.Services;

/// <summary>
/// View-model projection for a prerequisite or downstream dependent task entry.
/// </summary>
public sealed record TaskDependencyItemViewModel(
    int TaskId,
    string Title,
    string StatusLabel,
    TaskOverallStatus? Status,
    bool IsClosed,
    bool IsBlocking,
    bool RequiresUpstreamAttention,
    bool IsWaitingForHuman,
    string CssClass,
    string ChipCssClass,
    string TaskUrl);

/// <summary>
/// Centralizes prerequisite resolution, blocker/stalled detection, workspace-aware URLs,
/// and UI formatting for suspended or dependency-blocked tasks outside Razor markup.
/// </summary>
public static class TaskDependencyPresentationHelper
{
    private const int MaxInlineBadgeBlockerIds = 3;

    /// <summary>
    /// Resolves distinct prerequisite task IDs using explicit IDs when provided,
    /// falling back to <see cref="ForgeTask.Dependencies"/>.
    /// </summary>
    public static IReadOnlyList<int> ResolvePrerequisiteIds(
        ForgeTask task,
        IReadOnlyList<int>? prerequisiteIds = null)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (prerequisiteIds != null)
        {
            return prerequisiteIds.Where(id => id > 0).Distinct().ToList();
        }

        if (task.Dependencies is { Count: > 0 })
        {
            return task.Dependencies
                .Select(d => d.DependsOnTaskId)
                .Where(id => id > 0)
                .Distinct()
                .ToList();
        }

        return Array.Empty<int>();
    }

    /// <summary>
    /// Returns prerequisite <see cref="ForgeTask"/> entries present in <paramref name="allTasks"/>
    /// whose status is not <see cref="TaskOverallStatus.Closed"/>.
    /// </summary>
    public static IReadOnlyList<ForgeTask> GetUnclosedBlockers(
        ForgeTask task,
        IReadOnlyList<ForgeTask>? allTasks,
        IReadOnlyList<int>? prerequisiteIds = null)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (allTasks is not { Count: > 0 })
        {
            return Array.Empty<ForgeTask>();
        }

        var resolvedIds = ResolvePrerequisiteIds(task, prerequisiteIds);
        if (resolvedIds.Count == 0)
        {
            return Array.Empty<ForgeTask>();
        }

        var taskLookup = BuildTaskLookup(allTasks);
        var blockers = new List<ForgeTask>();
        foreach (var id in resolvedIds)
        {
            if (taskLookup.TryGetValue(id, out var blocker) && blocker.Status != TaskOverallStatus.Closed)
            {
                blockers.Add(blocker);
            }
        }

        return blockers;
    }

    /// <summary>
    /// Returns true when the task has at least one unclosed prerequisite in <paramref name="allTasks"/>.
    /// </summary>
    public static bool HasUnclosedBlockers(
        ForgeTask task,
        IReadOnlyList<ForgeTask>? allTasks,
        IReadOnlyList<int>? prerequisiteIds = null)
    {
        return GetUnclosedBlockers(task, allTasks, prerequisiteIds).Count > 0;
    }

    /// <summary>
    /// Returns true when an <see cref="TaskOverallStatus.Open"/> non-running task has unclosed blockers.
    /// </summary>
    public static bool IsSuspendedByBlockers(
        ForgeTask task,
        IReadOnlyList<ForgeTask>? allTasks,
        IReadOnlyList<int>? prerequisiteIds = null,
        bool isRunning = false)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (task.Status != TaskOverallStatus.Open || isRunning)
        {
            return false;
        }

        return HasUnclosedBlockers(task, allTasks, prerequisiteIds);
    }

    /// <summary>
    /// Returns true when an unclosed prerequisite requires manual intervention or is in a terminal non-closed state
    /// and therefore will not close automatically on its own.
    /// </summary>
    public static bool IsStalledOrFailedBlocker(ForgeTask blocker)
    {
        ArgumentNullException.ThrowIfNull(blocker);

        if (blocker.Status == TaskOverallStatus.Closed)
        {
            return false;
        }

        if (blocker.IsWaitingForHuman || blocker.IsDeferredByPause)
        {
            return true;
        }

        if (blocker.Status == TaskOverallStatus.Ready && blocker.MergeMode != MergeMode.Automatic)
        {
            return true;
        }

        return blocker.Status is TaskOverallStatus.Failed
            or TaskOverallStatus.Rejected
            or TaskOverallStatus.Cancelled
            or TaskOverallStatus.Draft
            or TaskOverallStatus.Suggested
            or TaskOverallStatus.Triage;
    }

    /// <summary>
    /// Returns all blockers from <paramref name="blockers"/> that require upstream intervention.
    /// </summary>
    public static IReadOnlyList<ForgeTask> GetStalledOrFailedBlockers(IReadOnlyList<ForgeTask>? blockers)
    {
        if (blockers is not { Count: > 0 })
        {
            return Array.Empty<ForgeTask>();
        }

        return blockers.Where(IsStalledOrFailedBlocker).ToList();
    }

    /// <summary>
    /// Returns true when any blocker in <paramref name="blockers"/> requires upstream intervention.
    /// </summary>
    public static bool HasStalledOrFailedBlocker(IReadOnlyList<ForgeTask>? blockers)
    {
        return blockers is { Count: > 0 } && blockers.Any(IsStalledOrFailedBlocker);
    }

    /// <summary>
    /// Returns true when the task has any unclosed prerequisite requiring upstream intervention.
    /// </summary>
    public static bool HasStalledOrFailedBlocker(
        ForgeTask task,
        IReadOnlyList<ForgeTask>? allTasks,
        IReadOnlyList<int>? prerequisiteIds = null)
    {
        return HasStalledOrFailedBlocker(GetUnclosedBlockers(task, allTasks, prerequisiteIds));
    }

    /// <summary>
    /// Formats a concise comma-separated list of blocker task IDs (for example <c>#1</c> or <c>#1, #3</c>).
    /// </summary>
    public static string FormatBlockerReasonLabel(IReadOnlyList<ForgeTask>? blockers)
    {
        if (blockers is not { Count: > 0 })
        {
            return string.Empty;
        }

        if (blockers.Count <= MaxInlineBadgeBlockerIds)
        {
            return string.Join(", ", blockers.Select(b => $"#{b.Id}"));
        }

        var visible = string.Join(", ", blockers.Take(MaxInlineBadgeBlockerIds).Select(b => $"#{b.Id}"));
        return $"{visible} (+{blockers.Count - MaxInlineBadgeBlockerIds})";
    }

    /// <summary>
    /// Formats the compact Kanban badge label (for example <c>⏸️ Blocked on #1</c>).
    /// </summary>
    public static string FormatSuspendedBadgeText(IReadOnlyList<ForgeTask>? blockers)
    {
        var label = FormatBlockerReasonLabel(blockers);
        return string.IsNullOrEmpty(label) ? string.Empty : $"⏸️ Blocked on {label}";
    }

    /// <summary>
    /// Formats the detailed suspension banner message displayed in task detail views and drawers.
    /// </summary>
    public static string FormatSuspensionBannerText(IReadOnlyList<ForgeTask>? blockers)
    {
        if (blockers is not { Count: > 0 })
        {
            return string.Empty;
        }

        var details = string.Join(", ", blockers.Select(b => $"#{b.Id} ({b.Title} — {b.Status})"));
        return $"⏸️ Task execution is suspended awaiting completion of {details}. Worktree will automatically initialize from the updated base branch once all prerequisites reach Closed.";
    }

    /// <summary>
    /// Formats the upstream-attention warning message when one or more blockers are failed, stalled, or awaiting manual action.
    /// </summary>
    public static string FormatStalledOrFailedWarningText(IReadOnlyList<ForgeTask>? stalledOrFailedBlockers)
    {
        if (stalledOrFailedBlockers is not { Count: > 0 })
        {
            return string.Empty;
        }

        var details = string.Join(", ", stalledOrFailedBlockers.Select(b => FormatStalledBlockerDetail(b)));
        return $"Upstream attention required: {details} will not close automatically without manual intervention.";
    }

    private static string FormatStalledBlockerDetail(ForgeTask blocker)
    {
        var reasons = new List<string> { blocker.Status.ToString() };
        if (blocker.IsWaitingForHuman)
        {
            reasons.Add("waiting for human");
        }

        if (blocker.IsDeferredByPause)
        {
            reasons.Add("deferred by pause");
        }

        if (blocker.Status == TaskOverallStatus.Ready && blocker.MergeMode != MergeMode.Automatic)
        {
            reasons.Add(blocker.MergeMode == MergeMode.ExternalPullRequest
                ? "external PR merge required"
                : "manual review required");
        }

        return $"#{blocker.Id} ({string.Join(", ", reasons)})";
    }

    /// <summary>
    /// Builds view-model items for a task's prerequisites, with workspace-aware URLs.
    /// </summary>
    public static IReadOnlyList<TaskDependencyItemViewModel> ResolvePrerequisiteItems(
        ForgeTask task,
        IReadOnlyList<ForgeTask>? allTasks,
        int? workspaceId,
        IReadOnlyList<int>? prerequisiteIds = null)
    {
        ArgumentNullException.ThrowIfNull(task);

        var resolvedIds = ResolvePrerequisiteIds(task, prerequisiteIds);
        if (resolvedIds.Count == 0)
        {
            return Array.Empty<TaskDependencyItemViewModel>();
        }

        var lookup = BuildTaskLookup(allTasks);
        return resolvedIds
            .Select(id => BuildDependencyItem(id, lookup, workspaceId, fallbackWorkspaceId: task.WorkspaceId, isPrerequisite: true))
            .ToList();
    }

    /// <summary>
    /// Builds view-model items for a task's downstream dependents, with workspace-aware URLs.
    /// </summary>
    public static IReadOnlyList<TaskDependencyItemViewModel> ResolveDependentItems(
        IReadOnlyList<int>? dependentIds,
        IReadOnlyList<ForgeTask>? allTasks,
        int? workspaceId,
        int? fallbackWorkspaceId = null)
    {
        if (dependentIds is not { Count: > 0 })
        {
            return Array.Empty<TaskDependencyItemViewModel>();
        }

        var lookup = BuildTaskLookup(allTasks);
        return dependentIds
            .Where(id => id > 0)
            .Distinct()
            .Select(id => BuildDependencyItem(id, lookup, workspaceId, fallbackWorkspaceId, isPrerequisite: false))
            .ToList();
    }

    private static TaskDependencyItemViewModel BuildDependencyItem(
        int targetId,
        Dictionary<int, ForgeTask> lookup,
        int? workspaceId,
        int? fallbackWorkspaceId,
        bool isPrerequisite)
    {
        lookup.TryGetValue(targetId, out var relatedTask);

        var isClosed = relatedTask?.Status == TaskOverallStatus.Closed;
        var isBlocking = isPrerequisite && relatedTask != null && !isClosed;
        var requiresAttention = isPrerequisite && relatedTask != null && IsStalledOrFailedBlocker(relatedTask);
        var isWaitingForHuman = relatedTask?.IsWaitingForHuman == true;

        var cssClass = isPrerequisite
            ? isClosed
                ? "satisfied"
                : requiresAttention
                    ? "blocking upstream-attention"
                    : isBlocking
                        ? "blocking"
                        : "neutral"
            : "neutral";

        var chipCssClass = isClosed
            ? "prereq-chip prereq-chip-closed"
            : requiresAttention
                ? "prereq-chip prereq-chip-warning"
                : isBlocking
                    ? "prereq-chip prereq-chip-blocking"
                    : "prereq-chip";

        int? effectiveWorkspaceId = workspaceId is > 0
            ? workspaceId
            : relatedTask?.WorkspaceId is > 0
                ? relatedTask.WorkspaceId
                : fallbackWorkspaceId is > 0
                    ? fallbackWorkspaceId
                    : null;

        return new TaskDependencyItemViewModel(
            TaskId: targetId,
            Title: relatedTask?.Title ?? $"Task #{targetId}",
            StatusLabel: relatedTask?.Status.ToString() ?? "Unknown",
            Status: relatedTask?.Status,
            IsClosed: isClosed,
            IsBlocking: isBlocking,
            RequiresUpstreamAttention: requiresAttention,
            IsWaitingForHuman: isWaitingForHuman,
            CssClass: cssClass,
            ChipCssClass: chipCssClass,
            TaskUrl: WorkspaceUrlHelper.BuildPath(effectiveWorkspaceId, $"tasks/{targetId}"));
    }

    private static Dictionary<int, ForgeTask> BuildTaskLookup(IReadOnlyList<ForgeTask>? allTasks)
    {
        var lookup = new Dictionary<int, ForgeTask>();
        if (allTasks == null)
        {
            return lookup;
        }

        foreach (var candidate in allTasks)
        {
            lookup[candidate.Id] = candidate;
        }

        return lookup;
    }
}
