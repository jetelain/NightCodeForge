namespace NightCodeForge.Core.Domain;

using NightCodeForge.Core.Workflow;

/// <summary>
/// Canonical domain workflow definitions and templates decoupled from persistence.
/// </summary>
public static class StandardWorkflowDefinitions
{
    public const string Standard3AgentCode = "standard-3-agent";

    /// <summary>
    /// Creates the standard 3-agent pipeline with deterministic quality gates.
    /// </summary>
    public static WorkflowDefinition CreateStandard3AgentWorkflow()
    {
        var workflow = new WorkflowDefinition
        {
            Name = "Standard 3-Agent Pipeline",
            Code = Standard3AgentCode,
            Icon = "⚡",
            Description = "Default autonomous pipeline: ForgeTask Review -> Pre-Impl Baseline -> Implementation -> Post-Impl Gates -> Code Review & Fix -> Post-Review Verification.",
            IsDefault = true,
            WorkspaceId = null,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Stages = new List<StageDefinition>
            {
                new StageDefinition
                {
                    StageId = StandardStageIds.TaskReview,
                    DisplayName = "ForgeTask Review",
                    Icon = "🔍",
                    SequenceOrder = 1,
                    QueueTier = StageQueueTier.TaskReview,
                    Kind = WorkflowStageKind.Agentic,
                    CsharpImplementationType = StageImplementationTypes.Agentic,
                    ConfigurationJson = """
                    {
                      "AgentProfileId": null,
                      "RequiredMode": 1,
                      "MaxRetries": 3,
                      "TimeoutMinutes": 30,
                      "StageDirective": "You are a Staff Software Architect & Requirements Investigator.\n1. Thoroughly investigate the task requirements and inspect the codebase using 'read' and 'bash' (ls, rg, find) to locate affected files, dependencies, and interfaces.\n2. MANDATORY FILE EDIT: You must edit the task specification document on disk (specified in [ForgeTask Specification Document] or docs/tasks/) using 'edit' (or 'write'). Enrich it with your approved technical architecture, affected files/classes, step-by-step implementation plan, and testable acceptance criteria. When you finish, the system automatically reads back the file from disk to update the task specification and guide the implementation agent. Always preserve the YAML frontmatter block (between --- and ---) at the top of the file without modifying the id or status fields. Do not append conversational chat history, transcript dialogue, or duplicate reasoning monologues. The task markdown document must remain a structured, clean technical specification.\n3. Gather all questions and ambiguities into a single 'ask' call using the question list, expecting all answers at once. Follow-up asks are permitted only for newly discovered blockers after answers arrive. Do not guess.\n4. Do not write or modify production code in this stage.",
                      "PromptTemplate": "Execute stage '{{ stage.display_name }}' for task #{{ task.id }}: {{ task.title }}\nPlease review the task specification in the system prompt and [ForgeTask Specification Document] below to begin execution.\n{{- if context.task_markdown_path }}\n\n[ForgeTask Specification Document]:\n{{ context.task_markdown_path }}\n(CRITICAL: You are required to edit this file directly on disk using 'edit' or 'write' to document your approved architecture, step-by-step implementation plan, and acceptance criteria. Your disk changes will be read back automatically upon stage completion. Preserve the top YAML frontmatter block without modifying the task status.)\n{{- end }}\n{{- if context.answered_questions }}\n\n[Human Clarifications & Answers]:\n{{ for qa in context.answered_questions }}\nQ: {{ qa.prompt }}\nA: {{ qa.answer }}\n{{ end }}\n{{- end }}"
                    }
                    """
                },
                new StageDefinition
                {
                    StageId = StandardStageIds.PreImplementationBaseline,
                    DisplayName = "Pre-Implementation Baseline",
                    Icon = "⚙️",
                    SequenceOrder = 2,
                    QueueTier = StageQueueTier.Implementation,
                    Kind = WorkflowStageKind.Deterministic,
                    CsharpImplementationType = StageImplementationTypes.DeterministicComposite,
                    ConfigurationJson = """{"Action": "CompositeBaseline", "FailOnError": false, "TimeoutSeconds": 600, "ExtraParameters": {"Stages": "build,test-with-coverage"}}"""
                },
                new StageDefinition
                {
                    StageId = StandardStageIds.Implementation,
                    DisplayName = "Implementation",
                    Icon = "💻",
                    SequenceOrder = 3,
                    QueueTier = StageQueueTier.Implementation,
                    Kind = WorkflowStageKind.Agentic,
                    CsharpImplementationType = StageImplementationTypes.Agentic,
                    ConfigurationJson = """
                    {
                      "AgentProfileId": null,
                      "RequiredMode": 1,
                      "MaxRetries": 3,
                      "TimeoutMinutes": 60,
                      "StageDirective": "You are a Senior Software Engineer & TDD Practitioner.\n1. Implement clean, robust, and maintainable code adhering to the task specification document in docs/tasks/, the approved plan, human answers, and prior reviewer feedback. If editing docs/tasks/, preserve the YAML frontmatter block intact without modifying the id or status fields.\n2. Keep the blast radius minimal: avoid unnecessary refactoring or file churn outside the approved scope.\n3. Apply precision edits with 'edit' (or 'write' for new files). Follow repository .editorconfig formatting standards.\n4. Continuously verify your changes: execute builds and tests incrementally using 'bash'. Fix all compiler errors and test failures.\n5. Write comprehensive unit tests covering all new logic and edge cases. If unexpected breaking blockers arise, invoke 'ask'.",
                      "PromptTemplate": "Execute stage '{{ stage.display_name }}' for task #{{ task.id }}: {{ task.title }}\nPlease review the task specification in the system prompt and [ForgeTask Specification Document] below to begin execution.\n{{- if context.task_markdown_path }}\n\n[ForgeTask Specification Document]:\n{{ context.task_markdown_path }} (You can read or update this document with read/edit tools.)\n{{- end }}\n{{- if context.approved_plan }}\n\n[Approved Implementation Plan]:\n{{ context.approved_plan }}\n{{- end }}\n{{- if context.reviewer_feedback }}\n\n[Reviewer Requested Changes / Feedback]:\n{{ context.reviewer_feedback }}\nNotice: You are required to implement a dedicated automated test for every bug or defect identified in the reviewer's feedback before completing your implementation.\n{{- end }}\n{{- if context.compiler_errors }}\n\n[Prior Diagnostic Failures to Remediate]:\n{{ for err in context.compiler_errors }}\n{{ err }}\n{{ end }}\n{{- end }}\n{{- if context.answered_questions }}\n\n[Human Clarifications & Answers]:\n{{ for qa in context.answered_questions }}\nQ: {{ qa.prompt }}\nA: {{ qa.answer }}\n{{ end }}\n{{- end }}"
                    }
                    """
                },
                new StageDefinition
                {
                    StageId = StandardStageIds.PostImplementationGates,
                    DisplayName = "Post-Implementation Gates",
                    Icon = "🧪",
                    SequenceOrder = 4,
                    QueueTier = StageQueueTier.Implementation,
                    Kind = WorkflowStageKind.Deterministic,
                    CsharpImplementationType = StageImplementationTypes.DeterministicComposite,
                    ConfigurationJson = """{"Action": "CompositeVerification", "LoopBackStageId": "{LOOPBACK}", "FailOnError": true, "TimeoutSeconds": 900, "ExtraParameters": {"Stages": "format,build,test-with-coverage,sast"}}"""
                        .Replace("{LOOPBACK}", StandardStageIds.Implementation)
                },
                new StageDefinition
                {
                    StageId = StandardStageIds.CodeReviewAndFix,
                    DisplayName = "Code Review & Fix",
                    Icon = "🧐",
                    SequenceOrder = 5,
                    QueueTier = StageQueueTier.CodeReview,
                    Kind = WorkflowStageKind.Agentic,
                    CsharpImplementationType = StageImplementationTypes.Agentic,
                    ConfigurationJson = """
                    {
                      "AgentProfileId": null,
                      "RequiredMode": 2,
                      "LoopBackStageId": "{LOOPBACK}",
                      "MaxRetries": 3,
                      "TimeoutMinutes": 30,
                      "StageDirective": "You are a Principal Code Reviewer and Quality Gatekeeper.\n1. Inspect the full git diff against the base branch using 'bash' (e.g. git diff, git status) and 'read'. Compare against the specification in docs/tasks/ and the implementation claims.\n2. TIER 1 - MINOR POLISH: If the implementation is fundamentally sound and only requires minor edge-case fixes, code style cleanup, variable renames, or documentation additions, apply precision fixes directly using 'edit' and verify with 'bash' (build and test).\n3. TIER 2 - REJECTION GATE: Rejection is strictly reserved for major tasks or missing features. If core acceptance criteria are unfulfilled, if nothing has been implemented, if tests fundamentally fail requiring architectural redesign, or if entire modules are missing, REJECT the implementation immediately:\n   - If 'request_changes' tool is available, invoke 'request_changes(feedback, reason)' immediately.\n   - Otherwise, conclude your final response with:\n     <request_changes>\n     [Detailed explanation of missing features, broken requirements, and instructions for rework]\n     </request_changes>\n   MANDATORY BUG REGRESSION POLICY: When rejecting due to bugs, regressions, or unfulfilled requirements, your feedback MUST explicitly require the implementation agent to create a dedicated automated test (unit or integration test) reproducing each bug found before declaring the fix complete.",
                      "PromptTemplate": "Execute stage '{{ stage.display_name }}' for task #{{ task.id }}: {{ task.title }}\nPlease review the task specification in the system prompt and [ForgeTask Specification Document] below to begin execution.\n{{- if context.task_markdown_path }}\n\n[ForgeTask Specification Document]:\n{{ context.task_markdown_path }}\n{{- end }}\n{{- if context.approved_plan }}\n\n[Approved Implementation Plan]:\n{{ context.approved_plan }}\n{{- end }}\n{{- if context.implementation_summary }}\n\n[Implementation Agent Completion Summary]:\n{{ context.implementation_summary }}\n(Note: Treat these reported achievements as claims to verify against the git diff and test results. Do not assume claims are true without checking the code.)\n{{- end }}\n{{- if context.review_diff_stat }}\n\n[Git Diff Summary]:\n```diff\n{{ context.review_diff_stat }}\n```\nTo inspect the diff of a specific file against base branch '{{ task.base_branch }}', run:\ngit diff {{ task.base_branch }}...{{ task.feature_branch }} -- <file-path>\n{{- end }}"
                    }
                    """.Replace("{LOOPBACK}", StandardStageIds.Implementation)
                },
                new StageDefinition
                {
                    StageId = StandardStageIds.PostReviewVerification,
                    DisplayName = "Post-Review Verification",
                    Icon = "🛡️",
                    SequenceOrder = 6,
                    QueueTier = StageQueueTier.CodeReview,
                    Kind = WorkflowStageKind.Deterministic,
                    CsharpImplementationType = StageImplementationTypes.DeterministicComposite,
                    ConfigurationJson = """{"Action": "CompositeVerification", "LoopBackStageId": "{LOOPBACK}", "FailOnError": true, "TimeoutSeconds": 600, "ExtraParameters": {"Stages": "build,test-with-coverage"}}"""
                        .Replace("{LOOPBACK}", StandardStageIds.Implementation)
                }
            }
        };

        foreach (var stage in workflow.Stages)
        {
            stage.Workflow = workflow;
        }

        return workflow;
    }
}
