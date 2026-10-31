using NightCodeForge.Core.Domain;

namespace NightCodeForge.Web.Services;

/// <summary>
/// Selects the conversation to display in the Task Execution Inspector when task data is loaded or refreshed.
/// </summary>
public static class TaskConversationSelector
{
    /// <summary>
    /// Resolves the target conversation to select after a task reload.
    /// Does not keep a <see cref="StageExecutionStatus.Superseded"/> conversation pinned across refreshes
    /// when a newer or non-superseded conversation becomes available.
    /// </summary>
    public static Conversation? SelectActiveConversation(
        IReadOnlyList<Conversation>? conversations,
        int? previousSelectedConversationId)
    {
        if (conversations == null || conversations.Count == 0)
        {
            return null;
        }

        var preservedPrevious = previousSelectedConversationId.HasValue
            ? conversations.FirstOrDefault(c =>
                c.Id == previousSelectedConversationId.Value &&
                c.Status != StageExecutionStatus.Superseded)
            : null;

        return preservedPrevious
               ?? conversations.LastOrDefault(c =>
                   c.Status == StageExecutionStatus.Running ||
                   c.Status == StageExecutionStatus.Interrupted ||
                   c.Status == StageExecutionStatus.WaitingForHuman)
               ?? conversations.LastOrDefault(c => c.Status != StageExecutionStatus.Superseded)
               ?? conversations.LastOrDefault();
    }
}

