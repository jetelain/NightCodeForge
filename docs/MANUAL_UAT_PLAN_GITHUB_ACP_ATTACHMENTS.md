# 🧪 Manual User Acceptance Test (UAT) Plan: Task Attachments, Task Difficulty + Antigravity ACP & GitHub Integrations

> **Scope:** Manual end-to-end verification of recently implemented Phase 12 features:
> - **Part 1 — Task Attachments** ([Step 12.2A](archive/phase-12/STEP_12_2A_ATTACHMENTS_STORE_INTAKE_AND_MARKITDOWN.md), [Step 12.2B](archive/phase-12/STEP_12_2B_AGENT_ATTACHMENT_TOOLS_AND_PROMPT_DIGEST.md), [Step 12.2C](archive/phase-12/STEP_12_2C_ATTACHMENTS_WEB_UI_AND_TASK_PICKER.md))
> - **Part 2 — Task Difficulty & Antigravity ACP Integration** ([Step 12.5A](archive/phase-12/STEP_12_5A_ACP_HARNESS_CATALOG_ANTIGRAVITY_AND_CONTAINER_REWORK.md), [Step 12.5B](archive/phase-12/STEP_12_5B_TASK_DIFFICULTY_AND_ROLE_ESCALATION_MATRIX.md))
> - **Part 3 — GitHub Integrations** ([Step 12.11A](archive/phase-12/STEP_12_11A_GITHUB_CLIENT_DEVICE_AUTH_AND_SETTINGS.md), [Step 12.11B](archive/phase-12/STEP_12_11B_GITHUB_PULL_REQUEST_DELIVERY.md), [Step 12.11C](archive/phase-12/STEP_12_11C_GITHUB_UNTRUSTED_ISSUE_INTAKE_AND_TRIAGE.md), [Step 12.11D](archive/phase-12/STEP_12_11D_GITHUB_TASK_MIRRORING_AND_MULTI_USER_CLAIMING.md))

---

## 📋 Test Session Sign-Off Summary

| Area | Total Scenarios | Passed | Failed | Notes / Blocking Issues |
| :--- | :---: | :---: | :---: | :--- |
| **Part 1: Task Attachments (12.2A/B/C)** | 10 | | | |
| **Part 2: Task Difficulty & Antigravity ACP (12.5A/B)** | 6 | | | |
| **Part 3: GitHub Integrations (12.11A/B/C/D)** | 8 | | | |

---

## 🛠️ Prerequisites & Environment Setup

Before running the tests, verify the test environment is prepared:

