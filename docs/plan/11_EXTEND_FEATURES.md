# 🧩 Phase 11: Extend Features & Integrations

> **Architectural Specification & Implementation Roadmap**  
> *Parent Plan:* [docs/PLAN.md](../PLAN.md) | *Dependencies:* Phase 4 (Harnesses & Sandboxing), Phase 5 (Web UI), Phase 8 (Database-Driven Workflows), Phase 9 (Codebase Remediation & Robustness), Phase 10 (MVP Final Polish)  
> *Status:* Active / In Progress | *Target Framework:* .NET 10 (C# 13)

---

## 1. Executive Summary & Strategic Prioritization

The core architecture of **NightCodeForge** delivers an autonomous software engineering pipeline: multi-workspace isolation, local Git mirror synchronization, single-worker GPU Hangfire queuing, multi-stage agentic workflows, deterministic verification quality gates, a modern Blazor web dashboard, database-driven dynamic workflow orchestration, and strict adherence to architectural robustness policies.

To transition active, real-world development projects (**OllamaRouter** and **ComicGenCLI**) into NightCodeForge immediately, **Phase 11 (Extend Features - Post-MVP)** prioritizes **backlog onboarding, direct task management, project preparedness, and quick wins**:

1. **Tier 1: Real-Project Onboarding & Backlog Management (Top Priority - Immediate Value):**
   - **Step 11.2:** Dedicated Task Creation Page (`/tasks/new`), First-Class Priority Management (P1–P5), and MergeMode Lifecycle.
   - **Step 11.7:** Task State History & Temporal Snapshot Audit Trail (interactive timeline and DiffPlex visual text diffs in Web UI).
   - **Step 11.12:** Interactive Agent Skills for Backlog Migration & Ideation (`ncf-backlog-migrator`, `ncf-ideation`, ready-to-use prompts, and bulk import).
   - **Step 11.13:** Project Preparedness, Compatibility Audit & Onboarding Tooling (verifying clean builds, fast test suites, and `.nightcodeforge` configuration for `OllamaRouter` and `ComicGenCLI`).
   - **Step 11.14:** Intelligent Task Dependency Detection in Ideation & Review Agents (backlog context injection, DAG relationship inference, and prerequisite enforcement).
   - **Step 11.15:** Priority-Aware Task Queue Scheduling & Execution Ordering (aligning DAG cascade unblocking, Hangfire insertion order, and DevQueue promotion with 1 Highest -> 5 Lowest).

2. **Tier 2: Core Ideation & Developer Ergonomics Quick Wins:**
   - **Step 11.5:** Ideation Quick Notes, Suggestions Backlog & Conversational Refinement.
   - **Step 11.1:** Heterogeneous Dual Model Architecture (Reviewer vs Coder models) and difficulty escalation.
   - **Step 11.3:** Smart CLI Output Filtering (compiler and test noise suppression for 95% token savings).
   - **Step 11.4:** Zero-Token Open-Source SAST Gate via Microsoft DevSkim CLI.

3. **Tier 3: Deep Integrations & Remote Experience:**
   - **Step 11.6:** Mobile-Responsive Web UI & Remote Tailscale Experience.
   - **Step 11.8:** Custom Repository Scripts for Deterministic Stages (Bash, Python, PowerShell).
   - **Step 11.9:** Model Context Protocol (MCP) Integration (HTTP & ContainerSandbox Stdio).
   - **Step 11.10:** GitHub Integration (Untrusted Intake Triage, PR Delivery & Mirroring).
   - **Step 11.11:** Voice Support, Continuous Dictation & PWA Mobile Experience.

---

## 2. Implementation Steps Roadmap

### Priority Tier 1: Real-Project Onboarding & Backlog Management (Top Priority)

| Step | Specification | Focus & Key Deliverables | Status |
| **11.2** | [📄 Step 11.2: Dedicated Task Creation Page, Priority Management & MergeMode](phase-11/STEP_11_2_TASK_CREATION_PAGE_AND_MERGE_MODE.md) | Dedicated task authoring page (`/tasks/new`) and modal, first-class editable Priority management (P1 Critical to P5 Trivial badges, drawer & inline card editing, sorting/filtering in Backlog), DAG prerequisite selection at creation, and interactive `MergeMode` editing (`Automatic`, `ManualReviewRequired`; `ExternalPullRequest` deferred to Step 11.10). | **Top Priority** |
| **11.7** | [🕰️ Step 11.7: Task State History & Temporal Snapshot Audit Trail](phase-11/STEP_11_7_TASK_STATE_HISTORY_AND_AUDIT_TRAIL.md) | Temporal audit history for `ForgeTask` entities: backend `TaskHistories` table with immutable snapshots, attribution (`ChangedBy`, `ChangeReason`), and interactive `TaskHistoryTimeline.razor` UI with DiffPlex text diff comparison. | **Top Priority** |
| **11.15** | [⏱️ Step 11.15: Priority-Aware Task Queue Scheduling & Execution Ordering](phase-11/STEP_11_15_PRIORITY_AWARE_TASK_QUEUE_SCHEDULING.md) | Priority queue enforcement for single GPU inference worker: DAG unblocking cascade ordered by `Priority ASC (1 -> 5)`, P1 hotfix routing to `llm-urgent`, and DevQueue priority sorting and manual head promotion. | Completed |
| **11.12** | [📋 Step 11.12: Interactive Agent Skills for Backlog Migration & Ideation](phase-11/STEP_11_12_BACKLOG_MIGRATION_AND_IDEATION_SKILLS.md) | Agent skills (`ncf-backlog-migrator`, `ncf-ideation`) and prompt templates to help developers ingest roadmaps, markdown TODOs, or issue dumps into PR-sized NCF tasks; Web UI bulk importer (`/tasks/import`). | **Next Priority** |
| **11.13** | [🚀 Step 11.13: Project Preparedness, Compatibility Audit & Onboarding Tooling](phase-11/STEP_11_13_PROJECT_PREPAREDNESS_AND_ONBOARDING.md) | Skill (`ncf-project-readiness`) and CLI pre-flight check (`ncf-audit.ps1`) to audit and configure existing repositories (`OllamaRouter`, `ComicGenCLI`): deterministic builds, category test filters, git hygiene, and `.nightcodeforge/workspace.json` bootstrap. | **Next Priority** |
| **11.14** | [🔗 Step 11.14: Intelligent Task Dependency Detection in Ideation & Review Agents](phase-11/STEP_11_14_INTELLIGENT_TASK_DEPENDENCY_DETECTION.md) | Backlog context injection for Ideation and Stage 1 Review agents; system prompts requiring identification of prerequisites, shared interfaces, and ordering; auto-population of `dependsOnTitles` and DAG dependency chips in Kanban and Drawer. | **Next Priority** |

### Priority Tier 2: Core Ideation & Developer Ergonomics Quick Wins

| Step | Specification | Focus & Key Deliverables | Status |
| :--- | :--- | :--- | :--- |
| **11.5** | [💡 Step 11.5: Ideation Quick Notes, Suggestions & Conversational Refinement](phase-11/STEP_11_5_IDEATION_QUICK_NOTES_SUGGESTIONS_AND_REFINEMENT.md) | Quick notes backlog intake ("Think Now, Chat Later"); proposal card lifecycle (`Approve` $\rightarrow$ Open, `Keep as Suggestion` $\rightarrow$ Suggested, `Discard` $\rightarrow$ Delete); "Refine in Ideation" pre-seeded chat; scoped `edit_task` tools; 24h draft pruning. | Planned |
| **11.1** | [🧠 Step 11.1: Dual Model Role Configuration & Difficulty Escalation](phase-11/STEP_11_1_DUAL_MODEL_ROLE_CONFIGURATION_AND_DIFFICULTY_ESCALATION.md) | Heterogeneous model architecture: Architect/Reviewer model (e.g. Qwen 3.8 27B) for ideation, review, code review; Coder model (e.g. Qwen 3.6 35B A3B) for fast coding; dynamic difficulty escalation for difficult/expert tasks. | Planned |
| **11.3** | [🧹 Step 11.3: Smart CLI & Build Tool Output Filtering (Context Compression)](phase-11/STEP_11_3_SMART_CLI_AND_BASH_OUTPUT_FILTERING.md) | Semantic output filtering for verbose CLI tools (`dotnet build`, `dotnet test`, `npm`). Relativizes paths, strips SDK boilerplates, drops non-fatal warnings when errors exist, preserving raw transcripts on disk. | Planned |
| **11.4** | [🛡️ Step 11.4: Free & Open-Source SAST Quality Gate via Microsoft DevSkim CLI](phase-11/STEP_11_4_DEVSKIM_SAST_QUALITY_GATE.md) | Deterministic SAST quality gate using Microsoft DevSkim CLI across C#, JS/TS, Python, Go. SARIF report parsing, configurable severity thresholds, and automatic loopback routing with diagnostics. | Planned |

### Priority Tier 3: Deep Integrations & Remote Experience

| Step | Specification | Focus & Key Deliverables | Status |
| :--- | :--- | :--- | :--- |
| **11.6** | [📱 Step 11.6: Mobile-Responsive Web UI & Remote Tailscale Experience](phase-11/STEP_11_6_MOBILE_RESPONSIVE_UI_AND_TAILSCALE_ACCESS.md) | First-class mobile/tablet responsiveness: off-canvas hamburger navigation drawer, touch targets $\ge 44\text{px}$, fixed-bottom ideation chat, mobile bottom sheet drawer, and SignalR reconnect resilience over Tailscale. | Planned |
| **11.8** | [📜 Step 11.8: Custom Repository Scripts for Deterministic Steps (Bash, Python, PowerShell)](phase-11/STEP_11_8_CUSTOM_SCRIPTS_FOR_DETERMINISTIC_STEPS.md) | Execution of custom repository scripts in `.nightcodeforge/scripts/` during deterministic stages across Bash, Python, and PowerShell. Injection of `PipelineDataBag` environment variables and output directives. | Planned |
| **11.9** | [⚡ Step 11.9: Model Context Protocol (MCP) Integration: HTTP Servers & ContainerSandbox Stdio](phase-11/STEP_11_9_MCP_HTTP_AND_CONTAINER_SANDBOX_INTEGRATION.md) | First-class MCP tool discovery and execution via Microsoft Agent Framework standards (`ModelContextProtocol`), streamable HTTP/SSE remote servers, and isolated stdio MCP servers in `ContainerSandbox`. | Planned |
| **11.10** | [🐙 Step 11.10: GitHub Integration (Untrusted Intake Triage, PR Delivery & Task Mirroring)](phase-11/STEP_11_10_GITHUB_INTEGRATION_ISSUES_AND_PRS.md) | Modular GitHub integration: untrusted issue intake routed to sandboxed triage to generate proposals, Pull Request delivery mode with rich PR metadata, and bi-directional status mirroring. | Planned |
| **11.11** | [🎙️ Step 11.11: Voice Support, Continuous Dictation & PWA Mobile Experience](phase-11/STEP_11_11_VOICE_SUPPORT_PWA_AND_MOBILE_SPEECH.md) | Hands-free mobile voice operations via Web Speech API (`SpeechRecognition`) and Blazor JS Interop. Continuous dictation with 1500ms silence detection and PWA configuration for persistent mic permissions. | Planned |

---

## 3. High-Level Architecture Overview

### 3.1 Step 11.2: Dedicated Task Creation & Priority Management
```mermaid
flowchart LR
    CreatePage["CreateTask.razor (/tasks/new)"] --> FormSubmit["Direct Task Authoring<br/>- Priority (P1-P5)<br/>- DAG Dependency Picker<br/>- MergeMode Selector"]
    FormSubmit --> DB[("ForgeTask Entity")]
    DB --> PriorityView["Backlog Priority Sorting & Badges"]
    DB --> ModeSelect["Editable MergeMode in Drawer & Review"]
```

### 3.2 Step 11.7: Task State History & Snapshots
```mermaid
flowchart LR
    Mutation["Task Mutation<br/>(UI Drawer, Ideation edit_task, Stage Advance)"] --> Capture["Capture Snapshot & Attribution"]
    Capture --> Latest["Tasks Table (Current State)"]
    Capture --> History["TaskHistories Table (Immutable Revisions)"]
    History --> Timeline["TaskHistoryTimeline.razor (DiffPlex Diffs)"]
```

### 3.3 Step 11.12: Backlog Migration & Ideation Skills
```mermaid
flowchart TD
    subgraph InputSources ["External Project Assets"]
        Roadmap["Existing ROADMAP.md / TODO.md / Issues<br/>(OllamaRouter, ComicGenCLI)"]
    end

    subgraph AgentSkills ["Interactive Agent Skills"]
        MigrateSkill["ncf-backlog-migrator<br/>- PR Sizing Enforcement<br/>- Sub-tasks & Acceptance Criteria<br/>- Dependency Identification"]
        IdeateSkill["ncf-ideation<br/>- Interactive Architectural Brainstorming"]
    end

    subgraph BacklogOutputs ["NCF Intake"]
        WebImport["Web UI Bulk Importer (/tasks/import)"]
        DirectTasks["Direct Tasks Table / .nightcodeforge/tasks/*.md"]
    end

    Roadmap --> MigrateSkill --> WebImport --> DirectTasks
    IdeateSkill --> DirectTasks
```

### 3.4 Step 11.13: Project Preparedness & Onboarding Tooling
```mermaid
flowchart LR
    TargetRepo["Target Project<br/>(OllamaRouter / ComicGenCLI)"] --> AuditTool["ncf-project-readiness Audit<br/>1. Clean git status<br/>2. Deterministic CLI build<br/>3. Fast test pass (no E2E)<br/>4. Execution mode choice"]
    AuditTool --> Bootstrap[".nightcodeforge/workspace.json"]
    AuditTool --> Scorecard["Readiness Scorecard (🟢 / 🟡 / 🔴)"]
```

### 3.5 Step 11.14: Intelligent Task Dependency Detection
```mermaid
flowchart TD
    BacklogContext["Inject <existing_workspace_tasks> Context Digest"] --> Ideation["Ideation Agent: Set dependsOnTitles"]
    BacklogContext --> Stage1["Stage 1 Review Agent: Audit Prerequisites"]
    Ideation --> AutoDAG["Create TaskDependency Records"]
    Stage1 -- Prerequisite Missing --> AskHuman["Call 'ask' or block on unmerged prerequisite"]
    AutoDAG --> DAGRun["DAG-Ordered Execution in Hangfire Queue"]
```

---

## 4. Phase 11 Definition of Done (DoD) Checklist

### Tier 1 Deliverables (Top Priority)
- [ ] **Step 11.2 (Dedicated Task Creation Page, Priority & MergeMode):**
  - [ ] Implement `CreateTask.razor` page at `/tasks/new` supporting direct creation without ideation chat.
  - [ ] Implement multi-select prerequisite dependency selector querying existing workspace tasks.
  - [ ] Make priority fully editable on existing tasks (interactive [TaskDetailDrawer.razor](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Backlog/TaskDetailDrawer.razor) selector, fast inline Kanban/card popover, and queue synchronization).
  - [ ] Add sorting and filtering by priority in [Backlog.razor](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Pages/Backlog.razor) (`OrderBy(t => t.Priority).ThenBy(t => t.CreatedAt)` putting P1 first).
  - [ ] Enable interactive MergeMode editing (Automatic, ManualReviewRequired) in [TaskDetailDrawer.razor](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Backlog/TaskDetailDrawer.razor) and [ApproveMergeModal.razor](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Review/ApproveMergeModal.razor) (ExternalPullRequest deferred to Step 11.10).

- [ ] **Step 11.7 (Task State History & Audit Trail):**
  - [x] Configure EF Core table `TaskHistories` with unique `(TaskId, Version)` index and entity `ForgeTaskHistory`.
  - [x] Implement repository baseline history capture and version incrementation in `EfCoreTaskRepository`.
  - [ ] Ensure all mutation channels (`TaskDetailDrawer`, `IdeationService`, `TaskOrchestrator`, `TaskMarkdownSyncService`) provide explicit `ChangedBy` and `ChangeReason`.
  - [ ] Build `TaskHistoryTimeline.razor` revision viewer with DiffPlex text diff comparison and embed in `TaskDetailDrawer.razor` and `TaskInspector.razor`.

- [x] **Step 11.15 (Priority-Aware Task Queue Scheduling & Ordering):**
  - [x] Update `TaskOrchestrator` cascade unblocking to query and sort unblocked tasks by `Priority ASC (1 -> 5), CreatedAt ASC`.
  - [x] Route Priority 1 (Critical) unblocked tasks directly to `llm-urgent`.
  - [x] Set default priority to `3` (Normal) across `ForgeTask`, `TaskCreationModal`, `MaintainerPromotionModal`, and `CanonicalTools`.
  - [x] Add queue priority sorting and manual head promotion in `DevQueueService` and `DevQueueTable.razor`.

- [ ] **Step 11.12 (Backlog Migration & Ideation Skills):**
  - [ ] Create `.nightcodeforge/skills/ncf-backlog-migrator/SKILL.md` with PR-sizing and decomposition rules.
  - [ ] Create `.nightcodeforge/skills/ncf-ideation/SKILL.md` for interactive brainstorming.
  - [ ] Provide standalone prompt templates in `docs/prompts/BACKLOG_MIGRATION_PROMPT.md` and `docs/prompts/TASK_DECOMPOSITION_PROMPT.md`.
  - [ ] Build Web UI bulk import page or modal (`/tasks/import`) supporting markdown files and JSON proposals.

- [ ] **Step 11.13 (Project Preparedness & Onboarding Tooling):**
  - [ ] Create `.nightcodeforge/skills/ncf-project-readiness/SKILL.md`.
  - [ ] Provide standalone prompt in `docs/prompts/PROJECT_PREPAREDNESS_PROMPT.md`.
  - [ ] Implement automated configuration generator producing `.nightcodeforge/workspace.json`.
  - [ ] Implement `scripts/ncf-audit.ps1` and `scripts/ncf-audit.sh` pre-flight audit CLI scripts.

- [ ] **Step 11.14 (Intelligent Task Dependency Detection):**
  - [ ] Implement `IWorkspaceBacklogSummaryService` generating compact `<existing_workspace_tasks>` digests.
  - [ ] Inject existing backlog digest into `IdeationService` system prompt.
  - [ ] Inject existing backlog digest and dependency verification directives into Stage 1 Task Review directive in `CrucibleAgentHarness`.
  - [ ] Update `TaskMarkdownSyncService` to parse `## Dependencies` from `docs/tasks/*.md` and sync `TaskDependency` records.
  - [ ] Render clickable prerequisite and downstream dependency chips in `KanbanCard.razor` and `TaskDetailDrawer.razor`.

### Tier 2 Deliverables (Quick Wins & Ergonomics)
- [ ] **Step 11.5 (Ideation Quick Notes & Suggestions):** Quick notes bar, proposal card tri-state actions, "Refine in Ideation", scoped `edit_task` tools, 24h pruning.
- [ ] **Step 11.1 (Dual Model Role Configuration):** Architect/Reviewer model vs Coder model, dynamic escalation on difficult tasks.
- [ ] **Step 11.3 (Smart CLI Output Filtering):** Compiler & test noise suppression, workspace path relativization.
- [ ] **Step 11.4 (DevSkim SAST Quality Gate):** Microsoft DevSkim CLI SARIF parsing and loopback diagnostics.

### Tier 3 Deliverables (Deep Integrations & Remote Experience)
- [ ] **Step 11.6 (Mobile Responsive UI & Tailscale):** Off-canvas nav drawer, touch targets $\ge 44\text{px}$, bottom sheet drawer.
- [ ] **Step 11.8 (Custom Repository Scripts):** Support `.nightcodeforge/scripts/` (.sh, .py, .ps1) and DataBag environment serialization.
- [ ] **Step 11.9 (MCP Integration):** Streamable HTTP/SSE and containerized stdio MCP transports.
- [ ] **Step 11.10 (GitHub Integration):** Sandboxed issue triage, Pull Request delivery, and bi-directional status mirroring.
- [ ] **Step 11.11 (Voice Support & PWA):** Web Speech API JS interop, 1500ms silence detection, and PWA manifest.
