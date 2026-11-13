using Microsoft.Extensions.Logging;
using NightCodeForge.Agents.Context;
using NightCodeForge.Agents.Optimizations;
using NightCodeForge.Agents.Skills;
using NightCodeForge.Agents.Tools;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Services;

namespace NightCodeForge.Agents.Context;

/// <summary>Repository, working clone and agent working directory of an agent session.</summary>
public sealed record AgentWorkspaceLocations(string RepoPath, string? CloneDir, string AgentWorkingDir);

/// <summary>Project rules and skills sections injected into an agent's system prompt.</summary>
public sealed record AgentPromptContext(string ProjectContext, string SkillsCatalog, IReadOnlyList<string> ActiveSkills);

/// <summary>
/// Shared workspace context discovery of the agent sessions (Crucible, Ideation): locations, backlog pre-sync,
/// project rules, skills catalog, symbol map and backlog digest.
/// </summary>
public interface IAgentWorkspaceContextService
{
    /// <summary>
    /// Resolves the locations of a session across BareHost, WslDirect and ContainerSandbox modes.
    /// <paramref name="workspacePathOverride"/> is the already-resolved repository path of pipeline stages.
    /// </summary>
    AgentWorkspaceLocations ResolveLocations(Workspace workspace, int? taskId, string? workspacePathOverride);

    /// <summary>Pre-syncs the backlog task markdown files to disk so the agent can inspect existing tasks.</summary>
    Task SyncBacklogToDiskAsync(Workspace workspace, AgentWorkspaceLocations locations, CancellationToken cancellationToken);

    /// <summary>Builds the project context section and (only when <c>load_skill</c> is permitted) the skills catalog.</summary>
    Task<AgentPromptContext> BuildPromptContextAsync(
        Workspace workspace,
        string repoPath,
        IReadOnlyList<string> permittedTools,
        IWorkspaceSkillsService skillsService,
        CancellationToken cancellationToken);

    /// <summary>Deterministic symbol map prompt matching the given task text.</summary>
    Task<string> GetSymbolMapPromptAsync(string workspacePath, string taskText, LocalOptimizationFlags flags, CancellationToken cancellationToken);

    /// <summary>Comma separated top-level entries of the repository root, or null when none can be listed.</summary>
    string? DescribeTopLevelEntries(Workspace workspace, string repoPath);

    /// <summary>Builds the existing backlog digest (empty when the backlog has no active task).</summary>
    Task<string> BuildBacklogDigestAsync(int workspaceId, int? excludeTaskId, CancellationToken cancellationToken);

