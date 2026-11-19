using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Security;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Web.Services;

public sealed record TaskCreationRequest(
    int WorkspaceId,
    Workspace? Workspace,
    string? RepositoryPath,
    string Title,
    string? Description,
    int? WorkflowId,
    int Priority,
    MergeMode MergeMode,
    string? BaseBranch,
    string? FeatureBranch,
    TaskOverallStatus InitialStatus,
    IReadOnlyList<int>? PrerequisiteIds,
    bool LaunchImmediately,
    string AuditReason,
    TaskDifficulty Difficulty = TaskDifficulty.Standard,
    IReadOnlyList<int>? AttachmentIds = null);

public sealed record TaskCreationResult(ForgeTask Task, bool WasDispatched);

/// <summary>
/// UI coordination service encapsulating task creation, dependency validation, disk markdown sync,
/// GitHub mirroring, and launch queue dispatch outside Razor presentation components.
/// </summary>
public interface ITaskCreationUiService
{
    Task<bool> IsPullRequestDeliveryAvailableAsync(Workspace? workspace, CancellationToken cancellationToken = default);
    Task<bool> IsPullRequestDeliveryAvailableAsync(int workspaceId, Workspace? activeWorkspace = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WorkflowDefinition>> LoadWorkflowsAsync(int? workspaceId, CancellationToken cancellationToken = default);
    int? ResolveDefaultWorkflowId(IReadOnlyList<WorkflowDefinition> workflows, int? preferredWorkflowId, int? currentSelectionId = null);
    Task<IReadOnlyList<ForgeTask>> LoadSelectablePrerequisitesAsync(int workspaceId, CancellationToken cancellationToken = default);
    IReadOnlyList<ForgeTask> FilterPrerequisites(IEnumerable<ForgeTask> availableTasks, string? searchQuery);
    Task<TaskCreationResult> CreateTaskAsync(TaskCreationRequest request, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public class TaskCreationUiService : ITaskCreationUiService
{
    private readonly ITaskRepository _taskRepository;
    private readonly IWorkspaceRepository _workspaceRepository;
    private readonly IWorkflowRepository _workflowRepository;
    private readonly ITaskMarkdownSyncService _markdownSyncService;
    private readonly IGitHubTaskMirrorService _gitHubTaskMirrorService;
    private readonly IGitHubPullRequestDeliveryService _prDeliveryService;
    private readonly ITaskJobQueue _taskJobQueue;
    private readonly ITaskAttachmentRepository _attachmentRepository;
    private readonly SecurityOptions _securityOptions;
    private readonly ILogger<TaskCreationUiService> _logger;

    public TaskCreationUiService(
        ITaskRepository taskRepository,
        IWorkspaceRepository workspaceRepository,
        IWorkflowRepository workflowRepository,
        ITaskMarkdownSyncService markdownSyncService,
        IGitHubTaskMirrorService gitHubTaskMirrorService,
        IGitHubPullRequestDeliveryService prDeliveryService,
        ITaskJobQueue taskJobQueue,
        ITaskAttachmentRepository attachmentRepository,
        IOptions<SecurityOptions> securityOptions,
        ILogger<TaskCreationUiService> logger)
    {
        _taskRepository = taskRepository ?? throw new ArgumentNullException(nameof(taskRepository));
        _workspaceRepository = workspaceRepository ?? throw new ArgumentNullException(nameof(workspaceRepository));
        _workflowRepository = workflowRepository ?? throw new ArgumentNullException(nameof(workflowRepository));
        _markdownSyncService = markdownSyncService ?? throw new ArgumentNullException(nameof(markdownSyncService));
        _gitHubTaskMirrorService = gitHubTaskMirrorService ?? throw new ArgumentNullException(nameof(gitHubTaskMirrorService));
        _prDeliveryService = prDeliveryService ?? throw new ArgumentNullException(nameof(prDeliveryService));
        _taskJobQueue = taskJobQueue ?? throw new ArgumentNullException(nameof(taskJobQueue));
        _attachmentRepository = attachmentRepository ?? throw new ArgumentNullException(nameof(attachmentRepository));
        _securityOptions = securityOptions?.Value ?? throw new ArgumentNullException(nameof(securityOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<bool> IsPullRequestDeliveryAvailableAsync(
        Workspace? workspace,
        CancellationToken cancellationToken = default) =>
        GitHubPullRequestUiHelper.IsPullRequestDeliveryAvailableAsync(
            _securityOptions,
            _prDeliveryService,
            workspace,
            cancellationToken);

    public async Task<bool> IsPullRequestDeliveryAvailableAsync(
        int workspaceId,
        Workspace? activeWorkspace = null,
        CancellationToken cancellationToken = default)
    {
        if (workspaceId <= 0)
        {
            return false;
        }

        var workspace = activeWorkspace?.Id == workspaceId
            ? activeWorkspace
            : await _workspaceRepository.GetByIdAsync(workspaceId, cancellationToken);

        return await IsPullRequestDeliveryAvailableAsync(workspace, cancellationToken);
    }

    public async Task<IReadOnlyList<WorkflowDefinition>> LoadWorkflowsAsync(
        int? workspaceId,
        CancellationToken cancellationToken = default) =>
        await _workflowRepository.GetWorkflowsAsync(workspaceId, cancellationToken);

    public int? ResolveDefaultWorkflowId(
        IReadOnlyList<WorkflowDefinition> workflows,
        int? preferredWorkflowId,
        int? currentSelectionId = null)
    {
        ArgumentNullException.ThrowIfNull(workflows);

        if (currentSelectionId.HasValue && workflows.Any(w => w.Id == currentSelectionId.Value))
        {
            return currentSelectionId.Value;
        }

        if (preferredWorkflowId.HasValue && workflows.Any(w => w.Id == preferredWorkflowId.Value))
        {
            return preferredWorkflowId.Value;
        }

        return (workflows.FirstOrDefault(w => w.IsDefault) ?? workflows.FirstOrDefault())?.Id;
    }

    public async Task<IReadOnlyList<ForgeTask>> LoadSelectablePrerequisitesAsync(
        int workspaceId,
        CancellationToken cancellationToken = default)
    {
        if (workspaceId <= 0)
        {
            return Array.Empty<ForgeTask>();
        }

        var tasks = await _taskRepository.GetByWorkspaceIdAsync(workspaceId, cancellationToken);
        return tasks
            .Where(t => t.Status != TaskOverallStatus.Triage &&
                        t.Status != TaskOverallStatus.Rejected &&
                        t.Status != TaskOverallStatus.Cancelled &&
                        t.Status != TaskOverallStatus.Failed)
            .OrderBy(t => t.Id)
            .ToList();
    }

    public IReadOnlyList<ForgeTask> FilterPrerequisites(
        IEnumerable<ForgeTask> availableTasks,
        string? searchQuery)
    {
        ArgumentNullException.ThrowIfNull(availableTasks);

        if (string.IsNullOrWhiteSpace(searchQuery))
        {
            return availableTasks.ToList();
        }

        var search = searchQuery.Trim();
        if (int.TryParse(search.TrimStart('#'), out var searchId))
        {
            return availableTasks
                .Where(t => t.Id == searchId || t.Title.Contains(search, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        return availableTasks
            .Where(t => t.Title.Contains(search, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public async Task<TaskCreationResult> CreateTaskAsync(
        TaskCreationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.InitialStatus == TaskOverallStatus.Triage)
        {
            throw new ArgumentException("Cannot manually create a task with Triage status.", nameof(request));
        }

        var trimmedTitle = request.Title?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmedTitle))
        {
            throw new ArgumentException("Task title cannot be empty.", nameof(request));
        }

        var workspace = request.Workspace ??
                        (request.WorkspaceId > 0
                            ? await _workspaceRepository.GetByIdAsync(request.WorkspaceId, cancellationToken)
                            : null);

        if (request.MergeMode == MergeMode.ExternalPullRequest &&
            !await IsPullRequestDeliveryAvailableAsync(workspace, cancellationToken))
        {
            throw new InvalidOperationException(
                "MergeMode 'ExternalPullRequest' requires GitHub Pull Request delivery to be enabled, configured, and authenticated.");
        }

        var prereqIds = request.PrerequisiteIds?
            .Where(id => id > 0)
            .Distinct()
            .ToList() ?? new List<int>();

        IReadOnlyList<ForgeTask> prereqTasks = Array.Empty<ForgeTask>();
        if (prereqIds.Count > 0)
        {
            prereqTasks = await _taskRepository.GetByIdsAsync(prereqIds, cancellationToken);
            var validIds = prereqTasks
                .Where(t => t.WorkspaceId == request.WorkspaceId)
                .Select(t => t.Id)
                .ToHashSet();

            var missingOrForeign = prereqIds.Where(id => !validIds.Contains(id)).ToList();
            if (missingOrForeign.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Prerequisite task(s) {string.Join(", ", missingOrForeign.Select(id => $"#{id}"))} do not exist in workspace #{request.WorkspaceId}.");
            }

            var triagePrereqs = prereqTasks
                .Where(t => t.Status == TaskOverallStatus.Triage)
                .Select(t => t.Id)
                .OrderBy(id => id)
                .ToList();
            if (triagePrereqs.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Prerequisite task(s) {string.Join(", ", triagePrereqs.Select(id => $"#{id}"))} are triage session records and cannot be used as dependencies.");
            }
        }

        var validatedAttachmentIds = await AttachmentCitationParser.ValidateAndResolveWorkspaceIdsAsync(
            _attachmentRepository,
            request.WorkspaceId,
            request.AttachmentIds,
            request.Description,
            cancellationToken);

        var newTask = new ForgeTask
        {
            WorkspaceId = request.WorkspaceId,
            WorkflowId = request.WorkflowId,
            Title = trimmedTitle,
            Description = request.Description?.Trim() ?? string.Empty,
            Priority = request.Priority,
            Difficulty = request.Difficulty,
            MergeMode = request.MergeMode,
            BaseBranch = string.IsNullOrWhiteSpace(request.BaseBranch) ? "main" : request.BaseBranch.Trim(),
            FeatureBranch = string.IsNullOrWhiteSpace(request.FeatureBranch) ? string.Empty : request.FeatureBranch.Trim(),
            Status = request.InitialStatus,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var created = await _taskRepository.CreateWithBaselineAsync(
            newTask,
            dependsOn: prereqIds,
            createdBy: AuditActors.User,
            reason: request.AuditReason,
            cancellationToken);

        if (validatedAttachmentIds.Count > 0)
        {
            await _attachmentRepository.SetTaskAttachmentLinksAsync(created.Id, validatedAttachmentIds, cancellationToken);
            var linkedAttachments = await _attachmentRepository.GetByTaskIdAsync(created.Id, cancellationToken)
                ?? Array.Empty<TaskAttachment>();
            if (linkedAttachments.Count == 0)
            {
                linkedAttachments = await _attachmentRepository.GetByIdsAsync(validatedAttachmentIds, cancellationToken)
                    ?? Array.Empty<TaskAttachment>();
            }
            created.AttachmentLinks = linkedAttachments
                .Select(a => new TaskAttachmentLink
                {
                    TaskId = created.Id,
                    TaskAttachmentId = a.Id,
                    TaskAttachment = a
                })
                .ToList();
        }

        await GitHubPullRequestUiHelper.MirrorTaskOpenedSafeAsync(
            _gitHubTaskMirrorService,
            created,
            workspace,
            _logger,
            cancellationToken);

        var repoPath = !string.IsNullOrWhiteSpace(request.RepositoryPath)
            ? request.RepositoryPath
            : workspace?.RepositoryPath;

        if (!string.IsNullOrWhiteSpace(repoPath) && Directory.Exists(repoPath))
        {
            try
            {
                await _markdownSyncService.SyncTaskToDiskAsync(
                    created,
                    repoPath,
                    dependencies: prereqIds,
                    cancellationToken: cancellationToken);
            }
            catch (Exception syncEx) when (syncEx is not OperationCanceledException)
            {
                _logger.LogWarning(syncEx, "Failed to sync created task markdown to disk for Task #{TaskId}", created.Id);
            }
        }

        if (!request.LaunchImmediately)
        {
            return new TaskCreationResult(created, WasDispatched: false);
        }

        var allPrerequisitesClosed = prereqIds.Count == 0 ||
                                     prereqTasks.All(t => t.Status == TaskOverallStatus.Closed);
        if (!allPrerequisitesClosed)
        {
            _logger.LogInformation(
                "Deferred immediate dispatch for Task #{TaskId} because prerequisite task(s) {Prereqs} are not yet Closed.",
                created.Id,
                string.Join(", ", prereqIds.Select(id => $"#{id}")));
            return new TaskCreationResult(created, WasDispatched: false);
        }

        var workflows = await _workflowRepository.GetWorkflowsAsync(
            created.WorkspaceId > 0 ? created.WorkspaceId : null,
            cancellationToken);
        var workflow = workflows.FirstOrDefault(w => w.Id == created.WorkflowId) ?? workflows.FirstOrDefault();
        var initialStage = workflow?.Stages.MinBy(s => s.SequenceOrder);
        var tier = initialStage?.QueueTier ?? StageQueueTier.TaskReview;

        var jobId = _taskJobQueue.EnqueueStage(created.Id, tier, created.Priority);
        _logger.LogInformation(
            "Dispatched Task #{TaskId} (P{Priority}) to tier {Tier} (JobId: {JobId})",
            created.Id,
            created.Priority,
            tier,
            jobId);

        created.LastHangfireJobId = jobId;
        await _taskRepository.UpdateAsync(created, dependsOn: prereqIds, cancellationToken);

        return new TaskCreationResult(created, WasDispatched: true);
    }
}
