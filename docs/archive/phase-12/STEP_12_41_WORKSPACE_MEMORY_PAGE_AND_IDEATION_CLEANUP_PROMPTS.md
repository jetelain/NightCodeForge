# 🧠 Step 12.41: Full-Page Agent Memory Explorer, Quick Ideation Cleanup Prompts & Nightwatch Memory Audit

> **Feature Implementation Step (Replacing `WorkspaceMemoryDrawer` Modal with Dedicated `/memory` Page & Ideation Cleanup Workflows)**  
> *Parent Plan:* [Features & Integrations Roadmap](../../features/README.md)  
> *Prerequisite Step:* [Step 12.39: Two-Tier Agent Memory Management, Selective `:rw` Memory Mount & Live UI Notifications](STEP_12_39_TWO_TIER_AGENT_MEMORY_MANAGEMENT_AND_UI_NOTIFICATIONS.md)  
> *Companion Step:* [Step 12.35B: Idle-GPU Nightwatch Deep Transcript & Workspace Analysis](../../features/STEP_12_35B_NIGHTWATCH_IDLE_DEEP_ANALYSIS_AND_LEGACY_DIAGNOSTIC_REPLACEMENT.md) *(Memory scanning & improvement suggestions)*  
> *Target Components:* `src/NightCodeForge.Web`, `tests/NightCodeForge.Web.Tests` | *Status:* Completed | *Target Framework:* .NET 10 (C# 13)  
> *Aligns With:* [AGENTS.md](../../../AGENTS.md), [docs/PLAN.md](../../PLAN.md)
>
> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Problem Statement & Motivation

[Step 12.39](STEP_12_39_TWO_TIER_AGENT_MEMORY_MANAGEMENT_AND_UI_NOTIFICATIONS.md) introduced the two-tier Markdown memory system (Tier 1 Committed Repository Memory: `AGENTS.md` + `.agents/memory/*.md`; Tier 2 Local Workspace Memory: `/workspace/memory/MEMORY.md` + topic `.md` files) and surfaced it through `WorkspaceMemoryDrawer.razor` — a modal dialog accessible primarily from Ideation, Settings, and tool-call badges.

In practice, the modal layout has three major usability limitations:
1. **Constrained Viewport:** A modal dialog is too narrow and short to comfortably read, compare, or edit multi-section Markdown memory files alongside the file catalog.
2. **Hidden Discoverability:** Memory is a first-class workspace artifact across Ideation, Backlog, Execution, and Review, yet it has no top-level navigation entry or dedicated URL.
3. **Manual Maintenance Friction:** As `MEMORY.md` approaches its pinned injection limits (`MaxPinnedMemoryLines = 150`, `MaxPinnedMemoryBytes = 12_288`) or accumulates stale notes, there is no one-click way to ask the Ideation agent to summarize, deduplicate, or promote durable conventions into a repository memory task.

This step replaces the modal with a dedicated full-page **Agent Memory Explorer** (`/memory` and `/w/{WorkspaceId:int}/memory`), adds **Quick Ideation Prompts** to summarize and clean up memory (updating local workspace memory directly and proposing a Stage 2 task when repository memory needs updates), and pairs with [Step 12.35B](../../features/STEP_12_35B_NIGHTWATCH_IDLE_DEEP_ANALYSIS_AND_LEGACY_DIAGNOSTIC_REPLACEMENT.md) so **Nightwatch** proactively scans memory for improvement opportunities.

---

## 2. Architecture & Navigation Flow

```mermaid
flowchart LR
    Nav["NavMenu / Ideation Header /\nToolCall Badges / Settings"] -->|Navigate to /w/{id}/memory\n?scope=...&path=...| MemPage["Memory.razor\n(Full-Page Memory Explorer)"]

    subgraph MemPageLayout ["Full-Page Memory Explorer"]
        Sidebar["Left File Tree\n• Tier 2: Local Workspace Memory\n• Tier 1: Committed Repo Memory"]
        Viewer["Main Pane\n• Rendered (HTML-safe) / Raw / Edit\n• MEMORY.md Budget Meter"]
        QuickActions["Quick Ideation Prompts\n• Summarize & Compact MEMORY.md\n• Deduplicate & Prune Local Memory\n• Promote Durable Rules to Repo\n• Review Selected File in Ideation"]
    end

    QuickActions -->|Navigate to /w/{id}/ideation\nwith curated memory prompt| Ideation["Ideation.razor\n(RestrictedReadOnly)"]
    Ideation -->|Direct write/edit| LocalMem["Tier 2: /workspace/memory/*.md"]
    Ideation -->|propose_tasks when repo\nmemory needs update| RepoTask["Suggested / Open ForgeTask\n(Update AGENTS.md or .agents/memory/*)"]
```

---

## 3. Detailed Requirements & Specifications

### 3.1 Clean-Break Replacement of `WorkspaceMemoryDrawer` with `Memory.razor`

Per the `AGENTS.md` Clean Break policy, remove the modal drawer and replace all entry points with full-page navigation:

1. **Dedicated Routes, Sidebar Navigation & URL Helpers:**
   - Create `src/NightCodeForge.Web/Components/Pages/Memory.razor` routed at `@page "/memory"` and `@page "/w/{WorkspaceId:int}/memory"`.
   - Support optional query parameters (`scope` and `path`, using named constants in `MemoryPresentationHelper` rather than magic strings) so deep links from tool-call badges, toast/desktop notifications, or Settings open the exact scope (`LocalWorkspace` or `SharedRepository`) and file directly.
   - Add a **Memory** (`🧠 Agent Memory`) navigation link in `NavMenu.razor` using `WorkspaceState.GetNavHref("memory")` (optionally displaying the unread `MemoryUpdateTracker` count alongside the link, while keeping the Ideation header badge active).
   - Update `WorkspaceUrlHelper`:
     - Map `"memory"` in `GetSectionDisplayName` to `"Agent Memory"`.
     - Strip file-specific memory query parameters (`scope`, `path`) in `GetTargetUrlForWorkspaceSwitch` when switching workspaces while on `/memory`, so switching to another workspace lands cleanly on `/w/{newWorkspaceId}/memory` instead of attempting to load the previous workspace's selected file.
   - When no workspace is active (`WorkspaceState.ActiveWorkspace == null`), render `<WorkspaceEmptyStateHero />` on `Memory.razor` and hide/disable workspace-scoped memory controls.
   - Entering `Memory.razor` for an active workspace clears its unread update counter via `MemoryUpdateTracker.Reset(workspaceId)`, regardless of which entry point navigated there.
2. **Both Memory Tiers Simultaneously Visible (Workspace & Repository):**
   - Display **both** memory tiers simultaneously in stacked sections of the left navigation pane (not hidden behind mutually exclusive tabs):
     - **Tier 2 — Local Workspace Memory (`~/.nightcodeforge/wks/{id}/memory/*.md`):** Full atomic CRUD (create new `.md` file, rendered HTML-disabled Markdown preview via `MarkdownHelper`, raw source view, inline editor with atomic save, and confirmed delete), plus the live line/byte budget meter for `MEMORY.md` against `AgentMemoryConstants.MaxPinnedMemoryLines` and `AgentMemoryConstants.MaxPinnedMemoryBytes` (highlighting both **near-budget** at $\ge 80\%$ via a named constant and **over-budget**).
     - **Tier 1 — Committed Repository Memory (`AGENTS.md`, `.agents/AGENTS.md`, and `.agents/memory/*.md`):** Read-only inspection of the repository working tree (`workspace.RepositoryPath`) with rendered/raw toggle, file metadata (line count, byte size, summary), and a clear note that repository memory is updated via Git or Stage 2 tasks.
   - Keep live auto-refresh via `MemoryUpdateTracker.OnMemoryUpdated` for both tiers of the active workspace: refresh the file list on updates, preserve in-progress edits if the currently edited file changes externally, and cleanly reset the selection without an error banner if the selected non-edited file is deleted externally.
   - Handle invalid or missing `scope`/`path` deep-link query parameters gracefully by displaying an inline warning/error banner while keeping the file tree loaded and usable.
3. **Remove Modal & Update All Entry Points (UI, Notifications & Tooltips):**
   - Remove `src/NightCodeForge.Web/Components/Memory/WorkspaceMemoryDrawer.razor` (and any modal-only CSS/test harness references; replace `WorkspaceMemoryDrawerTests.cs` with `Memory.razor` page tests).
   - Update all entry points to navigate to `/w/{WorkspaceId}/memory` (passing `scope` and `path` query parameters when targeting a specific file):
     - `Ideation.razor` (both the `#ideation-memory-btn` header button with its live update badge and tool-call `OnMemoryBadgeClick` replacing `OpenMemoryDrawerForTarget`).
     - `StageConversationViewer.razor` (tool-call `OnMemoryBadgeClick`).
     - `WorkspaceMemoryTab.razor` in Settings (button/link navigating to `/w/{WorkspaceId}/memory`).
     - `WebNotificationService` desktop push notifications (update `MemoryUiConstants.WorkspaceMemoryUrlSuffix` from `"ideation"` to `"memory"`, deep-linking to `/w/{workspaceId}/memory` with `scope` and `path` query parameters when available).
     - Update drawer-specific UI copy and tooltips (including `MemoryToolTarget.BadgeTooltip`) to reference the Agent Memory page rather than the removed drawer.

### 3.2 Quick Ideation Prompts for Memory Summarization & Cleanup

Provide a **Quick Ideation Actions** toolbar/panel on `Memory.razor` that lets the user hand off memory maintenance to the Ideation agent in one click:

1. **Curated Cleanup & Promotion Actions (Named Constants / Templates in `NightCodeForge.Web.Services`):**
   - **Summarize & Compact `MEMORY.md`:** Instructs Ideation to review `/workspace/memory/MEMORY.md`, condense verbose entries, move detailed topic notes into `/workspace/memory/<topic>.md`, and bring `MEMORY.md` comfortably below `MaxPinnedMemoryLines` / `MaxPinnedMemoryBytes`. *(Disabled when `MEMORY.md` does not exist; visually emphasized whenever `MEMORY.md` is near ($\ge 80\%$, named constant) or over budget.)*
   - **Deduplicate & Prune Workspace Memory:** Instructs Ideation to audit all `/workspace/memory/*.md` files against the current codebase and recent decisions, merge duplicates, remove obsolete notes, and update `/workspace/memory/*.md` directly. *(Enabled when at least one Tier 2 local memory file exists.)*
   - **Audit & Promote to Repository Memory:** Instructs Ideation to compare `/workspace/memory/*.md` with `AGENTS.md` and `.agents/memory/*.md`, identify durable team-wide conventions or architectural invariants that belong in Git, clean up the local copies if promoted, and **create a backlog task (`propose_tasks`)** to update `AGENTS.md` or `.agents/memory/<topic>.md` (since Ideation runs in `RestrictedReadOnly` and cannot write to `/workspace/clone`). *(Enabled when at least one Tier 2 local memory file exists.)*
   - **Discuss Selected File in Ideation:** Enabled whenever any Tier 1 or Tier 2 memory file is selected; pre-fills Ideation with a focused prompt referencing that exact file path (`/workspace/memory/<file>` for Tier 2, or `<relativePath>` for Tier 1) and asking Ideation to review/update workspace memory or propose a task if repository memory should change.
2. **Ideation Switch & Prompt Handoff Contract:**
   - Clicking any Quick Ideation Action navigates to `/w/{WorkspaceId}/ideation` with named query parameters (action kind plus optional `scope` and `path`) and hands off the generated prompt via a dedicated helper/session method (e.g., `IdeationMemoryPromptHelper` + `IdeationSessionState`, mirroring the `IdeationRefinementHelper` `taskId`/`draftId` handoff pattern without putting prompt-building logic in `.razor` `@code` blocks).
   - Every generated prompt must explicitly remind Ideation of the two-tier boundary:
     - Update `/workspace/memory/*.md` **directly** using `write`/`edit`.
     - **Never** attempt to edit `AGENTS.md`, `.agents/AGENTS.md`, or `.agents/memory/*` directly; instead, **propose a Stage 2 task** (`propose_tasks`) whenever repository memory needs to be updated.
   - Start a fresh Ideation conversation and **pre-fill** the composer with the generated prompt so the user can review or adjust it before sending (matching `taskId` / `draftId` handoff behavior).
   - **Concurrency & Re-Render Guardrails:**
     - Respect `Session.IsGenerating`: if an Ideation turn is currently active, do not clobber the running session or overwrite the composer; surface a warning toast/notice instead.
     - Deduplicate handled memory-action query parameters across `OnParametersSetAsync` re-renders (mirroring `TaskId` / `DraftId` deduplication).

### 3.3 Nightwatch Proactive Memory Scanning (Companion Scope — [Step 12.35B](../../features/STEP_12_35B_NIGHTWATCH_IDLE_DEEP_ANALYSIS_AND_LEGACY_DIAGNOSTIC_REPLACEMENT.md))

> [!NOTE]
> Nightwatch idle-GPU memory scanning is **owned by and specified in [Step 12.35B](../../features/STEP_12_35B_NIGHTWATCH_IDLE_DEEP_ANALYSIS_AND_LEGACY_DIAGNOSTIC_REPLACEMENT.md)** (deferred post-`v0.1.0`) and is **out of scope for Step 12.41 implementation** — no Nightwatch code changes are required to complete Step 12.41.

Complementing the user-initiated Quick Ideation Prompts in Step 12.41, autonomous **Nightwatch** memory auditing in [Step 12.35B](../../features/STEP_12_35B_NIGHTWATCH_IDLE_DEEP_ANALYSIS_AND_LEGACY_DIAGNOSTIC_REPLACEMENT.md) operates as follows:

- During idle-GPU deep analysis passes, Nightwatch already holds `RestrictedReadOnly` access to `/nightcodeforge/wks/{id}/memory/` and each workspace's repository (`AGENTS.md`, `.agents/memory/*.md`).
- Nightwatch scans both memory tiers alongside recent transcripts to detect:
  - `MEMORY.md` exceeding or approaching `MaxPinnedMemoryLines` / `MaxPinnedMemoryBytes`,
  - Contradictions, stale rules, or duplication across local and committed repository memory,
  - Recurring operational discoveries or transcript friction that should be codified in `AGENTS.md` / `.agents/memory/*.md` or pruned from `/workspace/memory/*.md`.
- Nightwatch routes concrete memory cleanup or repo-memory promotion proposals into `TaskOverallStatus.Suggested` `ForgeTask`s via `propose_workspace_task` (or `HarnessIssue` entries when NCF's own memory/prompt harness is at fault), ready for the user to approve or refine in Ideation.

### 3.4 UI & Presentation Separation ([AGENTS.md](../../../AGENTS.md))

- Keep `Memory.razor` and `Ideation.razor` strictly focused on layout, rendering, and event binding.
- Extract URL building, query-parameter parsing, memory action prompt generation, and scope/file grouping into dedicated testable services/helpers in `src/NightCodeForge.Web/Services/` (e.g., `MemoryPresentationHelper`, `WorkspaceMemoryUiService`, `IdeationMemoryPromptHelper`), adhering to $\le 10$ required constructor parameters, no optional constructor parameters, no bare `catch { }` blocks, and named constants/enums instead of magic strings.

---

## 4. Definition of Done (DoD) Checklist

- [x] Replace `WorkspaceMemoryDrawer.razor` with the full-page `Memory.razor` (`/memory`, `/w/{WorkspaceId:int}/memory`), add the `NavMenu.razor` link, render `<WorkspaceEmptyStateHero />` when no workspace is active, and update `WorkspaceUrlHelper` (`GetSectionDisplayName` + stripping memory query parameters on workspace switch).
- [x] Display both Tier 2 Local Workspace Memory (atomic CRUD + `MEMORY.md` near-budget and over-budget meter) and Tier 1 Committed Repository Memory (`AGENTS.md`, `.agents/AGENTS.md`, `.agents/memory/*.md`, read-only) simultaneously in the left navigation pane with HTML-disabled Markdown rendering, graceful deep-link error handling, and live `MemoryUpdateTracker` refresh and reset.
- [x] Update `Ideation.razor`, `StageConversationViewer.razor`, `WorkspaceMemoryTab.razor`, `WebNotificationService` push notification URLs (`MemoryUiConstants.WorkspaceMemoryUrlSuffix`), and memory badge tooltips to navigate to `/w/{WorkspaceId}/memory` (with deep-link `scope` and `path` query support), and remove `WorkspaceMemoryDrawer.razor`.
- [x] Add Quick Ideation Prompt actions on `Memory.razor` (with clear enablement and near/over-budget emphasis rules) that navigate to `/w/{WorkspaceId}/ideation`, start a fresh session (respecting `Session.IsGenerating` and deduplicating query parameters), and pre-fill curated prompts instructing Ideation to update workspace memory directly and propose a Stage 2 task if repository memory needs updating.
- [x] Cover `Memory.razor`, URL/query helpers, `IdeationMemoryPromptHelper`, deep-link navigation from Ideation/Inspector/Settings/Notifications, and `Ideation.razor` memory prompt handoff with unit and bUnit tests in `NightCodeForge.Web.Tests`, then run the full test suite per [AGENTS.md](../../../AGENTS.md).
