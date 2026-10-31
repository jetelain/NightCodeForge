# Step 12.24 — Home Page Pending Human Code Review Tasks

**Status:** Planned (Feature / Developer Ergonomics & Review Room Visibility)

---

## 1. Problem Statement

When an autonomous task completes implementation, post-implementation quality gates, and automated Stage 3 code review (or finishes autonomous merge-conflict resolution), it transitions to `TaskOverallStatus.Ready` (`HasExecutionRun == true`) and waits for a human maintainer to inspect the diff and approve/merge it in the Review Room (`/review/{taskId}`).

Currently on the Home page ([`Home.razor`](../../src/NightCodeForge.Web/Components/Pages/Home.razor) and [`HomeDashboardBuilder.cs`](../../src/NightCodeForge.Web/Services/HomeDashboardBuilder.cs)):

1. **No Dedicated Pending Code Review Section:** While the Home page prominently surfaces pending human clarification questions (`#home-inbox-btn` and `#home-pending-questions-banner`) and running/scheduled Hangfire jobs (`#home-scheduled-tasks-section`), tasks awaiting human code review and merge approval are not listed on the Home page at all.
2. **Hidden Behind Question Badges on Workspace Cards:** In `RecentWorkspaceSummary`, tasks in `TaskOverallStatus.Ready` are combined with `IsWaitingForHuman` into `WaitingOrReviewTaskCount` (`Attention: N`), and in `Home.razor` that badge is rendered inside an `else if (item.WaitingOrReviewTaskCount > 0)` branch that is completely hidden whenever `item.PendingQuestionCount > 0`.
3. **No Direct Deep-Link from Home to Review Room:** Developers must manually navigate into a workspace and open the Review Room or Backlog to discover which tasks are ready for human code review and merge.

---

## 2. Architectural Goals

1. **First-Class Pending Review Aggregation in `HomeDashboardBuilder`:**
   - Extract all tasks awaiting human code review (`t.Status == TaskOverallStatus.Ready && t.HasExecutionRun`) across all workspaces into `HomeDashboardSnapshot` and per-workspace `RecentWorkspaceSummary` records.
2. **Prominent Home Page Review Room Access & Task List:**
   - Add a **Review Room** quick-access button with a live count badge in the Home Command Center header.
   - Display a clickable **`🔍 Review: N`** badge on each workspace card in `Recent Workspaces` (independent of the `📬 Questions: N` badge).
   - Render a dedicated **Tasks Awaiting Human Code Review** section on the Home page listing pending review tasks with branch targets, priority, conflict-resolved indicators, and 1-click **`🔍 Review & Merge`** deep-links to `/w/{workspaceId}/review/{taskId}`.

---

## 3. Required Specification & Behavioral Contracts

### 3.1 `HomeDashboardBuilder` Data Contracts (`NightCodeForge.Web.Services`)

1. **`HomePendingReviewItem` Record:**
   ```csharp
   public record HomePendingReviewItem(
       int TaskId,
       string Title,
       int WorkspaceId,
       string WorkspaceName,
       string FeatureBranch,
       string BaseBranch,
       int Priority,
       bool HasResolvedConflict,
       DateTime UpdatedAt,
       string ReviewUrl,
       string InspectorUrl,
       string TaskDetailUrl);
   ```
2. **`RecentWorkspaceSummary` Extension:**
   - Add `int PendingReviewCount` (count of tasks in the workspace where `t.Status == TaskOverallStatus.Ready && t.HasExecutionRun`) and `string ReviewUrl` (`WorkspaceUrlHelper.BuildPath(ws.Id, "review")`).
3. **`HomeDashboardSnapshot` Extension:**
   - Add `IReadOnlyList<HomePendingReviewItem> PendingReviewTasks` and `int PendingReviewCount` (total count of pending human code review tasks across all workspaces).
4. **Selection & Ordering Logic:**
   - A task qualifies as awaiting human code review when `t.Status == TaskOverallStatus.Ready && t.HasExecutionRun` (matching `TaskReviewService.IsPendingReview`).
   - Extract `HasResolvedConflict` from `PipelineDataBag.FromJson(task.PipelineDataJson).Get<bool?>(PipelineDataBagKeys.HasResolvedConflict) ?? false`.
   - Order `PendingReviewTasks` by `Priority` ascending (`Critical = 1` first), then by `UpdatedAt` descending, then by `TaskId` ascending.
   - Build URLs using `WorkspaceUrlHelper.BuildPath`:
     - `ReviewUrl`: `/w/{workspaceId}/review/{taskId}` (or `/review/{taskId}` if `workspaceId <= 0`)
     - `InspectorUrl`: `/w/{workspaceId}/inspector/{taskId}`
     - `TaskDetailUrl`: `/w/{workspaceId}/tasks/{taskId}`

