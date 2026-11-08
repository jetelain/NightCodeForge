using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.JSInterop;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Web.Services;

/// <summary>
/// Scoped service that listens for background agent and task transitions, catches up on reconnect/wakeup,
/// suppresses active route notifications in favor of in-app toasts, and dispatches web push notifications.
/// </summary>
public class WebNotificationService : IWebNotificationService, IAsyncDisposable
{
    private readonly OrchestratorHubClientConnection _hubConnection;
    private readonly ITaskRepository _taskRepository;
    private readonly ToastService _toastService;
    private readonly NavigationManager _navigationManager;
    private readonly IJSRuntime _jsRuntime;
    private readonly ILogger<WebNotificationService> _logger;

    private readonly RingBufferDeduplicator _deduplicator = new(NotificationConstants.DefaultDeduplicatorCapacity);
    private readonly Dictionary<int, TaskSnapshot> _baselineSnapshot = new();
    private readonly object _snapshotLock = new();

    private NotificationPreferences _cachedPreferences = new();
    private bool _isPreferencesLoaded = false;
    private bool _isInitialized = false;
    private bool _disposed = false;

    private static readonly TaskOverallStatus[] ActiveRunnableStatuses =
    [
        TaskOverallStatus.Open,
        TaskOverallStatus.Reviewed,
        TaskOverallStatus.Implemented
    ];

    private record TaskSnapshot(
        int TaskId,
        TaskOverallStatus Status,
        bool IsWaitingForHuman,
        MergeMode MergeMode,
        DateTime UpdatedAt);

    public WebNotificationService(
        OrchestratorHubClientConnection hubConnection,
        ITaskRepository taskRepository,
        ToastService toastService,
        NavigationManager navigationManager,
        IJSRuntime jsRuntime,
        ILogger<WebNotificationService> logger)
    {
        _hubConnection = hubConnection ?? throw new ArgumentNullException(nameof(hubConnection));
        _taskRepository = taskRepository ?? throw new ArgumentNullException(nameof(taskRepository));
        _toastService = toastService ?? throw new ArgumentNullException(nameof(toastService));
        _navigationManager = navigationManager ?? throw new ArgumentNullException(nameof(navigationManager));
        _jsRuntime = jsRuntime ?? throw new ArgumentNullException(nameof(jsRuntime));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _hubConnection.OnStageTransitionReceived += HandleStageTransitionReceived;
        _hubConnection.OnWorkerStatusReceived += HandleWorkerStatusReceived;
        _hubConnection.OnConnectionStateChanged += HandleConnectionStateChanged;
    }

    /// <summary>
    /// Populates the initial baseline snapshot of existing task states, starts the SignalR hub connection,
    /// and primes the deduplicator so that existing tasks do not produce notifications upon page reload or initial circuit attachment.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_isInitialized || _disposed) return;
        _isInitialized = true;

        try
        {
            await SyncPreferencesFromStorageAsync();

            var readyTasks = await _taskRepository.GetByStatusAsync(TaskOverallStatus.Ready, cancellationToken);
            if (readyTasks != null)
            {
                foreach (var t in readyTasks)
                {
                    UpdateSnapshot(t);
                    if (t.MergeMode == MergeMode.ManualReviewRequired)
                    {
                        _deduplicator.TryAdd(BuildSignature(NotificationSubject.ReviewRoom, t));
                    }
                }
            }

            var closedTasks = await _taskRepository.GetByStatusAsync(TaskOverallStatus.Closed, cancellationToken);
            if (closedTasks != null)
            {
                foreach (var t in closedTasks)
                {
                    UpdateSnapshot(t);
                    _deduplicator.TryAdd(BuildSignature(NotificationSubject.TaskMerged, t));
                }
            }

            foreach (var status in ActiveRunnableStatuses)
            {
                var activeTasks = await _taskRepository.GetByStatusAsync(status, cancellationToken);
                if (activeTasks != null)
                {
                    foreach (var t in activeTasks)
                    {
                        UpdateSnapshot(t);
                        if (t.IsWaitingForHuman)
                        {
                            _deduplicator.TryAdd(BuildSignature(NotificationSubject.QuestionInbox, t));
                        }
                    }
                }
            }

            var pendingQuestions = await _taskRepository.GetQuestionsByStatusAsync(QuestionStatus.Pending, null, cancellationToken);
            if (pendingQuestions != null)
            {
                foreach (var q in pendingQuestions)
                {
                    var t = q.ForgeTask ?? (q.TaskId > 0 ? await _taskRepository.GetByIdAsync(q.TaskId, cancellationToken) : null);
                    if (t != null && TaskBadgeCounter.IsPendingQuestion(q, t))
                    {
                        UpdateSnapshot(t, isWaitingForHuman: true);
                        _deduplicator.TryAdd(BuildSignature(NotificationSubject.QuestionInbox, t));
                    }
                }
            }

            try
            {
                await _hubConnection.StartAsync();
            }
            catch (Exception hubEx)
            {
                _logger.LogDebug(hubEx, "Could not start SignalR hub connection during WebNotificationService initialization; catch-up will reconcile on foreground/reconnect.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed during WebNotificationService baseline snapshot initialization.");
        }
    }

