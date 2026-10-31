# Step 12.25B — Multi-Worker Telemetry UI, Worker Status Cards & Concurrency Settings

**Status:** Planned (Feature / Multi-Server & Multi-GPU Scaling — Web UI)
**Prerequisite:** Step 12.25A

---

## 1. Problem Statement

Once the backend supports $N$ concurrent inference workers (Step 12.25A), several Web UI services and components that were originally designed around a single active task/call need to be updated:

1. **[`OperationsTelemetryUiService`](../../src/NightCodeForge.Web/Services/OperationsTelemetryUiService.cs) Single-Slot Overwriting:**
   - `OperationsTelemetryUiService` exposes singular `ActiveLlmCall`, `ActiveShellCommand`, and `ActiveTask` properties. When Worker #2 starts an LLM call or shell command while Worker #1 is still streaming, `ActiveLlmCall` is overwritten by Worker #2's call, and when Worker #2 finishes, the UI briefly jumps or loses track of live progress if not indexed by `CallId` / `CommandId` / `TaskId`.
2. **[`WorkerStateService`](../../src/NightCodeForge.Web/Services/WorkerStateService.cs) & [`WorkerStatusCard.razor`](../../src/NightCodeForge.Web/Components/Monitoring/WorkerStatusCard.razor):**
   - `WorkerStateService.Current` holds a single `ActiveTaskId` and `ActiveTaskTitle`. When 2 tasks run concurrently across 2 Ollama servers, the sidebar/monitoring worker status card only shows whichever task started most recently.
3. **[`ActiveOperationsHero.razor`](../../src/NightCodeForge.Web/Components/Monitoring/ActiveOperationsHero.razor) Single-Item Cards:**
   - The three hero cards (`In-Flight LLM Inference`, `In-Flight Shell Operation`, and `In-Flight Autonomous Task`) only render a single `TelemetryUi.ActiveLlmCall`, `TelemetryUi.ActiveShellCommand`, and `TelemetryUi.ActiveTask`, hiding the second concurrent GPU server's inference stream, target backend node, and token speed.
4. **No In-App Setting for `MaxConcurrentInferenceTasks`:**
   - Users with multiple Ollama servers or multi-GPU setups need a straightforward control in `/settings` to configure `MaxConcurrentInferenceTasks` ($N$) and see how many worker slots are currently active (`ActiveTasks.Count / MaxConcurrentInferenceTasks`).

---

## 2. Architectural Goals

1. **Multi-Slot State Management in `OperationsTelemetryUiService` & `WorkerStateService`:**
   - Track all concurrent `ActiveLlmCalls`, `ActiveShellCommands`, and `ActiveTasks` keyed by ID so real-time streaming progress (`HandleLlmCallProgress`, `HandleShellCommandOutput`) updates the exact matching in-flight item.
