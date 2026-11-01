using NightCodeForge.Core.Domain;

namespace NightCodeForge.Web.Services;

/// <summary>
/// Presentation rules for the "Refine in Ideation" workflow: eligibility, navigation URL, conversation title and seed prompt.
/// </summary>
public static class IdeationRefinementHelper
{
    /// <summary>Query-string parameter carrying the task to refine on the Ideation page.</summary>
    public const string TaskIdQueryParameter = "taskId";

    private const string IdeationRoute = "ideation";

    /// <summary>Only unstarted tasks (Suggested/Open without any execution run) can be refined from ideation.</summary>
    public static bool CanRefine(ForgeTask? task) =>
        task != null
        && task.Status is TaskOverallStatus.Suggested or TaskOverallStatus.Open
        && !task.HasExecutionRun;

    public static string BuildRefineUrl(ForgeTask task) =>
        WorkspaceUrlHelper.BuildPath(task.WorkspaceId, $"{IdeationRoute}?{TaskIdQueryParameter}={task.Id}");

    /// <summary>Prefix shared by every conversation title dedicated to a task; used to resume an existing session.</summary>
    public static string BuildConversationTitlePrefix(int taskId) => $"Refining Task #{taskId}:";

    public static string BuildConversationTitle(ForgeTask task) =>
        $"{BuildConversationTitlePrefix(task.Id)} {task.Title}";

    public static string BuildSeedPrompt(ForgeTask task) =>
        $"Let's refine Task #{task.Id} ('{task.Title}'). Please inspect its current specification with get_task({task.Id}), " +
        "identify any open questions or missing details, and let's resolve them before updating the task.";
}

