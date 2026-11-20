namespace NightCodeForge.Web.Services;

/// <summary>
/// Domain enum identifying the operational notification subjects.
/// </summary>
public enum NotificationSubject
{
    QuestionInbox,
    ReviewRoom,
    TaskMerged,
    AgentMemory
}

/// <summary>
/// Device-local notification configuration stored in browser localStorage.
/// </summary>
public class NotificationPreferences
{
    public bool MasterEnabled { get; set; } = true;
    public bool NotifyOnQuestionInbox { get; set; } = true;
    public bool NotifyOnReviewRoom { get; set; } = true;
    public bool NotifyOnTaskMerged { get; set; } = true;
    public bool NotifyOnAgentMemory { get; set; } = false;

    /// <summary>
    /// Determines whether a notification for the specified subject should be dispatched.
    /// </summary>
    public bool ShouldNotify(NotificationSubject subject)
    {
        if (!MasterEnabled)
        {
            return false;
        }

        return subject switch
        {
            NotificationSubject.QuestionInbox => NotifyOnQuestionInbox,
            NotificationSubject.ReviewRoom => NotifyOnReviewRoom,
            NotificationSubject.TaskMerged => NotifyOnTaskMerged,
            NotificationSubject.AgentMemory => NotifyOnAgentMemory,
            _ => false
        };
    }
}
