# 🐙 Step 12.11B: GitHub Outbound Pull Request Delivery (`MergeMode.ExternalPullRequest`)

> **Feature Implementation Step (Part 2 of 4 — Outbound Pull Request Delivery & Review Room Integration)**  
> *Parent Plan:* [Phase 12 Master Plan](../12_EXTEND_FEATURES.md)  
> *Prerequisite Step:* [Step 12.11A: GitHub App Device Flow, Token Store, Client & Settings](STEP_12_11A_GITHUB_CLIENT_DEVICE_AUTH_AND_SETTINGS.md)  
> *Companion Steps:* [Step 12.11C: Untrusted Issue Intake & Triage](STEP_12_11C_GITHUB_UNTRUSTED_ISSUE_INTAKE_AND_TRIAGE.md) | [Step 12.11D: Bi-Directional Task Mirroring & Multi-User Claiming](STEP_12_11D_GITHUB_TASK_MIRRORING_AND_MULTI_USER_CLAIMING.md)  
> *Project Target:* `src/NightCodeForge.Core`, `src/NightCodeForge.Infrastructure/Git`, `src/NightCodeForge.Infrastructure/GitHub`, `src/NightCodeForge.Agents`, `src/NightCodeForge.Web`, `tests/NightCodeForge.Core.Tests`, `tests/NightCodeForge.Infrastructure.Tests`, `tests/NightCodeForge.Web.Tests` | *Status:* Completed | *Target Framework:* .NET 10 (C# 13)  
> *Aligns With:* [AGENTS.md](../../../AGENTS.md), [docs/PLAN.md](../../PLAN.md)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Objective

Building on `IGitHubClientService`, `IGitHubDeviceAuthService`, `IGitHubTokenStore`, and `GitHubIntegrationSettings` from [Step 12.11A](STEP_12_11A_GITHUB_CLIENT_DEVICE_AUTH_AND_SETTINGS.md), this step (Part 2 of Step 12.11) implements outbound **Pull Request Delivery Mode (`MergeMode.ExternalPullRequest`)** with multi-user NightCodeForge collaboration safeguards:

1. **Pull Request Delivery Mode via `MergeMode.ExternalPullRequest`:**
   - Provide a third task delivery mode alongside `MergeMode.Automatic` and `MergeMode.ManualReviewRequired`: deliver completed tasks via **GitHub Pull Requests**.
2. **Multi-User NCF Collaboration Safeguards:**
   - Because multiple developers on the same team may each run their own NightCodeForge instance against the same GitHub repository (each with an independent local SQLite database and auto-incrementing `task.Id`), outbound PR delivery enforces:
     - **User-Namespaced Remote Branches (`ncf/{githubLogin}/task-{id}`):** Pushes the isolated local feature branch (`feature/task-{id}`) to `refs/heads/ncf/{sanitizedLogin}/task-{id}` on GitHub (`https://github.com/{RepositoryOwner}/{RepositoryName}.git`) using ephemeral HTTP header authentication (with mandatory log/exception redaction in [`GitCliRunner`](../../../src/NightCodeForge.Infrastructure/Git/GitCliRunner.cs)) without persisting tokens in `.git/config`, preventing remote branch collisions when two developers both have a local `Task #12`.
     - **Pathspec-Safe Exclusion of Local `docs/tasks/*.md` State Files from Outbound PRs:** Strips/reverts local NCF task tracking markdown files (`docs/tasks/*.md`) from the pushed branch before opening/updating the PR so that merging the PR into `main` never pollutes the shared repository with active local task files or triggers duplicate task ingestion in teammates' NCF instances.
     - **Remote Base Branch Sync (Protected Against `PullFromHostAsync` Reset Wipe):** Fetches the latest `task.BaseBranch` from GitHub before branching/baseline verification—while guarding [`GitMirrorSyncService.PullFromHostAsync`](../../../src/NightCodeForge.Infrastructure/Git/GitMirrorSyncService.cs) with an ancestor check (`git merge-base --is-ancestor`)—so tasks build on top of teammates' latest merged PRs.
3. **Automated PR Creation & Review Room UI Integration:**
   - Opens (or updates) a GitHub Pull Request against `task.BaseBranch` with an automated markdown summary detailing the task objective, stage execution history, and deterministic quality gate metrics (coverage delta, warning ratchet, test results).
   - Surfaces **"Open GitHub Pull Request"** / **"Create GitHub Pull Request"** actions in the Review Room UI ([`Review.razor`](../../../src/NightCodeForge.Web/Components/Pages/Review.razor)) and Task Details ([`TaskDetail.razor`](../../../src/NightCodeForge.Web/Components/Pages/TaskDetail.razor) / [`TaskDetailDrawer.razor`](../../../src/NightCodeForge.Web/Components/Backlog/TaskDetailDrawer.razor)).

---

## 2. Domain, Persistence & Markdown Sync Alignment (`MergeMode.ExternalPullRequest`)

GitHub PR delivery extends the core **[`MergeMode`](../../../src/NightCodeForge.Core/Domain/Enums.cs)** enumeration:
```csharp
public enum MergeMode
{
    Automatic = 0,             // Auto-squashes and merges to base branch once status reaches Ready
    ManualReviewRequired = 1,  // Maintainer manually reviews diff/tests before triggering merge in UI
    ExternalPullRequest = 2    // Pushes branch to GitHub remote and opens a GitHub PR (enabled when GitHub PR delivery is active)
}
```

### 2.1 [`ForgeTask`](../../../src/NightCodeForge.Core/Domain/ForgeTask.cs) Properties, History & Database Persistence
- `ForgeTask.MergeMode`: Set to `MergeMode.ExternalPullRequest` when PR delivery is selected.
- `ForgeTask.ExternalSource`: External provider identifier (e.g. `"GitHub"`), nullable `string?` (max length `64`).
- `ForgeTask.ExternalReferenceId`: Unprefixed external issue identifier (e.g. `"123"` for GitHub Issue `#123`), nullable `string?` (max length `128`). Kept distinct from Pull Request tracking so a task originating from Issue `#123` can also open Pull Request `#45` without overwriting its upstream issue link.
- `ForgeTask.PullRequestNumber`: Created GitHub Pull Request number (e.g. `45`), nullable `int?`.
- `ForgeTask.PullRequestUrl`: Full URL to the created GitHub PR (e.g. `"https://github.com/owner/repo/pull/45"`), nullable `string?` (max length `1024`).
- Register the EF Core migration and `pragma_table_info` baseline check in [`AppDbContext.BaselineExistingPreMigrationDatabaseAsync`](../../../src/NightCodeForge.Infrastructure/Data/AppDbContext.cs), and copy/preserve these properties across:
  - [`TaskWorkspacePreparer.CopyTaskMetadata`](../../../src/NightCodeForge.Core/Workflow/TaskWorkspacePreparer.cs)
  - [`EfCoreTaskRepository.UpdateAsync`](../../../src/NightCodeForge.Infrastructure/Data/Repositories/EfCoreTaskRepository.cs):
    - In the `existing.Version > task.Version` optimistic-concurrency preservation block, preserve maintainer/provenance fields (`ExternalSource`, `ExternalReferenceId`, alongside `MergeMode`) from `existing`, while copying non-null orchestrator delivery outputs (`PullRequestNumber`, `PullRequestUrl`) from `task` (and never overwriting populated `existing.PullRequestNumber` / `existing.PullRequestUrl` with `null` from a stale execution snapshot).
  - [`InMemoryTaskRepository`](../../../src/NightCodeForge.Core/Repositories/ITaskRepository.cs)
  - [`ForgeTaskHistory`](../../../src/NightCodeForge.Core/Domain/ForgeTaskHistory.cs) and [`TaskHistoryDiffService`](../../../src/NightCodeForge.Core/Services/TaskHistoryDiffService.cs) (supporting `MergeMode.ExternalPullRequest` audit diffs)
  - [`TaskReviewSnapshot`](../../../src/NightCodeForge.Core/Domain/TaskReviewModels.cs) (`PullRequestNumber`, `PullRequestUrl`, `ExternalSource`, and `ExternalReferenceId`)
  - [`TaskMergeAndCascadeCoordinator`](../../../src/NightCodeForge.Core/Workflow/TaskMergeAndCascadeCoordinator.cs): ensure `discardApproval` on stage loopback / re-entry applies whenever `task.MergeMode != MergeMode.Automatic` (both `ManualReviewRequired` and `ExternalPullRequest`), never assuming a binary check against `ManualReviewRequired`.

### 2.2 Centralized `MergeModeHelper`, Markdown Sync, Backlog Tooling & UI Availability
- **Centralized [`MergeModeHelper`](../../../src/NightCodeForge.Core/Domain/MergeModeHelper.cs):**
  - Update `MergeModeHelper` (`All`, `GetLabel`, `GetShortLabel` returning `"PR"`, `GetDescription`, `GetBadgeClass`, `GetBadgeColor`, and `GetIcon`) for `MergeMode.ExternalPullRequest`.
  - Eliminate remaining binary `MergeMode == MergeMode.Automatic ? ... : ...` ternary assumptions across [`KanbanCard.razor`](../../../src/NightCodeForge.Web/Components/Backlog/KanbanCard.razor), [`DagVisualizer.razor`](../../../src/NightCodeForge.Web/Components/Backlog/DagVisualizer.razor), [`TaskDetail.razor`](../../../src/NightCodeForge.Web/Components/Pages/TaskDetail.razor), [`TaskDetailDrawer.razor`](../../../src/NightCodeForge.Web/Components/Backlog/TaskDetailDrawer.razor), and [`HomeDashboardBuilder`](../../../src/NightCodeForge.Web/Services/HomeDashboardBuilder.cs) by routing labels, icons, and badge classes through `MergeModeHelper`.
- **Markdown Sync (`docs/tasks/*.md`):**
  - Update [`TaskMarkdownSyncService`](../../../src/NightCodeForge.Core/Services/TaskMarkdownSyncService.cs) and [`TaskFrontmatter`](../../../src/NightCodeForge.Core/Services/TaskFrontmatter.cs) to serialize and parse `MergeMode.ExternalPullRequest` as `"external_pull_request"` (alongside `"automatic"` and `"manual_review_required"`), and preserve `external_source`, `external_reference_id`, `pull_request_number`, and `pull_request_url` in frontmatter, content hashing (`ComputeTaskContentHash`), and 3-way disk sync (`SyncAllTasksFromDiskAsync`) while remaining backward-compatible with existing `docs/tasks/*.md` files that omit these optional keys.
- **Backlog Agent Tools:**
  - Update [`BacklogMutationToolService`](../../../src/NightCodeForge.Agents/Tools/BacklogMutationToolService.cs) and [`BacklogManagementToolModule`](../../../src/NightCodeForge.Agents/Tools/BacklogManagementToolModule.cs) (`EditTaskAsync` / `ProposeTasksAsync` / `GetTaskAsync`) to accept both `"ExternalPullRequest"` and `"external_pull_request"`, and reject `ExternalPullRequest` with a clear validation error when `PullRequestDeliveryEnabled` is `false` on the workspace or `SecurityOptions.AirGappedMode` is enabled.
- **Notifications & Badges:**
  - Update [`WebNotificationService`](../../../src/NightCodeForge.Web/Services/WebNotificationService.cs) so `NotificationSubject.ReviewRoom` notifications fire when `task.MergeMode is MergeMode.ManualReviewRequired or MergeMode.ExternalPullRequest`.
- **Air-Gapped & Feature Availability Guard:**
  - When `SecurityOptions.AirGappedMode` is `false`, `workspace.GitHubSettings is { Enabled: true, PullRequestDeliveryEnabled: true }`, and a valid token is present in `IGitHubTokenStore`, `MergeMode.ExternalPullRequest` is enabled as a selectable option across `/tasks/new` ([`CreateTask.razor`](../../../src/NightCodeForge.Web/Components/Pages/CreateTask.razor)), the Backlog creation modal ([`TaskCreationModal.razor`](../../../src/NightCodeForge.Web/Components/Backlog/TaskCreationModal.razor)), backlog promotion modals ([`MaintainerPromotionModal.razor`](../../../src/NightCodeForge.Web/Components/Backlog/MaintainerPromotionModal.razor)), proposal cards, [`TaskDetail.razor`](../../../src/NightCodeForge.Web/Components/Pages/TaskDetail.razor), and [`TaskDetailDrawer.razor`](../../../src/NightCodeForge.Web/Components/Backlog/TaskDetailDrawer.razor). When disabled or unauthenticated, existing `Automatic` and `ManualReviewRequired` workflows behave identically with zero GitHub network calls.

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
   - When `workspace.GitHubSettings is { Enabled: true, PullRequestDeliveryEnabled: true }`, during [`TaskWorkspacePreparer.PrepareTaskBranchAndMirrorAsync`](../../../src/NightCodeForge.Core/Workflow/TaskWorkspacePreparer.cs) (and prior to Stage 2 `PreImplementationBaseline`), fetch the latest `task.BaseBranch` from `https://github.com/{RepositoryOwner}/{RepositoryName}.git` into the canonical clone (and fast-forward local `BaseBranch` when clean) so new task branches always start from teammates' latest merged GitHub commits.
   - **Critical `PullFromHostAsync` Guard:** In [`GitMirrorSyncService.PullFromHostAsync`](../../../src/NightCodeForge.Infrastructure/Git/GitMirrorSyncService.cs), `git merge --ff-only host-origin/{baseBranch}` currently falls back to a destructive `git reset --hard host-origin/{baseBranch}` on non-zero exit. If the canonical ext4 mirror fast-forwards `BaseBranch` from GitHub, `ext4Mirror/{baseBranch}` will be *ahead* of `host-origin/{baseBranch}` (the Windows host repo), causing `--ff-only` to fail and `reset --hard` to wipe the fetched GitHub commits! Therefore:
     1. Fetch from GitHub *after* `PullFromHostAsync` in `PrepareTaskBranchAndMirrorAsync` (or push the fast-forwarded `BaseBranch` back to `host-origin` when clean), AND
     2. Update [`GitMirrorSyncService.PullFromHostAsync`](../../../src/NightCodeForge.Infrastructure/Git/GitMirrorSyncService.cs) to check whether `host-origin/{baseBranch}` is already an ancestor of `HEAD` (`git merge-base --is-ancestor host-origin/{baseBranch} HEAD`) before ever executing `git reset --hard host-origin/{baseBranch}`.

### 3.2 Pull Request Generation, Failure Resilience & External Merge Closure
To respect the maximum 10-constructor-parameter rule in [AGENTS.md](../../../AGENTS.md) (since [`TaskMergeAndCascadeCoordinator`](../../../src/NightCodeForge.Core/Workflow/TaskMergeAndCascadeCoordinator.cs) already has 10 parameters and [`TaskOrchestrator`](../../../src/NightCodeForge.Core/Workflow/TaskOrchestrator.cs) has 9), encapsulate outbound PR delivery in a cohesive service routed via [`ITaskWorkspacePreparer`](../../../src/NightCodeForge.Core/Workflow/ITaskWorkspacePreparer.cs) (currently at 8 parameters):

When a task completes all verification stages (`PostReviewVerification` passes) and `task.MergeMode == MergeMode.ExternalPullRequest`:
1. **Clean Outbound Branch & Raw-URL Lease Push to GitHub Remote (Ephemeral Auth, Explicit Ref Lease, `GitCliRunner` Redaction & Timeout Safety):**
   - Ensure `docs/tasks/` changes on `feature/task-{id}` are stripped/reverted to match `task.BaseBranch` (using the pathspec-safe `git rm -rf --ignore-unmatch docs/tasks` + conditional `git checkout <baseBranch> -- docs/tasks` flow **after** `SyncAllTasksFromDiskAsync` has persisted task metadata in SQLite and synced host `docs/tasks/*.md`) so local NCF task state files are never pushed to the shared GitHub repository and `Review.razor` code diffs remain free of `docs/tasks/` noise.
   - Compute `remoteBranch = $"ncf/{SanitizeGitHubLogin(tokenPayload.AuthenticatedUserLogin)}/task-{task.Id}"`.
   - **Raw-URL Explicit Ref Lease (`--force-with-lease=refs/heads/{remoteBranch}:{expectedRemoteSha}`):**
     - Because `origin` in the canonical ext4 clone (`Ext4MirrorPath`) points to the local host repository path rather than GitHub, there is no local remote-tracking ref `refs/remotes/.../{remoteBranch}`. Invoking bare `--force-with-lease` against a raw URL (`https://github.com/{owner}/{repo}.git`) fails on branch updates with `! [rejected] ... (stale info)`.
     - Query the current remote ref SHA first via `git -c http.extraHeader="..." ls-remote https://github.com/{owner}/{repo}.git refs/heads/{remoteBranch}` (returning empty `""` when the branch does not exist yet on GitHub, or `<remoteSha>` when updating an existing PR branch after a "Request Changes" loopback), then push `feature/task-{id}:refs/heads/{remoteBranch}` with explicit `--force-with-lease=refs/heads/{remoteBranch}:{expectedRemoteSha}` so the short-lived `ghu_...` token is **never** written into `.git/config` while still guarding against concurrent remote overwrites.
   - **Mandatory Credential Redaction & Network Timeout Resolution in [`GitCliRunner`](../../../src/NightCodeForge.Infrastructure/Git/GitCliRunner.cs):**
     - Redact any argument, header, or URL containing `http.extraHeader=`, `Authorization:`, or embedded credentials (`http.extraHeader="Authorization: [REDACTED]"`) across **all** diagnostic outputs: `ILogger` statements (including `wsl.exe` wrapped argument logs), `GitCommandException.Arguments`, `GitCommandException.Message`, `GitCommandException.StdErr`, and `TimeoutException` messages (all of which can flow into `ExecutionLog` in SQLite and SignalR streams).
     - Ensure `GitCliRunner.ResolveTimeout` detects network subcommands (`push`, `fetch`, `pull`, `clone`, `ls-remote`) even when preceded by global Git options such as `-c http.extraHeader=...` (applying the network timeout rather than the 30-second local plumbing timeout).
   - Also sync the feature branch to the local host repository (`PushToHostAsync`) so the developer's local repository retains the updated branch ref.
2. **Build PR Title & Body (Shared Quality Gate Metrics Extraction):**
   - Extract deterministic quality gate metrics from `PipelineDataBag` using a shared helper in `NightCodeForge.Core` (consolidating the logic currently in [`TaskReviewService.ExtractGateMetrics`](../../../src/NightCodeForge.Web/Services/TaskReviewService.cs)).
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
3. **Open or Update Pull Request & Non-Destructive Failure Handling:**
   - If `task.PullRequestNumber` / `task.PullRequestUrl` is not yet set, invoke `IGitHubClientService.CreatePullRequestAsync` (`POST /repos/{owner}/{repo}/pulls`) with `head = remoteBranch` (`ncf/{sanitizedLogin}/task-{id}`), `base = task.BaseBranch`, `draft = settings.CreateAsDraft`, and store `task.PullRequestNumber` and `task.PullRequestUrl`.
   - If `task.PullRequestNumber` already exists (e.g., after a maintainer "Request Changes" loopback), pushing `feature/task-{id}:refs/heads/{remoteBranch}` updates the PR head automatically, and `IGitHubClientService.UpdatePullRequestAsync` updates the PR body/title with the latest gate metrics.
   - Transition `task.Status` to `TaskOverallStatus.Ready` so the task remains visible in the Review Room while the PR is open on GitHub.
   - **Critical Non-Destructive Failure Invariant:** Because Stage 6 (`PostReviewVerification`) deterministic builds, tests, and coverage gates have already succeeded at this point, transient GitHub delivery failures (expired/revoked OAuth token, network outage, branch protection rejection, HTTP 5xx) **must never mark the task `Failed` or trigger Stage 6 retry loops**. Instead, catch outbound delivery exceptions (`GitCommandException`, `GitHubApiException`, `HttpRequestException`, `OperationCanceledException` when not shutting down), record a warning `ExecutionLog` with the redacted error details, transition the task cleanly to `TaskOverallStatus.Ready`, and allow the maintainer to retry on demand via **"Create GitHub Pull Request"** / **"Sync Pull Request"** in the Review Room once auth or connectivity is restored.
4. **Closing an Externally Merged Pull Request & Downstream DAG Cascade (`CloseExternallyMergedTaskAsync`):**
   - When a Pull Request is merged on GitHub (rather than squash-merged locally via `Approve & Merge`), calling `CompleteTaskAndCascadeAsync` would attempt a duplicate local `git merge --squash feature/task-{id}` into `BaseBranch`, whereas calling `RejectTaskAsync` would mark the task `Rejected` and **fail to unblock downstream DAG dependents**.
   - Provide a dedicated external-PR-merge closure path on [`ITaskMergeAndCascadeCoordinator`](../../../src/NightCodeForge.Core/Workflow/ITaskMergeAndCascadeCoordinator.cs) / [`ITaskOrchestrator`](../../../src/NightCodeForge.Core/Workflow/ITaskOrchestrator.cs) (reusable both by the Review Room UI in this step and by the background `CloseTaskOnPrMerge` poller in [Step 12.11D](STEP_12_11D_GITHUB_TASK_MIRRORING_AND_MULTI_USER_CLAIMING.md)):
     1. Verifies via `IGitHubClientService.GetPullRequestAsync` that the linked PR has `Merged == true` (failing fast with a clear domain exception if the PR is still unmerged so maintainers cannot accidentally close an unmerged task as merged), capturing `MergeCommitSha` into `task.SquashCommitSha` when present.
     2. Best-effort fetches `task.BaseBranch` from GitHub into the canonical clone, fast-forwards local `BaseBranch`, and pushes the fast-forwarded `BaseBranch` to `host-origin` so downstream tasks immediately branch from the merged PR code without running a local `git merge --squash`.
     3. Transitions `task.Status` to `TaskOverallStatus.Closed` (`CurrentStageId = string.Empty`) with audit history (`UpdateWithHistoryAsync`), updates `ITaskDependencyStore`, synchronizes `TaskBadgeCounter`, removes the task worktree & container, deletes the local `feature/task-{id}` branch, archives `docs/tasks/*.md`, and **runs downstream DAG cascade unblocking (`ResolveCascadeUnblockedTasksAsync`)** to enqueue newly unblocked tasks.
5. **Review Room & Task Detail UI Presentation (Presentation Separation per [AGENTS.md](../../../AGENTS.md)):**
   - Keep [`Review.razor`](../../../src/NightCodeForge.Web/Components/Pages/Review.razor), [`TaskDetail.razor`](../../../src/NightCodeForge.Web/Components/Pages/TaskDetail.razor), and [`TaskDetailDrawer.razor`](../../../src/NightCodeForge.Web/Components/Backlog/TaskDetailDrawer.razor) strictly focused on layout and interaction by placing on-demand PR creation/sync and external-merge verification/closure orchestration in [`ITaskReviewService`](../../../src/NightCodeForge.Web/Services/ITaskReviewService.cs) / [`TaskReviewService`](../../../src/NightCodeForge.Web/Services/TaskReviewService.cs).
   - When `_snapshot.MergeMode == MergeMode.ExternalPullRequest` (or whenever `_snapshot.PullRequestUrl` is populated):
     - Render an **"Open GitHub Pull Request (#{PullRequestNumber})"** button/badge linking to `_snapshot.PullRequestUrl` (`target="_blank"`).
     - If `_snapshot.PullRequestUrl` is not yet created for a `Ready` task (e.g., if switched to `ExternalPullRequest` in `Ready` state or after a transient Stage 6 push/API error), render **"Create GitHub Pull Request"** (and **"Sync Pull Request"** when already linked) to trigger PR delivery on demand.
     - Retain **"Request Changes"** (to loop back to `Implementation`), **"Approve & Merge Locally"** (if the maintainer chooses local squash-merge), and **"Close Merged PR"** (invoking `CloseExternallyMergedTaskAsync` once merged on GitHub).

---

## 4. Comprehensive Verification & Non-Regression Test Plan (Unit + Integration)

Because Step 12.11B touches core Git synchronization (`GitCliRunner`, `GitMirrorSyncService`, `GitWorkspaceManager`), task persistence (`EfCoreTaskRepository`, `TaskMarkdownSyncService`), workflow completion (`TaskOrchestrator`, `TaskMergeAndCascadeCoordinator`), and the Review Room, **all four test layers below are mandatory** to guarantee existing local workflows never regress:

### 4.1 Existing Workflow Non-Regression Suite (`MergeMode.Automatic` & `MergeMode.ManualReviewRequired`)
- **Zero-Interference When GitHub PR Delivery Is Disabled:**
  - Verify across `TaskOrchestratorTests` and `TaskMergeAndCascadeCoordinatorTests` that workspaces with `GitHubSettings.Enabled == false` (and workspaces with `Enabled == true, PullRequestDeliveryEnabled == false`) invoke **zero** GitHub remote fetches, pushes, or `IGitHubClientService` calls during task preparation, Stage 1–6 execution, local squash-merge, or Review Room approval.
- **Mixed-Mode Workspace Isolation:**
  - Verify that in a workspace where `PullRequestDeliveryEnabled == true`, tasks configured with `MergeMode.Automatic` still auto-squash-merge into `BaseBranch` and trigger DAG cascade upon Stage 6 completion, and tasks configured with `MergeMode.ManualReviewRequired` still stop in `Ready` without pushing a branch or opening a GitHub PR.
- **Legacy `docs/tasks/*.md` Frontmatter Compatibility:**
  - Verify that `TaskMarkdownSyncService` parses existing `docs/tasks/*.md` files lacking `external_source`, `external_reference_id`, `pull_request_number`, and `pull_request_url` without altering content hashes or triggering spurious version bumps on existing backlog items.

### 4.2 Real-Git Bare-Repository Integration Tests (`tests/NightCodeForge.Infrastructure.Tests`)
Execute against real temporary Git repositories (`git init --bare` simulating `host-origin` and `github-remote`, plus a real canonical `ext4-mirror` clone and `task-{id}` worktree via `GitCliRunner` — **not** mocked `IGitCliRunner`):
1. **`PullFromHostAsync` Ancestor Protection vs. True Divergence:**
   - Advance `github-remote/main` with a simulated teammate commit, fetch & fast-forward `ext4-mirror/main` so it is 1 commit ahead of `host-origin/main`, then invoke `GitMirrorSyncService.PullFromHostAsync`: assert `ext4-mirror/main` **retains** the teammate commit (`git merge-base --is-ancestor` prevents `reset --hard`).
   - Conversely, force-rewrite `host-origin/main` to a non-ancestor commit and invoke `PullFromHostAsync`: assert `ext4-mirror/main` reconciles to `host-origin/main` as expected by existing local host workflows.
2. **Pre-Branch Remote `BaseBranch` Sync:**
   - Commit a new file on `github-remote/main`, run task branch preparation (`PrepareTaskBranchAndMirrorAsync`), and assert the newly created `feature/task-{id}` worktree contains the upstream file from `github-remote/main`.
3. **Pathspec-Safe `docs/tasks/*.md` Stripping Across All Tree Topologies & Re-Review Loops:**
   - **Case A (`BaseBranch` has no `docs/tasks/` directory):** Create `docs/tasks/2026-11-13-sample.md` + `src/Feature.cs` on `feature/task-1`, run outbound PR cleanup + push, and assert `refs/heads/ncf/alice/task-1` on `github-remote` contains `src/Feature.cs` and **zero** files under `docs/tasks/` (with no `pathspec 'docs/tasks' did not match` error).
   - **Case B (`BaseBranch` has committed files in `docs/tasks/`):** Commit `docs/tasks/README.md` on `main`, add `docs/tasks/2026-11-13-task-2.md` + modify `docs/tasks/README.md` + add `src/App.cs` on `feature/task-2`, run outbound PR cleanup + push, and assert `refs/heads/ncf/alice/task-2` preserves `docs/tasks/README.md` identical to `main` while excluding `2026-11-13-task-2.md` and keeping `src/App.cs`.
   - **Case C (Idempotency Across `Request Changes` Loopback):** Simulate Stage 3 re-entry (`CreateWipCheckpointAsync` re-adding `docs/tasks/2026-11-13-sample.md` alongside `src/FeatureV2.cs`), run outbound PR cleanup + push a second time, and assert the updated remote branch contains `src/FeatureV2.cs` and still zero local `docs/tasks/*.md` files, while `GenerateLiveDiffAsync` produces a clean diff of only `src/` changes.
4. **Raw-URL Explicit `--force-with-lease` Push Mechanics:**
   - Assert initial push (`expectedRemoteSha == ""`) creates `refs/heads/ncf/alice/task-1` on `github-remote`.
   - Amend/add commits on `feature/task-1` and run a second push: assert `ls-remote` + `--force-with-lease=refs/heads/ncf/alice/task-1:{remoteSha}` succeeds cleanly (proving no `stale info` rejection on raw URLs) and `.git/config` contains zero credentials or `extraHeader` entries.
   - Advance `refs/heads/ncf/alice/task-1` out-of-band on `github-remote` between `ls-remote` and `push` (stale expected SHA): assert `--force-with-lease` deterministically rejects the stale overwrite.
5. **Multi-User Branch Isolation:**
   - Push local `feature/task-5` as `alice` and an independent local `feature/task-5` as `bob` to the same `github-remote`: assert `refs/heads/ncf/alice/task-5` and `refs/heads/ncf/bob/task-5` coexist with distinct commit trees.
6. **`GitCliRunner` Redaction & `-c` Option Timeout Tests:**
   - Execute `GitCliRunner.ExecuteAsync` with `-c http.extraHeader="Authorization: Basic SUPER_SECRET_TOKEN"` against an invalid remote path (triggering `GitCommandException`) and under a simulated timeout (`TimeoutException`): assert `"SUPER_SECRET_TOKEN"` never appears in `GitCommandException.Message`, `GitCommandException.Arguments`, `TimeoutException.Message`, or captured `ILogger` entries.
   - Unit test `GitCliRunner` timeout resolution confirming `["-c", "http.extraHeader=...", "push", ...]` and `["-c", "http.extraHeader=...", "fetch", ...]` receive the network timeout (`120s`) rather than the `30s` default.

### 4.3 Orchestrator, Repository & Markdown Sync Integration Tests (`tests/NightCodeForge.Core.Tests` & `tests/NightCodeForge.Infrastructure.Tests`)
1. **Stage 6 Outbound PR Delivery Happy Path:**
   - Task with `MergeMode.ExternalPullRequest` completes `PostReviewVerification`: assert branch push + `IGitHubClientService.CreatePullRequestAsync` execute with `CreateAsDraft` and extracted gate metrics, `PullRequestNumber` and `PullRequestUrl` persist in SQLite and `docs/tasks/*.md`, `Status` transitions to `Ready` (never auto-merged), `TaskBadgeCounter` increments pending review count, and `WebNotificationService` emits `NotificationSubject.ReviewRoom`.
2. **Non-Destructive GitHub Failure Handling After Stage 6 Passes:**
   - Simulate `GitHubApiException` (e.g. `401 Unauthorized` / `503 Service Unavailable`) and `GitCommandException` during outbound PR delivery after Stage 6 passes: assert `task.Status` still transitions to `TaskOverallStatus.Ready` (not `Failed`), Stage 6 deterministic tests are **not** re-executed, a redacted warning `ExecutionLog` is persisted, and invoking on-demand PR creation via `TaskReviewService` succeeds once the mock error clears.
3. **`Request Changes` Loopback & PR Update:**
   - On a `Ready` task with `PullRequestNumber = 45`, trigger `RequestChangesAsync`, run `Implementation` $\rightarrow$ `PostReviewVerification`: assert `UpdatePullRequestAsync` (not `CreatePullRequestAsync`) updates PR `#45` with refreshed metrics and `IsApprovedByHuman` is cleared (`discardApproval`).
4. **External PR Merged Closure & Downstream DAG Cascade (`CloseExternallyMergedTaskAsync`):**
   - Set up Task `#10` (`MergeMode.ExternalPullRequest`, `PullRequestNumber = 45`, `Status = Ready`) blocking downstream Task `#11` (`Status = Open`, `Dependencies = [10]`):
     - When `GetPullRequestAsync` returns `Merged = false`, assert `CloseExternallyMergedTaskAsync` throws a domain validation error and leaves Task `#10` in `Ready` and Task `#11` blocked.
     - When `GetPullRequestAsync` returns `Merged = true, MergeCommitSha = "abc1234"`, assert `CloseExternallyMergedTaskAsync` records `SquashCommitSha = "abc1234"`, syncs `BaseBranch` from remote without calling `SquashAndMergeAsync`, transitions Task `#10` to `Closed`, cleans worktree/container/branch, archives markdown, and **unblocks + enqueues Task `#11` in `IBackgroundJobQueue`**.
5. **EF Core Persistence & Optimistic Concurrency:**
   - Against SQLite (`AppDbContext`), verify schema migration + pre-migration baseline check, 3-way `TaskMarkdownSyncService` sync of `"external_pull_request"` and PR/issue provenance fields, and `EfCoreTaskRepository.UpdateAsync` preserving `ExternalSource`, `ExternalReferenceId`, `PullRequestNumber`, and `PullRequestUrl` across concurrent version updates.

### 4.4 Web Services & Blazor Component Tests (`tests/NightCodeForge.Web.Tests`)
- Unit tests for `MergeModeHelper` covering labels, short labels (`"PR"`), descriptions, icons, and CSS classes across all 3 `MergeMode` values, plus `BacklogMutationToolService` rejecting `"external_pull_request"` when `PullRequestDeliveryEnabled == false` or `AirGappedMode == true`.
- Unit tests for `TaskReviewService` covering `ExtractGateMetrics` parity, on-demand PR creation/sync, and `CloseExternallyMergedTaskAsync` delegation.
- bUnit tests for `Review.razor`, `CreateTask.razor`, `TaskDetail.razor`, `TaskDetailDrawer.razor`, `KanbanCard.razor`, and `DagVisualizer.razor` verifying:
  - When `PullRequestDeliveryEnabled == false` or token missing: `ExternalPullRequest` selector option is hidden/disabled and existing `Automatic` / `ManualReviewRequired` UI renders unchanged.
  - When enabled and authenticated: `ExternalPullRequest` option is selectable, `"PR"` badge renders on Kanban/DAG cards, and `Review.razor` displays `"Open GitHub Pull Request (#45)"`, `"Create GitHub Pull Request"` (when `PullRequestUrl` is null), `"Request Changes"`, and `"Close Merged PR"`.
- Full verification gate upon completion per [AGENTS.md](../../../AGENTS.md): `dotnet test` (including Acceptance & E2E suites).

---

## 5. Implementation Checklist & Deliverables

- [x] **Domain Model, `MergeModeHelper`, EF Core Migration & Markdown Sync:**
  - [x] Add `MergeMode.ExternalPullRequest = 2` and `ExternalSource`, `ExternalReferenceId`, `PullRequestNumber`, and `PullRequestUrl` on [`ForgeTask`](../../../src/NightCodeForge.Core/Domain/ForgeTask.cs), plus `MergeModeHelper` support across all UI badge/selector call sites.
  - [x] Add EF Core migration and update `AppDbContext.BaselineExistingPreMigrationDatabaseAsync`, `EfCoreTaskRepository.UpdateAsync` (concurrency-safe preservation), `InMemoryTaskRepository`, `ForgeTaskHistory` / `TaskHistoryDiffService`, `TaskWorkspacePreparer.CopyTaskMetadata`, `TaskMergeAndCascadeCoordinator` (`discardApproval`), `TaskMarkdownSyncService`, `BacklogMutationToolService`, and `WebNotificationService`.
- [x] **Git Plumbing Hardening (`GitCliRunner` & `GitMirrorSyncService`):**
  - [x] Redact `http.extraHeader` / `Authorization:` / URL credentials across all [`GitCliRunner`](../../../src/NightCodeForge.Infrastructure/Git/GitCliRunner.cs) logs, `TimeoutException`, and `GitCommandException` fields, and ensure network timeouts apply when `-c` flags precede `push` / `fetch` / `ls-remote`.
  - [x] Guard [`GitMirrorSyncService.PullFromHostAsync`](../../../src/NightCodeForge.Infrastructure/Git/GitMirrorSyncService.cs) with an ancestor check (`git merge-base --is-ancestor`) so fetched GitHub commits on `BaseBranch` are never wiped by `reset --hard`.
- [x] **PR Delivery Orchestration, Non-Destructive Error Handling & External Merge Closure:**
  - [x] Extract shared quality gate metrics helper in `NightCodeForge.Core`.
  - [x] Implement remote `BaseBranch` fetch, pathspec-safe stripping of local `docs/tasks/*.md` state files from outbound PR commits, raw-URL explicit ref lease push (`ls-remote` + `--force-with-lease=refs/heads/ncf/{sanitizedLogin}/task-{id}:{expectedSha}`), non-destructive Stage 6 delivery failure recovery (transitioning to `Ready` with warning log), and `CloseExternallyMergedTaskAsync` (with remote `BaseBranch` sync and downstream DAG cascade unblocking) within the 10-constructor-parameter limit.
- [x] **Blazor UI Integration (Presentation Separation):**
  - [x] Extract PR creation/sync and external-merge closure actions into `ITaskReviewService` / `TaskReviewService`.
  - [x] Enable `MergeMode.ExternalPullRequest` option across task creation, promotion, and detail selectors when `PullRequestDeliveryEnabled` is active, and render `"Open GitHub Pull Request"`, `"Create GitHub Pull Request"`, and `"Close Merged PR"` actions in `Review.razor`, `TaskDetail.razor`, and `TaskDetailDrawer.razor`.
- [x] **Complete Automated Test Suite (Section 4):**
  - [x] Implement all 4 test layers: existing workflow non-regression (`Automatic` & `ManualReviewRequired`), real-Git bare-repo integration tests (`NightCodeForge.Infrastructure.Tests`), orchestrator & EF Core lifecycle tests (`NightCodeForge.Core.Tests`), and `TaskReviewService` + bUnit UI tests (`NightCodeForge.Web.Tests`), followed by full `dotnet test` (including E2E).

