using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Hangfire.Storage.Monitoring;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Infrastructure.BackgroundJobs;

namespace NightCodeForge.Web.Services;

/// <summary>
/// Service implementing dev-queue observability, task reprioritization,
/// job cancellation, and concurrency lock inspection.
/// </summary>
public class DevQueueService : IDevQueueService
{
    private readonly IWorkspaceLockProvider _lockProvider;
    private readonly IBackgroundJobClient _backgroundJobClient;
    private readonly ITaskJobQueue _taskJobQueue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly JobStorage _jobStorage;
    private readonly ITaskCancellationManager _cancellationManager;
    private readonly IWorkspacePathResolver _pathResolver;
    private readonly ILogger<DevQueueService> _logger;

    public DevQueueService(
        IWorkspaceLockProvider lockProvider,
        IBackgroundJobClient backgroundJobClient,
        ITaskJobQueue taskJobQueue,
        IServiceScopeFactory scopeFactory,
        JobStorage jobStorage,
        ITaskCancellationManager cancellationManager,
        IWorkspacePathResolver pathResolver,
        ILogger<DevQueueService> logger)
    {
        _lockProvider = lockProvider ?? throw new ArgumentNullException(nameof(lockProvider));
        _backgroundJobClient = backgroundJobClient ?? throw new ArgumentNullException(nameof(backgroundJobClient));
        _taskJobQueue = taskJobQueue ?? throw new ArgumentNullException(nameof(taskJobQueue));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _jobStorage = jobStorage ?? throw new ArgumentNullException(nameof(jobStorage));
        _cancellationManager = cancellationManager ?? throw new ArgumentNullException(nameof(cancellationManager));
        _pathResolver = pathResolver ?? throw new ArgumentNullException(nameof(pathResolver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<DevQueueSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var activeLocks = _lockProvider.GetActiveLocks();

        var urgentJobs = new List<QueuedJobItem>();
        var inferenceJobs = new List<QueuedJobItem>();
        var utilityJobs = new List<QueuedJobItem>();
        var processingJobs = new List<QueuedJobItem>();
        var failedJobs = new List<QueuedJobItem>();

        try
        {
            var monitoring = _jobStorage.GetMonitoringApi();

                var rawUrgent = monitoring.EnqueuedJobs(HangfireQueues.LlmUrgent, 0, 100);
                var rawInference = monitoring.EnqueuedJobs(HangfireQueues.LlmInference, 0, 100);
                var rawDefault = monitoring.EnqueuedJobs(HangfireQueues.Default, 0, 100);
                var rawProcessing = monitoring.ProcessingJobs(0, 50);
                var rawFailed = monitoring.FailedJobs(0, 50);

                var taskIds = new HashSet<int>();
                ExtractTaskIds(rawUrgent, taskIds);
                ExtractTaskIds(rawInference, taskIds);
                ExtractTaskIds(rawDefault, taskIds);
                ExtractTaskIdsFromProcessing(rawProcessing, taskIds);
                ExtractTaskIdsFromFailed(rawFailed, taskIds);

                var taskLookup = await LoadTasksAsync(taskIds, cancellationToken);

                urgentJobs.AddRange(MapEnqueued(rawUrgent, HangfireQueues.LlmUrgent, "Urgent", taskLookup));
                inferenceJobs.AddRange(MapEnqueued(rawInference, HangfireQueues.LlmInference, "Queued", taskLookup));
                utilityJobs.AddRange(MapEnqueued(rawDefault, HangfireQueues.Default, "Queued", taskLookup));
                processingJobs.AddRange(MapProcessing(rawProcessing, taskLookup));
                failedJobs.AddRange(MapFailed(rawFailed, taskLookup));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query Hangfire monitoring API");
        }

        return new DevQueueSnapshot(
            urgentJobs,
            inferenceJobs,
            utilityJobs,
            processingJobs,
            failedJobs,
            activeLocks);
    }

    public async Task<bool> PromoteToUrgentAsync(string jobId, int taskId, CancellationToken cancellationToken = default)
    {
        try
        {
            if (taskId > 0)
            {
                await EnsureTaskPriorityEscalatedAsync(taskId, cancellationToken);
            }

            if (!string.IsNullOrWhiteSpace(jobId))
            {
                _backgroundJobClient.ChangeState(jobId, new EnqueuedState(HangfireQueues.LlmUrgent));
                _logger.LogInformation("Promoted Hangfire job {JobId} for Task #{TaskId} to llm-urgent", jobId, taskId);
                return true;
            }

            if (taskId > 0)
            {
                _taskJobQueue.EnqueueUrgent(taskId);
                _logger.LogInformation("Enqueued Task #{TaskId} directly to llm-urgent", taskId);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to promote job {JobId} for Task #{TaskId} to llm-urgent", jobId, taskId);
            if (taskId > 0)
            {
                try
                {
                    _taskJobQueue.EnqueueUrgent(taskId);
                    return true;
                }
                catch (Exception fallbackEx)
                {
                    _logger.LogError(fallbackEx, "Fallback urgent enqueue failed for Task #{TaskId}", taskId);
                }
            }
            return false;
        }
    }

    public async Task<bool> PromoteToUrgentAsync(int taskId, CancellationToken cancellationToken = default)
    {
        if (taskId <= 0) return false;

        try
        {
            var snapshot = await GetSnapshotAsync(cancellationToken);
            var queuedJob = snapshot.InferenceJobs.FirstOrDefault(j => j.TaskId == taskId)
                         ?? snapshot.UtilityJobs.FirstOrDefault(j => j.TaskId == taskId);

            if (queuedJob != null && !string.IsNullOrWhiteSpace(queuedJob.JobId))
            {
                return await PromoteToUrgentAsync(queuedJob.JobId, taskId, cancellationToken);
            }

            if (snapshot.UrgentJobs.Any(j => j.TaskId == taskId))
            {
                await EnsureTaskPriorityEscalatedAsync(taskId, cancellationToken);
                return true;
            }

            // Task is not currently queued in Hangfire (e.g. Suggested, DAG-blocked, or Closed).
            // Escalate DB priority, but do not enqueue to workers to avoid bypassing triage/DAG rules.
            await EnsureTaskPriorityEscalatedAsync(taskId, cancellationToken);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query snapshot when resolving job for Task #{TaskId}", taskId);
            return false;
        }
    }

    private async Task EnsureTaskPriorityEscalatedAsync(int taskId, CancellationToken cancellationToken)
    {
        if (taskId <= 0) return;

        using var scope = _scopeFactory.CreateScope();
        var taskRepo = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
        var task = await taskRepo.GetByIdAsync(taskId, cancellationToken);
        if (task != null && task.Priority != TaskPriority.Critical)
        {
            var oldPriority = task.Priority;
            task.Priority = TaskPriority.Critical;
            task.UpdatedAt = DateTime.UtcNow;

            await taskRepo.UpdateWithHistoryAsync(
                task,
                dependsOn: null,
                changedBy: "User",
                reason: $"Updated Priority from P{oldPriority} to P{TaskPriority.Critical} (llm-urgent promotion)",
                cancellationToken: cancellationToken);

            var workspaceRepo = scope.ServiceProvider.GetService<IWorkspaceRepository>();
            var markdownSync = scope.ServiceProvider.GetService<ITaskMarkdownSyncService>();
            if (workspaceRepo != null && markdownSync != null)
            {
                var workspace = await workspaceRepo.GetByIdAsync(task.WorkspaceId, cancellationToken);
                if (workspace != null && !string.IsNullOrWhiteSpace(workspace.RepositoryPath))
                {
                    await markdownSync.SyncTaskToDiskAsync(task, workspace.RepositoryPath, cancellationToken: cancellationToken);
                    _logger.LogInformation("Synced promoted task markdown to disk for Task #{TaskId}", taskId);
                }
            }
        }
    }

    public async Task<bool> CancelJobAsync(string jobId, int? taskId = null, CancellationToken cancellationToken = default)
    {
        try
        {
            if (taskId.HasValue && taskId.Value > 0)
            {
                _cancellationManager.Cancel(taskId.Value);
            }

            if (!string.IsNullOrWhiteSpace(jobId))
            {
                _backgroundJobClient.Delete(jobId);
                _logger.LogInformation("Cancelled Hangfire job {JobId}", jobId);
            }

            if (taskId.HasValue && taskId.Value > 0)
            {
                using var scope = _scopeFactory.CreateScope();
                var taskRepo = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
                var task = await taskRepo.GetByIdAsync(taskId.Value, cancellationToken);
                if (task != null && task.Status != TaskOverallStatus.Closed && task.Status != TaskOverallStatus.Cancelled)
                {
                    task.Status = TaskOverallStatus.Cancelled;
                    await taskRepo.UpdateAsync(task, cancellationToken: cancellationToken);
                    _logger.LogInformation("Marked Task #{TaskId} status as Cancelled after job cancellation", taskId.Value);

                    var workspaceRepo = scope.ServiceProvider.GetService<IWorkspaceRepository>();
                    var markdownSync = scope.ServiceProvider.GetService<ITaskMarkdownSyncService>();
                    if (workspaceRepo != null && markdownSync != null)
                    {
                        var workspace = await workspaceRepo.GetByIdAsync(task.WorkspaceId, cancellationToken);
                        if (workspace != null && !string.IsNullOrWhiteSpace(workspace.RepositoryPath))
                        {
                            await markdownSync.ArchiveOrCleanupTaskFileAsync(task, workspace.RepositoryPath, archive: true, cancellationToken);
                            _logger.LogInformation("Archived task file for cancelled Task #{TaskId}", taskId.Value);
                        }
                    }
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cancel job {JobId} for Task #{TaskId}", jobId, taskId);
            return false;
        }
    }

    public async Task<bool> RequeueJobAsync(string jobId, int? taskId = null, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(jobId))
            {
                _backgroundJobClient.Requeue(jobId);
                _logger.LogInformation("Requeued Hangfire job {JobId}", jobId);
                return true;
            }

            if (taskId.HasValue && taskId.Value > 0)
            {
                using var scope = _scopeFactory.CreateScope();
                var taskRepo = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
                var task = await taskRepo.GetByIdAsync(taskId.Value, cancellationToken);
                if (task?.Priority == TaskPriority.Critical)
                {
                    _taskJobQueue.EnqueueUrgent(taskId.Value);
                    _logger.LogInformation("Re-enqueued critical Task #{TaskId} to llm-urgent", taskId.Value);
                }
                else
                {
                    _taskJobQueue.EnqueueInference(taskId.Value);
                    _logger.LogInformation("Re-enqueued Task #{TaskId} to llm-inference", taskId.Value);
                }
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to requeue job {JobId}", jobId);
            if (taskId.HasValue && taskId.Value > 0)
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var taskRepo = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
                    var task = await taskRepo.GetByIdAsync(taskId.Value, cancellationToken);
                    if (task?.Priority == TaskPriority.Critical)
                    {
                        _taskJobQueue.EnqueueUrgent(taskId.Value);
                    }
                    else
                    {
                        _taskJobQueue.EnqueueInference(taskId.Value);
                    }
                    return true;
                }
                catch
                {
                    _taskJobQueue.EnqueueInference(taskId.Value);
                    return true;
                }
            }
            return false;
        }
    }

    public async Task<bool> EmergencyReleaseLockAsync(int workspaceId, int? taskId = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var released = _lockProvider.EmergencyRelease(workspaceId, taskId);

            using var scope = _scopeFactory.CreateScope();
            var workspaceRepo = scope.ServiceProvider.GetRequiredService<IWorkspaceRepository>();
            var workspace = await workspaceRepo.GetByIdAsync(workspaceId, cancellationToken);
            if (workspace != null)
            {
                if (!string.IsNullOrWhiteSpace(workspace.RepositoryPath))
                {
                    GitLockPurgeHelper.PurgeDanglingLocks(workspace.RepositoryPath, workspaceId, _pathResolver, _logger);
                }

                if (!string.IsNullOrWhiteSpace(workspace.Ext4MirrorPath))
                {
                    GitLockPurgeHelper.PurgeDanglingLocks(workspace.Ext4MirrorPath, workspaceId, _pathResolver, _logger);
                }
            }

            return released;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing emergency release for Workspace #{WorkspaceId}", workspaceId);
            return false;
        }
    }

    public async Task<bool> RescheduleTaskAsync(int taskId, bool restartPipeline = false, CancellationToken cancellationToken = default)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var taskRepo = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
            var task = await taskRepo.GetByIdAsync(taskId, cancellationToken);
            if (task == null)
            {
                _logger.LogWarning("Cannot reschedule Task #{TaskId}: task not found", taskId);
                return false;
            }

            if (restartPipeline)
            {
                task.CurrentStageId = string.Empty;
                task.Status = TaskOverallStatus.Open;
                task.SquashCommitSha = null;
            }
            else
            {
                if (task.Status == TaskOverallStatus.Failed || task.Status == TaskOverallStatus.Cancelled)
                {
                    task.Status = TaskOverallStatus.Open;
                }
            }

            task.IsWaitingForHuman = false;
            task.InterruptedAgentProfileId = null;
            task.InterruptedStageId = null;
            task.UpdatedAt = DateTime.UtcNow;

            await taskRepo.UpdateAsync(task, dependsOn: null, cancellationToken);

            var jobId = _taskJobQueue.EnqueueInference(taskId);
            _logger.LogInformation("Rescheduled Task #{TaskId} (RestartPipeline={RestartPipeline}, JobId={JobId})", taskId, restartPipeline, jobId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reschedule Task #{TaskId}", taskId);
            return false;
        }
    }

