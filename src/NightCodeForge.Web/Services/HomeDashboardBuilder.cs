using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Web.Services;

public record RecentWorkspaceSummary(
    Workspace Workspace,
    DateTime LastActivityAt,
    int RunningTaskCount,
    int OpenTaskCount,
    int SuggestedTaskCount,
    int WaitingOrReviewTaskCount,
    int PendingQuestionCount,
    int TotalTaskCount,
    string BacklogUrl,
    string IdeationUrl,
    string InboxUrl,
    int PendingReviewCount,
    string ReviewUrl);

public record HomePendingReviewItem(
    int TaskId,
    string Title,
    int WorkspaceId,
    string WorkspaceName,
    string FeatureBranch,
    string BaseBranch,
    int Priority,
    bool HasResolvedConflict,
    DateTime UpdatedAt,
    string ReviewUrl,
    string InspectorUrl,
    string TaskDetailUrl,
    string WorkspaceEmoji = WorkspacePresentationHelper.DefaultEmoji);

public record HomeScheduledTaskItem(
    int TaskId,
    string Title,
    int WorkspaceId,
    string WorkspaceName,
    string StageId,
    string QueueLabel,
    int Priority,
    bool IsRunning,
    string InspectorUrl,
    string TaskDetailUrl,
    string WorkspaceEmoji = WorkspacePresentationHelper.DefaultEmoji,
    bool IsPaused = false,
    string? RunningDurationFormatted = null);

public record HomeRecentTerminalTaskItem(
    int TaskId,
    string Title,
    TaskOverallStatus Status,
    int WorkspaceId,
    string WorkspaceName,
    int Priority,
    DateTime UpdatedAt,
    string? SquashCommitSha,
    string InspectorUrl,
    string TaskDetailUrl,
    string WorkspaceEmoji = WorkspacePresentationHelper.DefaultEmoji);

public record HomeDashboardSnapshot(
    IReadOnlyList<RecentWorkspaceSummary> RecentWorkspaces,
    IReadOnlyList<HomeScheduledTaskItem> RunningAndNextTasks,
    int TotalQueuedCount,
    int PendingQuestionCount,
    IReadOnlyList<HomePendingReviewItem> PendingReviewTasks,
    int PendingReviewCount,
    IReadOnlyList<HomeRecentTerminalTaskItem> RecentFailedAndClosedTasks);

/// <summary>
/// Builds the workspace-agnostic homepage snapshot aggregating recent workspaces,
/// running tasks, next scheduled tasks, and pending human questions across all workspaces.
/// </summary>
public static class HomeDashboardBuilder
{
    public const int MaxScheduledTasks = 5;
    public const int DefaultStateChangeDebounceSeconds = 10;
    public static readonly TimeSpan DefaultStateChangeDebounceInterval = TimeSpan.FromSeconds(DefaultStateChangeDebounceSeconds);
    private const string UnknownWorkspaceName = "Unknown Workspace";
    private const string RunningQueueLabel = "Running";
    private const string DefaultStageLabel = "Pending";
    private const string QueuedStageLabel = "Queued";

    public static async Task<HomeDashboardSnapshot> BuildSnapshotAsync(
        IWorkspaceRepository workspaceRepository,
        ITaskRepository taskRepository,
        IDevQueueService devQueueService,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspaceRepository);
        ArgumentNullException.ThrowIfNull(taskRepository);
        ArgumentNullException.ThrowIfNull(devQueueService);
        ArgumentNullException.ThrowIfNull(logger);

        var workspaces = await workspaceRepository.GetAllAsync(cancellationToken);
        var workspaceById = workspaces.ToDictionary(w => w.Id);
        var allTasksById = new Dictionary<int, ForgeTask>();

        var rawPendingQuestions = await taskRepository.GetQuestionsByStatusAsync(
            QuestionStatus.Pending,
            workspaceId: null,
            cancellationToken: cancellationToken);
        var pendingQuestions = rawPendingQuestions.Where(TaskBadgeCounter.IsPendingQuestion).ToList();

        DevQueueSnapshot? queueSnapshot = null;
        try
        {
            queueSnapshot = await devQueueService.GetSnapshotAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to retrieve DevQueue snapshot for homepage; falling back to repository task states.");
        }

