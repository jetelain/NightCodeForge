namespace NightCodeForge.Core.Tests;

using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Core.Workflow.Configuration;
using NightCodeForge.Core.Workflow.Stages;

/// <summary>
/// Test fixture factory for constructing in-memory pipelines using GenericAgenticStage and standard deterministic quality gates.
/// </summary>
public static class TestWorkflowPipelines
{
    public static GenericAgenticStage CreateReviewStage(
        Func<PipelineContext, CancellationToken, Task<StageExecutionResult>>? handler = null)
    {
        return new GenericAgenticStage(
            StandardStageIds.TaskReview,
            "ForgeTask Review",
            new AgenticStageConfig
            {
                RequiredMode = AgentMode.Full,
                StageDirective = "You are a Staff Software Architect & Requirements Investigator.\n1. Thoroughly investigate the task requirements and inspect the codebase using 'read' and 'bash' (ls, rg, find) to locate affected files, dependencies, and interfaces.\n2. MANDATORY FILE EDIT: You must edit the task specification document on disk (specified in [ForgeTask Specification Document] or docs/tasks/) using 'edit' (or 'write'). Enrich it with your approved technical architecture, affected files/classes, step-by-step implementation plan, and testable acceptance criteria. When you finish, the system automatically reads back the file from disk to update the task specification and guide the implementation agent.\n3. Proactively uncover ambiguities, implicit assumptions, or architectural conflicts. If any requirement or design decision is ambiguous or has multiple valid alternatives, invoke 'ask' immediately. Do not guess.\n4. Do not write or modify production code in this stage.",
                PromptTemplate = "Execute stage '{{ stage.display_name }}' for task #{{ task.id }}: {{ task.title }}\n{{ task.description }}\n{{- if context.task_markdown_path }}\n\n[ForgeTask Specification Document]:\n{{ context.task_markdown_path }}\n(CRITICAL: You are required to edit this file directly on disk using 'edit' or 'write' to document your approved architecture, step-by-step implementation plan, and acceptance criteria. Your disk changes will be read back automatically upon stage completion to update the task specification and guide the implementation agent.)\n{{- end }}\n{{- if context.answered_questions }}\n\n[Human Clarifications & Answers]:\n{{ for qa in context.answered_questions }}\nQ: {{ qa.prompt }}\nA: {{ qa.answer }}\n{{ end }}\n{{- end }}"
            })
        {
            Handler = handler
        };
    }

    public static GenericAgenticStage CreateImplementationStage(
        Func<PipelineContext, CancellationToken, Task<StageExecutionResult>>? handler = null)
    {
        return new GenericAgenticStage(
            StandardStageIds.Implementation,
            "Implementation",
            new AgenticStageConfig
            {
                RequiredMode = AgentMode.Full,
                AgentProfileId = StandardStageIds.Implementation,
                StageDirective = "You are a Senior Software Engineer & TDD Practitioner.\n1. Implement clean, robust, and maintainable code adhering to the task specification document in docs/tasks/, the approved plan, human answers, and prior reviewer feedback.\n2. Keep the blast radius minimal: avoid unnecessary refactoring or file churn outside the approved scope.\n3. Apply precision edits with 'edit' (or 'write' for new files). Follow repository .editorconfig formatting standards.\n4. Continuously verify your changes: execute builds and tests incrementally using 'bash'. Fix all compiler errors and test failures.\n5. Write comprehensive unit tests covering all new logic and edge cases. If unexpected breaking blockers arise, invoke 'ask'.",
                PromptTemplate = "Execute stage '{{ stage.display_name }}' for task #{{ task.id }}: {{ task.title }}\n{{ task.description }}\n{{- if context.task_markdown_path }}\n\n[ForgeTask Specification Document]:\n{{ context.task_markdown_path }} (You can read or update this document with read/edit tools.)\n{{- end }}\n{{- if context.approved_plan }}\n\n[Approved Implementation Plan]:\n{{ context.approved_plan }}\n{{- end }}\n{{- if context.reviewer_feedback }}\n\n[Reviewer Requested Changes / Feedback]:\n{{ context.reviewer_feedback }}\n{{- end }}\n{{- if context.compiler_errors }}\n\n[Prior Diagnostic Failures to Remediate]:\n{{ for err in context.compiler_errors }}\n{{ err }}\n{{ end }}\n{{- end }}\n{{- if context.answered_questions }}\n\n[Human Clarifications & Answers]:\n{{ for qa in context.answered_questions }}\nQ: {{ qa.prompt }}\nA: {{ qa.answer }}\n{{ end }}\n{{- end }}"
            })
        {
            Handler = handler
        };
    }

