using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Web.Services;

public class TriageSubmissionModel
{
    public const string DraftIdQueryParameter = "draftId";
    private const string TriageRoute = "triage";

    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string BaseBranch { get; set; } = string.Empty;
    public string? ExternalUrl { get; set; }
    public string? StackTrace { get; set; }

    public bool IsValid =>
        !string.IsNullOrWhiteSpace(Title) &&
        !string.IsNullOrWhiteSpace(Description);

    /// <summary>
    /// Builds the workspace-scoped URL for opening the Triage portal pre-populated with a companion draft.
    /// </summary>
    public static string BuildCompanionDraftUrl(int workspaceId, string draftId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(draftId);
        return WorkspaceUrlHelper.BuildPath(workspaceId, $"{TriageRoute}?{DraftIdQueryParameter}={Uri.EscapeDataString(draftId.Trim())}");
    }

    /// <summary>
    /// Applies a staged companion triage draft matching <paramref name="draftId"/> and <paramref name="activeWorkspaceId"/>
    /// once per distinct draft identifier so subsequent re-renders do not overwrite maintainer edits.
    /// </summary>
    public bool TryApplyCompanionDraft(
        ICompanionTriageDraftStore draftStore,
        int? activeWorkspaceId,
        string? draftId,
        ref string? lastAppliedDraftId) =>
        TryApplyCompanionDraft(draftStore, activeWorkspaceId, draftId, ref lastAppliedDraftId, out _);

    /// <summary>
    /// Applies a staged companion triage draft matching <paramref name="draftId"/> and <paramref name="activeWorkspaceId"/>
    /// once per distinct draft identifier, exposing the resolved <paramref name="appliedDraft"/>.
    /// </summary>
    public bool TryApplyCompanionDraft(
        ICompanionTriageDraftStore draftStore,
        int? activeWorkspaceId,
        string? draftId,
        ref string? lastAppliedDraftId,
        out CompanionTriageDraft? appliedDraft)
    {
        ArgumentNullException.ThrowIfNull(draftStore);

        appliedDraft = null;
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

        Title = draft.Title;
        Description = draft.ReportMarkdown;
        BaseBranch = draft.BaseBranch;
        ExternalUrl = null;
        StackTrace = null;
        lastAppliedDraftId = normalizedDraftId;
        appliedDraft = draft;
        return true;
    }

    public string BuildFullDescription()
    {
        var fullDescription = Description?.Trim() ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(StackTrace))
        {
            fullDescription += $"\n\n### Stack Trace / Error Logs\n```text\n{StackTrace.Trim()}\n```";
        }

        if (!string.IsNullOrWhiteSpace(ExternalUrl))
        {
            fullDescription += $"\n\n*Origin:* [{ExternalUrl.Trim()}]({ExternalUrl.Trim()})";
        }

        return fullDescription;
    }
}

public record TriageExecutionState(
    bool IsRunning,
    string? CurrentActivity,
    IReadOnlyList<string> SecurityBadges,
    IReadOnlyList<ForgeTask> GeneratedTasks,
    string? ErrorMessage);

/// <summary>
/// Pure presentation helper for formatting untrusted intake triage status summaries.
/// </summary>
public static class TriagePresentationHelper
{
    public static string FormatSessionCompleteMessage(TriageSessionResult sessionResult)
    {
        ArgumentNullException.ThrowIfNull(sessionResult);

        if (sessionResult.UpdatedTaskIds.Count == 0 && sessionResult.RemovedTaskIds.Count == 0)
        {
            return $"Triage agent formulated {sessionResult.Tasks.Count} suggested task(s).";
        }

        var parts = new List<string>();
        if (sessionResult.CreatedTaskIds.Count > 0)
        {
            parts.Add($"{sessionResult.CreatedTaskIds.Count} created");
        }
        if (sessionResult.UpdatedTaskIds.Count > 0)
        {
            parts.Add($"{sessionResult.UpdatedTaskIds.Count} updated");
        }
        if (sessionResult.RemovedTaskIds.Count > 0)
        {
            parts.Add($"{sessionResult.RemovedTaskIds.Count} removed");
        }

        return parts.Count > 0
            ? $"Triage complete ({string.Join(", ", parts)})."
            : $"Triage agent formulated {sessionResult.Tasks.Count} suggested task(s).";
    }
}
