# 🌱 Step 8.2: Fresh Database Seeding & Default Standard Workflow

> **Technical Specification & Implementation Guide**  
> *Parent Plan:* [docs/plan/08_DATABASE_DRIVEN_WORKFLOWS_AND_STAGES.md](../08_DATABASE_DRIVEN_WORKFLOWS_AND_STAGES.md)  
> *Status:* Ready for Implementation

---

## 1. Objective

Implement the startup seeder `EnsureDefaultWorkflowsAsync` in `AppDbContext` to create the standard 6-stage autonomous workflow on a fresh empty database, complete with UI glyph icons for both the workflow and each of its constituent stages. (Note: Legacy database migration/backfill is **not** required; setup will initialize on a clean database).

---

## 2. Seed Data Specification: Standard 3-Agent Workflow

### 2.1 Workflow Definition Record
- `Name`: `"Standard 3-Agent Pipeline"`
- `Code`: `"standard-3-agent"`
- `Icon`: `"⚡"` (Emoji/Glyph for badges, dropdowns, and cards)
- `Description`: `"Default autonomous pipeline: ForgeTask Review -> Pre-Impl Baseline -> Implementation -> Post-Impl Gates -> Code Review & Fix -> Post-Review Verification -> Auto-Merge."`
- `IsDefault`: `true`
- `WorkspaceId`: `null` (Global template)

### 2.2 Stage Definitions Configuration Matrix

All agentic stages use the unified `agentic` C# implementation type driven by `AgenticStageConfig` containing explicit `StageDirective` and Scriban `PromptTemplate` payloads.

| Order | Icon | StageId | DisplayName | Kind | C# Implementation Type | Configuration Payload Summary |
| :---: | :---: | :--- | :--- | :---: | :--- | :--- |
| **1** | 🔍 | `task-review` | ForgeTask Review | Agentic | `agentic` | Architect persona, Full mode, Scriban prompt requesting task spec edits on disk |
| **2** | ⚙️ | `deterministic-pre-impl` | Pre-Implementation Baseline | Deterministic | `deterministic-composite` | `{"Action": "CompositeBaseline", "FailOnError": false, "TimeoutSeconds": 600, "ExtraParameters": {"Stages": "build,test"}}` |
| **3** | 💻 | `implementation` | Implementation | Agentic | `agentic` | Developer persona, Full mode, Scriban prompt rendering spec doc, approved plan, feedback, and compiler errors |
| **4** | 🧪 | `deterministic-post-impl` | Post-Implementation Gates | Deterministic | `deterministic-composite` | `{"Action": "CompositeVerification", "LoopBackStageId": "implementation", "FailOnError": true, "TimeoutSeconds": 900, "ExtraParameters": {"Stages": "build,test,coverage,sast"}}` |
| **5** | 🧐 | `code-review-fix` | Code Review & Fix | Agentic | `agentic` | Reviewer persona, Review mode, loopback to `implementation`, Scriban prompt rendering diff handoff |
| **6** | 🛡️ | `deterministic-post-review` | Post-Review Verification | Deterministic | `deterministic-composite` | `{"Action": "CompositeVerification", "LoopBackStageId": "implementation", "FailOnError": true, "TimeoutSeconds": 600, "ExtraParameters": {"Stages": "build,test"}}` |

---

## 3. Implementation Details

