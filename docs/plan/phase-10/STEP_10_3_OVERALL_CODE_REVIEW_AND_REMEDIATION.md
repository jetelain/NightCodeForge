# 🔍 Step 10.3: Comprehensive Code Review & AGENTS.md Hardening

> **Phase 10 Implementation Step**  
> *Parent Plan:* [Phase 10 Master Plan](../10_MVP_FINAL_POLISH.md) | *Source Policy:* [AGENTS.md](../../../AGENTS.md)  
> *Status:* Ready for Execution | *Target Framework:* .NET 10 (C# 13)  

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
