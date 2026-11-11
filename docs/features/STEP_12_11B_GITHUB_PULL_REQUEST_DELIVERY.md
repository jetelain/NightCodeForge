# 🐙 Step 12.11B: GitHub Outbound Pull Request Delivery (`MergeMode.ExternalPullRequest`)

> **Feature Implementation Step (Part 2 of 4 — Outbound Pull Request Delivery & Review Room Integration)**  
> *Parent Plan:* [Features & Integrations Roadmap](README.md)  
> *Prerequisite Step:* [Step 12.11A: GitHub App Device Flow, Token Store, Client & Settings](STEP_12_11A_GITHUB_CLIENT_DEVICE_AUTH_AND_SETTINGS.md)  
> *Companion Steps:* [Step 12.11C: Untrusted Issue Intake & Triage](STEP_12_11C_GITHUB_UNTRUSTED_ISSUE_INTAKE_AND_TRIAGE.md) | [Step 12.11D: Bi-Directional Task Mirroring & Multi-User Claiming](STEP_12_11D_GITHUB_TASK_MIRRORING_AND_MULTI_USER_CLAIMING.md)  
> *Project Target:* `src/NightCodeForge.Core`, `src/NightCodeForge.Infrastructure/Git`, `src/NightCodeForge.Infrastructure/GitHub`, `src/NightCodeForge.Agents`, `src/NightCodeForge.Web`, `tests/NightCodeForge.Core.Tests`, `tests/NightCodeForge.Infrastructure.Tests` | *Status:* Planned | *Target Framework:* .NET 10 (C# 13)  
> *Aligns With:* [AGENTS.md](../../AGENTS.md), [docs/PLAN.md](../PLAN.md)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Objective

Building on `IGitHubClientService`, `IGitHubDeviceAuthService`, `IGitHubTokenStore`, and `GitHubIntegrationSettings` from [Step 12.11A](STEP_12_11A_GITHUB_CLIENT_DEVICE_AUTH_AND_SETTINGS.md), this step (Part 2 of Step 12.11) implements outbound **Pull Request Delivery Mode (`MergeMode.ExternalPullRequest`)** with multi-user NightCodeForge collaboration safeguards:

1. **Pull Request Delivery Mode via `MergeMode.ExternalPullRequest`:**
   - Provide a third task delivery mode alongside `MergeMode.Automatic` and `MergeMode.ManualReviewRequired`: deliver completed tasks via **GitHub Pull Requests**.
2. **Multi-User NCF Collaboration Safeguards:**
   - Because multiple developers on the same team may each run their own NightCodeForge instance against the same GitHub repository (each with an independent local SQLite database and auto-incrementing `task.Id`), outbound PR delivery enforces:
     - **User-Namespaced Remote Branches (`ncf/{githubLogin}/task-{id}`):** Pushes the isolated local feature branch (`feature/task-{id}`) to `refs/heads/ncf/{sanitizedLogin}/task-{id}` on GitHub (`https://github.com/{RepositoryOwner}/{RepositoryName}.git`) using ephemeral HTTP header authentication (with mandatory log/exception redaction in [`GitCliRunner`](../../src/NightCodeForge.Infrastructure/Git/GitCliRunner.cs)) without persisting tokens in `.git/config`, preventing remote branch collisions when two developers both have a local `Task #12`.
     - **Pathspec-Safe Exclusion of Local `docs/tasks/*.md` State Files from Outbound PRs:** Strips/reverts local NCF task tracking markdown files (`docs/tasks/*.md`) from the pushed branch before opening/updating the PR so that merging the PR into `main` never pollutes the shared repository with active local task files or triggers duplicate task ingestion in teammates' NCF instances.
     - **Remote Base Branch Sync (Protected Against `PullFromHostAsync` Reset Wipe):** Fetches the latest `task.BaseBranch` from GitHub before branching/baseline verification—while guarding [`GitMirrorSyncService.PullFromHostAsync`](../../src/NightCodeForge.Infrastructure/Git/GitMirrorSyncService.cs) with an ancestor check (`git merge-base --is-ancestor`)—so tasks build on top of teammates' latest merged PRs.
3. **Automated PR Creation & Review Room UI Integration:**
   - Opens (or updates) a GitHub Pull Request against `task.BaseBranch` with an automated markdown summary detailing the task objective, stage execution history, and deterministic quality gate metrics (coverage delta, warning ratchet, test results).
   - Surfaces **"Open GitHub Pull Request"** / **"Create GitHub Pull Request"** actions in the Review Room UI ([`Review.razor`](../../src/NightCodeForge.Web/Components/Pages/Review.razor)) and Task Details ([`TaskDetail.razor`](../../src/NightCodeForge.Web/Components/Pages/TaskDetail.razor) / [`TaskDetailDrawer.razor`](../../src/NightCodeForge.Web/Components/Backlog/TaskDetailDrawer.razor)).

---

## 2. Domain, Persistence & Markdown Sync Alignment (`MergeMode.ExternalPullRequest`)

GitHub PR delivery extends the core **[`MergeMode`](../../src/NightCodeForge.Core/Domain/Enums.cs)** enumeration:
```csharp
public enum MergeMode
{
    Automatic = 0,             // Auto-squashes and merges to base branch once status reaches Ready
    ManualReviewRequired = 1,  // Maintainer manually reviews diff/tests before triggering merge in UI
    ExternalPullRequest = 2    // Pushes branch to GitHub remote and opens a GitHub PR (enabled when GitHub PR delivery is active)
}
```

### 2.1 [`ForgeTask`](../../src/NightCodeForge.Core/Domain/ForgeTask.cs) Properties & Database Persistence
- `ForgeTask.MergeMode`: Set to `MergeMode.ExternalPullRequest` when PR delivery is selected.
- `ForgeTask.ExternalSource`: External provider identifier (e.g. `"GitHub"`), nullable `string?` (max length `64`).
- `ForgeTask.ExternalReferenceId`: Unprefixed external issue identifier (e.g. `"123"` for GitHub Issue `#123`), nullable `string?` (max length `128`). Kept distinct from Pull Request tracking so a task originating from Issue `#123` can also open Pull Request `#45` without overwriting its upstream issue link.
- `ForgeTask.PullRequestNumber`: Created GitHub Pull Request number (e.g. `45`), nullable `int?`.
- `ForgeTask.PullRequestUrl`: Full URL to the created GitHub PR (e.g. `"https://github.com/owner/repo/pull/45"`), nullable `string?` (max length `1024`).
- Copy and preserve these properties in:
  - [`TaskWorkspacePreparer.CopyTaskMetadata`](../../src/NightCodeForge.Core/Workflow/TaskWorkspacePreparer.cs)
  - [`EfCoreTaskRepository.UpdateAsync`](../../src/NightCodeForge.Infrastructure/Data/Repositories/EfCoreTaskRepository.cs) (including the `existing.Version > task.Version` optimistic-concurrency fallback block)
  - [`InMemoryTaskRepository`](../../src/NightCodeForge.Core/Repositories/ITaskRepository.cs)
  - [`TaskReviewSnapshot`](../../src/NightCodeForge.Core/Domain/TaskReviewModels.cs) (`PullRequestNumber` and `PullRequestUrl`)

### 2.2 Markdown Sync, Backlog Tooling & Notification Alignment
- **Markdown Sync (`docs/tasks/*.md`):**
  - Update [`TaskMarkdownSyncService`](../../src/NightCodeForge.Core/Services/TaskMarkdownSyncService.cs) and [`TaskFrontmatter`](../../src/NightCodeForge.Core/Services/TaskFrontmatter.cs) to serialize and parse `MergeMode.ExternalPullRequest` as `"external_pull_request"` (alongside `"automatic"` and `"manual_review_required"`), and preserve `external_source`, `external_reference_id`, `pull_request_number`, and `pull_request_url` in frontmatter, content hashing (`ComputeTaskContentHash`), and 3-way disk sync (`SyncAllTasksFromDiskAsync`).
- **Backlog Agent Tools:**
  - Update [`BacklogMutationToolService`](../../src/NightCodeForge.Agents/Tools/BacklogMutationToolService.cs) (`EditTaskAsync` / `ProposeTasksAsync`) to accept both `"ExternalPullRequest"` and `"external_pull_request"`, and reject it with a clear validation error when `PullRequestDeliveryEnabled` is `false` on the workspace.
- **Notifications & Badges:**
  - Update [`WebNotificationService`](../../src/NightCodeForge.Web/Services/WebNotificationService.cs) so `NotificationSubject.ReviewRoom` notifications fire when `task.MergeMode is MergeMode.ManualReviewRequired or MergeMode.ExternalPullRequest`.
  - Update [`KanbanCard.razor`](../../src/NightCodeForge.Web/Components/Backlog/KanbanCard.razor) and [`DagVisualizer.razor`](../../src/NightCodeForge.Web/Components/Backlog/DagVisualizer.razor) to render `"PR"` for `MergeMode.ExternalPullRequest` instead of assuming a binary `Automatic ? "Auto" : "Manual"` check.
- **UI Availability:**
  - When `workspace.GitHubSettings is { Enabled: true, PullRequestDeliveryEnabled: true }` and a valid token is present in `IGitHubTokenStore`, `MergeMode.ExternalPullRequest` is enabled as an option across `/tasks/new` ([`CreateTask.razor`](../../src/NightCodeForge.Web/Components/Pages/CreateTask.razor)), the Backlog creation modal ([`TaskCreationModal.razor`](../../src/NightCodeForge.Web/Components/Backlog/TaskCreationModal.razor)), backlog promotion modals ([`MaintainerPromotionModal.razor`](../../src/NightCodeForge.Web/Components/Backlog/MaintainerPromotionModal.razor)), proposal cards, [`TaskDetail.razor`](../../src/NightCodeForge.Web/Components/Pages/TaskDetail.razor), and [`TaskDetailDrawer.razor`](../../src/NightCodeForge.Web/Components/Backlog/TaskDetailDrawer.razor).

---

## 3. Pull Request Delivery & Multi-User Collaboration Specification

### 3.1 Multi-User NCF Collaboration Model
When multiple developers on the same team use NightCodeForge against a shared GitHub repository (`{RepositoryOwner}/{RepositoryName}`), each developer runs an independent NCF instance with its own local SQLite database:
1. **Remote Branch Disambiguation (`ncf/{githubLogin}/task-{id}`):**
   - Local `ForgeTask.Id` values (`1, 2, 3...`) are scoped to each developer's local SQLite database. If Developer A (`alice`) and Developer B (`bob`) both push `refs/heads/feature/task-5` to GitHub, their branches will collide.
   - To guarantee zero remote branch collisions while keeping local worktree branch naming (`feature/task-{id}`) unchanged, the remote branch ref pushed to GitHub is always namespaced with the authenticated GitHub user's login (`AuthenticatedUserLogin` from `IGitHubTokenStore`, sanitized to lowercase alphanumeric and `-`, falling back to `"user"` only if unavailable):
     $$\text{RemoteHeadBranch} = \texttt{ncf/\{sanitizedLogin\}/task-\{taskId\}}$$
2. **Preventing `docs/tasks/*.md` Leakage Across NCF Instances (Pathspec-Safe & Idempotent Across Re-Reviews):**
   - During Stage 1 (`task-review`), the agent updates the task's markdown specification in `docs/tasks/YYYY-MM-DD-<slug>.md` inside the task worktree, and `TaskMarkdownSyncService.SyncAllTasksFromDiskAsync` imports that updated specification into `ForgeTask.Description` in SQLite.
   - In `MergeMode.ExternalPullRequest`, the PR is opened while the task is still in `TaskOverallStatus.Ready` (before `ArchiveTaskMarkdownAsync` deletes the file on task closure). If `docs/tasks/*.md` were included in the pushed PR branch and merged into `main` on GitHub, every other NCF user pulling `main` would ingest that unarchived `status: Open` file with a colliding local `id:`!
   - Therefore, immediately before every push of the task branch to GitHub (including re-entries after a maintainer clicks **Request Changes** and Stage 3's `CreateWipCheckpointAsync` runs `git add -A`), the PR delivery service **must strip any `docs/tasks/` additions or modifications from the outbound commit** on the task branch so `docs/tasks/` matches `task.BaseBranch`:
     - Because `docs/tasks/` typically does not exist at all in `task.BaseBranch`'s Git tree on GitHub (where `git checkout <baseBranch> -- docs/tasks` would fail with `error: pathspec 'docs/tasks' did not match any file(s) known to git`), the cleanup must first remove tracked `docs/tasks/` entries from the index (`git rm -rf --ignore-unmatch docs/tasks`), then restore any `docs/tasks/` entries that *do* exist in `<baseBranch>` (`if git ls-tree <baseBranch> -- docs/tasks` is non-empty, run `git checkout <baseBranch> -- docs/tasks`), and commit the cleanup if `git status --porcelain -- docs/tasks` is non-empty.
3. **Syncing Latest Remote `BaseBranch` Before Task Branching (Without `PullFromHostAsync` Reset Wipe):**
   - When `workspace.GitHubSettings is { Enabled: true, PullRequestDeliveryEnabled: true }`, during [`TaskWorkspacePreparer.PrepareTaskBranchAndMirrorAsync`](../../src/NightCodeForge.Core/Workflow/TaskWorkspacePreparer.cs) (and prior to Stage 2 `PreImplementationBaseline`), fetch the latest `task.BaseBranch` from `https://github.com/{RepositoryOwner}/{RepositoryName}.git` into the canonical clone (and fast-forward local `BaseBranch` when clean) so new task branches always start from teammates' latest merged GitHub commits.
   - **Critical `PullFromHostAsync` Guard:** In [`GitMirrorSyncService.PullFromHostAsync`](../../src/NightCodeForge.Infrastructure/Git/GitMirrorSyncService.cs), `git merge --ff-only host-origin/{baseBranch}` currently falls back to a destructive `git reset --hard host-origin/{baseBranch}` on non-zero exit. If the canonical ext4 mirror fast-forwards `BaseBranch` from GitHub, `ext4Mirror/{baseBranch}` will be *ahead* of `host-origin/{baseBranch}` (the Windows host repo), causing `--ff-only` to fail and `reset --hard` to wipe the fetched GitHub commits! Therefore:
     1. Fetch from GitHub *after* `PullFromHostAsync` in `PrepareTaskBranchAndMirrorAsync` (or push the fast-forwarded `BaseBranch` back to `host-origin` when clean), AND
     2. Update [`GitMirrorSyncService.PullFromHostAsync`](../../src/NightCodeForge.Infrastructure/Git/GitMirrorSyncService.cs) to check whether `host-origin/{baseBranch}` is already an ancestor of `HEAD` (`git merge-base --is-ancestor host-origin/{baseBranch} HEAD`) before ever executing `git reset --hard host-origin/{baseBranch}`.

### 3.2 Pull Request Generation & Lifecycle Flow
To respect the maximum 10-constructor-parameter rule in [AGENTS.md](../../AGENTS.md) (since [`TaskMergeAndCascadeCoordinator`](../../src/NightCodeForge.Core/Workflow/TaskMergeAndCascadeCoordinator.cs) already has 10 parameters and [`TaskOrchestrator`](../../src/NightCodeForge.Core/Workflow/TaskOrchestrator.cs) has 9), encapsulate outbound PR delivery in a cohesive service routed via [`ITaskWorkspacePreparer`](../../src/NightCodeForge.Core/Workflow/ITaskWorkspacePreparer.cs):

When a task completes all verification stages (`PostReviewVerification` passes) and `task.MergeMode == MergeMode.ExternalPullRequest`:
1. **Clean Outbound Branch & Push to GitHub Remote (Ephemeral Auth with `GitCliRunner` Redaction, Ext4 Mirror Compatible):**
   - Ensure `docs/tasks/` changes on `feature/task-{id}` are stripped/reverted to match `task.BaseBranch` (using the pathspec-safe `git rm -rf --ignore-unmatch docs/tasks` + conditional `git checkout <baseBranch> -- docs/tasks` flow) so local NCF task state files are never pushed to the shared GitHub repository.
   - Compute `remoteBranch = $"ncf/{SanitizeGitHubLogin(tokenPayload.AuthenticatedUserLogin)}/task-{task.Id}"`.
   - Because `origin` in the canonical ext4 clone (`Ext4MirrorPath`) points to the local host repository path rather than GitHub, push `feature/task-{id}` directly to `https://github.com/{RepositoryOwner}/{RepositoryName}.git` onto `refs/heads/{remoteBranch}`.
   - Authenticate the `git push` command using the refresh-aware access token from `IGitHubDeviceAuthService.GetValidAccessTokenAsync` via an ephemeral per-command HTTP header (`git -c http.extraHeader="Authorization: Basic <base64(x-access-token:token)>" push https://github.com/{owner}/{repo}.git feature/task-{id}:refs/heads/{remoteBranch} --force-with-lease`) so the short-lived `ghu_...` token is **never** written into `.git/config`.
   - **Mandatory Credential Redaction in [`GitCliRunner`](../../src/NightCodeForge.Infrastructure/Git/GitCliRunner.cs):** Because [`GitCliRunner.ExecuteAsync`](../../src/NightCodeForge.Infrastructure/Git/GitCliRunner.cs) logs `displayArgs` and passes `argsArray` into [`GitCommandException`](../../src/NightCodeForge.Infrastructure/Git/GitCommandException.cs) (which is persisted into `ExecutionLog` in SQLite and streamed to the UI), `GitCliRunner` must redact any argument or header containing `http.extraHeader=` or `Authorization:` (`http.extraHeader="Authorization: [REDACTED]"`) across all log statements, exception messages, and `GitCommandException.Arguments`.
   - Also sync the feature branch to the local host repository (`PushToHostAsync`) so the developer's local repository has the branch ref.
2. **Build PR Title & Body (Shared Quality Gate Metrics Extraction):**
   - Extract deterministic quality gate metrics from `PipelineDataBag` using a shared helper in `NightCodeForge.Core` (consolidating the logic currently in [`TaskReviewService.ExtractGateMetrics`](../../src/NightCodeForge.Web/Services/TaskReviewService.cs)).
   - Format the PR title using the task title (and issue reference when `ExternalSource == "GitHub"` and `ExternalReferenceId` is populated, e.g. `"{TaskTitle} (#{ExternalReferenceId})"` rather than exposing only the developer-local `Task #{TaskId}`).
   - Format a structured PR description (including `Resolves #{ExternalReferenceId}` only when `ExternalSource == "GitHub"` and `ExternalReferenceId` is populated):
     ```markdown
     ## 🚀 {TaskTitle}
     
     Resolves #{IssueNumber}
     
     ### 📋 Summary
     {TaskDescription}
     
     ### 🛡️ Quality Gate Verification Results
     - **Build Status:** ✅ Passed
     - **Test Suite:** ✅ Passed ({PassedTests} / {TotalTests}, 0 failures)
     - **Coverage Delta:** 📈 +1.2% (Baseline: 82.1% -> Current: 83.3%)
     - **Linter / Warning Ratchet:** ✅ 0 new compiler warnings
     
     ---
     *Generated via [NightCodeForge](https://github.com/jetelain/NightCodeForge) by @{AuthenticatedUserLogin} (Local Task #{TaskId})*
     ```
3. **Open or Update Pull Request & Transition to `Ready`:**
   - If `task.PullRequestNumber` / `task.PullRequestUrl` is not yet set, invoke `IGitHubClientService.CreatePullRequestAsync` (`POST /repos/{owner}/{repo}/pulls`) with `head = remoteBranch` (`ncf/{sanitizedLogin}/task-{id}`), `base = task.BaseBranch`, `draft = settings.CreateAsDraft`, and store `task.PullRequestNumber` and `task.PullRequestUrl`.
   - If `task.PullRequestNumber` already exists (e.g., after a maintainer "Request Changes" loopback), pushing `feature/task-{id}:refs/heads/{remoteBranch}` updates the PR head automatically, and `IGitHubClientService` updates the PR body/title with the latest gate metrics.
   - Transition `task.Status` to `TaskOverallStatus.Ready` so the task remains visible in the Review Room while the PR is open on GitHub.
4. **Review Room UI Presentation (`Review.razor`):**
   - When `_snapshot.MergeMode == MergeMode.ExternalPullRequest` (or whenever `_snapshot.PullRequestUrl` is populated):
     - Render an **"Open GitHub Pull Request (#{PullRequestNumber})"** button/badge linking to `_snapshot.PullRequestUrl` (`target="_blank"`).
     - If `_snapshot.PullRequestUrl` is not yet created for a `Ready` task (e.g., if switched to `ExternalPullRequest` in `Ready` state), render a **"Create GitHub Pull Request"** button to trigger PR delivery on demand.
     - Retain **"Request Changes"** (to loop back to `Implementation`) and allow the maintainer to either squash-merge locally (`Approve & Merge`) or close the task once the external PR is merged.

---

## 4. Implementation Checklist & Deliverables

- [ ] **Domain Model, EF Core Migration & Markdown Sync:**
  - [ ] Add `MergeMode.ExternalPullRequest = 2` and `ExternalSource`, `ExternalReferenceId`, `PullRequestNumber`, and `PullRequestUrl` on [`ForgeTask`](../../src/NightCodeForge.Core/Domain/ForgeTask.cs).
  - [ ] Add EF Core migration and update `AppDbContext.BaselineExistingPreMigrationDatabaseAsync`, `EfCoreTaskRepository.UpdateAsync`, `InMemoryTaskRepository`, `TaskWorkspacePreparer.CopyTaskMetadata`, `TaskMarkdownSyncService`, `BacklogMutationToolService`, `WebNotificationService`, `KanbanCard.razor`, and `DagVisualizer.razor`.
- [ ] **Git Plumbing Hardening (`GitCliRunner` & `GitMirrorSyncService`):**
  - [ ] Redact `http.extraHeader` / `Authorization:` tokens in [`GitCliRunner`](../../src/NightCodeForge.Infrastructure/Git/GitCliRunner.cs) logs and `GitCommandException`.
  - [ ] Guard [`GitMirrorSyncService.PullFromHostAsync`](../../src/NightCodeForge.Infrastructure/Git/GitMirrorSyncService.cs) with an ancestor check (`git merge-base --is-ancestor`) so fetched GitHub commits on `BaseBranch` are never wiped by `reset --hard`.
- [ ] **PR Delivery Orchestration (Multi-User NCF Safe):**
  - [ ] Extract shared quality gate metrics helper in `NightCodeForge.Core`.
  - [ ] Implement remote base branch fetch, pathspec-safe stripping of local `docs/tasks/*.md` state files from the outbound PR commit, ephemeral-auth branch push to `refs/heads/ncf/{sanitizedLogin}/task-{id}` on `https://github.com/{Owner}/{Repo}.git`, and automated PR creation/update upon Stage 6 completion without exceeding the 10-constructor-parameter limit on orchestrator services.
- [ ] **Blazor UI Integration:**
  - [ ] Enable `MergeMode.ExternalPullRequest` option in task creation (`CreateTask.razor` and `TaskCreationModal.razor`), promotion (`MaintainerPromotionModal.razor`), and detail selectors (`TaskDetail.razor` and `TaskDetailDrawer.razor`) when `PullRequestDeliveryEnabled` is active.
  - [ ] Render "Open GitHub Pull Request" / "Create GitHub Pull Request" actions in `Review.razor`, `TaskDetail.razor`, and `TaskDetailDrawer.razor`.
- [ ] **Unit & Mock Tests:**
  - [ ] Unit tests for `GitCliRunner` header redaction and `GitMirrorSyncService.PullFromHostAsync` ancestor protection.
  - [ ] Unit tests for PR body markdown formatting, `TaskMarkdownSyncService` `external_pull_request` round-trip, `BacklogMutationToolService` validation, and `MergeMode.ExternalPullRequest` orchestration.

