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
