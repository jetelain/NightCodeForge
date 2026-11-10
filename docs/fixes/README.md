# 🛡️ Bug Fixes, Resiliency & Hardening Roadmap

> **Active Bug Fixes & Resiliency Specifications**  
> *Parent Index:* [docs/PLAN.md](../PLAN.md) | *Completed Fixes Archive:* [docs/archive/11_EXTEND_FEATURES.md](../archive/11_EXTEND_FEATURES.md) (`docs/archive/phase-11/`)  
> *Target Framework:* .NET 10 (C# 13)

---

## 1. Overview

This directory contains the active technical specifications for remaining **bug fixes, system hardening, code factorization, and resilience improvements** (historically tracked under Phase 11).

Once a step is implemented and all unit, integration, and end-to-end tests pass, its specification document is moved to [`docs/archive/phase-11/`](../archive/phase-11/) and recorded in [`docs/archive/11_EXTEND_FEATURES.md`](../archive/11_EXTEND_FEATURES.md).

---

## 2. Remaining Steps

| Step | Specification | Focus & Key Deliverables | Status |
| :--- | :--- | :--- | :--- |
| **11.40** | [🛡️ Step 11.40: Bash Timeout Escalation Ladder, In-Container Process Reaper, Stale Container Recycling & WSL2 Self-Healing](STEP_11_40_BASH_TIMEOUT_CONTAINER_PROCESS_REAPER_AND_WSL_RECOVERY.md) | 3-stage self-healing escalation on `bash` timeout: (1) in-container process sweep (`SIGTERM` $\to$ 5s wait $\to$ `SIGKILL`) preserving `PID 1`/`tail` and whitelisted daemons (`csnav`) + stale `index.lock` cleanup, (2) 5s container liveness probe with container kill/recreate and 5s probe retry, (3) Windows-specific `wsl --shutdown` escalation with Docker daemon readiness check and 30s probe retry, plus `--init` zombie reaping, `--pids-limit 4096` guard, and idle container eviction for `Ready`/`WaitingForHuman`/`Failed` tasks. | Proposed |
| **11.43C** | [⚡ Step 11.43C: `Pmad.AI` Opt-In SLM Delegating Chat Clients (`ToolCallHealing`, `ToolLoopCircuitBreaker`, `ThinkingTraceCompaction` & `ToolResultCompaction`)](STEP_11_43C_PMAD_AI_SLM_DELEGATING_CHAT_CLIENTS_OPT_IN.md) | Expose four opt-in (`default: false`) flags on `LocalOptimizationFlags` (`ToolCallHealing`, `ToolLoopCircuitBreaker`, `ThinkingTraceCompaction`, `ToolResultCompaction`) tailored for small local LLMs ($\le 8\text{B}$–$14\text{B}$), wiring `ToolCallHealingChatClient`, `ToolLoopCircuitBreakerChatClient`, `ThinkingTraceCompactionChatClient`, and `ToolResultCompactionChatClient` into the `IChatClient` pipeline (`MutateInPlace = false`) alongside Settings UI toggles and transcript frontmatter metadata. | Proposed |
| **11.48** | [🛡️ Step 11.48: Large-Context Timeout KV-Cache Compaction Recovery (Pre-Abandonment Compaction)](STEP_11_48_LARGE_CONTEXT_TIMEOUT_KV_CACHE_COMPACTION_RECOVERY.md) | Intercept 30-minute LLM timeouts and timeout retry exhaustion on large contexts ($> 100\text{k}$ tokens) caused by KV cache loss in local inference engines. Trigger pre-abandonment context compaction before failing, compacting the active context down to $\le 96\text{k}$ tokens with deterministic summarizer fallback, releasing stale OllamaRouter session affinity, resetting outage counters, and resuming execution. If compaction fails or post-compaction calls time out, fail fast without looping. | **Next in Line (#3)** |
| **11.49** | [🛡️ Step 11.49: Two-Phase Streaming Idle Watchdog, Output Token Cap (`MaxOutputTokens` / `num_predict`) & Mid-Stream Timeout Diagnostics](STEP_11_49_STREAMING_IDLE_WATCHDOG_OUTPUT_TOKEN_CAP_AND_TIMEOUT_DIAGNOSTICS.md) | Replace single-phase timeout gaps during active LLM streaming with a two-phase `TimeProvider`-aware watchdog (`FirstTokenTimeout = 10m` for prefill + `StreamIdleTimeout = 10m` sliding inactivity window between chunks, with immediate enumerator disposal to unblock `OllamaSharp.ReadLineAsync()`), wire `ChatOptions.MaxOutputTokens` (`num_predict`, default `16,384`) across capability resolvers and `HarnessAgentFactory`, sanitize in-flight turns before mid-stream retries in `CrucibleAgentHarness`, and emit differentiated prefill vs. mid-stream stall/abort diagnostics. | Completed |
| **11.49B** | [🧹 Step 11.49B: Retire `CancellationAwareStream` Workaround Upon `OllamaSharp` PR #400 Upgrade](STEP_11_49B_RETIRE_OLLAMASHARP_STREAM_CANCELLATION_WORKAROUND.md) | Once upstream [awaescher/OllamaSharp#400](https://github.com/awaescher/OllamaSharp/pull/400) (passing `CancellationToken` to `StreamReader.ReadLineAsync` on `.NET 7+`) is released on NuGet, upgrade `OllamaSharp`, delete `CancellationAwareStream` / `CancellationAwareHttpContent`, remove ambient `CancellationToken` propagation from `StreamingCancellationScope`, and verify `StreamingIdleWatchdogIntegrationTests` pass natively. | Proposed (Blocked on upstream release) |
| **11.49C** | [🛡️ Step 11.49C: Streaming Tool-Call Delta Telemetry (`OpenAI`) & 10-Minute `StreamIdleTimeoutMinutes` for Ollama Native Buffering](STEP_11_49C_STREAMING_TOOL_CALL_DELTAS_AND_OLLAMA_BUFFERING_WATCHDOG.md) | Extract incremental tool-call deltas from `OpenAI.Chat.StreamingChatCompletionUpdate.ToolCallUpdates` in `TelemetryChatClient` (since `OpenAIChatClient` buffers `FunctionCallContent` until stream completion) without double-counting on the final synthesized update, and raise `DefaultStreamIdleTimeoutMinutes` from `5` to `10` minutes to protect against Ollama's native `/api/chat` server-side tool-call JSON buffering. | Completed |
| **11.50** | [⚡ Step 11.50: TaskDiskSyncCoordinator & Markdown Sync Performance Hardening ($O(N^2) \to O(N)$ In-Memory Indexing, Hash Diffing & WSL UNC Parallelism)](STEP_11_50_TASK_DISK_SYNC_COORDINATOR_PERFORMANCE_AND_WSL_BATCHING.md) | Eliminate quadratic $O(N^2)$ directory re-scanning and remote network latency spikes over WSL UNC paths (`\\wsl.localhost\Ubuntu\...`) during startup and workspace reconciliation. Pre-index directories in a single $O(N)$ pass, resolve filenames and purge duplicate/stale files via in-memory dictionary lookups, bypass redundant disk/WSL writes when content hashes match, parallelize WSL directory reading with bounded concurrency, and run startup workspace sync concurrently via `Task.WhenAll`. | Completed |

---

## 3. Definition of Done (DoD) Checklist for Remaining Fixes

- [ ] **Step 11.40 (Bash Timeout Escalation Ladder, In-Container Process Reaper, Stale Container Recycling & WSL2 Self-Healing):**
  - [ ] Add `ProcessKillWhitelist` (defaulting to `["csnav"]`) and `PidsLimit` (defaulting to `4096`) to `ContainerExecutorOptions`, and include `--init` and `--pids-limit` in `BuildDockerRunArgs`.
  - [ ] Implement Step 1 in-container process reaper (`SIGTERM` $\to$ 5s wait $\to$ `SIGKILL`) on `bash` timeout, preserving `PID 1`/`tail` and whitelisted processes and purging stale `index.lock` files.
  - [ ] Implement Step 2 container liveness probe (5s timeout), container force-recreation (`docker rm -f` + `EnsureContainerRunning`), and probe retry (5s timeout).
  - [ ] Implement Step 3 Windows-specific WSL2 shutdown escalation (`wsl --shutdown`), Docker daemon readiness check, and 30s probe retry.
  - [ ] Expand `TaskContainerReaper` and lifecycle hooks to tear down containers for `Failed`, `Ready`, and `WaitingForHuman` tasks and evict idle workspace containers.
  - [ ] Author unit tests in `NightCodeForge.Infrastructure.Tests` and `NightCodeForge.Core.Tests`.

- [ ] **Step 11.43C (`Pmad.AI` Opt-In SLM Delegating Chat Clients — `ToolCallHealing`, `ToolLoopCircuitBreaker`, `ThinkingTraceCompaction` & `ToolResultCompaction`):**
  - [ ] Add `ToolCallHealing`, `ToolLoopCircuitBreaker`, `ThinkingTraceCompaction`, and `ToolResultCompaction` (`default: false`, opt-in) to `LocalOptimizationFlags` and `LocalOptimizationFlags.Clone()`.
  - [ ] Wire `ThinkingTraceCompactionChatClient`, `ToolResultCompactionChatClient`, `ToolCallHealingChatClient`, and `ToolLoopCircuitBreakerChatClient` into the `IChatClient` pipeline in `CrucibleAgentHarness` and `IdeationService` (preserving uncompacted `chatHistory` via `MutateInPlace = false`).
  - [ ] Update `SessionTranscriptService`, `TranscriptBrowserService`, `TranscriptMetadataCard.razor`, `GlobalOptimizationSettingsTab.razor`, and `AgentProfilesTab.razor` to serialize, parse, display, and toggle the four opt-in SLM flags.
  - [ ] Add unit tests in `NightCodeForge.Agents.Tests` and `NightCodeForge.Core.Tests` verifying default opt-out behavior, opt-in activation, non-destructive history preservation, and transcript frontmatter round-tripping.
  - [ ] Verify all non-E2E tests pass via `dotnet test --filter "Category!=Acceptance&Category!=E2E"`.

- [ ] **Step 11.48 (Large-Context Timeout KV-Cache Compaction Recovery — Pre-Abandonment Compaction):**
  - [ ] Add `EnableTimeoutRecoveryCompaction` (default: `true`) and `LargeContextTimeoutRecoveryThresholdTokens` (default: `100_000`) to `CrucibleCompactionOptions`.
  - [ ] Expose an emergency compaction entry point in `PiStyleCrucibleCompactionStrategy` with automatic fallback to `GenerateDeterministicSummary` if the summarizer client times out or fails.
  - [ ] Implement pre-abandonment timeout recovery in `CrucibleAgentHarness` for both streaming and non-streaming loops before `FailAsync`, validating HTTP timeout, context size $\ge 100\text{k}$ tokens, and single-attempt guard per turn.
  - [ ] On successful compaction, update `chatHistory`, persist the compaction checkpoint turn in `ConversationTurns`, reset outage timestamps and timeout retry counters, broadcast notice telemetry, and resume the agent loop.
  - [ ] On compaction failure or subsequent timeout, fail fast with `FailAsync` without infinite loops.
  - [ ] Author unit tests in `NightCodeForge.Agents.Tests` covering threshold gating, successful recovery and resumption, deterministic fallback, compaction failure escalation, and post-compaction timeout fail-fast behavior.
  - [ ] Verify all non-E2E tests pass via `dotnet test --filter "Category!=Acceptance&Category!=E2E"`.

- [x] **Step 11.49 (Two-Phase Streaming Idle Watchdog, Output Token Cap & Mid-Stream Timeout Diagnostics):**
  - [x] Add `StreamIdleTimeoutSeconds` (default `300` / 5 minutes) and update `DefaultMaxOutputTokens` to `16384` on `LlmProviderOptions`; add `StreamIdleTimeout` (`5 minutes` default) to `LlmRetryPolicyOptions`.
  - [x] Wire `ChatOptions.MaxOutputTokens` (`num_predict` / `max_tokens`) across `OllamaCapabilityResolver`, `OpenAiCapabilityResolver`, `HarnessAgentFactory` (with optional `AgentProfile.MaxOutputTokens` override), and `PiStyleCrucibleCompactionStrategy`.
  - [x] Implement the `TimeProvider`-driven two-phase streaming watchdog (10-minute `FirstTokenTimeout` for the first chunk, 5-minute sliding `StreamIdleTimeout` for subsequent chunks) with immediate enumerator disposal on timeout so `OllamaSharp`'s token-less `ReadLineAsync()` is deterministically unblocked.
  - [x] Enrich streaming timeout and premature stream-abort exceptions with `TimeoutPhase`, elapsed duration, generated token count, and tok/s rate, and surface differentiated diagnostics in `TelemetryChatClient` and `HttpErrorDetailHelper`.
  - [x] Invoke `SanitizeInFlightTurns(chatHistory)` inside `CrucibleAgentHarness`'s streaming retry catch block before re-running the stream.
  - [x] Add fake-`HttpClient` (`HttpMessageHandler`) streaming integration tests with lower timeout ceilings covering all 4 streaming scenarios (slow first token within budget + long active stream beyond `FirstTokenTimeout`, first-token timeout, mid-stream pause within budget, and mid-stream pause exceeding `StreamIdleTimeout` unblocking `OllamaSharp.ReadLineAsync()`) plus outbound `"num_predict"` payload verification.
  - [x] Add unit tests in `NightCodeForge.Agents.Tests` and `NightCodeForge.Core.Tests` covering default 10m/5m ceilings with `FakeTimeProvider`, `MaxOutputTokens` resolution precedence, mid-stream retry sanitization, and differentiated timeout messages.
  - [x] Verify all non-E2E tests pass via `dotnet test --filter "Category!=Acceptance&Category!=E2E"`.

- [ ] **Step 11.49B (Retire `CancellationAwareStream` Workaround Upon `OllamaSharp` PR #400 Upgrade):**
  - [ ] Upgrade `OllamaSharp` to the NuGet release containing [awaescher/OllamaSharp#400](https://github.com/awaescher/OllamaSharp/pull/400).
  - [ ] Delete `src/NightCodeForge.Core/Resilience/CancellationAwareStream.cs` and remove `CancellationAwareHttpContent` wrapping from `TransientHttpErrorRetryHandler.SendAsync`.
  - [ ] Remove ambient `CancellationToken` propagation from `StreamingCancellationScope`, keeping only attempt/header lifecycle tracking for `PreHeaderPrefill` vs. `FirstTokenPrefill` diagnostics.
  - [ ] Remove `docs/fixes/OLLAMASHARP_STREAMING_CANCELLATION_PR_SPEC.md`.
  - [ ] Verify all `StreamingIdleWatchdogIntegrationTests` and non-E2E tests pass via `dotnet test --filter "Category!=Acceptance&Category!=E2E"`.

- [x] **Step 11.49C (Streaming Tool-Call Delta Telemetry (`OpenAI`) & 10-Minute `StreamIdleTimeoutMinutes` for Ollama Native Buffering):**
  - [x] Update `LlmProviderOptions.DefaultStreamIdleTimeoutMinutes` and `LlmRetryPolicyOptions.DefaultStreamIdleTimeoutMinutes` from `5` to `10` minutes.
  - [x] Extract live tool-call argument deltas from `OpenAI.Chat.StreamingChatCompletionUpdate.ToolCallUpdates` in `TelemetryChatClient.GetStreamingResponseAsync` and update `HasNonEmptyStreamContent`.
  - [x] Prevent token double-counting when `OpenAIChatClient` emits the final synthesized `FunctionCallContent` at stream completion while replacing raw JSON fragments in `CompletionPreview` with the formatted tool call preview.
  - [x] Add unit tests in `TelemetryChatClientTests` verifying live token increments across `StreamingChatCompletionUpdate.ToolCallUpdates` chunks and deduplication on the final `FunctionCallContent` update.
  - [x] Verify all non-E2E tests pass via `dotnet test --filter "Category!=Acceptance&Category!=E2E"`.

- [x] **Step 11.50 (TaskDiskSyncCoordinator & Markdown Sync Performance Hardening — $O(N^2) \to O(N)$ In-Memory Indexing, Hash Diffing & WSL UNC Parallelism):**
  - [x] Implement upfront directory indexing in `TaskMarkdownSyncService.cs` (`DirectoryTaskIndex` / snapshot).
  - [x] Refactor `SyncAllTasksToDiskAsync` to resolve filenames and detect duplicate/stale files in $O(1)$ from the index.
  - [x] Implement write-skipping when generated markdown hash matches the existing index hash for both primary and clone targets.
  - [x] Parallelize remote WSL clone directory reading with bounded concurrency (`Parallel.ForEachAsync`).
  - [x] Refactor workspace startup sync loop in `Program.cs` to run concurrently with `Task.WhenAll`.
  - [x] Ensure all unit tests in `NightCodeForge.Core.Tests` and `NightCodeForge.Web.Tests` pass.
  - [x] Verify test suite passes with `dotnet test --filter "Category!=Acceptance&Category!=E2E"`.

---

## 4. Completed Fixes Archive

All 39 previously completed Phase 11 onboarding, hardening, and bug-fix steps (`11.2` through `11.47`) are archived in:
- **Summary & DoD Record:** [`docs/archive/11_EXTEND_FEATURES.md`](../archive/11_EXTEND_FEATURES.md)
- **Step Specifications:** [`docs/archive/phase-11/`](../archive/phase-11/)
