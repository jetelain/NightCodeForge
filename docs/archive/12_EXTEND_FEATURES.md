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
| **12.3** | [🎨 Step 12.3: Conversational UI Unification, Review Room & Task Detail Ergonomics, Inspector Grouping & Running State Hygiene](phase-12/STEP_12_3_CONVERSATIONAL_UI_UNIFICATION_AND_REVIEW_ERGONOMICS.md) | Reuse `AgentQuestionTurnCard` in `/inbox`, fix `PipelineStepper` false "Pending" state during active runs, unify `StageConversationViewer` with `Ideation` compact bubble layout, provide sticky file tree with `/inspector` and `/tasks/{id}` links, hierarchical tree view preventing path truncation, DiffViewer syntax highlighting, in-flight task display in live telemetry, `<optgroup>` status grouping in Task Inspector, strict pending reviews filtering in Review Room, dedicated "Changes" diff tab in Task Details with "Open in Review Room" button, and pulsing running markers on Kanban cards. | Completed |
| **12.14** | [📦 Step 12.14: Global Skills Injection & Stack-Specific Tool Provisioning (csnav, mdfetch, codecondenser)](phase-12/STEP_12_14_GLOBAL_SKILLS_INJECTION_AND_STACK_TOOLING.md) | Central global skills store (`~/.nightcodeforge/skills/`), stack-driven skill mapping via `CodeStackPresets`, transparent worktree injection (symlink / bind-mount with zero git pollution), and integration of `csnav` (C# navigation & BCL/NuGet decompiler), `mdfetch` (markdown fetcher), and `codecondenser` (clone detection). | Completed |
| **12.17** | [📜 Step 12.17: Follow Trail Auto-Scroll & Bottom-Right Action in Task Execution Inspector & Reasoning Trace](phase-12/STEP_12_17_FOLLOW_TRAIL_AUTO_SCROLL_INSPECTOR_AND_REASONING_TRACE.md) | Universal reasoning trace auto-scroll tracking in Ideation Chat and Task Execution Inspector, mounting a floating follow-trail button at the bottom-right of `StageConversationViewer`, dual-tier pinning of stream and open `.think-content`, non-intrusive decoupling on manual upward scroll, and bUnit test coverage. | Completed |
| **12.19** | [🛡️ Step 12.19: Air-Gapped & Offline Web Application Hardening](phase-12/STEP_12_19_AIR_GAPPED_OFFLINE_WEB_APP_HARDENING.md) | Total elimination of external Google Fonts and CDN dependencies; local bundling of Inter and JetBrains Mono WOFF2 typography with native system fallbacks; strict Content Security Policy (CSP) guardrails; zero-telemetry privacy guarantee; and automated static analysis subresource tests. | Completed |

---

## 3. Completed Definition of Done (DoD) Record

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

- [x] **Step 12.14 (Global Skills Injection & Stack-Specific Tool Provisioning):**
  - [x] Implement `IWorkspaceSkillsService` and `IBundledSkillsProvider` providing stack-driven global skills (`csharp-nav`, `mdfetch`, `codecondenser`) without repository git pollution.
  - [x] Implement `ICompanionToolInstaller` auto-installing companion tools via `dotnet tool install -g`.
  - [x] Mount host global skills directory and `~/.dotnet/tools` read-only into sandbox containers.

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
