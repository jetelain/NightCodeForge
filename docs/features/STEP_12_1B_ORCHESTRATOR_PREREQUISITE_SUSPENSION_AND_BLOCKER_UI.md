# 🔗 Step 12.1B: Dynamic Task Suspension on Unclosed Prerequisites & UI Blocker Badges

> **Feature Implementation Step (Part 2 of 2 — Orchestrator Suspension & Web UI Layer)**  
> *Parent Plan:* [Features & Integrations Roadmap](README.md)  
> *Prerequisite Step:* [Step 12.1A: Backlog Context Digest & In-Tool Cycle Rejection](../archive/phase-12/STEP_12_1A_BACKLOG_CONTEXT_DIGEST_AND_IN_TOOL_CYCLE_REJECTION.md)  
> *Target Components:* `src/NightCodeForge.Core/Workflow`, `src/NightCodeForge.Infrastructure/Git`, `src/NightCodeForge.Web/Components/Backlog`, `src/NightCodeForge.Web/Services` | *Status:* Planned | *Target Framework:* .NET 10 (C# 13)  
> *Aligns With:* [AGENTS.md](../../AGENTS.md), [docs/PLAN.md](../PLAN.md)

---

## 1. Executive Objective

When a task begins execution at Stage 1 (Task Review), the scheduler dispatched it because it had zero unsatisfied prerequisites at that moment. However, with [Step 12.1A](../archive/phase-12/STEP_12_1A_BACKLOG_CONTEXT_DIGEST_AND_IN_TOOL_CYCLE_REJECTION.md), the Stage 1 Task Review agent can discover and declare a dependency on another unmerged task in the backlog.

This step (Part 2 of Step 12.1) implements:
1. **Dynamic Task Suspension, Resource Cleanup & Clean Workflow Reset:**
   - If an unclosed prerequisite is detected after Stage 1 completes, [`TaskOrchestrator`](../../src/NightCodeForge.Core/Workflow/TaskOrchestrator.cs) executes an immediate early return: suspends execution, sets `task.Status = TaskOverallStatus.Open`, resets `task.CurrentStageId = StandardStageIds.TaskReview`, and records `cohortExitReason = CohortExitReason.Yielded` (or `Suspended`).
   - Clean up execution resources: remove the task worktree via [`RemoveWorktreeAsync`](../../src/NightCodeForge.Infrastructure/Git/GitWorkspaceManager.cs#L311-L325), clean up the container via `CleanupTaskContainerAsync`, and **delete or reset the feature branch** `feature/task-{taskId}` so that when the prerequisite merges, the task is re-branched cleanly from the updated base branch head.
   - Once all prerequisites reach `Closed` (merged), the task is automatically re-enqueued via DAG cascade to rerun from Stage 1 against the updated codebase.
2. **Surface Dependencies & Suspension Clearly in UI (Presentation Separation):**
   - Encapsulate blocker resolution and suspension logic in a dedicated presentation service (`TaskDependencyPresentationHelper` in `NightCodeForge.Web.Services`, per [AGENTS.md](../../AGENTS.md)), keeping [`KanbanCard.razor`](../../src/NightCodeForge.Web/Components/Backlog/KanbanCard.razor) and [`TaskDetailDrawer.razor`](../../src/NightCodeForge.Web/Components/Backlog/TaskDetailDrawer.razor) strictly focused on markup and user interaction.
   - Display clickable dependency badges ("Depends on #12", "Blocks #15") and distinctive `⏸️ Blocked on #101` suspension indicators.

---

## 2. Dynamic Task Suspension & Full Workflow Reset Protocol

### 2.1 Rationale for Suspension & Full Reset
If the Task Review agent uncovers an unmerged prerequisite task (e.g. #101) that is currently `Open`, `Reviewed`, `Implemented`, or `Ready`:
- **Implementation Cannot Proceed:** Stage 2 / Stage 3 agents cannot write implementation code or tests against APIs, database tables, or contracts that only exist in an unmerged branch or proposal.
- **Workflow Must Reset to Stage 1:** When prerequisite #101 is eventually completed and merged into the base branch, the codebase will have changed (new files, modified types, new commits). The review specifications, architectural design, and affected file list established *before* #101 merged are likely stale or invalid. Therefore, the task must **reset to Stage 1** and delete its stale `feature/task-{id}` branch so that once unblocked, the review agent re-investigates the task against the actual merged code on disk.

### 2.2 Orchestration Flow in `TaskOrchestrator`
Upon completion of Stage 1 (`StandardStageIds.TaskReview`) and post-stage disk synchronization:
1. **Unclosed Prerequisite Audit:**
   - [`TaskOrchestrator`](../../src/NightCodeForge.Core/Workflow/TaskOrchestrator.cs) reloads the task's dependencies from the database and checks whether all prerequisite tasks are in `TaskOverallStatus.Closed`:
     ```csharp
     var unclosedPrerequisites = task.Dependencies
         .Where(d => d.DependsOnTask == null || d.DependsOnTask.Status != TaskOverallStatus.Closed)
         .Select(d => d.DependsOnTaskId)
         .ToList();
     ```
2. **Suspension, Resource Cleanup & State Reset Action:**
   - If `unclosedPrerequisites.Count > 0`:
     - **Status Reset:** `task.Status = TaskOverallStatus.Open`.
     - **Stage Reset:** `task.CurrentStageId = StandardStageIds.TaskReview`.
     - **Early Return:** Exit `ProcessTaskAsync` immediately. Do **not** evaluate `pipeline.GetNextStage(...)` (which would otherwise advance to Gate 1 / Implementation). Set `cohortExitReason = CohortExitReason.Yielded` (or dedicated `Suspended` exit reason).
     - **Worktree, Container & Branch Cleanup:**
       1. Remove worktree: `await _gitWorkspaceManager.RemoveWorktreeAsync(workspace, task.Id, cancellationToken)`.
       2. Clean up task container: `await _environmentProvider.CleanupTaskContainerAsync(workspace, task.Id, cancellationToken)`.
       3. **Delete/Reset Task Branch:** Invoke `_gitWorkspaceManager.DeleteTaskBranchAsync(workspace, task.Id, cancellationToken)` (or execute `git branch -D feature/task-{task.Id}`) in the canonical clone. This guarantees that when the prerequisite merges and unblocks this task, `EnsureWorktreeAsync` will provision a **brand-new branch created directly off the freshly updated base branch head**, avoiding the stale branch trap.
     - **Audit Logging:** Save task with `reason: $"Task suspended: waiting for unclosed prerequisites [{string.Join(", ", unclosedPrerequisites)}]; workflow reset to Open; worktree and stale branch cleaned up."` and actor `AuditActors.System`.
     - **Dependency Store Sync:** Notify `_dependencyStore.UpdateTaskStatusAsync(task.Id, task.Status)`.
     - **Execution Log:** Log an informational entry: `Task #{task.Id} suspended due to unclosed dependencies [{string.Join(", ", unclosedPrerequisites)}]. Worktree and branch removed; workflow will rerun from Stage 1 once prerequisites are Closed.`
     - Gracefully finish the current Hangfire job without error.
3. **Automatic Resumption via DAG Cascade:**
   - When the prerequisite tasks reach `TaskOverallStatus.Closed`, `TaskOrchestrator.CompleteMergeAsync` executes its DAG cascade unblocking check via `_dependencyEngine.ResolveCascadeUnblockedTasksAsync(task.Id)` and automatically re-enqueues the unblocked task into the Hangfire queue (`_jobQueue.EnqueueStage(unblockedTask.Id, initialStage.QueueTier, unblockedTask.Priority)`).

---

## 3. UI Presentation: Interactive Dependency Badges & Suspension States

### 3.1 Presentation Separation Architecture
In accordance with [AGENTS.md](../../AGENTS.md) (*Keep Razor components focused on presentation, layout, and user interaction*), all dependency status evaluation, blocker calculation, and badge styling logic is extracted into a dedicated helper service:
- Implement `TaskDependencyPresentationHelper` in `NightCodeForge.Web.Services`.
- Exposes pure, unit-testable methods:
  - `IsSuspended(ForgeTask task, IReadOnlyList<int> prerequisiteIds, IReadOnlyList<ForgeTask> allTasks)`
  - `GetUnclosedBlockers(IReadOnlyList<int> prerequisiteIds, IReadOnlyList<ForgeTask> allTasks)`
  - `FormatBlockerTooltip(...)`

### 3.2 Kanban Cards ([`KanbanCard.razor`](../../src/NightCodeForge.Web/Components/Backlog/KanbanCard.razor))
- Shows interactive chips:
  - 🔗 `Prereq: #101` (hover shows title and status; clicking navigates to or opens task drawer for #101).
  - ⚠️ Amber indicator if an upstream prerequisite failed or is blocked.
  - ⏸️ **Suspended Badge (`⏸️ Blocked on #101`):** When a task is in `Status == TaskOverallStatus.Open` but has unclosed dependencies, render a distinctive amber/slate suspended pill so developers immediately understand why the card is not currently executing or enqueued in DevQueue.

### 3.3 Task Detail Drawer ([`TaskDetailDrawer.razor`](../../src/NightCodeForge.Web/Components/Backlog/TaskDetailDrawer.razor))
- Displays "Dependencies & Blockers" card:
  - Lists **Prerequisite Tasks** (tasks this task depends on) with status pills and completion state.
  - If any prerequisite is unclosed, displays an alert banner: `⏸️ Task execution is suspended awaiting completion of #101. Worktree will automatically initialize when unblocked.`
  - Lists **Downstream Blocked Tasks** (tasks waiting on this task).
  - Quick **Add/Remove Dependency** button linking to [`DependencyLinkingModal.razor`](../../src/NightCodeForge.Web/Components/Backlog/DependencyLinkingModal.razor).

---

## 4. Implementation Checklist & Deliverables

- [ ] **Dynamic Task Suspension, Branch Cleanup & Workflow Reset:**
  - [ ] Update [`TaskOrchestrator`](../../src/NightCodeForge.Core/Workflow/TaskOrchestrator.cs) upon Stage 1 completion: if newly added dependencies are not `Closed`, suspend execution, reset `task.Status = TaskOverallStatus.Open`, reset `task.CurrentStageId = StandardStageIds.TaskReview`, and execute an immediate early return from `ProcessTaskAsync` without routing to Stage 2.
  - [ ] Remove task worktree (`RemoveWorktreeAsync`), clean up container (`CleanupTaskContainerAsync`), and delete or reset feature branch `feature/task-{task.Id}` via [`GitWorkspaceManager`](../../src/NightCodeForge.Infrastructure/Git/GitWorkspaceManager.cs).
  - [ ] Verify that the existing DAG unblocking cascade in `TaskOrchestrator.CompleteMergeAsync` re-enqueues the task to rerun from Stage 1 with a fresh worktree branched off the updated base branch once prerequisites reach `Closed`.
- [ ] **UI Dependency Presentation Helper & Enhancements:**
  - [ ] Create `TaskDependencyPresentationHelper` in `NightCodeForge.Web.Services` for blocker and suspension state calculations.
  - [ ] Render prerequisite/downstream chips and `⏸️ Blocked on #X` pill in [`KanbanCard.razor`](../../src/NightCodeForge.Web/Components/Backlog/KanbanCard.razor).
  - [ ] Add dependency section with suspension banner and link modal in [`TaskDetailDrawer.razor`](../../src/NightCodeForge.Web/Components/Backlog/TaskDetailDrawer.razor).
- [ ] **Tests:**
  - [ ] Unit tests for `TaskDependencyPresentationHelper`.
  - [ ] Unit test verifying task suspension and reset: Stage 1 adding unclosed dependency resets task to `Open` / `TaskReview`, deletes stale feature branch, and halts progression.
  - [ ] Integration test verifying that closing the prerequisite task cascades to re-enqueue the suspended task to execute Stage 1 with a clean branch off the updated base branch.