- [ ] **NightCodeForge Web UI is running** and accessible (e.g., `http://localhost:5000` or your configured Kestrel URL), with `SecurityOptions.AirGappedMode` set to `false` (unless explicitly testing Air-Gapped failure paths).
- [ ] **Active Workspace configured:** An existing Git workspace is onboarded and selected in the top workspace switcher (note its numeric `WorkspaceId`, e.g., `1`, and ensure `ExecutionMode` is `ContainerSandbox` or `WslDirect` so WSL2/Docker behaviors can be observed).
- [ ] **WSL2 shell access available** to inspect `~/.nightcodeforge/wks/{workspaceId}/attachments/` and running containers (`docker ps` / `docker inspect`).
- [ ] **Sample test files prepared on Windows Host** (e.g., in `C:\temp\ncf-uat\`):
  - A plain-text/Markdown file: `C:\temp\ncf-uat\ncf-uat-spec.md`
  - A rich document: a `.pdf`, `.docx`, or `.html` file (and optionally an `.html` file containing hidden text / `display:none` element to verify filtering)
  - A valid image: `.png` or `.jpg`
  - An unsupported binary file (e.g., `.zip` or `.bin`) and a fake `.png` containing plain text `"not an image"`
- [ ] **Antigravity CLI (`agy`) available in WSL2** (`~/.local/bin/agy`) or on Host OS (`agy.exe` / `agy`), with an active Google AI Pro/Ultra or Gemini subscription for live ACP execution tests.
- [ ] **Disposable GitHub test repository** prepared with a clean default branch (`main`), at least one existing GitHub account (`User A`), and optionally a second GitHub account (`User B`) or manual GitHub UI access to simulate teammate claim conflicts and PR merges.

---

## 📎 Part 1: Task Attachments (Steps 12.2A, 12.2B, 12.2C)

### UAT-12.2-01 — File Drop Upload & Plain-Text Passthrough (Ideation Chat)
**Goal:** Verify file upload intake, `PlainText` passthrough, composer chips, and workspace storage layout.

1. Navigate to **`/ideation`** (or `/w/{WorkspaceId}/ideation`).
2. Click **`📎 Drop / Upload File`** (`#ideation-attachment-file-input`) and select a `.md`, `.txt`, or `.cs` file.
3. **Expected Results:**
   - [ ] A pending chip appears in the composer bar above the chat textarea, resolving to **`#<id>`**, the original filename/title, and a teal **`stored`** status badge (`✕` remove button is present on pending chips).
   - [ ] Switch the right side pane to the **`📎 Attachments`** tab (`#ideation-side-tab-attachments`). The new entry appears with `#<id>`, 📄 icon, **`Plain Text`** badge, human-readable size, and timestamp.
   - [ ] In WSL2, inspect `~/.nightcodeforge/wks/{workspaceId}/attachments/`:
     ```bash
     ls -la ~/.nightcodeforge/wks/{workspaceId}/attachments/
     cat ~/.nightcodeforge/wks/{workspaceId}/attachments/manifest.json
     ```
     Confirm `{seq:000}-{slug}.{ext}` exists as a regular file (not inside `clone/` or `task-*/`), `manifest.json` contains the matching entry, and `MarkdownPath` matches `RelativePath`.

---

### UAT-12.2-02 — Rich Document Conversion via `Pmad.Markitdown` (PDF / DOCX / HTML)
**Goal:** Verify convertible rich documents generate a companion `.md` file and render in the Preview Drawer.

1. In **`/ideation`**, click **`📎 Drop / Upload File`** and upload a `.pdf`, `.docx`, or `.html` document.
2. Open the **`📎 Attachments`** tab (`#ideation-side-tab-attachments`), locate the newly added entry, and click **`👁️ Preview`**.
3. **Expected Results:**
   - [ ] The workspace attachments row shows a 📑 icon and **`Rich Document`** badge.
   - [ ] In WSL2 (`ls -la ~/.nightcodeforge/wks/{workspaceId}/attachments/`), **both** the original file (`{seq:000}-{slug}.pdf` / `.docx` / `.html`) **and** the generated companion (`{seq:000}-{slug}.md`) exist.
   - [ ] Inside **`AttachmentPreviewDrawer`**, the **Provenance & Metadata** grid shows `Content Kind: Rich Document`, `Storage Mode: Stored`, `SHA-256`, and `Sandbox Path: /workspace/attachments/{seq:000}-{slug}.md`.
   - [ ] The **Content Preview** section renders the converted Markdown as formatted HTML.

---

### UAT-12.2-03 — Image Attachment Passthrough, Dimensions & Fail-Fast Binary Rejection
**Goal:** Verify valid image intake/preview and fail-fast error handling on corrupt images or unsupported binaries.

1. In **`/ideation`**, upload a valid `.png` or `.jpg` image via **`📎 Drop / Upload File`**.
2. Click the attachment chip or **`👁️ Preview`** to open **`AttachmentPreviewDrawer`**.
3. Next, attempt to upload:
   - An unsupported binary (e.g., `.zip` or `.exe`).
   - A text file renamed to `corrupt.png`.
4. **Expected Results:**
   - [ ] For the valid image: row displays 🖼️ icon and **`Image`** badge.
   - [ ] In **`AttachmentPreviewDrawer`**, **Provenance & Metadata** displays `Dimensions: <WidthPx> x <HeightPx>` and `Sandbox Path: /workspace/attachments/{seq:000}-{slug}.png` (no `.md` companion generated).
   - [ ] **Content Preview** displays the inline image alongside dimension and MIME-type badges (`image/png`).
   - [ ] For the `.zip` and corrupt `.png`: upload fails fast with a warning/error toast (without leaving failed composer chips behind), and **no** partial file or DB/manifest entry is left in `~/.nightcodeforge/wks/{workspaceId}/attachments/`.

---

### UAT-12.2-04 — Host File Path Mapping (WSL2 POSIX Symlink, Zero NTFS Symlinks)
**Goal:** Verify attaching a Windows host file creates a POSIX symlink inside WSL2 without touching NTFS or copying host source directories.

1. Ensure `C:\temp\ncf-uat\ncf-uat-spec.md` exists on the Windows host.
2. In **`/ideation`**, paste `C:\temp\ncf-uat\ncf-uat-spec.md` into the **`Attach host file path...`** input (`#ideation-host-file-input`) and click **`🔗 Attach Host File`** (`#btn-attach-host-file`).
3. Click the resulting chip to open **`AttachmentPreviewDrawer`**.
4. Also try attaching a directory path, an unsupported `.exe`, a non-existent path (`C:\temp\ncf-uat\does-not-exist.md`), or a traversal path (`C:\temp\..\secret.txt`).
5. **Expected Results:**
   - [ ] The attachment chip displays **`#<id>`**, the filename, and **`symlinked`** status badge (with the host/WSL source path available via tooltip rather than an inline path badge).
   - [ ] In WSL2 (`ls -la ~/.nightcodeforge/wks/{workspaceId}/attachments/`), `{seq:000}-ncf-uat-spec.md` is a **POSIX symbolic link** pointing to `/mnt/c/temp/ncf-uat/ncf-uat-spec.md` (verify no symlink was created on Windows NTFS).
   - [ ] In **`AttachmentPreviewDrawer`**, `#attachment-reachability-badge` displays **`Reachable`**, `Storage Mode` is **`Symlink`**, and `Source Ref` shows the resolved host/WSL path.
   - [ ] Directories, unsupported extensions, non-existent paths, or traversal paths fail fast with an error toast (without leaving failed chips) and create no attachment record.

---

### UAT-12.2-05 — Explicit Citation Only (No Auto-Ingestion from Raw Chat Paths or URLs)
**Goal:** Verify raw host paths and HTTP(S) URLs typed in chat messages are never auto-attached, while explicit `@attachment:{id}` tokens resolve chips cleanly.

1. Create a fresh host file `C:\temp\ncf-uat\not-attached-in-chat.md`.
2. In the **`/ideation`** chat textarea, type:
   `Please note C:\temp\ncf-uat\not-attached-in-chat.md and https://example.com alongside @attachment:<existing-id>.`
   and click **`🚀 Send`**.
3. **Expected Results:**
   - [ ] Only the explicitly attached/cited `@attachment:<existing-id>` chip renders on the user message bubble.
   - [ ] Neither `not-attached-in-chat.md` nor `https://example.com` is ingested into the **`📎 Attachments`** tab or `~/.nightcodeforge/wks/{workspaceId}/attachments/`.

---

### UAT-12.2-06 — Content-Addressed SHA-256 Deduplication
**Goal:** Verify uploading or attaching identical bytes reuses the existing workspace attachment ID.

1. In **`/ideation`**, upload the exact same `.md` or `.pdf` file used in **UAT-12.2-01** or **UAT-12.2-02** a second time.
2. **Expected Results:**
   - [ ] No duplicate attachment ID is created in the **`📎 Attachments`** tab (total count stays unchanged).
   - [ ] The composer chip reuses the original `#<id>`.
   - [ ] In WSL2 `attachments/`, only one file entry exists for that SHA-256 hash.

---

### UAT-12.2-07 — `💬 Cite in Chat` (`@attachment:{id}`), Ideation Inspection & Proposal Card Chips
**Goal:** Verify progressive disclosure via `<workspace_attachments>`, canonical `read` tool usage by Ideation Agent, and linked chips on Proposal Cards.

1. In the **`📎 Attachments`** tab (or inside **`AttachmentPreviewDrawer`**), click **`💬 Cite in Chat`** on an attachment (e.g., `#1`).
2. Verify `@attachment:1 <Title>` is appended to the chat composer input (and on mobile viewports, switches back to the Chat pane).
3. Ask the Architect Agent:
   `Read @attachment:1 and propose 1 backlog task summarizing the implementation requirements.`
4. Inspect the agent's streamed tool calls and the generated card in **Discovered Proposals**.
5. Click **`➕ Add to Backlog`** on the Proposal Card.
6. **Expected Results:**
   - [ ] The Ideation Agent reads the attachment using the canonical **`read`** tool targeting `/workspace/attachments/...` (no custom attachment-specific tool exists).
   - [ ] The resulting **`IdeationProposalCard`** displays a **`📎 Linked Attachments (N)`** section with clickable attachment chips (`#1`, title, tooltip showing source ref).
   - [ ] Clicking a chip on the Proposal Card opens **`AttachmentPreviewDrawer`**.
   - [ ] Clicking **`➕ Add to Backlog`** creates a `Suggested` task with the `TaskAttachmentLink` persisted.

---

### UAT-12.2-08 — `/tasks/new` Attachments Picker & Task Detail Views
**Goal:** Verify creating a task via `/tasks/new` with selected/uploaded/symlinked attachments and inspecting links in Backlog & Task Details.

1. Navigate to **`/tasks/new`** (`Create New Task`).
2. Scroll to **`📎 Context Attachments`** (`#create-task-attachments-section`).
3. Test all three actions in the picker:
   - Filter existing attachments via `#attachment-search-input` (search by title or `#id`) and check one attachment.
   - Upload a new file using **`📎 Drop / Upload File`** (`#create-task-attachment-file-input`).
   - Attach a host file using `#create-task-host-file-input` + **`🔗 Attach Host File`** (`#btn-create-task-attach-host-file`).
   - Also type `@attachment:<id>` directly in the **Description** textarea for another existing attachment.
4. Enter a Task Title, select **`📂 Save as Open (Backlog Queue)`**, and wait for navigation.
5. Open the created task in **Backlog Drawer** (`TaskDetailDrawer`) and **Task Detail Page** (`/tasks/{id}`).
6. **Expected Results:**
   - [ ] All checked, newly ingested, and `@attachment:<id>`-cited attachments are displayed as clickable chips on both `TaskDetailDrawer` and `TaskDetail` (`/tasks/{id}`).
   - [ ] Clicking any attachment chip opens **`AttachmentPreviewDrawer`**, and the **Linked Tasks (N)** section inside the drawer lists the newly created task with a clickable link to `/tasks/{id}`.

---

### UAT-12.2-09 — Stage 1–3 Agent Execution with Read-Only `/workspace/attachments` Mount
**Goal:** Verify task pipeline stages receive `<task_attachments>` in Layer 3, can `read`/`bash` `/workspace/attachments`, and cannot write to `/workspace/attachments`.

1. Ensure the task from **UAT-12.2-08** is **`Open`** and let the orchestrator run it through Stage 1 (`task-review`) and Stage 2 (`implementation`) (in `ContainerSandbox` or `WslDirect`).
2. *(Optional Docker check while Stage 2 container is running):* Run `docker inspect <containerName>` (or `mount` inside the container) to inspect mounts.
3. Open **`/inspector/{taskId}`** $\rightarrow$ **`💬 Stage Conversations`** and inspect the Stage 1 / Stage 2 user prompt and tool calls.
4. **Expected Results:**
   - [ ] In `ContainerSandbox`, `~/.nightcodeforge/wks/{workspaceId}/attachments` is bind-mounted at `/workspace/attachments` strictly read-only (`:ro`).
   - [ ] In the stage's User/Orchestrator bootstrap prompt, `<task_attachments>` is injected inside the task scope (Layer 3) with `{id, title, contentKind, mediaType, isSymlink, path: "/workspace/attachments/..."}`.
   - [ ] The agent successfully inspects attachments via canonical `read("/workspace/attachments/...")` or `bash` (`ls`/`cat`).
   - [ ] Any write/edit/touch attempt targeting `/workspace/attachments/...` fails with `UnauthorizedAccessException` / `Read-only file system`.

---

### UAT-12.2-10 — Broken Symlink Detection & Safe User-Only Deletion (Host File Preservation)
**Goal:** Verify broken symlinks surface `Target Missing` clearly and deleting an attachment leaves the original Windows host file untouched.

1. On Windows host, temporarily rename `C:\temp\ncf-uat\ncf-uat-spec.md` to `C:\temp\ncf-uat\ncf-uat-spec.MISSING.md`.
2. In **`/ideation`** (or **`/tasks/{id}`**), click the symlinked attachment to open **`AttachmentPreviewDrawer`**.
3. *(Optional Agent Check):* Ask Ideation Agent to `read` that attachment path and observe the tool error.
4. Rename `C:\temp\ncf-uat\ncf-uat-spec.MISSING.md` back to `C:\temp\ncf-uat\ncf-uat-spec.md`, reopen the drawer, and verify **`Reachable`** returns.
5. Click **`🗑️ Delete`** (`#btn-open-delete-attachment`) in the Preview Drawer (or on the Workspace Attachments row).
6. Review the confirmation dialog, then click **`Confirm Delete`** (`#btn-confirm-delete-attachment`).
7. **Expected Results:**
   - [ ] While the host file was renamed: `#attachment-reachability-badge` displays **`Target Missing`**, the **`⚠️ Target Missing`** warning banner appears inside the preview drawer, and an agent `read` fails fast with `AttachmentTargetNotFoundException` (no empty/placeholder output).
   - [ ] In the delete confirmation modal (`role="alertdialog"`), **Linked Tasks Impacted (N)** accurately lists every linked task, and the warning explicitly states the external host file will never be deleted or modified.
   - [ ] After confirming delete:
     - Toast confirms deletion (`Attachment Deleted`).
     - Attachment disappears from **`📎 Attachments`** (`#ideation-side-tab-attachments`), `manifest.json`, and `~/.nightcodeforge/wks/{workspaceId}/attachments/`.
     - Linked tasks no longer show the deleted chip.
     - **`C:\temp\ncf-uat\ncf-uat-spec.md` on Windows NTFS still exists and is completely unmodified.**

---

## 🧠 Part 2: Task Difficulty & Antigravity ACP Integration (Steps 12.5A, 12.5B)

### UAT-12.5-01 — Antigravity ACP Discovery, Status Badge & Diagnostics Modal
**Goal:** Verify `IAcpHarnessCatalogService` discovers `agy`, parses authentication/tier/models, and surfaces actionable diagnostics in Settings.

1. Navigate to **`/settings`** $\rightarrow$ **`🌐 Global System Settings`** $\rightarrow$ **`🤖 Global Agent Profiles`**.
2. Inspect the Provider Connection strip at the top of the tab (`#acp-status-badge`).
3. Click **`🔑 Login Hints / Test Connection`** (`#btn-acp-login-hints`) to open `#acp-login-modal`.
4. Click **`🧪 Test ACP Connection`** (`#btn-test-acp-connection`), then **`🔄 Refresh ACP Status & Models`** (`#btn-refresh-acp`).
5. *(Negative check, if testable by temporarily renaming `~/.gemini/antigravity-cli/antigravity-oauth-token`):* Click Refresh and verify unauthenticated state, then restore token and refresh again.
6. **Expected Results:**
   - [ ] When `agy` is installed and authenticated: `#acp-status-badge` displays **`🟢 Antigravity ACP v... Ready (<ExecutionTarget> | <AccountTier> | <N> model families)`**.
   - [ ] `#acp-login-modal` displays **Detected Binary** (e.g., `~/.local/bin/agy`), **Execution Target** (e.g., WSL2 + Container), **Server Version / Tier** (e.g., `Google AI Ultra`), and sign-in commands (`wsl -e ~/.local/bin/agy login` / `agy login`).
   - [ ] When unauthenticated: `#acp-status-badge` displays **`🟡 Antigravity ACP Installed — Authentication Required (Run 'wsl -e agy login')`**.
   - [ ] When `agy` is not installed: `#acp-status-badge` displays **`⚪ Antigravity ACP Not Detected`**.

---

### UAT-12.5-02 — Per-Stage Harness Selection & Pinned Stages (`Crucible` vs `Antigravity ACP`)
**Goal:** Verify `triage`/`ideation` remain pinned to `Crucible`, pipeline stages allow switching to `Antigravity ACP`, and `HarnessId` persists across reloads.

1. In **`/settings`** $\rightarrow$ **`🤖 Global Agent Profiles`**, scroll below the Difficulty Matrix to the 5 per-stage profile cards (`code-review-fix`, `ideation`, `implementation`, `task-review`, `triage`).
2. Check the `triage` and `ideation` cards.
3. On the `implementation` (or `task-review` / `code-review-fix`) card, change **Execution Harness** from `Crucible (Local / Open-Weight & API)` to **`Antigravity ACP (Google AI / Gemini CLI)`**.
4. Inspect the card controls, then change it back (or keep it if desired) and verify saving.
5. **Expected Results:**
   - [ ] `triage` and `ideation` cards show **`Harness: crucible (Pinned)`** and have **no** Execution Harness dropdown.
   - [ ] Switching a pipeline card to **`Antigravity ACP`**:
     - Header badge switches to **`🔌 External Antigravity ACP`**.
     - **Inference Model** dropdown populates with Antigravity models (`Gemini 4 Argon`, `Gemini 3.8 Flash`, `Claude Opus 5.5`, `Claude Sonnet 5.5`, `GPT-OSS 120B`) plus an **`Enter custom name`** toggle.
     - **Thinking Effort** dropdown shows ACP levels (`Low (thought_level: low)`, `Medium (thought_level: medium)`, `High (thought_level: high)`).
     - Crucible-only controls (**OllamaRouter Target**, **Local Optimizations Override/Revert**) are hidden.
   - [ ] Clicking **`💾 Save Profiles`** and reloading `/settings` preserves `HarnessId` and `ModelName`.

---

### UAT-12.5-03 — Role-Based Task Difficulty Escalation Matrix & Workspace Cloning
**Goal:** Verify configuring the `Standard` / `Difficult` / `Expert` $\times$ (`Architecture & Review` / `Coder / Implementation`) matrix and workspace profile cloning.

1. In **`/settings`** $\rightarrow$ **`🤖 Global Agent Profiles`**, locate **`⚡ Role-Based Task Difficulty Escalation Matrix`** (`#difficulty-escalation-matrix`).
2. Verify all 3 difficulty tiers (**`Standard`**, **`🔥 Difficult`**, **`⚡ Expert`**) appear, each with **Architecture & Review** (`task-review, code-review-fix`) and **Coder / Implementation** (`implementation`) cells.
3. Configure a clear escalation setup:
   - **`Standard`:** Keep `Crucible` with your baseline local models (e.g., `High` for Architecture & Review, `Medium` for Coder).
   - **`🔥 Difficult`:** Configure Architecture & Review on `Crucible` (e.g., Architect model + `High`), then click **`Same as Architecture & Review`** on the Coder cell.
   - **`⚡ Expert`:** Switch both **Architecture & Review** and **Coder / Implementation** cells to **`Antigravity ACP`**, select **`gemini-4-argon`**, and set Thinking Effort to **`High (thought_level: high)`**.
4. Click **`💾 Save Profiles`**, refresh the browser (`F5`), and verify the matrix state persisted.
5. Switch the top-right scope pill to **`🏢 Workspace Settings`** $\rightarrow$ **`🤖 Workspace Agent Profiles`**, click **`⚡ Customize Profiles for this Workspace`** (or `⚡ Create Dedicated Workspace Profiles`), reload, and verify the workspace profiles inherited the exact `DifficultyOverrides` and `HarnessId` values (then test **`↺ Revert to Global Profiles`** if you want to revert back to global inheritance).
6. **Expected Results:**
   - [ ] Clicking **`Same as Architecture & Review`** immediately copies Harness, Model, Thinking Effort, and Routing Target into the Coder cell and updates the button label to **`✓ Same as Architecture & Review`**.
   - [ ] `Expert` cells configured with `Antigravity ACP` hide `OllamaRouter Target` and `Custom Thinking Token Budget`.
   - [ ] Both global save and workspace profile cloning persist `DifficultyOverrides` cleanly across page reloads.

---

### UAT-12.5-04 — Authoring, Editing & History Tracking of Task Difficulty Across UI & Markdown
**Goal:** Verify `Difficulty` selection, badges, mutation persistence, revision history diffs, and `docs/tasks/*.md` frontmatter sync.

1. **Create Task (`/tasks/new`):**
   - Select **`🔥 Difficult`** in **Task Difficulty Tier**, enter a title/description, and click **`📂 Save as Open (Backlog Queue)`**.
   - Inspect the generated `docs/tasks/YYYY-MM-DD-*.md` file on disk.
2. **Ideation Proposals (`/ideation`):**
   - Generate or inspect a proposal card (`IdeationProposalCard`) and verify the **`Difficulty:`** dropdown (`Standard`, `🔥 Difficult`, `⚡ Expert`) is editable prior to `➕ Add to Backlog`.
3. **Backlog Kanban, Drawer & Task Detail (`/backlog`, `/tasks/{id}`):**
   - Locate the newly created task on the Backlog Kanban board and click it to open **`TaskDetailDrawer`**.
   - Click the **`⚡ Expert`** chip under **Difficulty Tier** inside `TaskDetailDrawer`.
   - Switch to the **`🕰️ Revisions & History`** tab inside `TaskDetailDrawer` (or open `/tasks/{id}` and click `🕰️ Revisions & History`).
4. **Expected Results:**
   - [ ] Creating the task writes `difficulty: difficult` in the task's markdown YAML frontmatter.
   - [ ] Kanban card, `TaskDetailDrawer` header, and `TaskDetail` header render the color-coded difficulty badge (`Standard` gray/neutral, `🔥 Difficult` amber, `⚡ Expert` purple).
   - [ ] Updating difficulty to `Expert` updates the badge immediately, updates `difficulty: expert` in `docs/tasks/*.md` frontmatter, and records a revision entry in **`🕰️ Revisions & History`** showing a purple **`Difficulty`** change badge (`Difficult` $\rightarrow$ `Expert`).

---

### UAT-12.5-05 — End-to-End Execution of an `Expert` Task via `Antigravity ACP` (`agy acp`)
**Goal:** Verify `Expert` tasks dispatch via `agy acp` inside `ContainerSandbox` (or `WslDirect`), skip GPU preloading, stream telemetry, and persist full conversations.

1. Ensure the **`⚡ Expert`** row in `#difficulty-escalation-matrix` maps both roles to **`Antigravity ACP`** / `gemini-4-argon` (or another valid Antigravity model) and `agy` is authenticated.
2. Create a small, self-contained coding task in **`/tasks/new`** (e.g., adding a simple helper method and unit test in your test repo), select **`⚡ Expert`**, and click **`🚀 Save & Launch Immediately`**.
3. While the task executes, observe **`/monitoring`** (or Live Operations panel) and **`/inspector/{taskId}`**.
4. Let the task run through `task-review` $\rightarrow$ `pre-implementation-baseline` $\rightarrow$ `implementation` $\rightarrow$ `post-implementation-gates` $\rightarrow$ `code-review-fix` $\rightarrow$ `post-review-verification`.
5. Open **`/inspector/{taskId}`** $\rightarrow$ **`💬 Stage Conversations`** and inspect each agentic stage (`task-review`, `implementation`, `code-review-fix`).
6. **Expected Results:**
   - [ ] During `PreImplementationBaseline`, no local GPU model preload is triggered for the upcoming `ACP` implementation stage (check logs: `Preload` skipped because harness is `acp`).
   - [ ] Active worker telemetry shows hardware target **`Antigravity ACP`** (rather than `Local GPU`).
   - [ ] Live tool activity strip (`ToolActivityStrip`) shows `"Crucible & ACP Tool Invocation Telemetry"` and correlates running/completed tool pills cleanly (e.g., `run_command`, `read_file`, `edit_file`) rather than displaying `"completed"` as a tool name.
   - [ ] In **`/inspector/{taskId}`**:
     - Header shows **`⚡ Expert`** badge.
     - Each agentic stage card shows compute badge **`Antigravity ACP`** and escalation badge **`⚡ Escalated (Expert: Antigravity ACP / gemini-4-argon)`**.
     - `StageConversationViewer` renders persisted turns (`User / Orchestrator`, `🧠 Reasoning Deliberation`, `Tool` + `ToolResult`, final assistant response) and recorded token usage (`inputTokens`, `outputTokens`, `cachedInputTokens`) **without** a synthetic `$0.00` cost badge.
   - [ ] In `ContainerSandbox`, all `agy` file edits and commands run inside `/workspace/task-{id}` (verify edits landed in the task branch worktree and deterministic build/tests pass).

---

### UAT-12.5-06 — Stage 1 (`task-review`) Dynamic Mid-Pipeline Difficulty Upgrade
**Goal:** Verify `task-review` upgrading `difficulty:` in markdown YAML frontmatter escalates Stages 2/3 dynamically.

1. Set **`Standard`** to use a fast local model (`Crucible`) and **`🔥 Difficult`** (or **`⚡ Expert`**) to use a distinct escalated model/harness in the Difficulty Matrix.
2. Create an `Open` task with Difficulty **`Standard`**, whose description includes a complex multi-file architectural change (or explicitly ask in the description for Stage 1 Task Review to evaluate technical complexity and set `difficulty: difficult` or `difficulty: expert` in the task markdown frontmatter if warranted).
3. Launch the task and wait for Stage 1 (`task-review`) to finish and Stage 3 (`implementation`) to begin.
4. *(If your local Stage 1 model did not decide to upgrade automatically, you can test the pipeline sync mechanism by manually editing `difficulty: difficult` in the worktree's `docs/tasks/*.md` before Stage 1 finishes or in a controlled test task.)*
5. **Expected Results:**
   - [ ] Once `task-review` updates `difficulty:` in `docs/tasks/*.md`, post-stage markdown sync updates `task.Difficulty` in SQLite and increments `task.Version`.
   - [ ] The `ExecutionLog` / inspector records an escalation message (`Task #{TaskId} [Difficult/Expert]: running stage 'implementation' with harness '...', model '...'`).
   - [ ] Stage 3 (`implementation`) and Stage 5 (`code-review-fix`) execute with the **upgraded** difficulty tier's harness and model instead of the initial `Standard` profile.

---

## 🐙 Part 3: GitHub Integrations (Steps 12.11A, 12.11B, 12.11C, 12.11D)

### UAT-12.11A-01 — GitHub App Device Flow Authentication & OS Keyring Storage
**Goal:** Verify GitHub App Device Flow login, polling, OS keyring persistence, and clean disconnect/reconnect.

1. Navigate to **`/settings`**, click **`🏢 Workspace Settings`** in the top-right scope pill, and select the **`🐙 GitHub Integration`** tab.
2. Verify initial header badge is **`⚪ Disconnected`**.
3. Click **`🔑 Connect GitHub Account`**.
4. Click **`📋 Copy Code`**, click **`🔗 Open GitHub Device Activation ↗`** (`https://github.com/login/device`), paste the 8-character code, and authorize the NightCodeForge GitHub App.
5. Return to NightCodeForge `/settings` without refreshing.
6. Verify OS credential storage (on Windows, open **Credential Manager** $\rightarrow$ **Windows Credentials** $\rightarrow$ Generic Credentials).
7. **Expected Results:**
   - [ ] Device Flow UI displays the monospace activation code, copy toast, verification link, and countdown spinner (`Waiting for GitHub authorization...`).
   - [ ] Within one polling interval after authorizing on GitHub, the tab updates automatically to **`🟢 Connected (@<yourLogin>)`** with your account name/login and **`⚙️ Manage App Connection ↗`** / **`Disconnect Account`** buttons.
   - [ ] Windows Credential Manager contains `NightCodeForge` / `GitHubAppOAuth` / `oauth-token` (or verify SQLite `Workspaces.GitHubSettings` contains **zero** `access_token` or `refresh_token` secrets).
   - [ ] *(Optional check)* Click **`Disconnect Account`**, verify it returns to `⚪ Disconnected` and removes the OS keyring entry, then reconnect for the remaining tests.

---

### UAT-12.11A-02 — Repository Binding, Access Probe & Workspace Settings Persistence
**Goal:** Verify GitHub App repository installation discovery, access probe, and workspace settings saving.

1. Ensure the NightCodeForge GitHub App is installed on your test GitHub repository (click **`⚙️ Manage App Connection ↗`** if you need to grant access to the test repo).
2. In **`🐙 GitHub Integration`**, click **`🔄 Refresh Repositories`**.
3. Select the **Repository Owner** (`#github-repo-owner`) and **Repository Name** (`#github-repo-name`). *(Also click `Enter manually` once to verify manual text input toggle works.)*
4. Click **`🔍 Test Repository Access`**.
5. Check **`Enable GitHub Integration for this Workspace`** (`#enableGitHub`), then click **`💾 Save GitHub Settings`**.
6. Reload `/settings` (`F5`) and re-open `🏢 Workspace Settings` $\rightarrow$ `🐙 GitHub Integration`.
7. **Expected Results:**
   - [ ] Repository dropdowns list accessible repositories where the GitHub App is installed.
   - [ ] Access probe displays green badge: **`✓ Access verified: <owner>/<repo> (Push: Yes, Admin: ...)`**.
   - [ ] Saved settings (`Enabled = true`, Owner, Repo) remain intact after browser reload.

---

### UAT-12.11B-01 — Outbound Pull Request Delivery (`MergeMode.ExternalPullRequest`)
**Goal:** Verify end-to-end PR delivery: user-namespaced remote branch, `docs/tasks/*.md` stripping, PR creation with gate metrics, and UI badges/buttons.

1. In **`🐙 GitHub Integration`** settings, enable **`🚀 Outbound Pull Request Delivery`** (`PullRequestDeliveryEnabled`), leave **`Auto-close Task when Pull Request merges`** (`#closeOnMerge`) checked (or uncheck temporarily if you want to test manual closure in UAT-12.11B-03 first), optionally test **`Open Pull Requests as Draft`** (`#createDraft`), and click **`💾 Save GitHub Settings`**.
2. Navigate to **`/tasks/new`** and inspect **Merge Lifecycle Mode**.
3. Select **`🐙 External Pull Request`** (`#mergemode-option-external-pr`), author a small valid code-change task, and click **`🚀 Save & Launch Immediately`**.
4. Wait for the task to complete all 6 stages (`TaskReview` $\rightarrow$ `PreImplementationBaseline` $\rightarrow$ `Implementation` $\rightarrow$ `PostImplementationGates` $\rightarrow$ `CodeReviewAndFix` $\rightarrow$ `PostReviewVerification`).
5. Inspect the task in **`/review/{taskId}`**, **`/tasks/{taskId}`**, **`TaskDetailDrawer`**, and on **GitHub**.
6. **Expected Results:**
   - [ ] On `/tasks/new` (and Kanban/Task Detail), `External Pull Request` (`🐙 PR` badge) is selectable only when GitHub PR delivery is enabled and authenticated.
   - [ ] Upon passing Stage 6, the task transitions to **`Ready`** (not auto-merged locally).
   - [ ] On GitHub, a branch named **`ncf/<sanitizedGithubLogin>/task-<taskId>`** was pushed, and a Pull Request against `BaseBranch` was opened (as Draft if `Open Pull Requests as Draft` was enabled).
   - [ ] **Critical check:** Inspect the PR's **Files changed** tab on GitHub — **`docs/tasks/*.md` local task tracking files are completely absent** from the PR diff.
   - [ ] The PR body contains `### 📋 Summary` and `### 🛡️ Quality Gate Verification Results` (Build Status, Test Suite counts, Coverage Delta, Linter / Warning Ratchet).
   - [ ] Check `.git/config` in the host repo and ext4 mirror + `ExecutionLog` in NightCodeForge: **zero `ghu_...` tokens or `Authorization:` headers appear anywhere**.
   - [ ] In `/review/{taskId}` and `/tasks/{taskId}`, **`🐙 Open GitHub Pull Request (#<PR>)`** (`#btn-open-github-pr`), **`🐙 Sync Pull Request`** (`#btn-create-or-sync-pr`), and **`✅ Close Merged PR`** (`#btn-close-merged-pr`) render.

---

### UAT-12.11B-02 — Request Changes Loopback & On-Demand PR Update (`--force-with-lease`)
**Goal:** Verify looping back from Review Room updates the existing PR branch and description without creating a duplicate PR or leaking `docs/tasks/*.md`.

1. In **`/review/{taskId}`** for the open PR task from **UAT-12.11B-01**, click **`📝 Request Changes`** (`#btn-request-changes`), enter a small change instruction (e.g., `"Add a brief XML doc comment to the new method"`), and submit.
2. Wait for `Implementation` $\rightarrow$ `PostReviewVerification` to complete and return the task to **`Ready`**.
3. **Expected Results:**
   - [ ] No second Pull Request is opened; `PullRequestNumber` remains the same.
   - [ ] The existing GitHub PR (`ncf/<login>/task-<id>`) reflects the new commit via `--force-with-lease` and updated gate metrics in the PR description.
   - [ ] `docs/tasks/*.md` remains excluded from the PR diff after the Stage 3 checkpoint re-run.
   - [ ] *(Optional resilience check)* Temporarily click **`Disconnect Account`**, click **`🐙 Sync Pull Request`** (`#btn-create-or-sync-pr`): verify an error toast appears without failing the task (`Status` stays `Ready`). Reconnect and click **`🐙 Sync Pull Request`** again to verify on-demand recovery succeeds.

---

### UAT-12.11B-03 — External PR Merge Closure & Downstream DAG Cascade Unblocking
**Goal:** Verify unmerged PR closure fails fast, whereas merging on GitHub closes the task (manually or via poller), fast-forwards `BaseBranch`, cleans up, and unblocks DAG dependents.

1. Create a downstream **`Open`** task (`Task B`) in **`/tasks/new`** with the PR task (`Task A`, currently `Ready`) selected under **Prerequisite Tasks**. Verify `Task B` remains blocked in `/backlog`.
2. In **`/review/{taskA}`** (while the PR is still **open/unmerged** on GitHub), click **`✅ Close Merged PR`** (`#btn-close-merged-pr`).
3. Next, merge the Pull Request on GitHub (via Squash & Merge or Merge Commit).
4. Either click **`✅ Close Merged PR`** in **`/review/{taskA}`** immediately, **or** wait for the background `CloseTaskOnPrMerge` job (when `#closeOnMerge` is checked).
5. **Expected Results:**
   - [ ] Step 2 (while PR is unmerged) fails fast with an error toast (`Cannot Close Task`), leaving `Task A` in `Ready` and `Task B` blocked.
   - [ ] Step 4 (after merging on GitHub):
     - `Task A` transitions to **`Closed`** with `SquashCommitSha` populated from GitHub's `MergeCommitSha`.
     - Local `BaseBranch` on host and ext4 mirror fast-forwards to include the merged GitHub PR commit.
     - Task A's worktree, local `feature/task-<id>` branch, and container are cleaned up, and its `docs/tasks/*.md` is archived.
     - **Downstream `Task B` automatically unblocks and starts executing** from the newly fast-forwarded `BaseBranch` containing Task A's merged code.

---

### UAT-12.11C-01 — Untrusted Issue Intake, Assignee Scoping & `RestrictedReadOnly` Triage
**Goal:** Verify GitHub Issues are polled safely, filtered by assignee scope, triaged in `RestrictedReadOnly`, and deduplicated across polls.

1. In **`🐙 GitHub Integration`** settings:
   - Check **`📥 Untrusted Issue Intake & Triage`** (`IssueTriageEnabled`).
   - Set **Triage Scope** to **`Assigned to Me or Unassigned (Default)`**.
   - Set **Polling Interval (Minutes)** to **`1`** (and optionally set **Issue Filter Label**, e.g., `triage`, or leave blank to scan all open issues).
   - Ensure **`Synchronize task status labels`** and **`Post stage milestone comments on issue`** are checked under Sub-Feature C (or leave unchecked to test local `TriagedIssueNumbers` watermark deduplication).
   - Click **`💾 Save GitHub Settings`**.
2. On GitHub, create:
   - **Issue #A (Actionable, unassigned or assigned to you):** Describe a concrete, small enhancement or bug in the repo.
   - **Issue #B (Assigned to another collaborator, if available):** Assign strictly to another GitHub user.
   - **Issue #C (Non-actionable / vague noise, e.g., `"Hello world test"`):** To verify 0-proposal deduplication.
3. Wait 1–2 minutes for the Hangfire polling cycle (`github-sync-workspace-{workspaceId}`) to run.
4. **Expected Results:**
   - [ ] Open Pull Requests in the repository are **ignored** (only true Issues are processed).
   - [ ] **Issue #B** (assigned to another user) is skipped under `AssignedToMeOrUnassigned`.
   - [ ] **Issue #A** is triaged strictly in `RestrictedReadOnly` (`--network none` / `ReadOnly`) and spawns one or more **`Suggested`** tasks in `/backlog` with `Origin = GitHubIssue`, `ExternalSource = "GitHub"`, and `ExternalReferenceId = "<issueA_number>"`.
   - [ ] On GitHub, **Issue #A** receives label **`ncf:triaged`** and an acknowledgment comment containing the hidden marker `<!-- ncf:triaged -->` (if label/comment toggles are enabled).
   - [ ] Wait through 2+ subsequent polling intervals: **neither Issue #A nor Issue #C (0 proposals) is re-triaged** in an infinite loop.

---

### UAT-12.11D-01 — Bi-Directional Lifecycle Mirroring (`ncf:*` Labels, Milestone Comments & 1:N Sibling Guard)
**Goal:** Verify full lifecycle label transitions, auto-assignment, milestone comments, optional local-task issue export, and 1:N sibling closure guard.

1. In **`🐙 GitHub Integration`** settings, enable **`🔄 Task Mirroring & Multi-User`** with:
   - **`Auto-assign issue to me on task start`** (`#autoAssign`): Checked
   - **`Synchronize task status labels`** (`#syncStatus`): Checked
   - **`Post stage milestone comments on issue`** (`#postMilestone`): Checked
   - Click **`💾 Save GitHub Settings`**.
2. *(1:N Sibling Guard preparation)* If **Issue #A** produced only 1 `Suggested` task, optionally create or keep a second task linked to the same Issue (or test single-task closure first, then verify 1:N if Triage proposed multiple tasks).
3. Promote the `Suggested` task from **Issue #A** to **`Open`** in `/backlog` and observe the GitHub Issue after each stage:
4. *(Optional Local Task Export check)* Check **`Create GitHub Issues for local tasks`** (`#mirrorLocalTasks`), save settings, create a brand-new local `Open` task in `/tasks/new`, and inspect GitHub.
5. **Expected Results:**
   - [ ] **On promotion to `Open`:** GitHub Issue label transitions from `ncf:triaged` $\rightarrow$ **`ncf:backlog`**.
   - [ ] **On Stage 1 (`task-review`) start/completion:** Issue is auto-assigned to your GitHub login (`assignees`), label transitions to **`ncf:in-progress`**, and a milestone comment summarizes the approved task specification.
   - [ ] **On Stage 4 (`post-implementation-gates`) completion:** Label transitions to **`ncf:gates-passed`**, and a milestone comment posts build, test, and coverage metrics.
   - [ ] **On reaching `Ready` (Stage 6 completion):** Label transitions to **`ncf:ready-for-review`**, and a milestone comment notes gates passed (with `PullRequestUrl` if PR delivery is enabled).
   - [ ] **On reaching `Closed`:**
     - If multiple sibling tasks in the workspace share `ExternalReferenceId = "<issueA_number>"` and at least one sibling is still open/ready, the GitHub Issue stays **open** until the **last** sibling closes.
     - Once all sibling tasks for Issue #A are closed, the label transitions to **`ncf:closed`**, the completion comment posts (`SquashCommitSha` / merged PR URL), and the GitHub Issue is **closed**.
   - [ ] When `Create GitHub Issues for local tasks` is enabled, creating a local `Open` task immediately creates a corresponding GitHub Issue stamped with `ncf:backlog` and assigned to you.

---

### UAT-12.11D-02 — Multi-User Pre-Flight Claim Conflict Detection (`/inbox` Suspension & External Close Cancellation)
**Goal:** Verify Stage 1 pre-flight ownership checks prevent two NCF instances from executing the same GitHub Issue simultaneously.

1. **Scenario A — Externally Closed Issue Before Execution:**
   - Keep a `Suggested` task linked to a GitHub Issue (`ExternalSource = "GitHub"`, `ExternalReferenceId = "<issueNumber>"`), or pause queue dispatch before promoting it to `Open`.
   - Manually **Close** that Issue directly on GitHub.
   - Allow NightCodeForge to pick up the `Open` task to begin Stage 1 (`task-review`).
2. **Scenario B — Issue Claimed / In-Progress by Another User on GitHub:**
   - Prepare an `Open` task linked to an open GitHub Issue.
   - On GitHub, manually assign that Issue to a **different** GitHub user (or apply the **`ncf:in-progress`** label while unassigned/assigned to someone else).
   - Allow NightCodeForge to pick up the task to begin Stage 1 (`task-review`).
   - Open **`/inbox`** (Human-in-the-Loop Inbox) and resolve the conflict question (test either overriding to run locally or cancelling the local task).
3. **Expected Results:**
   - [ ] **Scenario A:** Before running any LLM calls, the task deterministically transitions to **`Cancelled`**, records audit history / `ExecutionLog`, and updates DAG dependents & badge counts.
   - [ ] **Scenario B:**
     - Execution suspends **before** Stage 1 runs LLM turns (`task.IsWaitingForHuman = true`, `InterruptedStageId = "task-review"`).
     - **`/inbox`** displays a pending single-choice question card (e.g., *"GitHub Issue #<id> is already assigned to or in progress by another user on GitHub. How should NightCodeForge proceed?"*) with options to override the claim and execute locally or cancel the local task.
     - Submitting **Cancel** transitions the task cleanly to `Cancelled`; submitting **Override** resumes execution locally.