        var runningTaskIds = new HashSet<int>();
        var workspaceSummaries = new List<RecentWorkspaceSummary>(workspaces.Count);
        var tasksByWorkspaceId = new Dictionary<int, IReadOnlyList<ForgeTask>>(workspaces.Count);

        foreach (var ws in workspaces)
        {
            var tasks = await taskRepository.GetByWorkspaceIdAsync(ws.Id, cancellationToken);
            tasksByWorkspaceId[ws.Id] = tasks;
            foreach (var task in tasks)
            {
                allTasksById[task.Id] = task;
            }
        }

        if (queueSnapshot != null)
        {
            foreach (var p in queueSnapshot.ProcessingJobs.Where(j => j.TaskId > 0))
            {
                if (allTasksById.TryGetValue(p.TaskId, out var procTask) &&
                    !IsValidProcessingTaskForJob(procTask, p.JobId))
                {
                    continue;
                }

                runningTaskIds.Add(p.TaskId);
            }
            foreach (var l in queueSnapshot.ActiveLocks.Where(l => l.TaskId.HasValue && l.TaskId.Value > 0))
            {
                var lockTaskId = l.TaskId!.Value;
                if (allTasksById.TryGetValue(lockTaskId, out var lockTask) && !lockTask.IsRunnable())
                {
                    continue;
                }

                runningTaskIds.Add(lockTaskId);
            }
        }

        foreach (var ws in workspaces)
        {
            var tasks = tasksByWorkspaceId[ws.Id];
            var wsTaskIds = tasks.Select(t => t.Id).ToHashSet();

            var lastTaskActivity = tasks.Count > 0
                ? tasks.Max(t => t.UpdatedAt > t.CreatedAt ? t.UpdatedAt : t.CreatedAt)
                : ws.CreatedAt;
            var lastActivity = lastTaskActivity > ws.CreatedAt ? lastTaskActivity : ws.CreatedAt;

            var runningCount = tasks.Count(t => runningTaskIds.Contains(t.Id));
            var openCount = tasks.Count(t =>
                t.Status == TaskOverallStatus.Open ||
                t.Status == TaskOverallStatus.Reviewed ||
                t.Status == TaskOverallStatus.Implemented);
            var suggestedCount = tasks.Count(t => t.Status == TaskOverallStatus.Suggested);
            var wsPendingReviewCount = tasks.Count(IsPendingHumanReview);
            var waitingOrReviewCount = tasks.Count(t =>
                t.IsWaitingForHuman || IsPendingHumanReview(t));
            var wsPendingQuestionCount = pendingQuestions.Count(q =>
                (q.ForgeTask != null && q.ForgeTask.WorkspaceId == ws.Id) ||
                wsTaskIds.Contains(q.TaskId));

            workspaceSummaries.Add(new RecentWorkspaceSummary(
                Workspace: ws,
                LastActivityAt: lastActivity,
                RunningTaskCount: runningCount,
                OpenTaskCount: openCount,
                SuggestedTaskCount: suggestedCount,
                WaitingOrReviewTaskCount: waitingOrReviewCount,
                PendingQuestionCount: wsPendingQuestionCount,
                TotalTaskCount: tasks.Count,
                BacklogUrl: WorkspaceUrlHelper.BuildPath(ws.Id, "backlog"),
                IdeationUrl: WorkspaceUrlHelper.BuildPath(ws.Id, "ideation"),
                InboxUrl: WorkspaceUrlHelper.BuildPath(ws.Id, "inbox"),
                PendingReviewCount: wsPendingReviewCount,
                ReviewUrl: WorkspaceUrlHelper.BuildPath(ws.Id, "review")));
        }

        var orderedWorkspaces = workspaceSummaries
            .OrderByDescending(w => w.RunningTaskCount > 0)
            .ThenByDescending(w => w.LastActivityAt)
            .ThenByDescending(w => w.Workspace.Id)
            .ToList();

        var scheduledItems = BuildRunningAndNextTasks(
            queueSnapshot,
            allTasksById,
            workspaceById,
            out var totalQueuedCount);

        var pendingReviewTasks = BuildPendingReviewTasks(allTasksById.Values, workspaceById);
        var recentTerminalTasks = BuildRecentFailedAndClosedTasks(allTasksById.Values, workspaceById);

