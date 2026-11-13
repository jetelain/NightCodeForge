# 🐙 Step 12.11D: GitHub Bi-Directional Task Mirroring, Multi-User Issue Claiming & PR-Merge Closure (`GitHubTaskMirrorService`)

> **Feature Implementation Step (Part 4 of 4 — Bi-Directional Task Mirroring, Pre-Flight Claiming & PR-Merge Closure)**  
> *Parent Plan:* [Features & Integrations Roadmap](README.md)  
> *Prerequisite Steps:* [Step 12.11A: GitHub App Device Flow, Token Store, Client & Settings](../archive/phase-12/STEP_12_11A_GITHUB_CLIENT_DEVICE_AUTH_AND_SETTINGS.md) | [Step 12.11B: Outbound PR Delivery](STEP_12_11B_GITHUB_PULL_REQUEST_DELIVERY.md) | [Step 12.11C: Untrusted Issue Intake & Triage](STEP_12_11C_GITHUB_UNTRUSTED_ISSUE_INTAKE_AND_TRIAGE.md)  
> *Project Target:* `src/NightCodeForge.Infrastructure/GitHub`, `src/NightCodeForge.Core/Workflow`, `tests/NightCodeForge.Infrastructure.Tests`, `tests/NightCodeForge.Core.Tests` | *Status:* Planned | *Target Framework:* .NET 10 (C# 13)  
> *Aligns With:* [AGENTS.md](../../AGENTS.md), [docs/PLAN.md](../PLAN.md)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Objective

Building on [Step 12.11A](../archive/phase-12/STEP_12_11A_GITHUB_CLIENT_DEVICE_AUTH_AND_SETTINGS.md), [Step 12.11B](STEP_12_11B_GITHUB_PULL_REQUEST_DELIVERY.md), and [Step 12.11C](STEP_12_11C_GITHUB_UNTRUSTED_ISSUE_INTAKE_AND_TRIAGE.md), this final part of Step 12.11 implements **Bi-Directional Task Mirroring, Multi-User Pre-Flight Issue Claiming, and Pull Request Merge Closure (`GitHubTaskMirrorService`)**:

1. **Multi-User Pre-Flight Claiming & Conflict Prevention (`TryClaimIssueForExecutionAsync`):**
   - Before Stage 1 (`StandardStageIds.TaskReview`) begins on a GitHub-linked task, verify the live GitHub Issue is not already closed or claimed by another teammate's NCF instance, transitioning deterministically to `Cancelled` (if closed externally) or suspending with `IsWaitingForHuman = true` (if claimed by another user), and automatically assigning the issue to `AuthenticatedUserLogin` when `AutoAssignIssueOnStart` is enabled.
2. **Lifecycle & Stage Transition Mirroring:**
   - Automatically link (or optionally create when `CreateGitHubIssuesForLocalTasks == true`) a corresponding GitHub Issue when a task is promoted or created in `Open` status.
   - Mirror pipeline stage progression and status transitions through labels (`ncf:backlog`, `ncf:in-progress`, `ncf:gates-passed`, `ncf:ready-for-review`, `ncf:closed`) and optional milestone comments—respecting a **1:N sibling task guard** so decomposed issues only close when all sibling tasks in the workspace are closed.
3. **Pull Request Merge Closure & DAG Cascade Synchronization (`CloseTaskOnPrMerge`):**
   - Synchronize task closure, worktree/container cleanup, local base branch fast-forward, and downstream DAG cascade unblocking when a linked GitHub Pull Request is merged on GitHub.

---

## 2. Task Mirroring & Multi-User Claiming Specification (`GitHubTaskMirrorService`)

### 2.1 Multi-User Claiming & Conflict Prevention
When multiple developers run NightCodeForge on the same repository:
1. **Opt-In Local Task Issue Export (`CreateGitHubIssuesForLocalTasks = false`):**
   - By default (`CreateGitHubIssuesForLocalTasks == false`), `GitHubTaskMirrorService` only mirrors status labels and milestone comments onto tasks that **already** link to a GitHub Issue (`ExternalSource == "GitHub"` and `!string.IsNullOrWhiteSpace(ExternalReferenceId)`). Local tasks created in Ideation or `/tasks/new` stay private to the developer's local backlog until a PR is opened.
   - When `CreateGitHubIssuesForLocalTasks == true`, promoting/creating a local `Open` task with no `ExternalReferenceId` creates a new GitHub Issue immediately stamped with `ncf:backlog` and assigned to `AuthenticatedUserLogin` so other teammates' `GitHubIssueSyncService` instances never mistake it for an untriaged external issue.
2. **Pre-Flight Ownership & Claim Check Before Execution (`TryClaimIssueForExecutionAsync`):**
   - Before a GitHub-linked task (`ExternalSource == "GitHub"` and `ExternalReferenceId != null`) begins Stage 1 (`StandardStageIds.TaskReview`), `GitHubTaskMirrorService` fetches the live GitHub Issue via `IGitHubClientService.GetIssueAsync`:
     - **Conflict Detection & Deterministic Task State Transition:** If the GitHub Issue is already `closed`, OR assigned to a **different** GitHub user (`issue.Assignees.Count > 0` and `issue.Assignees` does not contain `AuthenticatedUserLogin`, case-insensitive), OR already carries an active execution label (`ncf:in-progress`, `ncf:gates-passed`, `ncf:ready-for-review`) when not assigned to `AuthenticatedUserLogin` (and with no sibling task for the same `ExternalReferenceId` owned locally in that workspace):
       - Execution is suspended before running LLM stages:
         - If the GitHub Issue is already `closed` (and no sibling local task closed it), transition `task.Status` to `TaskOverallStatus.Cancelled` (recording audit history via `UpdateWithHistoryAsync`).
         - If the GitHub Issue is open but claimed/in-progress by another GitHub user, set `task.IsWaitingForHuman = true` and `task.WaitingReason = $"Skipped execution: GitHub Issue #{task.ExternalReferenceId} is already claimed or in progress on GitHub"` so queue sweeps and startup recovery do not re-trigger the task in a loop.
       - Record an `ExecutionLog` and broadcast the updated task status (`BroadcastTaskStatusAsync`), preventing two NCF instances from burning compute on the same issue simultaneously.
     - **Automatic Claiming (`AutoAssignIssueOnStart = true`):** When the issue is unassigned (or already assigned to `AuthenticatedUserLogin`), `GitHubTaskMirrorService` assigns the issue to `AuthenticatedUserLogin` (`assignees: [authenticatedUserLogin]`) alongside applying the `ncf:in-progress` label.

### 2.2 Lifecycle & Stage Transition Mirroring
Because active tasks in [`TaskOrchestrator`](../../src/NightCodeForge.Core/Workflow/TaskOrchestrator.cs) remain in `TaskOverallStatus.Open` (`HasExecutionRun = true`) while advancing through workflow stages (`CurrentStageId`), `GitHubTaskMirrorService` hooks into **lifecycle and stage completion transitions**.
- **Constructor Parameter Budget Guard (`<= 10` Parameters per [AGENTS.md](../../AGENTS.md)):** Because [`TaskMergeAndCascadeCoordinator`](../../src/NightCodeForge.Core/Workflow/TaskMergeAndCascadeCoordinator.cs) is at `10/10` constructor parameters, [`TaskOrchestrator`](../../src/NightCodeForge.Core/Workflow/TaskOrchestrator.cs) is at `9/10`, and [`TaskLifecycleMutationCoordinator`](../../src/NightCodeForge.Core/Workflow/TaskLifecycleMutationCoordinator.cs) is at `9/10`, expose outbound PR delivery and issue lifecycle mirroring behind a single cohesive collaborator on [`ITaskWorkspacePreparer`](../../src/NightCodeForge.Core/Workflow/ITaskWorkspacePreparer.cs) (which is currently at `8/10` parameters and already injected into all three workflow coordinators):

| Lifecycle / Stage Transition | GitHub Issue Label | Action / Milestone Comment (when `PostMilestoneComments` is enabled) |
| :--- | :--- | :--- |
| **Task Created / Promoted to `Open` (`!HasExecutionRun`)** | `ncf:backlog` | Link existing Issue `#ExternalReferenceId` (or create a new GitHub Issue assigned to `AuthenticatedUserLogin` when `CreateGitHubIssuesForLocalTasks == true`); replace `ncf:triaged` with `ncf:backlog`. |
| **Stage 1 (`StandardStageIds.TaskReview`) Started / Completed** | `ncf:in-progress` | Verify issue is not claimed by another user; assign to `AuthenticatedUserLogin` (when `AutoAssignIssueOnStart == true`), replace prior `ncf:*` status label with `ncf:in-progress`, and post comment with the approved task specification summary. |
| **Stage 4 (`StandardStageIds.PostImplementationGates`) Completed** | `ncf:gates-passed` | Replace prior `ncf:*` status label with `ncf:gates-passed`; post deterministic build, test, and code coverage metrics. |
| **Task Reaches `TaskOverallStatus.Ready` (Stage 6 Completed)** | `ncf:ready-for-review` | Replace prior `ncf:*` status label with `ncf:ready-for-review`; comment that all verification gates passed (referencing `PullRequestUrl` if opened). |
| **Task Reaches `TaskOverallStatus.Closed`** | `ncf:closed` | Post completion comment referencing `SquashCommitSha` or merged `PullRequestUrl`. **1:N Sibling Task Guard:** Because [`ITriageService.TriageExternalReportAsync`](../../src/NightCodeForge.Core/Workflow/ITriageService.cs) can decompose a single GitHub Issue into multiple `ForgeTask` items sharing the same `ExternalReferenceId`, replace the prior `ncf:*` status label with `ncf:closed` and close the GitHub Issue **only when no other non-cancelled, non-rejected tasks in the workspace with the same `ExternalReferenceId` remain unclosed**. |

### 2.3 Pull Request Merge & Issue Closure Synchronization (`CloseTaskOnPrMerge`)
When `CloseTaskOnPrMerge` is enabled, the recurring sync job checks open NightCodeForge tasks (in `Ready` or `Open` status) that have a linked `PullRequestNumber` / `PullRequestUrl`:
1. Queries `IGitHubClientService.GetPullRequestAsync` for the linked `PullRequestNumber`.
2. When the GitHub Pull Request is merged (`Merged == true`):
   - Updates `task.SquashCommitSha` (from the PR's `MergeCommitSha` if available) and transitions the task to `TaskOverallStatus.Closed` (`CurrentStageId = string.Empty`) with audit history (`UpdateWithHistoryAsync`) and dependency store update (`UpdateTaskStatusInStoreAsync`).
   - Best-effort fetches/pulls `task.BaseBranch` from the remote into the workspace so local `BaseBranch` incorporates the merged PR commits before downstream tasks branch from it.
   - Cleans up the task worktree (`RemoveWorktreeSafeAsync`), container (`CleanupTaskContainerSafeAsync`), and archives the task markdown file (`ArchiveTaskMarkdownSafeAsync`).
   - Triggers downstream DAG cascade unblocking (`ResolveCascadeUnblockedTasksAsync` and enqueuing newly unblocked tasks) and synchronizes badge counts.

---

## 3. Implementation Checklist & Deliverables

- [ ] **Pre-Flight Ownership & Claiming (`TryClaimIssueForExecutionAsync`):**
  - [ ] Implement pre-flight ownership/claim verification before Stage 1 execution to transition the task to `Cancelled` (if the GitHub Issue is already closed externally) or suspend with `IsWaitingForHuman = true` and `WaitingReason` (if claimed/in-progress by another GitHub user), and auto-assign to `AuthenticatedUserLogin` when `AutoAssignIssueOnStart` is enabled.
- [ ] **Lifecycle & Stage Transition Mirroring (`GitHubTaskMirrorService`):**
  - [ ] Implement `GitHubTaskMirrorService` (routed via a single cohesive collaborator on `ITaskWorkspacePreparer` to respect the 10-constructor-parameter limit) to synchronize `ncf:*` status labels and post milestone comments on `Open` promotion (creating new GitHub issues for local tasks only when `CreateGitHubIssuesForLocalTasks` is enabled), `TaskReview` completion, `PostImplementationGates` completion, `Ready` transition, and `Closed` transition (closing the GitHub Issue only when all sibling tasks for the same `ExternalReferenceId` are closed) when `TaskMirroringEnabled` is active.
- [ ] **PR Merge Polling & DAG Cascade Unblocking (`CloseTaskOnPrMerge`):**
  - [ ] Implement PR merge polling when `CloseTaskOnPrMerge` is enabled, performing worktree/container cleanup, markdown archiving, remote `BaseBranch` fast-forward sync, and downstream DAG cascade unblocking.
- [ ] **Unit & Mock Tests:**
  - [ ] Unit tests for `GitHubTaskMirrorService` verifying multi-user claim conflict detection (`Cancelled` on externally closed issue, `IsWaitingForHuman = true` on teammate-claimed issue), auto-assignment, stage-based label transitions (`ncf:backlog`, `ncf:in-progress`, `ncf:gates-passed`, `ncf:ready-for-review`, `ncf:closed`), 1:N sibling task closure guard, milestone comment formatting, and PR-merge task closure + DAG cascade unblocking.