    public async Task<int> ReorderInferenceQueueByPriorityAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var monitoring = _jobStorage.GetMonitoringApi();
            var rawInference = monitoring.EnqueuedJobs(HangfireQueues.LlmInference, 0, 1000);
            if (rawInference.Count == 0)
            {
                return 0;
            }

            var taskIds = new HashSet<int>();
            ExtractTaskIds(rawInference, taskIds);
            var tasks = await LoadTasksAsync(taskIds, cancellationToken);

            var currentQueueOrder = new List<(string JobId, int TaskId, ForgeTask? Task, DateTime EnqueuedAt)>();
            // Hangfire queue storage can return jobs in non-deterministic or hash-bucket order.
            // We order by EnqueuedAt ascending to reconstruct the true FIFO execution sequence (head to tail).
            foreach (var (key, dto) in rawInference.OrderBy(x => x.Value.EnqueuedAt ?? DateTime.MinValue))
            {
                if (string.Equals(dto.State, "Deleted", StringComparison.OrdinalIgnoreCase)) continue;
                var (taskId, _, _) = ParseJobInfo(dto.Job);
                if (taskId <= 0) continue; // Preserve non-task background jobs

                tasks.TryGetValue(taskId, out var task);
                currentQueueOrder.Add((key, taskId, task, dto.EnqueuedAt ?? DateTime.UtcNow));
            }

