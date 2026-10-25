namespace NightCodeForge.Core.Workflow;

/// <summary>
/// Exit reasons for a workflow cohort execution turn in <see cref="ITaskOrchestrator"/>.
/// </summary>
public enum CohortExitReason
{
    /// <summary>
    /// Execution turn was aborted before or during execution without progressing.
    /// </summary>
    Aborted,

    /// <summary>
    /// The current cohort completed successfully (either reaching a cohort boundary or terminal delivery).
    /// </summary>
    CohortCompleted,

    /// <summary>
    /// Execution was paused due to global operations pause or task deferral.
    /// </summary>
    Paused,

    /// <summary>
    /// Execution was cooperatively yielded to allow a pending P1 Urgent job to run.
    /// </summary>
    Yielded,

    /// <summary>
    /// Execution exited because the task requires human input (clarification question or conflict resolution escalation).
    /// </summary>
    WaitingForHuman,

    /// <summary>
    /// Execution failed (e.g. fatal stage failure, workspace checkout error, or cross-cohort loopback).
    /// </summary>
    Failed,

    /// <summary>
    /// Execution was cancelled by the user or application shutdown.
    /// </summary>
    Cancelled
}
