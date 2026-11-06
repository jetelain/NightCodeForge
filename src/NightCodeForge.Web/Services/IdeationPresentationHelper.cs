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
    public static async Task<ForgeTask> PromoteOrCreateProposalTaskAsync(
        Workspace workspace,
        IdeationProposalViewModel proposal,
        ITaskRepository taskRepository,
        IWorkflowRepository workflowRepository,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(taskRepository);
        ArgumentNullException.ThrowIfNull(workflowRepository);

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

        if (proposal.CreatedTaskId.HasValue)
        {
            var existing = await taskRepository.GetByIdAsync(proposal.CreatedTaskId.Value, cancellationToken);
            if (existing != null && existing.WorkspaceId == workspace.Id)
            {
                existing.Title = proposal.Title.Trim();
                existing.Description = proposal.Description.Trim();
                existing.Priority = proposal.Priority;
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

                return existing;
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
            MergeMode = MergeMode.ManualReviewRequired,
            BaseBranch = effectiveBaseBranch,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        return await taskRepository.CreateWithBaselineAsync(
            newTask,
            selectedDeps,
            AuditActors.IdeationAgent,
            "Created task proposal from Ideation",
            cancellationToken);
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