            // Priority ascending; preserve FIFO arrival sequence for equal priority
            var ordered = currentQueueOrder
                .OrderBy(j => j.Task?.Priority ?? TaskPriority.Normal)
                .ThenBy(j => j.EnqueuedAt)
                .ToList();


            bool isAlreadySorted = true;
            for (int i = 0; i < currentQueueOrder.Count; i++)
            {
                if (currentQueueOrder[i].JobId != ordered[i].JobId)
                {
                    isAlreadySorted = false;
                    break;
                }
            }

            bool hasCritical = currentQueueOrder.Any(j => j.Task?.Priority == TaskPriority.Critical);

            if (isAlreadySorted && !hasCritical)
            {
                return 0;
            }

            using var scope = _scopeFactory.CreateScope();
            var taskRepo = scope.ServiceProvider.GetRequiredService<ITaskRepository>();

            foreach (var item in currentQueueOrder)
            {
                _backgroundJobClient.Delete(item.JobId);
            }

            int reorderedCount = 0;
            foreach (var item in ordered)
            {
                if (item.TaskId > 0)
                {
                    string newJobId;
                    if (item.Task?.Priority == TaskPriority.Critical)
                    {
                        newJobId = _taskJobQueue.EnqueueUrgent(item.TaskId);
                        _logger.LogInformation("Promoted critical Task #{TaskId} from inference queue to llm-urgent (JobId: {JobId})", item.TaskId, newJobId);
                    }
                    else
                    {
                        newJobId = _taskJobQueue.EnqueueInference(item.TaskId);
                        _logger.LogInformation("Re-enqueued Task #{TaskId} (Priority {Priority}) to llm-inference (JobId: {JobId})", item.TaskId, item.Task?.Priority ?? TaskPriority.Normal, newJobId);
                    }

                    if (item.Task != null)
                    {
                        item.Task.LastHangfireJobId = newJobId;
                        await taskRepo.UpdateAsync(item.Task, cancellationToken: cancellationToken);
                    }
                    reorderedCount++;
                }
            }

