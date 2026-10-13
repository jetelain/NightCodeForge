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
│   ├── REFERENCES.md            # Source code mapping to MS Agent Framework, Pi & ACP
│   └── PLAN.md                  # Step-by-step roadmap & execution guide
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

## 3. Phased Implementation Roadmap

### Phase 1: Foundation, Domain Models & Extensible Workflow Pipeline
* **Objective:** Establish the solution, multi-workspace support, domain entities, SQLite persistence, DAG dependency manager, and extensible workflow pipeline engine.
* **Tasks:**
  1. Initialize .NET 10 solution and create project skeleton (`Core`, `Infrastructure`, `Agents`, `Web`, `Tests`).
  2. Implement domain entities and enumerations:
     * `Workspace` (Id, Name, RepositoryPath, DefaultBaseBranch, ExecutionMode [BareHost, WslDirect, ContainerSandbox], ContainerImage, Ext4MirrorPath, CreatedAt).
     * `AgentProfile` (Id, WorkspaceId, Name, HarnessId, ModelName, ThinkingEffort, ThinkingTokenBudget, Temperature, HarnessConfigurationJson).
     * `Issue` (Id, WorkspaceId, Title, Description, Status, CurrentStageId, Priority, BaseBranch, FeatureBranch).
     * `IssueDependency` (IssueId, DependsOnIssueId).
     * `Question` (IssueId, StageId, Prompt, Rationale, OptionsJson, Answer, Status).
     * `StageCheckpoint` (IssueId, StageId, SerializedMemory, GitCommitHash).
     * `StageExecutionRecord` (IssueId, StageId, AgentProfileId, HarnessId, ModelName, StartedAt, CompletedAt, DurationMs, PromptTokens, CompletionTokens, TotalTokens, TargetRouting, Status).
     * `ExecutionLog` (IssueId, StageId, Timestamp, Level, Source, Message).
     * Enums: `IssueOverallStatus` (`WaitingOnDependencies`, `Pending`, `Running`, `BlockedQuestion`, `Review`, `Completed`, `Cancelled`, `Failed`), `ExecutionMode`, `ThinkingEffort`, `HarnessType`.
  3. Implement `DependencyResolutionEngine`:
     * Topological sorting of issues per workspace.
     * Cycle detection (reject circular dependencies).
     * Dependency readiness evaluation: `IsReadyForExecution(issueId)`.
     * Cascade activation when a prerequisite reaches `Completed`.
  4. Implement **Workspace Concurrency Control (`IWorkspaceLockProvider`):**
     * Keyed async lock provider (`AsyncKeyedLock<int>` by `WorkspaceId`).
     * Guarantees that while the global inference queue processes sequentially (`WorkerCount = 1`), background utility jobs (`WorkerCount = 4`) and user UI actions do not execute concurrent Git operations or index modifications on the same workspace, preventing `.git/index.lock` race conditions.
  5. Implement `ExtensibleWorkflowEngine`:
     * `IWorkflowPipeline` and `IWorkflowStage` abstractions supporting both **Agentic** (LLM-driven with GPU allocation) and **Deterministic** (CPU-native builds, tests, SAST with 0 GPU/tokens) stages via `WorkflowStageKind`.
      * **Deterministic Quality Gates & Outcome Routing:** Enables deterministic stages (`DeterministicBuildStage`, `DeterministicTestStage`, `DeterministicSastStage`) to act as outcome-driven routers: advancing forward on pass, or looping backward to fix errors (with retry circuit breaker) paired with **Synthetic Conversation Priming** to prevent agents from re-running redundant builds/tests.
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
     * Enforces field protection: user edits to title, body, priority, profile, and dependencies sync to DB; system-managed fields (`status`, `stage`, `id`) are protected and restored if altered.
     * Detects dropped new files, allocates a new database ID, and injects `id` into frontmatter in-place without renaming.
     * Cleans up or archives files when issues reach `Completed` or `Cancelled`.
  7. Configure `AppDbContext` with SQLite and create initial migrations.
  8. Unit tests in `NightCodeForge.Core.Tests` for pipeline progression, pause/resume, token/duration tracking, DAG dependency graph resolution, echo loop suppression, and markdown issue sync.
