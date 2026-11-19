# ⚡ Step 11.53: Schedule `Open` Task When Blocking Dependencies Are Removed

> **Parent Plan:** [docs/fixes/README.md](README.md)  
> **Target Framework:** .NET 10 (C# 13)  
> **Status:** Completed  
> **Target Projects:** `NightCodeForge.Core`, `NightCodeForge.Infrastructure`, `NightCodeForge.Web`, `NightCodeForge.Agents`, `NightCodeForge.Core.Tests`, `NightCodeForge.Infrastructure.Tests`, `NightCodeForge.Web.Tests`, `NightCodeForge.Agents.Tests`  
> **Aligns With:** `AGENTS.md`, `docs/PLAN.md`

> [!IMPORTANT]
> **Specification Disclaimer:** This document is a behavioral specification. It defines required behaviors, architectural invariants, and verification criteria. Specific internal helper names and implementation details remain at the discretion of the implementer within these constraints.

---

## 1. Context & Motivation

When a task is created as `Open` (or promoted from `Suggested` to `Open`) with all prerequisites already `Closed`, it is immediately enqueued to Hangfire (`ITaskJobQueue.EnqueueStage`). When an `Open` task has unclosed prerequisites, it waits in `Open` status until `TaskMergeAndCascadeCoordinator` cascades unblocking when a prerequisite task transitions to `Closed`.

However, if a maintainer edits an `Open` task (via `TaskDetail.razor`, `Backlog.razor`, or `DependencyLinkingModal.razor` -> `TaskMutationService.UpdateDependenciesAsync`) or an agent edits an `Open` task (via `edit_task` -> `BacklogMutationToolService.EditTaskAsync`) to **remove** its blocking dependencies, the dependency rows are removed from the database, but **nothing checks whether the task is now unblocked and enqueues it for execution**. Consequently, the task sits idle in `Open` state indefinitely even though no dependencies remain blocking and a worker/GPU slot is available.

---

## 2. Required Behavior & Constraints

### 2.1 Enqueue Unblocked `Open` Tasks After Dependency Updates
- Whenever dependencies are updated on an existing task (both via UI `TaskMutationService.UpdateDependenciesAsync` and via agent tool `BacklogMutationToolService.EditTaskAsync`), if the task meets **all** of the following conditions:
  1. `task.Status == TaskOverallStatus.Open` (not `Draft`, `Suggested`, `InProgress`, `InReview`, `Blocked`, `Closed`, `Rejected`, `Cancelled`, etc.),
  2. `!task.IsWaitingForHuman` and `!task.HasExecutionRun` (and not globally paused if applicable, or let `TryScheduleOpenTaskIfReadyAsync` check execution readiness consistently with promotion),
  3. All remaining prerequisite dependencies (if any) are `TaskOverallStatus.Closed`, and
  4. The task does not already have an active (`Enqueued`, `Scheduled`, `Processing`, or `Awaiting`) Hangfire job (`!_jobQueue.IsJobActive(task.LastHangfireJobId)`),
  then the task MUST be enqueued to its current/initial workflow stage queue via `ITaskJobQueue.EnqueueStage(...)`, updating `task.LastHangfireJobId` and persisting the change.

### 2.2 Shared Orchestration Entry Point & Readiness Consistency
- Readiness evaluation and enqueuing MUST be exposed through a dedicated method on `ITaskOrchestrator` (e.g., `Task<bool> TryScheduleOpenTaskIfReadyAsync(int taskId, CancellationToken cancellationToken = default)`), implemented in `TaskOrchestrator` (delegating to `ITaskLifecycleMutationCoordinator` or `ITaskMergeAndCascadeCoordinator`) and stubbed in `tests/NightCodeForge.Web.Tests/MockTaskOrchestrator.cs`, rather than duplicating pipeline/queue resolution logic across `NightCodeForge.Web` and `NightCodeForge.Agents`.
- **Repository & Dependency Store Consistency:** `InMemoryTaskRepository.UpdateWithHistoryAsync` updates in-memory repository dependencies without mutating `InMemoryTaskDependencyStore` (and some unit test fixtures inject a mocked `ITaskDependencyStore`). Therefore, the readiness check in `TryScheduleOpenTaskIfReadyAsync` MUST verify `task.Status == TaskOverallStatus.Open`, `!task.IsWaitingForHuman`, and `!task.HasExecutionRun`, and verify that all persisted prerequisites—checking `_taskRepository.GetDependenciesAsync(task.Id)` / `task.Dependencies` as well as `_dependencyStore`, and resolving each prerequisite's `TaskOverallStatus` via `_taskRepository.GetStatusAsync(prereqId)` / `_dependencyStore.GetTaskStatusAsync(prereqId)`—are `TaskOverallStatus.Closed` (and ensure `ITaskDependencyStore` / `UpdateTaskStatusInStoreAsync` stays consistent when applicable).
- **Stage Resolution:** If `task.CurrentStageId` is already set to a valid stage in the task's workflow pipeline, enqueue to that stage's `QueueTier`; otherwise resolve `pipeline.InitialStage`. If the workflow or stage cannot be resolved, fail fast with the appropriate domain exception (`WorkflowValidationException` / `KeyNotFoundException`) per `AGENTS.md`.

### 2.3 Active Job Guard, Stale Job Cleanup & Constructor Ceiling
- **Active Hangfire Job Inspection (`ITaskJobQueue.IsJobActive`):** Add `bool IsJobActive(string? jobId)` to `ITaskJobQueue` (`src/NightCodeForge.Core/Workflow/ITaskJobQueue.cs`):
  - In `HangfireTaskJobQueue` (`src/NightCodeForge.Infrastructure/BackgroundJobs/HangfireTaskJobQueue.cs`), check whether `_jobStorage.GetConnection().GetJobData(jobId)?.State` is an active Hangfire state (`Enqueued`, `Scheduled`, `Processing`, or `Awaiting`). Return `false` for null/whitespace IDs, unknown/missing jobs, or terminal states (`Succeeded`, `Deleted`, `Failed`).
  - Implement `IsJobActive` across test implementations (`FakeTaskJobQueue`, `InMemoryTaskJobQueue`, `MockTaskJobQueue`, `FakeTestJobQueue`, etc.) by checking `!string.IsNullOrWhiteSpace(jobId)` and not deleted/completed.
  - Also ensure `TaskOrchestrator.ProcessTaskAsync` clears `task.LastHangfireJobId` (e.g., via `_taskRepository.UpdateLastHangfireJobIdAsync(task.Id, null, cancellationToken)`) when exiting early at `EnsureReadyForExecutionAsync` so a blocked `Open` task never retains a stale `LastHangfireJobId` after a rejected dispatch.
- **Constructor Parameter Ceiling (`<= 10` Parameters per `AGENTS.md` and `ConstructorParameterCeilingTests`):**
  - `TaskMutationService` (`src/NightCodeForge.Web/Services/TaskMutationService.cs`) currently has 6 constructor parameters and already injects `ITaskOrchestrator`.
  - `BacklogMutationToolService` (`src/NightCodeForge.Agents/Tools/BacklogMutationToolService.cs`) is currently at the **10-parameter constructor ceiling** (10 parameters; `TaskOrchestrator` and `TaskMergeAndCascadeCoordinator` are also at 10 parameters, while `TaskLifecycleMutationCoordinator` is at 9).
  - `BacklogMutationToolService` MUST NOT exceed 10 constructor parameters and MUST NOT use optional constructor parameters per `AGENTS.md`. Inject `ITaskOrchestrator` into `BacklogMutationToolService` by extracting/consolidating an existing dependency (e.g., extracting a cohesive collaborator or consolidating merge-mode/dependency validation), and update test instantiations (`BacklogMutationToolServiceTests`, `Step125BBacklogDifficultyToolTests`, `Step122BAttachmentToolsAndPromptDigestTests`) so `ConstructorParameterCeilingTests.AllInstanceConstructors_DoNotExceedParameterCeiling` passes.
- **Idempotency & Auditability:** Duplicate enqueuing for a task that already has an active queued/processing job MUST be avoided, and an informational `ExecutionLog` entry recording that the task was scheduled upon dependency removal is recommended for observability.

---

## 3. Definition of Done (DoD) Checklist

- [x] Add `bool IsJobActive(string? jobId)` to `ITaskJobQueue`, implement it in `HangfireTaskJobQueue` (checking `Enqueued`, `Scheduled`, `Processing`, `Awaiting` job states) and test queue fakes, and clear `LastHangfireJobId` in `TaskOrchestrator.ProcessTaskAsync` when `EnsureReadyForExecutionAsync` exits early.
- [x] Add `Task<bool> TryScheduleOpenTaskIfReadyAsync(int taskId, CancellationToken cancellationToken = default)` to `ITaskOrchestrator` (implemented in `TaskOrchestrator` via `ITaskLifecycleMutationCoordinator` / `ITaskMergeAndCascadeCoordinator`, and stubbed in `MockTaskOrchestrator`), checking `Open` status, `!IsWaitingForHuman`, `!HasExecutionRun`, closed prerequisites across both `_taskRepository` and `_dependencyStore`, and `!IsJobActive(task.LastHangfireJobId)`.
- [x] Invoke `TryScheduleOpenTaskIfReadyAsync` from `TaskMutationService.UpdateDependenciesAsync` (`src/NightCodeForge.Web/Services/TaskMutationService.cs`) after persisting dependency changes.
- [x] Inject `ITaskOrchestrator` into `BacklogMutationToolService` (`src/NightCodeForge.Agents/Tools/BacklogMutationToolService.cs`) while keeping constructor parameters `<= 10` (no optional parameters) and preserving `ConstructorParameterCeilingTests`, and invoke `TryScheduleOpenTaskIfReadyAsync` from `BacklogMutationToolService.EditTaskAsync` after dependency changes.
- [x] Add unit tests in `NightCodeForge.Core.Tests`, `NightCodeForge.Infrastructure.Tests`, `NightCodeForge.Web.Tests`, and `NightCodeForge.Agents.Tests` verifying that:
  - Removing the last blocking dependency from an `Open` task enqueues it to the workflow stage queue and updates `LastHangfireJobId`.
  - Removing one dependency while another unclosed dependency still blocks the task does NOT enqueue it.
  - Removing dependencies on a `Draft`, `Suggested`, or `HasExecutionRun == true` / `IsWaitingForHuman == true` task does NOT enqueue it.
  - Editing dependencies on an `Open` task that already has an active (`Enqueued`/`Scheduled`/`Processing`/`Awaiting`) Hangfire job does NOT create a duplicate job, whereas a task with a stale/deleted/completed `LastHangfireJobId` IS enqueued cleanly.
  - `ConstructorParameterCeilingTests` and all non-E2E tests pass via `dotnet test --filter "Category!=Acceptance&Category!=E2E"`.