            _logger.LogInformation("Reordered {Count} inference queue jobs by priority.", reorderedCount);
            return reorderedCount;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reorder inference queue by priority: {Message}", ex.Message);
            throw;
        }
    }

    private static (int TaskId, string FallbackTitle, string FallbackStage) ParseJobInfo(Job? job)
    {
        if (job == null) return (0, "Background Job", "System");

        int taskId = 0;
        if (job.Args != null && job.Args.Count > 0 && job.Args[0] != null)
        {
            if (int.TryParse(job.Args[0].ToString(), out var parsedId))
            {
                taskId = parsedId;
            }
        }

        var fallbackTitle = taskId > 0 ? $"ForgeTask #{taskId}" : job.Method.Name;
        var fallbackStage = job.Type != null ? job.Type.Name : "Utility";
        return (taskId, fallbackTitle, fallbackStage);
    }

    private static void ExtractTaskIds(JobList<EnqueuedJobDto> jobs, HashSet<int> taskIds)
    {
        foreach (var (_, jobDto) in jobs)
        {
            if (string.Equals(jobDto.State, "Deleted", StringComparison.OrdinalIgnoreCase)) continue;
            var (taskId, _, _) = ParseJobInfo(jobDto.Job);
            if (taskId > 0) taskIds.Add(taskId);
        }
    }

    private static void ExtractTaskIdsFromProcessing(JobList<ProcessingJobDto> jobs, HashSet<int> taskIds)
    {
        foreach (var (_, jobDto) in jobs)
        {
            var (taskId, _, _) = ParseJobInfo(jobDto.Job);
            if (taskId > 0) taskIds.Add(taskId);
        }
    }

    private static void ExtractTaskIdsFromFailed(JobList<FailedJobDto> jobs, HashSet<int> taskIds)
    {
        foreach (var (_, jobDto) in jobs)
        {
            var (taskId, _, _) = ParseJobInfo(jobDto.Job);
            if (taskId > 0) taskIds.Add(taskId);
        }
    }

    private async Task<Dictionary<int, ForgeTask>> LoadTasksAsync(IEnumerable<int> taskIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, ForgeTask>();
        using var scope = _scopeFactory.CreateScope();
        var taskRepo = scope.ServiceProvider.GetRequiredService<ITaskRepository>();

        var tasks = await taskRepo.GetByIdsAsync(taskIds, cancellationToken);
        foreach (var task in tasks)
        {
            result[task.Id] = task;
        }

        return result;
    }

    private static IEnumerable<QueuedJobItem> MapEnqueued(
        JobList<EnqueuedJobDto> jobs,
        string queueName,
        string status,
        IReadOnlyDictionary<int, ForgeTask> tasks)
    {
        foreach (var (key, dto) in jobs)
        {
            if (string.Equals(dto.State, "Deleted", StringComparison.OrdinalIgnoreCase)) continue;
            var (taskId, fallbackTitle, fallbackStage) = ParseJobInfo(dto.Job);
            var title = fallbackTitle;
            var stage = fallbackStage;
            var priority = TaskPriority.Normal;

            if (taskId > 0 && tasks.TryGetValue(taskId, out var task))
            {
                title = task.Title;
                stage = !string.IsNullOrWhiteSpace(task.CurrentStageId) ? task.CurrentStageId : "Queued";
                priority = task.Priority;
            }
            else if (queueName == HangfireQueues.LlmUrgent)
            {
                priority = TaskPriority.Critical;
            }

            yield return new QueuedJobItem(
                key,
                queueName,
                taskId,
                title,
                stage,
                dto.EnqueuedAt ?? DateTime.UtcNow,
                status,
                priority);
        }
    }

    private static IEnumerable<QueuedJobItem> MapProcessing(
        JobList<ProcessingJobDto> jobs,
        IReadOnlyDictionary<int, ForgeTask> tasks)
    {
        foreach (var (key, dto) in jobs)
        {
            var (taskId, fallbackTitle, fallbackStage) = ParseJobInfo(dto.Job);
            var title = fallbackTitle;
            var stage = fallbackStage;
            var priority = TaskPriority.Normal;

            if (taskId > 0 && tasks.TryGetValue(taskId, out var task))
            {
                title = task.Title;
                stage = !string.IsNullOrWhiteSpace(task.CurrentStageId) ? task.CurrentStageId : "Processing";
                priority = task.Priority;
            }

            yield return new QueuedJobItem(
                key,
                "processing",
                taskId,
                title,
                stage,
                dto.StartedAt ?? DateTime.UtcNow,
                "Running",
                priority);
        }
    }

    private static IEnumerable<QueuedJobItem> MapFailed(
        JobList<FailedJobDto> jobs,
        IReadOnlyDictionary<int, ForgeTask> tasks)
    {
        foreach (var (key, dto) in jobs)
        {
            var (taskId, fallbackTitle, fallbackStage) = ParseJobInfo(dto.Job);
            var title = fallbackTitle;
            var stage = fallbackStage;
            var priority = TaskPriority.Normal;

            if (taskId > 0 && tasks.TryGetValue(taskId, out var task))
            {
                title = task.Title;
                stage = !string.IsNullOrWhiteSpace(task.CurrentStageId) ? task.CurrentStageId : "Failed";
                priority = task.Priority;
            }

            yield return new QueuedJobItem(
                key,
                "failed",
                taskId,
                title,
                stage,
                dto.FailedAt ?? DateTime.UtcNow,
                "Failed",
                priority);
        }
    }
}