    public async Task<string> GetPermissionStateAsync()
    {
        try
        {
            return await _jsRuntime.InvokeAsync<string>(NotificationConstants.JsFunctions.GetPermissionState);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read notification permission state from JS.");
            return NotificationConstants.PermissionStates.Unsupported;
        }
    }

    public async Task<string> RequestPermissionAsync()
    {
        try
        {
            return await _jsRuntime.InvokeAsync<string>(NotificationConstants.JsFunctions.RequestPermission);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error requesting notification permission via JS runtime.");
            return NotificationConstants.PermissionStates.Denied;
        }
    }

    public async Task<bool> IsSupportedAsync()
    {
        try
        {
            return await _jsRuntime.InvokeAsync<bool>(NotificationConstants.JsFunctions.IsSupported);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to check if notifications are supported.");
            return false;
        }
    }

    public async Task<NotificationPreferences> GetPreferencesAsync()
    {
        if (!_isPreferencesLoaded)
        {
            await SyncPreferencesFromStorageAsync();
        }
        return _cachedPreferences;
    }

    public async Task SavePreferencesAsync(NotificationPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        _cachedPreferences = preferences;
        _isPreferencesLoaded = true;

        try
        {
            await _jsRuntime.InvokeVoidAsync(NotificationConstants.JsFunctions.SavePreferences, preferences);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist notification preferences via JS localStorage.");
        }
    }

    public async Task<bool> SendTestNotificationAsync()
    {
        try
        {
            var options = new
            {
                body = NotificationConstants.Bodies.Test,
                tag = NotificationConstants.Tags.Test,
                data = new { url = NotificationConstants.Urls.Settings },
                icon = NotificationConstants.DefaultIconPath,
                badge = NotificationConstants.DefaultIconPath
            };

            return await _jsRuntime.InvokeAsync<bool>(
                NotificationConstants.JsFunctions.ShowNotification,
                NotificationConstants.Titles.Test,
                options);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to dispatch test notification.");
            return false;
        }
    }

    public async Task<bool> DispatchNotificationAsync(NotificationSubject subject, ForgeTask task)
    {
        ArgumentNullException.ThrowIfNull(task);

        var prefs = await GetPreferencesAsync();
        if (!prefs.ShouldNotify(subject))
        {
            return false;
        }

        var signature = BuildSignature(subject, task);
        if (_deduplicator.Contains(signature))
        {
            return false;
        }

        string title;
        string body;
        string tag;
        string clickUrl;
        string targetPath;

        switch (subject)
        {
            case NotificationSubject.QuestionInbox:
                title = NotificationConstants.Titles.Question(task.Id);
                body = NotificationConstants.Bodies.Question(task.Title);
                tag = NotificationConstants.Tags.Question(task.Id);
                clickUrl = NotificationConstants.Urls.Question(task.Id);
                targetPath = NotificationConstants.Targets.Inbox;
                break;

            case NotificationSubject.ReviewRoom:
                title = NotificationConstants.Titles.Review(task.Id);
                body = NotificationConstants.Bodies.Review(task.Title);
                tag = NotificationConstants.Tags.Review(task.Id);
                clickUrl = NotificationConstants.Urls.Review(task.Id);
                targetPath = NotificationConstants.Targets.Review(task.Id);
                break;

            case NotificationSubject.TaskMerged:
                var baseBranch = string.IsNullOrWhiteSpace(task.BaseBranch) ? NotificationConstants.DefaultBaseBranch : task.BaseBranch;
                title = NotificationConstants.Titles.Merged(task.Id);
                body = NotificationConstants.Bodies.Merged(task.Title, baseBranch);
                tag = NotificationConstants.Tags.Merged(task.Id);
                clickUrl = NotificationConstants.Urls.Merged(task.Id);
                targetPath = NotificationConstants.Targets.Tasks(task.Id);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(subject), subject, "Unsupported notification subject.");
        }

