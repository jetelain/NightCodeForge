using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Core.Workflow.Stages;

namespace NightCodeForge.Agents.Triage;

/// <summary>
/// Service implementing external untrusted intake triage on the base branch using a RestrictedReadOnly sandbox.
/// Validates reproducibility, explores the base branch, and proposes structured backlog tasks in 'Suggested' status.
/// </summary>
public class TriageService : ITriageService
{
    private readonly ITaskRepository _taskRepository;
    private readonly IWorkspaceRepository _workspaceRepository;
    private readonly IHarnessRegistry _harnessRegistry;
    private readonly ITaskMarkdownSyncService _markdownSyncService;
    private readonly IWorkspacePathResolver _pathResolver;
    private readonly IGlobalOptimizationSettingsService _globalOptimizationSettings;
    private readonly ILogger<TriageService> _logger;

    public TriageService(
        ITaskRepository taskRepository,
        IWorkspaceRepository workspaceRepository,
        IHarnessRegistry harnessRegistry,
        ITaskMarkdownSyncService markdownSyncService,
        IWorkspacePathResolver pathResolver,
        IGlobalOptimizationSettingsService globalOptimizationSettings,
        ILogger<TriageService> logger)
    {
        _taskRepository = taskRepository ?? throw new ArgumentNullException(nameof(taskRepository));
        _workspaceRepository = workspaceRepository ?? throw new ArgumentNullException(nameof(workspaceRepository));
        _harnessRegistry = harnessRegistry ?? throw new ArgumentNullException(nameof(harnessRegistry));
        _markdownSyncService = markdownSyncService ?? throw new ArgumentNullException(nameof(markdownSyncService));
        _pathResolver = pathResolver ?? throw new ArgumentNullException(nameof(pathResolver));
        _globalOptimizationSettings = globalOptimizationSettings ?? throw new ArgumentNullException(nameof(globalOptimizationSettings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<ForgeTask>> TriageExternalReportAsync(
        Workspace workspace,
        TriageReportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(request);

        var baseBranch = !string.IsNullOrWhiteSpace(request.BaseBranch)
            ? request.BaseBranch
            : (!string.IsNullOrWhiteSpace(workspace.DefaultBaseBranch) ? workspace.DefaultBaseBranch : WorkflowDefaults.DefaultBaseBranch);

        _logger.LogInformation("Starting untrusted intake triage for '{ReportTitle}' on workspace #{WorkspaceId} ({BaseBranch})",
            request.Title, workspace.Id, baseBranch);

        // Pre-sync existing backlog tasks into clone/docs/tasks so the triage agent knows about existing backlog
        string? cloneDir = null;
        if (!string.IsNullOrWhiteSpace(workspace.Ext4MirrorPath))
        {
            var customClone = _pathResolver.ToHostPath(workspace.Ext4MirrorPath);
            if (Directory.Exists(customClone)) cloneDir = customClone;
        }
        if (cloneDir == null && workspace.Id > 0)
        {
            try
            {
                var canonicalClone = _pathResolver.GetAgentWorkingClonePath(workspace.Id);
                if (!string.IsNullOrWhiteSpace(canonicalClone))
                {
                    var hostClone = _pathResolver.ToHostPath(canonicalClone);
                    if (Directory.Exists(hostClone)) cloneDir = hostClone;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not resolve working clone directory for workspace #{WorkspaceId}", workspace.Id);
            }
        }

        var repoPath = (!string.IsNullOrWhiteSpace(workspace.RepositoryPath) && Directory.Exists(workspace.RepositoryPath))
            ? workspace.RepositoryPath
            : cloneDir;

        if (string.IsNullOrWhiteSpace(repoPath))
        {
            throw new DirectoryNotFoundException(
                $"Neither workspace repository path '{workspace.RepositoryPath}' nor agent clone directory exists for workspace #{workspace.Id}.");
        }

        try
        {
            await _markdownSyncService.SyncAllTasksToDiskAsync(workspace, repoPath, repoPath, cloneDirectory: cloneDir, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to pre-sync existing backlog tasks to clone/docs/tasks for workspace #{WorkspaceId}", workspace.Id);
        }

        // 1. Resolve triage agent profile with RestrictedReadOnly sandbox tier and ReadOnly agent mode
        var profile = await _workspaceRepository.GetEffectiveAgentProfileAsync(workspace.Id, StandardStageIds.Triage, cancellationToken);

        if (profile == null)
        {
            throw new AgentProfileNotFoundException(StandardStageIds.Triage, workspace.Id);
        }

        // 2. Set up synthetic triage task and pipeline context
        var triageTask = new ForgeTask
        {
            WorkspaceId = workspace.Id,
            Title = $"Triage: {request.Title}",
            Description = request.Description,
            BaseBranch = baseBranch,
            FeatureBranch = baseBranch,
            CurrentStageId = StandardStageIds.Triage,
            Status = TaskOverallStatus.Suggested,
            Priority = TaskPriority.Critical,
            MergeMode = MergeMode.ManualReviewRequired,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var pipelineContext = new PipelineContext(triageTask, workspace);
        pipelineContext.CurrentProfile = profile;

        var stageDirective = "Analyze the reported task against the repository base branch. Determine affected files and acceptance criteria. Inspect existing backlog tasks in docs/tasks/ to ensure the issue is not already reported, in progress, or planned. Call propose_tasks to recommend structured backlog items. You have read-only access and cannot modify code or access external networks. If the reported task or method is missing from the base branch, verify its absence in the codebase and promptly propose the needed feature or bug backlog item via propose_tasks without exhaustively exploring other branches.";

        var workspacePath = !string.IsNullOrWhiteSpace(workspace.Ext4MirrorPath)
            ? workspace.Ext4MirrorPath
            : workspace.RepositoryPath;

        var harnessId = profile.HarnessId;
        if (!_harnessRegistry.TryGetHarness(harnessId, out var harness) || harness == null)
        {
            throw new InvalidOperationException($"Harness '{harnessId}' not found for triage execution.");
        }

        var agentContext = new AgentSessionContext
        {
            ForgeTask = triageTask,
            Workspace = workspace,
            Profile = profile,
            StageId = StandardStageIds.Triage,
            StageDirective = stageDirective,
            RequiredMode = AgentMode.ReadOnly,
            WorkspacePath = workspacePath,
            BootstrapPrompt = $"External Report to Triage:\nTitle: {request.Title}\nDescription:\n{request.Description}\n\nPlease inspect the codebase and check existing backlog tasks in docs/tasks/, determine affected components, and invoke propose_tasks to recommend concrete backlog items.",
            OptimizationFlags = profile.OptimizationFlags ?? _globalOptimizationSettings?.GetFlags() ?? new LocalOptimizationFlags(),
            PipelineContext = pipelineContext
        };

        // 3. Execute triage stage with the harness
        var runResult = await harness.RunStageAsync(agentContext, cancellationToken);
        _logger.LogInformation("Triage agent finished with status {Status}: {Summary}", runResult.Status, runResult.Summary);

        // 4. Extract proposed backlog items and persist as Suggested tasks
        var pendingProposals = (runResult.Proposals != null && runResult.Proposals.Count > 0)
            ? runResult.Proposals.ToList()
            : pipelineContext.DataBag.Get<List<TaskProposal>>(PipelineDataBagKeys.PendingProposals) ?? new List<TaskProposal>();
        var createdTasks = new List<ForgeTask>();

        if (pendingProposals.Count > 0)
        {
            foreach (var p in pendingProposals)
            {
                var newTask = new ForgeTask
                {
                    WorkspaceId = workspace.Id,
                    Title = p.Title,
                    Description = p.Description,
                    Status = TaskOverallStatus.Suggested,
                    Origin = TaskOrigin.UntrustedIntake,
                    MergeMode = MergeMode.ManualReviewRequired,
                    BaseBranch = baseBranch,
                    Priority = p.Priority > 0 ? p.Priority : TaskPriority.Normal,
                    CurrentStageId = string.Empty,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };

                var created = await _taskRepository.CreateWithBaselineAsync(
                    newTask,
                    dependsOn: null,
                    createdBy: AuditActors.TriageAgent,
                    reason: "Created task proposal from untrusted intake triage",
                    cancellationToken: cancellationToken);
                createdTasks.Add(created);
                _logger.LogInformation("Triage generated suggested task #{NewTaskId}: '{Title}'", created.Id, created.Title);

                // Mirror new suggested task to docs/tasks/ (both workspace repository and clone)
                var syncTarget = (!string.IsNullOrWhiteSpace(workspace.RepositoryPath) && Directory.Exists(workspace.RepositoryPath))
                    ? workspace.RepositoryPath
                    : cloneDir;

                if (!string.IsNullOrWhiteSpace(syncTarget))
                {
                    try
                    {
                        await _markdownSyncService.SyncTaskToDiskAsync(
                            created,
                            syncTarget,
                            dependencies: null,
                            agentProfileId: null,
                            cloneDirectory: cloneDir,
                            cancellationToken: cancellationToken);
                    }
                    catch (Exception mex)
                    {
                        _logger.LogWarning(mex, "Failed to mirror triaged task #{TaskId} to docs/tasks/", created.Id);
                    }
                }
            }
        }

        return createdTasks;
    }
}
