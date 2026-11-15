using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Services;

namespace NightCodeForge.Web.Services;

/// <summary>
/// Presentation rules for the "Refine in Ideation" and companion ideation pre-fill workflows.
/// </summary>
public static class IdeationRefinementHelper
{
    /// <summary>Query-string parameter carrying the task to refine on the Ideation page.</summary>
    public const string TaskIdQueryParameter = "taskId";

    /// <summary>Query-string parameter carrying a staged companion draft on the Ideation page.</summary>
    public const string DraftIdQueryParameter = "draftId";

    private const string IdeationRoute = "ideation";

    /// <summary>Only unstarted tasks (Draft/Suggested/Open without any execution run) can be refined from ideation.</summary>
    public static bool CanRefine(ForgeTask? task) =>
        task != null
        && task.Status is TaskOverallStatus.Draft or TaskOverallStatus.Suggested or TaskOverallStatus.Open
        && !task.HasExecutionRun;

    public static string BuildRefineUrl(ForgeTask task) =>
        WorkspaceUrlHelper.BuildPath(task.WorkspaceId, $"{IdeationRoute}?{TaskIdQueryParameter}={task.Id}");

    public static string BuildCompanionDraftUrl(int workspaceId, string draftId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(draftId);
        return WorkspaceUrlHelper.BuildPath(workspaceId, $"{IdeationRoute}?{DraftIdQueryParameter}={Uri.EscapeDataString(draftId.Trim())}");
    }

    /// <summary>
    /// Resolves a staged companion draft matching <paramref name="draftId"/> and <paramref name="activeWorkspaceId"/>
    /// once per distinct draft identifier.
    /// </summary>
    public static bool TryResolveCompanionDraft(
        ICompanionTriageDraftStore draftStore,
        int? activeWorkspaceId,
        string? draftId,
        ref string? lastAppliedDraftId,
        out CompanionTriageDraft? resolvedDraft)
    {
        ArgumentNullException.ThrowIfNull(draftStore);

        resolvedDraft = null;
        if (string.IsNullOrWhiteSpace(draftId) || !activeWorkspaceId.HasValue)
        {
            return false;
        }

        var normalizedDraftId = draftId.Trim();
        if (string.Equals(lastAppliedDraftId, normalizedDraftId, StringComparison.Ordinal))
        {
            return false;
        }

        var draft = draftStore.GetDraft(normalizedDraftId);
        if (draft is null || draft.WorkspaceId != activeWorkspaceId.Value)
        {
            return false;
        }

        lastAppliedDraftId = normalizedDraftId;
        resolvedDraft = draft;
        return true;
    }

    public static string BuildCompanionSeedPrompt(CompanionTriageDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return $"""
            Please analyze the following exploratory testing report for '{draft.Title}' (base branch '{draft.BaseBranch}'), inspect the relevant codebase files, ask any clarifying questions if needed, and propose structured backlog tasks:

            {draft.ReportMarkdown.Trim()}
            """;
    }

    /// <summary>Prefix shared by every conversation title dedicated to a task; used to resume an existing session.</summary>
    public static string BuildConversationTitlePrefix(int taskId) => $"Refining Task #{taskId}:";

    public static string BuildConversationTitle(ForgeTask task) =>
        $"{BuildConversationTitlePrefix(task.Id)} {task.Title}";

    public static string BuildSeedPrompt(ForgeTask task) =>
        $"Let's refine Task #{task.Id} ('{task.Title}'). Please inspect its current specification with get_task({task.Id}), " +
        "identify any open questions or missing details, and let's resolve them before updating the task.";
}

