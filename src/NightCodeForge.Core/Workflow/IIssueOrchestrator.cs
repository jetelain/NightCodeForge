using Hangfire;

namespace NightCodeForge.Core.Workflow;

/// <summary>
/// Central orchestrator coordinating issue DAG readiness, workspace locking, stage execution,
/// human-in-the-loop interruption/resumption, and merge cascade unblocking.
/// </summary>
public interface IIssueOrchestrator
{
    /// <summary>
    /// Executes the current workflow stage for the specified issue with workspace locking and git checkout.
    /// LLM inference jobs are configured with zero automatic retries to prevent runaway edits.
    /// </summary>
    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    Task ProcessIssueAsync(int issueId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resumes an issue blocked on human clarification, persisting the answer and enqueuing to llm-urgent.
    /// </summary>
    Task SubmitHumanAnswerAsync(int questionId, string answer, CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-routes an issue in review back to the implementation stage with reviewer notes and enqueues to llm-urgent.
    /// </summary>
    Task RequestChangesAsync(int issueId, string revisionNotes, CancellationToken cancellationToken = default);

    /// <summary>
    /// Completes an issue by squash-merging its branch into base branch and cascades unblocking to dependent DAG issues.
    /// </summary>
    Task CompleteIssueAndCascadeAsync(int issueId, string finalCommitMessage, CancellationToken cancellationToken = default);
}