2. **Multi-Operation Rendering in `ActiveOperationsHero.razor` & `WorkerStatusCard.razor`:**
   - Display all concurrent in-flight LLM calls (with each call's model, `OllamaRouter` target backend badge, tok/s, and prompt preview), shell commands, and autonomous tasks in `ActiveOperationsHero.razor` and `WorkerStatusCard.razor`.
3. **Concurrent Inference Workers Setting in `/settings`:**
   - Add a **Max Concurrent Inference Tasks (`WorkerCount`)** configuration input in `/settings` persisted to `config/config.json` via `IUserConfigurationWriter`.

---

## 3. Required Specification & Behavioral Contracts

### 3.1 `OperationsTelemetryUiService` Multi-Item Tracking

Extend [`OperationsTelemetryUiService`](../../src/NightCodeForge.Web/Services/OperationsTelemetryUiService.cs):

- Expose plural read-only collections:
  - `IReadOnlyList<LlmCallRecord> ActiveLlmCalls { get; }`
  - `IReadOnlyList<ShellCommandRecord> ActiveShellCommands { get; }`
  - `IReadOnlyList<ActiveTaskSummary> ActiveTasks { get; }`
- Keep singular `ActiveLlmCall`, `ActiveShellCommand`, and `ActiveTask` as computed convenience properties (`ActiveLlmCalls.FirstOrDefault()`, etc.) for backward compatibility.
- In `HydrateFromService()`, populate `ActiveLlmCalls`, `ActiveShellCommands`, and `ActiveTasks` from `IOperationsTelemetryService.GetActiveLlmCalls()`, `GetActiveShellCommands()`, and `GetActiveTasks()`.
- In `HandleLlmCallStarted`, `HandleLlmCallProgress`, and `HandleLlmCallCompleted`, maintain all running calls in `ActiveLlmCalls` by `CallId` so concurrent token-per-second and token-count updates for Call A and Call B update independently in real time.
- In `HandleShellCommandStarted`, `HandleShellCommandOutput`, and `HandleShellCommandCompleted`, maintain all running commands in `ActiveShellCommands` by `CommandId`.
- In `HandleActiveTaskChanged`, synchronize `ActiveTasks` with `IOperationsTelemetryService.GetActiveTasks()`.

### 3.2 `WorkerStateService` & `WorkerStatusCard.razor`

- Extend [`WorkerStatusSnapshot`](../../src/NightCodeForge.Web/Services/WorkerStateService.cs) and [`WorkerStateService`](../../src/NightCodeForge.Web/Services/WorkerStateService.cs) to expose:
  - `IReadOnlyList<ActiveTaskSummary> ActiveTasks` (all currently running autonomous tasks),
  - `int ActiveTaskCount` (`ActiveTasks.Count`),
  - `int ConfiguredWorkerCount` (from `LlmProviderOptions.MaxConcurrentInferenceTasks`).
- Update [`WorkerStatusCard.razor`](../../src/NightCodeForge.Web/Components/Monitoring/WorkerStatusCard.razor) to display worker utilization (e.g., `Running (2 / 2 Workers)` when 2 tasks are active) and list all active tasks with links to their respective Task Inspectors.

### 3.3 `ActiveOperationsHero.razor` Multi-Slot Cards

Update [`ActiveOperationsHero.razor`](../../src/NightCodeForge.Web/Components/Monitoring/ActiveOperationsHero.razor):

1. **In-Flight LLM Inference Card (`#active-llm-card`):**
   - Header badge shows `● @TelemetryUi.ActiveLlmCalls.Count Inferring` when `ActiveLlmCalls.Count > 1` (or `● Inferring` when `1`).
   - Renders each active call in `TelemetryUi.ActiveLlmCalls` (showing `@call.Model`, `@call.TargetBackend` route badge, context tokens, tok/s speed, completion/reasoning tokens, elapsed duration, and prompt preview) separated by a subtle divider when multiple calls are in flight simultaneously.
2. **In-Flight Shell Operation Card (`#active-shell-card`):**
   - Header badge shows `● @TelemetryUi.ActiveShellCommands.Count Executing` when `ActiveShellCommands.Count > 1` (or `● Executing` when `1`).
   - Renders each active shell command in `TelemetryUi.ActiveShellCommands`.
3. **In-Flight Autonomous Task Card (`#active-task-card`):**
   - Header badge shows `Running (@TelemetryUi.ActiveTasks.Count)` when multiple tasks are executing concurrently.
   - Renders each active task in `TelemetryUi.ActiveTasks` with its `#TaskId`, `TaskTitle`, `CurrentStageId` badge, `Priority`, and `🔍 Inspector` / `📋 Details` buttons.

### 3.4 Concurrency Configuration in `/settings`

- In [`Settings.razor`](../../src/NightCodeForge.Web/Components/Pages/Settings.razor) (or the LLM Provider / [`OllamaRouterGatewayCard.razor`](../../src/NightCodeForge.Web/Components/Settings/OllamaRouterGatewayCard.razor) settings section):
  - Add a numeric input (`id="input-max-concurrent-tasks"`, min `1`, max `16`) bound to `LlmProvider:MaxConcurrentInferenceTasks`.
  - Persist changes to `config/config.json` via `IUserConfigurationWriter` and display an informational toast/banner indicating when a restart is required for Hangfire server worker count changes to take effect.

---

## 4. Verification Criteria

- Unit and bUnit tests in `NightCodeForge.Web.Tests` verify:
  - `OperationsTelemetryUiService` tracks 2 simultaneous `LlmCallRecord` streams (`call-1` on Node A and `call-2` on Node B), updating each call's `CompletionTokens` and `TokensPerSecond` independently and keeping `call-1` in `ActiveLlmCalls` when `call-2` completes.
  - `ActiveOperationsHero.razor` renders both concurrent LLM calls, shell commands, and autonomous tasks when 2 workers are active.
  - `WorkerStateService` and `WorkerStatusCard.razor` accurately display multiple active tasks and worker slot utilization (`2 / 2`).
  - Saving `MaxConcurrentInferenceTasks` in `/settings` writes `LlmProvider:MaxConcurrentInferenceTasks` via `IUserConfigurationWriter`.