* **Deliverables:** Solution builds cleanly; workflow pipeline, metrics tracking, DAG dependency, and markdown sync unit tests pass 100%.

---

### Phase 2: Git CLI Subsystem & Ext4 Workspace Mirroring (CliWrap)
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
  3. Implement **`GitMirrorSyncService` (Ext4 Mirroring Protocol):**
     * Solves the WSL2 9p filesystem performance bottleneck on Windows hosts.
     * Establishes isolated clone on ext4 (`/var/ncf/workspaces/{workspaceId}/repo`) with local remote `host-origin` pointing to the developer's Windows repository.
     * Configures the host repository with `git config receive.denyCurrentBranch updateInstead` to ensure `git push host-origin feature/issue-{id}` succeeds without rejection even when the developer has the branch checked out in their IDE on the host.
     * **Host Working Tree Dirty Conflict Fallback:** If the developer has uncommitted changes to tracked files in their host IDE, Git rejects `updateInstead`; `GitMirrorSyncService` catches the rejection, pushes to `refs/heads/feature/issue-{id}-ncf` instead, and emits a SignalR alert informing the developer to reconcile local uncommitted files.
     * Performs `git fetch host-origin <baseBranch>` on stage inception.
     * Performs `git push host-origin feature/issue-{id}` upon WIP checkpointing, human questions, or stage completion to keep developer's IDE in sync.
     * Directly mounts workspace when repo is already native to Linux/WSL2 ext4.
  4. Create integration tests in `NightCodeForge.Infrastructure.Tests` executing real Git commands on dynamically created temporary directories and verifying local remote fetch/push mirror sync.
* **Deliverables:** Git operations verified with automated tests covering branch switching, dirty detection, WIP commits, diff generation, and bidirectional ext4 mirror synchronization.

---

### Phase 3: Hangfire Queue & Orchestration Engine
* **Objective:** Configure the single-worker GPU inference queue, step metrics recording, and background task coordinator.
* **Tasks:**
  1. Configure Hangfire with SQLite storage.
  2. Register two background job servers:
     * **LLM Inference Server:** `WorkerCount = 1`, `Queues = new[] { "llm-urgent", "llm-inference" }` (global across all workspaces to guard GPU VRAM; provides non-preemptive priority pick-up so resumed issues in `llm-urgent` are prioritized upon the next worker turn without concurrent VRAM oversubscription).
     * **Utility Server:** `WorkerCount = 4`, `Queues = new[] { "default" }`.
  3. Implement `IssueOrchestrator`:
     * Dequeues issue, checks workspace and DAG dependencies.
     * Acquires workspace lock via `IWorkspaceLockProvider` and checks out branch.
     * Loads current stage in the pipeline and invokes the selected harness (`IAgentHarness`).
     * Measures step performance (start/end timestamp, duration in ms, token usage).
     * Evaluates `StageExecutionResult`:
       * If `WaitingForHuman`: records `Question`, `StageCheckpoint`, and `StageExecutionRecord`, performs Git WIP commit, sets status to `BlockedQuestion`, and cleanly completes the Hangfire job (`Succeeded`) to release the worker without false error alerts.
       * If `StageCompleted`: saves `StageExecutionRecord` and checkpoint, advances to next stage in pipeline. If final stage completed, transitions to `Review` and generates diff.
       * If `Failed`: logs error and metrics, transitions state to `Failed`.
  4. Implement `SubmitHumanAnswerAsync`:
     * Saves answer in DB.
     * Transitions state from `BlockedQuestion` to `Pending`.
     * Enqueues job to `llm-urgent` queue for immediate priority resumption.
  5. Implement `RequestChangesAsync`:
     * Allows reviewer to provide revision feedback notes from the Review Room.
     * Transitions issue from `Review` back to Stage 2 (`ImplementationStage`) with reviewer feedback injected into prompt context.
     * Enqueues job to `llm-urgent` queue for priority iteration.
  6. Implement `CompleteIssueAndCascadeAsync`:
     * On final human approval/merge, sets issue to `Completed`.
     * Unlocks dependent issues in the same workspace and enqueues newly ready issues into `llm-inference`.
  7. Implement `IStartupRecoveryService` (Graceful Restart & Windows Update Recovery):
     * Executes during host startup before Hangfire worker begins polling.
     * Purges dangling Git lock files (`.git/index.lock`, `.git/refs/...`) left by abrupt process termination.
     * Scans working tree and creates automated rescue commit for uncommitted edits (`WIP-RECOVERY`).
     * Reconciles orphaned `Running` issues: rehydrates conversation memory from last checkpoint, injects reboot notice, and enqueues to `llm-urgent` for immediate resumption.