### 3.1 `EnsureDefaultWorkflowsAsync` in `AppDbContext.cs`
```csharp
public async Task<WorkflowDefinition> EnsureDefaultWorkflowsAsync(CancellationToken cancellationToken = default)
{
    var standardWorkflow = await WorkflowDefinitions
        .Include(w => w.Stages)
        .FirstOrDefaultAsync(w => w.Code == "standard-3-agent", cancellationToken);

    if (standardWorkflow == null)
    {
        standardWorkflow = new WorkflowDefinition
        {
            Name = "Standard 3-Agent Pipeline",
            Code = "standard-3-agent",
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
                    StageId = "task-review",
                    DisplayName = "ForgeTask Review",
                    Icon = "🔍",
                    SequenceOrder = 1,
                    Kind = WorkflowStageKind.Agentic,
                    CsharpImplementationType = "agentic",
                    ConfigurationJson = """
                    {
                      "AgentProfileId": null,
                      "RequiredMode": "Full",
                      "MaxRetries": 3,
                      "TimeoutMinutes": 30,
                      "StageDirective": "You are a Staff Software Architect & Requirements Investigator.\n1. Thoroughly investigate the task requirements and inspect the codebase using 'read' and 'bash' (ls, rg, find) to locate affected files, dependencies, and interfaces.\n2. MANDATORY FILE EDIT: You must edit the task specification document on disk (specified in [ForgeTask Specification Document] or docs/tasks/) using 'edit' (or 'write'). Enrich it with your approved technical architecture, affected files/classes, step-by-step implementation plan, and testable acceptance criteria. When you finish, the system automatically reads back the file from disk to update the task specification and guide the implementation agent.\n3. Proactively uncover ambiguities, implicit assumptions, or architectural conflicts. If any requirement or design decision is ambiguous or has multiple valid alternatives, invoke 'ask' immediately. Do not guess.\n4. Do not write or modify production code in this stage.",
                      "PromptTemplate": "Execute stage '{{ stage.display_name }}' for task #{{ task.id }}: {{ task.title }}\n{{ task.description }}\n{{- if context.task_markdown_path }}\n\n[ForgeTask Specification Document]:\n{{ context.task_markdown_path }}\n(CRITICAL: You are required to edit this file directly on disk using 'edit' or 'write' to document your approved architecture, step-by-step implementation plan, and acceptance criteria.)\n{{- end }}\n{{- if context.answered_questions }}\n\n[Human Clarifications & Answers]:\n{{ for qa in context.answered_questions }}\nQ: {{ qa.prompt }}\nA: {{ qa.answer }}\n{{ end }}\n{{- end }}"
                    }
                    """
                },
                new StageDefinition
                {
                    StageId = "deterministic-pre-impl",
                    DisplayName = "Pre-Implementation Baseline",
                    Icon = "⚙️",
                    SequenceOrder = 2,
                    Kind = WorkflowStageKind.Deterministic,
                    CsharpImplementationType = "deterministic-composite",
                    ConfigurationJson = """{"Action": "CompositeBaseline", "FailOnError": false, "TimeoutSeconds": 600, "ExtraParameters": {"Stages": "build,test"}}"""
                },
                new StageDefinition
                {
                    StageId = "implementation",
                    DisplayName = "Implementation",
                    Icon = "💻",
                    SequenceOrder = 3,
                    Kind = WorkflowStageKind.Agentic,
                    CsharpImplementationType = "agentic",
                    ConfigurationJson = """
                    {
                      "AgentProfileId": null,
                      "RequiredMode": "Full",
                      "MaxRetries": 3,
                      "TimeoutMinutes": 60,
                      "StageDirective": "You are a Senior Software Engineer & TDD Practitioner.\n1. Implement clean, robust, and maintainable code adhering to the task specification document in docs/tasks/, the approved plan, human answers, and prior reviewer feedback.\n2. Keep the blast radius minimal: avoid unnecessary refactoring or file churn outside the approved scope.\n3. Apply precision edits with 'edit' (or 'write' for new files). Follow repository .editorconfig formatting standards.\n4. Continuously verify your changes: execute builds and tests incrementally using 'bash'. Fix all compiler errors and test failures.\n5. Write comprehensive unit tests covering all new logic and edge cases. If unexpected breaking blockers arise, invoke 'ask'.",
                      "PromptTemplate": "Execute stage '{{ stage.display_name }}' for task #{{ task.id }}: {{ task.title }}\n{{ task.description }}\n{{- if context.task_markdown_path }}\n\n[ForgeTask Specification Document]:\n{{ context.task_markdown_path }} (You can read or update this document with read/edit tools.)\n{{- end }}\n{{- if context.approved_plan }}\n\n[Approved Implementation Plan]:\n{{ context.approved_plan }}\n{{- end }}\n{{- if context.reviewer_feedback }}\n\n[Reviewer Requested Changes / Feedback]:\n{{ context.reviewer_feedback }}\n{{- end }}\n{{- if context.compiler_errors }}\n\n[Prior Diagnostic Failures to Remediate]:\n{{ for err in context.compiler_errors }}\n{{ err }}\n{{ end }}\n{{- end }}\n{{- if context.answered_questions }}\n\n[Human Clarifications & Answers]:\n{{ for qa in context.answered_questions }}\nQ: {{ qa.prompt }}\nA: {{ qa.answer }}\n{{ end }}\n{{- end }}"
                    }
                    """
                },
                new StageDefinition
                {
                    StageId = "deterministic-post-impl",
                    DisplayName = "Post-Implementation Gates",
                    Icon = "🧪",
                    SequenceOrder = 4,
                    Kind = WorkflowStageKind.Deterministic,
                    CsharpImplementationType = "deterministic-composite",
                    ConfigurationJson = """{"Action": "CompositeVerification", "LoopBackStageId": "implementation", "FailOnError": true, "TimeoutSeconds": 900, "ExtraParameters": {"Stages": "build,test,coverage,sast"}}"""
                },
                new StageDefinition
                {
                    StageId = "code-review-fix",
                    DisplayName = "Code Review & Fix",
                    Icon = "🧐",
                    SequenceOrder = 5,
                    Kind = WorkflowStageKind.Agentic,
                    CsharpImplementationType = "agentic",
                    ConfigurationJson = """
                    {
                      "AgentProfileId": null,
                      "RequiredMode": "Review",
                      "LoopBackStageId": "implementation",
                      "MaxRetries": 3,
                      "TimeoutMinutes": 30,
                      "StageDirective": "You are a Principal Code Reviewer and Quality Gatekeeper.\n1. Inspect the full git diff against the base branch using 'bash' (e.g. git diff, git status) and 'read'. Compare against the specification in docs/tasks/ and the implementation claims.\n2. TIER 1 - MINOR POLISH: If the implementation is fundamentally sound and only requires minor edge-case fixes, code style cleanup, variable renames, or documentation additions, apply precision fixes directly using 'edit' and verify with 'bash' (build and test).\n3. TIER 2 - REJECTION GATE: Rejection is strictly reserved for major tasks or missing features. If core acceptance criteria are unfulfilled, if nothing has been implemented, if tests fundamentally fail requiring architectural redesign, or if entire modules are missing, REJECT the implementation immediately:\n   - If 'request_changes' tool is available, invoke 'request_changes(feedback, reason)' immediately.\n   - Otherwise, conclude your final response with:\n     <request_changes>\n     [Detailed explanation of missing features, broken requirements, and instructions for rework]\n     </request_changes>\n   Do not attempt to write entire missing feature modules in the review stage.",
                      "PromptTemplate": "Execute stage '{{ stage.display_name }}' for task #{{ task.id }}: {{ task.title }}\n{{ task.description }}\n{{- if context.task_markdown_path }}\n\n[ForgeTask Specification Document]:\n{{ context.task_markdown_path }}\n{{- end }}\n{{- if context.approved_plan }}\n\n[Approved Implementation Plan]:\n{{ context.approved_plan }}\n{{- end }}\n{{- if context.implementation_summary }}\n\n[Implementation Agent Completion Summary]:\n{{ context.implementation_summary }}\n(Note: Treat these reported achievements as claims to verify against the git diff and test results. Do not assume claims are true without checking the code.)\n{{- end }}"
                    }
                    """
                },
                new StageDefinition
                {
                    StageId = "deterministic-post-review",
                    DisplayName = "Post-Review Verification",
                    Icon = "🛡️",
                    SequenceOrder = 6,
                    Kind = WorkflowStageKind.Deterministic,
                    CsharpImplementationType = "deterministic-composite",
                    ConfigurationJson = """{"Action": "CompositeVerification", "LoopBackStageId": "implementation", "FailOnError": true, "TimeoutSeconds": 600, "ExtraParameters": {"Stages": "build,test"}}"""
                }
            }
        };

        WorkflowDefinitions.Add(standardWorkflow);
        await SaveChangesAsync(cancellationToken);
    }

    return standardWorkflow;
}
```

### 3.2 Startup Hook in `src/NightCodeForge.Web/Program.cs`
Executed when initializing a clean database:
```csharp
await dbContext.EnsureDatabaseCreatedAndWalAsync();
await dbContext.EnsureDefaultGlobalAgentProfilesAsync(defaultModel);
await dbContext.EnsureDefaultWorkflowsAsync(); // <-- Seed standard workflow & stages
```

---

## 4. Verification Criteria
1. When starting an empty database, `WorkflowDefinitions` contains the standard workflow with 6 ordered stages, each with its designated `Icon`.
2. Re-running the application does not duplicate the workflow or stages.
3. Seeding completes in < 50ms during startup.
