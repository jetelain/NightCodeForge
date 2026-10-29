# 🗺️ NightCodeForge - Implementation Plan & Roadmap

## 1. Project Directory & Solution Layout

The solution is organized using clean architecture principles, separating core domain models, workflow pipeline, infrastructure, AI harnesses, and the interactive Blazor UI.

```text
NightCodeForge/
├── docs/
│   ├── ARCHITECTURE.md          # Complete architectural specification of Forge orchestrator
│   ├── CRUCIBLE.md              # Native Crucible agent harness specification
│   ├── PI.md                    # Pi headless JSONL RPC daemon protocol & integration
│   ├── ACP.md                   # Agent Client Protocol (ACP) specification & modernization
│   ├── LOCAL_LLM_OPTIMIZATIONS.md # High-value local LLM optimization blueprint (<think> pruning, loop breakers, symbol index)
│   ├── REFERENCES.md            # Source code mapping to MS Agent Framework, Pi & ACP
│   ├── PLAN.md                  # Master roadmap & executive guide
│   └── plan/                    # Detailed phase-by-phase implementation specifications:
│       ├── 01_FOUNDATION_DOMAIN_AND_WORKFLOW.md
│       ├── 02_GIT_SUBSYSTEM_AND_EXT4_MIRROR.md
│       ├── 03_QUEUE_AND_ORCHESTRATION_ENGINE.md
│       ├── 04_HARNESSES_TOOLS_AND_LOCAL_OPTIMIZATIONS.md
│       ├── 04B_EXECUTION_ENVIRONMENT_AND_WORKFLOW_ALIGNMENT.md
│       ├── 05_BLAZOR_WEB_UI_AND_TELEMETRY.md
│       ├── 06_END_TO_END_TESTING_AND_HARDENING.md
│       ├── 07_PLAYWRIGHT_END_TO_END_ACCEPTANCE_TEST_PLAN.md
│       ├── 08_DATABASE_DRIVEN_WORKFLOWS_AND_STAGES.md
│       ├── 09_CODEBASE_REMEDIATION_AND_ROBUSTNESS.md
│       ├── 10_MVP_FINAL_POLISH.md
│       ├── 11_EXTEND_FEATURES.md
│       └── 12_EXTEND_FEATURES.md
├── src/
│   ├── NightCodeForge.Core/
│   │   ├── Domain/              # Entities: ForgeTask, TaskDependency, Question, StageCheckpoint, ExecutionLog
│   │   ├── Enums/               # TaskOverallStatus, HarnessType, LogLevel, QuestionType
│   │   ├── Workflow/            # Extensible Pipeline Engine (IWorkflowPipeline, IWorkflowStage, PipelineContext)
│   │   ├── Dependencies/        # DAG Resolution Engine, Topological Sort, Cycle Detection
│   │   └── Interfaces/          # IGitService, IAgentHarness, IOrchestratorEngine, IIdeationService
│   │
│   ├── NightCodeForge.Infrastructure/
│   │   ├── Data/                # AppDbContext, Entity Configurations, SQLite Migrations
│   │   ├── Git/                 # Native Git CLI runner using CliWrap (WIP commits, diff, branch isolation)
│   │   ├── Execution/           # Pluggable Execution Environments & Sandboxing:
│   │   │   ├── IExecutionEnvironment.cs           # Execution environment contract (BareHost, WSL2, Container)
│   │   │   ├── BareHostExecutionEnvironment.cs    # Direct host CLI execution (Developer opt-in)
│   │   │   ├── WslExecutionEnvironment.cs         # WSL2 distro execution (wsl.exe -d <distro>)
│   │   │   ├── ContainerSandboxEnvironment.cs     # Docker/Podman container runner with shared cache mounts
│   │   │   └── GitMirrorSyncService.cs            # Ext4 local Git mirror & host remote push/fetch
│   │   ├── BackgroundJobs/      # Hangfire setup (llm-urgent & llm-inference queues: WorkerCount = 1)
│   │   └── Hubs/                # SignalR Telemetry & Live Chat Hub (/hubs/orchestrator)
│   │
│   ├── NightCodeForge.Agents/
│   │   ├── Common/              # AgentSessionContext, AgentRunResult, TelemetryEvent
│   │   ├── Tools/               # Minimalist Canonical (Pi-Harmonized): read, edit, write, bash, web_search, ask, propose_tasks
│   │   │   └── EditorConfig/    # IEditorConfigService, EditorConfigParser, EditorConfigFileApplier (EOL, indent, tab/space, whitespace, charset)
│   │   ├── Dispatcher/          # LenientToolDispatcher (Aliasing, fuzzy matching, parameter normalization, payload repair)
│   │   ├── Stages/              # Agent Stages:
│   │   │   ├── TriageStage.cs            # Stage 0: Untrusted external intake, read-only pre-analysis on main/master
│   │   │   ├── TaskReviewStage.cs       # Stage 1: Scope, ambiguities, proactive human Q&A
│   │   │   ├── ImplementationStage.cs    # Stage 2: Code modifications, compilation & tests
│   │   │   └── CodeReviewAndFixStage.cs  # Stage 3: Gap analysis, lint checks & automated fixes
│   │   ├── Ideation/            # Interactive Discovery & Codebase Chat Agent
│   │   ├── Crucible/            # Implementation A: Crucible Harness (Microsoft Agent Framework & Extensions.AI)
│   │   ├── PiRpc/               # Implementation B: Fallback Pi Agent via JSON-RPC
│   │   └── Acp/                 # Implementation C: Open Agent Client Protocol (ACP) Adapter
│   │
│   └── NightCodeForge.Web/
│       ├── Components/          # Blazor Interactive Server UI
│       │   ├── Layout/          # NavMenu, MainLayout (Modern Obsidian/Indigo Dark Theme)
│       │   ├── Ideation/        # Interactive Codebase Discovery Chat & ForgeTask Proposer
│       │   ├── Backlog/         # ForgeTask creation, dependency DAG visualizer, priority reordering
│       │   ├── Monitoring/      # Live terminal output, GPU status gauge, stage progress bar
│       │   ├── Inbox/           # Human-in-the-loop pending question cards & response forms
│       │   └── Review/          # Git diff visualizer, commit squasher & branch merge
│       ├── Program.cs           # Dependency injection & pipeline composition
│       └── appsettings.json     # DB paths, repository settings, LLM endpoint configs
│
└── tests/
    ├── NightCodeForge.Core.Tests/           # Workflow pipeline & DAG dependency engine tests
    ├── NightCodeForge.Infrastructure.Tests/ # Git CLI & CliWrap integration tests (temp git repos)
    └── NightCodeForge.Agents.Tests/         # Harness tests, tool calling & interception tests
```

---

## 2. Technology Stack & NuGet Dependencies