        return new HomeDashboardSnapshot(
            RecentWorkspaces: orderedWorkspaces,
            RunningAndNextTasks: scheduledItems,
            TotalQueuedCount: totalQueuedCount,
            PendingQuestionCount: pendingQuestions.Count,
            PendingReviewTasks: pendingReviewTasks,
            PendingReviewCount: pendingReviewTasks.Count,
            RecentFailedAndClosedTasks: recentTerminalTasks);
    }

    public static string FormatRunningDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;
        if (duration.TotalMinutes < 1)
            return $"{(int)duration.TotalSeconds}s";
        if (duration.TotalHours < 1)
            return $"{(int)duration.TotalMinutes}m {duration.Seconds}s";
        return $"{(int)duration.TotalHours}h {duration.Minutes}m";
    }

    public static IReadOnlyList<HomeRecentTerminalTaskItem> BuildRecentFailedAndClosedTasks(
        IEnumerable<ForgeTask> tasks,
        IReadOnlyDictionary<int, Workspace> workspacesById,
        DateTime? utcNow = null)
    {
        var now = utcNow ?? DateTime.UtcNow;
        var cutoff = now - TaskRecencyAndRetentionHelper.RecentTerminalTaskWindow;

        return tasks
            .Where(t => (t.Status is TaskOverallStatus.Failed or TaskOverallStatus.Closed) && t.UpdatedAt >= cutoff)
            .OrderByDescending(t => t.Status == TaskOverallStatus.Failed)
            .ThenByDescending(t => t.UpdatedAt)
            .ThenBy(t => t.Id)
            .Select(t =>
            {
                var ws = workspacesById.TryGetValue(t.WorkspaceId, out var w) ? w : null;
                var wsName = ws?.Name ?? UnknownWorkspaceName;
                var wsEmoji = WorkspacePresentationHelper.ResolveEmoji(ws?.Emoji);
                int? wsId = t.WorkspaceId > 0 ? t.WorkspaceId : null;

                return new HomeRecentTerminalTaskItem(
                    TaskId: t.Id,
                    Title: t.Title,
                    Status: t.Status,
                    WorkspaceId: t.WorkspaceId,
                    WorkspaceName: wsName,
                    Priority: t.Priority,
                    UpdatedAt: t.UpdatedAt,
                    SquashCommitSha: t.SquashCommitSha,
                    InspectorUrl: WorkspaceUrlHelper.BuildPath(wsId, $"inspector/{t.Id}"),
                    TaskDetailUrl: WorkspaceUrlHelper.BuildPath(wsId, $"tasks/{t.Id}"),
                    WorkspaceEmoji: wsEmoji);
            })
            .ToList();
    }

    public static bool IsPendingHumanReview(ForgeTask task) =>
        TaskBadgeCounter.IsPendingHumanReview(task);

    public static IReadOnlyList<HomePendingReviewItem> BuildPendingReviewTasks(
        IEnumerable<ForgeTask> tasks,
        IReadOnlyDictionary<int, Workspace> workspacesById)
    {
        return tasks
            .Where(IsPendingHumanReview)
            .OrderBy(t => t.Priority)
            .ThenByDescending(t => t.UpdatedAt)
            .ThenBy(t => t.Id)
            .Select(t =>
            {
                var ws = workspacesById.TryGetValue(t.WorkspaceId, out var w) ? w : null;
                var wsName = ws?.Name ?? UnknownWorkspaceName;
                var wsEmoji = WorkspacePresentationHelper.ResolveEmoji(ws?.Emoji);
                int? wsId = t.WorkspaceId > 0 ? t.WorkspaceId : null;
                var hasResolvedConflict = PipelineDataBag.FromJson(t.PipelineDataJson)
                    .Get<bool?>(PipelineDataBagKeys.HasResolvedConflict) ?? false;
                return new HomePendingReviewItem(
                    TaskId: t.Id,
                    Title: t.Title,
                    WorkspaceId: t.WorkspaceId,
                    WorkspaceName: wsName,
                    FeatureBranch: t.FeatureBranch,
                    BaseBranch: t.BaseBranch,
                    Priority: t.Priority,
                    HasResolvedConflict: hasResolvedConflict,
                    UpdatedAt: t.UpdatedAt,
                    ReviewUrl: WorkspaceUrlHelper.BuildPath(wsId, $"review/{t.Id}"),
                    InspectorUrl: WorkspaceUrlHelper.BuildPath(wsId, $"inspector/{t.Id}"),
                    TaskDetailUrl: WorkspaceUrlHelper.BuildPath(wsId, $"tasks/{t.Id}"),
                    WorkspaceEmoji: wsEmoji);
            })
            .ToList();
    }

    public static IReadOnlyList<HomeScheduledTaskItem> BuildRunningAndNextTasks(
        DevQueueSnapshot? queueSnapshot,
        IReadOnlyDictionary<int, ForgeTask> tasksById,
        IReadOnlyDictionary<int, Workspace> workspacesById,
        out int totalQueuedCount,
        DateTime? utcNow = null)
    {
        var now = utcNow ?? DateTime.UtcNow;
        var results = new List<HomeScheduledTaskItem>(MaxScheduledTasks);
        var seenTaskIds = new HashSet<int>();

        // 1. Currently processing jobs from DevQueueSnapshot
        if (queueSnapshot != null)
        {
            foreach (var job in queueSnapshot.ProcessingJobs)
            {
                if (job.TaskId <= 0)
                {
                    continue;
                }

                if (tasksById.TryGetValue(job.TaskId, out var procTask) &&
                    !IsValidProcessingTaskForJob(procTask, job.JobId))
                {
                    continue;
                }

                if (!seenTaskIds.Add(job.TaskId))
                {
                    continue;
                }

                results.Add(CreateScheduledItemFromJob(job, isRunning: true, tasksById, workspacesById, queueSnapshot, now));
                if (results.Count >= MaxScheduledTasks)
                {
                    break;
                }
            }

            // 2. Any active workspace lock tasks not already listed in ProcessingJobs
            if (results.Count < MaxScheduledTasks)
            {
                foreach (var activeLock in queueSnapshot.ActiveLocks)
                {
                    if (!activeLock.TaskId.HasValue || activeLock.TaskId.Value <= 0)
                    {
                        continue;
                    }

                    var lockTaskId = activeLock.TaskId.Value;
                    if (!tasksById.TryGetValue(lockTaskId, out var lockedTask) || !lockedTask.IsRunnable())
                    {
                        continue;
                    }

                    if (!seenTaskIds.Add(lockTaskId))
                    {
                        continue;
                    }

                    results.Add(CreateScheduledItemFromTask(lockedTask, isRunning: true, RunningQueueLabel, workspacesById, queueSnapshot, now));
                    if (results.Count >= MaxScheduledTasks)
                    {
                        break;
                    }
                }
            }
        }

        // 3. Next queued jobs in tier priority order: Urgent -> CodeReview -> Implementation -> TaskReview -> Inference
        var allEnqueuedJobs = queueSnapshot != null
            ? queueSnapshot.UrgentJobs
                .Concat(queueSnapshot.CodeReviewJobs)
                .Concat(queueSnapshot.ImplementationJobs)
                .Concat(queueSnapshot.TaskReviewJobs)
                .Concat(queueSnapshot.InferenceJobs)
                .Where(j => j.TaskId > 0)
                .ToList()
            : new List<QueuedJobItem>();

        totalQueuedCount = allEnqueuedJobs.Select(j => j.TaskId).Distinct().Count();

        if (results.Count < MaxScheduledTasks)
        {
            foreach (var job in allEnqueuedJobs)
            {
                if (!seenTaskIds.Add(job.TaskId))
                {
                    continue;
                }

                results.Add(CreateScheduledItemFromJob(job, isRunning: false, tasksById, workspacesById, queueSnapshot, now));
                if (results.Count >= MaxScheduledTasks)
                {
                    break;
                }
            }
        }

        // 4. Fallback to in-progress / open ready tasks from repository if queue snapshot had fewer items
        if (results.Count < MaxScheduledTasks)
        {
            var openTasks = tasksById.Values
                .Where(t => t.IsRunnable() && !seenTaskIds.Contains(t.Id))
                .OrderBy(t => t.Priority)
                .ThenBy(t => t.CreatedAt);

            foreach (var task in openTasks)
            {
                if (!seenTaskIds.Add(task.Id))
                {
                    continue;
                }

                results.Add(CreateScheduledItemFromTask(task, isRunning: false, QueuedStageLabel, workspacesById, queueSnapshot, now));
                if (results.Count >= MaxScheduledTasks)
                {
                    break;
                }
            }
        }

        return results;
    }

    private static HomeScheduledTaskItem CreateScheduledItemFromJob(
        QueuedJobItem job,
        bool isRunning,
        IReadOnlyDictionary<int, ForgeTask> tasksById,
        IReadOnlyDictionary<int, Workspace> workspacesById,
        DevQueueSnapshot? queueSnapshot = null,
        DateTime? utcNow = null)
    {
        var now = utcNow ?? DateTime.UtcNow;
        if (tasksById.TryGetValue(job.TaskId, out var task))
        {
            var ws = workspacesById.TryGetValue(task.WorkspaceId, out var w) ? w : null;
            var wsName = ws?.Name ?? UnknownWorkspaceName;
            var wsEmoji = WorkspacePresentationHelper.ResolveEmoji(ws?.Emoji);
            var stageId = !string.IsNullOrWhiteSpace(task.CurrentStageId)
                ? task.CurrentStageId
                : (!string.IsNullOrWhiteSpace(job.StageId) ? job.StageId : DefaultStageLabel);
            var queueLabel = isRunning ? RunningQueueLabel : job.QueueName;
            var isPaused = !isRunning && (task.IsDeferredByPause || task.IsWaitingForHuman);

            string? runningDurationFormatted = null;
            if (isRunning)
            {
                var activeLock = queueSnapshot?.ActiveLocks.FirstOrDefault(l => l.TaskId == task.Id);
                var duration = activeLock != null
                    ? activeLock.DurationHeld
                    : (now >= job.EnqueuedAt ? now - job.EnqueuedAt : TimeSpan.Zero);
                runningDurationFormatted = FormatRunningDuration(duration);
            }

            return new HomeScheduledTaskItem(
                TaskId: task.Id,
                Title: task.Title,
                WorkspaceId: task.WorkspaceId,
                WorkspaceName: wsName,
                StageId: stageId,
                QueueLabel: queueLabel,
                Priority: task.Priority,
                IsRunning: isRunning,
                InspectorUrl: WorkspaceUrlHelper.BuildPath(task.WorkspaceId > 0 ? task.WorkspaceId : null, $"inspector/{task.Id}"),
                TaskDetailUrl: WorkspaceUrlHelper.BuildPath(task.WorkspaceId > 0 ? task.WorkspaceId : null, $"tasks/{task.Id}"),
                WorkspaceEmoji: wsEmoji,
                IsPaused: isPaused,
                RunningDurationFormatted: runningDurationFormatted);
        }

        string? unmappedDurationFormatted = null;
        if (isRunning)
        {
            var activeLock = queueSnapshot?.ActiveLocks.FirstOrDefault(l => l.TaskId == job.TaskId);
            var duration = activeLock != null
                ? activeLock.DurationHeld
                : (now >= job.EnqueuedAt ? now - job.EnqueuedAt : TimeSpan.Zero);
            unmappedDurationFormatted = FormatRunningDuration(duration);
        }

        return new HomeScheduledTaskItem(
            TaskId: job.TaskId,
            Title: job.TaskTitle,
            WorkspaceId: 0,
            WorkspaceName: UnknownWorkspaceName,
            StageId: !string.IsNullOrWhiteSpace(job.StageId) ? job.StageId : DefaultStageLabel,
            QueueLabel: isRunning ? RunningQueueLabel : job.QueueName,
            Priority: job.Priority,
            IsRunning: isRunning,
            InspectorUrl: WorkspaceUrlHelper.BuildPath(null, $"inspector/{job.TaskId}"),
            TaskDetailUrl: WorkspaceUrlHelper.BuildPath(null, $"tasks/{job.TaskId}"),
            WorkspaceEmoji: WorkspacePresentationHelper.DefaultEmoji,
            IsPaused: false,
            RunningDurationFormatted: unmappedDurationFormatted);
    }

    private static HomeScheduledTaskItem CreateScheduledItemFromTask(
        ForgeTask task,
        bool isRunning,
        string queueLabel,
        IReadOnlyDictionary<int, Workspace> workspacesById,
        DevQueueSnapshot? queueSnapshot = null,
        DateTime? utcNow = null)
    {
        var now = utcNow ?? DateTime.UtcNow;
        var ws = workspacesById.TryGetValue(task.WorkspaceId, out var w) ? w : null;
        var wsName = ws?.Name ?? UnknownWorkspaceName;
        var wsEmoji = WorkspacePresentationHelper.ResolveEmoji(ws?.Emoji);
        var stageId = !string.IsNullOrWhiteSpace(task.CurrentStageId)
            ? task.CurrentStageId
            : DefaultStageLabel;
        var isPaused = !isRunning && (task.IsDeferredByPause || task.IsWaitingForHuman);

        string? runningDurationFormatted = null;
        if (isRunning)
        {
            var activeLock = queueSnapshot?.ActiveLocks.FirstOrDefault(l => l.TaskId == task.Id);
            var duration = activeLock != null
                ? activeLock.DurationHeld
                : (now >= (task.UpdatedAt > task.CreatedAt ? task.UpdatedAt : task.CreatedAt)
                    ? now - (task.UpdatedAt > task.CreatedAt ? task.UpdatedAt : task.CreatedAt)
                    : TimeSpan.Zero);
            runningDurationFormatted = FormatRunningDuration(duration);
        }

        return new HomeScheduledTaskItem(
            TaskId: task.Id,
            Title: task.Title,
            WorkspaceId: task.WorkspaceId,
            WorkspaceName: wsName,
            StageId: stageId,
            QueueLabel: queueLabel,
            Priority: task.Priority,
            IsRunning: isRunning,
            InspectorUrl: WorkspaceUrlHelper.BuildPath(task.WorkspaceId > 0 ? task.WorkspaceId : null, $"inspector/{task.Id}"),
            TaskDetailUrl: WorkspaceUrlHelper.BuildPath(task.WorkspaceId > 0 ? task.WorkspaceId : null, $"tasks/{task.Id}"),
            WorkspaceEmoji: wsEmoji,
            IsPaused: isPaused,
            RunningDurationFormatted: runningDurationFormatted);
    }

    private static bool IsValidProcessingTaskForJob(ForgeTask task, string? jobId)
    {
        if (!task.IsRunnable())
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(task.LastHangfireJobId) &&
            !string.IsNullOrWhiteSpace(jobId) &&
            !string.Equals(task.LastHangfireJobId, jobId, StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }
}

