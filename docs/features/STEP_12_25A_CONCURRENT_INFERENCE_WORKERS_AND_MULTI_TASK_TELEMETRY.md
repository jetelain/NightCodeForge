# Step 12.25A — Configurable $N$-Worker LLM Inference Concurrency & Multi-Task Telemetry

**Status:** Planned (Feature / Multi-Server & Multi-GPU Scaling)

---

## 1. Problem Statement

NightCodeForge's task orchestration pipeline was architected with per-task isolation in mind:
- **Hierarchical Workspace & Task Locking ([`WorkspaceLockProvider`](../../src/NightCodeForge.Core/Concurrency/WorkspaceLockProvider.cs)):** [`TaskOrchestrator.ProcessTaskAsync`](../../src/NightCodeForge.Core/Workflow/TaskOrchestrator.cs) holds a task-level lock (`AcquireTaskLockAsync(workspaceId, taskId)`) during LLM inference, build, and test stages, and only acquires the workspace-level lock (`AcquireLockAsync(workspaceId)`) briefly during Git mirror initialization/fetch, worktree creation, and final merge into the base branch.
- **Per-Task Git Worktrees & Containers ([`GitWorkspaceManager`](../../src/NightCodeForge.Infrastructure/Git/GitWorkspaceManager.cs), [`ExecutionEnvironmentProvider`](../../src/NightCodeForge.Infrastructure/Execution/ExecutionEnvironmentProvider.cs)):** Every task executes in its own isolated Git worktree (`worktrees/task-{taskId}`) on branch `feature/task-{taskId}` and its own container/environment keyed by `(WorkspaceId, SecurityTier, TaskId)`.
- **Scoped DI & Per-Task Cancellation:** Harnesses, `TaskOrchestrator`, and EF Core `AppDbContext` instances are `Scoped` per Hangfire job, and `TaskCancellationManager` tracks tokens by `taskId`.
- **Optimistic Concurrency & Base-Branch Drift Recovery:** `TaskOrchestrator` already handles concurrent `docs/tasks/*.md` sync retries and detects when `main` advances while a sibling task is running (auto-merging `main` into `feature/task-{id}` and re-running verification gates or initiating conflict resolution).

However, when scaling to **2 or more dedicated Ollama inference servers** (dispatched via `OllamaRouter` or an OpenAI-compatible router), three backend bottlenecks prevent running $N$ inference tasks concurrently:

1. **Hardcoded `WorkerCount = 1` in [`HangfireConfigurationExtensions`](../../src/NightCodeForge.Infrastructure/BackgroundJobs/HangfireConfigurationExtensions.cs):**
   - `CreateLlmInferenceServerOptions()` hardcodes `WorkerCount = 1` on `NCF-LLM-Inference-Worker`, serializing all `[llm-urgent, llm-code-review, llm-implementation, llm-task-review, llm-inference]` jobs even when multiple GPU servers are available.
2. **Single-Item Active Telemetry APIs in [`IOperationsTelemetryService`](../../src/NightCodeForge.Core/Interfaces/IOperationsTelemetryService.cs):**
   - Although `OperationsTelemetryService` internally stores `_activeTasks[taskId]` in a dictionary and maintains ring buffers of `_llmCalls` and `_shellCommands`, its query methods (`GetActiveTask()`, `GetActiveLlmCall()`, `GetActiveShellCommand()`) only return a single item (`.MaxBy(t => t.StartedAt)` or `.FirstOrDefault(r => r.Status == Running)`).
   - Furthermore, `ClearActiveTask(int? taskId)` broadcasts only a single `remaining` task over SignalR (`ReceiveActiveTask`), causing listeners to lose track of which specific task finished when $N > 1$ tasks are running concurrently.
3. **SQLite Busy Timeout & `BareHost` Shared-Directory Safety:**
   - Under $N$ concurrent workers writing conversation turns, checkpoints, and execution logs simultaneously, SQLite connections need an explicit `Default Timeout` / `PRAGMA busy_timeout` so concurrent writes wait cleanly on the WAL lock rather than throwing `SQLITE_BUSY`.
   - For workspaces configured with `ExecutionMode.BareHost` (which operate directly on `workspace.RepositoryPath` without per-task ext4 worktrees), concurrent tasks in the same `BareHost` workspace must remain serialized at the workspace lock level so two agents never checkout different branches in the same working tree simultaneously.

---

## 2. Architectural Goals

1. **Configurable `MaxConcurrentInferenceTasks` (`WorkerCount = N`):**
   - Allow configuring the number of concurrent LLM inference workers (`MaxConcurrentInferenceTasks`, default `1`, range `1..16`) in `LlmProviderOptions` (`config/config.json` / `appsettings.json`) and wire it into `HangfireConfigurationExtensions.CreateLlmInferenceServerOptions(int workerCount)` and `AddNightCodeForgeHangfireServers(int inferenceWorkerCount)`.
2. **Multi-Task & Multi-Operation Telemetry APIs:**
   - Extend `IOperationsTelemetryService` (`OperationsTelemetryService`) with plural query APIs (`GetActiveTasks()`, `GetActiveLlmCalls()`, `GetActiveShellCommands()`) and task-specific SignalR lifecycle notifications so all $N$ concurrent tasks, LLM calls, and shell commands are tracked without overwriting each other.