    /// <summary>Builds a bounded recent transcript friction hotspot digest (empty when no friction exists).</summary>
    Task<string> BuildFrictionHotspotsDigestAsync(string repoPath, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class AgentWorkspaceContextService : IAgentWorkspaceContextService
{
    public const string ContainerWorkspaceRoot = "/workspace";
    public const string ContainerCloneDir = "/workspace/clone";
    private const string ContainerTaskDirFormat = "/workspace/task-{0}";
    private const int MaxTopLevelEntries = 20;

    private readonly IWorkspacePathResolver _pathResolver;
    private readonly AgentsMdService _agentsMdService;
    private readonly IWorkspaceSymbolService _symbolService;
    private readonly ITaskMarkdownSyncService _markdownSyncService;
    private readonly IBacklogPlanningContext _backlogContext;
    private readonly ITranscriptAnalysisService _transcriptAnalysisService;
    private readonly ILogger<AgentWorkspaceContextService> _logger;

    public AgentWorkspaceContextService(
        IWorkspacePathResolver pathResolver,
        AgentsMdService agentsMdService,
        IWorkspaceSymbolService symbolService,
        ITaskMarkdownSyncService markdownSyncService,
        IBacklogPlanningContext backlogContext,
        ITranscriptAnalysisService transcriptAnalysisService,
        ILogger<AgentWorkspaceContextService> logger)
    {
        _pathResolver = pathResolver ?? throw new ArgumentNullException(nameof(pathResolver));
        _agentsMdService = agentsMdService ?? throw new ArgumentNullException(nameof(agentsMdService));
        _symbolService = symbolService ?? throw new ArgumentNullException(nameof(symbolService));
        _markdownSyncService = markdownSyncService ?? throw new ArgumentNullException(nameof(markdownSyncService));
        _backlogContext = backlogContext ?? throw new ArgumentNullException(nameof(backlogContext));
        _transcriptAnalysisService = transcriptAnalysisService ?? throw new ArgumentNullException(nameof(transcriptAnalysisService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public AgentWorkspaceLocations ResolveLocations(Workspace workspace, int? taskId, string? workspacePathOverride)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        var hasOverride = !string.IsNullOrEmpty(workspacePathOverride);
        var repoPath = hasOverride
            ? workspacePathOverride!
            : (!string.IsNullOrEmpty(workspace.RepositoryPath) ? workspace.RepositoryPath : Directory.GetCurrentDirectory());

        var cloneDir = ResolveCloneDirectory(workspace);

        string agentWorkingDir;
        if (workspace.ExecutionMode == ExecutionMode.ContainerSandbox)
        {
            if (taskId is > 0)
            {
                agentWorkingDir = string.Format(ContainerTaskDirFormat, taskId.Value);
            }
            else
            {
                bool hasClone = hasOverride ||
                                !string.IsNullOrEmpty(workspace.Ext4MirrorPath) ||
                                Directory.Exists(_pathResolver.ResolveCruciblePath(workspace.Id, string.Empty));
                agentWorkingDir = hasClone ? ContainerCloneDir : ContainerWorkspaceRoot;
            }
        }
        else if (workspace.ExecutionMode == ExecutionMode.WslDirect && !hasOverride)
        {
            agentWorkingDir = !string.IsNullOrEmpty(workspace.Ext4MirrorPath)
                ? workspace.Ext4MirrorPath
                : _pathResolver.GetAgentWorkingClonePath(workspace.Id);
        }
        else
        {
            agentWorkingDir = repoPath;
        }

        return new AgentWorkspaceLocations(repoPath, cloneDir, agentWorkingDir);
    }

    private string? ResolveCloneDirectory(Workspace workspace)
    {
        if (!string.IsNullOrWhiteSpace(workspace.Ext4MirrorPath))
        {
            var customClone = _pathResolver.ToHostPath(workspace.Ext4MirrorPath);
            if (Directory.Exists(customClone))
            {
                return customClone;
            }
        }

        if (workspace.Id > 0)
        {
            try
            {
                var canonicalClone = _pathResolver.GetAgentWorkingClonePath(workspace.Id);
                if (!string.IsNullOrWhiteSpace(canonicalClone))
                {
                    var hostClone = _pathResolver.ToHostPath(canonicalClone);
                    if (Directory.Exists(hostClone))
                    {
                        return hostClone;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not resolve working clone directory for workspace #{WorkspaceId}", workspace.Id);
            }
        }

        return null;
    }

    public async Task SyncBacklogToDiskAsync(Workspace workspace, AgentWorkspaceLocations locations, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(locations);

        if (string.IsNullOrWhiteSpace(locations.RepoPath) || !Directory.Exists(locations.RepoPath))
        {
            return;
        }

        try
        {
            await _markdownSyncService.SyncAllTasksToDiskAsync(workspace, locations.RepoPath, locations.RepoPath, cloneDirectory: locations.CloneDir, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to pre-sync existing backlog tasks to clone/docs/tasks for workspace #{WorkspaceId}", workspace.Id);
        }
    }

    public async Task<AgentPromptContext> BuildPromptContextAsync(
        Workspace workspace,
        string repoPath,
        IReadOnlyList<string> permittedTools,
        IWorkspaceSkillsService skillsService,
        CancellationToken cancellationToken)
    {
        var agentsRules = await _agentsMdService.IngestRulesAsync(repoPath, cancellationToken);
        var projectConfig = CodeStackPresets.ResolveConfig(repoPath, null);
        var projectContext = _agentsMdService.FormatProjectContextSection(agentsRules, projectConfig, workspace);

        // The skills catalog (lean ~25 tokens/skill) is only useful when the agent can call load_skill;
        // skill resources/scripts are reached through the canonical read and bash tools.
        IReadOnlyList<SkillDescriptor> skills = Array.Empty<SkillDescriptor>();
        var skillsCatalog = string.Empty;
        if (permittedTools.Contains(CanonicalToolNames.LoadSkill, StringComparer.OrdinalIgnoreCase))
        {
            skills = await skillsService.GetActiveSkillsAsync(cancellationToken);
            skillsCatalog = await skillsService.GetSkillsCatalogPromptAsync(cancellationToken);
        }

        return new AgentPromptContext(projectContext, skillsCatalog, skills.Select(s => s.Name).ToList());
    }

    public Task<string> GetSymbolMapPromptAsync(string workspacePath, string taskText, LocalOptimizationFlags flags, CancellationToken cancellationToken) =>
        _symbolService.GetMatchingSymbolsPromptAsync(workspacePath, taskText, flags, cancellationToken);

    public string? DescribeTopLevelEntries(Workspace workspace, string repoPath)
    {
        try
        {
            var hostDir = _pathResolver.ResolveCruciblePath(workspace.Id, string.Empty);
            if (string.IsNullOrEmpty(hostDir) || !Directory.Exists(hostDir))
            {
                hostDir = repoPath;
            }

            if (string.IsNullOrEmpty(hostDir) || !Directory.Exists(hostDir))
            {
                return null;
            }

            var entries = Directory.GetFileSystemEntries(hostDir)
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrEmpty(n) &&
                            !n.StartsWith(".git") &&
                            !n.Equals("bin", StringComparison.OrdinalIgnoreCase) &&
                            !n.Equals("obj", StringComparison.OrdinalIgnoreCase) &&
                            !n.Equals(".vs", StringComparison.OrdinalIgnoreCase))
                .Take(MaxTopLevelEntries)
                .ToList();
            return entries.Count > 0 ? string.Join(", ", entries) : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not discover top-level entries for workspace {WorkspaceId}", workspace.Id);
            return null;
        }
    }

    public Task<string> BuildBacklogDigestAsync(int workspaceId, int? excludeTaskId, CancellationToken cancellationToken) =>
        _backlogContext.BuildBacklogDigestAsync(workspaceId, excludeTaskId, cancellationToken);

    public async Task<string> BuildFrictionHotspotsDigestAsync(string repoPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repoPath))
        {
            return string.Empty;
        }

        var hotspots = await _transcriptAnalysisService.GetWorkspaceFrictionHotspotsAsync(
            repoPath,
            TranscriptAnalysisDefaults.DefaultRecentLimit,
            cancellationToken);

        return hotspots.FormatPromptDigest();
    }
}

