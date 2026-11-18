# 📎 Step 12.2C: Attachments Web UI (Ideation Drop Zone, Preview Drawer & `/tasks/new` Picker)

> **Feature Implementation Step (Part 3 of 4 — Blazor Web UI Layer)**  
> *Parent Plan:* [Features & Integrations Roadmap](../../features/README.md)  
> *Prerequisite Steps:* [Step 12.2A: Attachments Store & Intake Service](STEP_12_2A_ATTACHMENTS_STORE_INTAKE_AND_MARKITDOWN.md) | [Step 12.2B: `/workspace/attachments` Read/Bash Access & Prompt Digest](STEP_12_2B_AGENT_ATTACHMENT_TOOLS_AND_PROMPT_DIGEST.md)  
> *Companion Step:* [Step 12.2D: Multimodal Vision & In-Loop Image Inspection](../../features/STEP_12_2D_MULTIMODAL_VISION_AND_IN_LOOP_IMAGE_INSPECTION.md)  
> *Related Steps:* [Step 11.2: Task Creation Page](../phase-11/STEP_11_2_TASK_CREATION_PAGE_AND_MERGE_MODE.md)  
> *Target Components:* `src/NightCodeForge.Web/Services`, `src/NightCodeForge.Web/Components/Ideation`, `src/NightCodeForge.Web/Components/Backlog`, `src/NightCodeForge.Web/Components/Pages`, `tests/NightCodeForge.Web.Tests` | *Status:* Completed | *Target Framework:* .NET 10 (C# 13)  
> *Aligns With:* [AGENTS.md](../../../AGENTS.md), [docs/PLAN.md](../../PLAN.md)
>
> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Objective

With the Attachments store, intake service ([Step 12.2A](STEP_12_2A_ATTACHMENTS_STORE_INTAKE_AND_MARKITDOWN.md)), and canonical `read`/`bash` attachment access ([Step 12.2B](STEP_12_2B_AGENT_ATTACHMENT_TOOLS_AND_PROMPT_DIGEST.md)) in place, this step (Part 3 of Step 12.2) delivers the Web UI experience across **Ideation Chat**, **Proposal Cards**, **Attachment Preview Drawer**, **Task Detail Views**, and the **Task Creation Page (`/tasks/new`)**:
1. **UI & Presentation Separation:** Encapsulate all attachment intake coordination, path/URL extraction, status badge formatting, filtering, and delete-impact resolution in dedicated `NightCodeForge.Web.Services` helpers/services so Razor components stay thin and testable.
2. **Ideation Chat Composer, Chips & Workspace Panel:** File drop zone (`<InputFile>`), **"Attach Host File"** path input, automatic intake of cited host paths/URLs, conversation attachment chips showing intake status (`stored`, `symlinked`, `converting`, `failed`), and a collapsible **"Workspace Attachments"** panel with **Cite in Chat** (`@attachment:{id}`).
3. **Proposal Cards, `/tasks/new` Picker & Task Detail Chips:** Render clickable linked attachment chips on [`IdeationProposalCard.razor`](../../../src/NightCodeForge.Web/Components/Ideation/IdeationProposalCard.razor), [`TaskDetail.razor`](../../../src/NightCodeForge.Web/Components/Pages/TaskDetail.razor), and [`TaskDetailDrawer.razor`](../../../src/NightCodeForge.Web/Components/Backlog/TaskDetailDrawer.razor), and add an Attachments picker to [`CreateTask.razor`](../../../src/NightCodeForge.Web/Components/Pages/CreateTask.razor) (`/tasks/new`).
4. **Attachment Preview Drawer & Safe User-Only Deletion:** Preview rendered companion markdown HTML, provenance metadata, symlink reachability, and linked tasks, and allow user-only deletion via a link-impact confirmation modal that strictly preserves any underlying host file.

---

## 2. Web UI & Presentation Specifications

### 2.1 UI & Presentation Separation Architecture (Mandatory per [AGENTS.md](../../../AGENTS.md))
Per [AGENTS.md](../../../AGENTS.md), Razor components (`.razor`) must remain focused on presentation, layout, and user interaction. All parsing, payload extraction, data mapping, and intake/delete coordination must live in dedicated, unit-testable classes in `NightCodeForge.Web.Services` (e.g., `AttachmentPresentationHelper` and `IAttachmentUiService`, alongside extensions to [`ITaskCreationUiService`](../../../src/NightCodeForge.Web/Services/TaskCreationUiService.cs), [`IdeationPresentationHelper`](../../../src/NightCodeForge.Web/Services/IdeationPresentationHelper.cs), and [`IdeationSessionState`](../../../src/NightCodeForge.Web/Services/IdeationSessionState.cs)):
- **Message Citation Extraction:** Extract cited host file paths and `http(s)` URLs from user chat messages before sending.
- **Formatting & View-State Helpers:** Format intake status badges (`stored`, `symlinked`, `converting`, `failed`), symlink badges (`🔗 <path>`), target reachability states (`Reachable` vs `Target Missing`), human-readable byte sizes, and `@attachment:{id} {Title}` citation snippets.
- **Filtering & Search:** Filter workspace attachments by title, source ref, or `#id` in the workspace panel and `/tasks/new` picker.
- **Intake & Delete Coordination:** Coordinate file upload streams, host path symlink mapping, URL capture, preview loading, and delete-impact resolution (fetching linked tasks for the confirmation dialog), surfacing domain errors clearly without bare `catch { }` blocks.
- **Constructor Ceiling Constraint ([AGENTS.md](../../../AGENTS.md)):** All Web UI services must have no optional constructor parameters and never exceed **10 constructor parameters** ([`TaskCreationUiService`](../../../src/NightCodeForge.Web/Services/TaskCreationUiService.cs) currently has 9 parameters).

### 2.2 Ideation Chat ([`Ideation.razor`](../../../src/NightCodeForge.Web/Components/Pages/Ideation.razor))
- **Composer Attachment Controls:** File drop zone / attach button (`<InputFile>`) feeding uploaded bytes into `IAttachmentIntakeService` (via `IAttachmentUiService`), plus an **"Attach Host File"** path input action allowing users to type or paste a system host filename to map as a WSL2 POSIX symlink.
- **Automatic Message Citation Intake & Chips:** Before/when sending a user message, cited host paths and captured URLs are ingested and surface as **attachment chips** in the conversation with live intake status (`stored` / `symlinked` / `converting` / `failed`), clickable to open `AttachmentPreviewDrawer.razor`.
- **Symlink Visual Distinction:** Host path attachments display a distinct symlink badge (e.g. `🔗 C:\dev\poc\router-spikes.md`).
- **Workspace Attachments Panel:** A collapsible **"Workspace Attachments"** panel lists all workspace attachments (`#id`, `title`, `kind`, `symlink badge`, `source`, `size`, `createdAt`) with actions to **Cite in Chat** (inserts `@attachment:{id}` into the composer input), **Preview** (opens `AttachmentPreviewDrawer.razor`), and **Delete**.

### 2.3 Proposal Cards, `/tasks/new` Picker & Task Detail Views
- **Proposal Cards ([`IdeationProposalCard.razor`](../../../src/NightCodeForge.Web/Components/Ideation/IdeationProposalCard.razor) & [`IdeationSessionState.cs`](../../../src/NightCodeForge.Web/Services/IdeationSessionState.cs)):**
  - Extend `IdeationProposalViewModel` to track linked attachment IDs (`SelectedAttachmentIds`, populated from `IdeationProposal.AttachmentIds` and `@attachment:{id}` citations in `Proposal.Description`).
  - Render linked attachments as clickable chips on `IdeationProposalCard.razor` (opening `AttachmentPreviewDrawer.razor`).
  - Persist `TaskAttachmentLinks` when promoting or creating tasks in `IdeationPresentationHelper.PromoteOrCreateProposalTaskAsync`.
- **Task Creation Page ([`CreateTask.razor`](../../../src/NightCodeForge.Web/Components/Pages/CreateTask.razor) & [`TaskCreationUiService`](../../../src/NightCodeForge.Web/Services/TaskCreationUiService.cs)):**
  - Extend `TaskCreationRequest` with `IReadOnlyList<int>? AttachmentIds = null`.
  - Add an **Attachments Picker** section to `/tasks/new` (`CreateTask.razor`) enabling users to select existing workspace attachments, drop/upload new files, or specify a system host filename to map as a WSL2 symlink directly at creation time.
  - Persist `TaskAttachmentLinks` (unioned with any `@attachment:{id}` citations parsed from `Description`) inside `TaskCreationUiService.CreateTaskAsync`.
- **Task Detail Views ([`TaskDetail.razor`](../../../src/NightCodeForge.Web/Components/Pages/TaskDetail.razor) & [`TaskDetailDrawer.razor`](../../../src/NightCodeForge.Web/Components/Backlog/TaskDetailDrawer.razor)):**
  - Display the task's linked attachments as clickable chips opening `AttachmentPreviewDrawer.razor`.

### 2.4 Attachment Preview Drawer (`AttachmentPreviewDrawer.razor`)
- For text/rich-document attachments, renders the markdown companion as HTML; for image attachments (`ContentKind == TaskAttachmentContentKind.Image`), renders an inline image preview alongside dimensions (`WidthPx x HeightPx`) and `MediaType` badge ([Step 12.2D](../../features/STEP_12_2D_MULTIMODAL_VISION_AND_IN_LOOP_IMAGE_INSPECTION.md)).
- Displays provenance metadata (`#Id`, `Title`, `ContentKind`, `MediaType`, `SourceKind`, `IsSymlink`, `SourceRef`, `Sha256`, formatted `SizeBytes`, `CreatedAt`), and the list of tasks currently linking the attachment.
- For symlinks (`IsSymlink == true`), indicates live host target reachability (`Reachable` vs `Target Missing`).

### 2.5 Attachment Management & Safe User-Only Deletion
- User-only **Delete** action displays a confirmation dialog showing every linked task that would lose the attachment link.
- **Safety guarantee:** Confirming deletion invokes `IAttachmentIntakeService` to remove the symlink entry and companion `.md` inside `attachments/` (plus DB and `manifest.json` records) and **strictly never deletes or modifies the external host file**.
- Re-download/re-convert is **not** supported: content is immutable by hash; to change an attachment, add a new one.

---

## 3. Implementation Checklist & Deliverables

- [x] **Web UI Presentation Helpers & Services (`NightCodeForge.Web/Services`):**
  - [x] Implement `AttachmentPresentationHelper` and `IAttachmentUiService` (keeping all constructors $\le 10$ parameters and free of optional parameters or bare `catch { }` blocks) for citation extraction, status/symlink/reachability formatting, filtering, intake coordination, and delete-impact resolution.
  - [x] Extend [`IdeationSessionState.cs`](../../../src/NightCodeForge.Web/Services/IdeationSessionState.cs) (`IdeationProposalViewModel.SelectedAttachmentIds`), [`IdeationPresentationHelper.cs`](../../../src/NightCodeForge.Web/Services/IdeationPresentationHelper.cs) (`PromoteOrCreateProposalTaskAsync`), and [`TaskCreationUiService.cs`](../../../src/NightCodeForge.Web/Services/TaskCreationUiService.cs) (`TaskCreationRequest.AttachmentIds`) to carry and persist `TaskAttachmentLinks`.
- [x] **Ideation Chat UI ([`Ideation.razor`](../../../src/NightCodeForge.Web/Components/Pages/Ideation.razor)):**
  - [x] Add file drop zone / `<InputFile>` upload, **"Attach Host File"** path input, automatic cited path/URL intake, and clickable status/symlink attachment chips.
  - [x] Build the collapsible **"Workspace Attachments"** panel with **Cite in Chat** (`@attachment:{id}`), **Preview**, and **Delete** actions.
- [x] **Preview Drawer & Management:**
  - [x] Build `AttachmentPreviewDrawer.razor` displaying rendered markdown companion HTML or inline image preview (`WidthPx x HeightPx`, `MediaType`), provenance metadata, live symlink target reachability (`Reachable` vs `Target Missing`), and linked tasks.
  - [x] Add user-only delete action with link-impact confirmation dialog preserving host files.
- [x] **Proposal Cards, `/tasks/new` Picker & Task Detail Views:**
  - [x] Render clickable attachment chips on [`IdeationProposalCard.razor`](../../../src/NightCodeForge.Web/Components/Ideation/IdeationProposalCard.razor), [`TaskDetail.razor`](../../../src/NightCodeForge.Web/Components/Pages/TaskDetail.razor), and [`TaskDetailDrawer.razor`](../../../src/NightCodeForge.Web/Components/Backlog/TaskDetailDrawer.razor).
  - [x] Add the Attachments picker (existing selection, file drop, and host path symlink mapping) to [`CreateTask.razor`](../../../src/NightCodeForge.Web/Components/Pages/CreateTask.razor) (`/tasks/new`).
- [x] **Tests:**
  - [x] Unit tests in `NightCodeForge.Web.Tests` for `AttachmentPresentationHelper`, `IAttachmentUiService`, `IdeationPresentationHelper`, and `TaskCreationUiService` (citation extraction, status/size/symlink formatting, filtering, link persistence on task creation/promotion).
  - [x] bUnit tests in `NightCodeForge.Web.Tests` covering the Ideation drop zone, "Attach Host File" input, attachment chips, Workspace Attachments panel, `/tasks/new` picker, `IdeationProposalCard`, `TaskDetail`/`TaskDetailDrawer` chips, `AttachmentPreviewDrawer`, and delete confirmation flow, followed by full `dotnet test` execution (including Acceptance & E2E suites) per [AGENTS.md](../../../AGENTS.md).
