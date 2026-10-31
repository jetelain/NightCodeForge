# 📎 Step 12.2B: Agent Attachment Tools, Citation Links & Prompt Digest Injection

> **Feature Implementation Step (Part 2 of 3 — Agent Tools & Prompt Context Layer)**  
> *Parent Plan:* [Features & Integrations Roadmap](README.md)  
> *Prerequisite Step:* [Step 12.2A: Attachments Store, Intake Service & MarkItDown Conversion](STEP_12_2A_ATTACHMENTS_STORE_INTAKE_AND_MARKITDOWN.md)  
> *Companion Step:* [Step 12.2C: Attachments Web UI & Task Picker](STEP_12_2C_ATTACHMENTS_WEB_UI_AND_TASK_PICKER.md)  
> *Target Components:* `src/NightCodeForge.Agents/Ideation`, `src/NightCodeForge.Agents/Crucible`, `src/NightCodeForge.Agents/Tools` | *Status:* Planned | *Target Framework:* .NET 10 (C# 13)  
> *Aligns With:* [AGENTS.md](../../AGENTS.md), [docs/PLAN.md](../PLAN.md)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Objective

With the workspace Attachments store and `IAttachmentIntakeService` in place ([Step 12.2A](STEP_12_2A_ATTACHMENTS_STORE_INTAKE_AND_MARKITDOWN.md)), this step (Part 2 of Step 12.2) exposes attachments to the **Ideation Agent** and the **Workflow Stage Agents** (Stage 1 Task Review, Stage 2 Implementation, Stage 3 Code Review & Fix):
1. **Progressive Disclosure via Prompt Digests:** Inject a compact `<workspace_attachments>` digest (`{id, title, kind, source, markdownPath}`) into the Ideation prompt and inject the task's linked attachments digest into Stage 1–3 prompts without bloating context windows.
2. **Read-Only Agent Tools (`list_attachments` & `read_attachment`):** Allow Ideation and Stage 1–3 agents to list workspace attachments and read their markdown companions, protected by path-traversal validation and fail-fast broken-symlink detection (`AttachmentTargetNotFoundException`).
3. **Citation Linking (`@attachment:{id}`):** Parse `@attachment:{id}` references and explicit attachment IDs from `propose_tasks` / task descriptions to materialize `TaskAttachmentLinks` in the database (failing fast on unknown attachment IDs).

---

## 2. Agent Consumption & Tool Specifications

### 2.1 Ideation Agent
- The ideation system prompt (built by context assembly in [`IdeationService`](../../src/NightCodeForge.Agents/Ideation/IdeationService.cs)) gains an **attachments digest** when the workspace has attachments: a compact list of `{id, title, kind, source, markdownPath}` — progressive disclosure, not full content injection.
- Read-only tools registered for the ideation agent:
  - `list_attachments` — full registry listing for the current workspace.
  - `read_attachment(attachmentId)` — returns the markdown companion content (truncated with an explicit continuation handle for very large docs).
- When proposing tasks, the agent **cites attachments** in the task description using the stable reference `@attachment:{id} {Title}` (e.g. *"Design the routing table per @attachment:2 OpenAI API Specification"*). The `propose_tasks` payload carries the linked attachment IDs; the service materializes `TaskAttachmentLinks` in the same transaction as task creation (no synthetic links — unknown IDs throw `AttachmentNotFoundException`).

### 2.2 Workflow Stage Agents (Stage 1 Review, Implementation, Code Review & Fix)
- The stage context (via `PipelineDataBag` / prompt template rendering in [`PromptPrefixStabilizer`](../../src/NightCodeForge.Agents/Optimizations/PromptPrefixStabilizer.cs) Layer 3) injects the **task's linked attachments digest** — the compact `{id, title, markdownPath}` list — so stage agents know the prior POC / external doc exists without bloating every prompt.
- Stage agents reuse the same `list_attachments` / `read_attachment` tools (scoped to the workspace) when a task description cites `@attachment:{id}` or when the digest reveals a relevant spec.
- Because the attachments directory lives outside the clone and worktrees, agents read it via the tools (which resolve through `IWorkspacePathResolver`) — **no agent tool receives a free-form path into this directory**.

### 2.3 Read-Only Enforcement & Symlink Traversal Guard
- `read_attachment` resolves `attachmentId` → registry → path:
  - For normal files: validates the resolved path stays inside the attachments directory (path traversal defense in depth) before reading.
  - For symlinked host files (`IsSymlink == true`): verifies that the symlink itself resides inside `attachments/`, and that its target matches the immutable, registered `SourceRef` recorded in the database / `manifest.json`.
  - If the target host file has been moved or deleted since registration, `read_attachment` throws `AttachmentTargetNotFoundException` with the missing target path (fail-fast, no silent empty response).
- No agent tool exposes write, edit, or delete operations on the attachments directory or on symlinked host files.

---

## 3. Implementation Checklist & Deliverables

- [ ] **Agent Tools (`CanonicalTools` & `CanonicalAIFunctionFactory`):**
  - [ ] Implement `list_attachments` and `read_attachment(attachmentId)` read-only tools in `NightCodeForge.Agents`.
  - [ ] Enforce workspace path-traversal validation, `SourceRef` symlink target verification, and fail-fast `AttachmentTargetNotFoundException` on broken symlinks.
  - [ ] Register `list_attachments` and `read_attachment` for Ideation and Workflow Stage 1–3 profiles.
- [ ] **Prompt Digests & `@attachment:{id}` Citation Linking:**
  - [ ] Inject workspace attachments digest into the Ideation system prompt when attachments exist.
  - [ ] Parse `@attachment:{id}` citations (and `propose_tasks` attachment IDs) into `TaskAttachmentLinks` on task creation, throwing `AttachmentNotFoundException` on unknown IDs.
  - [ ] Inject linked task attachments digest into Stage 1–3 prompts (Layer 3 Task Scope).
- [ ] **Tests:**
  - [ ] Unit tests: `read_attachment` broken symlink detection throwing `AttachmentTargetNotFoundException`, path-traversal rejection, and truncation handle for large docs.
  - [ ] Unit tests: `@attachment:{id}` parsing, unknown-ID hard error, and digest rendering in Ideation and Stage 1–3 prompts.
  - [ ] Integration test: intake attachment → ideation cites `@attachment:{id}` → task created with `TaskAttachmentLink` → stage agent reads attachment via `read_attachment`.

