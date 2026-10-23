using Hangfire;

namespace NightCodeForge.Core.Workflow;

/// <summary>
/// Central orchestrator coordinating task DAG readiness, workspace locking, stage execution,
/// human-in-the-loop interruption/resumption, and merge cascade unblocking.
/// </summary>
public interface ITaskOrchestrator
{
    /// <summary>
    /// Executes the current workflow stage for the specified task with workspace locking and git checkout.
    /// LLM inference jobs are configured with zero automatic retries to prevent runaway edits.
    /// </summary>
    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    Task ProcessTaskAsync(int taskId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resumes a task blocked on human clarification, persisting the answer and enqueuing to llm-urgent.
    /// </summary>
    Task SubmitHumanAnswerAsync(int questionId, string answer, CancellationToken cancellationToken = default);

    /// <summary>
    /// Submits a batch of answers for pending questions on a task.
    /// When all questions in the interruption batch are answered, resumes the task exactly once on llm-urgent.
    /// </summary>
    Task SubmitAnswersAsync(int taskId, IReadOnlyDictionary<int, string> answers, CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-routes a task in review back to the implementation stage with reviewer notes and enqueues to llm-urgent.
    /// </summary>
    Task RequestChangesAsync(int taskId, string revisionNotes, CancellationToken cancellationToken = default);

    /// <summary>
    /// Promotes a suggested task to Open (with priority and merge mode, enqueuing if dependencies closed) or Rejected.
    /// </summary>
    Task PromoteSuggestedTaskAsync(int taskId, Domain.TaskOverallStatus targetStatus, int priority, Domain.MergeMode mergeMode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Completes a task by squash-merging its branch into base branch and cascades unblocking to dependent DAG tasks.
    /// Returns the typed CompleteTaskResult indicating whether squash succeeded or conflict resolution was dispatched.
    /// </summary>
    Task<Domain.CompleteTaskResult> CompleteTaskAndCascadeAsync(int taskId, string finalCommitMessage, CancellationToken cancellationToken = default);

    /// <summary>
    /// Transitions a non-terminal task to Rejected status, evicts/cancels any queued Hangfire jobs,
    /// records user audit history, updates dependency graph, and syncs markdown frontmatter to disk.
    /// </summary>
    Task RejectTaskAsync(int taskId, string? reason = null, CancellationToken cancellationToken = default);
}
