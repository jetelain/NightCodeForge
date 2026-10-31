# 📎 Step 12.2C: Attachments Web UI (Ideation Drop Zone, Preview Drawer & `/tasks/new` Picker)

> **Feature Implementation Step (Part 3 of 3 — Blazor Web UI Layer)**  
> *Parent Plan:* [Features & Integrations Roadmap](README.md)  
> *Prerequisite Steps:* [Step 12.2A: Attachments Store & Intake Service](STEP_12_2A_ATTACHMENTS_STORE_INTAKE_AND_MARKITDOWN.md) | [Step 12.2B: Agent Attachment Tools & Prompt Digest](STEP_12_2B_AGENT_ATTACHMENT_TOOLS_AND_PROMPT_DIGEST.md)  
> *Related Steps:* [Step 11.2: Task Creation Page](../archive/phase-11/STEP_11_2_TASK_CREATION_PAGE_AND_MERGE_MODE.md)  
> *Target Components:* `src/NightCodeForge.Web/Components/Ideation`, `src/NightCodeForge.Web/Components/Pages`, `tests/NightCodeForge.Web.Tests` | *Status:* Planned | *Target Framework:* .NET 10 (C# 13)  
> *Aligns With:* [AGENTS.md](../../AGENTS.md), [docs/PLAN.md](../PLAN.md)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Objective

With the Attachments store, intake service ([Step 12.2A](STEP_12_2A_ATTACHMENTS_STORE_INTAKE_AND_MARKITDOWN.md)), and agent tools ([Step 12.2B](STEP_12_2B_AGENT_ATTACHMENT_TOOLS_AND_PROMPT_DIGEST.md)) in place, this step (Part 3 of Step 12.2) delivers the Web UI experience across **Ideation Chat**, **Proposal Cards**, **Attachment Preview Drawer**, and the **Task Creation Page (`/tasks/new`)**:
1. **Ideation Chat Composer & Chips:** File drop zone, "Attach Host File" path input, and conversation attachment chips showing intake status (`stored`, `symlinked`, `converting`, `failed`).
2. **Workspace Attachments Panel & Preview Drawer:** Browse all workspace attachments, preview rendered companion markdown, inspect metadata/symlink target reachability, and view linked tasks.
3. **Proposal Cards & `/tasks/new` Attachments Picker:** Display linked attachment chips on proposal cards and allow attaching existing workspace attachments, dropping new files, or mapping host file symlinks directly when creating a task on `/tasks/new`.
4. **Safe User-Only Deletion:** Delete attachments from the UI with a confirmation modal listing linked tasks, unlinking the WSL2 symlink and companion `.md` while strictly preserving any underlying host file.

---

## 2. Web UI Specifications

### 2.1 Ideation Chat ([`Ideation.razor`](../../src/NightCodeForge.Web/Components/Pages/Ideation.razor))
- **Drop zone / attach button** on the chat composer (file drop → `IAttachmentIntakeService`) with an **"Attach Host File"** input action allowing users to type or paste a system host filename.
- After a user message, cited host paths and captured URLs surface as **attachment chips** in the conversation with status (`stored` / `symlinked` / `converting` / `failed`) so the user sees what was ingested.
- Host path attachments display a distinct symlink badge (e.g. `🔗 C:\dev\poc\router-spikes.md`).
- A collapsible **"Workspace Attachments"** panel lists all attachments (`title`, `kind`, `symlink status`, `source`, `date`) with a "link to this conversation" affordance.

### 2.2 Proposal Cards & Task Creation ([`IdeationProposalCard.razor`](../../src/NightCodeForge.Web/Components/Ideation/IdeationProposalCard.razor) & [`CreateTask.razor`](../../src/NightCodeForge.Web/Components/Pages/CreateTask.razor))
- Proposed task cards display their linked attachments as chips (clickable → opens the Attachment Preview Drawer).
- `/tasks/new` (`CreateTask.razor`) gains an **Attachments picker**: select existing workspace attachments to link to the new task, drop new files, or specify a system host filename to map as a symlink directly at creation time.

### 2.3 Attachment Preview Drawer
- A detail drawer renders the markdown companion (rendered HTML), the source metadata (`kind`, `symlink indicator`, `target host path`, `SHA-256`, `size`, `capturedAt`), and the list of tasks currently linking the attachment.
- For symlinks, the drawer indicates whether the target host file is currently reachable or missing.

### 2.4 Attachment Management & Deletion
- Delete action (user-only) displays a confirmation dialog showing which tasks would lose the link.
- **Safety guarantee:** Deleting a symlink attachment removes the symlink entry in `attachments/` and its companion markdown via `IAttachmentIntakeService`, and **never deletes or modifies the external host file**.
- Re-download/re-convert is **not** supported: content is immutable by hash; to change an attachment, add a new one.

---

## 3. Implementation Checklist & Deliverables

- [ ] **Ideation Chat UI:**
  - [ ] Add file drop zone + "Attach Host File" input + attachment status/symlink chips to [`Ideation.razor`](../../src/NightCodeForge.Web/Components/Pages/Ideation.razor).
  - [ ] Build collapsible "Workspace Attachments" panel in `Ideation.razor`.
- [ ] **Preview Drawer & Management:**
  - [ ] Build `AttachmentPreviewDrawer.razor` displaying rendered markdown companion, provenance metadata, symlink target health indicator, and linked tasks.
  - [ ] Add user-only delete action with link-impact confirmation dialog.
- [ ] **Proposal Cards & `/tasks/new` Picker:**
  - [ ] Render clickable attachment chips on [`IdeationProposalCard.razor`](../../src/NightCodeForge.Web/Components/Ideation/IdeationProposalCard.razor).
  - [ ] Add Attachments picker component to [`CreateTask.razor`](../../src/NightCodeForge.Web/Components/Pages/CreateTask.razor) (`/tasks/new`) supporting existing workspace attachments, file drop, and host path symlink mapping.
- [ ] **Tests:**
  - [ ] bUnit tests in `NightCodeForge.Web.Tests` covering the drop zone, host file input, attachment chips, `/tasks/new` picker, preview drawer, and delete confirmation flow.

