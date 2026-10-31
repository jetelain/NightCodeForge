# 📋 Step 12.13A: Ideation Backlog Mutation Tools & Zero-Surprise Prompt Policy (Backend & Agents)

> **Feature Implementation Step (Part 1 of 2 — Agent Tools, Domain Guards & Prompt Engineering)**  
> *Parent Plan:* [Features & Integrations Roadmap](README.md)  
> *Companion Step:* [Step 12.13B: "Refine in Ideation" Workflow & Mutation Tool Cards (Web UI)](STEP_12_13B_REFINE_IN_IDEATION_WORKFLOW_AND_MUTATION_UI.md)  
> *Related Steps:* [Step 11.7: Task State History & Audit Trail](../archive/phase-11/STEP_11_7_TASK_STATE_HISTORY_AND_AUDIT_TRAIL.md) | [Step 12.1A: Backlog Context Digest & Cycle Rejection](STEP_12_1A_BACKLOG_CONTEXT_DIGEST_AND_IN_TOOL_CYCLE_REJECTION.md) | [Step 12.4: Ideation Quick Notes & Suggestions](STEP_12_4_IDEATION_QUICK_NOTES_SUGGESTIONS_AND_DRAFT_PRUNING.md)  
> *Target Components:* `src/NightCodeForge.Core`, `src/NightCodeForge.Agents/Ideation`, `src/NightCodeForge.Agents/Tools`, `skills/ncf-backlog`, `tests/NightCodeForge.Agents.Tests` | *Status:* Planned | *Target Framework:* .NET 10 (C# 13)  
> *Aligns With:* [AGENTS.md](../../AGENTS.md), [docs/PLAN.md](../PLAN.md)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Objective

To maintain strict system safety and prevent code corruption outside of formal workflow pipelines, the Ideation Agent operates within a **strictly read-only workspace** (`isReadOnly = true`). It cannot invoke file-modifying tools (`edit`, `write`) on repository source files or directly edit `docs/tasks/task-{id}.md`.

This step (Part 1 of Step 12.13) equips the Ideation Agent with **scoped, host-level backlog management tools** and enforces the **Upfront Functional & Business Ambiguity Resolution ("Zero-Surprise Scheduling")** policy:
1. **Scoped Host-Level Mutation Tools via `IBacklogMutationToolService` & `CanonicalTools`:**
   - `get_task(task_id)`: Fetches complete task details on demand without bloating the bootstrap prompt.
   - `edit_task(task_id, title?, description?, priority?, depends_on_ids?, merge_mode?, base_branch?)`: Updates unstarted tasks (`TaskOverallStatus.Suggested` or `TaskOverallStatus.Open` with `!task.HasExecutionRun` and not locked in `IWorkspaceLockProvider`), performs circular dependency validation, logs audit snapshots attributed to `AuditActors.IdeationAgent`, and triggers host-level markdown disk synchronization.
   - `set_task_priority(task_id, priority)` & `rework_priorities(updates)`: Updates priority tiers across one or multiple unstarted backlog items.
   - `remove_task(task_id, reason)`: Safely deletes or cancels unstarted tasks with active-dependent cascade guards.
2. **Upfront Ambiguity Resolution ("Zero-Surprise Scheduling"):**
   - The Ideation Agent **must** conduct an interactive interview with the maintainer to resolve all open **functional and business questions** before proposing or updating a task.
   - Ideation is **not** mandated to identify all technical impacts (such as exhaustive affected file lists); detailed technical scoping and symbol mapping belong to downstream **Stage 1 (ForgeTask Review)**.
   - Update [`skills/ncf-backlog/SKILL.md`](../../skills/ncf-backlog/SKILL.md) to align external AI harnesses with this policy.

*(The Web UI "💡 Refine in Ideation" button, `/w/{workspaceId}/ideation?taskId={id}` session seeding, and mutation tool call cards are specified in [Step 12.13B](STEP_12_13B_REFINE_IN_IDEATION_WORKFLOW_AND_MUTATION_UI.md).)*

---

## 2. Critical Lifecycle & Safety Invariants

1. **Workspace Filesystem Read-Only Protection:**
   File modification tools (`edit`, `write`) remain disabled in ideation. Task mutations occur strictly through structured domain tools executing host-side database transactions and [`ITaskMarkdownSyncService.SyncTaskToDiskAsync`](../../src/NightCodeForge.Core/Services/ITaskMarkdownSyncService.cs).
2. **Lifecycle State Guardrails (Exact `TaskOverallStatus` & Execution Flags):**
   - **Permitted States:** Tasks where `task.Status is TaskOverallStatus.Suggested or TaskOverallStatus.Open`, `!task.HasExecutionRun`, and `!workspaceLockProvider.IsTaskLocked(task.WorkspaceId, task.Id)` may be edited, reprioritized, or removed.
   - **Forbidden States:** Tasks that have already entered or completed pipeline execution (`task.HasExecutionRun == true`, `workspaceLockProvider.IsTaskLocked(task.WorkspaceId, task.Id) == true`, or `task.Status is TaskOverallStatus.Reviewed or TaskOverallStatus.Implemented or TaskOverallStatus.Ready or TaskOverallStatus.Closed or TaskOverallStatus.Rejected or TaskOverallStatus.Cancelled`) **must never** be mutated by ideation chat. Attempting to edit or remove them throws a dedicated domain exception (`TaskCannotBeModifiedException`).
3. **Deterministic Failure & No Silent Failures:**
   Looking up or modifying a non-existent task (or a task belonging to a different `WorkspaceId`) throws `TaskNotFoundException` immediately.
4. **Attribution & Audit Trail:**
   All mutations performed by the ideation agent record an explicit entry in `ForgeTaskHistory` (`TaskHistories` table) with `ChangedBy = AuditActors.IdeationAgent` and a descriptive `ChangeReason`.
5. **Constructor Parameter Ceiling (`AGENTS.md` — $\le 10$ Parameters):**
   Do **not** add `ITaskRepository`, `IDependencyResolutionEngine`, `ITaskMarkdownSyncService`, or `IWorkspaceLockProvider` to `CanonicalTools`'s constructor. Instead, encapsulate the 5 host-level backlog tools inside a cohesive collaborator `IBacklogMutationToolService` (`BacklogMutationToolService` in `NightCodeForge.Agents.Tools`, strictly $\le 6$ required constructor parameters: `ITaskRepository`, `IDependencyResolutionEngine`, `ITaskMarkdownSyncService`, `IWorkspaceLockProvider`, `IAgentActivityStreamer`, `ILogger<BacklogMutationToolService>`) and attach it to `CanonicalTools` via `public IBacklogMutationToolService? BacklogMutationService { get; set; }` (matching the existing `AskHandler` and `RequestChangesHandler` property pattern).

---

## 3. Scoped Agent Tools Specification

Registered in [`CanonicalAIFunctionFactory.cs`](../../src/NightCodeForge.Agents/Tools/CanonicalAIFunctionFactory.cs), added to [`DefaultAgentTools.All`](../../src/NightCodeForge.Core/Domain/DefaultAgentTools.cs) and `DefaultAgentTools.ReadOnly`, and reconciled onto existing `ideation-brainstormer` profiles in [`AppDbContext.EnsureDefaultGlobalAgentProfilesAsync`](../../src/NightCodeForge.Infrastructure/Data/AppDbContext.cs):

### 3.1 `get_task`
- **Parameters:** `taskId` (integer, required).
- **Behavior:** Queries `ITaskRepository` for the task and its dependencies within the current `_workspaceId`. Throws `TaskNotFoundException` if missing or in another workspace. Returns JSON containing `id`, `title`, `description`, `status`, `hasExecutionRun`, `priority`, `mergeMode`, `baseBranch`, `prerequisites`, and `dependentTaskIds`.

### 3.2 `edit_task`
- **Parameters:** `taskId` (int, required), `title` (string?), `description` (string?), `priority` (int?), `dependsOnIds` (int[]?), `mergeMode` (string?), `baseBranch` (string?).
- **Behavior:**
  - Verifies target task exists in `_workspaceId` and is in an editable unstarted state (`Status is Suggested or Open`, `!HasExecutionRun`, and not locked in `IWorkspaceLockProvider`); otherwise throws `TaskCannotBeModifiedException`.
  - If `dependsOnIds` is provided, validates DAG acyclicity via `IDependencyResolutionEngine.HasCycle`; throws `CircularDependencyException` on cycles.
  - Updates the [`ForgeTask`](../../src/NightCodeForge.Core/Domain/ForgeTask.cs) record in SQLite, records an audit snapshot with `AuditActors.IdeationAgent`, syncs `docs/tasks/task-{id}.md` via `ITaskMarkdownSyncService`, and broadcasts an `AgentActivityEvent`.

### 3.3 `set_task_priority` & `rework_priorities`
- **Parameters (`set_task_priority`):** `taskId` (int, required), `priority` (int, required: `1` Critical to `5` Trivial).
- **Parameters (`rework_priorities`):** `updates` (array of `{ taskId: int, priority: int }`).
- **Behavior:** Validates editable unstarted state and priority values (`1..5`), updates SQLite with `AuditActors.IdeationAgent` history snapshots, re-syncs markdown files for affected tasks, and returns a confirmation summary.

### 3.4 `remove_task`
- **Parameters:** `taskId` (int, required), `reason` (string?).
- **Behavior:**
  - Verifies task exists in `_workspaceId` and is in an editable unstarted state (`Status is Suggested or Open`, `!HasExecutionRun`, and not locked in `IWorkspaceLockProvider`); otherwise throws `TaskCannotBeModifiedException`.
  - Checks if other non-cancelled/non-rejected tasks depend on `taskId`. If active dependent tasks exist, fails fast with a descriptive error listing the dependent task IDs.
  - Marks `ForgeTask` as `TaskOverallStatus.Cancelled` (recording a `ForgeTaskHistory` entry with `AuditActors.IdeationAgent` and `reason`), detaches dependencies, and syncs/updates `docs/tasks/` markdown state.

---

## 4. Prompt Engineering & `skills/ncf-backlog` Alignment

### 4.1 Updated Base Persona Guidance (`AgentProfile.CreateDefaultIdeationProfile`)
```csharp
Avoid over-decomposing into micro-tasks; structure cohesive PR-sized tasks with concrete functional scope. Focus on functional and business requirements: solve all user-facing, behavioral, and acceptance criteria questions with the maintainer in chat before formulating tasks so that tasks enter the execution queue fully determined. You are not mandated to identify all technical impacts (e.g. files); downstream review stages handle technical scoping.
```

### 4.2 Updated `<task_planning_guidelines>` & `<backlog_management_guidelines>` in `IdeationService.cs`
- Replace the clause allowing open questions in proposed tasks with **Mandatory Upfront Functional & Business Resolution (Zero Open Business Questions)**:
  - Conduct an interactive interview to resolve user problems, expected behaviors, business rules, edge cases, and acceptance criteria before invoking `propose_tasks` or `edit_task`.
  - Clarify that exhaustive file impact lists are **not** required in ideation (handled by Stage 1 Review).
- Inject `<backlog_management_guidelines>` instructing the agent when and how to invoke `get_task`, `edit_task`, `set_task_priority`, `rework_priorities`, and `remove_task`.

### 4.3 Provided Skills Update (`skills/ncf-backlog/SKILL.md`)
- Update [`skills/ncf-backlog/SKILL.md`](../../skills/ncf-backlog/SKILL.md) to mandate upfront functional/business interviews, clarify that exhaustive file mapping is not mandated in ideation, forbid lazy "Open Questions" / "TBD" sections in authored task markdown files, and document the existing-task refinement workflow.

---

## 5. Implementation Checklist & Deliverables

- [ ] **Domain Exceptions & Interfaces (`NightCodeForge.Core`):**
  - [ ] Define `TaskCannotBeModifiedException` and `AuditActors.IdeationAgent`.
  - [ ] Add `GetByIdWithDependenciesAsync`, `UpdateTaskSpecificationAsync`, and `BatchUpdatePrioritiesAsync` to `ITaskRepository` / `ITaskService` as needed.
- [ ] **Agent Tools Implementation (`NightCodeForge.Agents`):**
  - [ ] Implement `IBacklogMutationToolService` (`BacklogMutationToolService`, $\le 6$ constructor parameters), attach `BacklogMutationService` property on `CanonicalTools`, and register `get_task`, `edit_task`, `set_task_priority`, `rework_priorities`, and `remove_task` in `CanonicalAIFunctionFactory`, `DefaultAgentTools.All`, `DefaultAgentTools.ReadOnly`, and `AppDbContext.EnsureDefaultGlobalAgentProfilesAsync`.
  - [ ] Enforce state guards (`TaskCannotBeModifiedException` when `HasExecutionRun == true`, task is locked in `IWorkspaceLockProvider`, or `Status` is not `Suggested`/`Open`), DAG cycle checks (`CircularDependencyException`), audit trail logging (`AuditActors.IdeationAgent`), and `ITaskMarkdownSyncService.SyncTaskToDiskAsync`.
- [ ] **Prompt Engineering & Skills (`NightCodeForge.Agents`, `NightCodeForge.Core`, `skills/ncf-backlog`):**
  - [ ] Update `AgentProfile.CreateDefaultIdeationProfile()` and `IdeationService.cs` (`<task_planning_guidelines>` and `<backlog_management_guidelines>`).
  - [ ] Update [`skills/ncf-backlog/SKILL.md`](../../skills/ncf-backlog/SKILL.md).
- [ ] **Automated Tests (`tests/NightCodeForge.Agents.Tests`):**
  - [ ] Unit tests for `get_task`, `edit_task`, `set_task_priority`, `rework_priorities`, and `remove_task` (covering happy paths, `TaskNotFoundException`, `TaskCannotBeModifiedException` on `InProgress`/`Closed` tasks, `CircularDependencyException`, active dependent guard, and markdown sync invocation).
  - [ ] Unit test verifying `IdeationService` system prompt contains upfront ambiguity resolution and backlog management guidelines.

