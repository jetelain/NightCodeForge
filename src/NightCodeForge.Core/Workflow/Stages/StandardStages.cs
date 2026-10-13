using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Workflow.Stages;

/// <summary>
/// Agentic stage performing deep codebase inspection, clarifying questions, and technical plan formulation.
/// </summary>
public class IssueReviewStage : IWorkflowStage
{
    public const string DefaultStageId = "issue-review";
    public string StageId => DefaultStageId;
    public string DisplayName => "Issue Review";
    public WorkflowStageKind Kind => WorkflowStageKind.Agentic;
    public AgentMode RequiredMode => AgentMode.Full;
    public string StageDirective { get; set; } = "Investigate requirements and codebase architecture. Proactively uncover ambiguities. If any design decision is ambiguous, invoke 'ask' immediately. Do not guess.";

    public Func<PipelineContext, CancellationToken, Task<StageExecutionResult>>? Handler { get; set; }

    public async Task<StageExecutionResult> ExecuteAsync(PipelineContext context, CancellationToken cancellationToken = default)
    {
        if (Handler != null)
        {
            return await Handler(context, cancellationToken);
        }
        return StageExecutionResult.Success();
    }
}

/// <summary>
/// Agentic stage executing code changes, builds, and local unit test validation.
/// </summary>
public class ImplementationStage : IWorkflowStage
{
    public const string DefaultStageId = "implementation";
    public string StageId => DefaultStageId;
    public string DisplayName => "Implementation";
    public WorkflowStageKind Kind => WorkflowStageKind.Agentic;
    public AgentMode RequiredMode => AgentMode.Full;
    public string StageDirective { get; set; } = "Implement clean code according to the approved plan. Run builds and tests incrementally with bash. Fix all compiler errors and test failures. If breaking blockers arise, call 'ask'.";

    public Func<PipelineContext, CancellationToken, Task<StageExecutionResult>>? Handler { get; set; }

    public async Task<StageExecutionResult> ExecuteAsync(PipelineContext context, CancellationToken cancellationToken = default)
    {
        if (Handler != null)
        {
            return await Handler(context, cancellationToken);
        }
        return StageExecutionResult.Success();
    }
}

/// <summary>
/// Agentic stage auditing git diff against base branch, enforcing coding standards and fixing regression gaps.
/// </summary>
public class CodeReviewAndFixStage : IWorkflowStage
{
    public const string DefaultStageId = "code-review-fix";
    public string StageId => DefaultStageId;
    public string DisplayName => "Code Review & Fix";
    public WorkflowStageKind Kind => WorkflowStageKind.Agentic;
    public AgentMode RequiredMode => AgentMode.Full;
    public string StageDirective { get; set; } = "Review the git diff against the base branch. Identify regressions or style violations and apply precision edits. Ensure builds and tests pass cleanly.";

    public Func<PipelineContext, CancellationToken, Task<StageExecutionResult>>? Handler { get; set; }

    public async Task<StageExecutionResult> ExecuteAsync(PipelineContext context, CancellationToken cancellationToken = default)
    {
        if (Handler != null)
        {
            return await Handler(context, cancellationToken);
        }
        return StageExecutionResult.Success();
    }
}
