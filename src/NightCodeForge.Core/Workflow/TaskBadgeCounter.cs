using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;

namespace NightCodeForge.Core.Workflow;

/// <summary>
/// Authoritative helper for computing and synchronizing pending question and code review badge counts.
/// </summary>
public static class TaskBadgeCounter
{
    /// <summary>
    /// Determines whether a task is awaiting human code review in the Review Room.
    /// </summary>
    public static bool IsPendingHumanReview(ForgeTask task) =>
        task != null && task.Status == TaskOverallStatus.Ready && task.HasExecutionRun;

    /// <summary>
    /// Determines whether a question is pending clarification and its parent task is not in a terminal state (Closed, Rejected, Cancelled).
    /// </summary>
    public static bool IsPendingQuestion(Question question, ForgeTask? parentTask)
    {
        if (question == null || question.Status != QuestionStatus.Pending)
        {
            return false;
        }

        var task = parentTask ?? question.ForgeTask;
        if (task != null)
        {
            return task.Status != TaskOverallStatus.Closed &&
                   task.Status != TaskOverallStatus.Rejected &&
                   task.Status != TaskOverallStatus.Cancelled;
        }

        return true;
    }

    /// <summary>
    /// Determines whether a question is pending clarification and its parent task is not in a terminal state (Closed, Rejected, Cancelled).
    /// </summary>
    public static bool IsPendingQuestion(Question question) =>
        IsPendingQuestion(question, question?.ForgeTask);

    /// <summary>
    /// Authoritatively computes pending question and pending review counts across all workspaces.
    /// </summary>
    public static async Task<(int PendingQuestionCount, int PendingReviewCount)> ComputeCountsAsync(
        ITaskRepository taskRepository,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(taskRepository);

        var questions = await taskRepository.GetQuestionsByStatusAsync(QuestionStatus.Pending, workspaceId: null, cancellationToken: cancellationToken)
            ?? Array.Empty<Question>();
        var pendingQuestionCount = questions.Count(IsPendingQuestion);

        var readyTasks = await taskRepository.GetByStatusAsync(TaskOverallStatus.Ready, cancellationToken: cancellationToken)
            ?? Array.Empty<ForgeTask>();
        var pendingReviewCount = readyTasks.Count(IsPendingHumanReview);

        return (pendingQuestionCount, pendingReviewCount);
    }

    /// <summary>
    /// Authoritatively recomputes counts from repository and publishes them to telemetry and event subscribers.
    /// </summary>
    public static async Task SynchronizeBadgeCountsAsync(
        ITaskRepository taskRepository,
        IOperationsTelemetryService telemetryService,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(taskRepository);
        ArgumentNullException.ThrowIfNull(telemetryService);

        var (questionCount, reviewCount) = await ComputeCountsAsync(taskRepository, cancellationToken);
        telemetryService.RecordPendingCounts(questionCount, reviewCount);
    }
}
