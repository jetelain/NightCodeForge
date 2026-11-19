# 🔄 Step 11.52: Synchronize Ideation Proposal View When Agent Updates Proposed Tasks

> **Parent Plan:** [docs/fixes/README.md](README.md)  
> **Target Framework:** .NET 10 (C# 13)  
> **Status:** Proposed  
> **Target Projects:** `NightCodeForge.Web`, `NightCodeForge.Web.Tests`  
> **Aligns With:** `AGENTS.md`, `docs/PLAN.md`

> [!IMPORTANT]
> **Specification Disclaimer:** This document is a behavioral specification. It defines required behaviors, architectural invariants, and verification criteria. Specific internal helper names and implementation details remain at the discretion of the implementer within these constraints.

---

## 1. Context & Motivation

When the Ideation agent refines or updates an already-proposed `Draft` (or `Suggested` / `Open`) task via host-level backlog mutation tools (such as `edit_task`, `set_task_priority`, `rework_priorities`, or `remove_task`), the database is updated immediately, and `Ideation.razor` reloads `_workspaceTasks` and invokes `IdeationSessionState.SyncProposalsWithWorkspaceTasks(_workspaceTasks)`.

However, `IdeationProposalViewModel` and `Ideation.razor` currently fail to fully reconcile with the persisted `ForgeTask` state:
1. `IdeationProposalViewModel.SyncFromTask(ForgeTask task)` updates `SelectedDependencyIds` from `task.Dependencies` (only when `task.Dependencies != null`), **but does not clear `DependsOnTitles`**.
2. `IdeationSessionState.SyncProposalsWithWorkspaceTasks` then calls `proposal.ResolveDependencies(workspaceTasks)` whenever `!proposal.IsAdded`, which iterates over the stale `DependsOnTitles` and **re-adds** the old dependency IDs back into `SelectedDependencyIds`.
3. `IdeationProposalCard.razor` also continues to render the stale `Proposal.DependsOnTitles` tags ("Sequential Dependencies"), misleading the maintainer.
4. `SyncFromTask` only appends `SelectedAttachmentIds` and does not remove attachments that were removed from the task. Moreover, in `IdeationProposalViewModel.FromProposal` (`src/NightCodeForge.Web/Services/IdeationSessionState.cs`, lines 185–239), initial `proposal.AttachmentIds` are merged *after* `vm.SyncFromTask(matchedTask)`—so when rehydrating historical turns in `Ideation.razor` (`LoadConversationIntoSession`), stale `proposal.AttachmentIds` from the initial `propose_tasks` turn metadata overwrite deleted attachments on the persisted `ForgeTask`.
5. In `Ideation.razor` (lines 1577–1596), when `IdeationStreamUpdateKind.Completed` processes `update.FinalResult.ProposedTasks` at the end of a turn, if an agent proposed a task and then cancelled/removed it via `remove_task` in the same turn, the end-of-turn pass can re-add the cancelled proposal into `Session.Proposals` without syncing against `_workspaceTasks`.

As a result, the Ideation UI retains stale, incorrect dependencies, attachments, or cancelled proposals even though the agent already updated the database. When the maintainer subsequently clicks "Add to Backlog" on the proposal card, those stale dependencies are written back to the database—potentially restoring removed dependencies or creating circular dependencies (e.g., if the agent reversed a dependency direction between two tasks).

---

## 2. Required Behavior & Constraints

### 2.1 Persisted Task as Authoritative Source of Truth
- Once a proposal is linked to a persisted `ForgeTask` (`CreatedTaskId.HasValue` and matched in `SyncFromTask(ForgeTask task)`), the database `ForgeTask` MUST be treated as the authoritative source of truth for its `Title`, `Description`, `Priority`, `Difficulty`, `MergeMode`, `SelectedDependencyIds`, and `SelectedAttachmentIds`.
- In `IdeationProposalViewModel.FromProposal(IdeationProposal proposal, IReadOnlyList<ForgeTask>? existingTasks = null)`, initial `proposal.AttachmentIds` mapping MUST run *before* matching `existingTasks` and invoking `vm.SyncFromTask(matchedTask)` so that when a persisted `ForgeTask` exists (`matchedTask != null`, such as during session rehydration in `LoadConversationIntoSession`), `SyncFromTask(matchedTask)` remains authoritative and stale `proposal.AttachmentIds` from initial turn metadata cannot re-inject removed attachments.
- `IdeationProposalViewModel.SyncFromTask(ForgeTask task)` MUST:
  - Replace `SelectedDependencyIds` with the distinct dependency IDs from `task.Dependencies` (`(task.Dependencies ?? Enumerable.Empty<TaskDependency>()).Select(d => d.DependsOnTaskId).Where(id => id != task.Id).Distinct().ToList()`), clearing `SelectedDependencyIds` when `task.Dependencies` is null or empty.
  - Clear `DependsOnTitles` (`DependsOnTitles.Clear()`) so that stale title-based dependency hints are neither displayed on `IdeationProposalCard.razor` nor re-applied by `ResolveDependencies`.
  - Replace `SelectedAttachmentIds` (rather than only appending) with the distinct reconciled attachment IDs from `task.AttachmentLinks` (`TaskAttachmentId > 0`) and attachment citations in `task.Description` (so removed attachments are also removed from the view model).

### 2.2 Prevent Stale Title Re-Resolution
- `IdeationProposalViewModel.ResolveDependencies` (and/or `IdeationSessionState.SyncProposalsWithWorkspaceTasks`) MUST NOT re-add dependencies from stale `DependsOnTitles` once the proposal has been synchronized from a persisted `ForgeTask`.
- `IdeationSessionState.SyncProposalsWithWorkspaceTasks` MUST continue to remove any `Draft` proposals whose persisted `ForgeTask` was `Cancelled` or `Rejected` (e.g., via `remove_task`).

### 2.3 Live UI Refresh on Agent Backlog Mutations & End-of-Turn Reconciliation
- When backlog mutation tools (`edit_task`, `set_task_priority`, `rework_priorities`, `remove_task`, `propose_tasks`) complete during an Ideation turn in `Ideation.razor`, `Session.Proposals` MUST be synchronized with the latest workspace tasks and `Session.NotifyStateChanged()` / `StateHasChanged()` MUST reflect the updated dependencies, priority, title, description, attachments, and removals immediately in the proposal cards.
- In `Ideation.razor`, when `IdeationStreamUpdateKind.Completed` processes `update.FinalResult.ProposedTasks`, it MUST NOT re-add a proposal whose persisted `ForgeTask` in `_workspaceTasks` is already `Cancelled` or `Rejected` (e.g., removed earlier in the same turn via `remove_task`), and MUST invoke `Session.SyncProposalsWithWorkspaceTasks(_workspaceTasks)` after reconciling `FinalResult.ProposedTasks`.

---

## 3. Definition of Done (DoD) Checklist

- [ ] Update `IdeationProposalViewModel.FromProposal` and `IdeationProposalViewModel.SyncFromTask(ForgeTask task)` in `src/NightCodeForge.Web/Services/IdeationSessionState.cs` so `SyncFromTask` runs after initial `proposal.AttachmentIds` mapping, clears `DependsOnTitles`, and replaces (rather than only appending) `SelectedDependencyIds` and `SelectedAttachmentIds`.
- [ ] Ensure `IdeationSessionState.SyncProposalsWithWorkspaceTasks` and `Ideation.razor` (both on `ToolCallCompleted` for backlog mutation tools and on `IdeationStreamUpdateKind.Completed` when reconciling `FinalResult.ProposedTasks`) keep all proposal cards in sync whenever the Ideation agent mutates or removes tasks, without re-injecting stale dependencies or re-adding cancelled/rejected proposals.
- [ ] Add unit tests in `tests/NightCodeForge.Web.Tests/IdeationSessionStateTests.cs` (and `tests/NightCodeForge.Web.Tests/IdeationPageTests.cs` if applicable) verifying that:
  - Updating a persisted `Draft` task's dependencies (including removing all dependencies or reversing a dependency between two tasks) clears `DependsOnTitles` and updates `SelectedDependencyIds` to match the database without resurrecting old dependencies.
  - Updating attachments on a persisted `Draft` task removes deleted attachments from `SelectedAttachmentIds` (both in `SyncFromTask` and when reconstructing via `FromProposal(..., existingTasks)`).
- [ ] Verify all non-E2E tests pass via `dotnet test --filter "Category!=Acceptance&Category!=E2E"`.
