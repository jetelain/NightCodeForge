# 🐙 Step 12.11C: GitHub Untrusted Issue Intake & Automated Triage (`GitHubIssueSyncService`)

> **Feature Implementation Step (Part 3 of 4 — Untrusted Issue Intake & `RestrictedReadOnly` Triage)**  
> *Parent Plan:* [Features & Integrations Roadmap](README.md)  
> *Prerequisite Steps:* [Step 12.11A: GitHub App Device Flow, Token Store, Client & Settings](STEP_12_11A_GITHUB_CLIENT_DEVICE_AUTH_AND_SETTINGS.md) | [Step 12.11B: Outbound PR Delivery](STEP_12_11B_GITHUB_PULL_REQUEST_DELIVERY.md)  
> *Companion Step:* [Step 12.11D: Bi-Directional Task Mirroring & Multi-User Claiming](STEP_12_11D_GITHUB_TASK_MIRRORING_AND_MULTI_USER_CLAIMING.md)  
> *Project Target:* `src/NightCodeForge.Infrastructure/GitHub`, `src/NightCodeForge.Agents/Triage`, `src/NightCodeForge.Core/Workflow`, `tests/NightCodeForge.Infrastructure.Tests` | *Status:* Planned | *Target Framework:* .NET 10 (C# 13)  
> *Aligns With:* [AGENTS.md](../../AGENTS.md), [docs/PLAN.md](../PLAN.md)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Objective

Building on `IGitHubClientService`, `IGitHubDeviceAuthService`, and `GitHubIntegrationSettings` from [Step 12.11A](STEP_12_11A_GITHUB_CLIENT_DEVICE_AUTH_AND_SETTINGS.md) and the external provenance fields (`ExternalSource`, `ExternalReferenceId`) from [Step 12.11B](STEP_12_11B_GITHUB_PULL_REQUEST_DELIVERY.md), this step (Part 3 of Step 12.11) implements inbound **Untrusted Issue Intake & Automated Triage (`GitHubIssueSyncService`)**—completely isolated from [`TaskOrchestrator`](../../src/NightCodeForge.Core/Workflow/TaskOrchestrator.cs):

1. **Untrusted Issue Intake & Automated Triage (`GitHubIssueSyncService`):**
   - GitHub issues from external contributors or bug reports are treated as **untrusted external inputs**.
   - Instead of blindly mapping 1:1 to executable tasks, discovered issues are routed directly into [`ITriageService.TriageExternalReportAsync`](../../src/NightCodeForge.Agents/Triage/TriageService.cs) executing strictly in `SandboxSecurityTier.RestrictedReadOnly` and `AgentMode.ReadOnly`.
   - Vetted task proposals are saved in `TaskOverallStatus.Suggested` with `Origin = TaskOrigin.GitHubIssue`, `ExternalSource = "GitHub"`, and `ExternalReferenceId = issue.Number.ToString()` (unprefixed numeric string, e.g. `"123"`).
2. **Multi-User NCF Assignee Scoping & 0-Proposal Deduplication:**
   - Filters candidate issues by `IssueTriageScope` (`AssignedToMeOrUnassigned`, `AssignedToMeOnly`, `AllOpenIssues`) so teammates running separate NightCodeForge instances do not step on each other's assigned issues.
   - Prevents infinite re-triage loops on non-actionable (0-proposal) issues and cross-instance duplicate triage via `ncf:*` GitHub labels and the persisted `workspace.GitHubSettings.TriagedIssueNumbers` local watermark.

*(Outbound stage-by-stage `ncf:*` label mirroring, multi-user pre-flight claiming, and PR-merge task closure are specified in [Step 12.11D](STEP_12_11D_GITHUB_TASK_MIRRORING_AND_MULTI_USER_CLAIMING.md).)*

---

## 2. Untrusted Issue Intake & Triage Specification (`GitHubIssueSyncService`)

### 2.1 Discovery & Multi-User Assignee Filtering (`IssueTriageScope`)
- Recurring Hangfire job `GitHubIssueSyncService.SyncOpenIssuesAsync` iterates over workspaces where `GitHubSettings is { Enabled: true, IssueTriageEnabled: true }` (honoring each workspace's `PollingIntervalMinutes` via a per-workspace recurring job ID `github-sync-workspace-{workspace.Id}` updated when workspace settings change, or a global tick checking a per-workspace `LastPolledAtUtc` watermark) and queries `IGitHubClientService.ListIssuesAsync` for open issues (excluding Pull Requests, as GitHub's Issues API returns PRs unless filtered out via `pull_request == null`) matching `GitHubIntegrationSettings.IssueFilterLabel` (when configured).
- **Multi-User NCF Assignee Guard:** When multiple developers on a team run NightCodeForge against the same GitHub repository, `IssueTriageScope` (`AssignedToMeOrUnassigned` by default) filters candidate issues against the authenticated GitHub user (`AuthenticatedUserLogin` from `IGitHubTokenStore`):
  - `AssignedToMeOrUnassigned` (Default): Includes issues that have `Assignees.Count == 0` OR where `Assignees` contains `AuthenticatedUserLogin` (case-insensitive), skipping issues assigned to other teammates.
  - `AssignedToMeOnly`: Includes only issues explicitly assigned to `AuthenticatedUserLogin`.
  - `AllOpenIssues`: Includes all matching open issues regardless of assignee.

### 2.2 Cross-Instance Deduplication (Including Non-Actionable / 0-Proposal Issues)
- Verifies that issue `issue.Number.ToString()` has not already been linked to an existing `ForgeTask` (`WorkspaceId == workspace.Id && ExternalSource == "GitHub" && ExternalReferenceId == issue.Number.ToString()`) and has not already been marked on GitHub or locally:
  - Skips any issue that already carries **any** `ncf:*` lifecycle label (`ncf:triaged`, `ncf:backlog`, `ncf:in-progress`, `ncf:gates-passed`, `ncf:ready-for-review`, `ncf:closed`) on GitHub, OR whose `issue.Number` is already recorded in `workspace.GitHubSettings.TriagedIssueNumbers` (persisted in the workspace's `GitHubSettings` JSON column).
- This guarantees both that:
  1. If the triage agent determines an issue is invalid, non-reproducible, or a duplicate and emits **0 proposals** (even when `SyncStatusLabels == false` and `PostMilestoneComments == false`), recording `issue.Number` in `workspace.GitHubSettings.TriagedIssueNumbers` prevents subsequent polling cycles from re-triaging the same open issue in an infinite loop.
  2. If Developer A's NCF instance has already triaged or started working on Issue `#123`, Developer B's NCF instance will not re-triage it or post duplicate comments.

### 2.3 Untrusted Triage Execution (`TriageReportRequest` & `TriageService`)
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

### 2.4 Backlog Population & Idempotent Issue Acknowledgment
- [`TriageService`](../../src/NightCodeForge.Agents/Triage/TriageService.cs) persists proposed tasks in `TaskOverallStatus.Suggested` with `Origin = request.Origin` (`TaskOrigin.GitHubIssue`), `ExternalSource = request.ExternalSource` (`"GitHub"`), and `ExternalReferenceId = request.ExternalReferenceId` (`issue.Number.ToString()`), and mirrors them to `docs/tasks/*.md`.
- Appends `issue.Number` to `workspace.GitHubSettings.TriagedIssueNumbers` (persisted via `IWorkspaceRepository.UpdateAsync`), marks the GitHub issue as triaged (`ncf:triaged` label when `SyncStatusLabels` is enabled), and optionally posts an acknowledgment comment (embedded with an idempotency marker `<!-- ncf:triaged -->` and `@AuthenticatedUserLogin` attribution) on the GitHub issue when `PostMilestoneComments` is enabled.

---

## 3. Implementation Checklist & Deliverables

- [ ] **Provenance Propagation in `TriageService`:**
  - [ ] Extend [`TriageReportRequest`](../../src/NightCodeForge.Core/Workflow/ITriageService.cs) and [`TriageService`](../../src/NightCodeForge.Agents/Triage/TriageService.cs) to propagate `Origin` (`TaskOrigin.GitHubIssue`), `ExternalSource` (`"GitHub"`), and `ExternalReferenceId` (`issue.Number.ToString()`).
- [ ] **Untrusted Intake & Triage Poller (`GitHubIssueSyncService`):**
  - [ ] Implement `GitHubIssueSyncService` and register Hangfire recurring poll job honoring per-workspace `PollingIntervalMinutes` and `IssueTriageScope` (`AssignedToMeOrUnassigned`, `AssignedToMeOnly`, `AllOpenIssues`).
  - [ ] Filter out GitHub Pull Requests from issue listing, enforce deduplication by `(WorkspaceId, ExternalSource, ExternalReferenceId)`, `workspace.GitHubSettings.TriagedIssueNumbers`, and any `ncf:*` label / `<!-- ncf:triaged -->` tracking so 0-proposal issues (even when `SyncStatusLabels == false`) and issues already handled by another NCF user are never re-triaged.
  - [ ] Route new issues through `ITriageService.TriageExternalReportAsync` in `RestrictedReadOnly` sandbox, persist the updated `TriagedIssueNumbers` watermark, and apply `ncf:triaged` + optional acknowledgment comment.
- [ ] **Unit & Mock Tests:**
  - [ ] Unit tests for `GitHubIssueSyncService` verifying PR filtering, `IssueTriageScope` assignee filtering (`AssignedToMeOrUnassigned`, `AssignedToMeOnly`, `AllOpenIssues`), issue deduplication (existing `ForgeTask`, `TriagedIssueNumbers` local watermark for 0-proposal non-actionable issues, and cross-instance `ncf:*` labels), and `TriageReportRequest` dispatch into `RestrictedReadOnly` triage with `TaskOrigin.GitHubIssue`.

