# 📋 Step 12.13B: "Refine in Ideation" Workflow & Mutation Tool Cards (Web UI)

> **Feature Implementation Step (Part 2 of 2 — Web UI Navigation, Session Seeding & Tool Cards)**  
> *Parent Plan:* [Phase 12 Master Plan](../12_EXTEND_FEATURES.md)  
> *Prerequisite Step:* [Step 12.13A: Ideation Backlog Mutation Tools & Prompt Policy](STEP_12_13A_IDEATION_BACKLOG_MUTATION_TOOLS_AND_PROMPT_POLICY.md)  
> *Related Steps:* [Step 12.4: Ideation Quick Notes & Suggestions](../../features/STEP_12_4_IDEATION_QUICK_NOTES_SUGGESTIONS_AND_DRAFT_PRUNING.md)  
> *Target Components:* `src/NightCodeForge.Web/Components/Pages`, `src/NightCodeForge.Web/Components/Backlog`, `src/NightCodeForge.Web/Components/Ideation`, `tests/NightCodeForge.Web.Tests` | *Status:* ✅ Completed | *Target Framework:* .NET 10 (C# 13)  
> *Aligns With:* [AGENTS.md](../../../AGENTS.md), [docs/PLAN.md](../../PLAN.md)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Objective

With the Ideation backlog mutation tools (`get_task`, `edit_task`, `set_task_priority`, `rework_priorities`, `remove_task`) in place ([Step 12.13A](STEP_12_13A_IDEATION_BACKLOG_MUTATION_TOOLS_AND_PROMPT_POLICY.md)), this step (Part 2 of Step 12.13) connects the Blazor Web UI so developers can seamlessly transition any unstarted task (`TaskOverallStatus.Suggested` or `TaskOverallStatus.Open` with `!task.HasExecutionRun`) into an interactive Ideation refinement session and observe live backlog mutations in chat:
1. **"💡 Refine in Ideation" Entry Points:** Add a unified refinement button on [`TaskDetail.razor`](../../src/NightCodeForge.Web/Components/Pages/TaskDetail.razor) and [`TaskDetailDrawer.razor`](../../src/NightCodeForge.Web/Components/Backlog/TaskDetailDrawer.razor) routing to workspace-scoped `/w/{workspaceId}/ideation?taskId={id}` (via `WorkspaceUrlHelper.BuildPath` / `@WorkspaceState.GetPath`).
2. **Scoped Task Session Auto-Seeding in `Ideation.razor` & `IdeationSessionState`:** Bind `[SupplyParameterFromQuery(Name = "taskId")]` in [`Ideation.razor`](../../src/NightCodeForge.Web/Components/Pages/Ideation.razor) to create or resume a task-scoped ideation conversation in [`IdeationSessionState`](../../src/NightCodeForge.Web/Services/IdeationSessionState.cs) pre-seeded with a refinement prompt that loads the task via `get_task({id})`.
3. **Rich Tool Execution Cards & Live SignalR Refresh:** Render compact, informative cards in the Ideation chat stream when the agent invokes `get_task`, `edit_task`, `set_task_priority` / `rework_priorities`, or `remove_task` (extracting payload parsing into a dedicated helper/view-model class in `NightCodeForge.Web.Services` per `AGENTS.md`), and refresh open Kanban/Backlog views dynamically on mutation events.

---

## 2. UI Presentation, Navigation & Ergonomics

### 2.1 "Refine in Ideation" Button on Task Views
Adhering to the repository's **UI and Presentation separation** rule ([AGENTS.md](../../AGENTS.md)):
1. **Task Detail Page ([`TaskDetail.razor`](../../src/NightCodeForge.Web/Components/Pages/TaskDetail.razor)):**
   - In the header action bar (alongside `Live Inspector`, `Dependencies`, and `Reject`), render for editable unstarted tasks (`(_task.Status == TaskOverallStatus.Suggested || _task.Status == TaskOverallStatus.Open) && !_task.HasExecutionRun`):
     ```razor
     @if ((_task.Status == TaskOverallStatus.Suggested || _task.Status == TaskOverallStatus.Open) && !_task.HasExecutionRun)
     {
         <a href="@WorkspaceUrlHelper.BuildPath(_task.WorkspaceId, $"ideation?taskId={_task.Id}")" class="forge-btn forge-btn-secondary" id="btn-refine-ideation" title="Refine task specification and resolve open questions in Ideation chat">
             💡 Refine in Ideation
         </a>
     }
     ```
2. **Task Detail Drawer ([`TaskDetailDrawer.razor`](../../src/NightCodeForge.Web/Components/Backlog/TaskDetailDrawer.razor)):**
   - In the drawer top header (next to `Inspect`), add a matching compact button:
     ```razor
     @if ((Task.Status == TaskOverallStatus.Suggested || Task.Status == TaskOverallStatus.Open) && !Task.HasExecutionRun)
     {
         <a href="@WorkspaceUrlHelper.BuildPath(Task.WorkspaceId, $"ideation?taskId={Task.Id}")" class="forge-btn forge-btn-secondary btn-xs" id="btn-drawer-refine-ideation" title="Refine in Ideation">
             💡 Refine
         </a>
     }
     ```

### 2.2 Ideation Page Scoped Task Initialization ([`Ideation.razor`](../../src/NightCodeForge.Web/Components/Pages/Ideation.razor))
1. **Query Parameter Binding:**
   `Ideation.razor` declares:
   ```csharp
   [SupplyParameterFromQuery(Name = "taskId")]
   public int? TaskId { get; set; }
   ```
2. **Refinement Session Auto-Seeding (in `IdeationSessionState`):**
   When `TaskId` is provided in the URL and the workspace is active:
   - `IdeationSessionState.InitializeTaskRefinementAsync(int workspaceId, int taskId)` checks if an ideation conversation specifically titled for Task `#{taskId}` already exists; if not, starts a new conversation titled `$"Refining Task #{task.Id}: {task.Title}"`.
   - Pre-seeds the chat input (or kicks off the first turn) with a targeted refinement prompt:
     `"Let's refine Task #{task.Id} ('{task.Title}'). Please inspect its current specification with get_task({task.Id}), identify any open questions or missing details, and let's resolve them before updating the task."`

### 2.3 Rich Tool Execution Badges & Live SignalR Broadcast
Extract JSON tool-argument/result parsing into a dedicated helper (`BacklogMutationCardParser` in `NightCodeForge.Web.Services`, keeping `.razor` `@code` blocks strictly presentation-only per `AGENTS.md`). In the ideation chat stream ([`Ideation.razor`](../../src/NightCodeForge.Web/Components/Pages/Ideation.razor)), tool calls for backlog mutation render compact, distinctive UI cards:
- 📖 **Loaded Task #14:** Displays compact title and current priority.
- 📝 **Updated Task #14:** Highlights modified fields (e.g. "Description updated", "Priority: P3 -> P2") with a direct link to open `@WorkspaceState.GetPath($"tasks/{taskId}")`.
- 🏷️ **Reprioritized Backlog:** Shows chip updates for affected tasks.
- 🗑️ **Removed Task #18:** Shows grayed-out cancellation badge with reason.

All mutations broadcast `AgentActivityEvent` through [`IAgentActivityStreamer`](../../src/NightCodeForge.Core/Streaming/IAgentActivityStreamer.cs), dynamically refreshing open Kanban boards, backlog lists, and task detail drawers without requiring manual page reloads.

---

## 3. Implementation Checklist & Deliverables

- [x] **Web UI Navigation & Task Detail Entry Points (`NightCodeForge.Web`):**
  - [x] Add "💡 Refine in Ideation" button to [`TaskDetail.razor`](../../src/NightCodeForge.Web/Components/Pages/TaskDetail.razor) header action bar for unstarted tasks (`(Status is Suggested or Open) && !HasExecutionRun`).
  - [x] Add "💡 Refine" button to [`TaskDetailDrawer.razor`](../../src/NightCodeForge.Web/Components/Backlog/TaskDetailDrawer.razor) header actions for unstarted tasks.
- [x] **Refinement Session Seeding & Tool Cards (`Ideation.razor` & `IdeationSessionState.cs`):**
  - [x] Add `[SupplyParameterFromQuery(Name = "taskId")] public int? TaskId { get; set; }` in [`Ideation.razor`](../../src/NightCodeForge.Web/Components/Pages/Ideation.razor) and implement conversation initialization / pre-seeding in `IdeationSessionState`.
  - [x] Implement `BacklogMutationCardParser` in `NightCodeForge.Web.Services` and render compact presentation cards in `Ideation.razor` for `get_task`, `edit_task`, `set_task_priority`, `rework_priorities`, and `remove_task` tool calls.
  - [x] Refresh Kanban board and backlog views dynamically on task mutation SignalR events.
- [x] **Automated Tests (`tests/NightCodeForge.Web.Tests`):**
  - [x] bUnit tests verifying `TaskDetail` and `TaskDetailDrawer` render "Refine in Ideation" only for unstarted tasks (`Suggested`/`Open` with `!HasExecutionRun`) and hide it when `HasExecutionRun == true` or `Status` is `Reviewed`/`Implemented`/`Ready`/`Closed`.
  - [x] Unit/bUnit tests for `BacklogMutationCardParser` and `Ideation.razor` `?taskId={id}` query parameter handling.
  - [x] **Playwright E2E Tests (Zero LLM Calls, `[Trait("Category", "E2E")]`):** Using `PlaywrightWebHostFixture` ([Step 11.39A](../fixes/STEP_11_39A_PLAYWRIGHT_E2E_HOST_FIXTURE_AND_BACKLOG_BASELINE_TESTS.md)), seed an unstarted `Open` task (`HasExecutionRun = false`) and an executed task (`HasExecutionRun = true`) + a seeded ideation conversation with `get_task` / `edit_task` / `set_task_priority` / `remove_task` tool turns; verify in Chromium that `#btn-refine-ideation` and `#btn-drawer-refine-ideation` appear only on the unstarted task, clicking `"💡 Refine in Ideation"` navigates to `/w/{wsId}/ideation?taskId={taskId}` with the pre-seeded refinement prompt, and the backlog mutation tool cards render in the chat transcript.

