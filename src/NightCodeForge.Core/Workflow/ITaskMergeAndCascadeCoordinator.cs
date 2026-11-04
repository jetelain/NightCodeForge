using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Core.Workflow;

/// <summary>
/// Outcome of verifying merge conflict resolution after the Implementation stage.
/// </summary>
public enum ConflictVerificationOutcome
{
    VerifiedOrNone,
    EscalatedToHuman,
    LoopedBackToImplementation
}

/// <summary>
/// Outcome of Gate 2 mergeability check and base branch synchronization before marking Ready or Auto-merging.
/// </summary>
public readonly record struct Gate2MergeabilityOutcome(
    bool DispatchedConflictResolution,
    bool BaseHadAdvanced);

/// <summary>
/// Collaborator encapsulating DAG readiness checks, merge conflict resolution verification and initiation,
/// squash-and-merge gate execution, and downstream DAG cascade unblocking.
/// </summary>
public interface ITaskMergeAndCascadeCoordinator
{
    Task<IWorkflowPipeline> GetPipelineAsync(ForgeTask task, CancellationToken cancellationToken = default);
    Task<bool> EnsureReadyForExecutionAsync(ForgeTask task, CancellationToken cancellationToken = default);
    Task<bool> IsReadyForExecutionAsync(int taskId, CancellationToken cancellationToken = default);
    Task UpdateTaskStatusInStoreAsync(int taskId, TaskOverallStatus status, CancellationToken cancellationToken = default);
    Task SetWaitingForHumanInStoreAsync(int taskId, bool isWaiting, CancellationToken cancellationToken = default);
    string EnqueueStage(int taskId, StageQueueTier tier, int priority, DateTime? originalEnqueuedAt = null);
    Task<bool> HasUrgentJobsWaitingAsync(CancellationToken cancellationToken = default);

    Task<ConflictVerificationOutcome> VerifyAndHandleImplementationConflictsAsync(
        ForgeTask task,
        Workspace workspace,
        PipelineContext pipelineContext,
        IWorkflowPipeline pipeline,
        CancellationToken cancellationToken = default);

    Task<Gate2MergeabilityOutcome> CheckAndSyncGate2MergeabilityAsync(
        ForgeTask task,
        Workspace workspace,
        PipelineContext pipelineContext,
        string stageId,
        CancellationToken cancellationToken = default);

    Task<CompleteTaskResult> CompleteTaskAndCascadeAsync(
        int taskId,
        string finalCommitMessage,
        CancellationToken cancellationToken = default);

    Task<CompleteTaskResult> CompleteTaskAndCascadeInternalAsync(
        ForgeTask task,
        Workspace workspace,
        string finalCommitMessage,
        CancellationToken cancellationToken = default);

    Task InitiateConflictResolutionAsync(
        ForgeTask task,
        Workspace workspace,
        PipelineContext pipelineContext,
        IReadOnlyList<string> conflictedFiles,
        CancellationToken cancellationToken = default,
        bool discardApproval = false);
}