        // Active Route Suppression: If the user is currently viewing the target route and the document is visible,
        // suppress OS push notification in favor of an in-app toast.
        var isSuppressed = await IsRouteActiveAndVisibleAsync(targetPath, task.Id);
        if (isSuppressed)
        {
            if (!_deduplicator.TryAdd(signature))
            {
                return false;
            }
            _toastService.Show(title, body, ToastLevel.Info);
            return true;
        }

        try
        {
            var options = new
            {
                body,
                tag,
                data = new { url = clickUrl },
                icon = NotificationConstants.DefaultIconPath,
                badge = NotificationConstants.DefaultIconPath
            };

            var dispatched = await _jsRuntime.InvokeAsync<bool>(
                NotificationConstants.JsFunctions.ShowNotification,
                title,
                options);
            if (dispatched)
            {
                _deduplicator.TryAdd(signature);
            }
            return dispatched;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to invoke showNotification in browser for task {TaskId}.", task.Id);
            return false;
        }
    }

    /// <summary>
    /// Catch-up routine executed whenever the mobile tab returns to the foreground or reconnects.
    /// Compares the current database state against baseline snapshots to detect transitions that
    /// occurred while the device was asleep or disconnected.
    /// </summary>
    public async Task RunCatchUpAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) return;

        try
        {
            var questionWaitingTaskIds = new HashSet<int>();

            // 1. Pending clarification questions
            var pendingQuestions = await _taskRepository.GetQuestionsByStatusAsync(QuestionStatus.Pending, null, cancellationToken);
            if (pendingQuestions != null)
            {
                foreach (var q in pendingQuestions)
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    var task = q.ForgeTask ?? (q.TaskId > 0 ? await _taskRepository.GetByIdAsync(q.TaskId, cancellationToken) : null);
                    if (task != null && TaskBadgeCounter.IsPendingQuestion(q, task))
                    {
                        questionWaitingTaskIds.Add(task.Id);
                        bool isNewTransition;
                        lock (_snapshotLock)
                        {
                            isNewTransition = !_baselineSnapshot.TryGetValue(task.Id, out var prev) || !prev.IsWaitingForHuman;
                        }

                        if (isNewTransition)
                        {
                            var notified = await DispatchNotificationAsync(NotificationSubject.QuestionInbox, task);
                            if (notified || !await ShouldNotifySubjectAsync(NotificationSubject.QuestionInbox))
                            {
                                UpdateSnapshot(task, isWaitingForHuman: true);
                            }
                        }
                        else
                        {
                            UpdateSnapshot(task, isWaitingForHuman: true);
                        }
                    }
                }
            }

            foreach (var status in ActiveRunnableStatuses)
            {
                if (cancellationToken.IsCancellationRequested) break;
                var activeTasks = await _taskRepository.GetByStatusAsync(status, cancellationToken);
                if (activeTasks != null)
                {
                    foreach (var task in activeTasks)
                    {
                        if (cancellationToken.IsCancellationRequested) break;
                        var isWaiting = task.IsWaitingForHuman || questionWaitingTaskIds.Contains(task.Id);
                        if (isWaiting)
                        {
                            bool isNewTransition;
                            lock (_snapshotLock)
                            {
                                isNewTransition = !_baselineSnapshot.TryGetValue(task.Id, out var prev) || !prev.IsWaitingForHuman;
                            }

                            if (isNewTransition)
                            {
                                var notified = await DispatchNotificationAsync(NotificationSubject.QuestionInbox, task);
                                if (notified || !await ShouldNotifySubjectAsync(NotificationSubject.QuestionInbox))
                                {
                                    UpdateSnapshot(task, isWaitingForHuman: true);
                                }
                            }
                            else
                            {
                                UpdateSnapshot(task, isWaitingForHuman: true);
                            }
                        }
                        else
                        {
                            UpdateSnapshot(task, isWaitingForHuman: false);
                        }
                    }
                }
            }

            // 2. Tasks awaiting review sign-off
            var readyTasks = await _taskRepository.GetByStatusAsync(TaskOverallStatus.Ready, cancellationToken);
            if (readyTasks != null)
            {
                foreach (var task in readyTasks)
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    if (task.MergeMode == MergeMode.ManualReviewRequired)
                    {
                        bool isNewTransition;
                        lock (_snapshotLock)
                        {
                            isNewTransition = !_baselineSnapshot.TryGetValue(task.Id, out var prev) || prev.Status != TaskOverallStatus.Ready;
                        }

                        if (isNewTransition)
                        {
                            var notified = await DispatchNotificationAsync(NotificationSubject.ReviewRoom, task);
                            if (notified || !await ShouldNotifySubjectAsync(NotificationSubject.ReviewRoom))
                            {
                                UpdateSnapshot(task, isWaitingForHuman: questionWaitingTaskIds.Contains(task.Id));
                            }
                        }
                        else
                        {
                            UpdateSnapshot(task, isWaitingForHuman: questionWaitingTaskIds.Contains(task.Id));
                        }
                    }
                    else
                    {
                        UpdateSnapshot(task, isWaitingForHuman: questionWaitingTaskIds.Contains(task.Id));
                    }
                }
            }

            // 3. Merged tasks
            var closedTasks = await _taskRepository.GetByStatusAsync(TaskOverallStatus.Closed, cancellationToken);
            if (closedTasks != null)
            {
                foreach (var task in closedTasks)
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    bool isNewTransition;
                    lock (_snapshotLock)
                    {
                        isNewTransition = !_baselineSnapshot.TryGetValue(task.Id, out var prev) || prev.Status != TaskOverallStatus.Closed;
                    }

                    if (isNewTransition)
                    {
                        var notified = await DispatchNotificationAsync(NotificationSubject.TaskMerged, task);
                        if (notified || !await ShouldNotifySubjectAsync(NotificationSubject.TaskMerged))
                        {
                            UpdateSnapshot(task, isWaitingForHuman: false);
                        }
                    }
                    else
                    {
                        UpdateSnapshot(task, isWaitingForHuman: false);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to complete wakeup catch-up in WebNotificationService.");
        }
    }

    public async Task HandleTabForegroundedAsync(CancellationToken cancellationToken = default)
    {
        await RunCatchUpAsync(cancellationToken);
    }

    private void HandleConnectionStateChanged(HubConnectionState state)
    {
        if (_disposed) return;

        if (state == HubConnectionState.Connected)
        {
            _ = RunCatchUpSafeAsync();
        }
    }

    private async Task RunCatchUpSafeAsync()
    {
        try
        {
            await RunCatchUpAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to run catch-up safely on connection state change.");
        }
    }

    private void HandleStageTransitionReceived(int taskId, string stageId, string stageKind, string status)
    {
        if (_disposed || taskId <= 0) return;
        _ = HandleStageTransitionAsync(taskId, stageId, stageKind, status);
    }

    private async Task HandleStageTransitionAsync(int taskId, string stageId, string stageKind, string status)
    {
        try
        {
            var task = await _taskRepository.GetByIdAsync(taskId);
            if (task == null) return;

            if (task.IsWaitingForHuman || WorkerStatusConstants.IsWaitingForHuman(status) ||
                string.Equals(status, nameof(StageExecutionStatus.WaitingForHuman), StringComparison.OrdinalIgnoreCase))
            {
                var notified = await DispatchNotificationAsync(NotificationSubject.QuestionInbox, task);
                if (notified || !await ShouldNotifySubjectAsync(NotificationSubject.QuestionInbox))
                {
                    UpdateSnapshot(task, isWaitingForHuman: true);
                }
                return;
            }

            if (task.Status == TaskOverallStatus.Ready && task.MergeMode == MergeMode.ManualReviewRequired)
            {
                var notified = await DispatchNotificationAsync(NotificationSubject.ReviewRoom, task);
                if (notified || !await ShouldNotifySubjectAsync(NotificationSubject.ReviewRoom))
                {
                    UpdateSnapshot(task, isWaitingForHuman: false);
                }
                return;
            }

            if (task.Status == TaskOverallStatus.Closed)
            {
                var notified = await DispatchNotificationAsync(NotificationSubject.TaskMerged, task);
                if (notified || !await ShouldNotifySubjectAsync(NotificationSubject.TaskMerged))
                {
                    UpdateSnapshot(task, isWaitingForHuman: false);
                }
                return;
            }

            UpdateSnapshot(task, isWaitingForHuman: false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing stage transition in WebNotificationService for task {TaskId}.", taskId);
        }
    }

    private void HandleWorkerStatusReceived(string status, int? activeTaskId, string? stageId, string hardwareTarget)
    {
        if (_disposed || !activeTaskId.HasValue || activeTaskId.Value <= 0) return;
        _ = HandleWorkerStatusAsync(status, activeTaskId.Value, stageId, hardwareTarget);
    }

    private async Task HandleWorkerStatusAsync(string status, int activeTaskId, string? stageId, string hardwareTarget)
    {
        try
        {
            if (WorkerStatusConstants.IsWaitingForHuman(status) ||
                string.Equals(status, nameof(StageExecutionStatus.WaitingForHuman), StringComparison.OrdinalIgnoreCase))
            {
                var task = await _taskRepository.GetByIdAsync(activeTaskId);
                if (task != null)
                {
                    var notified = await DispatchNotificationAsync(NotificationSubject.QuestionInbox, task);
                    if (notified || !await ShouldNotifySubjectAsync(NotificationSubject.QuestionInbox))
                    {
                        UpdateSnapshot(task, isWaitingForHuman: true);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing worker status in WebNotificationService for task {TaskId}.", activeTaskId);
        }
    }

    private async Task<bool> ShouldNotifySubjectAsync(NotificationSubject subject)
    {
        var prefs = await GetPreferencesAsync();
        return prefs.ShouldNotify(subject);
    }

    private void UpdateSnapshot(ForgeTask task, bool? isWaitingForHuman = null)
    {
        lock (_snapshotLock)
        {
            _baselineSnapshot[task.Id] = new TaskSnapshot(
                task.Id,
                task.Status,
                isWaitingForHuman ?? task.IsWaitingForHuman,
                task.MergeMode,
                task.UpdatedAt);
        }
    }

    public static string BuildSignature(NotificationSubject subject, ForgeTask task)
    {
        var subjectKey = subject switch
        {
            NotificationSubject.QuestionInbox => NotificationConstants.Subjects.QuestionInbox,
            NotificationSubject.ReviewRoom => NotificationConstants.Subjects.ReviewRoom,
            NotificationSubject.TaskMerged => NotificationConstants.Subjects.TaskMerged,
            _ => subject.ToString().ToLowerInvariant()
        };

        return $"{subjectKey}:{task.Id}:{task.Status}:{task.UpdatedAt.Ticks}";
    }

    private async Task<bool> IsRouteActiveAndVisibleAsync(string targetPath, int taskId)
    {
        bool isVisible;
        try
        {
            isVisible = await _jsRuntime.InvokeAsync<bool>(NotificationConstants.JsFunctions.IsDocumentVisible);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not check document visibility via JS runtime.");
            return false;
        }

        if (!isVisible)
        {
            return false;
        }

        var currentUri = _navigationManager.Uri;
        WorkspaceUrlHelper.TryExtractWorkspaceId(currentUri, out _, out var remainingPath, out _);

        var currentSegment = remainingPath.Trim('/');
        var normalizedTarget = targetPath.Trim('/');

        if (string.Equals(currentSegment, normalizedTarget, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(normalizedTarget, NotificationConstants.Targets.Inbox, StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(currentSegment, NotificationConstants.Targets.Inbox, StringComparison.OrdinalIgnoreCase) ||
                currentSegment.StartsWith(NotificationConstants.Targets.Inbox + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(currentSegment, NotificationConstants.Targets.Inspector(taskId), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(currentSegment, NotificationConstants.Targets.Monitoring(taskId), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (normalizedTarget.StartsWith(NotificationConstants.Targets.ReviewSection + "/", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(currentSegment, NotificationConstants.Targets.ReviewSection, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private async Task SyncPreferencesFromStorageAsync()
    {
        try
        {
            var stored = await _jsRuntime.InvokeAsync<NotificationPreferences>(NotificationConstants.JsFunctions.GetPreferences);
            if (stored != null)
            {
                _cachedPreferences = stored;
                _isPreferencesLoaded = true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not load notification preferences from localStorage; using defaults.");
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;

        _hubConnection.OnStageTransitionReceived -= HandleStageTransitionReceived;
        _hubConnection.OnWorkerStatusReceived -= HandleWorkerStatusReceived;
        _hubConnection.OnConnectionStateChanged -= HandleConnectionStateChanged;

        return ValueTask.CompletedTask;
    }
}
