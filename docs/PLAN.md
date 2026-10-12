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
* **Objective:** Establish the solution, domain entities, SQLite persistence, DAG dependency manager, and extensible workflow pipeline engine.
* **Tasks:**
  1. Initialize .NET 10 solution and create project skeleton (`Core`, `Infrastructure`, `Agents`, `Web`, `Tests`).
  2. Implement domain entities:
     * `Issue` (Id, Title, Description, Status, CurrentStageId, Priority, BaseBranch, FeatureBranch).
     * `IssueDependency` (IssueId, DependsOnIssueId).
     * `Question` (IssueId, StageId, Prompt, Rationale, OptionsJson, Answer, Status).
     * `StageCheckpoint` (IssueId, StageId, SerializedMemory, GitCommitHash).
     * `ExecutionLog` (IssueId, StageId, Timestamp, Level, Source, Message).
  3. Implement `DependencyResolutionEngine`:
     * Topological sorting of issues.
     * Cycle detection (reject circular dependencies).
     * Dependency readiness evaluation: `IsReadyForExecution(issueId)`.
     * Cascade activation when a prerequisite reaches `Completed`.
  4. Implement `ExtensibleWorkflowEngine`:
     * `IWorkflowPipeline` and `IWorkflowStage` abstractions.
     * Built-in `Default3AgentPipeline`:
       1. `IssueReviewStage`
       2. `ImplementationStage`
       3. `CodeReviewAndFixStage`
     * Transition logic: advancing stages, pausing for human input, failure handling.
  5. Configure `AppDbContext` with SQLite and create initial migrations.
  6. Unit tests in `NightCodeForge.Core.Tests` for pipeline progression, pause/resume, and DAG dependency graph resolution.
* **Deliverables:** Solution builds cleanly; workflow pipeline and DAG dependency unit tests pass 100%.

---

### Phase 2: Git CLI Subsystem (CliWrap)
* **Objective:** Deliver a bulletproof Git manager using native CLI only (no LibGit2Sharp) capable of safe context switching and WIP checkpointing.
* **Tasks:**
  1. Implement `GitCliRunner` wrapping `CliWrap` with command execution, timeout policies, stdout/stderr capture, and typed `GitCommandException`.
  2. Implement `GitWorkspaceManager`:
     * `EnsureRepositoryValidAsync()`: Validates target working tree.
     * `CheckoutIssueBranchAsync(int issueId, string baseBranch)`: Branch creation (`feature/issue-{id}`) and checkout.
     * `CreateWipCheckpointAsync(int issueId, string stageName, string questionSummary)`: Performs `git add -A` and `git commit -m "WIP: [Issue #{id}][Stage: {stageName}] {summary}" --no-verify`.
     * `GenerateDiffAsync(int issueId, string baseBranch)`: Executes `git diff {baseBranch}...feature/issue-{id}`.
     * `RevertOrCleanAsync()`: Handles dirty states during crashes.
     * `SquashAndMergeAsync(int issueId, string baseBranch, string finalMessage)`: Squashes WIP commits upon approval.
  3. Create integration tests in `NightCodeForge.Infrastructure.Tests` executing real Git commands on dynamically created temporary directories.
* **Deliverables:** Git operations verified with automated tests covering branch switching, dirty detection, WIP commits, and diff generation.

---

