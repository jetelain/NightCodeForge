namespace NightCodeForge.Core.Domain;

/// <summary>
/// Named constants for task mutation audit actors and helpers to derive actors from stage IDs.
/// </summary>
public static class AuditActors
{
    public const string System = "system";
    public const string User = "User";
    public const string TaskOrchestrator = "TaskOrchestrator";
    public const string IdeationAgent = "IdeationAgent";
    public const string Orchestrator = "TaskOrchestrator";
    public const string TaskReviewAgent = "agent:task-review";
    public const string FileWatcher = "file-watcher";

    /// <summary>
    /// Derives an audit actor identifier from a workflow stage ID.
    /// </summary>
    public static string GetActorForStage(string? stageId)
    {
        if (string.IsNullOrWhiteSpace(stageId))
        {
            return TaskOrchestrator;
        }

        return $"agent:{stageId.Trim()}";
    }
}
