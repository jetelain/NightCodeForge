using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Core.Workflow.Stages;

namespace NightCodeForge.Agents.Triage;

/// <summary>
/// Service implementing external untrusted intake triage on the base branch using a RestrictedReadOnly sandbox.
/// Validates reproducibility, explores the base branch, and proposes structured backlog issues in 'Suggested' status.
/// </summary>
public class TriageService : ITriageService
{
    private readonly IIssueRepository _issueRepository;
    private readonly IWorkspaceRepository _workspaceRepository;
    private readonly IHarnessRegistry _harnessRegistry;
    private readonly IIssueMarkdownSyncService? _markdownSyncService;
    private readonly IExecutionEnvironmentProvider? _envProvider;
    private readonly IExecutionEnvironment? _executionEnv;
    private readonly IWorkspacePathResolver? _pathResolver;
    private readonly ILogger<TriageService> _logger;

    public TriageService(
        IIssueRepository issueRepository,
        IWorkspaceRepository workspaceRepository,
        IHarnessRegistry harnessRegistry,
        IIssueMarkdownSyncService? markdownSyncService = null,
        IExecutionEnvironmentProvider? envProvider = null,
        IExecutionEnvironment? executionEnv = null,
        IWorkspacePathResolver? pathResolver = null,
        ILogger<TriageService>? logger = null)
    {
        _issueRepository = issueRepository ?? throw new ArgumentNullException(nameof(issueRepository));
        _workspaceRepository = workspaceRepository ?? throw new ArgumentNullException(nameof(workspaceRepository));
        _harnessRegistry = harnessRegistry ?? throw new ArgumentNullException(nameof(harnessRegistry));
        _markdownSyncService = markdownSyncService;
        _envProvider = envProvider;
        _executionEnv = executionEnv;
        _pathResolver = pathResolver;
        _logger = logger ?? NullLogger<TriageService>.Instance;
    }

    public async Task<IReadOnlyList<Issue>> TriageExternalReportAsync(
        Workspace workspace,
        TriageReportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(request);

        var baseBranch = !string.IsNullOrWhiteSpace(request.BaseBranch)
            ? request.BaseBranch
            : (!string.IsNullOrWhiteSpace(workspace.DefaultBaseBranch) ? workspace.DefaultBaseBranch : "main");

        _logger.LogInformation("Starting untrusted intake triage for '{ReportTitle}' on workspace #{WorkspaceId} ({BaseBranch})",
            request.Title, workspace.Id, baseBranch);

        // 1. Resolve triage agent profile with RestrictedReadOnly sandbox tier and ReadOnly agent mode
        var profile = workspace.AgentProfiles?.FirstOrDefault(p =>
            string.Equals(p.Id, TriageStage.DefaultStageId, StringComparison.OrdinalIgnoreCase) ||
            p.Name.Contains("triage", StringComparison.OrdinalIgnoreCase));

        if (profile == null)
        {
            profile = new AgentProfile
            {
                Id = TriageStage.DefaultStageId,
                WorkspaceId = workspace.Id,
                Name = "Untrusted Intake Triage Agent",
                HarnessId = "crucible",
                ModelName = workspace.AgentProfiles?.FirstOrDefault()?.ModelName ?? "default",
                Mode = AgentMode.ReadOnly,
                SecurityTier = SandboxSecurityTier.RestrictedReadOnly,
                PermittedTools = new[] { "read", "bash", "web_search", "propose_issues" }
            };
        }

        // 2. Set up synthetic triage issue and pipeline context
        var triageIssue = new Issue
        {
            WorkspaceId = workspace.Id,
            Title = $"Triage: {request.Title}",
            Description = request.Description,
            BaseBranch = baseBranch,
            FeatureBranch = baseBranch,
            CurrentStageId = TriageStage.DefaultStageId,
            Status = IssueOverallStatus.Suggested,
            Priority = 1,
            MergeMode = MergeMode.ManualReviewRequired,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var pipelineContext = new PipelineContext(triageIssue, workspace);
        pipelineContext.CurrentProfile = profile;

        var stageDirective = "Analyze the reported issue against the repository base branch. Determine affected files and acceptance criteria. Call propose_issues to recommend structured backlog items. You have read-only access and cannot modify code or access external networks. If the reported issue or method is missing from the base branch, verify its absence in the codebase and promptly propose the needed feature or bug backlog item via propose_issues without exhaustively exploring other branches.";

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
            Issue = triageIssue,
            Workspace = workspace,
            Profile = profile,
            StageId = TriageStage.DefaultStageId,
            StageDirective = stageDirective,
            RequiredMode = AgentMode.ReadOnly,
            WorkspacePath = workspacePath,
            BootstrapPrompt = $"External Report to Triage:\nTitle: {request.Title}\nDescription:\n{request.Description}\n\nPlease inspect the codebase, determine affected components, and invoke propose_issues to recommend concrete backlog items.",
            OptimizationFlags = profile.OptimizationFlags ?? new LocalOptimizationFlags(),
            PipelineContext = pipelineContext
        };

        // 3. Execute triage stage with the harness
        var runResult = await harness.RunStageAsync(agentContext, cancellationToken);
        _logger.LogInformation("Triage agent finished with status {Status}: {Summary}", runResult.Status, runResult.Summary);

        // 4. Extract proposed backlog items and persist as Suggested issues
        var pendingProposals = pipelineContext.DataBag.Get<List<IssueProposal>>("PendingProposals") ?? new List<IssueProposal>();
        var createdIssues = new List<Issue>();

        if (pendingProposals.Count > 0)
        {
            foreach (var p in pendingProposals)
            {
                var newIssue = new Issue
                {
                    WorkspaceId = workspace.Id,
                    Title = p.Title,
                    Description = p.Description,
                    Status = IssueOverallStatus.Suggested,
                    MergeMode = MergeMode.ManualReviewRequired,
                    BaseBranch = baseBranch,
                    Priority = 1,
                    CurrentStageId = "issue-review",
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };

                var created = await _issueRepository.CreateAsync(newIssue, cancellationToken: cancellationToken);
                createdIssues.Add(created);
                _logger.LogInformation("Triage generated suggested issue #{NewIssueId}: '{Title}'", created.Id, created.Title);

                // Mirror new suggested issue to docs/issues/
                if (_markdownSyncService != null && !string.IsNullOrWhiteSpace(workspace.RepositoryPath) && Directory.Exists(workspace.RepositoryPath))
                {
                    try
                    {
                        var issuesDir = Path.Combine(workspace.RepositoryPath, "docs", "issues");
                        Directory.CreateDirectory(issuesDir);
                        var slug = SanitizeSlug(created.Title);
                        var fileName = $"{DateTime.UtcNow:yyyy-MM-dd}-{slug}.md";
                        var filePath = Path.Combine(issuesDir, fileName);
                        var markdown = _markdownSyncService.GenerateMarkdown(created);
                        await File.WriteAllTextAsync(filePath, markdown, cancellationToken);
                    }
                    catch (Exception mex)
                    {
                        _logger.LogWarning(mex, "Failed to mirror triaged issue #{IssueId} to docs/issues/", created.Id);
                    }
                }
            }
        }

        return createdIssues;
    }

    private static string SanitizeSlug(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "issue";
        var cleaned = System.Text.RegularExpressions.Regex.Replace(title.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
        return string.IsNullOrWhiteSpace(cleaned) ? "issue" : cleaned;
    }
}
