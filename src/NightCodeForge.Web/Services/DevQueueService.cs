using System.Collections.Concurrent;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
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
    private const int EnqueuedJobsSnapshotLimit = 100;
    private const int ProcessingJobsSnapshotLimit = 50;
    private const int FailedJobsSnapshotLimit = 50;
    private const int StaleProcessingPurgeLimit = 100;
    private const int ReorderEnqueuedScanLimit = 1000;
    private const string ProcessingQueueName = "processing";
    private const string ProcessingFallbackStage = "Processing";
    private const string RunningJobStatus = "Running";

    private readonly IWorkspaceLockProvider _lockProvider;
    private readonly IBackgroundJobClient _backgroundJobClient;
    private readonly ITaskJobQueue _taskJobQueue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly JobStorage _jobStorage;
    private readonly ITaskCancellationManager _cancellationManager;
    private readonly IWorkspacePathResolver _pathResolver;
    private readonly IDevQueueSkippedJobRegistry _skippedJobRegistry;
    private readonly ITaskOrchestrator _taskOrchestrator;
    private readonly ILogger<DevQueueService> _logger;

    public DevQueueService(
        IWorkspaceLockProvider lockProvider,
        IBackgroundJobClient backgroundJobClient,
        ITaskJobQueue taskJobQueue,
        IServiceScopeFactory scopeFactory,
        JobStorage jobStorage,
        ITaskCancellationManager cancellationManager,
        IWorkspacePathResolver pathResolver,
        IDevQueueSkippedJobRegistry skippedJobRegistry,
        ITaskOrchestrator taskOrchestrator,
        ILogger<DevQueueService> logger)
    {
        _lockProvider = lockProvider ?? throw new ArgumentNullException(nameof(lockProvider));
        _backgroundJobClient = backgroundJobClient ?? throw new ArgumentNullException(nameof(backgroundJobClient));
        _taskJobQueue = taskJobQueue ?? throw new ArgumentNullException(nameof(taskJobQueue));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _jobStorage = jobStorage ?? throw new ArgumentNullException(nameof(jobStorage));
        _cancellationManager = cancellationManager ?? throw new ArgumentNullException(nameof(cancellationManager));
        _pathResolver = pathResolver ?? throw new ArgumentNullException(nameof(pathResolver));
        _skippedJobRegistry = skippedJobRegistry ?? throw new ArgumentNullException(nameof(skippedJobRegistry));
        _taskOrchestrator = taskOrchestrator ?? throw new ArgumentNullException(nameof(taskOrchestrator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<DevQueueSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var activeLocks = _lockProvider.GetActiveLocks();

        var urgentJobs = new List<QueuedJobItem>();
        var codeReviewJobs = new List<QueuedJobItem>();
        var implementationJobs = new List<QueuedJobItem>();
        var taskReviewJobs = new List<QueuedJobItem>();
        var inferenceJobs = new List<QueuedJobItem>();
        var utilityJobs = new List<QueuedJobItem>();
        var processingJobs = new List<QueuedJobItem>();
        var failedJobs = new List<QueuedJobItem>();

        try
        {
            var monitoring = _jobStorage.GetMonitoringApi();

            var rawUrgent = monitoring.EnqueuedJobs(HangfireQueues.LlmUrgent, 0, EnqueuedJobsSnapshotLimit);
            var rawCodeReview = monitoring.EnqueuedJobs(HangfireQueues.LlmCodeReview, 0, EnqueuedJobsSnapshotLimit);
            var rawImplementation = monitoring.EnqueuedJobs(HangfireQueues.LlmImplementation, 0, EnqueuedJobsSnapshotLimit);
            var rawTaskReview = monitoring.EnqueuedJobs(HangfireQueues.LlmTaskReview, 0, EnqueuedJobsSnapshotLimit);
            var rawInference = monitoring.EnqueuedJobs(HangfireQueues.LlmInference, 0, EnqueuedJobsSnapshotLimit);
            var rawDefault = monitoring.EnqueuedJobs(HangfireQueues.Default, 0, EnqueuedJobsSnapshotLimit);
            var rawProcessing = monitoring.ProcessingJobs(0, ProcessingJobsSnapshotLimit);
            var rawFailed = monitoring.FailedJobs(0, FailedJobsSnapshotLimit);
            var activeServerIds = GetActiveServerIds(monitoring);

            var taskIds = new HashSet<int>();
            ExtractTaskIds(rawUrgent, taskIds);
            ExtractTaskIds(rawCodeReview, taskIds);
            ExtractTaskIds(rawImplementation, taskIds);
            ExtractTaskIds(rawTaskReview, taskIds);
            ExtractTaskIds(rawInference, taskIds);
            ExtractTaskIds(rawDefault, taskIds);
            ExtractTaskIdsFromProcessing(rawProcessing, taskIds);
            ExtractTaskIdsFromFailed(rawFailed, taskIds);

            var taskLookup = await LoadTasksAsync(taskIds, cancellationToken);

            urgentJobs.AddRange(MapEnqueued(rawUrgent, HangfireQueues.LlmUrgent, "Urgent", taskLookup));
            codeReviewJobs.AddRange(MapEnqueued(rawCodeReview, HangfireQueues.LlmCodeReview, "Code Review", taskLookup));
            implementationJobs.AddRange(MapEnqueued(rawImplementation, HangfireQueues.LlmImplementation, "Implementation", taskLookup));
            taskReviewJobs.AddRange(MapEnqueued(rawTaskReview, HangfireQueues.LlmTaskReview, "Task Review", taskLookup));
            inferenceJobs.AddRange(MapEnqueued(rawInference, HangfireQueues.LlmInference, "Queued", taskLookup));
            utilityJobs.AddRange(MapEnqueued(rawDefault, HangfireQueues.Default, "Queued", taskLookup));
            processingJobs.AddRange(MapProcessing(rawProcessing, taskLookup, activeServerIds));
            failedJobs.AddRange(MapFailed(rawFailed, taskLookup));

            // Clean up any skipped jobs that are no longer present in Hangfire queues
            var currentEnqueuedJobIds = new HashSet<string>(
                rawUrgent.Select(j => j.Key)
                .Concat(rawCodeReview.Select(j => j.Key))
                .Concat(rawImplementation.Select(j => j.Key))
                .Concat(rawTaskReview.Select(j => j.Key))
                .Concat(rawInference.Select(j => j.Key))
                .Concat(rawDefault.Select(j => j.Key)));

            _skippedJobRegistry.PruneActiveJobIds(currentEnqueuedJobIds);
        }
        catch (ObjectDisposedException ex)
        {
            _logger.LogDebug(ex, "JobStorage was disposed while querying Hangfire monitoring API.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query Hangfire monitoring API");
        }

        return new DevQueueSnapshot(
            urgentJobs,
            codeReviewJobs,
            implementationJobs,
            taskReviewJobs,
            inferenceJobs,
            utilityJobs,
            processingJobs,
            failedJobs,
            activeLocks,
            _skippedJobRegistry.GetSkippedJobs());
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
            return false;
        }
    }

    public async Task<bool> PromoteToUrgentAsync(int taskId, CancellationToken cancellationToken = default)
    {
        if (taskId <= 0) return false;

        try
        {
            var snapshot = await GetSnapshotAsync(cancellationToken);
            var queuedJob = snapshot.CodeReviewJobs.FirstOrDefault(j => j.TaskId == taskId)
                         ?? snapshot.ImplementationJobs.FirstOrDefault(j => j.TaskId == taskId)
                         ?? snapshot.TaskReviewJobs.FirstOrDefault(j => j.TaskId == taskId)
                         ?? snapshot.InferenceJobs.FirstOrDefault(j => j.TaskId == taskId)
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
                changedBy: AuditActors.User,
                reason: $"Updated Priority from P{oldPriority} to P{TaskPriority.Critical} (llm-urgent promotion)",
                cancellationToken: cancellationToken);

            var workspaceRepo = scope.ServiceProvider.GetService<IWorkspaceRepository>();
            var markdownSync = scope.ServiceProvider.GetService<ITaskMarkdownSyncService>();
            if (workspaceRepo != null && markdownSync != null)
            {
                var workspace = await workspaceRepo.GetByIdAsync(task.WorkspaceId, cancellationToken);
                if (workspace != null && !string.IsNullOrWhiteSpace(workspace.RepositoryPath))
                {
                    var deps = await taskRepo.GetDependenciesAsync(task.Id, cancellationToken);
                    await markdownSync.SyncTaskToDiskAsync(task, workspace.RepositoryPath, deps, cancellationToken: cancellationToken);
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
                _skippedJobRegistry.RemoveSkippedJob(jobId);
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
                    task.IsWaitingForHuman = false;
                    await taskRepo.UpdateWithHistoryAsync(
                        task,
                        dependsOn: null,
                        changedBy: AuditActors.User,
                        reason: "Job cancelled via DevQueue",
                        cancellationToken: cancellationToken);
                    _logger.LogInformation("Marked Task #{TaskId} status as Cancelled after job cancellation", taskId.Value);

                    var convs = await taskRepo.GetConversationsAsync(task.Id, cancellationToken);
                    foreach (var c in convs.Where(c =>
                        c.Status == StageExecutionStatus.Running ||
                        c.Status == StageExecutionStatus.WaitingForHuman ||
                        c.Status == StageExecutionStatus.Interrupted))
                    {
                        await taskRepo.CompleteConversationAsync(c.Id, StageExecutionStatus.Cancelled, summary: "Execution was cancelled.", cancellationToken: cancellationToken);
                    }

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
                var pipelineProvider = scope.ServiceProvider.GetRequiredService<IWorkflowPipelineProvider>();
                var task = await taskRepo.GetByIdAsync(taskId.Value, cancellationToken);
                if (task != null)
                {
                    var pipeline = await pipelineProvider.GetPipelineAsync(task, cancellationToken);
                    var stage = (!string.IsNullOrWhiteSpace(task.CurrentStageId) ? pipeline.GetStage(task.CurrentStageId) : null)
                        ?? pipeline.InitialStage
                        ?? throw new InvalidOperationException($"Task #{task.Id} has no initial or active stage.");

                    task.IsDeferredByPause = false;
                    var newJobId = _taskJobQueue.EnqueueStage(task.Id, stage.QueueTier, task.Priority);
                    task.LastHangfireJobId = newJobId;
                    await taskRepo.UpdateAsync(task, cancellationToken: cancellationToken);
                    _logger.LogInformation("Re-enqueued Task #{TaskId} to stage tier {Tier} (JobId: {JobId})", taskId.Value, stage.QueueTier, newJobId);
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to requeue job {JobId} for Task #{TaskId}", jobId, taskId);
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

    public async Task<bool> RetryCurrentStageAsync(int taskId, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var taskRepo = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
        var initialTask = await taskRepo.GetByIdAsync(taskId, cancellationToken)
            ?? throw new KeyNotFoundException($"Task #{taskId} not found.");

        if (_cancellationManager.IsRegistered(taskId))
        {
            _cancellationManager.Cancel(taskId);
        }

        using var lockReleaser = await _lockProvider.AcquireTaskLockAsync(initialTask.WorkspaceId, taskId, cancellationToken);

        var task = await taskRepo.GetByIdAsync(taskId, cancellationToken)
            ?? throw new KeyNotFoundException($"Task #{taskId} not found.");

        var workspaceRepo = scope.ServiceProvider.GetRequiredService<IWorkspaceRepository>();
        var workspace = task.Workspace ?? await workspaceRepo.GetByIdAsync(task.WorkspaceId, cancellationToken)
            ?? throw new KeyNotFoundException($"Workspace #{task.WorkspaceId} for task #{taskId} not found.");

        if (task.Status == TaskOverallStatus.Failed || task.Status == TaskOverallStatus.Cancelled)
        {
            task.Status = TaskOverallStatus.Open;
        }

        task.IsWaitingForHuman = false;
        task.InterruptedAgentProfileId = null;
        task.InterruptedStageId = null;
        task.IsDeferredByPause = false;
        task.UpdatedAt = DateTime.UtcNow;

        if (task.Questions != null)
        {
            foreach (var question in task.Questions.Where(q => q.Status == QuestionStatus.Pending))
            {
                question.Status = QuestionStatus.Skipped;
            }
        }

        if (task.Checkpoints != null)
        {
            foreach (var checkpoint in task.Checkpoints.Where(c => !string.IsNullOrEmpty(c.SerializedMemory)))
            {
                checkpoint.SerializedMemory = string.Empty;
            }
        }

        var pipelineContext = new PipelineContext(task, workspace);
        pipelineContext.ResetRetries();
        pipelineContext.DataBag.Remove(PipelineDataBagKeys.RecoveredSerializedMemory);
        pipelineContext.DataBag.Remove(PipelineDataBagKeys.SerializedMemory);
        pipelineContext.DataBag.Remove(PipelineDataBagKeys.LastHumanAnswer);
        pipelineContext.DataBag.Remove(PipelineDataBagKeys.LastHumanAnswerJson);
        pipelineContext.DataBag.Remove(PipelineDataBagKeys.PendingSyntheticPriming);
        pipelineContext.DataBag.Remove(PipelineDataBagKeys.CompilerErrors);
        pipelineContext.DataBag.Remove(PipelineDataBagKeys.FailedTests);
        pipelineContext.SyncDataBagToTask();

        var convs = await taskRepo.GetConversationsAsync(task.Id, cancellationToken) ?? Array.Empty<Conversation>();
        foreach (var c in convs.Where(c =>
            c.Status == StageExecutionStatus.Running ||
            c.Status == StageExecutionStatus.WaitingForHuman ||
            c.Status == StageExecutionStatus.Interrupted ||
            c.Status == StageExecutionStatus.Pending))
        {
            await taskRepo.CompleteConversationAsync(
                c.Id,
                StageExecutionStatus.Superseded,
                summary: "Stage restarted; previous conversation superseded.",
                cancellationToken: cancellationToken);
        }

        // Delete dangling/previous Hangfire job and clear LastHangfireJobId before saving so that
        // if a Hangfire worker picks up the newly enqueued job before UpdateAsync completes,
        // it does not read the old LastHangfireJobId and self-abort as superseded.
        if (!string.IsNullOrWhiteSpace(task.LastHangfireJobId))
        {
            _taskJobQueue.Delete(task.LastHangfireJobId);
            task.LastHangfireJobId = null;
        }

        var rescheduleReason = $"Rescheduled task execution via DevQueue (Status: {task.Status})";

        await taskRepo.UpdateWithHistoryAsync(
            task,
            dependsOn: null,
            changedBy: AuditActors.User,
            reason: rescheduleReason,
            cancellationToken: cancellationToken);

        var dependencyStore = scope.ServiceProvider.GetRequiredService<ITaskDependencyStore>();
        await dependencyStore.UpdateTaskStatusAsync(task.Id, task.Status, cancellationToken);
        await dependencyStore.SetWaitingForHumanAsync(task.Id, false, cancellationToken);

        var pipelineProvider = scope.ServiceProvider.GetRequiredService<IWorkflowPipelineProvider>();
        var pipeline = await pipelineProvider.GetPipelineAsync(task, cancellationToken);
        var stage = (!string.IsNullOrWhiteSpace(task.CurrentStageId) ? pipeline.GetStage(task.CurrentStageId) : null)
            ?? pipeline.InitialStage
            ?? throw new InvalidOperationException($"Task #{task.Id} has no initial or active stage.");

        var jobId = _taskJobQueue.EnqueueStage(taskId, stage.QueueTier, task.Priority);
        task.LastHangfireJobId = jobId;
        await taskRepo.UpdateAsync(task, cancellationToken: cancellationToken);
        _logger.LogInformation("Retried stage for Task #{TaskId} (Stage: {StageId}, JobId: {JobId}, Tier: {Tier})",
            taskId, stage.StageId, jobId, stage.QueueTier);
        return true;
    }

    public async Task<bool> RestartPipelineAsync(
        int taskId,
        TaskRestartGitMode gitMode = TaskRestartGitMode.PreserveWork,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Delegating pipeline restart for Task #{TaskId} (Mode: {GitMode}) to TaskOrchestrator", taskId, gitMode);
        return await _taskOrchestrator.RestartTaskPipelineAsync(taskId, gitMode, cancellationToken);
    }

    public async Task<int> ReorderInferenceQueueByPriorityAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var monitoring = _jobStorage.GetMonitoringApi();
            await PurgeStaleProcessingJobsAsync(monitoring, cancellationToken);

            var targetQueues = new[]
            {
                HangfireQueues.LlmCodeReview,
                HangfireQueues.LlmImplementation,
                HangfireQueues.LlmTaskReview,
                HangfireQueues.LlmInference
            };

            var allRawJobs = new List<(string CurrentQueue, string JobId, EnqueuedJobDto Dto)>();
            var taskIds = new HashSet<int>();

            foreach (var q in targetQueues)
            {
                var enqueued = monitoring.EnqueuedJobs(q, 0, ReorderEnqueuedScanLimit);
                foreach (var (key, dto) in enqueued)
                {
                    if (string.Equals(dto.State, "Deleted", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var (taskId, _, _) = ParseJobInfo(dto.Job);
                    if (taskId <= 0) continue;

                    allRawJobs.Add((q, key, dto));
                    taskIds.Add(taskId);
                }
            }

            if (allRawJobs.Count == 0)
            {
                return 0;
            }

            var tasks = await LoadTasksAsync(taskIds, cancellationToken);
            using var scope = _scopeFactory.CreateScope();
            var taskRepo = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
            var pipelineProvider = scope.ServiceProvider.GetRequiredService<IWorkflowPipelineProvider>();

            var candidateJobs = new List<(string JobId, string CurrentQueue, int TaskId, ForgeTask Task, StageQueueTier Tier, DateTime OriginalEnqueuedAt)>();

            using (var conn = _jobStorage.GetConnection())
            {
                foreach (var (currentQueue, jobId, dto) in allRawJobs)
                {
                    var (taskId, _, _) = ParseJobInfo(dto.Job);
                    if (!tasks.TryGetValue(taskId, out var task) || task == null)
                    {
                        _logger.LogCritical("Orphaned Hangfire job {JobId} in queue {Queue} references Task #{TaskId} which does not exist in the task repository; deleting orphaned job.", jobId, currentQueue, taskId);
                        _backgroundJobClient.ChangeState(jobId, new DeletedState(), "Enqueued");
                        _skippedJobRegistry.RemoveSkippedJob(jobId);
                        continue;
                    }

                    var originalEnqueuedStr = conn.GetJobParameter(jobId, "OriginalEnqueuedAt");
                    DateTime originalEnqueuedAt;
                    if (!string.IsNullOrWhiteSpace(originalEnqueuedStr) && DateTimeOffset.TryParse(originalEnqueuedStr, out var parsedOffset))
                    {
                        originalEnqueuedAt = parsedOffset.UtcDateTime;
                    }
                    else
                    {
                        originalEnqueuedAt = dto.EnqueuedAt ?? DateTime.UtcNow;
                    }

                    IWorkflowPipeline pipeline;
                    try
                    {
                        pipeline = await pipelineProvider.GetPipelineAsync(task, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogCritical(ex, "Failed to resolve workflow pipeline for Task #{TaskId} during dev queue reordering; skipping job {JobId}.", task.Id, jobId);
                        _skippedJobRegistry.RecordSkippedJob(new SkippedJobItem(jobId, currentQueue, task.Id, $"Pipeline resolution failed: {ex.Message}", originalEnqueuedAt));
                        continue;
                    }

                    var stage = string.IsNullOrWhiteSpace(task.CurrentStageId)
                        ? pipeline.InitialStage
                        : pipeline.GetStage(task.CurrentStageId);

                    if (stage == null)
                    {
                        _logger.LogCritical("No workflow stage resolved for Task #{TaskId} (CurrentStageId: '{StageId}') during dev queue reordering; skipping job {JobId}.", task.Id, task.CurrentStageId, jobId);
                        _skippedJobRegistry.RecordSkippedJob(new SkippedJobItem(jobId, currentQueue, task.Id, $"Stage '{task.CurrentStageId}' could not be resolved in workflow pipeline", originalEnqueuedAt));
                        continue;
                    }

                    _skippedJobRegistry.RemoveSkippedJob(jobId);
                    candidateJobs.Add((jobId, currentQueue, task.Id, task, stage.QueueTier, originalEnqueuedAt));
                }
            }

            int reorderedCount = 0;
            var reorderExceptions = new List<Exception>();

            // Group by expected target queue
            var jobsByTargetQueue = candidateJobs
                .GroupBy(j => j.Task.Priority <= TaskPriority.Critical
                    ? HangfireQueues.LlmUrgent
                    : j.Tier switch
                    {
                        StageQueueTier.CodeReview => HangfireQueues.LlmCodeReview,
                        StageQueueTier.Implementation => HangfireQueues.LlmImplementation,
                        StageQueueTier.TaskReview => HangfireQueues.LlmTaskReview,
                        _ => throw new ArgumentOutOfRangeException(nameof(j.Tier), j.Tier, $"Unsupported stage queue tier: {j.Tier}")
                    })
                .ToDictionary(g => g.Key, g => g.OrderBy(j => j.Task.Priority).ThenBy(j => j.OriginalEnqueuedAt).ToList());

            foreach (var (targetQueue, targetJobs) in jobsByTargetQueue)
            {
                var currentJobsInQueue = candidateJobs.Where(j => j.CurrentQueue == targetQueue).ToList();
                bool needsReorder = targetJobs.Any(j => j.CurrentQueue != targetQueue);

                if (!needsReorder && currentJobsInQueue.Count == targetJobs.Count)
                {
                    for (int i = 0; i < targetJobs.Count; i++)
                    {
                        if (currentJobsInQueue[i].JobId != targetJobs[i].JobId)
                        {
                            needsReorder = true;
                            break;
                        }
                    }
                }
                else
                {
                    needsReorder = true;
                }

                if (!needsReorder)
                {
                    continue;
                }

                // Enqueue-First Pattern:
                // Enqueue the new job first so that if enqueuing fails (e.g. invalid tier, storage outage),
                // the old job remains untouched in the queue and no work is ever lost.
                foreach (var item in targetJobs)
                {
                    string newJobId;
                    try
                    {
                        if (item.Task.Priority <= TaskPriority.Critical)
                        {
                            newJobId = _taskJobQueue.EnqueueUrgent(item.TaskId, item.OriginalEnqueuedAt);
                        }
                        else
                        {
                            newJobId = _taskJobQueue.EnqueueStage(item.TaskId, item.Tier, item.Task.Priority, item.OriginalEnqueuedAt);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to re-enqueue Task #{TaskId} (Tier {Tier}, Priority {Priority}) to queue {TargetQueue}. Old job {JobId} remains untouched in queue.",
                            item.TaskId, item.Tier, item.Task.Priority, targetQueue, item.JobId);
                        reorderExceptions.Add(ex);
                        continue;
                    }

                    // Delete the old job atomically only if it is still in Enqueued state
                    var oldJobDeleted = _backgroundJobClient.ChangeState(item.JobId, new DeletedState(), "Enqueued");
                    if (!oldJobDeleted)
                    {
                        // Old job was already fetched by a worker concurrently. Delete the new job to prevent duplicate execution.
                        var newJobDeleted = _backgroundJobClient.ChangeState(newJobId, new DeletedState(), "Enqueued");
                        if (!newJobDeleted)
                        {
                            _logger.LogError("CRITICAL: Old job {OldJobId} for Task #{TaskId} was already fetched by a worker, and rollback cancellation of newly enqueued job {NewJobId} failed! Task may run twice.",
                                item.JobId, item.TaskId, newJobId);
                        }
                        else
                        {
                            _logger.LogInformation("Old job {OldJobId} for Task #{TaskId} was already fetched by a worker; cancelled newly enqueued job {NewJobId}.", item.JobId, item.TaskId, newJobId);
                        }
                        continue;
                    }

                    try
                    {
                        await taskRepo.UpdateLastHangfireJobIdAsync(item.TaskId, newJobId, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to update LastHangfireJobId for Task #{TaskId} to {NewJobId} after reorder.", item.TaskId, newJobId);
                        throw;
                    }

                    reorderedCount++;

                    _logger.LogInformation("Reordered/migrated Task #{TaskId} (Priority {Priority}, Tier {Tier}) to {Queue} (JobId: {JobId})",
                        item.TaskId, item.Task.Priority, item.Tier, targetQueue, newJobId);
                }
            }

            if (reorderExceptions.Count > 0)
            {
                throw new AggregateException("One or more jobs failed to re-enqueue during queue reordering.", reorderExceptions);
            }

            _logger.LogInformation("Reordered/migrated {Count} queue jobs by priority.", reorderedCount);
            return reorderedCount;
        }
        catch (ObjectDisposedException ex)
        {
            _logger.LogDebug(ex, "JobStorage was disposed while reordering inference queue.");
            return 0;
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

    private static HashSet<string> GetActiveServerIds(IMonitoringApi monitoring)
    {
        var rawServers = monitoring.Servers();
        if (rawServers == null || rawServers.Count == 0)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return new HashSet<string>(
            rawServers
                .Where(s => s != null && !string.IsNullOrWhiteSpace(s.Name))
                .Select(s => s.Name),
            StringComparer.OrdinalIgnoreCase);
    }

    private async Task PurgeStaleProcessingJobsAsync(IMonitoringApi monitoring, CancellationToken cancellationToken)
    {
        var rawProcessing = monitoring.ProcessingJobs(0, StaleProcessingPurgeLimit);
        if (rawProcessing == null || rawProcessing.Count == 0)
        {
            return;
        }

        var procTaskIds = new HashSet<int>();
        ExtractTaskIdsFromProcessing(rawProcessing, procTaskIds);
        var procTasks = await LoadTasksAsync(procTaskIds, cancellationToken);
        var activeServerIds = GetActiveServerIds(monitoring);
        _ = MapProcessing(rawProcessing, procTasks, activeServerIds).ToList();
    }

    private IEnumerable<QueuedJobItem> MapProcessing(
        JobList<ProcessingJobDto> jobs,
        IReadOnlyDictionary<int, ForgeTask> tasks,
        IReadOnlySet<string> activeServerIds)
    {
        var seenTaskIds = new HashSet<int>();
        foreach (var (key, dto) in jobs)
        {
            if (dto == null || !dto.InProcessingState)
            {
                continue;
            }

            var (taskId, fallbackTitle, fallbackStage) = ParseJobInfo(dto.Job);
            var isServerDead = activeServerIds.Count > 0
                && !string.IsNullOrWhiteSpace(dto.ServerId)
                && !activeServerIds.Contains(dto.ServerId);

            if (isServerDead)
            {
                TryDeleteStaleProcessingJob(key, taskId, $"Dead Hangfire server '{dto.ServerId}'");
                continue;
            }

            var isFromActiveServer = activeServerIds.Count > 0
                && !string.IsNullOrWhiteSpace(dto.ServerId)
                && activeServerIds.Contains(dto.ServerId);

            var title = fallbackTitle;
            var stage = fallbackStage;
            var priority = TaskPriority.Normal;

            if (taskId > 0)
            {
                if (!tasks.TryGetValue(taskId, out var task) || task == null)
                {
                    if (!isFromActiveServer)
                    {
                        TryDeleteStaleProcessingJob(key, taskId, "Task not found in repository");
                    }
                    continue;
                }

                if (!task.IsRunnable())
                {
                    if (!isFromActiveServer)
                    {
                        TryDeleteStaleProcessingJob(
                            key,
                            taskId,
                            $"Task status '{task.Status}' (WaitingForHuman={task.IsWaitingForHuman}, DeferredByPause={task.IsDeferredByPause}) is not runnable");
                    }
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(task.LastHangfireJobId)
                    && !string.IsNullOrWhiteSpace(key)
                    && !string.Equals(task.LastHangfireJobId, key, StringComparison.Ordinal))
                {
                    if (!isFromActiveServer)
                    {
                        TryDeleteStaleProcessingJob(
                            key,
                            taskId,
                            $"Superseded by newer Hangfire job '{task.LastHangfireJobId}'");
                    }
                    continue;
                }

                if (!seenTaskIds.Add(taskId))
                {
                    continue;
                }

                title = task.Title;
                stage = !string.IsNullOrWhiteSpace(task.CurrentStageId) ? task.CurrentStageId : ProcessingFallbackStage;
                priority = task.Priority;
            }

            yield return new QueuedJobItem(
                key,
                ProcessingQueueName,
                taskId,
                title,
                stage,
                dto.StartedAt ?? DateTime.UtcNow,
                RunningJobStatus,
                priority);
        }
    }

    private void TryDeleteStaleProcessingJob(string jobId, int taskId, string reason)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return;
        }

        try
        {
            _logger.LogWarning(
                "Purging stale Hangfire Processing job {JobId} for Task #{TaskId}: {Reason}.",
                jobId,
                taskId,
                reason);
            _backgroundJobClient.Delete(jobId);
            _skippedJobRegistry.RemoveSkippedJob(jobId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Best-effort cleanup of stale Hangfire Processing job {JobId} failed.", jobId);
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
