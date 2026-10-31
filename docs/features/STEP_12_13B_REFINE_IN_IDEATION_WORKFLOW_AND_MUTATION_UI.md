# 📋 Step 12.13B: "Refine in Ideation" Workflow & Mutation Tool Cards (Web UI)

> **Feature Implementation Step (Part 2 of 2 — Web UI Navigation, Session Seeding & Tool Cards)**  
> *Parent Plan:* [Features & Integrations Roadmap](README.md)  
> *Prerequisite Step:* [Step 12.13A: Ideation Backlog Mutation Tools & Prompt Policy](STEP_12_13A_IDEATION_BACKLOG_MUTATION_TOOLS_AND_PROMPT_POLICY.md)  
> *Related Steps:* [Step 12.4: Ideation Quick Notes & Suggestions](STEP_12_4_IDEATION_QUICK_NOTES_SUGGESTIONS_AND_DRAFT_PRUNING.md)  
> *Target Components:* `src/NightCodeForge.Web/Components/Pages`, `src/NightCodeForge.Web/Components/Backlog`, `src/NightCodeForge.Web/Components/Ideation`, `tests/NightCodeForge.Web.Tests` | *Status:* Planned | *Target Framework:* .NET 10 (C# 13)  
> *Aligns With:* [AGENTS.md](../../AGENTS.md), [docs/PLAN.md](../PLAN.md)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Objective

With the Ideation backlog mutation tools (`get_task`, `edit_task`, `set_task_priority`, `rework_priorities`, `remove_task`) in place ([Step 12.13A](STEP_12_13A_IDEATION_BACKLOG_MUTATION_TOOLS_AND_PROMPT_POLICY.md)), this step (Part 2 of Step 12.13) connects the Blazor Web UI so developers can seamlessly transition any unstarted task (`Suggested`, `Open`, `Draft`) into an interactive Ideation refinement session and observe live backlog mutations in chat:
1. **"💡 Refine in Ideation" Entry Points:** Add a unified refinement button on [`TaskDetail.razor`](../../src/NightCodeForge.Web/Components/Pages/TaskDetail.razor), [`TaskDetailDrawer.razor`](../../src/NightCodeForge.Web/Components/Backlog/TaskDetailDrawer.razor), and `IdeationBacklogTab.razor` ([Step 12.4](STEP_12_4_IDEATION_QUICK_NOTES_SUGGESTIONS_AND_DRAFT_PRUNING.md)) routing to `/ideation?taskId={id}`.
2. **Scoped Task Session Auto-Seeding in `Ideation.razor`:** Bind `[SupplyParameterFromQuery(Name = "taskId")]` in [`Ideation.razor`](../../src/NightCodeForge.Web/Components/Pages/Ideation.razor) to create or resume a task-scoped ideation conversation pre-seeded with a refinement prompt that loads the task via `get_task({id})`.
3. **Rich Tool Execution Cards & Live SignalR Refresh:** Render compact, informative cards in the Ideation chat stream when the agent invokes `get_task`, `edit_task`, `set_task_priority` / `rework_priorities`, or `remove_task`, and refresh open Kanban/Backlog views dynamically on mutation events.

---

## 2. UI Presentation, Navigation & Ergonomics

### 2.1 "Refine in Ideation" Button on Task Views
Adhering to the repository's **UI and Presentation separation** rule ([AGENTS.md](../../AGENTS.md)):
1. **Task Detail Page ([`TaskDetail.razor`](../../src/NightCodeForge.Web/Components/Pages/TaskDetail.razor)):**
   - In the header action bar (alongside `Live Inspector`, `Dependencies`, and `Reject`), render for unstarted tasks (`Suggested`, `Open`, `Draft`):
     ```razor
     @if (_task.Status == TaskOverallStatus.Suggested || _task.Status == TaskOverallStatus.Open || _task.Status == TaskOverallStatus.Draft)
     {
         <a href="/ideation?taskId=@_task.Id" class="forge-btn forge-btn-secondary" id="btn-refine-ideation" title="Refine task specification and resolve open questions in Ideation chat">
             💡 Refine in Ideation
         </a>
     }
     ```
2. **Task Detail Drawer ([`TaskDetailDrawer.razor`](../../src/NightCodeForge.Web/Components/Backlog/TaskDetailDrawer.razor)):**
   - In the drawer top header (next to `Inspect`), add a matching compact button:
     ```razor
     @if (Task.Status == TaskOverallStatus.Suggested || Task.Status == TaskOverallStatus.Open || Task.Status == TaskOverallStatus.Draft)
     {
         <a href="/ideation?taskId=@Task.Id" class="forge-btn forge-btn-secondary btn-xs" id="btn-drawer-refine-ideation" title="Refine in Ideation">
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
2. **Refinement Session Auto-Seeding:**
   When `TaskId` is provided in the URL and the workspace is active:
   - Checks if an ideation conversation specifically titled or tagged for Task `#{TaskId}` already exists; if not, starts a new conversation titled `$"Refining Task #{task.Id}: {task.Title}"`.
   - Pre-seeds the conversation with a targeted refinement prompt:
     `"Let's refine Task #{task.Id} ('{task.Title}'). Please inspect its current specification with get_task({task.Id}), identify any open questions or missing details, and let's resolve them before updating the task."`
   - Automatically kicks off the first turn or places the pre-filled prompt into the chat input for the maintainer to review and send.

### 2.3 Rich Tool Execution Badges & Live SignalR Broadcast
In the ideation chat stream ([`Ideation.razor`](../../src/NightCodeForge.Web/Components/Pages/Ideation.razor)), tool calls for backlog mutation render compact, distinctive UI cards:
- 📖 **Loaded Task #14:** Displays compact title and current priority.
- 📝 **Updated Task #14:** Highlights modified fields (e.g. "Description updated", "Priority: P3 -> P2") with a direct link to open `/tasks/14`.
- 🏷️ **Reprioritized Backlog:** Shows chip updates for affected tasks.
- 🗑️ **Removed Task #18:** Shows grayed-out removal badge with reason.

All mutations broadcast `AgentActivityEvent` through [`IAgentActivityStreamer`](../../src/NightCodeForge.Core/Streaming/IAgentActivityStreamer.cs), dynamically refreshing open Kanban boards, backlog lists, and task detail drawers without requiring manual page reloads.

---

## 3. Implementation Checklist & Deliverables

- [ ] **Web UI Navigation & Task Detail Entry Points (`NightCodeForge.Web`):**
  - [ ] Add "💡 Refine in Ideation" button to [`TaskDetail.razor`](../../src/NightCodeForge.Web/Components/Pages/TaskDetail.razor) header action bar for unstarted tasks (`Suggested`, `Open`, `Draft`).
  - [ ] Add "💡 Refine" button to [`TaskDetailDrawer.razor`](../../src/NightCodeForge.Web/Components/Backlog/TaskDetailDrawer.razor) header actions for unstarted tasks.
- [ ] **Refinement Session Seeding & Tool Cards (`Ideation.razor`):**
  - [ ] Add `[SupplyParameterFromQuery(Name = "taskId")] public int? TaskId { get; set; }` in [`Ideation.razor`](../../src/NightCodeForge.Web/Components/Pages/Ideation.razor) and implement conversation initialization / pre-seeding via `IdeationSessionState`.
  - [ ] Render compact presentation cards in `Ideation.razor` for `get_task`, `edit_task`, `set_task_priority`, `rework_priorities`, and `remove_task` tool calls.
  - [ ] Refresh Kanban board and backlog views dynamically on task mutation SignalR events.
- [ ] **Automated Tests (`tests/NightCodeForge.Web.Tests`):**
  - [ ] bUnit tests verifying `TaskDetail` and `TaskDetailDrawer` render "Refine in Ideation" only for unstarted tasks (`Suggested`, `Open`, `Draft`) and hide it for `InProgress`/`Closed` tasks.
  - [ ] bUnit tests verifying `Ideation.razor` handles `?taskId={id}` query parameter, initializes the refinement session, and renders mutation tool cards.

