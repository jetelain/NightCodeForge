# 🧩 Phase 12 (Archive): Completed Features & Integrations

> **Archived Feature Specifications**  
> *Parent Index:* [docs/PLAN.md](../PLAN.md) | *Archive Index:* [docs/archive/README.md](README.md) | *Remaining Features:* [docs/features/README.md](../features/README.md)  
> *Status:* Completed Steps Archive | *Target Framework:* .NET 10 (C# 13)

---

## 1. Overview

This document archives the **completed feature steps** from Phase 12. All remaining planned and active feature specifications now reside in **[`docs/features/README.md`](../features/README.md)**.

---

## 2. Completed Feature Steps (Archive)

| Step | Specification | Focus & Key Deliverables | Status |
| :--- | :--- | :--- | :--- |
| **12.1A** | [🔗 Step 12.1A: Backlog Context Digest, Agent Dependency Directives & In-Tool Cycle Rejection](phase-12/STEP_12_1A_BACKLOG_CONTEXT_DIGEST_AND_IN_TOOL_CYCLE_REJECTION.md) | `IWorkspaceBacklogSummaryService` (`<existing_workspace_tasks>` top 20 active tasks in Layer 3 so Ideation immediately knows `#ID`, title, status, priority, and dependencies), numeric `#ID` resolution in `IdeationProposalViewModel`, and immediate in-tool cycle rejection via `ITaskMarkdownValidator` in `CanonicalTools`. | Completed |
| **12.3** | [🎨 Step 12.3: Conversational UI Unification, Review Room & Task Detail Ergonomics, Inspector Grouping & Running State Hygiene](phase-12/STEP_12_3_CONVERSATIONAL_UI_UNIFICATION_AND_REVIEW_ERGONOMICS.md) | Reuse `AgentQuestionTurnCard` in `/inbox`, fix `PipelineStepper` false "Pending" state during active runs, unify `StageConversationViewer` with `Ideation` compact bubble layout, provide sticky file tree with `/inspector` and `/tasks/{id}` links, hierarchical tree view preventing path truncation, DiffViewer syntax highlighting, in-flight task display in live telemetry, `<optgroup>` status grouping in Task Inspector, strict pending reviews filtering in Review Room, dedicated "Changes" diff tab in Task Details with "Open in Review Room" button, and pulsing running markers on Kanban cards. | Completed |
| **12.8A** | [📱 Step 12.8A: Mobile-Responsive Layout, Navigation Drawer & Touch Ergonomics](phase-12/STEP_12_8A_MOBILE_RESPONSIVE_LAYOUT_AND_TOUCH_ERGONOMICS.md) | Off-canvas hamburger navigation drawer (`< 768px`), compact tablet sidebar (`768px–1024px`), `100dvh` & safe-area insets, $\ge 44\times 44\text{px}$ touch targets, mobile pane switcher in `Ideation.razor`, `/inbox?taskId=` deep-linking, mobile bottom-sheet `TaskDetailDrawer.razor`, card-stack layouts for Home/DevQueue/Review/Inspector, and multi-viewport Playwright E2E tests ($375\times 667$, $390\times 844$, $768\times 1024$). | Completed |
| **12.13A** | [📋 Step 12.13A: Ideation Backlog Mutation Tools & Zero-Surprise Prompt Policy](phase-12/STEP_12_13A_IDEATION_BACKLOG_MUTATION_TOOLS_AND_PROMPT_POLICY.md) | `TaskCannotBeModifiedException`, `IBacklogMutationToolService` (`get_task`, `edit_task`, `set_task_priority`, `rework_priorities`, `remove_task`) in `CanonicalTools` with DAG cycle validation, `AuditActors.IdeationAgent` audit logs, markdown disk sync, upfront functional ambiguity resolution prompt policy, and `skills/ncf-backlog/SKILL.md` alignment. | Completed |
| **12.13B** | [📋 Step 12.13B: "Refine in Ideation" Workflow & Mutation Tool Cards (Web UI)](phase-12/STEP_12_13B_REFINE_IN_IDEATION_WORKFLOW_AND_MUTATION_UI.md) | Unified **"💡 Refine in Ideation"** button on `TaskDetail.razor` and `TaskDetailDrawer.razor` routing to `/w/{wsId}/ideation?taskId={id}` with pre-seeded refinement conversation, compact mutation tool call cards in `Ideation.razor`, and LLM-free Playwright E2E tests. | Completed |
| **12.14** | [📦 Step 12.14: Global Skills Injection & Stack-Specific Tool Provisioning (csnav, mdfetch, codecondenser)](phase-12/STEP_12_14_GLOBAL_SKILLS_INJECTION_AND_STACK_TOOLING.md) | Central global skills store (`~/.nightcodeforge/skills/`), stack-driven skill mapping via `CodeStackPresets`, transparent worktree injection (symlink / bind-mount with zero git pollution), and integration of `csnav` (C# navigation & BCL/NuGet decompiler), `mdfetch` (markdown fetcher), and `codecondenser` (clone detection). | Completed |
| **12.16A** | [🔒 Step 12.16A: Basic Tailscale Connectivity (`TailscaleServeHostedService` & Forwarded Headers MVP)](phase-12/STEP_12_16A_TAILSCALE_SERVE_SERVICE_AND_FORWARDED_HEADERS.md) | `ForwardedHeadersOptions` (`X-Forwarded-For`, `X-Forwarded-Proto`) in `Program.cs` for reverse-proxy compatibility, `TailscaleServeHostedService` (`tailscale serve` device or `svc:ncf` mode, dynamic Kestrel port detection, `--tailscale` CLI / `config.json` activation, graceful shutdown reset), and Playwright reverse-proxy header E2E verification. | Completed |
| **12.17** | [📜 Step 12.17: Follow Trail Auto-Scroll & Bottom-Right Action in Task Execution Inspector & Reasoning Trace](phase-12/STEP_12_17_FOLLOW_TRAIL_AUTO_SCROLL_INSPECTOR_AND_REASONING_TRACE.md) | Universal reasoning trace auto-scroll tracking in Ideation Chat and Task Execution Inspector, mounting a floating follow-trail button at the bottom-right of `StageConversationViewer`, dual-tier pinning of stream and open `.think-content`, non-intrusive decoupling on manual upward scroll, and bUnit test coverage. | Completed |
| **12.19** | [🛡️ Step 12.19: Air-Gapped & Offline Web Application Hardening](phase-12/STEP_12_19_AIR_GAPPED_OFFLINE_WEB_APP_HARDENING.md) | Total elimination of external Google Fonts and CDN dependencies; local bundling of Inter and JetBrains Mono WOFF2 typography with native system fallbacks; strict Content Security Policy (CSP) guardrails; zero-telemetry privacy guarantee; and automated static analysis subresource tests. | Completed |
| **12.24** | [🔍 Step 12.24: Home Page Pending Human Code Review Tasks](phase-12/STEP_12_24_HOME_PAGE_PENDING_HUMAN_CODE_REVIEW_TASKS.md) | `HomePendingReviewItem` aggregation in `HomeDashboardBuilder`, top Command Center **Review Room** button & badge (`#home-review-btn`), independent clickable `🔍 Review: N` badges on workspace summary cards, dedicated **Tasks Awaiting Human Code Review** section (`#home-pending-reviews-section`) with 1-click `🔍 Review & Merge` deep-links, and LLM-free Playwright E2E tests. | Completed |
| **12.26** | [🎨 Step 12.26: Workspace Visual Identity — Custom Emoji, Color Code & Settings Management](phase-12/STEP_12_26_WORKSPACE_EMOJI_COLOR_CODE_AND_SETTINGS.md) | Custom workspace emoji and hex color codes across navigation bar, workspace cards, and drawer headers; EF Core migration for `Workspace.Emoji` and `Workspace.ColorCode`; `WorkspacePresentationHelper` with luminance contrast calculation and curated palette swatches; interactive visual identity editor in `WorkspaceSettingsTab.razor`; and browser Playwright E2E tests. | Completed |

---

## 3. Completed Definition of Done (DoD) Record

- [x] **Step 12.1A (Backlog Context Digest, Agent Dependency Directives & In-Tool Cycle Rejection):**
  - [x] Implement `IWorkspaceBacklogSummaryService` generating compact `<existing_workspace_tasks>` digests filtering out terminal/archived tasks and sorting by `Priority ASC` then `UpdatedAt DESC` (capped to 20 with overflow notice).
  - [x] Inject backlog summary into `IdeationService` system prompt and Stage 1 Task Review prompt in `PromptPrefixStabilizer` strictly within Layer 3 (Task Scope).
  - [x] Update Stage 1 directive in `CrucibleAgentHarness` and `IdeationProposalViewModel.ResolveDependencies` in `IdeationSessionState` to support numeric IDs (`101`, `#101`).
  - [x] Add frontmatter dependency validation in `CanonicalTools` for `edit` and `write` targeting `docs/tasks/*.md` using `IDependencyResolutionEngine.HasCycle`.

- [x] **Step 12.3 (Conversational UI Unification, Review Room & Task Detail Ergonomics, Inspector Grouping & Running State Hygiene):**
  - [x] Enhance `AgentQuestionTurnCard.razor` with task metadata header (Task #Id, Title, Stage, Time) and reuse in `Inbox.razor` / `QuestionCard.razor`.
  - [x] Fix `PipelineStepper.razor` to preserve `StageExecutionStatus.Running` when `OverallStatus == TaskOverallStatus.InProgress` or stage conversation is running.
  - [x] Explicitly pass `CurrentStageStatus` in `TaskExecutionInspector.razor` to `<PipelineStepper>`.
  - [x] Unify `StageConversationViewer.razor` with `Ideation.razor` compact chat bubble architecture, avatar column, inline `<details class="think-trace">`, and compact `<details class="tool-trace">`.
  - [x] Style `.file-tree` in `Review.razor` with sticky positioning (`position: sticky; top: 1rem; max-height: calc(100vh - 2rem)`).
  - [x] Add deep link buttons to `/inspector/@_snapshot.TaskId` and `/tasks/@_snapshot.TaskId` in `Review.razor` header.
  - [x] Update `TaskReviewService.GetPendingReviewsAsync` and `Review.razor` to strictly filter out closed and merged tasks (`Status != TaskOverallStatus.Closed`).
  - [x] Implement `ReviewFileTreeBuilder` helper class in `NightCodeForge.Web.Services` creating hierarchical `ReviewFileTreeNode` tree with folder grouping and single-child compression.
  - [x] Update `Review.razor` left panel to render hierarchical tree view ensuring filename is always visible.
  - [x] Implement `ISyntaxHighlightingService` / `SimpleSyntaxHighlighter` tokenizing C#, TS/JS, Python, JSON, XML, Shell code lines into safe HTML spans.
  - [x] Integrate syntax highlighting in `DiffViewer.razor` for both Side-by-Side and Unified views.
  - [x] Implement `TaskInspectorGroupingHelper` in `NightCodeForge.Web.Services` and update `TaskExecutionInspector.razor` dropdown with `<optgroup>` categories (Running, In Flight waiting for slot, Waiting for Human, Closed) sorted by priority.
  - [x] Add an In-Flight Task Card / Banner in `ActiveOperationsHero.razor` on `/monitoring` showing active task metadata, active stage, and direct navigation links.
  - [x] Implement `ITaskDiffService` and add a "Changes" (Diff) tab in `TaskDetail.razor` embedding `DiffViewer.razor` for both active and closed/merged tasks.
  - [x] Add a prominent `🔍 Open in Review Room` primary action button in `TaskDetail.razor` header when `task.Status == TaskOverallStatus.Ready`.
  - [x] Add explicit running indicator badges (`● Running` with pulse glow animation) and active stage styling on `KanbanCard.razor` matching the DAG view in-flight indicator.

- [x] **Step 12.8A (Mobile-Responsive Layout, Navigation Drawer & Touch Ergonomics):**
  - [x] Update viewport meta tag in `App.razor` (`viewport-fit=cover`) and add `100dvh`, `env(safe-area-inset-*)`, and mobile breakpoint rules to `app.css`.
  - [x] Implement hamburger toggle in `TopNavBar.razor` and off-canvas mobile drawer with backdrop in `MainLayout.razor` / `NavMenu.razor` for `< 768px`.
  - [x] Enforce minimum $44\times 44\text{px}$ touch targets and eliminate horizontal root overflow.
  - [x] Adapt `Home.razor`, `Monitoring.razor`, `Ideation.razor` (mobile chat/proposals switcher), `Inbox.razor` (`[SupplyParameterFromQuery] int? TaskId`), `TaskDetailDrawer.razor` (bottom-sheet), and Review Room for mobile viewports.
  - [x] Playwright E2E mobile and tablet viewport tests across $375\times 667\text{px}$ (iPhone SE), $390\times 844\text{px}$ (iPhone 14), and $768\times 1024\text{px}$ (iPad).

- [x] **Step 12.13A (Ideation Backlog Mutation Tools & Zero-Surprise Prompt Policy):**
  - [x] Implement `TaskCannotBeModifiedException` domain exception for started/terminal tasks.
  - [x] Implement `IBacklogMutationToolService` (`get_task`, `edit_task`, `set_task_priority`, `rework_priorities`, `remove_task`) in `CanonicalTools` with DAG cycle validation, markdown disk sync, and `AuditActors.IdeationAgent` audit history attribution.
  - [x] Update `skills/ncf-backlog/SKILL.md` and Ideation prompt policy with upfront ambiguity resolution mandate.

- [x] **Step 12.13B ("Refine in Ideation" Workflow & Mutation Tool Cards):**
  - [x] Add "💡 Refine in Ideation" primary action button on unstarted tasks in `TaskDetail.razor` and `TaskDetailDrawer.razor` routing to `/w/{wsId}/ideation?taskId={id}`.
  - [x] Support pre-seeded refinement conversation in `Ideation.razor` via `[SupplyParameterFromQuery] int? TaskId`.
  - [x] Render compact mutation tool cards in `Ideation.razor` chat transcript for backlog mutation tool calls and results.
  - [x] Add LLM-free Playwright E2E browser tests in `HitlAndObservabilityE2ETests.cs`.

- [x] **Step 12.14 (Global Skills Injection & Stack-Specific Tool Provisioning):**
  - [x] Implement `IWorkspaceSkillsService` and `IBundledSkillsProvider` providing stack-driven global skills (`csharp-nav`, `mdfetch`, `codecondenser`) without repository git pollution.
  - [x] Implement `ICompanionToolInstaller` auto-installing companion tools via `dotnet tool install -g`.
  - [x] Mount host global skills directory and `~/.dotnet/tools` read-only into sandbox containers.

- [x] **Step 12.16A (Basic Tailscale Connectivity - TailscaleServeHostedService & Forwarded Headers MVP):**
  - [x] Implement `ITailscaleServeService` / `TailscaleServeHostedService` with dynamic Kestrel port detection, `svc:ncf` service mode, device fallback mode, hot-apply support, and graceful shutdown reset.
  - [x] Add factory default `Tailscale` configuration, CLI `--tailscale*` flags, and `ForwardedHeadersOptions` in `Program.cs`.
  - [x] Add Playwright reverse-proxy header E2E test verifying Blazor InteractiveServer circuit connectivity.

- [x] **Step 12.17 (Follow Trail Auto-Scroll & Bottom-Right Action in Task Execution Inspector & Reasoning Trace):**
  - [x] Implement auto-scroll tracking and floating follow-trail button at the bottom-right of `StageConversationViewer` and `Ideation`.
  - [x] Support dual-tier pinning of outer stream and open `.think-content` containers with manual upward scroll decoupling.

- [x] **Step 12.19 (Air-Gapped & Offline Web Application Hardening - Quick Win):**
  - [x] Bundle optimized Latin-subset WOFF2 font files for Inter and JetBrains Mono in `src/NightCodeForge.Web/wwwroot/fonts/`.
  - [x] Create `wwwroot/css/fonts.css` with local `@font-face` definitions using `font-display: swap;`.
  - [x] Refactor `App.razor` to completely excise `https://fonts.googleapis.com` and `https://fonts.gstatic.com` preconnect and stylesheet tags.
  - [x] Update `theme.css` with robust native system font stacks (`system-ui`, `ui-monospace`) as fallbacks.
  - [x] Implement ASP.NET Core Content Security Policy (CSP) middleware / headers restricting subresources to `'self'`, local WebSockets (`ws:`, `wss:`), and data URIs.
  - [x] Add `AirGappedSubresourcesTests` in `NightCodeForge.Web.Tests` asserting zero external HTTP/HTTPS subresource references across all client-facing files.

- [x] **Step 12.24 (Home Page Pending Human Code Review Tasks):**
  - [x] Implement `HomePendingReviewItem` and extend `HomeDashboardSnapshot` in `NightCodeForge.Web.Services` with `PendingReviewTasks` and `PendingReviewCount`.
  - [x] Add top Command Center `#home-review-btn` (`href="/review"`) and `#home-review-count-badge` in `Home.razor`.
  - [x] Add independent clickable `#home-ws-review-{ws.Id}` badge on recent workspace summary cards.
  - [x] Add `#home-pending-reviews-section` in `Home.razor` with 1-click `#home-review-task-btn-{taskId}` links to Review Room.
  - [x] Add Playwright E2E browser tests in `HitlAndObservabilityE2ETests.cs`.

- [x] **Step 12.26 (Workspace Visual Identity — Custom Emoji, Color Code & Settings Management):**
  - [x] Add `Emoji` and `ColorCode` properties to `Workspace` entity and create EF Core migration `AddWorkspaceEmojiAndColorCode`.
  - [x] Implement `WorkspacePresentationHelper` with hex color validation, luminance-based text contrast calculation, fallback emoji assignment, and curated preset color palettes.
  - [x] Update `WorkspaceCreationModel`, `WorkspaceProvisioningService`, and `WorkspaceStateService` to support visual identity defaults and persistence.
  - [x] Update `TopNavBar.razor`, `Home.razor`, and `WizardStep1PathProbe.razor` to render custom workspace emoji and color badges.
  - [x] Create visual identity settings UI in `WorkspaceSettingsTab.razor` with real-time preview, emoji picker, and color palette chips.
  - [x] Author unit tests in `NightCodeForge.Web.Tests` and Playwright E2E tests in `WorkspaceVisualIdentityE2ETests.cs`.

