# 🐙 Step 12.11B: GitHub Untrusted Issue Intake Triage & Bi-Directional Task Mirroring

> **Feature Implementation Step (Part 2 of 2 — Inbound Issue Triage & Bi-Directional Sync)**  
> *Parent Plan:* [Features & Integrations Roadmap](README.md)  
> *Prerequisite Step:* [Step 12.11A: GitHub Client, Settings & PR Delivery](STEP_12_11A_GITHUB_CLIENT_SETTINGS_AND_PR_DELIVERY.md)  
> *Project Target:* `src/NightCodeForge.Infrastructure/GitHub`, `src/NightCodeForge.Agents/Triage`, `src/NightCodeForge.Core/Workflow`, `tests/NightCodeForge.Infrastructure.Tests` | *Status:* Planned | *Target Framework:* .NET 10 (C# 13)  
> *Aligns With:* [AGENTS.md](../../AGENTS.md), [docs/PLAN.md](../PLAN.md)

---

## 1. Executive Objective

Building on `IGitHubClientService` and `GitHubIntegrationSettings` from [Step 12.11A](STEP_12_11A_GITHUB_CLIENT_SETTINGS_AND_PR_DELIVERY.md), this step (Part 2 of Step 12.11) implements inbound **Untrusted Issue Intake & Automated Triage** and **Bi-Directional Task Mirroring**:

1. **Untrusted Issue Intake & Automated Triage (`GitHubIssueSyncService`):**
   - GitHub issues from external contributors or bug reports are treated as **untrusted external inputs**.
   - Instead of blindly mapping 1:1 to executable tasks, discovered issues are routed directly into [`ITriageService.TriageExternalReportAsync`](../../src/NightCodeForge.Agents/Triage/TriageService.cs) executing strictly in `SandboxSecurityTier.RestrictedReadOnly` and `AgentMode.ReadOnly`.
   - Vetted task proposals are saved in `TaskOverallStatus.Suggested` with `ExternalSource = "GitHub"` and `ExternalReferenceId = issue.Number.ToString()`.
2. **Task Mirroring & Bi-Directional State Synchronization (`GitHubTaskMirrorService`):**
   - Automatically create or link a corresponding GitHub Issue when a task is promoted to `Open`.
   - Mirror stage progression through labels (`ncf:backlog`, `ncf:in-progress`, `ncf:gates-passed`, `ncf:ready-for-review`, `ncf:closed`) and optional milestone comments.
   - Synchronize task closure when a linked GitHub PR or Issue is merged/closed.

---

## 2. Untrusted Issue Intake & Triage Specification (`GitHubIssueSyncService`)

1. **Discovery:** Recurring Hangfire job `GitHubIssueSyncService.SyncOpenIssuesAsync` queries `IGitHubClientService.ListIssuesAsync` for open issues matching `GitHubIntegrationSettings.IssueFilterLabel`.
2. **Deduplication:** Verifies that issue `#number` has not already been triaged or linked to an existing `ForgeTask` (`ExternalSource == "GitHub" && ExternalReferenceId == issue.Number.ToString()`).
3. **Untrusted Triage Execution:**
   - Wraps the issue payload into `TriageReportRequest`:
     - `Title = issue.Title`
     - `Description = issue.Body`
     - `BaseBranch = workspace.DefaultBaseBranch`
     - `Metadata["Source"] = "GitHub"`
     - `Metadata["IssueNumber"] = issue.Number.ToString()`
   - Dispatches to [`ITriageService.TriageExternalReportAsync`](../../src/NightCodeForge.Agents/Triage/TriageService.cs) (`AgentMode.ReadOnly`, `SandboxSecurityTier.RestrictedReadOnly`).
4. **Backlog Population & Issue Acknowledgment:**
   - Saves proposed tasks as `ForgeTask` in `TaskOverallStatus.Suggested` linked to `ExternalReferenceId = issue.Number.ToString()`.
   - Optionally posts an acknowledgment comment on the GitHub issue when `PostMilestoneComments` is enabled.

---

## 3. Task Mirroring Specification (`GitHubTaskMirrorService`)

When `TaskMirroringEnabled` is true and a `ForgeTask` changes status or advances stages:

| ForgeTask State | GitHub Issue Label | Action / Comment |
| :--- | :--- | :--- |
| **`Open`** | `ncf:backlog` | Create or link GitHub Issue; mark as accepted into backlog. |
| **`Reviewed`** | `ncf:in-progress` | Post comment with generated task implementation plan. |
| **`Implemented`** | `ncf:gates-passed` | Update labels; post deterministic test & coverage metrics. |
| **`Ready`** | `ncf:ready-for-review` | Ready for maintainer review or PR creation. |
| **`Closed`** | `ncf:closed` | Close GitHub Issue with reference to commit SHA or merged PR. |

When `CloseTaskOnPrMerge` is enabled, polling detects merged PRs (`PullRequestUrl` / `ExternalReferenceId`) and transitions the corresponding `ForgeTask` to `TaskOverallStatus.Closed` (triggering DAG cascade unblocking).

---

## 4. Implementation Checklist & Deliverables

- [ ] **Untrusted Intake & Triage (`GitHubIssueSyncService`):**
  - [ ] Implement `GitHubIssueSyncService` and register Hangfire recurring poll job.
  - [ ] Enforce deduplication by `(WorkspaceId, ExternalSource, ExternalReferenceId)` and route new issues through `ITriageService.TriageExternalReportAsync` in `RestrictedReadOnly` sandbox.
- [ ] **Task Mirroring (`GitHubTaskMirrorService`):**
  - [ ] Implement `GitHubTaskMirrorService` to synchronize `ncf:*` status labels and post milestone comments on stage transitions when `TaskMirroringEnabled` is active.
  - [ ] Implement PR merge / issue closure synchronization when `CloseTaskOnPrMerge` is enabled.
- [ ] **Unit & Mock Tests:**
  - [ ] Unit tests for `GitHubIssueSyncService` verifying issue deduplication and `TriageReportRequest` dispatch into `RestrictedReadOnly` triage.
  - [ ] Unit tests for `GitHubTaskMirrorService` verifying label transitions, milestone comment formatting, and PR-merge task closure.

