# 🎭 Step 11.39A: Reusable LLM-Free Playwright E2E Host Fixture & Core Backlog Baseline Tests

> **Fix & Resiliency Specification (Part 1 of 2 — Fast Browser E2E Harness & Core Workflow Baseline)**  
> *Parent Plan:* [Phase 11 Master Plan](../11_EXTEND_FEATURES.md)  
> *Companion Step:* [Step 11.39B: Baseline Playwright E2E Tests for HITL Inbox, Review Room & Inspector](STEP_11_39B_PLAYWRIGHT_E2E_HITL_INBOX_REVIEW_AND_INSPECTOR_TESTS.md)  
> *Target Components:* `tests/NightCodeForge.Web.Tests/E2E` | *Status:* **Completed** | *Target Framework:* .NET 10 (C# 13)  
> *Aligns With:* [AGENTS.md](../../../AGENTS.md), [docs/PLAN.md](../../PLAN.md)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Problem Statement & Motivation

Currently, [`AutonomousFeatureLifecycleTests`](../../tests/NightCodeForge.Web.Tests/E2E/AutonomousFeatureLifecycleTests.cs) is the only browser-based Playwright E2E test in the repository. Because it executes the full multi-stage LLM inference pipeline against a live model (`[Trait("Category", "Acceptance")]`), it takes several minutes to run and only exercises a single "golden path" (Wizard $\rightarrow$ Ideation $\rightarrow$ Auto-Merge).

Meanwhile, critical UI workflows—such as Home dashboard status cards, manual task creation, Kanban/Backlog filtering, workspace URL routing (`/w/{workspaceId}/...`), Question Inbox answering, and Review Room diff inspection—are only tested via in-memory bUnit component tests rather than a real Chromium browser with a live Kestrel server, Blazor InteractiveServer SignalR circuit, and SQLite database.

### Objective
1. **Reusable LLM-Free Playwright Host Fixture (`PlaywrightWebHostFixture`):** Extract a fast, deterministic Playwright E2E fixture in `tests/NightCodeForge.Web.Tests/E2E` that boots `NightCodeForge.Web` against an isolated temporary SQLite database and `SyntheticRepoFixture` **without invoking any LLM calls**, and allows tests to seed arbitrary domain states (`Workspace`, `ForgeTask`, `TaskDependency`, `Question`, `Conversation`, etc.) directly into SQLite before or during browser navigation.
2. **Test Category Separation (`E2E` vs. `Acceptance`):**
   - Keep `dotnet test --filter "Category!=Acceptance&Category!=E2E"` as the fast unit/bUnit command ([AGENTS.md](../../AGENTS.md)).
   - Tag deterministic, LLM-free Playwright browser tests with `[Trait("Category", "E2E")]` (and **not** `"Acceptance"`), so developers and CI can run the entire LLM-free Playwright suite in seconds via:
     ```powershell
     dotnet test --filter "Category=E2E&Category!=Acceptance"
     ```
   - Reserve `[Trait("Category", "Acceptance")]` for full live-LLM pipeline tests (`AutonomousFeatureLifecycleTests`).
3. **Core Baseline Playwright E2E Suite (Part 1):** Implement browser E2E tests covering Workspace Provisioning & Switching, Home Dashboard status rendering, Manual Task Creation (`/tasks/new`), Backlog & Kanban/DAG navigation, and `TaskDetail` page actions.

---

## 2. Detailed Functional & Technical Requirements

### 2.1 Reusable `PlaywrightWebHostFixture` (`tests/NightCodeForge.Web.Tests/E2E/PlaywrightWebHostFixture.cs`)
- Implements `IAsyncLifetime` so a single `NightCodeForge.Web` Kestrel process and Chromium `IBrowser` instance can be shared across an xUnit test collection (`[Collection("PlaywrightUiE2E")]`).
- **Isolated Kestrel & SQLite Setup:**
  - Selects an available local port (e.g. `http://127.0.0.1:5274` or dynamic ephemeral loopback port) so it never collides with a developer's running instance or `AutonomousFeatureLifecycleTests`.
  - Creates isolated temporary SQLite database files (`nightcodeforge_uie2e_{guid}.db` and `nightcodeforge_uie2e_hangfire_{guid}.db`) and sets `NCF_WORKSPACE_ID_SEED=1001000`.
  - Starts `NightCodeForge.Web` with background operations paused or without enqueuing Hangfire inference jobs, guaranteeing **zero LLM network calls**.
- **Direct SQLite Seeding Helper:**
  - Exposes `Task SeedDatabaseAsync(Func<AppDbContext, Task> seedAction)` and `Task<T> QueryDatabaseAsync<T>(Func<AppDbContext, Task<T>> queryAction)` connected to `Data Source={TempDbPath}` (using WAL mode), enabling each test to seed workspaces, tasks, dependencies, questions, and conversations deterministically in milliseconds.
- **Per-Test Browser Context Isolation:**
  - Exposes `Task<IBrowserContext> CreateContextAsync(ViewportSize? viewport = null)` so desktop (`1440x900`), tablet (`768x1024`), and mobile (`390x844`) Playwright E2E tests can run in clean, isolated browser contexts.

### 2.2 Baseline E2E Test Suite — Part 1 (`CoreBacklogAndNavigationE2ETests.cs`)
All tests in this class are decorated with `[Trait("Category", "E2E")]` and execute with **zero LLM calls**:

1. **`WorkspaceWizard_And_TopNavBar_WorkspaceSwitching_WorksEndToEnd`:**
   - Seeds 1 existing workspace (`Workspace A`) and initializes a `SyntheticRepoFixture` for `Workspace B`.
   - Opens the Workspace Creation Wizard from `TopNavBar`, probes the `SyntheticRepoFixture` path, selects `Bare Host` (`.mode-card:has-text('Bare Host')`) in Step 3 so no container/WSL setup is required, provisions `Workspace B`, and verifies the browser navigates to `/w/{workspaceBId}`.
   - Uses `select.workspace-dropdown` in `TopNavBar` to switch between `Workspace A` and `Workspace B`, asserting that the URL updates between `/w/{workspaceAId}/...` and `/w/{workspaceBId}/...` and the breadcrumb reflects the active workspace name.

2. **`HomeDashboard_RendersSeededKpis_ActiveTasks_And_GlobalPauseToggle`:**
   - Seeds a workspace with tasks in `Open`, `Suggested`, and `InProgress` states, plus a pending `Question`.
   - Navigates to `/w/{workspaceId}` and verifies the pending questions alert banner (`#home-pending-questions-banner` / `#home-inbox-count-badge`), the workspace summary card (`#home-workspace-card-{workspaceId}` showing `Open` / `Suggested` counts and `#home-ws-inbox-{workspaceId}`), the Running & Next Scheduled Tasks section (`#home-scheduled-tasks-section` / `#home-scheduled-task-{taskId}`), and the Live System Telemetry section (`#home-telemetry-section`) render accurately in Chromium.
   - Clicks `#nav-btn-pause-operations` ("Pause Operations") in `TopNavBar` and verifies it transitions to `#nav-btn-resume-operations` ("Resume Operations"), then clicks it again to resume.

3. **`ManualTaskCreation_FlowsToBacklog_And_TaskDetailPage`:**
   - Navigates to `/w/{workspaceId}/tasks/new` via `#nav-btn-new-task` ("New Task" button in `TopNavBar`).
   - Fills out `#task-title`, `#task-description`, `#task-priority` (`High`), and `#task-merge-mode` (`ManualReviewRequired`), and clicks `#btn-save-as-open` ("Save as Open (Backlog Queue)"), which persists the task as `Open` without enqueuing a Hangfire job and navigates to `/w/{workspaceId}/backlog`.
   - Verifies the newly created task card (`#kanban-card-{taskId}`) is visible on the Kanban board with the expected priority badge.
   - Clicks the task card on the Kanban board to navigate to `/w/{workspaceId}/tasks/{taskId}` (`TaskDetail.razor`), and verifies the task title, markdown description, status badge, priority, and history timeline render accurately.

4. **`Backlog_DependencyBadges_And_Filtering_RenderCorrectly`:**
   - Seeds two tasks (`Task #1` Prerequisite in `Open` status, and `Task #2` Dependent in `Suggested` status with a `TaskDependency` link to `Task #1`).
   - Navigates to `/w/{workspaceId}/backlog` and `/w/{workspaceId}/tasks/{task2Id}`, verifying dependency links are displayed and clickable in Chromium.

---

## 3. Verification & Acceptance Criteria

- [x] `PlaywrightWebHostFixture` starts `NightCodeForge.Web` cleanly, provides `SeedDatabaseAsync` / `QueryDatabaseAsync`, and tears down the Kestrel process and temp SQLite files in `DisposeAsync`.
- [x] All 4 baseline E2E tests in `CoreBacklogAndNavigationE2ETests` pass reliably in headless Chromium via `dotnet test --filter "Category=E2E&Category!=Acceptance"` with **zero LLM calls** and complete in under 30 seconds total.
- [x] Standard unit test command `dotnet test --filter "Category!=Acceptance&Category!=E2E"` remains unaffected.

