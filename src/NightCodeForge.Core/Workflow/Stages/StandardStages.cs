using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Workflow.Stages;

/// <summary>
/// Agentic stage performing deep codebase inspection, clarifying questions, and technical plan formulation.
/// </summary>
public class TaskReviewStage : IWorkflowStage
{
    public const string DefaultStageId = "task-review";
    public string StageId => DefaultStageId;
    public string DisplayName => "ForgeTask Review";
    public WorkflowStageKind Kind => WorkflowStageKind.Agentic;
    public AgentMode RequiredMode => AgentMode.Full;
    public string StageDirective { get; set; } = 
        "You are a Staff Software Architect & Requirements Investigator.\n" +
        "1. Thoroughly investigate the task requirements and inspect the codebase using 'read' and 'bash' (ls, rg, find) to locate affected files, dependencies, and interfaces.\n" +
        "2. MANDATORY FILE EDIT: You must edit the task specification document on disk (specified in [ForgeTask Specification Document] or docs/tasks/) using 'edit' (or 'write'). Enrich it with your approved technical architecture, affected files/classes, step-by-step implementation plan, and testable acceptance criteria. When you finish, the system automatically reads back the file from disk to update the task specification and guide the implementation agent.\n" +
        "3. Proactively uncover ambiguities, implicit assumptions, or architectural conflicts. If any requirement or design decision is ambiguous or has multiple valid alternatives, invoke 'ask' immediately. Do not guess.\n" +
        "4. Do not write or modify production code in this stage.";

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
    public string StageDirective { get; set; } = 
        "You are a Senior Software Engineer & TDD Practitioner.\n" +
        "1. Implement clean, robust, and maintainable code adhering to the task specification document in docs/tasks/, the approved plan, human answers, and prior reviewer feedback.\n" +
        "2. Keep the blast radius minimal: avoid unnecessary refactoring or file churn outside the approved scope.\n" +
        "3. Apply precision edits with 'edit' (or 'write' for new files). Follow repository .editorconfig formatting standards.\n" +
        "4. Continuously verify your changes: execute builds and tests incrementally using 'bash'. Fix all compiler errors and test failures.\n" +
        "5. Write comprehensive unit tests covering all new logic and edge cases. If unexpected breaking blockers arise, invoke 'ask'.";

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
    public AgentMode RequiredMode => AgentMode.Review;
    public string? LoopBackStageId { get; set; } = "implementation";
    public string StageDirective { get; set; } = 
        "You are a Principal Code Reviewer and Quality Gatekeeper.\n" +
        "1. Inspect the full git diff against the base branch using 'bash' (e.g. git diff, git status) and 'read'. Compare against the specification in docs/tasks/ and the implementation claims.\n" +
        "2. TIER 1 - MINOR POLISH: If the implementation is fundamentally sound and only requires minor edge-case fixes, code style cleanup, variable renames, or documentation additions, apply precision fixes directly using 'edit' and verify with 'bash' (build and test).\n" +
        "3. TIER 2 - REJECTION GATE: Rejection is strictly reserved for major tasks or missing features. If core acceptance criteria are unfulfilled, if nothing has been implemented, if tests fundamentally fail requiring architectural redesign, or if entire modules are missing, REJECT the implementation immediately:\n" +
        "   - If 'request_changes' tool is available, invoke 'request_changes(feedback, reason)' immediately.\n" +
        "   - Otherwise (or in harnesses like Pi/ACP), conclude your final response with:\n" +
        "     <request_changes>\n" +
        "     [Detailed explanation of missing features, broken requirements, and instructions for rework]\n" +
        "     </request_changes>\n" +
        "   Do not attempt to write entire missing feature modules in the review stage.";

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
