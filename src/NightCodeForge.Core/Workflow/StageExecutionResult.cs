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
    public Question? ClarificationQuestion { get; init; }
    public StageExecutionRecord? ExecutionRecord { get; init; }

    public static StageExecutionResult Success(string? nextStageId = null, StageExecutionRecord? record = null) =>
        new() { IsSuccess = true, TargetRoutingStageId = nextStageId, ExecutionRecord = record };

    public static StageExecutionResult Failure(string errorMessage, string? loopBackStageId = null, StageExecutionRecord? record = null) =>
        new() { IsSuccess = false, ErrorMessage = errorMessage, TargetRoutingStageId = loopBackStageId, ExecutionRecord = record };

    public static StageExecutionResult QuestionRequired(Question question, StageExecutionRecord? record = null) =>
        new() { IsSuccess = false, NeedsClarification = true, ClarificationQuestion = question, ExecutionRecord = record };

    public static StageExecutionResult WaitingForHuman(Question? question = null, StageExecutionRecord? record = null) =>
        new() { IsSuccess = true, NeedsClarification = true, ClarificationQuestion = question, ExecutionRecord = record };
}