### 3.2 `Home.razor` Presentation & UX Requirements

1. **Command Center Header Button (`#home-review-btn`):**
   - Next to `#home-inbox-btn`, render a link button (`href="/review"`, `id="home-review-btn"`) with label **`Review Room`** and badge `id="home-review-count-badge"` showing `@_snapshot.PendingReviewCount`.
   - Style with a highlighted button/badge variant when `_snapshot.PendingReviewCount > 0` and neutral secondary/gray styling when `0`.
2. **Workspace Summary Card Review Badge (`#home-ws-review-{ws.Id}`):**
   - On each workspace card in `#home-workspaces-section`, when `item.PendingReviewCount > 0`, render an independent clickable badge (`<a href="@item.ReviewUrl" id="home-ws-review-@ws.Id">🔍 Review: @item.PendingReviewCount</a>`) alongside `📬 Questions: @item.PendingQuestionCount` so both badges are visible simultaneously when a workspace has both pending questions and pending code reviews.
3. **Pending Human Code Reviews Section (`#home-pending-reviews-section`):**
   - Render a dedicated dashboard section (`id="home-pending-reviews-section"`) displaying tasks awaiting human code review:
     - When `_snapshot.PendingReviewTasks.Count > 0`, render each item (`id="home-pending-review-@task.TaskId"`) showing:
       - Priority badge (`P@(task.Priority)`),
       - Task title link (`#@task.TaskId: @task.Title` linking to `@task.ReviewUrl`),
       - Workspace badge (`🏢 @task.WorkspaceName`),
       - Branch target badge (`@task.FeatureBranch ➔ @task.BaseBranch`),
       - Optional conflict-resolved badge (`⚠️ Conflict Resolved`) when `task.HasResolvedConflict` is `true`,
       - Action buttons: **`🔍 Review & Merge`** (`href="@task.ReviewUrl"`, `id="home-review-task-btn-@task.TaskId"`), **`🔍 Inspector`** (`href="@task.InspectorUrl"`), and **`📋 Details`** (`href="@task.TaskDetailUrl"`).

---

## 4. Verification Criteria

- Unit tests in `HomeDashboardTests.cs` verify:
  - `HomeDashboardBuilder.BuildSnapshotAsync` populates `PendingReviewTasks`, `PendingReviewCount`, and per-workspace `PendingReviewCount` / `ReviewUrl` for tasks with `Status == TaskOverallStatus.Ready && HasExecutionRun == true` (and excludes `Ready` tasks where `HasExecutionRun == false`).
  - `PendingReviewTasks` are ordered by priority ascending, then `UpdatedAt` descending, and accurately surface `HasResolvedConflict` from `PipelineDataJson`.
  - bUnit rendering of `Home.razor` displays `#home-review-btn`, `#home-review-count-badge`, per-workspace `#home-ws-review-{ws.Id}` badges (even when `#home-ws-inbox-{ws.Id}` is also present), and `#home-pending-reviews-section` rows with working `/w/{ws.Id}/review/{task.Id}` links.
- **Playwright E2E Tests (Zero LLM Calls, `[Trait("Category", "E2E")]`):**
  - Using `PlaywrightWebHostFixture` ([Step 11.39A](../fixes/STEP_11_39A_PLAYWRIGHT_E2E_HOST_FIXTURE_AND_BACKLOG_BASELINE_TESTS.md)), seed tasks across statuses (`Ready` with `HasExecutionRun = true`, `Ready` with `HasExecutionRun = false`, `Open`, `Closed`) and a real feature branch in `SyntheticRepoFixture`.
  - Verify in headless Chromium on `/` that `#home-review-count-badge`, `#home-ws-review-{ws.Id}`, and `#home-pending-reviews-section` display the expected pending review items, and clicking `#home-review-task-btn-{task.Id}` navigates directly to the Review Room for that task.