| Project | Key Dependencies | Purpose |
| :--- | :--- | :--- |
| **NightCodeForge.Core** | *No external heavy dependencies* (Clean pure C#) | Domain models, extensible workflow engine, DAG dependency resolution |
| **NightCodeForge.Infrastructure** | `CliWrap` (v3.7+)<br>`Hangfire.AspNetCore`<br>`Hangfire.Storage.SQLite`<br>`Microsoft.EntityFrameworkCore.Sqlite`<br>`Microsoft.AspNetCore.SignalR` | Native Git CLI execution, background queue with `WorkerCount = 1`, database persistence & real-time telemetry |
| **NightCodeForge.Agents** | `Microsoft.Extensions.AI`<br>`Microsoft.Extensions.AI.Ollama`<br>`StreamJsonRpc`<br>`System.Text.Json` | AI agent abstractions, function/tool calling, memory checkpointing, ACP & Pi JSON-RPC communication, MCP host client |
| **NightCodeForge.Web** | `Microsoft.AspNetCore.Components.Web`<br>`DiffPlex` (for diff parsing) | Reactive Blazor UI, terminal streaming, side-by-side git diff rendering |
| **NightCodeForge.Tests** | `xunit`<br>`FluentAssertions`<br>`NSubstitute` | Automated unit and integration testing |

---

## 3. Master Phased Implementation Guides

To ensure thorough, battle-tested execution without ambiguity, each phase is decomposed into an exhaustive technical specification with explicit class contracts, test matrices, and Definition of Done (DoD) checklists:

| Phase | Dedicated Implementation Guide | Focus & Key Deliverables |
| :--- | :--- | :--- |
| **Phase 1** | [docs/plan/01_FOUNDATION_DOMAIN_AND_WORKFLOW.md](plan/01_FOUNDATION_DOMAIN_AND_WORKFLOW.md) | .NET 10 solution setup, domain entities (`TaskOverallStatus` from `Suggested` to `Closed`, `MergeMode`), DAG dependency engine, workspace concurrency lock, extensible workflow engine with `PipelineDataBag`, differential quality gates (`DeterministicCoverageGate`), and `docs/tasks/` markdown sync. |
| **Phase 2** | [docs/plan/02_GIT_SUBSYSTEM_AND_EXT4_MIRROR.md](plan/02_GIT_SUBSYSTEM_AND_EXT4_MIRROR.md) | Native Git CLI (`CliWrap`), branch isolation, WIP commits, noise-filtered diffs (`-U2`), working clone at `~/.nightcodeforge/wks/{id}`, remote path translation (`/mnt/...`), bare host (Unix) vs plain WSL2 (Windows) Git execution, and dirty host tree fallback. |
| **Phase 3** | [docs/plan/03_QUEUE_AND_ORCHESTRATION_ENGINE.md](plan/03_QUEUE_AND_ORCHESTRATION_ENGINE.md) | Hangfire single-worker GPU queue (`WorkerCount = 1`, `llm-urgent` vs `llm-inference`), clean `WaitingForHuman` interruption lifecycle, cascade unblocking when dependencies are `Closed`, and startup crash recovery (`IStartupRecoveryService`). |
| **Phase 4** | [docs/plan/04_HARNESSES_TOOLS_AND_LOCAL_OPTIMIZATIONS.md](plan/04_HARNESSES_TOOLS_AND_LOCAL_OPTIMIZATIONS.md) | Unified harness abstraction, OllamaRouter integration, canonical tools & `EditorConfigFileApplier`, container sandbox sentinel REPL, Crucible / Pi / ACP engines, modular skills & MCP, and the 7 local LLM optimizations (`LocalOptimizationFlags`). |
| **Phase 4B** *(Intermediate)* | [docs/plan/04B_EXECUTION_ENVIRONMENT_AND_WORKFLOW_ALIGNMENT.md](plan/04B_EXECUTION_ENVIRONMENT_AND_WORKFLOW_ALIGNMENT.md) | **Cross-Platform & Lifecycle Realignment:** Unix-only agent enforcement, Windows WSL2 mandatory requirement, working clones at `~/.nightcodeforge/wks/{id}`, Windows Crucible UNC path access (`\\wsl.localhost\...`), bare mode bash in WSL2, container mode `/workspace` tool call remapping, on-the-fly slim container images, 3-channel task intake (`Suggested`), manual promotion (`Open` / `Rejected`) with `Priority` & `MergeMode`, and main workflow status progression (`Reviewed` $\rightarrow$ `Implemented` $\rightarrow$ `Ready` $\rightarrow$ `Closed`). |
| **Phase 5** | [docs/plan/05_BLAZOR_WEB_UI_AND_TELEMETRY.md](plan/05_BLAZOR_WEB_UI_AND_TELEMETRY.md) | Blazor Interactive Server UI, obsidian/indigo dark theme, workspace provisioning & onboarding wizard, SignalR telemetry hub, ideation chat, visual DAG backlog with Suggested/Open promotion actions, question inbox, review room with diffs and manual merge triggers, and A/B testing comparator. |
| **Phase 6** | [docs/plan/06_END_TO_END_TESTING_AND_HARDENING.md](plan/06_END_TO_END_TESTING_AND_HARDENING.md) | End-to-end workflow simulation, chaos & sudden reboot resilience, multi-workspace VRAM protection validation, and empirical A/B benchmark validation. |
| **Phase 7** | [docs/plan/07_PLAYWRIGHT_END_TO_END_ACCEPTANCE_TEST_PLAN.md](plan/07_PLAYWRIGHT_END_TO_END_ACCEPTANCE_TEST_PLAN.md) | Playwright automated browser test suite driving workspace creation, task ideation, promotion, execution, deterministic gates, and diff review. |
| **Phase 8** | [docs/plan/08_DATABASE_DRIVEN_WORKFLOWS_AND_STAGES.md](plan/08_DATABASE_DRIVEN_WORKFLOWS_AND_STAGES.md) | Database-persisted `WorkflowDefinition` & `StageDefinition`, dynamic C# stage dispatcher, default 6-stage seeding, and permanent ideation chat persistence in SQLite. |
| **Phase 9** | [docs/plan/09_CODEBASE_REMEDIATION_AND_ROBUSTNESS.md](plan/09_CODEBASE_REMEDIATION_AND_ROBUSTNESS.md) | Architectural cleanup aligning with `AGENTS.md`: elimination of optional constructor parameters, hard errors on missing database records, constants/enums, and clean-break pruning. |
| **Phase 10** | [docs/plan/10_MVP_FINAL_POLISH.md](plan/10_MVP_FINAL_POLISH.md) | MVP Final Polish: enable EF Core migrations, build comprehensive namespace and module README.md documentation hierarchy, and execute an overall 360° code review and AGENTS.md policy hardening. |
| **Phase 11** *(Post-MVP)* | [docs/plan/11_EXTEND_FEATURES.md](plan/11_EXTEND_FEATURES.md) | Real-Project Onboarding, Hardening & Bug Fixes: dedicated task creation, priority queue scheduling, packaged agent skills, `ncf` CLI, temporal audit trail, live telemetry console, dynamic timeouts, relative worktree paths, task rejection, fine-grained locks, autonomous Git conflict management, and container lifecycle cleanup. |
| **Phase 12** *(Post-MVP)* | [docs/plan/12_EXTEND_FEATURES.md](plan/12_EXTEND_FEATURES.md) | Extend Features & Integrations: intelligent task dependency detection in ideation/review, task attachments store, unified conversational UI, ideation quick notes, heterogeneous dual model routing, smart CLI output filtering, DevSkim SAST quality gate, mobile & voice experience, custom repository scripts, MCP client integration, GitHub issue triage / PR delivery, Tailscale remote mesh, standalone desktop installer, and air-gapped / offline web app hardening. |

---

## 4. Executive Roadmap Summaries

### Phase 1: Foundation, Domain Models & Extensible Workflow Pipeline
> 📖 **Detailed Implementation Guide:** [docs/plan/01_FOUNDATION_DOMAIN_AND_WORKFLOW.md](plan/01_FOUNDATION_DOMAIN_AND_WORKFLOW.md)
* **Objective:** Establish the solution, multi-workspace support, domain entities, SQLite persistence, DAG dependency manager, and extensible workflow pipeline engine.
* **Tasks:**
  1. Initialize .NET 10 solution and create project skeleton (`Core`, `Infrastructure`, `Agents`, `Web`, `Tests`).
  2. Implement domain entities and enumerations:
     * `Workspace` (Id, Name, RepositoryPath, DefaultBaseBranch, ExecutionMode [BareHost, WslDirect, ContainerSandbox], ContainerImage, Ext4MirrorPath, CreatedAt).
     * `AgentProfile` (Id, WorkspaceId, Name, HarnessId, ModelName, ThinkingEffort, ThinkingTokenBudget, Temperature, HarnessConfigurationJson, OptimizationFlags [LocalOptimizationFlags]).
     * `ForgeTask` (Id, WorkspaceId, Title, Description, Status [defaults to Suggested], CurrentStageId, Priority, MergeMode [Automatic, ManualReviewRequired], BaseBranch, FeatureBranch, PipelineDataJson [cross-stage persistent data bag]).
     * `TaskDependency` (TaskId, DependsOnTaskId).
     * `Question` (TaskId, StageId, Prompt, Rationale, OptionsJson, Answer, Status).
     * `StageCheckpoint` (TaskId, StageId, SerializedMemory, GitCommitHash).
     * `StageExecutionRecord` (TaskId, StageId, AgentProfileId, HarnessId, ModelName, StartedAt, CompletedAt, DurationMs, PromptTokens, CompletionTokens, TotalTokens, TargetRouting, Status, OutputDataJson [stage output data snapshot]).
     * `ExecutionLog` (TaskId, StageId, Timestamp, Level, Source, Message).
     * Enums: `TaskOverallStatus` (`Suggested`, `Rejected`, `Open`, `Reviewed`, `Implemented`, `Ready`, `Closed`, `Cancelled`, `Failed`), `MergeMode` (`Automatic`, `ManualReviewRequired`), `ExecutionMode`, `ThinkingEffort`, `HarnessType`. Dedicated interruption fields: `ForgeTask.IsWaitingForHuman`, `ForgeTask.InterruptedAgentProfileId`, `Question.AgentProfileId`.
  3. Implement `DependencyResolutionEngine`:
     * Topological sorting of tasks per workspace.
     * Cycle detection (reject circular dependencies).
     * Dependency readiness evaluation: `IsReadyForExecution(taskId)` (requires `Status == Open` and all dependencies `Status == Closed`).
     * Cascade activation when a prerequisite reaches `Closed`.
  4. Implement **Workspace Concurrency Control (`IWorkspaceLockProvider`):**
     * Keyed async lock provider (`AsyncKeyedLock<int>` by `WorkspaceId`).
     * Guarantees that while the global inference queue processes sequentially (`WorkerCount = 1`), background utility jobs (`WorkerCount = 4`) and user UI actions do not execute concurrent Git operations or index modifications on the same workspace, preventing `.git/index.lock` race conditions.
  5. Implement `ExtensibleWorkflowEngine`:
     * `IWorkflowPipeline` and `IWorkflowStage` abstractions supporting both **Agentic** (LLM-driven with GPU allocation) and **Deterministic** (CPU-native builds, tests, SAST with 0 GPU/tokens) stages via `WorkflowStageKind`.
      * **`PipelineDataBag` Cross-Stage Data Persistence:** Thread-safe, serializable key-value store in `PipelineContext` (persisted to `AppDbContext.Tasks.PipelineDataJson`) enabling stages to record baseline metrics and share state across pipeline executions.
      * **Deterministic Quality Gates & Differential Outcome Routing:**
        - `DeterministicFormatStage`, `DeterministicBuildStage`, `DeterministicTestStage`, `DeterministicSastStage`.
        - **`DeterministicTestWithCoverageGate`:** Executes test suite with coverage collection (e.g. via Coverlet / Cobertura XML) on base branch and evaluates differential after implementation. Fails and loops back if coverage degrades below tolerance or if total test count decreases ("anti-cheating" detection).
        - **`DeterministicWarningRatchetStage`:** Captures baseline compiler/linter warning count; fails if newly introduced warnings exceed baseline ($\Delta > 0$).
        - Advances forward on pass, or loops backward to Stage 2 with structured diagnostic artifacts and **Synthetic Conversation Priming** to prevent agents from re-running redundant builds/tests.
     * **Explicit Stage Boundary Handover Protocol:** Enforces zero-memory clean context resets at stage transitions (archives previous stage chat history to disk transcripts, injecting only structured plan/diff/diagnostic artifacts into next stage).
     * Built-in `Default3AgentPipeline`:
       1. `TaskReviewStage` (Agentic - Default Profile: Deep Reasoning / high thinking effort)
       2. `ImplementationStage` (Agentic - Default Profile: Fast Coder / medium thinking effort)
       3. `CodeReviewAndFixStage` (Agentic - Default Profile: Strict Auditor / low temperature)
     * Extensible pipeline configurations allowing deterministic gate insertion (e.g. `Review ➔ Implementation ➔ Deterministic Build & Test ➔ Review & Fix`).
  6. Implement **`TaskMarkdownSyncService`**:
     * Generates and updates Markdown files with YAML frontmatter in `<WorkspaceRoot>/docs/tasks/YYYY-MM-DD-<slug>.md` (date-prefixed without ID in filename to enable clean sorting and avoid renames; appends `-2`, `-3` on slug collisions for the same date).
     * Automatically syncs question/answer logs, stage transitions, and generated plans to disk.
     * Employs `FileSystemWatcher` event debouncing (300ms window) and an in-memory write-suppression tracker (recording expected file hashes) to prevent self-triggering echo loops when writing back protected fields.
     * Enforces field protection: user edits to title, body, priority, merge_mode, profile, and dependencies sync to DB; system-managed fields (`status`, `stage`, `id`) are protected and restored if altered.
     * Detects dropped new files, allocates a new database ID, and injects `id` into frontmatter in-place with initial `status: suggested` and `merge_mode: manual_review_required`.
     * Cleans up or archives files when tasks reach `Closed` or `Cancelled`.
  7. Configure `AppDbContext` with SQLite and create initial migrations.
  8. Unit tests in `NightCodeForge.Core.Tests` for pipeline progression, pause/resume, token/duration tracking, DAG dependency graph resolution, echo loop suppression, and markdown task sync.
* **Deliverables:** Solution builds cleanly; workflow pipeline, metrics tracking, DAG dependency, and markdown sync unit tests pass 100%.

---

### Phase 2: Git CLI Subsystem & Ext4 Workspace Mirroring (CliWrap)
> 📖 **Detailed Implementation Guide:** [docs/plan/02_GIT_SUBSYSTEM_AND_EXT4_MIRROR.md](plan/02_GIT_SUBSYSTEM_AND_EXT4_MIRROR.md)
* **Objective:** Deliver a bulletproof Git manager using native CLI only (no LibGit2Sharp) capable of safe context switching, WIP checkpointing, and cross-filesystem Git mirroring (solving the 9p latency trap on Windows/WSL2).
* **Tasks:**
  1. Implement `GitCliRunner` wrapping `CliWrap` with command execution, timeout policies, stdout/stderr capture, and typed `GitCommandException`.
  2. Implement `GitWorkspaceManager`:
     * `EnsureRepositoryValidAsync(string repoPath)`: Validates target working tree.
     * `CheckoutTaskBranchAsync(Workspace workspace, int taskId, string baseBranch)`: Branch creation (`feature/task-{id}`) and checkout.
     * `CreateWipCheckpointAsync(Workspace workspace, int taskId, string stageName, string questionSummary)`: Performs `git add -A` and `git commit -m "WIP: [ForgeTask #{id}][Stage: {stageName}] {summary}" --no-verify`.
     * `GenerateDiffAsync(Workspace workspace, int taskId, string baseBranch)`: Executes `git diff {baseBranch}...feature/task-{id}`.
     * `RevertOrCleanAsync(Workspace workspace)`: Handles dirty states during crashes.
     * `SquashAndMergeAsync(Workspace workspace, int taskId, string baseBranch, string finalMessage)`: Squashes WIP commits upon approval.
  3. Implement **`GitMirrorSyncService` (Cross-Environment Working Clone Protocol):**
     * Agent clone repositories, where agents work, will ALWAYS be `~/.nightcodeforge/wks/{id}` (ext4 in WSL2 on Windows, host home on Unix).
     * On Windows, the remote pointing to the developer's workspace is `/mnt/{windows_path_transformed}`; on Unix, the remote is the raw developer workspace path.
     * Git push, pull, and merge operations on `main` are ALWAYS executed on the bare host on Unix, and on plain WSL2 on Windows.
     * On Windows, Crucible tools access working tree files via `\\wsl.localhost\{distrib}\home\{username}\.nightcodeforge\wks\{id}\` for high-speed I/O.
     * Configures the developer host repository with `git config receive.denyCurrentBranch updateInstead` to ensure `git push host-origin feature/task-{id}` succeeds without rejection even when checked out in the host IDE.
     * **Host Working Tree Dirty Conflict Fallback:** If the developer has uncommitted changes to tracked files in their host IDE, Git rejects `updateInstead`; `GitMirrorSyncService` catches the rejection, pushes to `refs/heads/feature/task-{id}-ncf` instead, and emits a SignalR alert informing the developer to reconcile local uncommitted files.
     * Performs `git fetch host-origin <baseBranch>` on stage inception.
     * Performs `git push host-origin feature/task-{id}` upon WIP checkpointing, human questions, or stage completion to keep developer's IDE in sync.
  4. Create integration tests in `NightCodeForge.Infrastructure.Tests` executing real Git commands on dynamically created temporary directories and verifying local remote fetch/push mirror sync.
* **Deliverables:** Git operations verified with automated tests covering branch switching, dirty detection, WIP commits, diff generation, and bidirectional mirror synchronization.

---

### Phase 3: Hangfire Queue & Orchestration Engine
> 📖 **Detailed Implementation Guide:** [docs/plan/03_QUEUE_AND_ORCHESTRATION_ENGINE.md](plan/03_QUEUE_AND_ORCHESTRATION_ENGINE.md)
* **Objective:** Configure the single-worker GPU inference queue, step metrics recording, and background task coordinator.
* **Tasks:**
  1. Configure Hangfire with SQLite storage.
  2. Register two background job servers:
     * **LLM Inference Server:** `WorkerCount = 1`, `Queues = new[] { "llm-urgent", "llm-inference" }` (global across all workspaces to guard GPU VRAM; provides non-preemptive priority pick-up so resumed tasks in `llm-urgent` are prioritized upon the next worker turn without concurrent VRAM oversubscription).
     * **Utility Server:** `WorkerCount = 4`, `Queues = new[] { "default" }`.
  3. Implement `TaskOrchestrator`:
     * Dequeues task, checks that `Status == Open` and all dependencies `Status == Closed`.
     * Acquires workspace lock via `IWorkspaceLockProvider` and checks out branch.
     * Loads current stage in the pipeline and invokes the selected harness (`IAgentHarness`).
     * Measures step performance (start/end timestamp, duration in ms, token usage).
     * Evaluates `StageExecutionResult`:
       * If `WaitingForHuman`: records `Question` (with `AgentProfileId`), `StageCheckpoint`, and `StageExecutionRecord`, performs Git WIP commit, sets `IsWaitingForHuman = true` and `InterruptedAgentProfileId = profile?.Id` without mutating `task.Status`, and cleanly completes the Hangfire job (`Succeeded`) to release the worker without false error alerts.
       * Stage Transitions:
         - Stage 1 (ForgeTask Review) completed $\rightarrow$ sets `Status = Reviewed`.
         - Stage 2 (Deterministic Pre-Implementation) captures baseline metrics.
         - Stage 3 (Implementation) completed $\rightarrow$ sets `Status = Implemented`.
         - Stage 4 (Deterministic Post-Implementation) verifies coverage and warnings.
         - Stage 5 (Code Review) applies review patches.
         - Stage 6 (Deterministic Post-Review Verification) tests review patches; on pass $\rightarrow$ sets `Status = Ready`; on failure $\rightarrow$ loops back with compiler/test diagnostics.
         - Final Merge Gate: If `MergeMode == Automatic`, executes squash & merge, sets `Status = Closed`, and cascades unblocking. If `MergeMode == ManualReviewRequired`, notifies UI and awaits maintainer approval before merging $\rightarrow$ `Closed`.
       * If `Failed`: logs error and metrics, transitions state to `Failed`.
  4. Implement `SubmitHumanAnswerAsync`:
     * Saves answer in DB (`question.Status = QuestionStatus.Answered; question.Answer = answer;`).
     * Sets `task.IsWaitingForHuman = false` (preserving `task.Status` intact).
     * Enqueues job to `llm-urgent` queue for immediate priority resumption with the exact interrupted agent profile (`InterruptedAgentProfileId`).
  5. Implement `RequestChangesAsync`:
     * Allows reviewer to provide revision feedback notes from the Review Room.
     * Transitions task from `Ready` back to Stage 3 (`ImplementationStage`) with reviewer feedback injected into prompt context.
     * Enqueues job to `llm-urgent` queue for priority iteration.
  6. Implement `CompleteTaskAndCascadeAsync`:
     * On final approval/merge, sets task to `Closed`.
     * Unlocks dependent tasks in the same workspace (where all dependencies have reached `Closed`) and enqueues newly ready `Open` tasks into `llm-inference`.
  7. Implement `IStartupRecoveryService` (Graceful Restart & Windows Update Recovery):
     * Executes during host startup before Hangfire worker begins polling.
     * Purges dangling Git lock files (`.git/index.lock`, `.git/refs/...`) left by abrupt process termination.
     * Scans working tree and creates automated rescue commit for uncommitted edits (`WIP-RECOVERY`).
     * Reconciles orphaned `Running` tasks: rehydrates conversation memory from last checkpoint, injects reboot notice, and enqueues to `llm-urgent` for immediate resumption.
* **Deliverables:** Background worker lifecycle working with simulated agent tasks, testing queue priority, step duration/token metrics, interruption, and dependency unblocking.

---

### Phase 4: AI Harness Abstraction & Implementations (Native, Pi, and ACP)
> 📖 **Detailed Implementation Guide:** [docs/plan/04_HARNESSES_TOOLS_AND_LOCAL_OPTIMIZATIONS.md](plan/04_HARNESSES_TOOLS_AND_LOCAL_OPTIMIZATIONS.md)
* **Objective:** Deliver the unified harness abstraction layer, first-class native & Pi engines, open **Agent Client Protocol (ACP)** adapter, and primary gateway integration with **OllamaRouter**.
* **Tasks:**
  1. Define `IAgentHarness`, `IHarnessRegistry`, `AgentSessionContext`, `AgentRunResult`, `StepExecutionMetrics`, and `AgentTelemetryEvent`.
  2. Implement **OllamaRouter Gateway Integration:**
     * Connect to OllamaRouter (`jetelain/OllamaRouter`) via `Microsoft.Extensions.AI.Ollama` or `Microsoft.Extensions.AI.OpenAI` pointing to `http://localhost:11434`.
     * Leverage OllamaRouter's automated GPU routing (Fast Local GPU vs Remote High-VRAM vs Cloud overflow).
     * Implement `IOllamaModelCatalogService` to query `/api/tags` and populate active local/remote models dynamically.
     * Capture target routing telemetry (e.g. from `/monitor` or response headers) for real-time UI display.
  3. Implement **Harness Implementations & Canonical Tools:**
      * **Minimalist Canonical Tool Suite (Pi-Harmonized) & EditorConfig Compliance:**
        - `read` (line-sliced via `offset`/`limit`), `bash` (with `HeadTailBuffer` truncation, compiler error extraction, and shell discovery `ls`/`find`/`rg`), `web_search` (extensible via `IWebSearchService`, defaulting to Ollama Web Search API), `ask`, and `propose_tasks`.
        - `write` & `edit` with **EditorConfig Enforcement (`IEditorConfigService`, `EditorConfigFileApplier`):**
          * Hierarchically resolves `.editorconfig` from target file directory up to `root = true`.
          * Applies `end_of_line` (`lf`, `crlf`, `cr`), `indent_style` (`tab`, `space`), `indent_size` / `tab_width`, `insert_final_newline` (`true`, `false`), `trim_trailing_whitespace` (`true`, `false`), and `charset` encoding on every file write or precision edit.
          * Automatically falls back to detecting dominant file conventions if `.editorconfig` is absent, preventing cross-platform Git diff churn.
          * Precision chunk search-and-replace in `edit`, supporting multi-disjoint `edits: []` and indentation tolerance.
     * **`LenientToolDispatcher` (Tolerance Engine):**
        - Tool name aliasing and fuzzy matching (`read` $\leftarrow$ `read_file`, `ReadFile`, `cat`, `view_file`; `edit` $\leftarrow$ `replace_file_content`, `edit_file`, `str_replace`, `replace_content`; `write` $\leftarrow$ `write_file`, `write_to_file`; `bash` $\leftarrow$ `run_command`, `exec`, `cmd`, `terminal`; `web_search` $\leftarrow$ `search_web`, `google_search`, `webSearch`, etc.).
        - Dropped tool translation: `list_dir` $\rightarrow$ `bash("ls -la ...")`, `grep_search` $\rightarrow$ `bash("rg -n ...")`.
       - Parameter normalization (`path` $\leftarrow$ `filePath`, `file`; `edits` $\leftarrow$ root `old_text` / `oldText` / `target_content`, `new_text` / `newText` / `replacement_content`; `offset` $\leftarrow$ `start_line`, `line`; `limit` $\leftarrow$ `end_line`, `limit_lines`).
       - Resilient JSON payload repair (stripping markdown code fences, sanitizing unescaped newlines in multi-line code writes).
       - Automatic type coercion (e.g. numeric strings to integers).
     * **Implementation A (Native First-Class): Crucible Harness (Microsoft Agent Framework via Official NuGet Releases, see docs/CRUCIBLE.md):**
       * Pre-configured `HarnessAgent` (`Microsoft.Agents.AI.Harness`) orchestrating tool invocation, function approval, and OpenTelemetry instrumentation.
       * Interruption & Checkpointing: Uses `AIAgent.SerializeSessionAsync` / `DeserializeSessionAsync` with `AgentSession` persistence to `AppDbContext`.
       * Zero data-loss guaranteed by `PerServiceCallChatHistoryPersistingChatClient` across tool calls; session resumption seamlessly injects human replies via `MessageInjectingChatClient`.
       * Automatic token usage extraction from `AdditionalProperties["usage"]`.
     * **Implementation B (Fallback First-Class): Pi via JSON-RPC (see docs/PI.md):**
       * External process runner communicating via JSON-RPC over `stdio` or local socket.
       * Captures `ask_user` events, requests session suspension, and captures state token.
      * **Implementation C (Open Standard): Agent Client Protocol (ACP) Harness Adapter (see docs/ACP.md):**
        * Protocol adapter implementing the official **[ACP specification](https://agentclientprotocol.com/)** over JSON-RPC (`stdio` or WebSocket/HTTP).
        * Incorporates and modernizes the `acp-csharp` SDK (`AgentClientProtocol`), upgrading to C# 13 and adding ACP v2 negotiation with backward-compatible v1 fallback.
        * Bridges ACP session messages, workspace tools, and human permission requests (`session/request_permission`) into NightCodeForge's pause/resume checkpointing.
        * Enables plugging any external ACP-compliant coding agent out-of-the-box.
  4. Implement **Execution Sandboxing Subsystem (`IExecutionEnvironment`):**
     * Pluggable runners: `BareHostExecutionEnvironment`, `WslExecutionEnvironment`, and `ContainerSandboxEnvironment` (`NightCodeForgeContainerExecutor`).
     * Inspired by Microsoft Agent Framework shell executor architecture:
       - **Persistent Sentinel REPL Protocol:** Keeps `docker exec -i <container> bash --noprofile --norc` alive with unique GUID sentinels (`echo "__NCF_SENTINEL_<guid>__ $?"`) for instant command execution without container restart overhead.
       - **Timeout & Runaway Process Cancellation:** Sends `\x03` (SIGINT) on command timeout, awaits 2-second grace period for sentinel return, and forcefully restarts the shell process if unresponsive.
       - **`HeadTailBuffer` Output Compaction:** UTF-8 rune-safe bounded accumulation capping stdout/stderr at 32 KB with `[... truncated N bytes ...]` markers.
     * Customizations for software engineering: Network enabled (for package restores), non-read-only root (for package caches), rich developer images (.NET, Node, Git), and native ext4 Git branch mirror mounts.
     * **Restricted Read-Only Sandbox Tier (`RestrictedReadOnly`):** For untrusted intake triage on `main`/`master`, launches containers with `--network none`, `:ro` workspace mounts, `--read-only` root filesystem, `--cap-drop=ALL`, `--user 1000:1000`, and scrubbed host secrets to neutralize indirect prompt injection exfiltration.
     * In-process harness delegation: `bash` in Crucible, Pi, and ACP harnesses executes inside the sandbox container/WSL, granting autonomous command execution without user prompt fatigue.
     * Central host orchestration: LLM inference, API keys, ACP/Pi lifecycle management, and `mcp_config.json` remain centralized on the host.
   5. Implement Specialized Workflow Stages (Agentic & Deterministic) & Bootstrap Prompts:
      * **Stage-Scoped Tool Visibility:** Enforces strict tool gating per stage (Stage 0 Triage: Read-only + `propose_tasks`; Stage 1: Read-only; Stage 2: Code/Build/Test; Stage 3: Audit/Patch; Discovery: Read-only exploration).
      * **`TriageStage`:** Automated intake stage for untrusted external reports (GitHub/GitLab tasks) running on `main`/`master`; uses Crucible-exclusive tool whitelisting (`read`, `propose_tasks`) and `RestrictedReadOnly` sandbox to prevent tampering or exfiltration; outputs backlog proposals for human review.
      * **`TaskReviewStage`:** System persona for architectural analysis; bootstrap prompt injecting `{{ForgeTask.Title}}`, `{{ForgeTask.Description}}`, repository structure, with strict directive to invoke `ask` on any ambiguity.
      * **`ImplementationStage`:** System persona for senior engineer / TDD; bootstrap prompt injecting approved Stage 1 plan, `{{PriorAnswers}}`, affected files, and parsed diagnostic findings from prior deterministic stages (`{{PriorStageArtifacts.Diagnostics}}`); tool loop for coding, building, and running tests.
      * **`CodeReviewAndFixStage`:** System persona for critical code auditor; bootstrap prompt injecting target `{{BaseBranch}}`, noise-filtered `{{GitDiff}}` changeset (`-U2`, lockfile exclusions); automated gap analysis, regression detection, and direct patch application.
      * **Built-in Deterministic Stages (DeterministicWorkflowStage):**
        - `DeterministicBuildStage`: Executes `dotnet build` / package build; validates compile cleanliness without consuming GPU tokens.
        - `DeterministicTestStage`: Executes unit test runner; parses failing test assertions and stack frames into structured diagnostic artifacts.
        - `DeterministicSastStage`: Executes static code and vulnerability analyzers (Roslyn Analyzers, Semgrep, Trivy) producing finding reports for remediation.
  6. Implement **Model Context Protocol (MCP) Integration:**
     * Define `IMcpClientManager` to decouple MCP lifecycle management from specific underlying client packages.
     * Connect to external MCP servers configured in `mcp_config.json` via Microsoft Agent Framework (`Microsoft.Agents.Mcp`) or standard MCP JSON-RPC.
     * Dynamically discover tools from database, GitHub, or custom CLI MCP servers and register them into the active agent session.
     * Forward MCP tool definitions to ACP agents for unified multi-harness tooling.
  7. Implement **Modular Skills System (`Microsoft.Agents.AI.Skills`):**
     * Adopt standard **[Agent Skills specification](https://agentskills.io/)** using `AgentSkillsProvider` (as an `AIContextProvider`).
     * Support hierarchical skill resolution via `AggregatingAgentSkillsSource` (global `~/.nightcodeforge/skills/` and repo-level `.nightcodeforge/skills/` or `.agents/skills/`).
     * Progressive disclosure pattern: advertises skill names and descriptions in the system prompt prefix (~25 tokens/skill); loads full markdown instructions on-demand via `load_skill`, supplementary assets via `read_skill_resource`, and verified automation scripts via `run_skill_script`.
  8. Implement **Repository Guidelines & `AGENTS.md` Loader:**
     * Automatically discover and parse `AGENTS.md`, `.agents/AGENTS.md`, or `CLAUDE.md` from the target workspace.
     * Prepend parsed repository instructions, architectural rules, and build/test commands to stage system prompts across all harnesses (Crucible, Pi, and ACP).
  9. Implement **Session Transcript & Hybrid Diagnostics Service:**
     * `SessionTranscriptService`: Serializes full turn-by-turn agent sessions into structured Markdown files in `<WorkspaceRoot>/docs/transcripts/YYYY-MM-DD-task-{id}-{stage}-{status}.md` (thought blocks, tool calls, compiler outputs, Q&A).
     * `TranscriptDiagnosticService`: Evaluates failed or anomalous transcripts via either **Cloud LLMs** (fast, zero local VRAM consumption via OllamaRouter cloud models or external cloud API) or **Local LLMs** (100% private/offline, queued sequentially via `WorkerCount = 1`) to produce actionable remediation advice.
  10. **Interactive Ideation Agent Service (`IIdeationService`):**
      * Read-only exploration agent that converses with the developer, browses the workspace codebase, and proposes tasks with dependencies via `propose_tasks`.
  11. Implement **Local LLM Token Minimization & Context Compaction Engine (see docs/LOCAL_LLM_OPTIMIZATIONS.md):**
      * Static prompt prefix stabilization enforcing a 4-layer immutable hierarchy for 100% KV cache hit rate across turns.
      * Native in-loop context compaction via `ToolResultCompactionStrategy` (`Microsoft.Agents.AI.Compaction`) with `MinimumPreservedGroups = 3` and custom `ToolCallFormatter` to compact older tool outputs into concise stubs.
      * **Thinking Trace Compaction (`ThinkingTraceCompactionChatClient`):** Prunes `<think>` monologues from prior assistant turns in conversation memory, cutting multi-turn token re-ingestion by 60-80% for reasoning models (DeepSeek-R1, QwQ).
      * **Degenerate Loop & Repetition Breakers (`ToolLoopCircuitBreaker`):** In-flight streaming n-gram repetition detector + duplicate tool invocation interceptor with synthetic steering alerts to prevent GPU lockup.
      * **Deterministic Diagnostic Extraction:** Roslyn error parser (`error CS\d+`) and test assertion extractor replacing raw multi-line compiler dumps.
      * **Deterministic Code Intelligence (`RoslynSymbolIndexProvider`):** Pre-indexes workspace symbols on branch creation, injecting exact class/interface file paths and line offsets into Stage 1 context (0 GPU tokens).
      * **JIT Model Pre-Loading:** Asynchronously warms up next stage's model weights in OllamaRouter during CPU-bound deterministic stage execution.
      * Bounded command output streaming via `HeadTailBuffer` + compiler diagnostic extraction (extracting only failing `error CS\d+` lines and test assertions).
      * Synthetic conversation priming: pre-seeds active chat history with simulated prior tool execution pairs (e.g. deterministic test results) to eliminate exploratory agent tool round-trips.
* **Deliverables:** Harness abstraction layer complete; Canonical tools & LenientToolDispatcher operational with EditorConfig compliance (EOL, tab/space, whitespace, charset); Crucible, Pi, and ACP adapter functional; token minimization & context compaction engine verified; MCP, Skills, and AGENTS.md integration verified; execution sandboxing and shared caches operational; session transcripts and hybrid failure diagnostics operational; 3 agent stages and discovery agent verified with mock/local LLMs and step metrics recorded.

---

### Phase 4B: Execution Environment, Path Remapping & ForgeTask Lifecycle Alignment (Intermediate)
> 📖 **Detailed Implementation Guide:** [docs/plan/04B_EXECUTION_ENVIRONMENT_AND_WORKFLOW_ALIGNMENT.md](plan/04B_EXECUTION_ENVIRONMENT_AND_WORKFLOW_ALIGNMENT.md)
* **Objective:** Align the backend with cross-platform realities (Unix-only agents, mandatory WSL2 on Windows, canonical clone at `~/.nightcodeforge/wks/{id}`, Windows Crucible UNC file bridge, container `/workspace` remapping) and establish the updated 3-channel task intake, manual promotion action (`Priority`, `MergeMode`), and deterministic post-review verification.
* **Tasks:**
  1. Enforce Unix-only agent confinement: require WSL2 on Windows; execute bare mode `bash` via `wsl.exe -d <distrib> -- bash` on Windows and `/bin/bash` on Unix.
  2. Implement `IWorkspacePathResolver` / `WorkspacePathResolver`:
     * Canonical agent working clone: `~/.nightcodeforge/wks/{id}`.
     * Remote URL resolution: `/mnt/{windows_path_transformed}` on Windows, raw developer path on Unix.
     * Windows Crucible UNC path access: `\\wsl.localhost\{distrib}\home\{username}\.nightcodeforge\wks\{id}\`.
     * Container mode path remapping: transparently translate `/workspace/...` calls from Crucible tools into real host/WSL filesystem paths.
  3. Implement on-the-fly container generator with shared package cache mounts (`~/.nuget/packages`, `~/.npm`, `~/.cache/uv`).
  4. Implement 3-channel task creation (External Triage workflow, Ideation chat, `docs/tasks/*.md`), all initiating tasks in `Suggested` status.
  5. Implement manual promotion action: promotes `Suggested` $\rightarrow$ `Open` (setting `Priority` 1..5 and `MergeMode` [Automatic vs. ManualReviewRequired]) or `Rejected`.
  6. Realize full main workflow stage sequence:
     * Stage 1: `TaskReviewStage` $\rightarrow$ `Reviewed`.
     * Stage 2: `DeterministicPreImplementationStage` (baseline build, test, coverage).
     * Stage 3: `ImplementationStage` $\rightarrow$ `Implemented`.
     * Stage 4: `DeterministicPostImplementationStage` (coverage & warning ratchet).
     * Stage 5: `CodeReviewAndFixStage` (auditing and direct polish fixes).
     * Stage 6: `DeterministicPostReviewStage` (compilation & test verification of review patches) $\rightarrow$ `Ready` on pass, loops back on failure.
     * Stage 7: Merge Gate (`Closed` via auto-merge or maintainer manual review approval).
  7. Support universal human clarification (`ask`) at any stage, pausing via `IsWaitingForHuman = true` and `InterruptedAgentProfileId` without mutating task status, freeing the GPU worker; resuming with exact agent on answer.
* **Deliverables:** Path resolver, cross-environment Git and Crucible UNC I/O, on-the-fly container sandbox, 3 task intake channels, manual promotion action, and complete 7-step main workflow verified with automated tests.

---

### Phase 5: Modern Blazor Web UI & SignalR Telemetry
> 📖 **Detailed Implementation Guide:** [docs/plan/05_BLAZOR_WEB_UI_AND_TELEMETRY.md](plan/05_BLAZOR_WEB_UI_AND_TELEMETRY.md)
* **Objective:** Build a sleek, responsive, developer-grade dashboard for ideation chat, backlog & dependency management, live monitoring, question answering, transcript inspection, and diff review.
* **Tasks:**
  1. Setup Blazor Interactive Server with modern dark-mode aesthetic (custom CSS tokens, obsidian/indigo palette, crisp typography).
  2. Implement **Workspace Provisioning & Creation Wizard:**
     * Multi-step repository onboarding wizard, local path validation, and automatic Git detection (`git rev-parse`).
     * Stack & toolchain auto-discovery (.NET, Node/TS, Python, Rust, Go) and default base branch resolution.
     * Execution environment configuration with mandatory Windows WSL2 verification (`IWslMetadataService`) and sandbox container setup.
     * Automated Ext4 mirror clone (`~/.nightcodeforge/wks/{workspaceId}`), directory scaffolding (`docs/tasks/`), markdown watcher registration, and default agent profile seeding.
     * Empty-state hero banners on Home/Ideation and persistent TopNavBar `[+]` quick-action button.
  3. Implement **SignalR Hub (`/hubs/orchestrator`)** streaming:
     * Live token output.
     * Agent tool executions (reading, writing, compiling).
     * Git CLI status updates.
     * Queue and worker status changes.
  4. Implement **Interactive Ideation & Codebase Discovery Chat View:**
     * Developer chat interface to ask questions about the repository.
     * ForgeTask proposal cards with editable scope and one-click "Add to Backlog" with dependency wiring.
  5. Implement **Backlog & Dependency DAG View:**
     * ForgeTask submission dialog (title, requirements, base branch, dependencies).
     * Dependency DAG graph / visual tree highlighting blocking relationships, lifecycle states (`Suggested`, `Open`, `Reviewed`, `Implemented`, `Ready`, `Closed`), and `WaitingForHuman` badges.
     * One-click maintainer promotion dialog: transition `Suggested` $\rightarrow$ `Open` with `Priority` (1..5) and `MergeMode` (`Automatic` vs. `ManualReviewRequired`), or `Rejected`.
  6. Implement **Monitoring View:**
     * Active worker dashboard: current task, active stage (`ForgeTask Review ➔ Deterministic Pre-Impl ➔ Implementation ➔ Deterministic Post-Impl ➔ Code Review ➔ Deterministic Post-Review`), elapsed time, GPU status.
     * Streaming terminal/log console with auto-scroll and log level filtering.
  7. Implement **Inbox View:**
     * Grid of questions awaiting developer answers.
     * Rich answering card displaying prompt, rationale, file context, and response form.
  8. Implement **Review Room:**
     * Visual `git diff` viewer highlighting added/modified/deleted files.
     * Action buttons: for `ManualReviewRequired` tasks, "Approve & Merge" (squashes WIP commits into base branch $\rightarrow$ `Closed`, unblocking dependent tasks) or "Request Changes" (re-routes back to Stage 3 `ImplementationStage` with reviewer feedback). Auto-merge tasks report automatic merge completion.
  9. Implement **Two-Tier Settings, Host SDKs, OllamaRouter Gateway, A/B Testing & Cloud/Web Search:**
     * Two-tier configuration hierarchy (Global System Settings vs Workspace Scoped Settings).
     * Agent Profile editor: configure Harness, Model, Thinking Effort, Temperature, generous autonomous turn budgets (500 turns), and 12-parameter optimization matrix.
     * Host SDK Inspector: probe and bind read-only toolchains and package cache mounts.
     * Deep OllamaRouter gateway integration: dynamic `/api/tags` catalog, `/api/ps` resident VRAM monitoring, target pinning (`X-OllamaRouter-Target`), and non-destructive memory reclaim.
     * Pluggable Cloud & Web Search settings (`WebSearchSettingsTab.razor`) supporting Ollama, SearXNG, and DuckDuckGo.
     * Empirical A/B testing comparator.
  10. Implement **Session Transcripts & AI Diagnostics:**
      * Markdown viewer for past and current execution transcripts with syntax highlighting and YAML frontmatter metadata.
      * Choice between "Diagnose with Cloud AI" (fast, zero VRAM impact) and "Diagnose with Local AI" (100% private, sequential execution).
  11. Implement **Untrusted Intake & Sandboxed Triage (Stage 0):**
      * Triage portal executing bug reproduction and backlog proposal generation inside a hardened `RestrictedReadOnly` sandbox (`--network none`, `:ro` workspace mount, `--read-only`, `--cap-drop=ALL`, `--user 1000:1000`).
  12. Implement **Ext4 Git Mirror & Host Sync:**
      * Working clone / Ext4 mirror inspector (`~/.nightcodeforge/wks/{id}`), active worktrees monitor, SignalR dirty host tree alert drawer, and guided `-ncf` rescue reconciliation.
  13. Implement **Dev-Queue & Concurrency Lock Inspector:**
      * Real-time Hangfire queue monitor (`llm-urgent` vs `llm-inference`), keyed `IWorkspaceLockProvider` concurrency lock inspection, emergency lock release, and mounted Hangfire dashboard.
* **Deliverables:** Fully operational web interface with real-time reactive updates.

---

### Phase 6: End-to-End Testing, Resilience & Hardening
> 📖 **Detailed Implementation Guide:** [docs/plan/06_END_TO_END_TESTING_AND_HARDENING.md](plan/06_END_TO_END_TESTING_AND_HARDENING.md)
* **Objective:** Validate the entire end-to-end flow under real-world conditions.
* **Tasks:**
  1. Complete workflow simulation test:
     * User creates ForgeTask A and dependent ForgeTask B via ideation chat (both created as `Suggested`).
     * Maintainer promotes ForgeTask A (`Priority = 1`, `MergeMode = ManualReviewRequired`) and ForgeTask B (`Priority = 2`, `MergeMode = Automatic`, depends on ForgeTask A) to `Open`.
     * ForgeTask A enters Stage 1 (ForgeTask Review) -> agent calls `ask` -> WIP checkpoint committed -> `IsWaitingForHuman = true` -> releases GPU worker cleanly.
     * Developer answers question -> status preserved, `IsWaitingForHuman = false` -> ForgeTask A resumes in `llm-urgent` with original agent profile and completes Stage 1 (`Reviewed`).
     * ForgeTask A advances through Stage 2 (`Deterministic Pre-Impl` baseline), Stage 3 (`ImplementationStage` $\rightarrow$ `Implemented`), Stage 4 (`Deterministic Post-Impl` coverage gate & warning ratchet), Stage 5 (`CodeReviewAndFixStage`), and Stage 6 (`Deterministic Post-Review Verification` $\rightarrow$ `Ready`).
     * ForgeTask A approved in Review Room -> squash-merged to `baseBranch` (`Closed`) -> ForgeTask B automatically unblocks and enters `llm-inference`.
  2. Resilience & cross-platform checks:
     * Abrupt process crash / host reboot simulation (e.g. Windows Update mid-inference): verify stale lock purge, rescue commit (`WIP-RECOVERY`), conversation rehydration, and automatic resumption via `llm-urgent`.
     * Windows WSL2 mandatory enforcement, Crucible Windows UNC path access (`\\wsl.localhost\...`), and container `/workspace` tool remapping.
     * Container shell hang, timeout cancellation, and sentinel REPL recovery (`\x03` SIGINT, grace period).
     * Host working tree dirty conflict fallback (`refs/heads/feature/task-{id}-ncf`).
     * Multi-workspace VRAM protection (`WorkerCount = 1`) and workspace concurrency locks (`IWorkspaceLockProvider`).
  3. Documentation updates, WSL2 setup guide, and quickstart instructions.
* **Deliverables:** Verified production-ready orchestrator.

---

### Phase 7: End-to-End Acceptance Test Plan (Playwright)
> 📖 **Detailed Implementation Guide:** [docs/plan/07_PLAYWRIGHT_END_TO_END_ACCEPTANCE_TEST_PLAN.md](plan/07_PLAYWRIGHT_END_TO_END_ACCEPTANCE_TEST_PLAN.md)
* **Objective:** Establish a 100% automated browser-driven Playwright acceptance test suite validating the complete autonomous software engineering lifecycle against a real, isolated synthetic repository.
* **Deliverables:** Fully automated Playwright E2E acceptance tests driving workspace provisioning, ideation chat, backlog promotion, 6-stage autonomous execution, deterministic gates, and host repository verification.

---

### Phase 8: Database-Driven Extensible Workflows & Unified Conversation Persistence
> 📖 **Detailed Implementation Guide:** [docs/plan/08_DATABASE_DRIVEN_WORKFLOWS_AND_STAGES.md](plan/08_DATABASE_DRIVEN_WORKFLOWS_AND_STAGES.md)  
> 📂 **Step Specifications:** [docs/plan/phase-8/](plan/phase-8/)
* **Objective:** Make workflows and stages extensible database entities, link each task to a workflow, seed the default standard 6-stage pipeline, dispatch to registered C# implementations with typed configuration payloads, and generalize `StageConversation` to persist interactive Ideation chats permanently in SQLite.
* **Tasks:**
  1. **Domain & Schema (Step 8.1):** Create `WorkflowDefinition`, `StageDefinition`, `AgenticStageConfig`, `DeterministicStageConfig`, and update `ForgeTask`, `Workspace`, and `StageConversation`.
  2. **Database Seeding (Step 8.2):** Implement `EnsureDefaultWorkflowsAsync()` to seed the standard 6-stage workflow and stage icons on clean database initialization.
  3. **Stage Registry & Factory (Step 8.3):** Build `IWorkflowStageRegistry` and `IWorkflowStageFactory` to instantiate C# stage handlers dynamically from JSON configs.
  4. **Orchestrator Pipeline Integration (Step 8.4):** Implement dynamic database-driven pipeline provider and update `TaskOrchestrator` for dynamic stage resolution and loopbacks.
  5. **Ideation Chat Persistence (Step 8.5):** Generalize `StageConversation` for workspace-level ideation chats, persisting messages, thinking traces, and tool calls.
  6. **Dynamic UI & Workflow Management (Step 8.6):** Update `PipelineStepper.razor`, `TaskExecutionInspector.razor`, add workflow selection in task creation, and provide a Workflows tab in Settings.
  7. **Comprehensive Hardening & Tests (Step 8.7):** Implement unit, repository, orchestrator, and web component tests across all layers.
* **Deliverables:** Fully extensible database-backed workflow engine and persistent ideation chat system.

---

### Phase 9: Codebase Remediation & Robustness
> 📖 **Detailed Implementation Guide:** [docs/plan/09_CODEBASE_REMEDIATION_AND_ROBUSTNESS.md](plan/09_CODEBASE_REMEDIATION_AND_ROBUSTNESS.md)  
> 📂 **Step Specifications:** [docs/plan/phase-9/](plan/phase-9/)
* **Objective:** Streamline the codebase and enforce strict architectural policies defined in `AGENTS.md`: fail-fast dependency injection, hard errors on missing database entities, centralization of magic strings/numbers, and clean-break pruning of dead legacy code.
* **Tasks:**
  1. **Constants, Enums & Literals (Step 9.1):** Introduce `StandardStageIds`, `PipelineDataBagKeys`, `StandardHarnessIds`, `StageImplementationTypes`, and `WorkflowDefaults`. Refactor raw literals across all layers. *(Completed)*
  2. **Database Integrity & Hard Errors (Step 9.2):** Disallow on-the-fly synthetic workflows and profiles; throw explicit exceptions (`WorkflowNotFoundException`, `AgentProfileNotFoundException`) when database records are missing. *(Completed)*
  3. **Clean Break & Dead Code Pruning (Step 9.3):** Prune obsolete deterministic stages (`DeterministicPreImplementationStage`, `DeterministicPostImplementationStage`, `DeterministicPostReviewStage`), redundant `SystemProcessRunner` subclass, legacy string prompt formatters, and dead sync-over-async interface methods. Format stage names dynamically from database `WorkflowStages` in `StageRunSelector` and `StageConversationViewer`.
  4. **Remove Non-Functional A/B Testing & Dead Navigation Relics (Step 9.4):** Delete `ABTestingComparatorTab.razor`, remove A/B testing tab from `Settings.razor`, rebrand vestigial "Settings & A/B" navigation links across `NavMenu`, `TopNavBar`, and `Home`, and prune unused `_dbContext` in `WorkspaceProvisioningService`. *(Completed)*
  5. **Standard Workflow Domain Extraction & Canonical Stage Identifiers (Step 9.5):** Extract standard 3-agent pipeline definition from `AppDbContext.EnsureDefaultWorkflowsAsync` into a dedicated `StandardWorkflowDefinitions` domain class. Add `StandardStageIds.Merge` and `StandardStageIds.Ideation` to eliminate magic strings across engine, activity streamers, and UI.
  6. **Required Constructor DI & Fail Fast (Step 9.6):** Remove `= null` defaults and fallback instantiations across all service constructors. Fix silent-success bug in deterministic quality gates on missing execution environments. Introduce reusable test fixture builders (`TestOrchestratorBuilder`).
  7. **DataBag Orphan Remediation & Quality Gate Handoff Integrity (Step 9.7):** Remediate orphan DataBag keys. Fix lost test diagnostics (`FailedTests`), empty warning ratchets (`WarningsNew`), deterministic execution tool logging (`StageCommand`, `StageOutput`), test metrics in Web UI (`TestsCurrentPassed`), and prune dead phantom keys.
  8. **AgentSessionContext Property Promotion & Harness Decoupling (Step 9.8):** Promote cross-cutting DataBag keys (`TaskMarkdownPath`, `ReviewDiff`, `ApprovedPlan`, `ReviewerFeedback`, `PriorDiagnostics`) to strongly-typed properties on `AgentSessionContext`. Wire git diff into Code Review prompts. Decouple `IAgentHarness` implementations from `PipelineDataBag`.
  9. **Resilient Crash Recovery, Database Conversation Rehydration & State Persistence (Step 9.9):** Upgrade `StartupRecoveryService` to resume interrupted agentic sessions from SQLite `Conversation` & `ConversationTurn` records with recovery notice; cleanly re-run deterministic steps with purged metrics; persist `PipelineDataBag` and mutable `AgentSessionContext` properties across turns in SQLite.
  10. **Silent Failure Elimination & Git Merge Safety (Step 9.10):** Eliminate bare `catch { }` blocks without logging; ensure fail-fast semantics on Git branch checkouts, cleans, resets, and prompt overrides.
  11. **Quality Gate Correctness & Metric Fidelity (Step 9.11):** Record baselines only from successful runs; fail on zero tests discovered; aggregate multi-project Coverlet code coverage; format code automatically via `DeterministicFormatStage`.
  12. **Async Lifecycle, Cancellation & Residual Policy Violations (Step 9.12):** Eliminate `async void` handlers, unobserved fire-and-forget tasks, and shell-escaping injection risks.
  13. **Infrastructure Constructor DI & Options Remediation (Step 9.13):** Eliminate residual optional constructor parameters and fallback initializations across `NightCodeForge.Infrastructure` and `NightCodeForge.Agents`.
  14. **Workspace Lock Hierarchy & Cancellation Coordination (Step 9.14):** Coordinate workspace-level exclusive locking with concurrent task executions; propagate cancellation tokens to active orchestrators.
  15. **Web UI State Synchronization & Circuit Resilience (Step 9.15):** Synchronize `Settings.razor` and `Review.razor` with `WorkspaceState.OnWorkspaceChanged`; remediate Task Execution Inspector live telemetry desynchronization on stage start/complete.
  16. **Bidirectional Task Markdown Synchronization, Workspace Hygiene & Audit Trail Anticipation (Step 9.16):** Synchronize all active workspace tasks to `docs/tasks/` in agent worktrees before launch; scan and sync back modified/new tasks to database on stage completion; anticipate Step 10.6 with `ForgeTaskHistory` entity and audit recording; ensure `*.trx` and `TestResults/` in `.gitignore`.
  17. **Diff Stat Review Prompts & Multi-Question Clarification Rework (Step 9.17):** Inject concise fenced `git diff --stat` into Code Review / Fix bootstrap prompts instead of full raw diffs; rework `ask` tool, `HumanInterruptionException`, and orchestrator to support structured multi-question batches; direct review agents to consolidate all questions into a single `ask` call.
  18. **Agentic Stage Summary Fidelity & Stepper Readability Polish (Step 9.18):** Ensure agent stage summary represents strictly the final assistant message (not all concatenated turn text); drop `DisplayName` and `StatusText` from pipeline stepper bar, displaying clean status circles with comprehensive tooltips.
* **Deliverables:** Streamlined, robust, fail-fast codebase strictly adhering to `AGENTS.md` guidelines.

---

### Phase 10: MVP Final Polish, EF Migrations & Codebase Hardening
> 📖 **Detailed Implementation Guide:** [docs/plan/10_MVP_FINAL_POLISH.md](plan/10_MVP_FINAL_POLISH.md)  
> 📂 **Step Specifications:** [docs/plan/phase-10/](plan/phase-10/)
* **Objective:** Complete the final three hardening tracks before building the initial v1 MVP release: transition schema lifecycle to EF Core migrations, establish namespace-level documentation for subagents and developers, and execute a comprehensive 360° code review enforcing AGENTS.md policies.
* **Tasks:**
  1. **Production EF Core Migrations (Step 10.1):** Add `Microsoft.EntityFrameworkCore.Design` to startup project, replace `Database.EnsureCreatedAsync()` with `Database.MigrateAsync()`, preserve SQLite WAL pragmas and sequence seeds, and automate migration integration tests.
  2. **Namespace Documentation Architecture (Step 10.2):** Generate structured, concise `README.md` files across all functional namespaces in Core, Infrastructure, Agents, Web, and Tests following a standard 5-section template.
  3. **Comprehensive Code Review & Policy Hardening (Step 10.3):** Execute multi-stream audits for bare catch blocks, required constructor DI, missing entity hard errors, DbContext thread lifetimes, process/resource cleanup, prompt cache stability, and Blazor component disposal. Remediate all High/Critical findings and verify zero warnings.
* **Deliverables:** Operational EF Core migration tooling, comprehensive namespace documentation hierarchy, zero AGENTS.md policy violations, zero compiler warnings, and 100% green test suites.

---

### Phase 11: Real-Project Onboarding, Hardening & Bug Fixes (Completed)
> 📖 **Detailed Implementation Guide:** [docs/plan/11_EXTEND_FEATURES.md](plan/11_EXTEND_FEATURES.md)  
> 📂 **Step Specifications:** [docs/plan/phase-11/](plan/phase-11/)
* **Objective:** Stabilize active real-world projects (OllamaRouter, ComicGenCLI) transitioning to NightCodeForge with direct task authoring, priority scheduling, deterministic onboarding tooling, temporal audit history, telemetry console, and deep system hardening across Git worktree concurrency, conflict resolution, container lifecycle, and UI state hygiene.
* **Tasks & Completed Deliverables:**
  1. **Dedicated Task Creation Page, Priority & MergeMode (Step 11.2):** Dedicated task creation page (`/tasks/new`), first-class editable Priority management (P1–P5), and MergeMode lifecycle.
  2. **Task State History & Audit Trail (Step 11.7):** Temporal snapshot history with `TaskHistories` table, immutable revisions, and `TaskHistoryTimeline.razor` UI with DiffPlex text diff comparison.
  3. **Packaged Agent Skills (Step 11.12):** Versioned skills (`ncf-backlog`, `ncf-backlog-migrator`, `ncf-project-readiness`), prompt templates, and flexible `depends_on` YAML frontmatter synchronization.
  4. **`ncf` CLI — Deterministic Project Preparation & Onboarding (Step 11.13):** NuGet `dotnet tool` (`ncf init` / `ncf audit`) with 5-pillar project readiness audit and automated config bootstrap.
  5. **Priority-Aware Task Queue Scheduling & Ordering (Step 11.15):** Cascade unblocking ordered by `Priority ASC (1 -> 5)`, P1 routing to `llm-urgent`, and DevQueue priority sorting and manual promotion.
  6. **Portal Task Scoping, Origin Provenance & Pipeline State Hygiene (Step 11.17):** Explicit `TaskOrigin` tracking (`UntrustedIntake`), scoping `/triage` and `/inspector` via `HasExecutionRun`, and eliminating false "Running" stepper indicators on idle tasks.
  7. **Live Operations Telemetry: Real-Time LLM Calls, Shell Activity & OllamaRouter Deep Monitoring (Step 11.18):** Operations telemetry service with circular buffer and live operations console in `/monitoring`.
  8. **Live Operations Telemetry Refinement, Command Transparency & Context Sizing (Step 11.20):** 500ms dynamic ticking timer, real command logging in container executor, and full prompt context sizing.
  9. **Dynamic Tool Timeouts, Container ICU & User Permissions, and Review Rejection Test Mandate (Step 11.21):** Dynamic tool timeout $\max(300\text{s}, \text{build} + \text{test})$, container ICU and non-root host UID/GID preservation, and mandatory test authoring contract on review rejection.
  10. **Container Git Worktree Relative Paths & Cross-Environment Portability (Step 11.22):** Bidirectional relative Git worktree pointers (`<worktree>/.git` and `<clone>/.git/worktrees/.../gitdir`), UNC path resolution, and `worktree.useRelativePaths = true`.
  11. **Backlog Task Rejection Lifecycle, Kanban State Hygiene & Shell UI Polish (Step 11.23):** First-class backlog rejection action, white text contrast for priority chips, decoupled stage IDs for unstarted tasks, sidebar collapse fix, and SVG favicon.
  12. **Fine-Grained Workspace Concurrency & Non-Blocking Git Merge Operations (Step 11.24):** Decoupled task execution semaphores from workspace reader lock, keeping atomic canonical lock $< 500\text{ms}$ and enabling instant Review Room squash-merge.
  13. **Autonomous Git Conflict Management, Baseline Synchronization & Re-Review Lifecycle (Step 11.25):** Conflict-free base branch sync at Stage 2 Pre-Implementation Baseline, non-destructive merge preview (`git merge-tree`) before Ready, atomic canonical squash rollback on conflict, and autonomous conflict resolution by the Implementation Agent.
  14. **Container Reuse Validation & Task Lifecycle Sandbox Cleanup (Step 11.26):** Full mount and image drift detection in container reuse, task terminal state container teardown (`Closed`, `Rejected`), and startup orphan container reconciliation sweeps.
  15. **Prompt Deduplication, Dynamic Compaction Budgeting & Inference Ergonomics (Step 11.31):** Single-source task descriptions in System Prompt Layer 3, mathematically balanced dynamic compaction budgets and turn debounce (stopping 80+ compaction passes per task), transcript compaction diagnostics dumping, multi-match `edit` tool replacements, and transcript sizing resilience.
  16. **Synthetic Conversation Priming & Human Clarification Answer Hygiene (Step 11.32):** Scope isolation and single-consumption lifecycle for human clarification answers (`LastHumanAnswer`), defensive orphan tool result prevention in `CrucibleAgentHarness`, and implementation of Synthetic Conversation Priming (pre-seeding `ChatHistory` with verified or failing `bash` tool call/result pairs from prior deterministic quality gates to eliminate redundant exploratory build/test agent turns).
  17. **Unified LLM Provider Architecture, Auto Context Window Discovery & Fleet Telemetry (Step 11.33):** Zero OllamaRouter imposition for single-GPU developers (standalone Ollama support), simplified global configuration, automated context window discovery from Modelfiles/GGUF/router engines, and dynamic fleet topology telemetry.
  18. **Live Git Diff Worktree Ingestion & Untracked File Tracking (Step 11.34):** Cross-platform worktree path resolution for live diffs, intent-to-add (`git add -N`) indexing for untracked files, and explicit diff provenance indicators (`LiveWorktree`, `BranchHeadFallback`, `SquashCommit`).
  19. **OpenAI-Compatible LLM Provider Integration & Explicit Model Allowlist (Step 11.35):** Standard OpenAI Chat Completions API provider (`/v1/chat/completions`) for local (vLLM, LM Studio) and cloud (OpenRouter, DeepSeek, OpenAI) endpoints, mandatory allowed model list with per-model context sizes, fail-fast unlisted model validation (`ModelNotAllowedException`), deterministic compaction sizing, and clean Bearer authentication.
* **Deliverables:** Fully operational and hardened multi-task execution pipeline with zero git worktree corruption, instant non-blocking merges, full container lifecycle management, active project onboarding capabilities, prompt deduplication, synthetic conversation priming, unified multi-provider LLM support, and live git diff accuracy.

---

### Phase 12: Extend Features & Integrations (Post-MVP)
> 📖 **Detailed Implementation Guide:** [docs/plan/12_EXTEND_FEATURES.md](plan/12_EXTEND_FEATURES.md)  
> 📂 **Step Specifications:** [docs/plan/phase-12/](plan/phase-12/)
* **Objective:** Expand NightCodeForge with multi-task dependency intelligence, context attachment ingestion, developer ergonomics, dual model cost/performance optimization, and ecosystem integrations (MCP, GitHub, Mobile/PWA, Custom Scripts).
* **Tasks & Strategic Deliverables:**
  1. **Intelligent Task Dependency Detection in Ideation & Review Agents (Step 12.1 - Top Priority):** Backlog context injection for Ideation and Stage 1 Review agents; system prompts requiring identification of prerequisites, shared interfaces, and ordering; auto-population of `dependsOnTitles` and DAG dependency chips in Kanban and Drawer. *(formerly Step 11.14)*
  2. **Task Attachments & Context Documents (Step 12.2 - Top Priority):** Workspace-level read-only `attachments/` store populated from file drops, cited host paths, and URL captures (MarkItDown companions), citable by ideation (`@attachment:{id}`) and readable by workflow stage agents. *(formerly Step 11.16)*
  3. **Conversational UI Unification, Review Room & Task Detail Ergonomics, Inspector Grouping & Running State Hygiene (Step 12.3 - Top Priority):** Reusing `AgentQuestionTurnCard` in the Question Inbox, fixing stepper false "Pending" state during active runs, unifying chat bubble layout, sticky navigation, hierarchical directory tree view, and simple syntax highlighting in the Review Room, in-flight task display in live telemetry, `<optgroup>` status grouping in Task Inspector, strict pending reviews filtering in Review Room, dedicated "Changes" diff tab in Task Details with "Open in Review Room" button, and pulsing running markers on Kanban cards. *(formerly Step 11.19)*
  4. **Ideation Quick Notes, Suggestions Backlog & Conversational Refinement (Step 12.4):** Quick notes backlog intake ("Think Now, Chat Later"); proposal card lifecycle (`Approve`, `Keep as Suggestion`, `Discard`); "Refine in Ideation" pre-seeded chat; scoped `edit_task` tools; 24h draft pruning. *(formerly Step 11.5)*
  5. **Heterogeneous Dual Model Role Configuration & Difficulty Escalation (Step 12.5):** Heterogeneous model architecture: Architect/Reviewer model for ideation, review, and triage; Coder model for fast coding; dynamic difficulty escalation for difficult/expert tasks. *(formerly Step 11.1)*
  6. **Smart CLI & Build Tool Output Filtering (Step 12.6):** Semantic output filtering for verbose CLI tools (`dotnet build`, `msbuild`, `dotnet test`, `npm`). Relativizes paths, strips SDK boilerplates, drops non-fatal warnings when errors exist, preserving raw transcripts on disk. *(formerly Step 11.3)*
  7. **Free & Open-Source SAST Gate via Microsoft DevSkim CLI (Step 12.7):** Deterministic SAST quality gate using Microsoft DevSkim CLI across C#, JS/TS, Python, Go. SARIF report parsing, configurable severity thresholds, and automatic loopback routing with diagnostics. *(formerly Step 11.4)*
  8. **Mobile-Responsive UI, PWA & Web Worker Notifications (Step 12.8):** First-class mobile/tablet responsiveness: off-canvas hamburger navigation drawer, touch targets $\ge 44\text{px}$, fixed-bottom ideation chat, mobile bottom sheet drawer, installable PWA (`manifest.webmanifest`, `service-worker.js`), and browser web notifications (dispatched via Web Worker / Service Worker on mobile) for human intervention and task merges with per-subject preferences. *(formerly Step 11.6)*
  9. **Custom Repository Scripts for Deterministic Steps (Step 12.9):** Execution of custom repository scripts in `.nightcodeforge/scripts/` during deterministic stages across Bash, Python, and PowerShell. Injection of `PipelineDataBag` environment variables and output directives. *(formerly Step 11.8)*
  10. **Model Context Protocol (MCP) Integration (Step 12.10):** First-class MCP discovery and execution via official Microsoft Agent Framework packages (`ModelContextProtocol`), supporting streamable HTTP/SSE remote servers and containerized stdio servers. *(formerly Step 11.9)*
  11. **GitHub Integration (Step 12.11):** Sandboxed issue triage, Pull Request delivery mode with rich PR metadata, and bi-directional status mirroring. *(formerly Step 11.10)*
  12. **Voice Support & Continuous Dictation (Step 12.12):** Hands-free voice operations via Web Speech API (`SpeechRecognition`) and Blazor JS Interop, continuous dictation with automatic 1500ms silence detection, and interactive visual audio feedback. *(formerly Step 11.11)*
  13. **Remote Tailscale Mesh Access, HTTPS & Transient Network Resilience (Step 12.16):** Zero-firewall private mesh VPN access, stable service identity (`svc:ncf` resolving to `https://ncf.<tailnet>.ts.net` with automated `advertise` and graceful `drain`), automated MagicDNS Let's Encrypt TLS termination via managed `TailscaleServeHostedService`, graceful shutdown cleanup (`tailscale serve clear` / `reset`) with zero desktop overhead when disabled, WebSocket reverse proxying for Blazor Interactive Server and SignalR telemetry, and transient network reconnection handling.
  14. **Follow Trail Auto-Scroll & Bottom-Right Action in Task Execution Inspector & Reasoning Trace (Step 12.17):** Universal reasoning trace auto-scroll tracking in Ideation Chat and Task Execution Inspector, mounting a floating follow-trail button at the bottom-right of `StageConversationViewer`, dual-tier pinning of stream and open `.think-content`, non-intrusive decoupling on manual upward scroll, and bUnit test coverage.
  15. **Air-Gapped & Offline Web Application Hardening (Step 12.19 - Quick Win):** Complete eradication of external Google Fonts and CDN dependencies (`https://fonts.googleapis.com`), bundled local Inter and JetBrains Mono WOFF2 typography with native system fallbacks, strict Content Security Policy (CSP) restricting subresources to `'self'`, zero-telemetry guarantee, and automated static analysis subresource tests in CI.
  16. **Air-Gapped Search Deactivation & MarkdownFetch Documentation Governance (Step 12.20):** Automatic deactivation of `web_search` and outbound requests to `ollama.com` in Air-Gapped Mode (until local offline search exists), explanatory UI warning banner and disabled inputs in `WebSearchSettingsTab.razor`, fail-fast `AirGappedOperationException` guardrails in agent tool dispatchers, in-app setup UI and configuration pipeline for `mdfetch` (`Pmad.MarkdownFetch`) allowed URL whitelisting (`--url-whitelist`, `--safety-config`), hardcoded defense against SSRF and cloud metadata endpoint access (`169.254.169.254`), and integration with global skills execution ([Step 12.14](plan/phase-12/STEP_12_14_GLOBAL_SKILLS_INJECTION_AND_STACK_TOOLING.md)).
  17. **Container Sandbox Egress Filtering & Istio-Style Envoy Proxy (Step 12.21):** Istio-style transparent container egress redirection using Linux kernel `iptables` rules and shared network namespaces (`--net=container:ncf-envoy-task<id>`), eliminating reliance on easily bypassable `HTTP_PROXY` environment variables; zero-decryption TLS SNI domain filtering via Envoy Proxy (`tls_inspector` listener filter) on port 443 with `ORIGINAL_DST` forwarding and TCP RST drop on unapproved domains; fallback explicit proxy mode for restricted container runtimes; in-app egress profile management (`/settings`); and real-time egress audit telemetry streaming into `/monitoring` live operations telemetry ([Step 11.18](plan/phase-11/STEP_11_18_LIVE_OPERATIONS_TELEMETRY.md)).
  18. **Cross-Platform Desktop Installer, Updater & Setup Wizard (Step 12.18):** Standalone Avalonia UI desktop application across Windows, Linux, and macOS delivering a zero-clone, zero-scripting out-of-the-box experience; automated host/WSL2/Docker/.NET provisioning with JIT privilege elevation, in-wizard micro-benchmark finding optimal mix of curated `jetelain/*` Unsloth Dynamic models with strict $\ge 128\text{k}$ context guarantee, OllamaRouter mesh fleet setup, and ongoing software updater hub.
* **Deliverables:** Intelligent multi-task dependency discovery, comprehensive attachment ingestion, unified modern UI, high-efficiency output compression, SAST security gates, mobile/voice support, MCP/GitHub/Tailscale ecosystem integrations, cross-platform desktop installer, fully air-gapped web application self-containment, and unbypassable container egress whitelist governance.






