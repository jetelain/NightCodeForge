using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Web.Services;

/// <summary>
/// Pure presentation helper governing how triage execution sessions versus normal backlog tasks
/// are displayed across inspection views and status badges.
/// </summary>
public static class TaskInspectorPresentationHelper
{
    public static bool IsTriageSession(ForgeTask? task) =>
        task != null &&
        (task.Status == TaskOverallStatus.Triage ||
         string.Equals(task.CurrentStageId, StandardStageIds.Triage, StringComparison.OrdinalIgnoreCase));

    public static bool ShouldLoadWorkflowStages(ForgeTask? task) =>
        task != null && !IsTriageSession(task);

    public static bool ShouldShowPipelineStepper(ForgeTask? task) =>
        task != null && !IsTriageSession(task);

    public static bool ShouldShowPipelineActionButtons(ForgeTask? task) =>
        task != null && !IsTriageSession(task);

    public static bool ShouldShowGitDiffTab(ForgeTask? task) =>
        task != null && !IsTriageSession(task);

    public static bool ShouldShowBranchAndMergeMetadata(ForgeTask? task) =>
        task != null && !IsTriageSession(task);

    public static string GetDescriptionHeading(ForgeTask? task) =>
        IsTriageSession(task) ? "Full Initial Intake Report" : "Full Task Description";

    public static string FormatStatusBadgeLabel(ForgeTask task) =>
        task.Status == TaskOverallStatus.Triage ? "Untrusted Intake Triage" : task.Status.ToString();

    public static string FormatTaskOrigin(TaskOrigin origin) => origin switch
    {
        TaskOrigin.UntrustedIntake => "Untrusted Intake",
        TaskOrigin.GitHubIssue => "GitHub Issue",
        TaskOrigin.Ideation => "Ideation",
        TaskOrigin.Import => "Import",
        _ => "Manual"
    };
}