### Phase 3: Hangfire Queue & Orchestration Engine
* **Objective:** Configure the single-worker GPU inference queue and background task coordinator.
* **Tasks:**
  1. Configure Hangfire with SQLite storage.
  2. Register two background job servers:
     * **LLM Inference Server:** `WorkerCount = 1`, `Queues = new[] { "llm-urgent", "llm-inference" }`.
     * **Utility Server:** `WorkerCount = 4`, `Queues = new[] { "default" }`.
  3. Implement `IssueOrchestrator`:
     * Dequeues issue, checks DAG dependencies.
     * Locks workspace mutex and checks out branch.
     * Loads current stage in the pipeline and invokes the appropriate agent harness.
     * Evaluates `StageExecutionResult`:
       * If `WaitingForHuman`: saves `Question` and `StageCheckpoint`, performs Git WIP commit, sets status to `BlockedQuestion`, finishes job to release worker.
       * If `StageCompleted`: saves checkpoint, advances to next stage in pipeline. If final stage completed, transitions to `Review` and generates diff.
       * If `Failed`: logs error, transitions state to `Failed`.
  4. Implement `SubmitHumanAnswerAsync`:
     * Saves answer in DB.
     * Transitions state from `BlockedQuestion` to `Pending`.
     * Enqueues job to `llm-urgent` queue for immediate priority resumption.
  5. Implement `CompleteIssueAndCascadeAsync`:
     * On final human approval/merge, sets issue to `Completed`.
     * Unlocks dependent issues and enqueues newly ready issues into `llm-inference`.
* **Deliverables:** Background worker lifecycle working with simulated agent tasks, testing queue priority, interruption, and dependency unblocking.

---

### Phase 4: AI Harness Implementations & 3 Specialized Agent Stages
* **Objective:** Implement both AI engines (Microsoft Agent Framework & Pi JSON-RPC fallback), the 3 default workflow stages, discovery agent, and primary gateway integration with **OllamaRouter**.
* **Tasks:**
  1. Define `IAgentHarness`, `AgentSessionContext`, `AgentRunResult`, and `AgentTelemetryEvent`.
  2. Implement **OllamaRouter Gateway Integration:**
     * Connect to OllamaRouter (`jetelain/OllamaRouter`) via `Microsoft.Extensions.AI.Ollama` or `Microsoft.Extensions.AI.OpenAI` pointing to `http://localhost:11434`.
     * Leverage OllamaRouter's automated GPU routing (Fast Local GPU vs Remote High-VRAM vs Cloud overflow).
     * Implement `IOllamaModelCatalogService` to query `/api/tags` and populate active local/remote models dynamically.
     * Capture target routing telemetry (e.g. from `/monitor` or response headers) for real-time UI display.
  3. Build standard agent tools:
     * `ReadFileTool`: reads line ranges safely.
     * `WriteFileTool`: creates/updates files inside repository root.
     * `RunCommandTool`: executes `dotnet build`, `dotnet test`, etc., with security filters.
     * `AskHumanQuestionTool`: records the question and triggers context suspension.
     * `ProposeIssuesTool`: structured proposal tool for the ideation chat.
  4. Implement the 3 Specialized Workflow Stages:
     * **`IssueReviewStage`:** Specialized prompt to verify requirements against codebase, check ambiguities, and clarify proactively with human.
     * **`ImplementationStage`:** Tool loop for coding, building, and running tests.
     * **`CodeReviewAndFixStage`:** Diff inspection against base branch, regression detection, and automated patch application.
  5. **Implementation A (Microsoft Agent Framework & `Microsoft.Extensions.AI`):**
     * Sets up `IChatClient` (configured for OllamaRouter or direct OpenAI/Ollama endpoints).
     * Manages function calling, chat history serialization, and question interception.
  6. **Implementation B (Pi via JSON-RPC Fallback):**
     * Implements JSON-RPC client over process stdin/stdout or local socket.
     * Listens for human input events, requests session suspension, and serializes state token.
  7. **Interactive Ideation Agent Service (`IIdeationService`):**
     * Read-only exploration agent that converses with the developer, browses the codebase, and proposes issues with dependencies.
* **Deliverables:** OllamaRouter integrated; both harnesses functional; 3 agent stages and discovery agent verified with mock/local LLMs.

---

### Phase 5: Modern Blazor Web UI & SignalR Telemetry
* **Objective:** Build a sleek, responsive, developer-grade dashboard for ideation chat, backlog & dependency management, live monitoring, question answering, and diff review.
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