/// <summary>
/// Debounces rapid workspace and worker state change notifications on the homepage,
/// coalescing bursts so automatic dashboard refreshes run at most once per configured interval.
/// </summary>
public sealed class HomeStateChangeDebouncer : IDisposable
{
    private readonly TimeSpan _interval;
    private readonly Func<CancellationToken, Task> _onRefreshAsync;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly CancellationToken _disposalToken;
    private CancellationTokenSource? _pendingCts;
    private bool _disposed;

    public HomeStateChangeDebouncer(
        TimeSpan interval,
        Func<CancellationToken, Task> onRefreshAsync,
        ILogger logger)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(interval, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(onRefreshAsync);
        ArgumentNullException.ThrowIfNull(logger);

        _interval = interval;
        _onRefreshAsync = onRefreshAsync;
        _logger = logger;
        _disposalToken = _disposeCts.Token;
    }

    public CancellationToken DisposalToken => _disposalToken;

    public void NotifyStateChanged()
    {
        CancellationTokenSource scheduledCts;
        lock (_lock)
        {
            if (_disposed || _pendingCts != null)
            {
                return;
            }

            scheduledCts = new CancellationTokenSource();
            _pendingCts = scheduledCts;
        }

        _ = RunDebouncedRefreshAsync(scheduledCts);
    }

    public void CancelPending()
    {
        lock (_lock)
        {
            if (_pendingCts != null)
            {
                _pendingCts.Cancel();
                _pendingCts = null;
            }
        }
    }

    private async Task RunDebouncedRefreshAsync(CancellationTokenSource scheduledCts)
    {
        try
        {
            await Task.Delay(_interval, scheduledCts.Token);

            lock (_lock)
            {
                if (_disposed || !ReferenceEquals(_pendingCts, scheduledCts))
                {
                    return;
                }

                _pendingCts = null;
            }

            await _onRefreshAsync(_disposalToken);
        }
        catch (OperationCanceledException) when (scheduledCts.IsCancellationRequested || _disposalToken.IsCancellationRequested)
        {
            _logger.LogDebug("Debounced homepage dashboard refresh was canceled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute debounced homepage dashboard refresh.");
        }
        finally
        {
            scheduledCts.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_pendingCts != null)
            {
                _pendingCts.Cancel();
                _pendingCts = null;
            }
        }

        _disposeCts.Cancel();
        _disposeCts.Dispose();
    }
}
