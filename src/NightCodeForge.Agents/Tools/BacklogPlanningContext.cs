using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Services;

namespace NightCodeForge.Agents.Tools;

/// <summary>
/// Groups the backlog-awareness collaborators shared by the Ideation and Crucible agents
/// (backlog digest for prompts, task specification validation for tools).
/// </summary>
public interface IBacklogPlanningContext
{
    /// <summary>Validator attached to <see cref="CanonicalTools.TaskMarkdownValidator"/>.</summary>
    ITaskMarkdownValidator TaskMarkdownValidator { get; }

    /// <summary>Host-level backlog mutation tools attached to the ideation agent's <see cref="CanonicalTools"/>.</summary>
    IBacklogMutationToolService BacklogMutationService { get; }

    /// <summary>Logger handed to the <see cref="CanonicalTools"/> instances created by agents.</summary>
    ILogger<CanonicalTools> CanonicalToolsLogger { get; }

    /// <summary>Builds the <c>&lt;existing_workspace_tasks&gt;</c> digest (empty when the backlog has no active task).</summary>
    Task<string> BuildBacklogDigestAsync(int workspaceId, int? excludeTaskId, CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed class BacklogPlanningContext : IBacklogPlanningContext
{
    private readonly IWorkspaceBacklogSummaryService _summaryService;

    public BacklogPlanningContext(
        IWorkspaceBacklogSummaryService summaryService,
        ITaskMarkdownValidator taskMarkdownValidator,
        IBacklogMutationToolService backlogMutationService,
        ILogger<CanonicalTools> canonicalToolsLogger)
    {
        CanonicalToolsLogger = canonicalToolsLogger ?? throw new ArgumentNullException(nameof(canonicalToolsLogger));
        _summaryService = summaryService ?? throw new ArgumentNullException(nameof(summaryService));
        TaskMarkdownValidator = taskMarkdownValidator ?? throw new ArgumentNullException(nameof(taskMarkdownValidator));
        BacklogMutationService = backlogMutationService ?? throw new ArgumentNullException(nameof(backlogMutationService));
    }

    public ITaskMarkdownValidator TaskMarkdownValidator { get; }

    public IBacklogMutationToolService BacklogMutationService { get; }

    public ILogger<CanonicalTools> CanonicalToolsLogger { get; }

    public Task<string> BuildBacklogDigestAsync(int workspaceId, int? excludeTaskId, CancellationToken cancellationToken) =>
        _summaryService.BuildDigestAsync(workspaceId, excludeTaskId, cancellationToken);
}

