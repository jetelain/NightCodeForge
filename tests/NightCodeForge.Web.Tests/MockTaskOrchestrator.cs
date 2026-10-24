using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Web.Tests;

public class MockTaskOrchestrator : ITaskOrchestrator
{
    public (int TaskId, TaskOverallStatus TargetStatus, int Priority, MergeMode MergeMode)? LastPromotedCall { get; private set; }
    public (int QuestionId, string Answer)? LastSubmitHumanAnswerCall { get; private set; }
    public (int TaskId, IReadOnlyDictionary<int, string> Answers)? LastSubmitAnswersCall { get; private set; }
    public (int TaskId, string RevisionNotes)? LastRequestChangesCall { get; private set; }
    public (int TaskId, string FinalCommitMessage)? LastCompleteTaskCall { get; private set; }
    public (int TaskId, string? Reason)? LastRejectCall { get; private set; }
    public (int TaskId, TaskRestartGitMode GitMode)? LastRestartPipelineCall { get; private set; }
    public bool RestartPipelineResult { get; set; } = true;
    public Exception? RestartPipelineException { get; set; }
    public int? LastProcessedTaskId { get; private set; }

    public Task<bool> RestartTaskPipelineAsync(int taskId, TaskRestartGitMode gitMode = TaskRestartGitMode.PreserveWork, CancellationToken cancellationToken = default)
    {
        LastRestartPipelineCall = (taskId, gitMode);
        if (RestartPipelineException != null)
        {
            throw RestartPipelineException;
        }
        return Task.FromResult(RestartPipelineResult);
    }

    public Task RejectTaskAsync(int taskId, string? reason = null, CancellationToken cancellationToken = default)
    {
        LastRejectCall = (taskId, reason);
        return Task.CompletedTask;
    }

    public Task PromoteSuggestedTaskAsync(int taskId, TaskOverallStatus targetStatus, int priority, MergeMode mergeMode, CancellationToken cancellationToken = default)
    {
        LastPromotedCall = (taskId, targetStatus, priority, mergeMode);
        return Task.CompletedTask;
    }

    public Task ProcessTaskAsync(int taskId, CancellationToken cancellationToken = default)
    {
        LastProcessedTaskId = taskId;
        return Task.CompletedTask;
    }

    public Task SubmitHumanAnswerAsync(int questionId, string answer, CancellationToken cancellationToken = default)
    {
        LastSubmitHumanAnswerCall = (questionId, answer);
        return Task.CompletedTask;
    }

    public Task SubmitAnswersAsync(int taskId, IReadOnlyDictionary<int, string> answers, CancellationToken cancellationToken = default)
    {
        LastSubmitAnswersCall = (taskId, answers);
        if (answers.Count == 1)
        {
            var kvp = answers.First();
            LastSubmitHumanAnswerCall = (kvp.Key, kvp.Value);
        }
        return Task.CompletedTask;
    }

    public Task RequestChangesAsync(int taskId, string revisionNotes, CancellationToken cancellationToken = default)
    {
        LastRequestChangesCall = (taskId, revisionNotes);
        return Task.CompletedTask;
    }

    public CompleteTaskResult CompleteResult { get; set; } = new(CompleteTaskStatus.Completed);
    public Exception? CompleteTaskException { get; set; }
    public Func<int, string, CompleteTaskResult>? OnCompleteTaskAndCascade { get; set; }

    public Task<CompleteTaskResult> CompleteTaskAndCascadeAsync(int taskId, string finalCommitMessage, CancellationToken cancellationToken = default)
    {
        LastCompleteTaskCall = (taskId, finalCommitMessage);
        if (CompleteTaskException != null)
        {
            throw CompleteTaskException;
        }
        if (OnCompleteTaskAndCascade != null)
        {
            return Task.FromResult(OnCompleteTaskAndCascade(taskId, finalCommitMessage));
        }
        return Task.FromResult(CompleteResult);
    }
}
