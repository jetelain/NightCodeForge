namespace NightCodeForge.Core.Domain;

/// <summary>
/// Named constants for task mutation audit actors and helpers to derive actors from stage IDs.
/// </summary>
public static class AuditActors
{
    public const string System = "system";
    public const string User = "user";
    public const string Orchestrator = "orchestrator";
    public const string TaskReviewAgent = "agent:task-review";
    public const string FileWatcher = "file-watcher";

    /// <summary>
    /// Derives an audit actor identifier from a workflow stage ID.
    /// </summary>
    public static string GetActorForStage(string? stageId)
    {
        if (string.IsNullOrWhiteSpace(stageId))
        {
            return Orchestrator;
        }

        return $"agent:{stageId.Trim()}";
    }
}
