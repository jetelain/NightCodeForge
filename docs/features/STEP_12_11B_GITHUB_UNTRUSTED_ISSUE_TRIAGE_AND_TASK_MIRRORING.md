# 🐙 Step 12.11B: GitHub Untrusted Issue Intake Triage & Bi-Directional Task Mirroring

> **Feature Implementation Step (Part 2 of 2 — Inbound Issue Triage & Bi-Directional Sync)**  
> *Parent Plan:* [Features & Integrations Roadmap](README.md)  
> *Prerequisite Step:* [Step 12.11A: GitHub Client, Settings & PR Delivery](STEP_12_11A_GITHUB_CLIENT_SETTINGS_AND_PR_DELIVERY.md)  
> *Project Target:* `src/NightCodeForge.Infrastructure/GitHub`, `src/NightCodeForge.Agents/Triage`, `src/NightCodeForge.Core/Workflow`, `tests/NightCodeForge.Infrastructure.Tests` | *Status:* Planned | *Target Framework:* .NET 10 (C# 13)  
> *Aligns With:* [AGENTS.md](../../AGENTS.md), [docs/PLAN.md](../PLAN.md)

---

## 1. Executive Objective

Building on `IGitHubClientService`, `IGitHubDeviceAuthService`, and `GitHubIntegrationSettings` from [Step 12.11A](STEP_12_11A_GITHUB_CLIENT_SETTINGS_AND_PR_DELIVERY.md), this step (Part 2 of Step 12.11) implements inbound **Untrusted Issue Intake & Automated Triage** and **Bi-Directional Task Mirroring**:

1. **Untrusted Issue Intake & Automated Triage (`GitHubIssueSyncService`):**
   - GitHub issues from external contributors or bug reports are treated as **untrusted external inputs**.
   - Instead of blindly mapping 1:1 to executable tasks, discovered issues are routed directly into [`ITriageService.TriageExternalReportAsync`](../../src/NightCodeForge.Agents/Triage/TriageService.cs) executing strictly in `SandboxSecurityTier.RestrictedReadOnly` and `AgentMode.ReadOnly`.
   - Vetted task proposals are saved in `TaskOverallStatus.Suggested` with `Origin = TaskOrigin.GitHubIssue`, `ExternalSource = "GitHub"`, and `ExternalReferenceId = issue.Number.ToString()` (unprefixed numeric string, e.g. `"123"`).
2. **Task Mirroring & Bi-Directional State Synchronization (`GitHubTaskMirrorService`):**
   - Automatically create or link a corresponding GitHub Issue when a task is promoted or created in `Open` status.
   - Mirror pipeline stage progression and status transitions through labels (`ncf:backlog`, `ncf:in-progress`, `ncf:gates-passed`, `ncf:ready-for-review`, `ncf:closed`) and optional milestone comments.
   - Synchronize task closure, worktree/container cleanup, local base branch fast-forward, and downstream DAG cascade unblocking when a linked GitHub PR or Issue is merged/closed.

---

## 2. Untrusted Issue Intake & Triage Specification (`GitHubIssueSyncService`)

1. **Discovery:**
   - Recurring Hangfire job `GitHubIssueSyncService.SyncOpenIssuesAsync` iterates over workspaces where `GitHubSettings is { Enabled: true, IssueTriageEnabled: true }` and queries `IGitHubClientService.ListIssuesAsync` for open issues (excluding Pull Requests, as GitHub's Issues API returns PRs unless filtered out via `pull_request == null`) matching `GitHubIntegrationSettings.IssueFilterLabel` (when configured).
2. **Deduplication (Including Non-Actionable / 0-Proposal Issues):**
   - Verifies that issue `issue.Number.ToString()` has not already been linked to an existing `ForgeTask` (`WorkspaceId == workspace.Id && ExternalSource == "GitHub" && ExternalReferenceId == issue.Number.ToString()`) and has not already been marked as triaged (e.g. via the `ncf:triaged` / `ncf:*` label on the GitHub issue or a tracked triaged-issue watermark).
   - This guarantees that if the triage agent determines an issue is invalid, non-reproducible, or a duplicate and emits **0 proposals**, subsequent polling cycles do not re-triage the same open issue in an infinite loop.
3. **Untrusted Triage Execution (`TriageReportRequest` & `TriageService`):**
   - Extend [`TriageReportRequest`](../../src/NightCodeForge.Core/Workflow/ITriageService.cs) with optional provenance parameters:
     ```csharp
     public record TriageReportRequest(
         string Title,
         string Description,
         string? BaseBranch = null,
         TaskOrigin Origin = TaskOrigin.UntrustedIntake,
         string? ExternalSource = null,
         string? ExternalReferenceId = null);
     ```
   - `GitHubIssueSyncService` dispatches `new TriageReportRequest(issue.Title, issue.Body ?? string.Empty, workspace.DefaultBaseBranch, TaskOrigin.GitHubIssue, "GitHub", issue.Number.ToString())` to [`ITriageService.TriageExternalReportAsync`](../../src/NightCodeForge.Agents/Triage/TriageService.cs) (`AgentMode.ReadOnly`, `SandboxSecurityTier.RestrictedReadOnly`).
4. **Backlog Population & Issue Acknowledgment:**
   - [`TriageService`](../../src/NightCodeForge.Agents/Triage/TriageService.cs) persists proposed tasks in `TaskOverallStatus.Suggested` with `Origin = request.Origin` (`TaskOrigin.GitHubIssue`), `ExternalSource = request.ExternalSource` (`"GitHub"`), and `ExternalReferenceId = request.ExternalReferenceId` (`issue.Number.ToString()`), and mirrors them to `docs/tasks/*.md`.
   - Marks the GitHub issue as triaged (`ncf:triaged` label when `SyncStatusLabels` is enabled) and optionally posts an acknowledgment comment on the GitHub issue when `PostMilestoneComments` is enabled.

---

## 3. Task Mirroring Specification (`GitHubTaskMirrorService`)

Because active tasks in [`TaskOrchestrator`](../../src/NightCodeForge.Core/Workflow/TaskOrchestrator.cs) remain in `TaskOverallStatus.Open` (`HasExecutionRun = true`) while advancing through workflow stages (`CurrentStageId`), `GitHubTaskMirrorService` hooks into **lifecycle and stage completion transitions** (invoked via a cohesive collaborator without exceeding the 10-constructor-parameter limit on `TaskOrchestrator`, `TaskMergeAndCascadeCoordinator`, or `TaskLifecycleMutationCoordinator`):

| Lifecycle / Stage Transition | GitHub Issue Label | Action / Milestone Comment (when `PostMilestoneComments` is enabled) |
| :--- | :--- | :--- |
| **Task Created / Promoted to `Open` (`!HasExecutionRun`)** | `ncf:backlog` | Create GitHub Issue (if `ExternalReferenceId` is empty) or link existing Issue `#ExternalReferenceId`; mark as accepted into backlog. |
| **Stage 1 (`StandardStageIds.TaskReview`) Completed** | `ncf:in-progress` | Replace prior `ncf:*` status label with `ncf:in-progress`; post comment with the approved task specification / implementation plan summary. |
| **Stage 4 (`StandardStageIds.PostImplementationGates`) Completed** | `ncf:gates-passed` | Replace prior `ncf:*` status label with `ncf:gates-passed`; post deterministic build, test, and code coverage metrics. |
| **Task Reaches `TaskOverallStatus.Ready` (Stage 6 Completed)** | `ncf:ready-for-review` | Replace prior `ncf:*` status label with `ncf:ready-for-review`; comment that all verification gates passed (referencing `PullRequestUrl` if opened). |
| **Task Reaches `TaskOverallStatus.Closed`** | `ncf:closed` | Replace prior `ncf:*` status label with `ncf:closed`; post completion comment referencing `SquashCommitSha` or merged `PullRequestUrl`, and close the GitHub Issue. |

### Pull Request Merge & Issue Closure Synchronization (`CloseTaskOnPrMerge`)
When `CloseTaskOnPrMerge` is enabled, the recurring sync job checks open NightCodeForge tasks (in `Ready` or `Open` status) that have a linked `PullRequestNumber` / `PullRequestUrl`:
1. Queries `IGitHubClientService.GetPullRequestAsync` for the linked `PullRequestNumber`.
2. When the GitHub Pull Request is merged (`Merged == true`):
   - Updates `task.SquashCommitSha` (from the PR's `MergeCommitSha` if available) and transitions the task to `TaskOverallStatus.Closed` (`CurrentStageId = string.Empty`) with audit history (`UpdateWithHistoryAsync`) and dependency store update (`UpdateTaskStatusInStoreAsync`).
   - Best-effort fetches/pulls `task.BaseBranch` from the remote into the workspace so local `BaseBranch` incorporates the merged PR commits before downstream tasks branch from it.
   - Cleans up the task worktree (`RemoveWorktreeSafeAsync`), container (`CleanupTaskContainerSafeAsync`), and archives the task markdown file (`ArchiveTaskMarkdownSafeAsync`).
   - Triggers downstream DAG cascade unblocking (`ResolveCascadeUnblockedTasksAsync` and enqueuing newly unblocked tasks) and synchronizes badge counts.

---

## 4. Implementation Checklist & Deliverables

- [ ] **Untrusted Intake & Triage (`GitHubIssueSyncService`):**
  - [ ] Extend `TriageReportRequest` and `TriageService` to propagate `Origin` (`TaskOrigin.GitHubIssue`), `ExternalSource` (`"GitHub"`), and `ExternalReferenceId` (`issue.Number.ToString()`).
  - [ ] Implement `GitHubIssueSyncService` and register Hangfire recurring poll job honoring `PollingIntervalMinutes`.
  - [ ] Filter out GitHub Pull Requests from issue listing, enforce deduplication by `(WorkspaceId, ExternalSource, ExternalReferenceId)` plus `ncf:triaged` tracking so 0-proposal issues are never re-triaged in an infinite loop, and route new issues through `ITriageService.TriageExternalReportAsync` in `RestrictedReadOnly` sandbox.
- [ ] **Task Mirroring (`GitHubTaskMirrorService`):**
  - [ ] Implement `GitHubTaskMirrorService` to synchronize `ncf:*` status labels and post milestone comments on `Open` promotion, `TaskReview` completion, `PostImplementationGates` completion, `Ready` transition, and `Closed` transition when `TaskMirroringEnabled` is active.
  - [ ] Implement PR merge polling when `CloseTaskOnPrMerge` is enabled, performing worktree/container cleanup, markdown archiving, and downstream DAG cascade unblocking.
- [ ] **Unit & Mock Tests:**
  - [ ] Unit tests for `GitHubIssueSyncService` verifying PR filtering, issue deduplication (including 0-proposal non-actionable issues), and `TriageReportRequest` dispatch into `RestrictedReadOnly` triage with `TaskOrigin.GitHubIssue`.
  - [ ] Unit tests for `GitHubTaskMirrorService` verifying stage-based label transitions (`ncf:backlog`, `ncf:in-progress`, `ncf:gates-passed`, `ncf:ready-for-review`, `ncf:closed`), milestone comment formatting, and PR-merge task closure + DAG cascade unblocking.
