# 🐙 Step 12.11A: GitHub Client, Workspace Settings & Pull Request Delivery (`MergeMode.ExternalPullRequest`)

> **Feature Implementation Step (Part 1 of 2 — GitHub Client, Settings UI & Outbound PR Delivery)** *(formerly Step 11.10)*  
> *Parent Plan:* [Features & Integrations Roadmap](README.md)  
> *Companion Step:* [Step 12.11B: GitHub Untrusted Issue Intake & Bi-Directional Task Mirroring](STEP_12_11B_GITHUB_UNTRUSTED_ISSUE_TRIAGE_AND_TASK_MIRRORING.md)  
> *Project Target:* `src/NightCodeForge.Infrastructure/GitHub`, `src/NightCodeForge.Core/Workflow`, `src/NightCodeForge.Web`, `tests/NightCodeForge.Infrastructure.Tests` | *Status:* Planned | *Target Framework:* .NET 10 (C# 13)  
> *Aligns With:* [AGENTS.md](../../AGENTS.md), [docs/PLAN.md](../PLAN.md)

---

## 1. Executive Objective

Provide a modular, **fully optional** GitHub client, workspace configuration UI, and outbound **Pull Request Delivery Mode (`MergeMode.ExternalPullRequest`)**:

1. **GitHub Client Abstraction (`IGitHubClientService`) & Workspace Settings (`GitHubIntegrationSettings`):**
   - Provide a clean, DI-registered GitHub client (`HttpClient` or `Octokit`) with fail-fast validation and per-workspace toggles in `/settings`.
2. **Pull Request Delivery Mode via `MergeMode.ExternalPullRequest`:**
   - Provide an alternative to direct squash-and-merge into the developer's local working copy: deliver completed tasks via **GitHub Pull Requests**.
   - Pushes the isolated feature branch (`feature/task-{id}`) to the remote GitHub repository.
   - Opens a GitHub Pull Request against `task.BaseBranch` with an automated markdown summary detailing the task objective, stage execution history, and deterministic quality gate metrics (coverage delta, warning ratchet, test results).
   - Surfaces a **"Create / Open Pull Request"** link in the Review Room UI (`Review.razor`).

*(Inbound untrusted GitHub Issue triage and bi-directional `ncf:*` label/comment mirroring are specified in [Step 12.11B](STEP_12_11B_GITHUB_UNTRUSTED_ISSUE_TRIAGE_AND_TASK_MIRRORING.md).)*

---

## 2. Configuration & Domain Extensions

### 2.1 Workspace Configuration (`GitHubIntegrationSettings`)
Stored in workspace settings or JSON configuration:
```csharp
public class GitHubIntegrationSettings
{
    public bool Enabled { get; set; } = false;
    public string? RepositoryOwner { get; set; }
    public string? RepositoryName { get; set; }
    public string? ApiToken { get; set; } // Or reference to environment variable GITHUB_TOKEN
    
    // Sub-feature A: Untrusted Issue Intake & Triage (Step 12.11B)
    public bool IssueTriageEnabled { get; set; } = false;
    public string? IssueFilterLabel { get; set; }
    public int PollingIntervalMinutes { get; set; } = 15;

    // Sub-feature B: PR Delivery (Step 12.11A)
    public bool PullRequestDeliveryEnabled { get; set; } = false;
    public bool CreateAsDraft { get; set; } = false;
    public bool CloseTaskOnPrMerge { get; set; } = true;

    // Sub-feature C: Task Mirroring (Step 12.11B)
    public bool TaskMirroringEnabled { get; set; } = false;
    public bool SyncStatusLabels { get; set; } = true;
    public bool PostMilestoneComments { get; set; } = true;
}
```

### 2.2 Domain Alignment with `MergeMode`
GitHub PR delivery is unified with the core **[`MergeMode`](../../src/NightCodeForge.Core/Domain/Enums.cs)** enumeration:
```csharp
public enum MergeMode
{
    Automatic = 0,             // Auto-squashes and merges to base branch once status reaches Ready
    ManualReviewRequired = 1,  // Maintainer manually reviews diff/tests before triggering merge in UI
    ExternalPullRequest = 2    // Pushes branch to origin and opens a GitHub PR (enabled when GitHub is active)
}
```
- **`ForgeTask.MergeMode`:** Set to `MergeMode.ExternalPullRequest` when PR delivery is selected.
- **`ForgeTask.ExternalSource`:** `"GitHub"`.
- **`ForgeTask.ExternalReferenceId`:** `#123` (Issue or PR number).
- **`ForgeTask.PullRequestUrl`:** URL to created GitHub PR (e.g. `https://github.com/owner/repo/pull/45`).
- **UI Availability:** When `GitHubIntegrationSettings.Enabled && PullRequestDeliveryEnabled` is true, `MergeMode.ExternalPullRequest` is enabled as an option across `/tasks/new`, backlog promotion modals, proposal cards, and the task detail drawer.

---

## 3. Pull Request Delivery Specification

### 3.1 Pull Request Generation Flow
When a task reaches completion and `task.MergeMode == MergeMode.ExternalPullRequest`:
1. **Push Branch to GitHub Remote:** [`TaskOrchestrator`](../../src/NightCodeForge.Core/Workflow/TaskOrchestrator.cs) pushes `feature/task-{id}` directly to the remote GitHub repository (using an authenticated remote URL or configured `github` remote), ensuring the feature branch is published upstream to GitHub rather than solely to the local clone.
2. **Build PR Body:** Formats a structured PR description:
   ```markdown
   ## 🚀 Autonomous Implementation: Task #{TaskId} - {TaskTitle}
   
   Resolves #{IssueNumber}
   
   ### 📋 Summary
   {TaskDescription}
   
   ### 🛡️ Quality Gate Verification Results
   - **Build Status:** ✅ Passed
   - **Test Suite:** ✅ Passed (0 failures)
   - **Coverage Delta:** 📈 +1.2% (Baseline: 82.1% -> Current: 83.3%)
   - **Linter / Warning Ratchet:** ✅ 0 new compiler warnings
   
   ---
   *Generated by [NightCodeForge](https://github.com/jetelain/NightCodeForge)*
   ```
3. **Open Pull Request:** Invokes `IGitHubClientService.CreatePullRequestAsync` (`POST /repos/{owner}/{repo}/pulls`) with `head = feature/task-{id}`, `base = task.BaseBranch`, and stores `task.PullRequestUrl`.
4. **UI Presentation:** [`Review.razor`](../../src/NightCodeForge.Web/Components/Pages/Review.razor) renders an **"Open GitHub Pull Request"** badge/button linking to `task.PullRequestUrl`.

---

## 4. Implementation Checklist & Deliverables

- [ ] **GitHub Client Abstraction (`IGitHubClientService`):**
  - [ ] Implement client in `NightCodeForge.Infrastructure/GitHub` using `HttpClient` or `Octokit` with strict constructor DI and fail-fast validation (`CreatePullRequestAsync`, `ListIssuesAsync`, `CreateIssueAsync`, `UpdateIssueAsync`).
- [ ] **PR Delivery via `MergeMode.ExternalPullRequest`:**
  - [ ] Extend [`TaskOrchestrator`](../../src/NightCodeForge.Core/Workflow/TaskOrchestrator.cs) and `GitWorkspaceManager` to push `feature/task-{id}` to the remote GitHub repository and create rich markdown PRs when `MergeMode == MergeMode.ExternalPullRequest`.
- [ ] **Blazor UI Integration:**
  - [ ] Add GitHub Integration workspace settings tab (`Owner`, `Repo`, `ApiToken`, sub-feature toggles).
  - [ ] Enable `MergeMode.ExternalPullRequest` option in task creation/approval selectors when `PullRequestDeliveryEnabled` is active.
  - [ ] Render "Open GitHub Pull Request" link in `Review.razor` and `TaskDetail.razor`.
- [ ] **Unit & Mock Tests:**
  - [ ] Unit tests for `IGitHubClientService`, PR body markdown formatting, and `TaskOrchestrator` `MergeMode.ExternalPullRequest` execution.

