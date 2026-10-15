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
    private readonly JobStorage? _jobStorage;
    private readonly ILogger<DevQueueService> _logger;

    public DevQueueService(
        IWorkspaceLockProvider lockProvider,
        IBackgroundJobClient backgroundJobClient,
        ITaskJobQueue taskJobQueue,
        IServiceScopeFactory scopeFactory,
        JobStorage? jobStorage = null,
        ILogger<DevQueueService>? logger = null)
    {
        _lockProvider = lockProvider ?? throw new ArgumentNullException(nameof(lockProvider));
        _backgroundJobClient = backgroundJobClient ?? throw new ArgumentNullException(nameof(backgroundJobClient));
        _taskJobQueue = taskJobQueue ?? throw new ArgumentNullException(nameof(taskJobQueue));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _jobStorage = jobStorage;
        _logger = logger ?? NullLogger<DevQueueService>.Instance;
    }

    public async Task<DevQueueSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var activeLocks = _lockProvider.GetActiveLocks();

        var urgentJobs = new List<QueuedJobItem>();
        var inferenceJobs = new List<QueuedJobItem>();
        var utilityJobs = new List<QueuedJobItem>();
        var processingJobs = new List<QueuedJobItem>();
        var failedJobs = new List<QueuedJobItem>();

        JobStorage? storage = _jobStorage;
        if (storage == null)
        {
            try
            {
                storage = JobStorage.Current;
            }
            catch
            {
                // JobStorage.Current not initialized yet
            }
        }

        if (storage != null)
        {
            try
            {
                var monitoring = storage.GetMonitoringApi();

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

    public async Task<bool> CancelJobAsync(string jobId, int? taskId = null, CancellationToken cancellationToken = default)
    {
        try
        {
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
                _taskJobQueue.EnqueueInference(taskId.Value);
                _logger.LogInformation("Re-enqueued Task #{TaskId} to llm-inference", taskId.Value);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to requeue job {JobId}", jobId);
            if (taskId.HasValue && taskId.Value > 0)
            {
                _taskJobQueue.EnqueueInference(taskId.Value);
                return true;
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
            if (workspace != null && !string.IsNullOrWhiteSpace(workspace.RepositoryPath))
            {
                var indexLock = Path.Combine(workspace.RepositoryPath, ".git", "index.lock");
                if (File.Exists(indexLock))
                {
                    File.Delete(indexLock);
                    _logger.LogWarning("Purged dangling .git/index.lock file for Workspace #{WorkspaceId}", workspaceId);
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

        foreach (var id in taskIds)
        {
            var task = await taskRepo.GetByIdAsync(id, cancellationToken);
            if (task != null)
            {
                result[id] = task;
            }
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
            var (taskId, fallbackTitle, fallbackStage) = ParseJobInfo(dto.Job);
            var title = fallbackTitle;
            var stage = fallbackStage;

            if (taskId > 0 && tasks.TryGetValue(taskId, out var task))
            {
                title = task.Title;
                stage = !string.IsNullOrWhiteSpace(task.CurrentStageId) ? task.CurrentStageId : "Queued";
            }

            yield return new QueuedJobItem(
                key,
                queueName,
                taskId,
                title,
                stage,
                dto.EnqueuedAt ?? DateTime.UtcNow,
                status);
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

            if (taskId > 0 && tasks.TryGetValue(taskId, out var task))
            {
                title = task.Title;
                stage = !string.IsNullOrWhiteSpace(task.CurrentStageId) ? task.CurrentStageId : "Processing";
            }

            yield return new QueuedJobItem(
                key,
                "processing",
                taskId,
                title,
                stage,
                dto.StartedAt ?? DateTime.UtcNow,
                "Running");
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

            if (taskId > 0 && tasks.TryGetValue(taskId, out var task))
            {
                title = task.Title;
                stage = !string.IsNullOrWhiteSpace(task.CurrentStageId) ? task.CurrentStageId : "Failed";
            }

            yield return new QueuedJobItem(
                key,
                "failed",
                taskId,
                title,
                stage,
                dto.FailedAt ?? DateTime.UtcNow,
                "Failed");
        }
    }
}