3. **SQLite WAL `busy_timeout` & `BareHost` Mutual Exclusion:**
   - Guarantee `busy_timeout` (`30,000 ms`) on SQLite connections and enforce workspace-level serialization when a workspace uses `ExecutionMode.BareHost` without isolated task worktrees.

---

## 3. Required Specification & Behavioral Contracts

### 3.1 Configurable Hangfire Inference Worker Count

1. **Configuration Property:**
   - Add `public int MaxConcurrentInferenceTasks { get; set; } = 1;` to `LlmProviderOptions` (and `ILlmProviderOptions` / `LlmEndpointOptions` as appropriate).
   - Clamp values `< 1` to `1` and values `> 16` to `16`.
2. **`HangfireConfigurationExtensions` Updates:**
   - Update `CreateLlmInferenceServerOptions(int workerCount = 1)` to set `WorkerCount = Math.Clamp(workerCount, 1, 16)`.
   - Update `AddNightCodeForgeHangfireServers(this IServiceCollection services, int inferenceWorkerCount = 1)` to pass `inferenceWorkerCount` to `CreateLlmInferenceServerOptions(inferenceWorkerCount)`.
   - In `Program.cs`, resolve `llmOptions.MaxConcurrentInferenceTasks` from configuration and pass it to `AddNightCodeForgeHangfireServers(llmOptions.MaxConcurrentInferenceTasks)`.
   - Update `ARCHITECTURE.md` and `PLAN.md` references to note that `NCF-LLM-Inference-Worker` defaults to `WorkerCount = 1` and scales to `N` concurrent workers when backed by multi-node `OllamaRouter` or parallel inference endpoints.

### 3.2 Multi-Task & Multi-Operation Telemetry in `IOperationsTelemetryService`

Extend [`IOperationsTelemetryService`](../../src/NightCodeForge.Core/Interfaces/IOperationsTelemetryService.cs) and [`OperationsTelemetryService`](../../src/NightCodeForge.Infrastructure/Telemetry/OperationsTelemetryService.cs):

1. **Plural Active Snapshot Methods:**
   - `IReadOnlyList<ActiveTaskSummary> GetActiveTasks()`: returns all currently active autonomous tasks in `_activeTasks.Values`, ordered by `StartedAt` ascending (oldest running first, or consistent chronological order).
   - `IReadOnlyList<LlmCallRecord> GetActiveLlmCalls()`: returns all records in `_llmCalls` with `Status == LlmCallStatus.Running`, ordered by `StartedAt` ascending.
   - `IReadOnlyList<ShellCommandRecord> GetActiveShellCommands()`: returns all records in `_shellCommands` with `Status == ShellCommandStatus.Running`, ordered by `StartedAt` ascending.
2. **Task-Specific Clear & SignalR Notification:**
   - Preserve `GetActiveTask(int? taskId = null)`, `GetActiveLlmCall()`, and `GetActiveShellCommand()` for callers expecting a single primary item.
   - Add `event Action<IReadOnlyList<ActiveTaskSummary>>? OnActiveTasksChanged;` (or include the full active task list / cleared `taskId` in SignalR broadcasts) so UI consumers can accurately add, update, and remove individual active tasks when $N$ tasks run in parallel.

### 3.3 SQLite `busy_timeout` & `BareHost` Concurrency Guard

1. **SQLite `busy_timeout`:**
   - In [`AppDbContext.EnsureDatabaseCreatedAndWalAsync`](../../src/NightCodeForge.Infrastructure/Data/AppDbContext.cs) and SQLite connection initialization, ensure `PRAGMA busy_timeout = 30000;` (and `Default Timeout=30` in `NormalizeSqliteConnectionString`) is configured so $N$ concurrent workers never fail with `SQLITE_BUSY` during simultaneous turn/checkpoint writes.
2. **`BareHost` Workspace Serialization:**
   - In `TaskOrchestrator.ProcessTaskAsync`, if `workspace.ExecutionMode == ExecutionMode.BareHost` (where tasks share `workspace.RepositoryPath` rather than per-task worktrees under `~/.nightcodeforge/wks/{id}/worktrees/task-{taskId}`), acquire the workspace-level lock (`_workspaceLockProvider.AcquireLockAsync(workspace.Id, cancellationToken)`) for the task run so two `BareHost` tasks in the same workspace never clobber the shared git working tree. Workspaces using `WslDirect` or `ContainerSandbox` continue using fine-grained per-task locks (`AcquireTaskLockAsync`) for full parallel execution.

---

## 4. Verification Criteria

- Unit tests in `NightCodeForge.Infrastructure.Tests` and `NightCodeForge.Core.Tests` verify:
  - `CreateLlmInferenceServerOptions(workerCount)` sets `WorkerCount` to the configured $N$ (and clamps invalid values to `1..16`).
  - `OperationsTelemetryService.GetActiveTasks()`, `GetActiveLlmCalls()`, and `GetActiveShellCommands()` track $N$ concurrent running items and remove only the completed item when one finishes while others remain running.
  - Two tasks in `ContainerSandbox` / `WslDirect` mode execute concurrently across 2 workers without blocking each other's inference/build stages, while two tasks in the same `BareHost` workspace serialize cleanly.

