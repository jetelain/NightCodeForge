# 🧠 Step 12.39: Two-Tier Agent Memory Management, Selective `:rw` Memory Mount & Live UI Notifications

> **Feature Implementation Step (Auditable Shared & Local Markdown Memory Across Ideation, Pipeline Stages & ACP)**  
> *Parent Plan:* [Features & Integrations Roadmap((README.md)  
> *Related Architecture:* [Step 12.2A: Attachments Store((STEP_12_2A_ATTACHMENTS_STORE_INTAKE_AND_MARKITDOWN.md) | [Step 12.14: Global Skills Injection((../archive/phase-12/STEP_12_14_GLOBAL_SKILLS_INJECTION_AND_STACK_TOOLING.md) | [Step 12.36A: Declarative Sandbox Mount Table((../archive/phase-12/STEP_12_36A_DECLARATIVE_SANDBOX_MOUNT_TABLE_AND_CONTAINER_LIFECYCLE_REDESIGN.md) | [Step 12.36B: CanonicalTools Modular Decomposition((../archive/phase-12/STEP_12_36B_CANONICAL_TOOLS_MODULAR_DECOMPOSITION_AND_INTERRUPTION_PIPELINE.md)  
> *Target Components:* `src/NightCodeForge.Core`, `src/NightCodeForge.Infrastructure`, `src/NightCodeForge.Agents`, `src/NightCodeForge.Web`, `tests/NightCodeForge.Core.Tests`, `tests/NightCodeForge.Infrastructure.Tests`, `tests/NightCodeForge.Agents.Tests`, `tests/NightCodeForge.Web.Tests` | *Status:* Planned | *Target Framework:* .NET 10 (C# 13)  
> *Aligns With:* [AGENTS.md((../../AGENTS.md), [docs/PLAN.md((../PLAN.md)
>
> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Objective & Two-Tier Memory Architecture

Today, [`AgentsMdService`((../../src/NightCodeForge.Agents/Context/AgentsMdService.cs) only ingests `AGENTS.md` passively as read-only prompt rules. Ideation runs in `ReadOnly` mode against `/workspace/clone`, Stage 2 runs in isolated `task-{id}` git worktrees, and no agent is instructed when or how to persist durable knowledge.

This step establishes a **100% auditable, plain-Markdown, two-tier agent memory system** designed for transparency, manual user editing, and cross-session continuity:

```mermaid
flowchart TB
    subgraph Tier1 ["Tier 1: In-Git Shared Memory (Repository)"(
        RootRules["AGENTS.md\n(Core constitution — always injected)"(
        Satellite[".agents/memory/*.md\n(Topic files — cataloged, read on demand)"(
    end

    subgraph Tier2 ["Tier 2: Out-of-Git Local Workspace Memory (~/.nightcodeforge/wks/{id}/memory/)"(
        Pinned["MEMORY.md\n(Pinned local index — always injected, capped)"(
        LocalTopics["*.md topic files\n(ideation-vision.md, user-preferences.md, operational-quirks.md)"(
    end

    Ideation["Ideation & Stage 1 / Stage 3\n(/workspace/clone :ro, /workspace/memory :rw)"(
    Stage2["Stage 2 Implementation (Crucible & ACP)\n(/workspace/task-{id} :rw, /workspace/memory :rw)"(
    Triage["Stage 0 Triage & Nightwatch\n(Untrusted / System :ro — /workspace/memory :ro)"(
    User["Developer (Git Repo & Web UI Memory Drawer)"(
    Notify["Memory Monitor -> SignalR -> Toast & Memory Badges"(

    Ideation -- "write / edit" --> Tier2
    Stage2 -- "write / edit / bash / ACP" --> Tier2
    Stage2 -- "PR commit (reviewed in Review Room)" --> Tier1
    User -- "Direct Git edits / commits" --> Tier1
    User -- "WorkspaceMemoryDrawer CRUD" --> Tier2
    Triage -. "read-only" .-> Tier2
    Tier1 --> Notify
    Tier2 --> Notify
```

### 1.1 Tier 1 vs. Tier 2 Responsibilities

| Dimension | Tier 1: Shared Repository Memory (`AGENTS.md` + `.agents/memory/*.md`) | Tier 2: Local Workspace Memory (`/workspace/memory/*.md`) |
| :--- | :--- | :--- |
| **Storage Location** | Inside the Git repository (`<repo>/AGENTS.md` and `<repo>/.agents/memory/*.md`) | Outside Git at `~/.nightcodeforge/wks/{workspaceId}/memory/` (sibling of `clone/`, `attachments/`, and `task-{taskId}/`) |
| **Audience** | All developers, CI, external agents (`agy`, Claude Code, Cursor), and all NCF agents | All local NCF sessions for the workspace (**Ideation**, **Stage 1–3**, and read-only for **Triage**/**Nightwatch**) |
| **Persistence & Sharing** | **Committed to Git** via **Stage 2 PR commits** (audited in the Review Room diff viewer) or **edited and committed directly by the developer** | **Live shared directory** (`:rw` across Ideation and Stages 1–3) — immediately visible without waiting for PR merges or causing worktree merge conflicts |
| **Typical Content** | Permanent coding standards, architectural invariants, module boundaries, domain glossary, testing pitfalls | Ideation interview outcomes, rejected alternatives, developer preferences, unmerged debugging discoveries, local environment quirks |
| **Context Injection** | `AGENTS.md` always injected in `<project_context>`; `.agents/memory/*.md` listed in `<memory_catalog>` for on-demand `read` | `/workspace/memory/MEMORY.md` always injected in `<workspace_memory>` (capped); other `/workspace/memory/*.md` files listed in `<memory_catalog>` |

### 1.2 Pinned Memory Size Caps (Named Constants)
To prevent context-window bloat across turns and preserve KV-cache efficiency, pinned `MEMORY.md` injection must enforce named constants in [`AgentsMdService`((../../src/NightCodeForge.Agents/Context/AgentsMdService.cs) (never magic numbers):
- `MaxPinnedMemoryLines = 150`
- `MaxPinnedMemoryBytes = 12_288` (12 KB)
- When `/workspace/memory/MEMORY.md` exceeds either limit, [`AgentsMdService`((../../src/NightCodeForge.Agents/Context/AgentsMdService.cs) truncates the injected excerpt cleanly at a line boundary and appends an explicit notice advising the agent to keep `MEMORY.md` concise and move detailed notes into topic files under `/workspace/memory/<topic>.md`.

---

## 2. Path Resolution, Selective `:rw` Mount & Security Boundaries

> **Service Design & Constructor Constraint ([AGENTS.md((../../AGENTS.md)):** All new and modified services must require all collaborators (no optional constructor parameters, no silent fallbacks or bare `catch { }` blocks), never exceed **10 constructor parameters**, and define named constants/enums instead of magic numbers or strings.

### 2.1 Directory Layout & Path Resolution
```text
~/.nightcodeforge/wks/{workspaceId}/
├── clone/                         # canonical git clone (:ro in Ideation / ReadOnly)
├── attachments/                   # workspace attachments store (:ro, Step 12.2A)
├── memory/                        # NEW — workspace local memory store (:rw for trusted agents, :ro for Triage/Nightwatch)
│   ├── MEMORY.md                  # pinned local memory index
│   └── ideation-vision.md         # topic-scoped local memory files
└── task-{taskId}/                 # ephemeral ForgeTask worktrees (:rw in Stage 2)
```

**ContainerSandbox Mapping (Docker):**
```text
/workspace/
├── clone/                         # :ro in ReadOnly containers
├── task-{taskId}/                 # :rw in Stage 2 containers
├── attachments/                   # :ro
└── memory/                        # :rw in trusted workspace scopes (including Ideation -ro containers), :ro in Triage/Nightwatch
```

- **Path Resolution Contracts ([`IWorkspacePathResolver`((../../src/NightCodeForge.Core/Interfaces/IWorkspacePathResolver.cs) & [`WorkspacePathResolver`((../../src/NightCodeForge.Infrastructure/Execution/WorkspacePathResolver.cs)):**
  - Add `GetWorkspaceMemoryPath(int workspaceId)` on `IWorkspacePathResolver` (with a default interface implementation returning `$"{GetAgentWorkspaceRootPath(workspaceId)}/memory"` so existing test fakes continue to compile) and implement it on `WorkspacePathResolver`.
  - Extend `WorkspacePathResolver.ResolveCruciblePath` so `/workspace/memory`, `/workspace/memory/...`, and `.nightcodeforge/wks/{workspaceId}/memory/...` resolve to the sibling `memory/` directory under `parentDir`, never inside `clone/` or `task-{id}/`.

### 2.2 Selective Read-Write Mount (`AllowWriteInReadOnlyScope`)
Because Ideation runs in a read-only workspace scope (`AgentMode.ReadOnly`, `ncf-wks-{id}-ro` in Docker) while still needing to persist local memory, [`SandboxMountTable`((../../src/NightCodeForge.Core/Interfaces/SandboxMountTable.cs) and [`SandboxPathResolver`((../../src/NightCodeForge.Core/Interfaces/SandboxPathResolver.cs) must support per-mount write exceptions inside read-only scopes:

1. **`VirtualMountEntry.AllowWriteInReadOnlyScope` ([`SandboxMountTable.cs`((../../src/NightCodeForge.Core/Interfaces/SandboxMountTable.cs)):**
   - Add `public bool AllowWriteInReadOnlyScope { get; init; }` (default `false`) to `VirtualMountEntry` (and optional constructor/helper support preserving existing callers).
2. **Workspace Scope Construction ([`ContainerEnvironmentFactory.cs`((../../src/NightCodeForge.Infrastructure/Execution/ContainerEnvironmentFactory.cs)):**
   - Add `EnsureHostMemoryDirectory` (following the `EnsureHostSkillsDirectory` / `EnsureHostTmpDirectory` pattern) so `~/.nightcodeforge/wks/{workspaceId}/memory/` is pre-created under user ownership before container spawn (preventing Docker from auto-creating an empty `root:root` directory).
   - Register the `/workspace/memory` `VirtualMountEntry` in `BuildWorkspaceScope` with:
     - `virtualSubPath: "memory"`
     - `virtualMountPath: "/workspace/memory"`
     - `isReadOnly: false`
     - `allowWriteInReadOnlyScope: true` (when trusted workspace scope; see Section 2.4 for Triage/Nightwatch `:ro` enforcement).
   - Ensure [`IdeationService`((../../src/NightCodeForge.Agents/Ideation/IdeationService.cs) and [`WorkspacePathResolverSandboxAdapter`((../../src/NightCodeForge.Agents/Tools/WorkspacePathResolverSandboxAdapter.cs) also carry/register the `/workspace/memory` mount entry (with `AllowWriteInReadOnlyScope = true` when trusted) and include the sibling `memory` root in `WorkspacePathResolverSandboxAdapter.ValidateWithinWorkspace` (permitting writes to `/workspace/memory` in read-only mode only when `AllowWriteInReadOnlyScope` is enabled while blocking writes outside `/workspace/memory`) so `read`/`write`/`edit` targeting `/workspace/memory/...` work uniformly across `ContainerSandbox`, `WslDirect`, and `BareHost`.
3. **Path Resolver Enforcement ([`SandboxPathResolver.cs`((../../src/NightCodeForge.Core/Interfaces/SandboxPathResolver.cs) & [`WorkspacePathResolverSandboxAdapter.cs`((../../src/NightCodeForge.Agents/Tools/WorkspacePathResolverSandboxAdapter.cs)):**
   - In `SandboxPathResolver.ResolveHostPath`, do **not** reject `forWrite && Scope.IsReadOnly` before resolving the target mount entry.
   - Instead, resolve the candidate mount (`ResolveCanonicalVirtualPathToHost` and `TryResolveAlreadyMappedHostOrWslPath`) and enforce:
     ```text
     effectiveReadOnly = matchedMount.IsReadOnly || (Scope.IsReadOnly && !matchedMount.AllowWriteInReadOnlyScope)
     ```
   - Reject writes to any mount where `effectiveReadOnly == true` (including `/workspace/clone`, `/workspace/skills`, `/workspace/attachments`, and unmapped paths) with `UnauthorizedAccessException`. When `Scope.IsReadOnly` is true and a mount has `AllowWriteInReadOnlyScope == true`, still reject writes to non-matching mounts or `/tmp` unless explicitly permitted.
   - Ensure `SandboxPathResolver.TranslateBashCommand` translates `/workspace/memory` prefixes accurately in `WslDirect` and `BareHost` execution tiers.
4. **Docker Bind Mount & Drift Detection ([`ContainerMountAndDriftBuilder.cs`((../../src/NightCodeForge.Infrastructure/Execution/ContainerMountAndDriftBuilder.cs)):**
   - In `ContainerMountAndDriftBuilder.BuildVolumeMounts` (Section 1C), compute:
     ```text
     effectiveReadOnly = entry.IsReadOnly || (options.IsRestrictedReadOnly && !entry.AllowWriteInReadOnlyScope)
     ```
   - Because `/workspace` is `:ro` in `RestrictedReadOnly` (`-ro`) containers while `/workspace/memory` evaluates `effectiveReadOnly == false`, Section 1C will **not** skip `/workspace/memory` and will mount `-v "{hostMemoryPath}:/workspace/memory"` (`:rw`) inside the `-ro` container over `/workspace`.
   - Ensure `EvaluateMountDrift` checks `/workspace/memory` presence, host source path, and `:rw` vs `:ro` flag so stale containers missing the `/workspace/memory` mount (or mounted `:ro` when `:rw` is expected) are recreated automatically.

### 2.3 Canonical File Tools & Profile Reconciliation
1. **Tool Permissions ([`DefaultAgentTools.cs`((../../src/NightCodeForge.Core/Domain/DefaultAgentTools.cs) & [`AppDbContext.cs`((../../src/NightCodeForge.Infrastructure/Data/AppDbContext.cs)):**
   - Grant `CanonicalToolNames.Write` and `CanonicalToolNames.Edit` to `DefaultAgentTools.Ideation` (so Ideation can create and update `/workspace/memory/*.md` directly using standard `read`/`write`/`edit` tools, while `/workspace/clone` remains protected as `:ro` by `SandboxPathResolver` and Docker).
   - Keep `DefaultAgentTools.ReadOnly` strictly read-only (no `Write`/`Edit`) so generic read-only profiles and untrusted triage do not gain write tools by default.
   - Update `AppDbContext.EnsureDefaultGlobalAgentProfilesAsync` to reconcile existing persisted Ideation profiles (global and workspace-scoped) with `Write` and `Edit` when still using standard Ideation tools.
2. **Module Guardrails ([`WorkspaceFileToolModule.cs`((../../src/NightCodeForge.Agents/Tools/WorkspaceFileToolModule.cs)):**
   - Update `WriteAsync` and `EditAsync`: when `IsEffectiveReadOnly` is true, check whether `SandboxPathResolver.MountTable.Entries` contains any writable entry with `AllowWriteInReadOnlyScope == true` (`!e.IsReadOnly && e.AllowWriteInReadOnlyScope`).
   - If **no** such selective writable mount exists, preserve the existing fail-fast `InvalidOperationException("Write operations are not permitted in read-only mode.")` (and equivalent edit exception).
   - If a selective writable mount **does** exist (such as `/workspace/memory` in Ideation), delegate path authorization to `SandboxPathResolver.ResolveHostPath(path, forWrite: true)`:
     - Paths under `/workspace/memory/*.md` resolve and write normally (with `.editorconfig` formatting).
     - Any attempt to write or edit `/workspace/clone/...`, `docs/tasks/...`, or arbitrary repo-relative paths throws `UnauthorizedAccessException` and returns a clear, actionable tool error:
       `Error: Access denied: Mount '<mount>' is read-only. In read-only mode, only '/workspace/memory' is writable.`
3. **Restrict Ideation `bash` Writes:**
   - In `ReadOnly` / Ideation sessions, [`ReadOnlyCommandValidator`((../../src/NightCodeForge.Agents/Tools/ReadOnlyCommandValidator.cs) continues blocking shell redirections and file-mutating shell commands; Ideation writes `/workspace/memory/*.md` exclusively through `write` and `edit` tools so every write is strictly boundary-checked and immediately traceable in chat.

### 2.4 Untrusted Intake Triage & Nightwatch Protection
- **Stage 0 Untrusted Intake Triage ([`TriageService.cs`((../../src/NightCodeForge.Agents/Triage/TriageService.cs)):**
  - Keep `UntrustedTriagePermittedTools` strictly without `Write` and `Edit`.
  - When `TriageService` constructs `SandboxScopeDescriptor.ForWorkspace(workspace, SandboxSecurityTier.RestrictedReadOnly)` (or if `BuildWorkspaceScope` attaches `/workspace/memory`), `/workspace/memory` must be mounted strictly **read-only** (`isReadOnly: true`, `allowWriteInReadOnlyScope: false`) for Triage so untrusted external reports can read workspace conventions/memory if helpful, **never** poison or overwrite them.
  - Because `ExecutionEnvironmentProvider` keys `TaskId == null` `RestrictedReadOnly` scopes by `ws:{workspaceId}:tier:RestrictedReadOnly` (`ncf-sandbox-{id}-ro`), `SandboxPathResolver`, `UntrustedTriagePermittedTools`, and `ReadOnlyCommandValidator` enforce `:ro` on `/workspace/memory` per-session in C# tools and shell validation; if container-level mount separation from Ideation's `:rw` `/workspace/memory` mount inside `ncf-sandbox-{id}-ro` is also required, `TriageService` must supply a distinct `ScopeCacheKey` / container name for Triage's `SandboxScope`.
- **System Nightwatch (`SandboxScopeKind.SystemNightwatch`):**
  - Keep all mounts under `/nightcodeforge/wks/...` strictly read-only (`isReadOnly: true`, `allowWriteInReadOnlyScope: false`), matching `SandboxMountTable.ForNightwatch`.

---

## 3. Progressive Disclosure Prompt Ingestion & Role-Specific `<memory_guidelines>`

### 3.1 Extending `AgentsMdService` & `AgentWorkspaceContextService`
To respect the **10-parameter constructor ceiling** on [`IdeationService`((../../src/NightCodeForge.Agents/Ideation/IdeationService.cs) (10 parameters), [`CrucibleAgentHarness`((../../src/NightCodeForge.Agents/Crucible/CrucibleAgentHarness.cs) (10 parameters), and [`AcpAgentHarness`((../../src/NightCodeForge.Agents/Acp/AcpAgentHarness.cs) (9 parameters), all memory ingestion and catalog discovery must flow through [`AgentsMdService`((../../src/NightCodeForge.Agents/Context/AgentsMdService.cs) and [`IAgentWorkspaceContextService`((../../src/NightCodeForge.Agents/Context/AgentWorkspaceContextService.cs) without adding constructor parameters to those agents/harnesses.

1. **Discovery Sources:**
   - **Tier 1 Root:** Existing `AGENTS.md` / `.agents/AGENTS.md` / `CLAUDE.md` / `GEMINI.md` via `AgentsMdService.IngestRulesAsync(repoPath)`.
   - **Tier 1 Committed Satellites:** All `*.md` files under `<repoPath>/.agents/memory/` (sorted alphabetically, capped to a reasonable catalog max constant e.g. `MaxCatalogEntries = 50`).
   - **Tier 2 Local Pinned Index:** `<memoryDir>/MEMORY.md` where `<memoryDir>` is resolved via `IWorkspacePathResolver.ToHostPath(IWorkspacePathResolver.GetWorkspaceMemoryPath(workspace.Id))` (when `workspace.Id > 0`).
   - **Tier 2 Local Topic Satellites:** All `*.md` files under `<memoryDir>/` excluding `MEMORY.md` (sorted alphabetically).
2. **Metadata & 1-Line Summary Extraction (`<memory_catalog>`):**
   - For each satellite file in `.agents/memory/*.md` and `/workspace/memory/*.md`, extract a 1-line description from optional YAML frontmatter (`description: ...`) or fall back to the first Markdown `# Heading` or first non-empty content line (truncated to `MaxCatalogDescriptionLength = 140` chars).
   - Render a lean progressive-disclosure block:
     ```xml
     <memory_catalog>
     Topic memory files available to read on demand via `read`:
     - [Shared/Git( .agents/memory/architecture-decisions.md — Module boundaries between Core, Agents, and Infrastructure
     - [Local( /workspace/memory/ideation-vision.md — Product roadmap constraints and rejected alternatives
     </memory_catalog>
     ```
3. **Always-Injected Local Memory (`<workspace_memory>`):**
   - When `/workspace/memory/MEMORY.md` exists and is non-empty, wrap its bounded content (enforcing `MaxPinnedMemoryLines` and `MaxPinnedMemoryBytes`) in `<workspace_memory>` tags.

### 3.2 KV-Cache Layer Placement ([`PromptPrefixStabilizer.cs`((../../src/NightCodeForge.Agents/Optimizations/PromptPrefixStabilizer.cs))
To preserve the >90% KV-cache prefix hit rate on local inference engines:
- **Layer 1 (`BuildLayer1GlobalRules`):** Holds the static `<project_context>` (`AGENTS.md` + execution guidelines), static `<memory_guidelines>` (role-specific write instructions), and `<skills>` catalog. Because `AGENTS.md` rarely changes between tasks, Layer 1 remains stable across runs.
- **Layer 3 (`BuildLayer3TaskScope`):** Holds the dynamic `<workspace_memory>` (`MEMORY.md`) and `<memory_catalog>` sections alongside `<task_scope>` and `<existing_workspace_tasks>`. Because local `/workspace/memory/*.md` files evolve dynamically during ideation and pipeline runs, placing them in Layer 3 prevents local memory edits from invalidating Layer 1 and Layer 2 KV-cache prefixes across tasks.
- **Ideation ([`IdeationService.cs`((../../src/NightCodeForge.Agents/Ideation/IdeationService.cs)):** Injects `<project_context>`, `<memory_guidelines>`, `<workspace_memory>`, and `<memory_catalog>` into the Ideation system prompt (and passes `<workspace_memory>` / `<memory_catalog>` through `AgentPromptContext` returned by `IAgentWorkspaceContextService.BuildPromptContextAsync`).
- **ACP Harness ([`AcpAgentHarness.cs`((../../src/NightCodeForge.Agents/Acp/AcpAgentHarness.cs)):** Receives the same `<workspace_memory>`, `<memory_catalog>`, and Stage-appropriate `<memory_guidelines>` via its prompt assembly path (`ExternalProcessAgentHarnessBase` / `AgentWorkspaceContextService`) so `agy acp` in Stage 2 knows both `/workspace/memory/` and `.agents/memory/`.

### 3.3 Role-Specific `<memory_guidelines>` Directives
Inject explicit, role-scoped `<memory_guidelines>` telling agents *what* to remember and *where* to write it:

- **Ideation Agent:**
  - Read `<workspace_memory>` and relevant `<memory_catalog>` files before asking questions the developer already answered in earlier sessions.
  - Whenever an `ask` interview or discussion establishes durable product vision, domain terminology, architectural preferences, user workflow preferences, or **explicitly rejected approaches**, use `write` or `edit` on `/workspace/memory/MEMORY.md` (concise bullet index) or `/workspace/memory/<topic>.md` (detailed topic notes with optional `description:` YAML frontmatter).
  - Never attempt to edit `AGENTS.md`, `.agents/memory/*`, or repository code directly from Ideation (the clone is read-only). When a local convention or decision should be shared with all developers via Git, include *"Update `AGENTS.md` or `.agents/memory/<topic>.md`"* as an explicit sub-task in the proposed Stage 2 task specification.
- **Stage 1 (`TaskReview`) & Stage 3 (`CodeReview`):**
  - Consult `<workspace_memory>` and relevant `<memory_catalog>` entries when scoping tasks or reviewing diffs.
  - Record non-obvious codebase pitfalls, build/test environment discoveries, or recurring review lessons into `/workspace/memory/*.md` so future runs benefit immediately.
  - In Stage 3 Review, treat updates to `AGENTS.md` and `.agents/memory/*.md` as first-class deliverables—verify they are accurate, concise, and free of machine-local/personal secrets.
- **Stage 2 (`Implementation` — Crucible & ACP `agy acp`):**
  - Record immediate operational/debugging findings or temporary cross-task notes in `/workspace/memory/<topic>.md` (shared live across concurrent worktrees without Git conflicts).
  - When implementing a task that establishes a permanent architectural invariant, project convention, or explicitly promotes local knowledge into shared team documentation, create or update `.agents/memory/<topic>.md` or `AGENTS.md` inside the task worktree (`/workspace/task-{id}`) so it is committed with the PR and reviewed by the user in the Review Room.
  - Respect user autonomy: developers may also edit and commit `AGENTS.md` and `.agents/memory/*.md` directly in the repository at any time; agents must treat committed repo rules as authoritative.

---

## 4. Real-Time UI Notifications, Telemetry & Workspace Memory Drawer

### 4.1 Dual-Path Memory Change Detection (`IAgentMemoryMonitorService`)
Because agents can modify memory via three channels—(1) Crucible `write`/`edit` tools (`WorkspaceFileToolModule`), (2) ACP host RPC (`AcpHostRpcHandler.WriteWorkspaceFileAsync`), or (3) direct container filesystem writes (`agy acp` native file tools or Stage 2 `bash` scripts into `:rw` mounts)—detection must combine immediate hooks with a lightweight snapshot diff:

1. **Canonical Memory Path Classifier:** A deterministic helper (`AgentMemoryPathClassifier` in `NightCodeForge.Core`) classifies any path into:
   - `AgentMemoryScope.LocalWorkspace` (`/workspace/memory/*.md`, host `~/.nightcodeforge/wks/{id}/memory/*.md`)
   - `AgentMemoryScope.SharedRepository` (`AGENTS.md`, `.agents/AGENTS.md`, `.agents/memory/*.md`)
   - `None` (ignore non-memory files)
2. **Pre/Post-Turn Snapshot Diffing (`IAgentMemoryMonitorService`):**
   - Captures lightweight `(RelativePath -> Sha256 / Length)` snapshots of `~/.nightcodeforge/wks/{workspaceId}/memory/` and `<workingDir>/.agents/memory/` + `AGENTS.md` before and after agent turns/stages in [`IdeationService`((../../src/NightCodeForge.Agents/Ideation/IdeationService.cs), [`CrucibleAgentHarness`((../../src/NightCodeForge.Agents/Crucible/CrucibleAgentHarness.cs), and [`AcpAgentHarness`((../../src/NightCodeForge.Agents/Acp/AcpAgentHarness.cs) (and attaches through `IAgentWorkspaceContextService` or `CanonicalTools` callbacks where constructor ceilings apply).
   - Also emits immediate activity notifications when `WorkspaceFileToolModule` (`write`/`edit`) or `AcpHostRpcHandler` (`fs/write_text_file`) mutates a memory path, deduplicating against the post-turn snapshot diff so a single write never fires duplicate toasts.

### 4.2 Activity Streaming & SignalR Contract
1. **Activity Stream Metadata ([`AgentActivityModels.cs`((../../src/NightCodeForge.Core/Streaming/AgentActivityModels.cs)):**
   - Add named constants to `AgentActivityMetadataKeys`:
     - `ActionTypeMemoryUpdated = "MemoryUpdated"`
     - `WorkspaceId = "workspaceId"` (or reuse/standardize key)
     - `MemoryScope = "memoryScope"` (`"Local"` | `"SharedGit"`)
     - `MemoryRelativePath = "memoryRelativePath"` (e.g. `/workspace/memory/ideation-vision.md` or `.agents/memory/architecture-decisions.md`)
     - `MemoryChangeKind = "memoryChangeKind"` (`"Created"` | `"Updated"` | `"Deleted"`)
   - Emit `AgentActivityKind.Notice` with `ActionType = ActionTypeMemoryUpdated` and the above metadata whenever a memory file changes.
2. **SignalR Hub & Client Connection ([`IOrchestratorHubClient.cs`((../../src/NightCodeForge.Infrastructure/Hubs/IOrchestratorHubClient.cs), [`SignalRActivityBridgeService.cs`((../../src/NightCodeForge.Infrastructure/Hubs/SignalRActivityBridgeService.cs), [`OrchestratorHubClientConnection.cs`((../../src/NightCodeForge.Web/Services/OrchestratorHubClientConnection.cs)):**
   - Add `Task ReceiveMemoryUpdated(int workspaceId, int? taskId, string stageId, string memoryScope, string relativePath, string changeKind, string summary);` to `IOrchestratorHubClient`.
   - In `SignalRActivityBridgeService.DispatchActivityEventAsync` (under `AgentActivityKind.Notice`), when `ActionType == AgentActivityMetadataKeys.ActionTypeMemoryUpdated`, broadcast `ReceiveMemoryUpdated` to `_hubContext.Clients.All` (mirroring the existing `ReceiveBacklogMutation` pattern).
   - Expose `event Action<int, int?, string, string, string, string, string>? OnMemoryUpdatedReceived;` on `OrchestratorHubClientConnection`.

### 4.3 Web UI Notifications, Chat Badges & `WorkspaceMemoryDrawer.razor`
> **UI & Presentation Separation ([AGENTS.md((../../AGENTS.md)):** Keep all `.razor` components strictly focused on layout, rendering, and event binding. All file listing, frontmatter/summary extraction, diff/notification formatting, validation, and CRUD coordination must reside in dedicated `NightCodeForge.Web.Services` classes (`WorkspaceMemoryUiService` and `MemoryPresentationHelper`) with unit tests and $\le 10$ constructor parameters (no optional constructor parameters, no bare `catch { }`).

1. **Live Toast Notifications ([`MainLayout.razor`((../../src/NightCodeForge.Web/Components/Layout/MainLayout.razor), [`ToastService.cs`((../../src/NightCodeForge.Web/Services/ToastService.cs), [`WebNotificationService.cs`((../../src/NightCodeForge.Web/Services/WebNotificationService.cs)):**
   - Subscribe to `OrchestratorHubClientConnection.OnMemoryUpdatedReceived` (in `MainLayout` / notification coordinator) and display a concise non-blocking toast via `ToastService.Show`:
     - Title: `🧠 Agent Memory Updated`
     - Message example: `Ideation updated /workspace/memory/ideation-vision.md` or `Task #42 (Stage 2) created .agents/memory/architecture-decisions.md`
   - Optionally include a `NotificationSubject` toggle in `WebNotificationService` / notification preferences if the user wants background desktop notifications on memory updates.
2. **Chat & Execution Inspector Badges ([`Ideation.razor`((../../src/NightCodeForge.Web/Components/Pages/Ideation.razor), [`StageConversationViewer.razor`((../../src/NightCodeForge.Web/Components/Monitoring/StageConversationViewer.razor)):**
   - Use `MemoryPresentationHelper` to identify tool calls (`write`, `edit`) targeting memory paths (`/workspace/memory/*`, `.agents/memory/*`, `AGENTS.md`) and post-turn memory change events.
   - Render a distinct `🧠 Memory` badge (`Local` vs `Shared/Git`, plus `Created`/`Updated`/`Deleted` pill) on those tool call cards in `Ideation.razor` and `StageConversationViewer.razor`, clickable to open `WorkspaceMemoryDrawer.razor` directly at that file.
3. **Workspace Memory Drawer (`WorkspaceMemoryDrawer.razor`):**
   - Add a `🧠 Memory` header action button in [`Ideation.razor`((../../src/NightCodeForge.Web/Components/Pages/Ideation.razor) (with a live indicator dot/count when memory files were updated in the session) and a memory inspector/editor panel accessible from Workspace Settings ([`Settings.razor`((../../src/NightCodeForge.Web/Components/Pages/Settings.razor) or workspace tabs) opening `WorkspaceMemoryDrawer.razor`.
   - **Tab 1 — Local Workspace Memory (`~/.nightcodeforge/wks/{id}/memory/*.md`):**
     - Lists `MEMORY.md` (with line/byte budget meter against `MaxPinnedMemoryLines` / `MaxPinnedMemoryBytes`) and all local topic `.md` files.
     - Full user audit & management: view rendered Markdown or raw source, create new `.md` files, edit inline and save directly to disk, or delete obsolete files (with confirmation).
   - **Tab 2 — Committed Repository Memory (`AGENTS.md` & `.agents/memory/*.md`):**
     - Lists `AGENTS.md` (and fallback rule file if used) plus all `.agents/memory/*.md` files from the workspace repository.
     - Allows inspecting content, viewing file paths for Git review, and optionally editing directly in the local repository path (or reminding the user when a task worktree has uncommitted/PR changes ready in the Review Room).
   - Automatically refreshes the active drawer list when `OnMemoryUpdatedReceived` fires for the current `workspaceId`.

---

## 5. Implementation Checklist & Deliverables

- [ ( **Core Contracts, Selective `:rw` Mount & Path Resolution (`NightCodeForge.Core` & `NightCodeForge.Infrastructure`):**
  - [ ( Add `GetWorkspaceMemoryPath(int workspaceId)` (with default interface implementation) on [`IWorkspacePathResolver`((../../src/NightCodeForge.Core/Interfaces/IWorkspacePathResolver.cs) and implement it plus `/workspace/memory` mapping in [`WorkspacePathResolver`((../../src/NightCodeForge.Infrastructure/Execution/WorkspacePathResolver.cs).
  - [ ( Add `VirtualMountEntry.AllowWriteInReadOnlyScope` to [`SandboxMountTable`((../../src/NightCodeForge.Core/Interfaces/SandboxMountTable.cs) and update [`SandboxPathResolver`((../../src/NightCodeForge.Core/Interfaces/SandboxPathResolver.cs) to enforce `matchedMount.IsReadOnly || (Scope.IsReadOnly && !matchedMount.AllowWriteInReadOnlyScope)` on write operations and translate `/workspace/memory` in `WslDirect`/`BareHost` commands.
  - [ ( Update [`ContainerEnvironmentFactory`((../../src/NightCodeForge.Infrastructure/Execution/ContainerEnvironmentFactory.cs) (`EnsureHostMemoryDirectory` + `/workspace/memory` mount entry in `BuildWorkspaceScope`) and [`ContainerMountAndDriftBuilder`((../../src/NightCodeForge.Infrastructure/Execution/ContainerMountAndDriftBuilder.cs) (`BuildVolumeMounts` Section 1C and `EvaluateMountDrift`) so `-ro` containers mount `/workspace/memory` `:rw` for trusted scopes while Triage and Nightwatch remain strictly `:ro`.
  - [ ( Add `AgentMemoryScope`, `AgentMemoryChangeKind`, `AgentMemoryChangeEvent`, and `AgentMemoryPathClassifier` in `NightCodeForge.Core`.
- [ ( **Canonical Tools, Profiles & Triage Isolation (`NightCodeForge.Core`, `NightCodeForge.Infrastructure`, `NightCodeForge.Agents`):**
  - [ ( Add `Write` and `Edit` to `DefaultAgentTools.Ideation` in [`DefaultAgentTools`((../../src/NightCodeForge.Core/Domain/DefaultAgentTools.cs), keep `DefaultAgentTools.ReadOnly` and [`TriageService`((../../src/NightCodeForge.Agents/Triage/TriageService.cs) (`UntrustedTriagePermittedTools`) strictly without write/edit tools and with `:ro` memory mount (supplying a distinct `ScopeCacheKey` / container name if container-level mount separation from Ideation's `ncf-sandbox-{id}-ro` is required), and reconcile stored Ideation profiles in [`AppDbContext`((../../src/NightCodeForge.Infrastructure/Data/AppDbContext.cs).
  - [ ( Update [`WorkspaceFileToolModule`((../../src/NightCodeForge.Agents/Tools/WorkspaceFileToolModule.cs) (`WriteAsync` / `EditAsync`) and [`WorkspacePathResolverSandboxAdapter`((../../src/NightCodeForge.Agents/Tools/WorkspacePathResolverSandboxAdapter.cs) (including the sibling `memory` root in `ValidateWithinWorkspace`) so `ReadOnly` scopes with an `AllowWriteInReadOnlyScope` mount delegate write checks to `SandboxPathResolver.ResolveHostPath(..., forWrite: true)` and reject all paths outside `/workspace/memory` with clear errors.
- [ ( **Prompt Ingestion, Progressive Disclosure & Role Directives (`NightCodeForge.Agents`):**
  - [ ( Extend [`AgentsMdService`((../../src/NightCodeForge.Agents/Context/AgentsMdService.cs) and [`AgentWorkspaceContextService`((../../src/NightCodeForge.Agents/Context/AgentWorkspaceContextService.cs) (preserving $\le 10$ constructor parameters on `IdeationService`, `CrucibleAgentHarness`, and `AcpAgentHarness`) to inject bounded `<workspace_memory>` (`MEMORY.md`, capped by `MaxPinnedMemoryLines = 150` / `MaxPinnedMemoryBytes = 12_288`) and `<memory_catalog>` across `.agents/memory/*.md` and `/workspace/memory/*.md`.
  - [ ( Place `<workspace_memory>` and `<memory_catalog>` in Layer 3 of [`PromptPrefixStabilizer`((../../src/NightCodeForge.Agents/Optimizations/PromptPrefixStabilizer.cs) and static `<memory_guidelines>` in Layer 1 / Ideation system prompt / ACP prompt context, including Stage 2 PR commit guidance for `.agents/memory/*.md` and `AGENTS.md`.
- [ ( **Memory Change Detection, Activity Telemetry & SignalR (`NightCodeForge.Core`, `NightCodeForge.Agents`, `NightCodeForge.Infrastructure`):**
  - [ ( Implement `IAgentMemoryMonitorService` (pre/post-turn snapshot diff + immediate tool/RPC deduplication) wired into [`IdeationService`((../../src/NightCodeForge.Agents/Ideation/IdeationService.cs), [`CrucibleAgentHarness`((../../src/NightCodeForge.Agents/Crucible/CrucibleAgentHarness.cs), and [`AcpAgentHarness`((../../src/NightCodeForge.Agents/Acp/AcpAgentHarness.cs).
  - [ ( Add memory metadata constants to [`AgentActivityModels`((../../src/NightCodeForge.Core/Streaming/AgentActivityModels.cs), `ReceiveMemoryUpdated` to [`IOrchestratorHubClient`((../../src/NightCodeForge.Infrastructure/Hubs/IOrchestratorHubClient.cs), event bridging to [`SignalRActivityBridgeService`((../../src/NightCodeForge.Infrastructure/Hubs/SignalRActivityBridgeService.cs), and `OnMemoryUpdatedReceived` to [`OrchestratorHubClientConnection`((../../src/NightCodeForge.Web/Services/OrchestratorHubClientConnection.cs).
- [ ( **Web UI Notifications, Badges & Drawer (`NightCodeForge.Web`):**
  - [ ( Implement `MemoryPresentationHelper` and `WorkspaceMemoryUiService` in `NightCodeForge.Web/Services` (no optional constructor parameters, $\le 10$ constructor parameters, no logic in `.razor` `@code` blocks).
  - [ ( Wire live `ToastService.Show` notifications in [`MainLayout`((../../src/NightCodeForge.Web/Components/Layout/MainLayout.razor) / [`WebNotificationService`((../../src/NightCodeForge.Web/Services/WebNotificationService.cs), render `🧠 Memory` badges in [`Ideation.razor`((../../src/NightCodeForge.Web/Components/Pages/Ideation.razor) and [`StageConversationViewer.razor`((../../src/NightCodeForge.Web/Components/Monitoring/StageConversationViewer.razor), and build `WorkspaceMemoryDrawer.razor` (Local Memory CRUD + Committed Repo Memory inspection/editing).
- [ ( **Tests:**
  - [ ( Unit tests in `NightCodeForge.Core.Tests` and `NightCodeForge.Infrastructure.Tests`: `SandboxPathResolver` selective `:rw` enforcement in `RestrictedReadOnly` scopes, `ContainerMountAndDriftBuilder` (`BuildVolumeMounts` + `EvaluateMountDrift` mounting `/workspace/memory` `:rw` inside `-ro` containers), `WorkspacePathResolver.ResolveCruciblePath`, and `AgentMemoryPathClassifier`.
  - [ ( Unit tests in `NightCodeForge.Agents.Tests`: `WorkspaceFileToolModule` allowing `/workspace/memory/*.md` writes while rejecting `/workspace/clone/...` in Ideation read-only mode, `TriageService` keeping `write`/`edit` excluded and `/workspace/memory` `:ro`, `AgentsMdService` `MEMORY.md` cap truncation + `.agents/memory/*.md` catalog generation, `PromptPrefixStabilizer` Layer 1/2 KV-cache invariance, and `IAgentMemoryMonitorService` snapshot diffing (including simulated ACP/bash writes).
  - [ ( Unit and bUnit tests in `NightCodeForge.Web.Tests`: `MemoryPresentationHelper`, `WorkspaceMemoryUiService`, SignalR `ReceiveMemoryUpdated` toast dispatch, `🧠 Memory` chat/inspector badges, and `WorkspaceMemoryDrawer.razor` local CRUD + committed memory view, followed by full test suite execution (including Acceptance & E2E suites) per [AGENTS.md((../../AGENTS.md).
