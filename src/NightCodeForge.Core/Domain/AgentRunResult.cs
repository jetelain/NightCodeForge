namespace NightCodeForge.Core.Domain;

/// <summary>
/// Status of an agent execution stage run.
/// </summary>
public enum AgentRunStatus
{
    Completed = 0,
    WaitingForHuman = 1,
    Failed = 2
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
    string? PendingQuestion = null,
    string? Summary = null,
    string? Error = null,
    IReadOnlyDictionary<string, string>? DiagnosticArtifacts = null)
{
    public static AgentRunResult Completed(
        AgentRunMetrics metrics,
        string? commitHash = null,
        string? updatedState = null,
        string? summary = null,
        IReadOnlyDictionary<string, string>? artifacts = null) =>
        new(AgentRunStatus.Completed, metrics, commitHash, updatedState, null, summary, null, artifacts);

    public static AgentRunResult WaitingForHuman(
        string pendingQuestion,
        AgentRunMetrics metrics,
        string? updatedState = null,
        string? summary = null) =>
        new(AgentRunStatus.WaitingForHuman, metrics, null, updatedState, pendingQuestion, summary, null, null);

    public static AgentRunResult Failed(
        string error,
        AgentRunMetrics metrics,
        string? updatedState = null,
        string? summary = null,
        IReadOnlyDictionary<string, string>? artifacts = null) =>
        new(AgentRunStatus.Failed, metrics, null, updatedState, null, summary, error, artifacts);
}
