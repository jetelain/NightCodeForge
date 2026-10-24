namespace NightCodeForge.Core.Domain;

/// <summary>
/// Status of an agent execution stage run.
/// </summary>
public enum AgentRunStatus
{
    Completed = 0,
    WaitingForHuman = 1,
    Failed = 2,
    Cancelled = 3,
    Interrupted = 4
}

/// <summary>
/// Execution metrics recorded during an agent stage run.
/// </summary>
public record AgentRunMetrics(
    long DurationMs = 0,
    long PromptTokens = 0,
    long CompletionTokens = 0,
    long TotalTokens = 0);

/// <summary>
/// The result of an agent stage execution.
/// </summary>
public record AgentRunResult(
    AgentRunStatus Status,
    AgentRunMetrics Metrics,
    string? CommitHash = null,
    string? UpdatedState = null,
    IReadOnlyList<Question>? PendingQuestions = null,
    string? Summary = null,
    string? Error = null,
    IReadOnlyDictionary<string, string>? DiagnosticArtifacts = null,
    string? TargetRouting = null,
    string? ReviewerFeedback = null,
    IReadOnlyList<TaskProposal>? Proposals = null)
{
    public static AgentRunResult Completed(
        AgentRunMetrics metrics,
        string? commitHash = null,
        string? updatedState = null,
        string? summary = null,
        IReadOnlyDictionary<string, string>? artifacts = null,
        string? targetRouting = null,
        string? reviewerFeedback = null,
        IReadOnlyList<TaskProposal>? proposals = null) =>
        new(AgentRunStatus.Completed, metrics, commitHash, updatedState, null, summary, null, artifacts, targetRouting, reviewerFeedback, proposals);

    public static AgentRunResult WaitingForHuman(
        IReadOnlyList<Question> pendingQuestions,
        AgentRunMetrics metrics,
        string? updatedState = null,
        string? summary = null)
    {
        if (pendingQuestions == null || pendingQuestions.Count == 0)
        {
            throw new ArgumentException("Pending questions collection cannot be null or empty.", nameof(pendingQuestions));
        }

        return new(AgentRunStatus.WaitingForHuman, metrics, null, updatedState, pendingQuestions, summary, null, null);
    }

    public static AgentRunResult Failed(
        string error,
        AgentRunMetrics metrics,
        string? updatedState = null,
        string? summary = null,
        IReadOnlyDictionary<string, string>? artifacts = null) =>
        new(AgentRunStatus.Failed, metrics, null, updatedState, null, summary, error, artifacts);

    public static AgentRunResult Cancelled(
        string error,
        AgentRunMetrics metrics,
        string? updatedState = null,
        string? summary = null,
        IReadOnlyDictionary<string, string>? artifacts = null) =>
        new(AgentRunStatus.Cancelled, metrics, null, updatedState, null, summary, error, artifacts);

    public static AgentRunResult Interrupted(
        string summary,
        AgentRunMetrics metrics,
        string? updatedState = null,
        IReadOnlyDictionary<string, string>? artifacts = null) =>
        new(AgentRunStatus.Interrupted, metrics, null, updatedState, null, summary, null, artifacts);
}
