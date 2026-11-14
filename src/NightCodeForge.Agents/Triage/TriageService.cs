using Microsoft.Extensions.Logging;
using NightCodeForge.Agents.Context;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Workflow;

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
    private readonly IAgentWorkspaceContextService _workspaceContext;
    private readonly ITaskMarkdownSyncService _markdownSyncService;
    private readonly IGlobalOptimizationSettingsService _globalOptimizationSettings;
    private readonly ILogger<TriageService> _logger;

    public TriageService(
        ITaskRepository taskRepository,
        IWorkspaceRepository workspaceRepository,
        IHarnessRegistry harnessRegistry,
        IAgentWorkspaceContextService workspaceContext,
        ITaskMarkdownSyncService markdownSyncService,
        IGlobalOptimizationSettingsService globalOptimizationSettings,
        ILogger<TriageService> logger)
    {
        _taskRepository = taskRepository ?? throw new ArgumentNullException(nameof(taskRepository));
        _workspaceRepository = workspaceRepository ?? throw new ArgumentNullException(nameof(workspaceRepository));
        _harnessRegistry = harnessRegistry ?? throw new ArgumentNullException(nameof(harnessRegistry));
        _workspaceContext = workspaceContext ?? throw new ArgumentNullException(nameof(workspaceContext));
        _markdownSyncService = markdownSyncService ?? throw new ArgumentNullException(nameof(markdownSyncService));
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
            ? request.BaseBranch.Trim()
            : (!string.IsNullOrWhiteSpace(workspace.DefaultBaseBranch) ? workspace.DefaultBaseBranch.Trim() : WorkflowDefaults.DefaultBaseBranch);

        _logger.LogInformation("Starting untrusted intake triage for '{ReportTitle}' on workspace #{WorkspaceId} ({BaseBranch})",
            request.Title, workspace.Id, baseBranch);

        // Pre-sync existing backlog tasks into clone/docs/tasks so the triage agent knows about existing backlog
        var locations = _workspaceContext.ResolveLocations(workspace, taskId: null, workspacePathOverride: null);
        var cloneDir = locations.CloneDir;
        var repoPath = (!string.IsNullOrWhiteSpace(workspace.RepositoryPath) && Directory.Exists(workspace.RepositoryPath))
            ? workspace.RepositoryPath
            : cloneDir;

        if (string.IsNullOrWhiteSpace(repoPath))
        {
            throw new DirectoryNotFoundException(
                $"Neither workspace repository path '{workspace.RepositoryPath}' nor agent clone directory exists for workspace #{workspace.Id}.");
        }

        await _workspaceContext.SyncBacklogToDiskAsync(workspace, locations with { RepoPath = repoPath }, cancellationToken);

        // 1. Resolve triage agent profile and enforce RestrictedReadOnly sandbox tier and ReadOnly agent mode
        var resolvedProfile = await _workspaceRepository.GetEffectiveAgentProfileAsync(workspace.Id, StandardStageIds.Triage, cancellationToken);

        if (resolvedProfile == null)
        {
            throw new AgentProfileNotFoundException(StandardStageIds.Triage, workspace.Id);
        }

        var profile = EnforceRestrictedReadOnlyProfile(resolvedProfile);

        var externalSource = string.IsNullOrWhiteSpace(request.ExternalSource)
            ? null
            : request.ExternalSource.Trim();
        var externalReferenceId = string.IsNullOrWhiteSpace(request.ExternalReferenceId)
            ? null
            : request.ExternalReferenceId.Trim();

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
            Origin = request.Origin,
            ExternalSource = externalSource,
            ExternalReferenceId = externalReferenceId,
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
            SandboxScope = SandboxScopeDescriptor.ForWorkspace(workspace, SandboxSecurityTier.RestrictedReadOnly),
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

        if (runResult.Status == AgentRunStatus.Cancelled && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        if (runResult.Status != AgentRunStatus.Completed)
        {
            var errorDetail = !string.IsNullOrWhiteSpace(runResult.Error)
                ? runResult.Error
                : (!string.IsNullOrWhiteSpace(runResult.Summary)
                    ? runResult.Summary
                    : $"Triage stage ended with non-completed status '{runResult.Status}'.");
            throw new InvalidOperationException($"Untrusted intake triage failed ({runResult.Status}): {errorDetail}");
        }

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
                    Origin = request.Origin,
                    ExternalSource = externalSource,
                    ExternalReferenceId = externalReferenceId,
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

    private static AgentProfile EnforceRestrictedReadOnlyProfile(AgentProfile profile)
    {
        var sanitizedTools = SanitizeReadOnlyTools(profile.PermittedTools);
        var toolsAlreadyValid = profile.PermittedTools != null
            && profile.PermittedTools.Count == sanitizedTools.Count
            && profile.PermittedTools.SequenceEqual(sanitizedTools, StringComparer.Ordinal);

        if (profile.Mode == AgentMode.ReadOnly
            && profile.SecurityTier == SandboxSecurityTier.RestrictedReadOnly
            && toolsAlreadyValid)
        {
            return profile;
        }

        return new AgentProfile
        {
            Id = profile.Id,
            WorkspaceId = profile.WorkspaceId,
            Name = profile.Name,
            HarnessId = profile.HarnessId,
            ModelName = profile.ModelName,
            ThinkingEffort = profile.ThinkingEffort,
            ThinkingTokenBudget = profile.ThinkingTokenBudget,
            Temperature = profile.Temperature,
            HarnessConfigurationJson = profile.HarnessConfigurationJson,
            OptimizationFlags = profile.OptimizationFlags,
            PermittedTools = sanitizedTools,
            StageDirectives = profile.StageDirectives,
            Mode = AgentMode.ReadOnly,
            SecurityTier = SandboxSecurityTier.RestrictedReadOnly,
            SystemPrompt = profile.SystemPrompt,
            MaxTurns = profile.MaxTurns,
            AllowNoTests = profile.AllowNoTests,
            ContextWindowTokens = profile.ContextWindowTokens,
            MaxOutputTokens = profile.MaxOutputTokens,
            RoutingTarget = profile.RoutingTarget,
            Workspace = profile.Workspace
        };
    }

    private static IReadOnlyList<string> SanitizeReadOnlyTools(IReadOnlyList<string>? configuredTools)
    {
        if (configuredTools == null || configuredTools.Count == 0)
        {
            return DefaultAgentTools.ReadOnly;
        }

        var allowedReadOnly = new HashSet<string>(DefaultAgentTools.ReadOnly, StringComparer.OrdinalIgnoreCase);
        var sanitized = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var tool in configuredTools)
        {
            if (string.IsNullOrWhiteSpace(tool))
            {
                continue;
            }

            var trimmed = tool.Trim();
            if (allowedReadOnly.Contains(trimmed) && seen.Add(trimmed))
            {
                sanitized.Add(trimmed);
            }
        }

        if (sanitized.Count == 0)
        {
            return DefaultAgentTools.ReadOnly;
        }

        if (!seen.Contains(CanonicalToolNames.ProposeTasks))
        {
            sanitized.Add(CanonicalToolNames.ProposeTasks);
        }

        return sanitized;
    }
}
