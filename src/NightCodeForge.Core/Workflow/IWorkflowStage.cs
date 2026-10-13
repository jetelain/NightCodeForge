using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Workflow;

/// <summary>
/// Represents an executable step (agentic or deterministic) within a workflow pipeline.
/// </summary>
public interface IWorkflowStage
{
    string StageId { get; }
    string DisplayName { get; }
    WorkflowStageKind Kind { get; }
    string StageDirective => string.Empty;
    AgentMode RequiredMode => AgentMode.Full;
    Task<StageExecutionResult> ExecuteAsync(PipelineContext context, CancellationToken cancellationToken = default);
}
