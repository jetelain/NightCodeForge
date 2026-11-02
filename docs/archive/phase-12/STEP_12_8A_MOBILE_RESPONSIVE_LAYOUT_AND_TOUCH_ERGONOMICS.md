# 📱 Step 12.8A: Mobile-Responsive Layout, Navigation Drawer & Touch Ergonomics

> **Feature Implementation Step (Part 1 of 3 — Responsive CSS, Off-Canvas Drawer & Mobile Page Layouts)** *(formerly Step 11.6)*  
> *Parent Plan:* [Phase 12 Master Plan](../12_EXTEND_FEATURES.md)  
> *Companion Steps:* [Step 12.8B: Installable PWA Manifest & Service Worker Shell](../../features/STEP_12_8B_PROGRESSIVE_WEB_APP_MANIFEST_AND_SERVICE_WORKER.md) | [Step 12.8C: Web Worker Notifications & Subject Preferences](../../features/STEP_12_8C_WEB_WORKER_NOTIFICATIONS_AND_SUBJECT_PREFERENCES.md)  
> *Related Steps:* [Step 12.4: Ideation Quick Notes](../../features/STEP_12_4_IDEATION_QUICK_NOTES_SUGGESTIONS_AND_DRAFT_PRUNING.md) | [Step 12.12: Voice Support](../../features/STEP_12_12_VOICE_SUPPORT_AND_CONTINUOUS_DICTATION.md)  
> *Source Policy:* [AGENTS.md](../../../AGENTS.md)  
> *Project Target:* `src/NightCodeForge.Web`, `tests/NightCodeForge.Web.Tests` | *Status:* ✅ Completed | *Target Framework:* .NET 10 (C# 13)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Objective

1. **First-Class Mobile Responsive Layout:** Ensure the NightCodeForge Blazor dashboard is fully responsive, touch-friendly, and ergonomically optimized for smartphones and tablets (`< 768px` and `768px–1024px`).
2. **Adaptive Navigation & Touch Ergonomics:** Replace desktop-only sidebars on `< 768px` with a slide-out off-canvas navigation drawer, bottom-sheet task drawers (`90dvh`), $\ge 44\times 44\text{px}$ touch targets, dynamic viewport units (`100dvh`), safe-area insets (`env(safe-area-inset-*)`), and fixed-bottom chat input bars pinned above virtual keyboards.
3. **Page-by-Page Mobile Adaptations:** Adapt Ideation (segmented pane switcher), Human Question Inbox (touch option chips & `?taskId=` deep-linking), Task Execution Inspector, Backlog/Kanban, Review Room, and Dev Queue tables (responsive card stacks) for narrow screens.
4. **Network & Visibility Synchronization:** Display a non-blocking `"Reconnecting to Forge..."` banner on transient connection drops and refresh active task status upon mobile tab wakeup (`visibilitychange`).

*(PWA manifest and `service-worker.js` are specified in [Step 12.8B](STEP_12_8B_PROGRESSIVE_WEB_APP_MANIFEST_AND_SERVICE_WORKER.md); Web Worker notifications and per-subject preferences are specified in [Step 12.8C](STEP_12_8C_WEB_WORKER_NOTIFICATIONS_AND_SUBJECT_PREFERENCES.md).)*

---

## 2. Behavioral & Technical Specifications

### 2.1 Responsive Viewport & Navigation Structure

1. **Viewport Meta Tag & Safe-Area Insets:**
   Ensure [`App.razor`](../../src/NightCodeForge.Web/Components/App.razor) contains the responsive viewport configuration:
   ```html
   <meta name="viewport" content="width=device-width, initial-scale=1.0, maximum-scale=5.0, viewport-fit=cover" />
   ```
   - Update `.forge-layout` in `app.css` to use `height: 100dvh;` (with `100vh` fallback) so dynamic mobile browser address bars do not clip bottom-pinned UI elements.
   - Apply `env(safe-area-inset-top)`, `env(safe-area-inset-bottom)`, `env(safe-area-inset-left)`, and `env(safe-area-inset-right)` padding to the top navbar, mobile navigation drawer, fixed-bottom chat bars, and bottom sheets.

2. **Mobile Drawer Navigation ([`NavMenu.razor`](../../src/NightCodeForge.Web/Components/Layout/NavMenu.razor), [`TopNavBar.razor`](../../src/NightCodeForge.Web/Components/Layout/TopNavBar.razor) & [`MainLayout.razor`](../../src/NightCodeForge.Web/Components/Layout/MainLayout.razor)):**
   - On desktop screens (`> 1024px`): Display full `260px` desktop sidebar (or `72px` when manually collapsed).
   - On tablet screens (`768px` – `1024px`): Display compact `72px` icon-only sidebar.
   - On mobile screens (`< 768px`):
     - `.forge-layout` collapses to a single column (`grid-template-columns: 1fr`).
     - Sidebar (`NavMenu.razor`) transforms into a fixed off-canvas drawer (`position: fixed; top: 0; left: 0; bottom: 0; width: 280px; z-index: 1050;`) with full labels visible when open.
     - `TopNavBar.razor` renders a prominent hamburger button ($\ge 44\times 44\text{px}$) on the far left.
     - Tapping the hamburger opens the navigation drawer with smooth CSS slide-in (`transform: translateX(0)`) and a dark backdrop overlay. Tapping any `NavLink` or the backdrop closes the drawer automatically.

3. **Top Bar Quick Status Strip (`TopNavBar.razor`):**
   - On `< 768px`, hide secondary desktop breadcrumb text and compact the workspace selector and action buttons so they fit without horizontal overflow.
   - Display a compact active worker pill (e.g. `⚡ #42`) and the Question Inbox shortcut pill directly in the mobile top bar.

### 2.2 Touch Target Ergonomics & CSS Polish

1. **Touch Target Size:** All interactive buttons, nav links, dropdown triggers, filter chips, and proposal action chips must have a minimum clickable/tappable area of **$44\times 44\text{px}$** on mobile viewports.
2. **Hover Elimination:** Any critical action that currently relies on `:hover` must be persistently visible or accessible via direct tap on touch devices (`@media (hover: none)`).
3. **No Horizontal Scroll on Root:** `html`, `body`, `.forge-layout`, and `.forge-main-content` must enforce `overflow-x: hidden; max-width: 100vw;`.

### 2.3 Page-by-Page Mobile Adaptations

#### A. Home Dashboard (`Home.razor`) & Live Telemetry (`Monitoring.razor`)
- **Home Dashboard ([`Home.razor`](../../src/NightCodeForge.Web/Components/Pages/Home.razor)):** Collapse the KPI card row into a `2x2` (or `1fr` on `< 480px`) grid and stack the main two-column dashboard panels (`Action Required: Human Questions`, `Pending Human Code Reviews` from [Step 12.24](STEP_12_24_HOME_PAGE_PENDING_HUMAN_CODE_REVIEW_TASKS.md), `Active Task Execution`, `Workspace Overview`, `Recent Completions & Failures`) into a clean single-column vertical flow on `< 768px`.
- **Live Telemetry ([`Monitoring.razor`](../../src/NightCodeForge.Web/Components/Pages/Monitoring.razor)):** Stack active worker/GPU telemetry panels and live stream consoles vertically without horizontal overflow.

#### B. Ideation Chat ([`Ideation.razor`](../../src/NightCodeForge.Web/Components/Pages/Ideation.razor))
- **Mobile Pane Switcher (`< 768px`):** Collapse `.ideation-grid` into a single-column view with a sticky top segmented switcher bar (`💬 Chat` | `📋 Proposals (@Session.Proposals.Count)`).
- **Fixed Bottom Input Bar:** Pin the user prompt input box and "Send" button to the bottom of the viewport (`position: sticky; bottom: 0; padding-bottom: env(safe-area-inset-bottom);`).
- **Card Stacking:** [`IdeationProposalCard.razor`](../../src/NightCodeForge.Web/Components/Ideation/IdeationProposalCard.razor) displays its action buttons as full-width stacked buttons on `< 768px`.

#### C. Human Question Inbox ([`Inbox.razor`](../../src/NightCodeForge.Web/Components/Pages/Inbox.razor), [`QuestionCard.razor`](../../src/NightCodeForge.Web/Components/Inbox/QuestionCard.razor) & [`AgentQuestionTurnCard.razor`](../../src/NightCodeForge.Web/Components/Monitoring/AgentQuestionTurnCard.razor))
- **Deep-Link Query Parameter (`taskId`):** Add `[SupplyParameterFromQuery(Name = "taskId")] public int? TaskId { get; set; }` to `Inbox.razor`. When navigated to via `/inbox?taskId={TaskId}`, ensure the task's workspace/questions are visible and scroll/highlight `#question-card-task-{TaskId}`.
- **Touch-Friendly Options & Submission:** Style `.agent-option-chip` choice buttons as large touch-friendly pill chips ($\ge 44\text{px}$ height) and anchor `.submit-answer-btn` as a full-width primary button at the bottom of the card on mobile.

#### D. Task Execution Inspector ([`TaskExecutionInspector.razor`](../../src/NightCodeForge.Web/Components/Monitoring/TaskExecutionInspector.razor))
- **Responsive Stage & Run Selector:** Extract the inline two-column grid style in the Stage Conversations tab to `.inspector-conversations-grid`, collapsing to `1fr` on `< 768px` with `StageRunSelector` above `StageConversationViewer`.
- **Horizontal Stepper & Terminal Line-Wrap:** Make [`PipelineStepper.razor`](../../src/NightCodeForge.Web/Components/Monitoring/PipelineStepper.razor) horizontally touch-scrollable (`overflow-x: auto`) and add a `line-wrap` toggle in [`TerminalConsole.razor`](../../src/NightCodeForge.Web/Components/Monitoring/TerminalConsole.razor).

#### E. Backlog, Task Detail, Review Room & Dev Queue
- **Backlog & Kanban Board ([`Backlog.razor`](../../src/NightCodeForge.Web/Components/Pages/Backlog.razor) & `KanbanBoard.razor`):** On `< 768px`, stack Kanban columns vertically as collapsible status sections or scroll horizontally with `scroll-snap-type: x mandatory;`. Style [`TaskDetailDrawer.razor`](../../src/NightCodeForge.Web/Components/Backlog/TaskDetailDrawer.razor) as a **mobile bottom sheet** (`max-height: 90dvh; border-radius: 16px 16px 0 0;`).
- **Task Detail Page ([`TaskDetail.razor`](../../src/NightCodeForge.Web/Components/Pages/TaskDetail.razor)):** Collapse `.task-detail-grid` to a single vertical stack on `< 768px`.
- **Review Room ([`Review.razor`](../../src/NightCodeForge.Web/Components/Pages/Review.razor) & `DiffWorkspace.razor`):** Stack Review header action buttons into a full-width row and collapse the `DiffWorkspace.razor` file tree sidebar into a collapsible file-picker dropdown above the diff canvas on `< 768px`.
- **Dev Queue Tables ([`DevQueue.razor`](../../src/NightCodeForge.Web/Components/Pages/DevQueue.razor), `DevQueueTable.razor`, `FailedJobsTable.razor`, `ActiveLocksGrid.razor`):** Transform data tables into responsive card stacks on `< 768px`.

### 2.4 Connection State & Visibility Synchronization
- Display a non-blocking banner `"Reconnecting to Forge..."` in `MainLayout.razor` on transient SignalR/circuit drops, auto-dismissing upon reconnection.
- Wire a `visibilitychange` listener in `app.js` to notify the active Blazor circuit and refresh active task status when a mobile browser tab returns to the foreground.

---

## 3. Implementation Checklist & Deliverables

- [x] **Global Layout & Responsive Styles:**
  - [x] Update viewport meta tag in `App.razor` (`viewport-fit=cover`) and add `100dvh`, `env(safe-area-inset-*)`, and mobile breakpoint rules to `app.css`.
  - [x] Implement hamburger toggle in `TopNavBar.razor` and off-canvas mobile drawer with backdrop in `MainLayout.razor` / `NavMenu.razor` for `< 768px`.
  - [x] Enforce minimum $44\times 44\text{px}$ touch targets and eliminate horizontal root overflow.
- [x] **Mobile Page Adaptations:**
  - [x] Adapt `Home.razor` (including Pending Human Code Reviews card from Step 12.24) and `Monitoring.razor` grids for `< 768px` and `768px–1024px`.
  - [x] Add mobile pane switcher (`💬 Chat` | `📋 Proposals (@Session.Proposals.Count)`) and sticky bottom input bar in `Ideation.razor`.
  - [x] Add `[SupplyParameterFromQuery] int? TaskId` in `Inbox.razor` and optimize `AgentQuestionTurnCard.razor` touch chips.
  - [x] Adapt `TaskExecutionInspector.razor`, `PipelineStepper.razor`, `TerminalConsole.razor`, `Backlog.razor`, `TaskDetailDrawer.razor` (bottom sheet), `TaskDetail.razor`, `Review.razor`, and `DevQueue.razor` tables (card stacks) for `< 768px`.
- [x] **Network & Visibility Handling:**
  - [x] Add non-blocking `"Reconnecting to Forge..."` banner in `MainLayout.razor` and `visibilitychange` foreground state sync.
- [x] **Automated Testing:**
  - [x] bUnit tests asserting responsive mobile navigation drawer toggles and `Inbox.razor?taskId=` deep-linking.
  - [x] **Playwright E2E Mobile & Tablet Viewport Suite (Zero LLM Calls, `[Trait("Category", "E2E")]`):** Using `PlaywrightWebHostFixture` ([Step 11.39A](../fixes/STEP_11_39A_PLAYWRIGHT_E2E_HOST_FIXTURE_AND_BACKLOG_BASELINE_TESTS.md)) with seeded tasks, pending questions, and pending reviews across $375\times 667\text{px}$ (iPhone SE), $390\times 844\text{px}$ (iPhone 14), and $768\times 1024\text{px}$ (iPad) viewports:
    - [x] Verify zero horizontal root overflow (`document.documentElement.scrollWidth <= window.innerWidth`) across Home (`/`), Ideation (`/w/{id}/ideation`), Backlog (`/w/{id}/backlog`), Question Inbox (`/w/{id}/inbox?taskId={taskId}`), Task Inspector (`/w/{id}/inspector/{taskId}`), and Review Room (`/w/{id}/review`).
    - [x] Verify hamburger button opens the off-canvas `NavMenu` drawer, clicking a nav link navigates and auto-closes the drawer, `Ideation.razor` switches between `💬 Chat` and `📋 Proposals`, and `TaskDetailDrawer` opens as a bottom sheet on `< 768px`.

---

## 4. Implementation Notes

- **Drawer state** lives in `MainLayout.razor` (`mobile-nav-open`); `TopNavBar` exposes the hamburger (`#mobile-nav-toggle`), `NavMenu` the drawer (`#forge-sidebar.mobile-open`). The drawer closes on backdrop tap, nav-link click (`OnNavigate`) and location change.
- **Reconnect banner:** `MainLayout.razor` hosts a custom `#components-reconnect-modal` element styled as a non-blocking banner (Blazor toggles its `components-reconnect-*` classes). **Foreground sync:** `app.js` `registerForegroundSync` → `MainLayout.OnTabForegrounded` → `WorkerStateService.RefreshFromTelemetry()`.
- **Ideation:** `IdeationMobilePane` enum drives the segmented switcher; inactive pane is hidden by CSS under 768px.
- **Inbox deep link:** `/inbox?taskId=` forces the all-workspaces filter, wraps the matching group in `#question-card-task-{id}` (`.deep-link-highlight`) and scrolls via `NightCodeForge.scrollToElement`.
- **Other:** `.inspector-conversations-grid`, terminal line-wrap toggle (`#terminal-line-wrap-toggle`), `DiffWorkspace` file-picker toggle, Dev-Queue tables use `.responsive-card-table` + `data-label` card stacks, Kanban uses horizontal scroll-snap columns, `.task-drawer` becomes a bottom sheet (`90dvh`). All mobile rules are in the Step 12.8A section at the end of `app.css`.
- **Known gap:** `TaskDetailDrawer.razor` is not hosted by any page today; its bottom-sheet CSS is verified in E2E on a synthetic DOM instance.
- **Tests:** `NavMenuShellTests` (drawer, backdrop, banner, foreground registration), `InboxPageTests` (deep link), `TerminalConsoleTests` (wrap toggle), `E2E/MobileResponsiveLayoutE2ETests` (375×667, 390×844, 768×1024).
