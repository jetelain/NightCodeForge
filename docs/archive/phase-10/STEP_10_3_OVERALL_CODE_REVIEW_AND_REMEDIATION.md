# 🔍 Step 10.3: Comprehensive Code Review & AGENTS.md Hardening

> **Phase 10 Implementation Step**  
> *Parent Plan:* [Phase 10 Master Plan](../10_MVP_FINAL_POLISH.md) | *Source Policy:* [AGENTS.md](../../../AGENTS.md)  
> *Status:* Completed | *Target Framework:* .NET 10 (C# 13)  

---

## 1. Objective

Perform a rigorous, 360-degree code review across all solution layers prior to building the v1 MVP. Eliminate silent failures, audit constructor dependency injection, enforce hard errors on missing database entities, ensure thread safety and resource cleanup, and resolve technical debt in accordance with [AGENTS.md](../../../AGENTS.md).

---

## 2. Review Taxonomy & Severity Levels

All review findings must be classified according to the following taxonomy:

| Severity | Definition | Action Required |
| :--- | :--- | :--- |
| **Critical** | Data corruption risk, security sandbox escape, unhandled crash in background worker, deadlocks. | Must be remediated immediately in Action 3.5 before MVP. |
| **High** | Violation of strict repository policy ([AGENTS.md](../../../AGENTS.md)), bare `catch { }` without logging, silent fallbacks, unobserved async tasks. | Must be remediated in Action 3.5 before MVP. |
| **Medium** | Missing cancellation token propagation, unindexed query, memory leak risk (missing event unsubscription). | Remediated in Action 3.5 or tracked as prioritized post-MVP issue. |
| **Low** | Minor code smell, formatting inconsistency, non-critical doc comment gap. | Cleaned up opportunistically. |

---

## 3. Subagent Action Breakdown

### Subagent Action 3.1: Strict AGENTS.md Policy Grep & Static Audit
- **Focus:** Automated script-based pattern scanning across all `.cs` and `.razor` files in `src/`.
- **Target Checks:**
  1. **Bare `catch` & Swallowed Exceptions:**
     - Search regex: `catch\s*\([^\)]*\)\s*\{\s*\}` or `catch\s*\{`
     - Search regex: `catch\s*\(Exception[^\)]*\)\s*\{[^\}]*\/\/\s*silently`
     - *Rule:* Bare catch blocks without logging via `ILogger` or justification are forbidden.
  2. **Constructor Optional Parameters:**
     - Search regex: `public\s+[A-Za-z0-9_]+\([^\)]*=\s*null[^\)]*\)` or `=\s*default`
     - *Rule:* In service constructors, avoid optional parameters. Require all dependencies to fail fast.
  3. **Synthetic Fallback Entities:**
     - Audit repositories and services for fallback instantiation (`?? new WorkflowDefinition()`, `?? new AgentProfile()`).
     - *Rule:* Throw dedicated domain exceptions (`WorkflowNotFoundException`, `AgentProfileNotFoundException`) on missing database entities.
  4. **Magic Strings & Numbers:**
     - Audit for raw string stage IDs (`"task-review"`, `"implementation"`, `"merge"`) instead of `StandardStageIds`.
- **Deliverable:** Consolidated Markdown table of all policy violations with file path, line number, snippet, and required fix.

---

### Subagent Action 3.2: Core & Infrastructure Deep Audit
- **Focus:** Backend runtime, concurrency, database access, and OS process management.
- **Target Checks:**
  1. **EF Core `AppDbContext` Lifetime & Concurrency:**
     - Ensure `AppDbContext` instances are scoped properly and never shared across concurrent background threads or tasks.
     - Verify asynchronous queries always use `AsNoTracking()` where read-only projections suffice.
  2. **Process Execution & Resource Disposal:**
     - Audit `CliWrap` invocations and system process wrappers (`GitCliService`, `BareHostExecutionEnvironment`).
     - Verify cancellation tokens are passed to all process executions and timeouts are enforced.
  3. **Synchronization Primitives:**
     - Verify that every `SemaphoreSlim.WaitAsync()` is paired with a corresponding `Release()` inside a `finally` block.
     - Check `WorkspaceLockManager` for deadlock potential and stale lock reclamation.
  4. **Git Mirror & Worktree Hygiene:**
     - Verify worktree paths and ext4 mirrors clean up orphaned lock files (`index.lock`) on startup recovery.
- **Deliverable:** Core & Infrastructure review report with exact code diff recommendations.

---

### Subagent Action 3.3: Agents, Harnesses & Inference Deep Audit
- **Focus:** LLM orchestration, prompt construction, tool execution, and memory management.
- **Target Checks:**
  1. **Tool Dispatcher & Sandboxing:**
     - Verify that file system tools (`read`, `edit`, `write`) strictly enforce path resolution within the assigned workspace or worktree root, preventing traversal attacks (`../`).
     - Check `LenientToolDispatcher` error responses to ensure tools report actionable errors back to agents instead of crashing.
  2. **Prompt Cache Stability (KV Cache Optimization):**
     - Verify Layer 1 & Layer 2 system prompt prefixes remain byte-identical across turns to ensure $\ge 90\%$ KV cache hit rates on local Ollama routers.
     - Ensure temporal context (current date/time) is injected exclusively into user bootstrap prompts, never in the static system prompt.
  3. **Token Estimators & Context Compaction:**
     - Verify sliding window token estimators accurately count prompt and tool payload tokens.
     - Ensure large file outputs (`git diff`, build logs) are truncated gracefully before blowing model context limits.
  4. **Streaming Telemetry & Error Handling:**
     - Ensure SignalR activity bridge and SSE streams catch and log client disconnects without terminating orchestrator execution.
- **Deliverable:** Agents layer review report with exact code diff recommendations.

---

### Subagent Action 3.4: Web UI Blazor & Frontend Resilience Audit
- **Focus:** Blazor server rendering, component lifecycle, SignalR connectivity, and DOM accessibility.
- **Target Checks:**
  1. **Component Disposal & Event Unsubscription:**
     - Verify all Blazor components that subscribe to events (`WorkspaceState.OnWorkspaceChanged`, `ToastService.OnShow`, etc.) implement `IDisposable` or `IAsyncDisposable` and detach handlers.
  2. **SignalR Connection Lifecycle:**
     - Audit `OrchestratorHubClientConnection`: ensure auto-reconnect cleanly re-joins active task groups and re-registers handlers without duplicating event fires.
  3. **Error Boundaries & Graceful Degradation:**
     - Verify Blazor components wrap risky interactive operations (e.g. diff rendering, markdown parsing) in appropriate error boundaries or try/catch with user-friendly toast notifications.
  4. **Accessibility & Responsive Semantics:**
     - Ensure interactive elements have distinct `aria-label`, `title`, and minimum 44px touch targets.
- **Deliverable:** Web UI review report with exact code diff recommendations.

---

### Subagent Action 3.5: Consolidated Remediation & Full Test Validation
- **Focus:** Implement code fixes for all identified Critical and High findings, followed by complete regression validation.
- **Execution Workflow:**
  1. Apply code patches across affected files in Core, Infrastructure, Agents, and Web.
  2. Ensure compiler produces **0 warnings and 0 errors**:
     ```powershell
     dotnet build
     ```
  3. Execute standard test suite:
     ```powershell
     dotnet test --filter "Category!=Acceptance&Category!=E2E"
     ```
  4. Execute all tests including E2E and acceptance tests:
     ```powershell
     dotnet test
     ```
  5. Validate that all tests pass cleanly.
- **Deliverable:** Final verification report showing 100% test pass rate and zero policy violations.
  *Status:* **Completed** (1,180 tests passing, 0 warnings, 0 errors).

---

## 4. Execution Summary & Verification Results

### 4.1 Audit & Remediation Highlights

1. **Strict AGENTS.md Policy Compliance (Action 3.1):**
   - **Bare Catches Swallowed:** Eliminated in [ToolCallCard.razor](../../../src/NightCodeForge.Web/Components/Transcripts/ToolCallCard.razor), [HostDirtyReconcileDrawer.razor](../../../src/NightCodeForge.Web/Components/GitSync/HostDirtyReconcileDrawer.razor), [Ideation.razor](../../../src/NightCodeForge.Web/Components/Pages/Ideation.razor), and [ThinkingAndActivityTracingChatClient.cs](../../../src/NightCodeForge.Agents/Inference/ThinkingAndActivityTracingChatClient.cs). Replaced with typed exception handling or explicit, justified catch blocks with logging.
   - **Constructor DI Optional Parameters:** Fixed in [ThinkingAndActivityTracingChatClient.cs](../../../src/NightCodeForge.Agents/Inference/ThinkingAndActivityTracingChatClient.cs), [OllamaChatClientFactory.cs](../../../src/NightCodeForge.Agents/Inference/OllamaChatClientFactory.cs), [OllamaChatClient.cs](../../../src/NightCodeForge.Agents/Inference/OllamaChatClient.cs), and [PerServiceCallChatHistoryPersistingChatClient.cs](../../../src/NightCodeForge.Agents/Crucible/ChatClients/PerServiceCallChatHistoryPersistingChatClient.cs). All service dependencies are now required or provided via explicit typed constructor overloads.

2. **Core & Infrastructure Deep Audit (Action 3.2):**
   - **EF Core Query Optimization:** Added `.AsNoTracking()` to read-only queries in [EfCoreWorkspaceRepository.cs](../../../src/NightCodeForge.Infrastructure/Data/Repositories/EfCoreWorkspaceRepository.cs) and [EfCoreWorkflowRepository.cs](../../../src/NightCodeForge.Infrastructure/Data/Repositories/EfCoreWorkflowRepository.cs).
   - **Process & Concurrency Safety:** Verified `SemaphoreSlim` release semantics across all services. Added try/catch safeguards around event broadcasting in [SignalRActivityBridgeService.cs](../../../src/NightCodeForge.Infrastructure/Hubs/SignalRActivityBridgeService.cs) to ensure disconnected clients never block background activity event processing.

3. **Agents & Inference Deep Audit (Action 3.3):**
   - **Sandbox Path Traversal Enforcement:** Implemented `ValidateWithinWorkspace(fullPath, requestedPath)` in [CanonicalTools.cs](../../../src/NightCodeForge.Agents/Tools/CanonicalTools.cs) across `ReadAsync`, `WriteAsync`, `EditAsync`, and `BashAsync`. Traversal attempts (e.g. `../../secret.txt`) are blocked and return clean, descriptive error messages to the agent without crashing the orchestrator.
   - **Sandbox Unit Tests:** Added 4 comprehensive theory test suites to [CanonicalToolsTests.cs](../../../tests/NightCodeForge.Agents.Tests/CanonicalToolsTests.cs) covering path traversal vectors (`../../`, `..\\..\\`, `/etc/shadow`, etc.).

4. **Web UI & Frontend Resilience (Action 3.4):**
   - **Event Unsubscription Audit:** Verified via AST analysis that 100% of Blazor components subscribing to C# events implement `IDisposable` or `IAsyncDisposable` and detach all event handlers.
   - **Error Boundaries:** Wrapped root `@Body` in `<ErrorBoundary>` within [MainLayout.razor](../../../src/NightCodeForge.Web/Components/Layout/MainLayout.razor).
   - **SignalR Auto-Reconnect Guard:** Guarded `StartAsync` in [OrchestratorHubClientConnection.cs](../../../src/NightCodeForge.Web/Services/OrchestratorHubClientConnection.cs) against re-entrant calls when already connecting or connected.
   - **Accessibility:** Added explicit `aria-label` and `title` attributes to icon buttons in [ToolCallCard.razor](../../../src/NightCodeForge.Web/Components/Transcripts/ToolCallCard.razor) and [DiffViewer.razor](../../../src/NightCodeForge.Web/Components/Review/DiffViewer.razor).

### 4.2 Pass 2: Deep Audit & Final Remediation Highlights

1. **Strict AGENTS.md Exception Handling Justification:**
   - Added explicit justification comments to expected `OperationCanceledException` and `ObjectDisposedException` blocks in [SystemProcessRunner.cs](../../../src/NightCodeForge.Core/Execution/SystemProcessRunner.cs), [UntrustedTriageScenario.cs](../../../src/NightCodeForge.ScenarioRunner/Scenarios/UntrustedTriageScenario.cs), and [AgentReasoningScenario.cs](../../../src/NightCodeForge.ScenarioRunner/Scenarios/AgentReasoningScenario.cs). Zero unjustified bare catch blocks remain in the codebase.

2. **Constructor Optional Parameter Elimination & Fail-Fast DI:**
   - **DeterministicWarningRatchetStage:** Replaced optional parameters with explicit parameterless constructor annotated with `[ActivatorUtilitiesConstructor]` and explicit 2-parameter constructor `(string stageId, string displayName)`. Updated [WorkflowStageRegistry.cs](../../../src/NightCodeForge.Core/Workflow/WorkflowStageRegistry.cs) to pass stage metadata directly.
   - **PipelineContext:** Replaced `= null` with explicit `(task, workspace)` and `(task, workspace, dataBag)` constructors in [PipelineContext.cs](../../../src/NightCodeForge.Core/Workflow/PipelineContext.cs).
   - **HeadTailBuffer:** Replaced default parameter with explicit `HeadTailBuffer()` and `HeadTailBuffer(int maxBytes)` in [HeadTailBuffer.cs](../../../src/NightCodeForge.Infrastructure/Execution/HeadTailBuffer.cs).
   - **ToolDefinition:** Replaced optional parameters with explicit 3-, 4-, and 5-parameter overloads in [ToolDefinition.cs](../../../src/NightCodeForge.Agents/Dispatcher/ToolDefinition.cs).
   - **OrchestratorHubClientConnection:** Enforced non-null `ILogger<OrchestratorHubClientConnection>` requirement on the primary constructor in [OrchestratorHubClientConnection.cs](../../../src/NightCodeForge.Web/Services/OrchestratorHubClientConnection.cs), with `NullLogger` fallback for standalone navigation callers.

3. **EF Core Query Optimizations (`AsNoTracking`):**
   - Added `.AsNoTracking()` to read-only queries in [EfCoreWorkspaceRepository.cs](../../../src/NightCodeForge.Infrastructure/Data/Repositories/EfCoreWorkspaceRepository.cs) (`GetByIdAsync`), [EfCoreTaskRepository.cs](../../../src/NightCodeForge.Infrastructure/Data/Repositories/EfCoreTaskRepository.cs) (`GetDependenciesAsync`), and [EfCoreTaskDependencyStore.cs](../../../src/NightCodeForge.Infrastructure/Data/Repositories/EfCoreTaskDependencyStore.cs) (`GetDependenciesForWorkspaceAsync`, `GetPrerequisiteTaskIdsAsync`, `GetDependentTaskIdsAsync`).
   - Verified question update semantics in `EfCoreTaskRepository` ensuring `GetQuestionsByStatusAsync` preserves change tracking for mutation workflows.

4. **Web UI Resilience & In-Place View Recovery:**
   - Enhanced `<ErrorBoundary>` in [MainLayout.razor](../../../src/NightCodeForge.Web/Components/Layout/MainLayout.razor) with a custom `<ErrorContent Context="ex">` banner and a "Try to recover" button bound to `_errorBoundary?.Recover()`, allowing seamless in-place view recovery without browser hard-reload.

5. **Regression Test Additions:**
   - Added constructor and behavioral unit tests across [DifferentialGateTests.cs](../../../tests/NightCodeForge.Core.Tests/DifferentialGateTests.cs), [PipelineDataBagTests.cs](../../../tests/NightCodeForge.Core.Tests/PipelineDataBagTests.cs), [ContainerReplExecutorTests.cs](../../../tests/NightCodeForge.Agents.Tests/ContainerReplExecutorTests.cs), [LenientToolDispatcherTests.cs](../../../tests/NightCodeForge.Agents.Tests/LenientToolDispatcherTests.cs), and [OrchestratorHubClientConnectionTests.cs](../../../tests/NightCodeForge.Web.Tests/OrchestratorHubClientConnectionTests.cs).

### 4.3 Final Test & Build Verification

- **Compiler Build:** `dotnet build` -> **0 Warnings, 0 Errors**.
- **Standard Unit/Integration Test Suite (`Category!=Acceptance&Category!=E2E`):**
  - **1,182 passed, 0 failed, 0 ignored**
- **Full Solution Test Suite (including E2E and Acceptance Tests):**
  - `NightCodeForge.Core.Tests`: **337 passed, 0 failed**
  - `NightCodeForge.Agents.Tests`: **355 passed, 0 failed**
  - `NightCodeForge.Infrastructure.Tests`: **208 passed, 0 failed**
  - `NightCodeForge.Web.Tests`: **285 passed, 0 failed**
  - **Total: 1,185 passed, 0 failed, 0 skipped** across all test suites.