* **Deliverables:** Background worker lifecycle working with simulated agent tasks, testing queue priority, step duration/token metrics, interruption, and dependency unblocking.

---

### Phase 4: AI Harness Abstraction & Implementations (Native, Pi, and ACP)
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
  11. Implement **Local LLM Token Minimization & Context Compaction Engine:**
      * Static prompt prefix stabilization ensuring 100% byte-identical headers for KV cache hit rate maximization.
      * Native in-loop context compaction via `ToolResultCompactionStrategy` (`Microsoft.Agents.AI.Compaction`) with `MinimumPreservedGroups = 3` and custom `ToolCallFormatter` to compact older tool outputs into concise stubs.
      * Bounded command output streaming via `HeadTailBuffer` + compiler diagnostic extraction (extracting only failing `error CS\d+` lines and test assertions).
      * Synthetic conversation priming: pre-seeds active chat history with simulated prior tool execution pairs (e.g. deterministic test results) to eliminate exploratory agent tool round-trips.
* **Deliverables:** Harness abstraction layer complete; Canonical tools & LenientToolDispatcher operational with EditorConfig compliance (EOL, tab/space, whitespace, charset); Crucible, Pi, and ACP adapter functional; token minimization & context compaction engine verified; MCP, Skills, and AGENTS.md integration verified; execution sandboxing and shared caches operational; session transcripts and hybrid failure diagnostics operational; 3 agent stages and discovery agent verified with mock/local LLMs and step metrics recorded.

---

### Phase 5: Modern Blazor Web UI & SignalR Telemetry
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
     * Dependency DAG graph / visual tree highlighting blocking relationships and current state.
  5. Implement **Monitoring View:**
     * Active worker dashboard: current issue, active stage (`Review ➔ Implement ➔ Fix`), elapsed time, GPU status.
     * Streaming terminal/log console with auto-scroll and log level filtering.
  6. Implement **Inbox View:**
     * Grid of questions awaiting developer answers.
     * Rich answering card displaying prompt, rationale, file context, and response form.
  7. Implement **Review Room:**
     * Visual `git diff` viewer highlighting added/modified/deleted files.
     * Action buttons: "Approve & Merge" (squash & merge to base branch, unblocking dependent tasks) or "Request Changes".
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
* **Objective:** Validate the entire end-to-end flow under real-world conditions.
* **Tasks:**
  1. Complete workflow simulation test:
     * User creates Issue A and dependent Issue B via ideation chat.
     * Issue A enters Stage 1 (Issue Review) -> asks human question -> WIP committed -> releases GPU.
     * User answers question -> Issue A resumes in `llm-urgent` -> proceeds through Stage 2 (Implementation) and Stage 3 (Code Review & Fix).
     * Issue A approved in Review Room -> merges -> Issue B automatically unblocks and enters queue.
  2. Resilience checks:
      * Abrupt process crash / host reboot simulation (e.g. Windows Update mid-inference): verify stale lock purge, rescue commit, conversation rehydration, and automatic resumption via `llm-urgent`.
     * Command timeout handling (long-running build commands).
     * Malformed JSON or LLM errors handled gracefully without deadlocking the queue.
  3. Documentation updates and quickstart instructions.
* **Deliverables:** Verified production-ready orchestrator.
