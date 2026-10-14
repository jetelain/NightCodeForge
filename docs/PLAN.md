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
│       └── 06_END_TO_END_TESTING_AND_HARDENING.md
├── src/
│   ├── NightCodeForge.Core/
│   │   ├── Domain/              # Entities: Issue, IssueDependency, Question, StageCheckpoint, ExecutionLog
│   │   ├── Enums/               # IssueOverallStatus, HarnessType, LogLevel, QuestionType
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
│   │   ├── Tools/               # Minimalist Canonical (Pi-Harmonized): read, edit, write, bash, web_search, ask, propose_issues
│   │   │   └── EditorConfig/    # IEditorConfigService, EditorConfigParser, EditorConfigFileApplier (EOL, indent, tab/space, whitespace, charset)
│   │   ├── Dispatcher/          # LenientToolDispatcher (Aliasing, fuzzy matching, parameter normalization, payload repair)
│   │   ├── Stages/              # Agent Stages:
│   │   │   ├── TriageStage.cs            # Stage 0: Untrusted external intake, read-only pre-analysis on main/master
│   │   │   ├── IssueReviewStage.cs       # Stage 1: Scope, ambiguities, proactive human Q&A
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
│       │   ├── Ideation/        # Interactive Codebase Discovery Chat & Issue Proposer
│       │   ├── Backlog/         # Issue creation, dependency DAG visualizer, priority reordering
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
| **Phase 1** | [docs/plan/01_FOUNDATION_DOMAIN_AND_WORKFLOW.md](plan/01_FOUNDATION_DOMAIN_AND_WORKFLOW.md) | .NET 10 solution setup, domain entities (`IssueOverallStatus` from `Suggested` to `Closed`, `MergeMode`), DAG dependency engine, workspace concurrency lock, extensible workflow engine with `PipelineDataBag`, differential quality gates (`DeterministicCoverageGate`), and `docs/issues/` markdown sync. |
| **Phase 2** | [docs/plan/02_GIT_SUBSYSTEM_AND_EXT4_MIRROR.md](plan/02_GIT_SUBSYSTEM_AND_EXT4_MIRROR.md) | Native Git CLI (`CliWrap`), branch isolation, WIP commits, noise-filtered diffs (`-U2`), working clone at `~/.nightcodeforge/wks/{id}`, remote path translation (`/mnt/...`), bare host (Unix) vs plain WSL2 (Windows) Git execution, and dirty host tree fallback. |
| **Phase 3** | [docs/plan/03_QUEUE_AND_ORCHESTRATION_ENGINE.md](plan/03_QUEUE_AND_ORCHESTRATION_ENGINE.md) | Hangfire single-worker GPU queue (`WorkerCount = 1`, `llm-urgent` vs `llm-inference`), clean `WaitingForHuman` interruption lifecycle, cascade unblocking when dependencies are `Closed`, and startup crash recovery (`IStartupRecoveryService`). |
| **Phase 4** | [docs/plan/04_HARNESSES_TOOLS_AND_LOCAL_OPTIMIZATIONS.md](plan/04_HARNESSES_TOOLS_AND_LOCAL_OPTIMIZATIONS.md) | Unified harness abstraction, OllamaRouter integration, canonical tools & `EditorConfigFileApplier`, container sandbox sentinel REPL, Crucible / Pi / ACP engines, modular skills & MCP, and the 7 local LLM optimizations (`LocalOptimizationFlags`). |
| **Phase 4B** *(Intermediate)* | [docs/plan/04B_EXECUTION_ENVIRONMENT_AND_WORKFLOW_ALIGNMENT.md](plan/04B_EXECUTION_ENVIRONMENT_AND_WORKFLOW_ALIGNMENT.md) | **Cross-Platform & Lifecycle Realignment:** Unix-only agent enforcement, Windows WSL2 mandatory requirement, working clones at `~/.nightcodeforge/wks/{id}`, Windows Crucible UNC path access (`\\wsl.localhost\...`), bare mode bash in WSL2, container mode `/workspace` tool call remapping, on-the-fly slim container images, 3-channel issue intake (`Suggested`), manual promotion (`Open` / `Rejected`) with `Priority` & `MergeMode`, and main workflow status progression (`Reviewed` $\rightarrow$ `Implemented` $\rightarrow$ `Ready` $\rightarrow$ `Closed`). |
| **Phase 5** | [docs/plan/05_BLAZOR_WEB_UI_AND_TELEMETRY.md](plan/05_BLAZOR_WEB_UI_AND_TELEMETRY.md) | Blazor Interactive Server UI, obsidian/indigo dark theme, SignalR telemetry hub, ideation chat, visual DAG backlog with Suggested/Open promotion actions, question inbox, review room with diffs and manual merge triggers, and A/B testing comparator. |
| **Phase 6** | [docs/plan/06_END_TO_END_TESTING_AND_HARDENING.md](plan/06_END_TO_END_TESTING_AND_HARDENING.md) | End-to-end workflow simulation, chaos & sudden reboot resilience, multi-workspace VRAM protection validation, and empirical A/B benchmark validation. |

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
     * `Issue` (Id, WorkspaceId, Title, Description, Status [defaults to Suggested], CurrentStageId, Priority, MergeMode [Automatic, ManualReviewRequired], BaseBranch, FeatureBranch, PipelineDataJson [cross-stage persistent data bag]).
     * `IssueDependency` (IssueId, DependsOnIssueId).
     * `Question` (IssueId, StageId, Prompt, Rationale, OptionsJson, Answer, Status).
     * `StageCheckpoint` (IssueId, StageId, SerializedMemory, GitCommitHash).
     * `StageExecutionRecord` (IssueId, StageId, AgentProfileId, HarnessId, ModelName, StartedAt, CompletedAt, DurationMs, PromptTokens, CompletionTokens, TotalTokens, TargetRouting, Status, OutputDataJson [stage output data snapshot]).
     * `ExecutionLog` (IssueId, StageId, Timestamp, Level, Source, Message).
     * Enums: `IssueOverallStatus` (`Suggested`, `Rejected`, `Open`, `Reviewed`, `Implemented`, `Ready`, `Closed`, `Cancelled`, `Failed`), `MergeMode` (`Automatic`, `ManualReviewRequired`), `ExecutionMode`, `ThinkingEffort`, `HarnessType`. Dedicated interruption fields: `Issue.IsWaitingForHuman`, `Issue.InterruptedAgentProfileId`, `Question.AgentProfileId`.
  3. Implement `DependencyResolutionEngine`:
     * Topological sorting of issues per workspace.
     * Cycle detection (reject circular dependencies).
     * Dependency readiness evaluation: `IsReadyForExecution(issueId)` (requires `Status == Open` and all dependencies `Status == Closed`).
     * Cascade activation when a prerequisite reaches `Closed`.
  4. Implement **Workspace Concurrency Control (`IWorkspaceLockProvider`):**
     * Keyed async lock provider (`AsyncKeyedLock<int>` by `WorkspaceId`).
     * Guarantees that while the global inference queue processes sequentially (`WorkerCount = 1`), background utility jobs (`WorkerCount = 4`) and user UI actions do not execute concurrent Git operations or index modifications on the same workspace, preventing `.git/index.lock` race conditions.
  5. Implement `ExtensibleWorkflowEngine`:
     * `IWorkflowPipeline` and `IWorkflowStage` abstractions supporting both **Agentic** (LLM-driven with GPU allocation) and **Deterministic** (CPU-native builds, tests, SAST with 0 GPU/tokens) stages via `WorkflowStageKind`.
      * **`PipelineDataBag` Cross-Stage Data Persistence:** Thread-safe, serializable key-value store in `PipelineContext` (persisted to `AppDbContext.Issues.PipelineDataJson`) enabling stages to record baseline metrics and share state across pipeline executions.
      * **Deterministic Quality Gates & Differential Outcome Routing:**
        - `DeterministicBuildStage`, `DeterministicTestStage`, `DeterministicSastStage`.
        - **`DeterministicCoverageGate`:** Captures baseline code coverage (e.g. via Coverlet / Cobertura XML) on base branch and evaluates differential after implementation. Fails and loops back if coverage degrades below tolerance or if total test count decreases ("anti-cheating" detection).
        - **`DeterministicWarningRatchetStage`:** Captures baseline compiler/linter warning count; fails if newly introduced warnings exceed baseline ($\Delta > 0$).
        - Advances forward on pass, or loops backward to Stage 2 with structured diagnostic artifacts and **Synthetic Conversation Priming** to prevent agents from re-running redundant builds/tests.
     * **Explicit Stage Boundary Handover Protocol:** Enforces zero-memory clean context resets at stage transitions (archives previous stage chat history to disk transcripts, injecting only structured plan/diff/diagnostic artifacts into next stage).
     * Built-in `Default3AgentPipeline`:
       1. `IssueReviewStage` (Agentic - Default Profile: Deep Reasoning / high thinking effort)
       2. `ImplementationStage` (Agentic - Default Profile: Fast Coder / medium thinking effort)
       3. `CodeReviewAndFixStage` (Agentic - Default Profile: Strict Auditor / low temperature)
     * Extensible pipeline configurations allowing deterministic gate insertion (e.g. `Review ➔ Implementation ➔ Deterministic Build & Test ➔ Review & Fix`).
  6. Implement **`IssueMarkdownSyncService`**:
     * Generates and updates Markdown files with YAML frontmatter in `<WorkspaceRoot>/docs/issues/YYYY-MM-DD-<slug>.md` (date-prefixed without ID in filename to enable clean sorting and avoid renames; appends `-2`, `-3` on slug collisions for the same date).
     * Automatically syncs question/answer logs, stage transitions, and generated plans to disk.
     * Employs `FileSystemWatcher` event debouncing (300ms window) and an in-memory write-suppression tracker (recording expected file hashes) to prevent self-triggering echo loops when writing back protected fields.
     * Enforces field protection: user edits to title, body, priority, merge_mode, profile, and dependencies sync to DB; system-managed fields (`status`, `stage`, `id`) are protected and restored if altered.
     * Detects dropped new files, allocates a new database ID, and injects `id` into frontmatter in-place with initial `status: suggested` and `merge_mode: manual_review_required`.
     * Cleans up or archives files when issues reach `Closed` or `Cancelled`.
  7. Configure `AppDbContext` with SQLite and create initial migrations.
  8. Unit tests in `NightCodeForge.Core.Tests` for pipeline progression, pause/resume, token/duration tracking, DAG dependency graph resolution, echo loop suppression, and markdown issue sync.
