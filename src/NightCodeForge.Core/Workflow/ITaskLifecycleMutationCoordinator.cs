using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Core.Workflow;

/// <summary>
/// Collaborator encapsulating human interaction state transitions (answer submission, change requests,
/// suggested task promotion, rejection, pipeline restart) and task cancellation registration.
/// </summary>
public interface ITaskLifecycleMutationCoordinator
{
    CancellationTokenSource RegisterExecutionCancellation(int taskId, CancellationToken externalToken = default);
    void UnregisterExecutionCancellation(int taskId, CancellationTokenSource cts);
    bool IsCancelRequested(int taskId);
    bool IsPauseRequested(int taskId);
    bool IsHostShuttingDown { get; }
    bool CanRestoreCheckpointMemory(IEnumerable<Conversation>? conversations, StageCheckpoint? checkpoint, string? stageId);

    Task SubmitHumanAnswerAsync(int questionId, string answer, CancellationToken cancellationToken = default);
    Task SubmitAnswersAsync(int taskId, IReadOnlyDictionary<int, string> answers, CancellationToken cancellationToken = default);
    Task RequestChangesAsync(int taskId, string revisionNotes, CancellationToken cancellationToken = default);
    Task PromoteSuggestedTaskAsync(
        int taskId,
        TaskOverallStatus targetStatus,
        int priority,
        MergeMode mergeMode,
        string? baseBranch = null,
        CancellationToken cancellationToken = default);
    Task RejectTaskAsync(int taskId, string? reason = null, CancellationToken cancellationToken = default);
    Task<bool> RestartTaskPipelineAsync(
        int taskId,
        TaskRestartGitMode gitMode = TaskRestartGitMode.PreserveWork,
        CancellationToken cancellationToken = default);
}

