using Microsoft.Extensions.Logging;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Web.Services;

/// <summary>
/// Helper and presentation service for Ideation Chat question mapping, answers dispatch, and history turns.
/// </summary>
public static class IdeationPresentationHelper
{
    private static readonly HashSet<string> ProposeTasksToolNames = new(StringComparer.OrdinalIgnoreCase)
    {
        CanonicalToolNames.ProposeTasks,
        "proposetasks",
        "propose_task",
        "proposetask",
        "propose_single_task",
        "create_task",
        "create_tasks",
        "add_task",
        "add_tasks",
        "new_task",
        "new_tasks",
        "submit_tasks",
        "propose_backlog",
        "propose_follow_up",
        "follow_up_tasks"
    };

    /// <summary>
    /// Returns true when the completed tool can create, edit, reprioritize, or remove backlog tasks
    /// and therefore requires synchronizing Ideation proposals with workspace tasks.
    /// </summary>
    public static bool RequiresWorkspaceTaskSync(string? toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return false;
        }

        if (BacklogMutationCardParser.IsBacklogMutationTool(toolName))
        {
            return true;
        }

        var clean = toolName.Trim();
        var colonIdx = clean.LastIndexOf(':');
        if (colonIdx >= 0)
        {
            clean = clean[(colonIdx + 1)..];
        }

        var dotIdx = clean.LastIndexOf('.');
        if (dotIdx >= 0)
        {
            clean = clean[(dotIdx + 1)..];
        }

