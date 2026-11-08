# 🔗 Step 12.1B: Dynamic Task Suspension on Unclosed Prerequisites & UI Blocker Badges

> **Feature Implementation Step (Part 2 of 2 — Orchestrator Suspension & Web UI Layer)**  
> *Parent Plan:* [Features & Integrations Roadmap](README.md)  
> *Prerequisite Step:* [Step 12.1A: Backlog Context Digest & In-Tool Cycle Rejection](../archive/phase-12/STEP_12_1A_BACKLOG_CONTEXT_DIGEST_AND_IN_TOOL_CYCLE_REJECTION.md)  
> *Target Components:* `src/NightCodeForge.Core/Workflow`, `src/NightCodeForge.Core/Interfaces`, `src/NightCodeForge.Infrastructure/Git`, `src/NightCodeForge.Web/Components/Backlog`, `src/NightCodeForge.Web/Components/Pages`, `src/NightCodeForge.Web/Services` | *Status:* Planned | *Target Framework:* .NET 10 (C# 13)  
> *Aligns With:* [AGENTS.md](../../AGENTS.md), [docs/PLAN.md](../PLAN.md)

---

## 1. Executive Objective

When a task begins execution at Stage 1 (`StandardStageIds.TaskReview`), the scheduler dispatched it because it had zero unsatisfied prerequisites at that moment. However, with [Step 12.1A](../archive/phase-12/STEP_12_1A_BACKLOG_CONTEXT_DIGEST_AND_IN_TOOL_CYCLE_REJECTION.md), the Stage 1 Task Review agent can discover and declare a dependency on another unmerged task in the backlog (either an existing task or a newly proposed task via `propose_tasks` + `depends_on` frontmatter).

This step (Part 2 of Step 12.1) implements:
1. **Dynamic Task Suspension, Resource Cleanup & Clean Workflow Reset:**
   - If an unclosed prerequisite is detected after Stage 1 completes and markdown changes are synchronized from disk, [`TaskOrchestrator`](../../src/NightCodeForge.Core/Workflow/TaskOrchestrator.cs) executes an immediate early return before creating a Stage 1 checkpoint or advancing to Stage 2:
     - Suspends execution and resets the task to a clean pre-execution `Open` state (`task.Status = TaskOverallStatus.Open`, `task.CurrentStageId = string.Empty`, `task.HasExecutionRun = false`, `task.MaxExecutedStageOrder = 0`, `task.LastHangfireJobId = null`, and clears Stage 1 artifacts from `PipelineDataJson`) so [`BacklogStatusClassifier`](../../src/NightCodeForge.Web/Services/BacklogStatusClassifier.cs), [`KanbanCard.razor`](../../src/NightCodeForge.Web/Components/Backlog/KanbanCard.razor), and [`DagLayoutEngine`](../../src/NightCodeForge.Web/Services/DagLayoutEngine.cs) classify it as **Blocked in Open (Backlog)** rather than **In-Flight**.
     - Records `cohortExitReason = CohortExitReason.Yielded` (or dedicated `Suspended` exit reason).
   - Cleans up execution resources through [`ITaskWorkspacePreparer`](../../src/NightCodeForge.Core/Workflow/ITaskWorkspacePreparer.cs) and [`IGitWorkspaceManager`](../../src/NightCodeForge.Core/Interfaces/IGitWorkspaceManager.cs):
     - Removes the task worktree (`RemoveWorktreeSafeAsync`), cleans up the task container (`CleanupTaskContainerSafeAsync`), and **deletes the local feature branch** `feature/task-{taskId}` (`DeleteTaskBranchAsync` / `DeleteTaskBranchSafeAsync`) so that when prerequisites eventually merge, [`EnsureWorktreeAsync`](../../src/NightCodeForge.Infrastructure/Git/GitWorkspaceManager.cs#L112-L163) provisions a fresh branch off the updated base branch head instead of checking out a stale pre-merge branch.
   - Once all prerequisites reach `Closed` (merged), [`TaskMergeAndCascadeCoordinator.CompleteTaskAndCascadeInternalAsync`](../../src/NightCodeForge.Core/Workflow/TaskMergeAndCascadeCoordinator.cs#L293-L457) automatically re-enqueues the task via DAG cascade to rerun from Stage 1 against the updated codebase.
2. **Surface Dependencies & Suspension Clearly in UI (Presentation Separation):**
   - Extract inline blocker resolution and suspension formatting out of Razor components into a dedicated presentation helper (`TaskDependencyPresentationHelper` in `NightCodeForge.Web.Services`, per [AGENTS.md](../../AGENTS.md)), keeping [`KanbanCard.razor`](../../src/NightCodeForge.Web/Components/Backlog/KanbanCard.razor), [`TaskDetail.razor`](../../src/NightCodeForge.Web/Components/Pages/TaskDetail.razor), and [`TaskDetailDrawer.razor`](../../src/NightCodeForge.Web/Components/Backlog/TaskDetailDrawer.razor) strictly focused on markup and user interaction.
   - Render interactive prerequisite chips (`🔗 Prereq: #101`), upstream failure/stall warnings, distinctive `⏸️ Blocked on #101` suspension badges on Kanban cards, and a suspension alert banner in both the full-page [`TaskDetail.razor`](../../src/NightCodeForge.Web/Components/Pages/TaskDetail.razor) view and [`TaskDetailDrawer.razor`](../../src/NightCodeForge.Web/Components/Backlog/TaskDetailDrawer.razor).

---

## 2. Dynamic Task Suspension & Full Workflow Reset Protocol

### 2.1 Rationale for Suspension & Full Reset
If the Task Review agent uncovers an unmerged prerequisite task (e.g. `#101`) whose `TaskOverallStatus` is not `Closed` (e.g. `Suggested`, `Open`, `Reviewed`, `Implemented`, `Ready`, `Failed`, `Rejected`, or `Cancelled`, including when `IsWaitingForHuman` or `IsDeferredByPause` is true):
- **Implementation Cannot Proceed:** Stage 2 / Stage 3 agents cannot write implementation code or tests against APIs, database tables, or contracts that only exist in an unmerged branch or proposal.
- **Workflow Must Reset Cleanly Before Stage 1 Completion:** When prerequisite `#101` is eventually completed and merged into the base branch, the codebase will have changed (new files, modified types, new commits). The review specifications, architectural design, and affected file list established *before* `#101` merged are likely stale or invalid. Furthermore, [`GitWorkspaceManager.EnsureWorktreeAsync`](../../src/NightCodeForge.Infrastructure/Git/GitWorkspaceManager.cs#L134-L143) reuses an existing `feature/task-{id}` branch if `branchExists` is true. Therefore, the task must **reset its execution state to pre-Stage 1** and **delete its stale `feature/task-{id}` branch** so that once unblocked, the review agent re-investigates the task on a fresh branch created from the updated base branch head.

### 2.2 Collaborator Responsibilities & Constructor Constraints
Per [AGENTS.md](../../AGENTS.md), service constructors must never exceed 10 parameters. [`TaskOrchestrator`](../../src/NightCodeForge.Core/Workflow/TaskOrchestrator.cs#L18-L52) (9 params) delegates workspace/git operations and dependency/merge operations to its existing collaborators rather than injecting new dependencies directly:
- **[`ITaskMergeAndCascadeCoordinator`](../../src/NightCodeForge.Core/Workflow/ITaskMergeAndCascadeCoordinator.cs) / [`TaskMergeAndCascadeCoordinator`](../../src/NightCodeForge.Core/Workflow/TaskMergeAndCascadeCoordinator.cs)** (10 params — already owns `_dependencyEngine`, `_dependencyStore`, `_taskRepository`, and `_jobQueue`):
  - Add `Task<IReadOnlyList<int>> GetUnclosedPrerequisiteIdsAsync(int taskId, CancellationToken cancellationToken = default)`.
  - **Important EF Core & In-Memory Store Constraint:** Do **not** rely on `task.Dependencies.Where(d => d.DependsOnTask == null || ...)` because [`EfCoreTaskRepository`](../../src/NightCodeForge.Infrastructure/Data/Repositories/EfCoreTaskRepository.cs#L30-L47) does not `.ThenInclude(d => d.DependsOnTask)` and [`TaskMarkdownSyncService.SyncAllTasksFromDiskAsync`](../../src/NightCodeForge.Core/Services/TaskMarkdownSyncService.cs#L1656-L1660) populates `new TaskDependency { TaskId = task.Id, DependsOnTaskId = depId }` with `DependsOnTask == null` (which would falsely classify already-`Closed` prerequisites as unclosed). Instead, obtain prerequisite IDs from `_dependencyStore.GetPrerequisiteTaskIdsAsync(taskId)` / `_taskRepository.GetDependenciesAsync(taskId)` and resolve each prerequisite's current `TaskOverallStatus` via `_dependencyStore.GetTaskStatusAsync(prereqId)` with fallback to `_taskRepository.GetStatusAsync(prereqId)`.
  - Add `Task SetTaskDependenciesAsync(int workspaceId, int taskId, IEnumerable<int> prerequisiteTaskIds, CancellationToken cancellationToken = default)` to [`ITaskDependencyStore`](../../src/NightCodeForge.Core/Dependencies/ITaskDependencyStore.cs) (implemented in [`InMemoryTaskDependencyStore`](../../src/NightCodeForge.Core/Dependencies/ITaskDependencyStore.cs) and [`EfCoreTaskDependencyStore`](../../src/NightCodeForge.Infrastructure/Data/Repositories/EfCoreTaskDependencyStore.cs)) so `ITaskDependencyStore` stays synchronized whenever dependencies or newly created tasks are persisted via [`TaskMarkdownSyncService.SyncAllTasksFromDiskAsync`](../../src/NightCodeForge.Core/Services/TaskMarkdownSyncService.cs), [`TaskMutationService.UpdateDependenciesAsync`](../../src/NightCodeForge.Web/Services/TaskMutationService.cs), or Stage 1 `pendingProposals`.
- **[`IGitWorkspaceManager`](../../src/NightCodeForge.Core/Interfaces/IGitWorkspaceManager.cs) / [`GitWorkspaceManager`](../../src/NightCodeForge.Infrastructure/Git/GitWorkspaceManager.cs) & [`ITaskWorkspacePreparer`](../../src/NightCodeForge.Core/Workflow/ITaskWorkspacePreparer.cs) / [`TaskWorkspacePreparer`](../../src/NightCodeForge.Core/Workflow/TaskWorkspacePreparer.cs)**:
  - Add `Task DeleteTaskBranchAsync(Workspace workspace, int taskId, CancellationToken cancellationToken = default)` to `IGitWorkspaceManager` (deletes `feature/task-{taskId}` via `git branch -D` in the canonical clone if the branch exists, after worktree removal).
  - Add `Task DeleteTaskBranchSafeAsync(Workspace workspace, int taskId, string contextDescription, CancellationToken cancellationToken = default)` to `ITaskWorkspacePreparer` (best-effort wrapper matching `RemoveWorktreeSafeAsync(workspace, taskId, contextDescription, cancellationToken)` and `CleanupTaskContainerSafeAsync(workspace, taskId, contextDescription, cancellationToken)`).

### 2.3 Orchestration Flow in `TaskOrchestrator.ProcessTaskAsync`
In [`TaskOrchestrator.ProcessTaskAsync`](../../src/NightCodeForge.Core/Workflow/TaskOrchestrator.cs#L1022-L1200), after Stage 1 (`StandardStageIds.TaskReview`) finishes, `pendingProposals` are persisted (and their statuses registered via `_mergeAndCascadeCoordinator.UpdateTaskStatusInStoreAsync`), and `SyncAllTasksFromDiskAsync` updates `task.Dependencies`:
1. **Unclosed Prerequisite Audit (Before Missing-Doc Check, Clarification Check, or Stage 1 Checkpoint):**
   - When `string.Equals(stage.StageId, StandardStageIds.TaskReview, StringComparison.OrdinalIgnoreCase) && !isStageCancelledOrPaused` and `task.Dependencies.Count > 0`:
     ```csharp
     var unclosedPrerequisites = await _mergeAndCascadeCoordinator
         .GetUnclosedPrerequisiteIdsAsync(task.Id, cancellationToken);
     ```
2. **Suspension, Resource Cleanup & Full State Reset Action:**
   - If `unclosedPrerequisites.Count > 0`:
     - **State Reset (Prevent False "In-Flight" Classification):**
       - `task.Status = TaskOverallStatus.Open`
       - `task.CurrentStageId = string.Empty` (when re-dispatched later, [`TaskOrchestrator`](../../src/NightCodeForge.Core/Workflow/TaskOrchestrator.cs#L270-L277) automatically resolves `pipeline.Stages.FirstOrDefault()`, i.e. `StandardStageIds.TaskReview`)
       - `task.HasExecutionRun = false`
       - `task.MaxExecutedStageOrder = 0`
       - `task.LastHangfireJobId = null`
       - `task.IsWaitingForHuman = false`
       - `task.InterruptedStageId = null`
       - `task.InterruptedAgentProfileId = null`
       - Clear Stage 1 execution artifacts (`ApprovedPlan`, `StageSummary(TaskReview)`, `SerializedMemory`, etc.) by resetting `task.PipelineDataJson = "{}"`.
     - **Persistence & Host Markdown Sync (Without Pushing Stale Branch):**
       - Save task with audit history via `_taskRepository.UpdateWithHistoryAsync(task, dependsOn: null, changedBy: AuditActors.TaskOrchestrator, reason: $"Task suspended: waiting for unclosed prerequisites [{string.Join(", ", unclosedPrerequisites)}]; workflow reset to Open; worktree and stale branch cleaned up.", cancellationToken: cancellationToken)`.
       - Update dependency store state via `await _mergeAndCascadeCoordinator.UpdateTaskStatusInStoreAsync(task.Id, task.Status, cancellationToken)` and `await _mergeAndCascadeCoordinator.SetWaitingForHumanInStoreAsync(task.Id, false, cancellationToken)`.
       - Synchronize badge counts via `await TaskBadgeCounter.SynchronizeBadgeCountsAsync(_taskRepository, _telemetryService, cancellationToken)`.
       - Synchronize updated frontmatter (`status: Open`, `current_stage: ""`, while keeping `depends_on`) back to the host markdown file via `await _workspacePreparer.SyncMarkdownTaskSafeAsync(workspace, task, cancellationToken)` (do **not** call `SyncTaskMarkdownAndMirrorAsync`, which would push the soon-to-be-deleted `feature/task-{id}` branch to `host-origin`).
     - **Worktree, Container & Branch Cleanup:**
       1. Remove worktree: `await _workspacePreparer.RemoveWorktreeSafeAsync(workspace, task.Id, "suspended", cancellationToken)`.
       2. Clean up task container: `await _workspacePreparer.CleanupTaskContainerSafeAsync(workspace, task.Id, "suspended", cancellationToken)`.
       3. Delete local task branch: `await _workspacePreparer.DeleteTaskBranchSafeAsync(workspace, task.Id, "suspended", cancellationToken)`.
     - **Execution Log & Activity Stream:**
       - Append an informational `ExecutionLog` via `_taskRepository.AddExecutionLogAsync` and broadcast via `_streamer.BroadcastAsync`: `Task #{task.Id} suspended due to unclosed dependencies [{string.Join(", ", unclosedPrerequisites)}]. Worktree and branch removed; workflow will rerun from Stage 1 once prerequisites are Closed.`
       - Set `cohortExitReason = CohortExitReason.Yielded` and return early from `ProcessTaskAsync` without creating a Stage 1 `StageCheckpoint` or advancing to Stage 2.
3. **Automatic Resumption via DAG Cascade (and Manual UI Unblocking):**
   - When the prerequisite tasks reach `TaskOverallStatus.Closed`, [`TaskMergeAndCascadeCoordinator.CompleteTaskAndCascadeInternalAsync`](../../src/NightCodeForge.Core/Workflow/TaskMergeAndCascadeCoordinator.cs#L420-L451) executes its DAG cascade unblocking check via `_dependencyEngine.ResolveCascadeUnblockedTasksAsync(task.Id)` and automatically re-enqueues the unblocked task into the Hangfire queue (`_jobQueue.EnqueueStage(unblockedTask.Id, initialStage.QueueTier, unblockedTask.Priority)`).
   - Additionally, if a user manually edits dependencies via [`TaskMutationService.UpdateDependenciesAsync`](../../src/NightCodeForge.Web/Services/TaskMutationService.cs#L246-L285), keep `ITaskDependencyStore` synchronized via `SetTaskDependenciesAsync` so the dependency graph reflects manual additions/removals immediately.

---

## 3. UI Presentation: Interactive Dependency Badges & Suspension States

### 3.1 Presentation Separation Architecture
In accordance with [AGENTS.md](../../AGENTS.md) (*Keep Razor components focused on presentation, layout, and user interaction*), all dependency status evaluation, blocker calculation, and badge/banner formatting logic is extracted into a dedicated helper service:
- Implement `TaskDependencyPresentationHelper` in `NightCodeForge.Web.Services`.
- Exposes pure, unit-testable methods (accepting explicit `IReadOnlyList<int>? prerequisiteIds` with fallback to `task.Dependencies` so [`KanbanCard.razor`](../../src/NightCodeForge.Web/Components/Backlog/KanbanCard.razor), [`TaskDetailDrawer.razor`](../../src/NightCodeForge.Web/Components/Backlog/TaskDetailDrawer.razor), and [`TaskDetail.razor`](../../src/NightCodeForge.Web/Components/Pages/TaskDetail.razor) can pass their loaded prerequisite ID lists directly):
  - `GetUnclosedBlockers(ForgeTask task, IReadOnlyList<ForgeTask> allTasks, IReadOnlyList<int>? prerequisiteIds = null)`
  - `IsSuspendedByBlockers(ForgeTask task, IReadOnlyList<ForgeTask> allTasks, IReadOnlyList<int>? prerequisiteIds = null)`
  - `IsStalledOrFailedBlocker(ForgeTask blocker)` / `HasStalledOrFailedBlocker(ForgeTask task, IReadOnlyList<ForgeTask> allTasks, IReadOnlyList<int>? prerequisiteIds = null)` (true when any unclosed prerequisite has `Status is TaskOverallStatus.Failed or TaskOverallStatus.Rejected or TaskOverallStatus.Cancelled`, `IsWaitingForHuman == true`, or `IsDeferredByPause == true`)
  - `FormatSuspendedBadgeText(IReadOnlyList<ForgeTask> unclosedBlockers)` (e.g., `"⏸️ Blocked on #101"` or `"⏸️ Blocked on #101, #102"`)
  - `FormatSuspensionBannerText(IReadOnlyList<ForgeTask> unclosedBlockers)`
- Harden [`BacklogStatusClassifier`](../../src/NightCodeForge.Web/Services/BacklogStatusClassifier.cs) and [`DagLayoutEngine`](../../src/NightCodeForge.Web/Services/DagLayoutEngine.cs) so that an `Open` task with unclosed prerequisites is always classified in the `Open` column / `Blocked` DAG state and never misclassified as `InFlight`.

### 3.2 Kanban Cards ([`KanbanCard.razor`](../../src/NightCodeForge.Web/Components/Backlog/KanbanCard.razor))
- Replace inline `GetBlockers()` logic in `@code` with `TaskDependencyPresentationHelper`.
- Render interactive prerequisite and blocker indicators:
  - ⏸️ **Suspended Badge (`⏸️ Blocked on #101`):** When a task is `Open` with unclosed prerequisites, render a distinctive suspended pill naming the blocking task ID(s) so developers immediately understand why the card is waiting in Backlog.
  - ⚠️ **Upstream Warning Indicator:** Highlight in amber/red if an upstream prerequisite has `Status is TaskOverallStatus.Failed or TaskOverallStatus.Rejected or TaskOverallStatus.Cancelled`, `IsWaitingForHuman`, or `IsDeferredByPause`.
  - 🔗 **Interactive Prerequisite Chips (`🔗 Prereq: #101`):** Clicking a prerequisite chip opens or navigates to that prerequisite task.

### 3.3 Task Detail Page ([`TaskDetail.razor`](../../src/NightCodeForge.Web/Components/Pages/TaskDetail.razor)) & Task Detail Drawer ([`TaskDetailDrawer.razor`](../../src/NightCodeForge.Web/Components/Backlog/TaskDetailDrawer.razor))
Both the full-page [`TaskDetail.razor`](../../src/NightCodeForge.Web/Components/Pages/TaskDetail.razor) view (used when navigating from [`Backlog.razor`](../../src/NightCodeForge.Web/Components/Pages/Backlog.razor) and [`Tasks.razor`](../../src/NightCodeForge.Web/Components/Pages/Tasks.razor)) and [`TaskDetailDrawer.razor`](../../src/NightCodeForge.Web/Components/Backlog/TaskDetailDrawer.razor) already render "Prerequisites" and "Downstream Dependents" sections and open [`DependencyLinkingModal.razor`](../../src/NightCodeForge.Web/Components/Backlog/DependencyLinkingModal.razor). Update both components to:
- Use `TaskDependencyPresentationHelper` for prerequisite/dependent status resolution.
- Display a prominent **Suspension Alert Banner** whenever the task is `Open` and has unclosed prerequisites:
  - `⏸️ Task execution is suspended awaiting completion of #101. Worktree will automatically initialize from the updated base branch when unblocked.`
  - Include an additional warning callout if any blocking prerequisite has `Status is TaskOverallStatus.Failed or TaskOverallStatus.Rejected or TaskOverallStatus.Cancelled`, `IsWaitingForHuman`, or `IsDeferredByPause`.

---

## 4. Implementation Checklist & Deliverables

- [ ] **Git & Workspace Preparer Branch Deletion:**
  - [ ] Add `DeleteTaskBranchAsync(Workspace workspace, int taskId, CancellationToken cancellationToken = default)` to [`IGitWorkspaceManager`](../../src/NightCodeForge.Core/Interfaces/IGitWorkspaceManager.cs) and implement it in [`GitWorkspaceManager`](../../src/NightCodeForge.Infrastructure/Git/GitWorkspaceManager.cs) (`git branch -D feature/task-{taskId}` in canonical clone if branch exists).
  - [ ] Add `DeleteTaskBranchSafeAsync(Workspace workspace, int taskId, string contextDescription, CancellationToken cancellationToken = default)` to [`ITaskWorkspacePreparer`](../../src/NightCodeForge.Core/Workflow/ITaskWorkspacePreparer.cs) and [`TaskWorkspacePreparer`](../../src/NightCodeForge.Core/Workflow/TaskWorkspacePreparer.cs).
- [ ] **Dynamic Task Suspension & Clean Workflow Reset:**
  - [ ] Add `GetUnclosedPrerequisiteIdsAsync(int taskId, CancellationToken cancellationToken = default)` to [`ITaskMergeAndCascadeCoordinator`](../../src/NightCodeForge.Core/Workflow/ITaskMergeAndCascadeCoordinator.cs) and [`TaskMergeAndCascadeCoordinator`](../../src/NightCodeForge.Core/Workflow/TaskMergeAndCascadeCoordinator.cs), resolving prerequisite statuses via `_dependencyStore` / `_taskRepository` (never relying on unpopulated `TaskDependency.DependsOnTask` navigation properties).
  - [ ] Add `SetTaskDependenciesAsync` to [`ITaskDependencyStore`](../../src/NightCodeForge.Core/Dependencies/ITaskDependencyStore.cs) and ensure `ITaskDependencyStore` stays synchronized when dependencies or tasks are updated during [`TaskMarkdownSyncService.SyncAllTasksFromDiskAsync`](../../src/NightCodeForge.Core/Services/TaskMarkdownSyncService.cs), [`TaskMutationService.UpdateDependenciesAsync`](../../src/NightCodeForge.Web/Services/TaskMutationService.cs), or Stage 1 proposal persistence.
  - [ ] Update [`TaskOrchestrator.ProcessTaskAsync`](../../src/NightCodeForge.Core/Workflow/TaskOrchestrator.cs) after Stage 1 disk sync: if unclosed prerequisites exist, reset `Status = Open`, `CurrentStageId = string.Empty`, `HasExecutionRun = false`, `MaxExecutedStageOrder = 0`, `LastHangfireJobId = null`, `IsWaitingForHuman = false`, `InterruptedStageId = null`, `InterruptedAgentProfileId = null`, and `PipelineDataJson = "{}"`; persist with history (`UpdateWithHistoryAsync`); sync host markdown (`SyncMarkdownTaskSafeAsync`); remove worktree, container, and `feature/task-{id}` branch; and return early with `CohortExitReason.Yielded`.
  - [ ] Verify that [`TaskMergeAndCascadeCoordinator.CompleteTaskAndCascadeInternalAsync`](../../src/NightCodeForge.Core/Workflow/TaskMergeAndCascadeCoordinator.cs) re-enqueues the suspended task to rerun from Stage 1 on a fresh branch off the updated base branch once all prerequisites reach `Closed`.
- [ ] **UI Dependency Presentation Helper & Enhancements:**
  - [ ] Create `TaskDependencyPresentationHelper` in `NightCodeForge.Web.Services` and update [`BacklogStatusClassifier`](../../src/NightCodeForge.Web/Services/BacklogStatusClassifier.cs) / [`DagLayoutEngine`](../../src/NightCodeForge.Web/Services/DagLayoutEngine.cs) so blocked `Open` tasks are never shown as `InFlight`.
  - [ ] Update [`KanbanCard.razor`](../../src/NightCodeForge.Web/Components/Backlog/KanbanCard.razor) to use `TaskDependencyPresentationHelper`, render `⏸️ Blocked on #X` badges, upstream warning indicators, and interactive `🔗 Prereq: #X` chips.
  - [ ] Update both [`TaskDetail.razor`](../../src/NightCodeForge.Web/Components/Pages/TaskDetail.razor) and [`TaskDetailDrawer.razor`](../../src/NightCodeForge.Web/Components/Backlog/TaskDetailDrawer.razor) to use `TaskDependencyPresentationHelper` and display the suspension alert banner when blocked by unclosed prerequisites.
- [ ] **Tests:**
  - [ ] Unit tests for `TaskDependencyPresentationHelper` and `BacklogStatusClassifier` blocked-task handling.
  - [ ] Unit tests for `TaskOrchestrator` verifying that Stage 1 adding an unclosed dependency resets the task (`Open`, empty `CurrentStageId`, `HasExecutionRun = false`), deletes the stale feature branch, removes the worktree/container, and halts progression before Stage 2.
  - [ ] Integration test verifying that closing the prerequisite task via `TaskMergeAndCascadeCoordinator` cascades to re-enqueue the suspended task to execute Stage 1 with a clean branch off the updated base branch.
  - [ ] bUnit tests for [`KanbanCard.razor`](../../src/NightCodeForge.Web/Components/Backlog/KanbanCard.razor), [`TaskDetail.razor`](../../src/NightCodeForge.Web/Components/Pages/TaskDetail.razor), and [`TaskDetailDrawer.razor`](../../src/NightCodeForge.Web/Components/Backlog/TaskDetailDrawer.razor) blocker badges and suspension alert banner.
