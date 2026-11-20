using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;

namespace NightCodeForge.Web.Services;

public sealed record PromoteProposalResult(ForgeTask CreatedTask, string? MarkdownSyncWarning);

/// <summary>
/// Web coordination collaborator encapsulating backlog proposal promotion, draft dismissal,
/// task querying, and targeted markdown disk sync outside Razor components.
/// </summary>
public interface IIdeationProposalService
{
    Task<IReadOnlyList<ForgeTask>> GetWorkspaceTasksAsync(int workspaceId, CancellationToken cancellationToken = default);

    Task<PromoteProposalResult> PromoteOrCreateProposalAsync(
        Workspace workspace,
        IdeationProposalViewModel proposal,
        CancellationToken cancellationToken = default);

    Task PurgeDraftProposalAsync(
        Workspace? workspace,
        IdeationProposalViewModel proposal,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class IdeationProposalService : IIdeationProposalService
{
    private readonly ITaskRepository _taskRepository;
    private readonly IWorkflowRepository _workflowRepository;
    private readonly ITaskAttachmentRepository _attachmentRepository;
    private readonly ITaskMarkdownSyncService _markdownSyncService;
    private readonly IIdeationConversationCoordinator _coordinator;
    private readonly ILogger<IdeationProposalService> _logger;

    public IdeationProposalService(
        ITaskRepository taskRepository,
        IWorkflowRepository workflowRepository,
        ITaskAttachmentRepository attachmentRepository,
        ITaskMarkdownSyncService markdownSyncService,
        IIdeationConversationCoordinator coordinator,
        ILogger<IdeationProposalService> logger)
    {
        _taskRepository = taskRepository ?? throw new ArgumentNullException(nameof(taskRepository));
        _workflowRepository = workflowRepository ?? throw new ArgumentNullException(nameof(workflowRepository));
        _attachmentRepository = attachmentRepository ?? throw new ArgumentNullException(nameof(attachmentRepository));
        _markdownSyncService = markdownSyncService ?? throw new ArgumentNullException(nameof(markdownSyncService));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<IReadOnlyList<ForgeTask>> GetWorkspaceTasksAsync(int workspaceId, CancellationToken cancellationToken = default) =>
        _taskRepository.GetByWorkspaceIdAsync(workspaceId, cancellationToken);

    public async Task<PromoteProposalResult> PromoteOrCreateProposalAsync(
        Workspace workspace,
        IdeationProposalViewModel proposal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(proposal);

        var created = await IdeationPresentationHelper.PromoteOrCreateProposalTaskAsync(
            workspace,
            proposal,
            _taskRepository,
            _workflowRepository,
            _attachmentRepository,
            cancellationToken).ConfigureAwait(false);

        string? syncWarning = null;
        if (!string.IsNullOrWhiteSpace(workspace.RepositoryPath) && Directory.Exists(workspace.RepositoryPath))
        {
            try
            {
                await _markdownSyncService.SyncTaskToDiskAsync(
                    created,
                    workspace.RepositoryPath,
                    proposal.SelectedDependencyIds,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception syncEx) when (syncEx is not OperationCanceledException)
            {
                _logger.LogWarning(syncEx, "Failed syncing promoted proposal task #{TaskId} markdown to disk.", created.Id);
                syncWarning = $"Created task #{created.Id} in DB but failed writing markdown: {syncEx.Message}";
            }
        }

        proposal.IsAdded = true;
        proposal.CreatedTaskId = created.Id;
        _coordinator.MarkWorkspaceContextDirty(workspace.Id);

        return new PromoteProposalResult(created, syncWarning);
    }

    public async Task PurgeDraftProposalAsync(
        Workspace? workspace,
        IdeationProposalViewModel proposal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);

        await IdeationPresentationHelper.PurgeDraftProposalAsync(
            workspace,
            proposal,
            _taskRepository,
            _markdownSyncService,
            _logger,
            cancellationToken).ConfigureAwait(false);

        if (workspace != null && proposal.CreatedTaskId.HasValue)
        {
            _coordinator.MarkWorkspaceContextDirty(workspace.Id);
        }
    }
}
