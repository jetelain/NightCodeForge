using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;

namespace NightCodeForge.Agents.Tools;

/// <summary>
/// Groups the backlog-awareness collaborators shared by the Ideation and Crucible agents
/// (backlog digest for prompts, task specification validation for tools, and per-invocation scope factory).
/// </summary>
public interface IBacklogPlanningContext
{
    /// <summary>Validator attached to <see cref="WorkspaceFileToolModule.TaskMarkdownValidator"/>.</summary>
    ITaskMarkdownValidator TaskMarkdownValidator { get; }

    /// <summary>Host-level backlog mutation tools attached to the ideation agent's <see cref="BacklogManagementToolModule"/>.</summary>
    IBacklogMutationToolService BacklogMutationService { get; }

    /// <summary>Logger handed to the <see cref="CanonicalTools"/> instances created by agents.</summary>
    ILogger<CanonicalTools> CanonicalToolsLogger { get; }

    /// <summary>
    /// Optional scope factory used by <see cref="WorkspaceFileToolModule"/> and <see cref="BacklogManagementToolModule"/>
    /// to resolve scoped database collaborators per tool invocation in warm sessions.
    /// </summary>
    IServiceScopeFactory? ScopeFactory => null;

    /// <summary>Builds the <c>&lt;existing_workspace_tasks&gt;</c> digest (empty when the backlog has no active task).</summary>
    Task<string> BuildBacklogDigestAsync(int workspaceId, int? excludeTaskId, CancellationToken cancellationToken);

    /// <summary>Builds the <c>&lt;workspace_attachments&gt;</c> digest (empty when no workspace attachments exist).</summary>
    Task<string> BuildWorkspaceAttachmentsDigestAsync(int workspaceId, CancellationToken cancellationToken = default);

    /// <summary>Builds the <c>&lt;task_attachments&gt;</c> digest for one task (empty when no attachments are linked).</summary>
    Task<string> BuildTaskAttachmentsDigestAsync(int workspaceId, int taskId, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class BacklogPlanningContext : IBacklogPlanningContext
{
    public BacklogPlanningContext(
        IWorkspaceBacklogSummaryService summaryService,
        ITaskMarkdownValidator taskMarkdownValidator,
        IBacklogMutationToolService backlogMutationService,
        ILogger<CanonicalTools> canonicalToolsLogger,
        IServiceScopeFactory scopeFactory)
    {
        SummaryService = summaryService ?? throw new ArgumentNullException(nameof(summaryService));
        TaskMarkdownValidator = taskMarkdownValidator ?? throw new ArgumentNullException(nameof(taskMarkdownValidator));
        BacklogMutationService = backlogMutationService ?? throw new ArgumentNullException(nameof(backlogMutationService));
        CanonicalToolsLogger = canonicalToolsLogger ?? throw new ArgumentNullException(nameof(canonicalToolsLogger));
        ScopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    }

    public IWorkspaceBacklogSummaryService SummaryService { get; }

    public ITaskMarkdownValidator TaskMarkdownValidator { get; }

    public IBacklogMutationToolService BacklogMutationService { get; }

    public ILogger<CanonicalTools> CanonicalToolsLogger { get; }

    public IServiceScopeFactory ScopeFactory { get; }

    public async Task<string> BuildBacklogDigestAsync(int workspaceId, int? excludeTaskId, CancellationToken cancellationToken)
    {
        await using var scope = ScopeFactory.CreateAsyncScope();
        var scopedSummary = scope.ServiceProvider.GetRequiredService<IWorkspaceBacklogSummaryService>();
        return await scopedSummary.BuildDigestAsync(workspaceId, excludeTaskId, cancellationToken);
    }

    public async Task<string> BuildWorkspaceAttachmentsDigestAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        await using var scope = ScopeFactory.CreateAsyncScope();
        var attachmentRepository = scope.ServiceProvider.GetRequiredService<ITaskAttachmentRepository>();
        var attachments = await attachmentRepository.GetByWorkspaceIdAsync(workspaceId, cancellationToken);
        return AttachmentDigestFormatter.FormatWorkspaceAttachmentsDigest(attachments);
    }

    public async Task<string> BuildTaskAttachmentsDigestAsync(int workspaceId, int taskId, CancellationToken cancellationToken = default)
    {
        if (taskId <= 0)
        {
            return string.Empty;
        }

        await using var scope = ScopeFactory.CreateAsyncScope();
        var attachmentRepository = scope.ServiceProvider.GetRequiredService<ITaskAttachmentRepository>();
        var attachments = await attachmentRepository.GetByTaskIdAsync(taskId, cancellationToken);
        var workspaceAttachments = workspaceId > 0
            ? attachments.Where(a => a.WorkspaceId == workspaceId)
            : attachments;
        return AttachmentDigestFormatter.FormatTaskAttachmentsDigest(workspaceAttachments);
    }
}