* **Deliverables:** Solution builds cleanly; workflow pipeline, metrics tracking, DAG dependency, and markdown sync unit tests pass 100%.

---

### Phase 2: Git CLI Subsystem & Ext4 Workspace Mirroring (CliWrap)
> 📖 **Detailed Implementation Guide:** [docs/plan/02_GIT_SUBSYSTEM_AND_EXT4_MIRROR.md](plan/02_GIT_SUBSYSTEM_AND_EXT4_MIRROR.md)
* **Objective:** Deliver a bulletproof Git manager using native CLI only (no LibGit2Sharp) capable of safe context switching, WIP checkpointing, and cross-filesystem Git mirroring (solving the 9p latency trap on Windows/WSL2).
* **Tasks:**
  1. Implement `GitCliRunner` wrapping `CliWrap` with command execution, timeout policies, stdout/stderr capture, and typed `GitCommandException`.
  2. Implement `GitWorkspaceManager`:
     * `EnsureRepositoryValidAsync(string repoPath)`: Validates target working tree.
     * `CheckoutIssueBranchAsync(Workspace workspace, int issueId, string baseBranch)`: Branch creation (`feature/issue-{id}`) and checkout.
     * `CreateWipCheckpointAsync(Workspace workspace, int issueId, string stageName, string questionSummary)`: Performs `git add -A` and `git commit -m "WIP: [Issue #{id}][Stage: {stageName}] {summary}" --no-verify`.
     * `GenerateDiffAsync(Workspace workspace, int issueId, string baseBranch)`: Executes `git diff {baseBranch}...feature/issue-{id}`.
     * `RevertOrCleanAsync(Workspace workspace)`: Handles dirty states during crashes.
     * `SquashAndMergeAsync(Workspace workspace, int issueId, string baseBranch, string finalMessage)`: Squashes WIP commits upon approval.
  3. Implement **`GitMirrorSyncService` (Cross-Environment Working Clone Protocol):**
     * Agent clone repositories, where agents work, will ALWAYS be `~/.nightcodeforge/wks/{id}` (ext4 in WSL2 on Windows, host home on Unix).
     * On Windows, the remote pointing to the developer's workspace is `/mnt/{windows_path_transformed}`; on Unix, the remote is the raw developer workspace path.
     * Git push, pull, and merge operations on `main` are ALWAYS executed on the bare host on Unix, and on plain WSL2 on Windows.
     * On Windows, Crucible tools access working tree files via `\\wsl.localhost\{distrib}\home\{username}\.nightcodeforge\wks\{id}\` for high-speed I/O.
     * Configures the developer host repository with `git config receive.denyCurrentBranch updateInstead` to ensure `git push host-origin feature/issue-{id}` succeeds without rejection even when checked out in the host IDE.
     * **Host Working Tree Dirty Conflict Fallback:** If the developer has uncommitted changes to tracked files in their host IDE, Git rejects `updateInstead`; `GitMirrorSyncService` catches the rejection, pushes to `refs/heads/feature/issue-{id}-ncf` instead, and emits a SignalR alert informing the developer to reconcile local uncommitted files.
     * Performs `git fetch host-origin <baseBranch>` on stage inception.
     * Performs `git push host-origin feature/issue-{id}` upon WIP checkpointing, human questions, or stage completion to keep developer's IDE in sync.
  4. Create integration tests in `NightCodeForge.Infrastructure.Tests` executing real Git commands on dynamically created temporary directories and verifying local remote fetch/push mirror sync.
* **Deliverables:** Git operations verified with automated tests covering branch switching, dirty detection, WIP commits, diff generation, and bidirectional mirror synchronization.

---

### Phase 3: Hangfire Queue & Orchestration Engine
> 📖 **Detailed Implementation Guide:** [docs/plan/03_QUEUE_AND_ORCHESTRATION_ENGINE.md](plan/03_QUEUE_AND_ORCHESTRATION_ENGINE.md)
* **Objective:** Configure the single-worker GPU inference queue, step metrics recording, and background task coordinator.
* **Tasks:**
  1. Configure Hangfire with SQLite storage.
  2. Register two background job servers:
     * **LLM Inference Server:** `WorkerCount = 1`, `Queues = new[] { "llm-urgent", "llm-inference" }` (global across all workspaces to guard GPU VRAM; provides non-preemptive priority pick-up so resumed issues in `llm-urgent` are prioritized upon the next worker turn without concurrent VRAM oversubscription).
     * **Utility Server:** `WorkerCount = 4`, `Queues = new[] { "default" }`.
  3. Implement `IssueOrchestrator`:
     * Dequeues issue, checks that `Status == Open` and all dependencies `Status == Closed`.
     * Acquires workspace lock via `IWorkspaceLockProvider` and checks out branch.
     * Loads current stage in the pipeline and invokes the selected harness (`IAgentHarness`).
     * Measures step performance (start/end timestamp, duration in ms, token usage).
     * Evaluates `StageExecutionResult`:
       * If `WaitingForHuman`: records `Question` (with `AgentProfileId`), `StageCheckpoint`, and `StageExecutionRecord`, performs Git WIP commit, sets `IsWaitingForHuman = true` and `InterruptedAgentProfileId = profile?.Id` without mutating `issue.Status`, and cleanly completes the Hangfire job (`Succeeded`) to release the worker without false error alerts.
       * Stage Transitions:
         - Stage 1 (Issue Review) completed $\rightarrow$ sets `Status = Reviewed`.
         - Stage 2 (Deterministic Pre-Implementation) captures baseline metrics.
         - Stage 3 (Implementation) completed $\rightarrow$ sets `Status = Implemented`.
         - Stage 4 (Deterministic Post-Implementation) verifies coverage and warnings.
         - Stage 5 (Code Review) applies review patches.
         - Stage 6 (Deterministic Post-Review Verification) tests review patches; on pass $\rightarrow$ sets `Status = Ready`; on failure $\rightarrow$ loops back with compiler/test diagnostics.
         - Final Merge Gate: If `MergeMode == Automatic`, executes squash & merge, sets `Status = Closed`, and cascades unblocking. If `MergeMode == ManualReviewRequired`, notifies UI and awaits maintainer approval before merging $\rightarrow$ `Closed`.
       * If `Failed`: logs error and metrics, transitions state to `Failed`.
  4. Implement `SubmitHumanAnswerAsync`:
     * Saves answer in DB (`question.Status = QuestionStatus.Answered; question.Answer = answer;`).
     * Sets `issue.IsWaitingForHuman = false` (preserving `issue.Status` intact).
     * Enqueues job to `llm-urgent` queue for immediate priority resumption with the exact interrupted agent profile (`InterruptedAgentProfileId`).
  5. Implement `RequestChangesAsync`:
     * Allows reviewer to provide revision feedback notes from the Review Room.
     * Transitions issue from `Ready` back to Stage 3 (`ImplementationStage`) with reviewer feedback injected into prompt context.
     * Enqueues job to `llm-urgent` queue for priority iteration.
  6. Implement `CompleteIssueAndCascadeAsync`:
     * On final approval/merge, sets issue to `Closed`.
     * Unlocks dependent issues in the same workspace (where all dependencies have reached `Closed`) and enqueues newly ready `Open` issues into `llm-inference`.
  7. Implement `IStartupRecoveryService` (Graceful Restart & Windows Update Recovery):
     * Executes during host startup before Hangfire worker begins polling.
     * Purges dangling Git lock files (`.git/index.lock`, `.git/refs/...`) left by abrupt process termination.
     * Scans working tree and creates automated rescue commit for uncommitted edits (`WIP-RECOVERY`).
     * Reconciles orphaned `Running` issues: rehydrates conversation memory from last checkpoint, injects reboot notice, and enqueues to `llm-urgent` for immediate resumption.
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
        - `read` (line-sliced via `offset`/`limit`), `bash` (with `HeadTailBuffer` truncation, compiler error extraction, and shell discovery `ls`/`find`/`rg`), `web_search` (extensible via `IWebSearchService`, defaulting to Ollama Web Search API), `ask`, and `propose_issues`.
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
      * **Stage-Scoped Tool Visibility:** Enforces strict tool gating per stage (Stage 0 Triage: Read-only + `propose_issues`; Stage 1: Read-only; Stage 2: Code/Build/Test; Stage 3: Audit/Patch; Discovery: Read-only exploration).
      * **`TriageStage`:** Automated intake stage for untrusted external reports (GitHub/GitLab issues) running on `main`/`master`; uses Crucible-exclusive tool whitelisting (`read`, `propose_issues`) and `RestrictedReadOnly` sandbox to prevent tampering or exfiltration; outputs backlog proposals for human review.
      * **`IssueReviewStage`:** System persona for architectural analysis; bootstrap prompt injecting `{{Issue.Title}}`, `{{Issue.Description}}`, repository structure, with strict directive to invoke `ask` on any ambiguity.
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
     * `SessionTranscriptService`: Serializes full turn-by-turn agent sessions into structured Markdown files in `<WorkspaceRoot>/docs/transcripts/YYYY-MM-DD-issue-{id}-{stage}-{status}.md` (thought blocks, tool calls, compiler outputs, Q&A).
     * `TranscriptDiagnosticService`: Evaluates failed or anomalous transcripts via either **Cloud LLMs** (fast, zero local VRAM consumption via OllamaRouter cloud models or external cloud API) or **Local LLMs** (100% private/offline, queued sequentially via `WorkerCount = 1`) to produce actionable remediation advice.
  10. **Interactive Ideation Agent Service (`IIdeationService`):**
      * Read-only exploration agent that converses with the developer, browses the workspace codebase, and proposes issues with dependencies via `propose_issues`.
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

### Phase 4B: Execution Environment, Path Remapping & Issue Lifecycle Alignment (Intermediate)
> 📖 **Detailed Implementation Guide:** [docs/plan/04B_EXECUTION_ENVIRONMENT_AND_WORKFLOW_ALIGNMENT.md](plan/04B_EXECUTION_ENVIRONMENT_AND_WORKFLOW_ALIGNMENT.md)
* **Objective:** Align the backend with cross-platform realities (Unix-only agents, mandatory WSL2 on Windows, canonical clone at `~/.nightcodeforge/wks/{id}`, Windows Crucible UNC file bridge, container `/workspace` remapping) and establish the updated 3-channel issue intake, manual promotion action (`Priority`, `MergeMode`), and deterministic post-review verification.
* **Tasks:**
  1. Enforce Unix-only agent confinement: require WSL2 on Windows; execute bare mode `bash` via `wsl.exe -d <distrib> -- bash` on Windows and `/bin/bash` on Unix.
  2. Implement `IWorkspacePathResolver` / `WorkspacePathResolver`:
     * Canonical agent working clone: `~/.nightcodeforge/wks/{id}`.
     * Remote URL resolution: `/mnt/{windows_path_transformed}` on Windows, raw developer path on Unix.
     * Windows Crucible UNC path access: `\\wsl.localhost\{distrib}\home\{username}\.nightcodeforge\wks\{id}\`.
     * Container mode path remapping: transparently translate `/workspace/...` calls from Crucible tools into real host/WSL filesystem paths.
  3. Implement on-the-fly container generator with shared package cache mounts (`~/.nuget/packages`, `~/.npm`, `~/.cache/uv`).
  4. Implement 3-channel issue creation (External Triage workflow, Ideation chat, `docs/issues/*.md`), all initiating issues in `Suggested` status.
  5. Implement manual promotion action: promotes `Suggested` $\rightarrow$ `Open` (setting `Priority` 1..5 and `MergeMode` [Automatic vs. ManualReviewRequired]) or `Rejected`.
  6. Realize full main workflow stage sequence:
     * Stage 1: `IssueReviewStage` $\rightarrow$ `Reviewed`.
     * Stage 2: `DeterministicPreImplementationStage` (baseline build, test, coverage).
     * Stage 3: `ImplementationStage` $\rightarrow$ `Implemented`.
     * Stage 4: `DeterministicPostImplementationStage` (coverage & warning ratchet).
     * Stage 5: `CodeReviewAndFixStage` (auditing and direct polish fixes).
     * Stage 6: `DeterministicPostReviewStage` (compilation & test verification of review patches) $\rightarrow$ `Ready` on pass, loops back on failure.
     * Stage 7: Merge Gate (`Closed` via auto-merge or maintainer manual review approval).
  7. Support universal human clarification (`ask`) at any stage, pausing via `IsWaitingForHuman = true` and `InterruptedAgentProfileId` without mutating issue status, freeing the GPU worker; resuming with exact agent on answer.
* **Deliverables:** Path resolver, cross-environment Git and Crucible UNC I/O, on-the-fly container sandbox, 3 issue intake channels, manual promotion action, and complete 7-step main workflow verified with automated tests.

---

### Phase 5: Modern Blazor Web UI & SignalR Telemetry
> 📖 **Detailed Implementation Guide:** [docs/plan/05_BLAZOR_WEB_UI_AND_TELEMETRY.md](plan/05_BLAZOR_WEB_UI_AND_TELEMETRY.md)
* **Objective:** Build a sleek, responsive, developer-grade dashboard for ideation chat, backlog & dependency management, live monitoring, question answering, transcript inspection, and diff review.
* **Tasks:**
  1. Setup Blazor Interactive Server with modern dark-mode aesthetic (custom CSS tokens, obsidian/indigo palette, crisp typography).
  2. Implement **SignalR Hub (`/hubs/orchestrator`)** streaming:
     * Live token output.
     * Agent tool executions (reading, writing, compiling).
     * Git CLI status updates.
     * Queue and worker status changes.
  3. Implement **Interactive Ideation & Codebase Discovery Chat View:**
     * Developer chat interface to ask questions about the repository.
     * Issue proposal cards with editable scope and one-click "Add to Backlog" with dependency wiring.
  4. Implement **Backlog & Dependency DAG View:**
     * Issue submission dialog (title, requirements, base branch, dependencies).
     * Dependency DAG graph / visual tree highlighting blocking relationships, lifecycle states (`Suggested`, `Open`, `Reviewed`, `Implemented`, `Ready`, `Closed`), and `WaitingForHuman` badges.
     * One-click maintainer promotion dialog: transition `Suggested` $\rightarrow$ `Open` with `Priority` (1..5) and `MergeMode` (`Automatic` vs. `ManualReviewRequired`), or `Rejected`.
  5. Implement **Monitoring View:**
     * Active worker dashboard: current issue, active stage (`Issue Review ➔ Deterministic Pre-Impl ➔ Implementation ➔ Deterministic Post-Impl ➔ Code Review ➔ Deterministic Post-Review`), elapsed time, GPU status.
     * Streaming terminal/log console with auto-scroll and log level filtering.
  6. Implement **Inbox View:**
     * Grid of questions awaiting developer answers.
     * Rich answering card displaying prompt, rationale, file context, and response form.
  7. Implement **Review Room:**
     * Visual `git diff` viewer highlighting added/modified/deleted files.
     * Action buttons: for `ManualReviewRequired` issues, "Approve & Merge" (squashes WIP commits into base branch $\rightarrow$ `Closed`, unblocking dependent tasks) or "Request Changes" (re-routes back to Stage 3 `ImplementationStage` with reviewer feedback). Auto-merge issues report automatic merge completion.
  8. Implement **Workspace, Agent Profile, Execution & MCP Settings:**
     * Workspace creation/switching and target Git repository path configuration.
     * **Execution Sandbox Settings:** Selection between `BareHost`, `WslDirect`, and `ContainerSandbox` (Docker/Podman image selection, cache mount paths, read-only SDK mount paths).
     * Agent Profile editor: configure Harness, Model, Thinking Effort, Temperature, and custom JSON parameters, with stage-to-profile assignment.
  9. Implement **Session Transcripts & AI Diagnostics Modal:**
     * Markdown viewer for past and current execution transcripts with syntax highlighting.
     * Choice between "Diagnose with Cloud AI" (fast, zero VRAM impact) and "Diagnose with Local AI" (100% private, sequential execution).
* **Deliverables:** Fully operational web interface with real-time reactive updates.

---

### Phase 6: End-to-End Testing, Resilience & Hardening
> 📖 **Detailed Implementation Guide:** [docs/plan/06_END_TO_END_TESTING_AND_HARDENING.md](plan/06_END_TO_END_TESTING_AND_HARDENING.md)
* **Objective:** Validate the entire end-to-end flow under real-world conditions.
* **Tasks:**
  1. Complete workflow simulation test:
     * User creates Issue A and dependent Issue B via ideation chat (both created as `Suggested`).
     * Maintainer promotes Issue A (`Priority = 1`, `MergeMode = ManualReviewRequired`) and Issue B (`Priority = 2`, `MergeMode = Automatic`, depends on Issue A) to `Open`.
     * Issue A enters Stage 1 (Issue Review) -> agent calls `ask` -> WIP checkpoint committed -> `IsWaitingForHuman = true` -> releases GPU worker cleanly.
     * Developer answers question -> status preserved, `IsWaitingForHuman = false` -> Issue A resumes in `llm-urgent` with original agent profile and completes Stage 1 (`Reviewed`).
     * Issue A advances through Stage 2 (`Deterministic Pre-Impl` baseline), Stage 3 (`ImplementationStage` $\rightarrow$ `Implemented`), Stage 4 (`Deterministic Post-Impl` coverage gate & warning ratchet), Stage 5 (`CodeReviewAndFixStage`), and Stage 6 (`Deterministic Post-Review Verification` $\rightarrow$ `Ready`).
     * Issue A approved in Review Room -> squash-merged to `baseBranch` (`Closed`) -> Issue B automatically unblocks and enters `llm-inference`.
  2. Resilience & cross-platform checks:
     * Abrupt process crash / host reboot simulation (e.g. Windows Update mid-inference): verify stale lock purge, rescue commit (`WIP-RECOVERY`), conversation rehydration, and automatic resumption via `llm-urgent`.
     * Windows WSL2 mandatory enforcement, Crucible Windows UNC path access (`\\wsl.localhost\...`), and container `/workspace` tool remapping.
     * Container shell hang, timeout cancellation, and sentinel REPL recovery (`\x03` SIGINT, grace period).
     * Host working tree dirty conflict fallback (`refs/heads/feature/issue-{id}-ncf`).
     * Multi-workspace VRAM protection (`WorkerCount = 1`) and workspace concurrency locks (`IWorkspaceLockProvider`).
  3. Documentation updates, WSL2 setup guide, and quickstart instructions.
* **Deliverables:** Verified production-ready orchestrator.