        return ProposeTasksToolNames.Contains(clean);
    }

    /// <summary>
    /// Maps a batch of live IdeationQuestion objects into a list of domain QuestionItem records.
    /// </summary>
    public static IReadOnlyList<QuestionItem> MapToQuestionItems(IEnumerable<IdeationQuestion>? questions)
    {
        if (questions == null) return Array.Empty<QuestionItem>();
        return questions.Select(q => new QuestionItem(q.Prompt, q.Rationale, q.Options)).ToList();
    }

    /// <summary>
    /// Delivers answered texts to each pending IdeationQuestion in sequence.
    /// </summary>
    public static void SubmitAnswersToPendingQuestions(
        IReadOnlyList<IdeationQuestion>? pendingQuestions,
        IReadOnlyDictionary<int, string> answersByIndex)
    {
        if (pendingQuestions == null || pendingQuestions.Count == 0 || answersByIndex == null)
            return;

        for (int i = 0; i < pendingQuestions.Count; i++)
        {
            if (answersByIndex.TryGetValue(i, out var ans) && !string.IsNullOrWhiteSpace(ans))
            {
                pendingQuestions[i].Submit(ans.Trim());
            }
            else
            {
                pendingQuestions[i].Submit("(No answer provided)");
            }
        }
    }

    /// <summary>
    /// Computes the single formatted answer and structured metadata JSON for a question submission.
    /// </summary>
    public static (string FormattedAnswer, string MetadataJson) FormatSubmission(
        IReadOnlyDictionary<int, string> answersByIndex,
        IReadOnlyList<QuestionItem>? questions = null)
    {
        var formatted = StageConversationParser.FormatSingleSubmissionAnswer(answersByIndex);
        var metadata = StageConversationParser.BuildStructuredAnswersJson(answersByIndex, questions);
        return (formatted, metadata);
    }

    /// <summary>
    /// Gets the user-facing submit button label according to the number of questions.
    /// </summary>
    public static string GetSubmitButtonLabel(int questionCount)
    {
        return questionCount > 1
            ? $"Submit All Clarifications ({questionCount}) ➔"
            : "Submit & Continue Ideation ➔";
    }

    /// <summary>
    /// Promotes an existing Draft task to Suggested or creates a new Suggested task from an ideation proposal.
    /// </summary>
    public static Task<ForgeTask> PromoteOrCreateProposalTaskAsync(
        Workspace workspace,
        IdeationProposalViewModel proposal,
        ITaskRepository taskRepository,
        IWorkflowRepository workflowRepository,
        CancellationToken cancellationToken = default) =>
        PromoteOrCreateProposalTaskAsync(
            workspace,
            proposal,
            taskRepository,
            workflowRepository,
            attachmentRepository: null,
            cancellationToken);

    /// <summary>
    /// Promotes an existing Draft task to Suggested or creates a new Suggested task from an ideation proposal,
    /// persisting linked workspace attachments when <paramref name="attachmentRepository"/> is supplied.
    /// </summary>
    public static async Task<ForgeTask> PromoteOrCreateProposalTaskAsync(
        Workspace workspace,
        IdeationProposalViewModel proposal,
        ITaskRepository taskRepository,
        IWorkflowRepository workflowRepository,
        ITaskAttachmentRepository? attachmentRepository,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(taskRepository);
        ArgumentNullException.ThrowIfNull(workflowRepository);

        IReadOnlyList<int> validatedAttachmentIds = Array.Empty<int>();
        var effectiveAttachmentIds = AttachmentCitationParser.UnionAttachmentIds(
            proposal.SelectedAttachmentIds,
            proposal.Description);
        if (attachmentRepository != null && effectiveAttachmentIds.Count > 0)
        {
            validatedAttachmentIds = await AttachmentCitationParser.ValidateAndResolveWorkspaceIdsAsync(
                attachmentRepository,
                workspace.Id,
                proposal.SelectedAttachmentIds,
                proposal.Description,
                cancellationToken);
        }

        int? workflowId = workspace.DefaultWorkflowId;
        if (!workflowId.HasValue || workflowId.Value <= 0)
        {
            var defaultWorkflow = await workflowRepository.GetDefaultWorkflowWithStagesAsync(cancellationToken);
            workflowId = defaultWorkflow?.Id;
        }

        var selectedDeps = proposal.SelectedDependencyIds
            .Where(id => id != proposal.CreatedTaskId)
            .Distinct()
            .ToList();

        var effectiveBaseBranch = string.IsNullOrWhiteSpace(workspace.DefaultBaseBranch)
            ? WorkflowDefaults.DefaultBaseBranch
            : workspace.DefaultBaseBranch;

        ForgeTask savedTask;
        if (proposal.CreatedTaskId.HasValue)
        {
            var existing = await taskRepository.GetByIdAsync(proposal.CreatedTaskId.Value, cancellationToken);
            if (existing != null && existing.WorkspaceId == workspace.Id)
            {
                existing.Title = proposal.Title.Trim();
                existing.Description = proposal.Description.Trim();
                existing.Priority = proposal.Priority;
                existing.Difficulty = proposal.Difficulty;
                existing.Status = TaskOverallStatus.Suggested;
                if ((!existing.WorkflowId.HasValue || existing.WorkflowId.Value <= 0) && workflowId.HasValue)
                {
                    existing.WorkflowId = workflowId;
                }
                if (string.IsNullOrWhiteSpace(existing.BaseBranch) ||
                    string.Equals(existing.BaseBranch, WorkflowDefaults.DefaultBaseBranch, StringComparison.OrdinalIgnoreCase))
                {
                    existing.BaseBranch = effectiveBaseBranch;
                }
                existing.UpdatedAt = DateTime.UtcNow;

                await taskRepository.UpdateWithHistoryAsync(
                    existing,
                    selectedDeps,
                    AuditActors.User,
                    "Promoted draft proposal to Suggested",
                    cancellationToken);

                savedTask = existing;
                proposal.SelectedDependencyIds = selectedDeps;
                await PersistProposalAttachmentLinksAsync(savedTask, proposal, attachmentRepository, validatedAttachmentIds, cancellationToken);
                return savedTask;
            }
        }

        var newTask = new ForgeTask
        {
            WorkspaceId = workspace.Id,
            WorkflowId = workflowId,
            Title = proposal.Title.Trim(),
            Description = proposal.Description.Trim(),
            Status = TaskOverallStatus.Suggested,
            Priority = proposal.Priority,
            Difficulty = proposal.Difficulty,
            MergeMode = MergeMode.ManualReviewRequired,
            BaseBranch = effectiveBaseBranch,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        savedTask = await taskRepository.CreateWithBaselineAsync(
            newTask,
            selectedDeps,
            AuditActors.IdeationAgent,
            "Created task proposal from Ideation",
            cancellationToken);

        proposal.SelectedDependencyIds = selectedDeps;
        await PersistProposalAttachmentLinksAsync(savedTask, proposal, attachmentRepository, validatedAttachmentIds, cancellationToken);
        return savedTask;
    }

    private static async Task PersistProposalAttachmentLinksAsync(
        ForgeTask task,
        IdeationProposalViewModel proposal,
        ITaskAttachmentRepository? attachmentRepository,
        IReadOnlyList<int> validatedAttachmentIds,
        CancellationToken cancellationToken)
    {
        if (attachmentRepository == null)
        {
            return;
        }

        if (validatedAttachmentIds.Count > 0 || (task.AttachmentLinks?.Count ?? 0) > 0)
        {
            await attachmentRepository.SetTaskAttachmentLinksAsync(task.Id, validatedAttachmentIds, cancellationToken);
            var linked = validatedAttachmentIds.Count > 0
                ? (await attachmentRepository.GetByTaskIdAsync(task.Id, cancellationToken) ?? Array.Empty<TaskAttachment>())
                : Array.Empty<TaskAttachment>();
            if (linked.Count == 0 && validatedAttachmentIds.Count > 0)
            {
                linked = await attachmentRepository.GetByIdsAsync(validatedAttachmentIds, cancellationToken)
                    ?? Array.Empty<TaskAttachment>();
            }
            task.AttachmentLinks = linked
                .Select(a => new TaskAttachmentLink
                {
                    TaskId = task.Id,
                    TaskAttachmentId = a.Id,
                    TaskAttachment = a
                })
                .ToList();
            proposal.SelectedAttachmentIds = validatedAttachmentIds.ToList();
        }
    }

    /// <summary>
    /// Deletes an unpromoted Draft task from the repository and removes its markdown file on dismissal.
    /// </summary>
    public static async Task PurgeDraftProposalAsync(
        Workspace? workspace,
        IdeationProposalViewModel proposal,
        ITaskRepository taskRepository,
        ITaskMarkdownSyncService markdownSync,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(taskRepository);
        ArgumentNullException.ThrowIfNull(markdownSync);
        ArgumentNullException.ThrowIfNull(logger);

        if (!proposal.CreatedTaskId.HasValue || workspace == null)
        {
            return;
        }

        try
        {
            var task = await taskRepository.GetByIdAsync(proposal.CreatedTaskId.Value, cancellationToken);
            if (task != null && task.WorkspaceId == workspace.Id && task.Status == TaskOverallStatus.Draft)
            {
                await taskRepository.DeleteAsync(task.Id, cancellationToken);
                if (!string.IsNullOrWhiteSpace(workspace.RepositoryPath) && Directory.Exists(workspace.RepositoryPath))
                {
                    await markdownSync.ArchiveOrCleanupTaskFileAsync(task, workspace.RepositoryPath, archive: false, cancellationToken);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to purge draft task #{TaskId} on dismissal", proposal.CreatedTaskId.Value);
        }
    }
}
