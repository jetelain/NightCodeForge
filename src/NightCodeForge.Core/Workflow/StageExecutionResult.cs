using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Workflow;

/// <summary>
/// Outcome of executing a workflow stage, indicating status, failure reason, or routing directives.
/// </summary>
public class StageExecutionResult
{
    public bool IsSuccess { get; init; }
    public string? TargetRoutingStageId { get; init; }
    public string? LoopBackStageId => TargetRoutingStageId;
    public string? ErrorMessage { get; init; }
    public bool NeedsClarification { get; init; }
    public bool IsCancelled { get; init; }
    public IReadOnlyList<Question>? ClarificationQuestions { get; init; }
    public StageExecutionRecord? ExecutionRecord { get; init; }

    public static StageExecutionResult Success(string? nextStageId = null, StageExecutionRecord? record = null) =>
        new() { IsSuccess = true, TargetRoutingStageId = nextStageId, ExecutionRecord = record };

    public static StageExecutionResult Failure(string errorMessage, string? loopBackStageId = null, StageExecutionRecord? record = null) =>
        new() { IsSuccess = false, ErrorMessage = errorMessage, TargetRoutingStageId = loopBackStageId, ExecutionRecord = record };

    public static StageExecutionResult Cancelled(string? message = null, StageExecutionRecord? record = null) =>
        new() { IsSuccess = false, IsCancelled = true, ErrorMessage = message ?? "Execution was cancelled.", ExecutionRecord = record };

    public static StageExecutionResult QuestionRequired(IReadOnlyList<Question> questions, StageExecutionRecord? record = null)
    {
        if (questions == null || questions.Count == 0)
        {
            throw new ArgumentException("Clarification questions cannot be null or empty.", nameof(questions));
        }
        return new() { IsSuccess = false, NeedsClarification = true, ClarificationQuestions = questions, ExecutionRecord = record };
    }

    public static StageExecutionResult WaitingForHuman(IReadOnlyList<Question> questions, StageExecutionRecord? record = null)
    {
        if (questions == null || questions.Count == 0)
        {
            throw new ArgumentException("Clarification questions cannot be null or empty.", nameof(questions));
        }
        return new() { IsSuccess = true, NeedsClarification = true, ClarificationQuestions = questions, ExecutionRecord = record };
    }
}