    public static GenericAgenticStage CreateCodeReviewStage(
        Func<PipelineContext, CancellationToken, Task<StageExecutionResult>>? handler = null,
        string? loopBackStageId = StandardStageIds.Implementation)
    {
        return new GenericAgenticStage(
            StandardStageIds.CodeReviewAndFix,
            "Code Review & Fix",
            new AgenticStageConfig
            {
                RequiredMode = AgentMode.Review,
                LoopBackStageId = loopBackStageId,
                StageDirective = "You are a Principal Code Reviewer and Quality Gatekeeper.\n1. Inspect the full git diff against the base branch using 'bash' (e.g. git diff, git status) and 'read'. Compare against the specification in docs/tasks/ and the implementation claims.\n2. TIER 1 - MINOR POLISH: If the implementation is fundamentally sound and only requires minor edge-case fixes, code style cleanup, variable renames, or documentation additions, apply precision fixes directly using 'edit' and verify with 'bash' (build and test).\n3. TIER 2 - REJECTION GATE: Rejection is strictly reserved for major tasks or missing features. If core acceptance criteria are unfulfilled, if nothing has been implemented, if tests fundamentally fail requiring architectural redesign, or if entire modules are missing, REJECT the implementation immediately:\n   - If 'request_changes' tool is available, invoke 'request_changes(feedback, reason)' immediately.\n   - Otherwise, conclude your final response with:\n     <request_changes>\n     [Detailed explanation of missing features, broken requirements, and instructions for rework]\n     </request_changes>\n   Do not attempt to write entire missing feature modules in the review stage.",
                PromptTemplate = "Execute stage '{{ stage.display_name }}' for task #{{ task.id }}: {{ task.title }}\n{{ task.description }}\n{{- if context.task_markdown_path }}\n\n[ForgeTask Specification Document]:\n{{ context.task_markdown_path }}\n{{- end }}\n{{- if context.approved_plan }}\n\n[Approved Implementation Plan]:\n{{ context.approved_plan }}\n{{- end }}\n{{- if context.implementation_summary }}\n\n[Implementation Agent Completion Summary]:\n{{ context.implementation_summary }}\n(Note: Treat these reported achievements as claims to verify against the git diff and test results. Do not assume claims are true without checking the code.)\n{{- end }}"
            })
        {
            Handler = handler
        };
    }

    public static IWorkflowPipeline CreateStandardPipeline(
        GenericAgenticStage? reviewStage = null,
        IWorkflowStage? preImplStage = null,
        GenericAgenticStage? implStage = null,
        IWorkflowStage? postImplStage = null,
        GenericAgenticStage? fixStage = null,
        IWorkflowStage? postReviewStage = null)
    {
        return new WorkflowPipeline(new IWorkflowStage[]
        {
            reviewStage ?? CreateReviewStage(),
            preImplStage ?? new DeterministicBuildStage(stageId: StandardStageIds.PreImplementationBaseline, displayName: "Pre-Implementation Baseline"),
            implStage ?? CreateImplementationStage(),
            postImplStage ?? new DeterministicWarningRatchetStage(),
            fixStage ?? CreateCodeReviewStage(),
            postReviewStage ?? new DeterministicTestStage(stageId: StandardStageIds.PostReviewVerification, displayName: "Post-Review Verification")
        });
    }
}
