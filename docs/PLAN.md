# 🗺️ NightCodeForge - Implementation Plan & Roadmap

## 1. Project Directory & Solution Layout

The solution is organized using clean architecture principles, separating core domain models, workflow pipeline, infrastructure, AI harnesses, and the interactive Blazor UI.

```text
NightCodeForge/
├── docs/
│   ├── ARCHITECTURE.md          # Complete architectural specification
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
│   │   ├── BackgroundJobs/      # Hangfire setup (llm-urgent & llm-inference queues: WorkerCount = 1)
│   │   └── Hubs/                # SignalR Telemetry & Live Chat Hub (/hubs/orchestrator)
│   │
│   ├── NightCodeForge.Agents/
│   │   ├── Common/              # AgentSessionContext, AgentRunResult, TelemetryEvent
│   │   ├── Tools/               # FileReadTool, FileWriteTool, RunCmdTool, AskHumanQuestionTool, ProposeIssuesTool
│   │   ├── Stages/              # Default 3-Agent Stages:
│   │   │   ├── IssueReviewStage.cs       # Stage 1: Scope, ambiguities, proactive human Q&A
│   │   │   ├── ImplementationStage.cs    # Stage 2: Code modifications, compilation & tests
│   │   │   └── CodeReviewAndFixStage.cs  # Stage 3: Gap analysis, lint checks & automated fixes
│   │   ├── Ideation/            # Interactive Discovery & Codebase Chat Agent
│   │   ├── MicrosoftAgent/      # Implementation A: Microsoft Agent Framework & Extensions.AI
│   │   └── PiRpc/               # Implementation B: Fallback Pi Agent via JSON-RPC
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
| **NightCodeForge.Agents** | `Microsoft.Extensions.AI`<br>`Microsoft.Extensions.AI.Ollama`<br>`System.Text.Json` | AI agent abstractions, function/tool calling, memory checkpointing, JSON-RPC communication |
| **NightCodeForge.Web** | `Microsoft.AspNetCore.Components.Web`<br>`DiffPlex` (for diff parsing) | Reactive Blazor UI, terminal streaming, side-by-side git diff rendering |
| **NightCodeForge.Tests** | `xunit`<br>`FluentAssertions`<br>`NSubstitute` | Automated unit and integration testing |

---

## 3. Phased Implementation Roadmap

### Phase 1: Foundation, Domain Models & Extensible Workflow Pipeline
* **Objective:** Establish the solution, multi-workspace support, domain entities, SQLite persistence, DAG dependency manager, and extensible workflow pipeline engine.
* **Tasks:**
  1. Initialize .NET 10 solution and create project skeleton (`Core`, `Infrastructure`, `Agents`, `Web`, `Tests`).
  2. Implement domain entities:
     * `Workspace` (Id, Name, RepositoryPath, DefaultBaseBranch, CreatedAt).
     * `AgentProfile` (Id, WorkspaceId, Name, HarnessId, ModelName, ThinkingEffort, ThinkingTokenBudget, Temperature, HarnessConfigurationJson).
     * `Issue` (Id, WorkspaceId, Title, Description, Status, CurrentStageId, Priority, BaseBranch, FeatureBranch).
     * `IssueDependency` (IssueId, DependsOnIssueId).
     * `Question` (IssueId, StageId, Prompt, Rationale, OptionsJson, Answer, Status).
     * `StageCheckpoint` (IssueId, StageId, SerializedMemory, GitCommitHash).
     * `StageExecutionRecord` (IssueId, StageId, AgentProfileId, HarnessId, StartedAt, CompletedAt, DurationMs, PromptTokens, CompletionTokens, TotalTokens, TargetRouting, Status).
     * `ExecutionLog` (IssueId, StageId, Timestamp, Level, Source, Message).
  3. Implement `DependencyResolutionEngine`:
     * Topological sorting of issues per workspace.
     * Cycle detection (reject circular dependencies).
     * Dependency readiness evaluation: `IsReadyForExecution(issueId)`.
     * Cascade activation when a prerequisite reaches `Completed`.
  4. Implement `ExtensibleWorkflowEngine`:
     * `IWorkflowPipeline` and `IWorkflowStage` abstractions with `AgentProfile` associations.
     * Built-in `Default3AgentPipeline`:
       1. `IssueReviewStage` (Default Profile: Deep Reasoning / high thinking effort)
       2. `ImplementationStage` (Default Profile: Fast Coder / medium thinking effort)
       3. `CodeReviewAndFixStage` (Default Profile: Strict Auditor / low temperature)
  5. Implement **`IssueMarkdownSyncService`**:
     * Generates and updates Markdown files with YAML frontmatter in `<WorkspaceRoot>/docs/issues/YYYY-MM-DD-<slug>.md` (date-prefixed without ID in filename to enable clean sorting and avoid renames).
     * Automatically syncs question/answer logs, stage transitions, and generated plans to disk.
     * Enforces field protection: user edits to title, body, priority, profile, and dependencies sync to DB; system-managed fields (`status`, `stage`, `id`) are protected and restored if altered.
     * Detects dropped new files, allocates a new database ID, and injects `id` into frontmatter in-place without renaming.
     * Cleans up or archives files when issues reach `Completed` or `Cancelled`.
  6. Configure `AppDbContext` with SQLite and create initial migrations.
  7. Unit tests in `NightCodeForge.Core.Tests` for pipeline progression, pause/resume, token/duration tracking, DAG dependency graph resolution, and markdown issue sync.
* **Deliverables:** Solution builds cleanly; workflow pipeline, metrics tracking, DAG dependency, and markdown sync unit tests pass 100%.

---

### Phase 2: Git CLI Subsystem (CliWrap)
* **Objective:** Deliver a bulletproof Git manager using native CLI only (no LibGit2Sharp) capable of safe context switching and WIP checkpointing across configured workspaces.
* **Tasks:**
  1. Implement `GitCliRunner` wrapping `CliWrap` with command execution, timeout policies, stdout/stderr capture, and typed `GitCommandException`.
  2. Implement `GitWorkspaceManager`:
     * `EnsureRepositoryValidAsync(string repoPath)`: Validates target working tree.
     * `CheckoutIssueBranchAsync(Workspace workspace, int issueId, string baseBranch)`: Branch creation (`feature/issue-{id}`) and checkout.
     * `CreateWipCheckpointAsync(Workspace workspace, int issueId, string stageName, string questionSummary)`: Performs `git add -A` and `git commit -m "WIP: [Issue #{id}][Stage: {stageName}] {summary}" --no-verify`.
     * `GenerateDiffAsync(Workspace workspace, int issueId, string baseBranch)`: Executes `git diff {baseBranch}...feature/issue-{id}`.
     * `RevertOrCleanAsync(Workspace workspace)`: Handles dirty states during crashes.
     * `SquashAndMergeAsync(Workspace workspace, int issueId, string baseBranch, string finalMessage)`: Squashes WIP commits upon approval.
  3. Create integration tests in `NightCodeForge.Infrastructure.Tests` executing real Git commands on dynamically created temporary directories.
* **Deliverables:** Git operations verified with automated tests covering branch switching, dirty detection, WIP commits, and diff generation.

---

### Phase 3: Hangfire Queue & Orchestration Engine
* **Objective:** Configure the single-worker GPU inference queue, step metrics recording, and background task coordinator.
* **Tasks:**
  1. Configure Hangfire with SQLite storage.
  2. Register two background job servers:
     * **LLM Inference Server:** `WorkerCount = 1`, `Queues = new[] { "llm-urgent", "llm-inference" }` (global across all workspaces to guard GPU VRAM).
     * **Utility Server:** `WorkerCount = 4`, `Queues = new[] { "default" }`.
  3. Implement `IssueOrchestrator`:
     * Dequeues issue, checks workspace and DAG dependencies.
     * Locks workspace mutex and checks out branch.
     * Loads current stage in the pipeline and invokes the selected harness (`IAgentHarness`).
     * Measures step performance (start/end timestamp, duration in ms, token usage).
     * Evaluates `StageExecutionResult`:
       * If `WaitingForHuman`: records `Question`, `StageCheckpoint`, and `StageExecutionRecord`, performs Git WIP commit, sets status to `BlockedQuestion`, finishes job to release worker.
       * If `StageCompleted`: saves `StageExecutionRecord` and checkpoint, advances to next stage in pipeline. If final stage completed, transitions to `Review` and generates diff.
       * If `Failed`: logs error and metrics, transitions state to `Failed`.
  4. Implement `SubmitHumanAnswerAsync`:
     * Saves answer in DB.
     * Transitions state from `BlockedQuestion` to `Pending`.
     * Enqueues job to `llm-urgent` queue for immediate priority resumption.
  5. Implement `CompleteIssueAndCascadeAsync`:
     * On final human approval/merge, sets issue to `Completed`.
     * Unlocks dependent issues in the same workspace and enqueues newly ready issues into `llm-inference`.
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
  3. Implement **Harness Implementations:**
     * **Implementation A (Native First-Class): Microsoft Agent Framework & `Microsoft.Extensions.AI`:**
       * In-process C# agent with function tools (`ReadFile`, `WriteFile`, `ListDirectory`, `RunCommand`, `AskHumanQuestion`).
       * Automatic token usage extraction from `AdditionalProperties["usage"]`.
     * **Implementation B (Fallback First-Class): Pi via JSON-RPC:**
       * External process runner communicating via JSON-RPC over `stdio` or local socket.
       * Captures `ask_user` events, requests session suspension, and captures state token.
     * **Implementation C (Open Standard): Agent Client Protocol (ACP) Harness Adapter:**
       * Protocol adapter implementing the official **[ACP specification](https://agentclientprotocol.com/)** over JSON-RPC (`stdio` or WebSocket/HTTP).
       * Bridges ACP session messages, workspace tools, and human prompt requests into NightCodeForge's pause/resume checkpointing.
       * Enables plugging any external ACP-compliant coding agent out-of-the-box.
  4. Implement the 3 Specialized Workflow Stages & Bootstrap Prompts:
     * **`IssueReviewStage`:** System persona for architectural analysis; bootstrap prompt injecting `{{Issue.Title}}`, `{{Issue.Description}}`, repository structure, with strict directive to invoke `AskHumanQuestion` on any ambiguity.
     * **`ImplementationStage`:** System persona for senior engineer / TDD; bootstrap prompt injecting approved Stage 1 plan, `{{PriorAnswers}}`, affected files; tool loop for coding, building, and running tests.
     * **`CodeReviewAndFixStage`:** System persona for critical code auditor; bootstrap prompt injecting target `{{BaseBranch}}`, `{{GitDiff}}` changeset; automated gap analysis, regression detection, and direct patch application.
  5. Implement **Model Context Protocol (MCP) Integration:**
     * Connect to external MCP servers configured in `mcp_config.json` via Microsoft Agent Framework (`Microsoft.Agents.Mcp`).
     * Dynamically discover tools from database, GitHub, or custom CLI MCP servers and register them into the active agent session.
     * Forward MCP tool definitions to ACP agents for unified multi-harness tooling.
  6. Implement **Modular Skills System:**
     * Parser for `.agents/skills/<skill-name>/SKILL.md` (metadata frontmatter + operational markdown instructions).
     * Provide `ReadSkill(skillName)` tool allowing agents to look up repo-specific guidelines on demand.
     * Context-aware skill injection into stage prompt templates based on file triggers or profile bindings.
  7. Implement **Repository Guidelines & `AGENTS.md` Loader:**
     * Automatically discover and parse `AGENTS.md`, `.agents/AGENTS.md`, or `CLAUDE.md` from the target workspace.
     * Prepend parsed repository instructions, architectural rules, and build/test commands to stage system prompts across all harnesses (Microsoft Agent, Pi, and ACP).
  8. Implement **Session Transcript & Hybrid Diagnostics Service:**
     * `SessionTranscriptService`: Serializes full turn-by-turn agent sessions into structured Markdown files in `<WorkspaceRoot>/docs/transcripts/YYYY-MM-DD-issue-{id}-{stage}-{status}.md` (thought blocks, tool calls, compiler outputs, Q&A).
     * `TranscriptDiagnosticService`: Evaluates failed or anomalous transcripts via either **Cloud LLMs** (fast, zero local VRAM consumption via OllamaRouter cloud models or external cloud API) or **Local LLMs** (100% private/offline, queued sequentially via `WorkerCount = 1`) to produce actionable remediation advice.
  9. **Interactive Ideation Agent Service (`IIdeationService`):**
     * Read-only exploration agent that converses with the developer, browses the workspace codebase, and proposes issues with dependencies.
* **Deliverables:** Harness abstraction layer complete; Microsoft Agent, Pi, and ACP adapter functional; MCP, Skills, and AGENTS.md integration verified; session transcripts and hybrid failure diagnostics operational; 3 agent stages and discovery agent verified with mock/local LLMs and step metrics recorded.

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
  8. Implement **Workspace, Agent Profile & MCP Settings:**
     * Workspace creation/switching and target Git repository path configuration.
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
     * Unexpected process crash during inference -> verify workspace recovery on next startup.
     * Command timeout handling (long-running build commands).
     * Malformed JSON or LLM errors handled gracefully without deadlocking the queue.
  3. Documentation updates and quickstart instructions.
* **Deliverables:** Verified production-ready orchestrator.
