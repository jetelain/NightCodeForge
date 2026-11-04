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
| **11.37A** | [🛡️ Step 11.37A: HTTP Transport Hardening, `ConnectionRefused` Classification & 5-Minute Outage Retry Policy](STEP_11_37A_HTTP_TRANSPORT_HARDENING_AND_OUTAGE_RETRY_POLICY.md) | Dedicated LLM 5-minute outage survival budget (`LlmRetryPolicyOptions`: `MaxRetryDuration = 5m`, `MaxDelay = 30s`, `MaxRetries = 20`) with live UI activity notice hooks and `Retry-After` compliance in `TransientHttpErrorRetryHandler`, fast UI liveness probes separation (`ILlmGatewayService`, `ILlmModelCatalogService`), `TransientErrorDetector` classification of `SocketError.ConnectionRefused` and DNS/network transition errors, disabling `System.ClientModel` duplicate retries, and `SocketsHttpHandler` hardening (`ConnectTimeout = 10s`, OS TCP Keep-Alive via `ConnectCallback`, 15-minute `HttpClient.Timeout`). | Implemented |
| **11.38B** | [🧱 Step 11.38B: Shared Base Class for `AcpAgentHarness` & `PiRpcAgentHarness` (`ExternalProcessAgentHarnessBase`)](STEP_11_38B_EXTERNAL_PROCESS_AGENT_HARNESS_BASE_AND_ACP_SLIMMING.md) | Extract `ExternalProcessAgentHarnessBase` to eliminate ~180 lines of duplicated process spawning, `HarnessConfigurationJson` parsing, Windows/WSL path resolution, prompt enrichment, activity broadcasting, and transcript persistence between `AcpAgentHarness` and `PiRpcAgentHarness`; extract ACP `fs/*` and `terminal/*` callbacks into `IAcpHostRpcHandler`, reducing `AcpAgentHarness` from 11 to `<= 8` constructor parameters. | Proposed |
| **11.38C** | [🧩 Step 11.38C: Shared Agent Session & Toolset Factorization for `CrucibleAgentHarness`, `IdeationService` & `CanonicalTools`](STEP_11_38C_CRUCIBLE_AND_IDEATION_SHARED_SESSION_AND_TOOLSET_FACTORIZATION.md) | Introduce `CanonicalToolScope` and `ICanonicalToolsetFactory` (eliminating 6 pass-through dependencies from callers), `IAgentWorkspaceContextService` (shared across Crucible, Ideation, and Triage), `IHarnessAgentFactory` (shared model/compaction/options wiring), and `ICrucibleConversationCoordinator` (conversation resumption, priming, `TurnWriter`, and transcripts), reducing `CrucibleAgentHarness` (`23 -> <= 8`), `IdeationService` (`19 -> <= 8`), and `CanonicalTools` (`13 -> <= 8`) constructor parameters. | Proposed |
| **11.38D** | [🏗️ Step 11.38D: `TaskOrchestrator` & `StartupRecoveryService` Decomposition and Zero-Exception `<= 10` Constructor Parameter Enforcement](STEP_11_38D_TASK_ORCHESTRATOR_AND_STARTUP_RECOVERY_DECOMPOSITION.md) | Decompose `TaskOrchestrator` (`19 -> <= 9` params) by extracting `ITaskWorkspacePreparer`, `ITaskMergeAndCascadeCoordinator`, and `ITaskLifecycleMutationCoordinator`; decompose `StartupRecoveryService` (`11 -> <= 8` params) by extracting `IWorkspaceGitRecoveryService`; extract `IContainerEnvironmentFactory` from `ExecutionEnvironmentProvider` (`10 -> <= 5` params); and remove the legacy allowlist from `ConstructorParameterCeilingTests` so zero classes in the solution exceed 10 constructor parameters. | Proposed |
| **11.40** | [🛡️ Step 11.40: Bash Timeout Escalation Ladder, In-Container Process Reaper, Stale Container Recycling & WSL2 Self-Healing](STEP_11_40_BASH_TIMEOUT_CONTAINER_PROCESS_REAPER_AND_WSL_RECOVERY.md) | 3-stage self-healing escalation on `bash` timeout: (1) in-container process sweep (`SIGTERM` $\to$ 5s wait $\to$ `SIGKILL`) preserving `PID 1`/`tail` and whitelisted daemons (`csnav`) + stale `index.lock` cleanup, (2) 5s container liveness probe with container kill/recreate and 5s probe retry, (3) Windows-specific `wsl --shutdown` escalation with Docker daemon readiness check and 30s probe retry, plus `--init` zombie reaping, `--pids-limit 4096` guard, and idle container eviction for `Ready`/`WaitingForHuman`/`Failed` tasks. | Proposed |
| **11.41** | [🛡️ Step 11.41: Task Review Specification Enforcement, Conversational Remediation & Unhandled Exception Shielding](STEP_11_41_TASK_REVIEW_SPECIFICATION_ENFORCEMENT_AND_CONVERSATIONAL_REMEDIATION.md) | Prevent premature pipeline aborts when `task-review` concludes without editing `docs/tasks/*.md`: replace fatal `WorkflowExecutionException` with in-session conversational continuation (injecting a high-priority user turn reminding the agent to update the specification or call `ask`), resume the conversation preserving 160+ turns of deep investigation context, shield against unhandled crashes, and harden stage directives against plain-text questioning. | Implemented |
| **11.42** | [🛡️ Step 11.42: Crucible Harness False-Completion Prevention, In-Session Conversational Continuation & Stage Artifact Gates](STEP_11_42_CRUCIBLE_HARNESS_FALSE_COMPLETION_PREVENTION_AND_CONVERSATIONAL_CONTINUATION.md) | Prevent premature stage completion when agents emit truncated mid-thought output or finish `implementation` without modifying code: add an in-harness multi-turn continuation loop in `CrucibleAgentHarness` with `InterruptedGenerationDetector` (`error=EOF` / unclosed fences & tags) and a 3-strike truncation circuit breaker, and enforce a harness-agnostic `Implementation` non-scratch diff gate in `TaskOrchestrator` (`implementation:empty-diff`) sharing Step 11.41's conversational remediation mechanism. | Proposed |
| **11.43A** | [🛡️ Step 11.43A: `Pmad.AI` 0.0.2 Upgrade & `ReadOnlyCommandValidator` Guardrail Migration](STEP_11_43A_PMAD_AI_READONLY_COMMAND_VALIDATOR_MIGRATION.md) | Add `Pmad.AI` `0.0.2` and replace the in-repo `ReadOnlyCommandValidator` (~382 lines) with `Pmad.AI.Guardrails.ReadOnlyCommandValidator` (~1,280 lines), configured with NightCodeForge's `AllowedBinaries` and a custom `csnav` `CommandValidationRule` (allowing read-only subcommands and `server status|start|reload` while blocking `skills` and `server stop`), closing shell escape hatches (`find -exec`/`-delete`, `rg --pre`, `sed -i`, `git -c` / branch / tag / remote mutations, process substitution, and variable expansion). | Proposed |
| **11.43B** | [🧩 Step 11.43B: `Pmad.AI` `LenientToolDispatcher` & Delegating Chat Client (`LenientToolDispatchChatClient`) Migration](STEP_11_43B_PMAD_AI_LENIENT_TOOL_DISPATCHER_AND_DELEGATING_CHAT_CLIENT_MIGRATION.md) | Replace ~1,450 lines of in-repo duplicated tool dispatching and XML tool-call chat client code (`LenientToolDispatcher`, `LenientToolDispatcher.Backlog`, `ToolDefinition`, `LenientToolCallChatClient`) with `Pmad.AI.Tools.LenientToolDispatcher`, declarative `ToolDispatcherConfiguration`, and `LenientToolDispatchChatClient` (in-flight `FunctionCallContent` alias & shell translation rewriting), making `CanonicalAIFunctionFactory` the single source of truth for tool handlers while preserving HITL/loopback exception propagation. | Proposed |
| **11.43C** | [⚡ Step 11.43C: `Pmad.AI` Opt-In SLM Delegating Chat Clients (`ToolCallHealing`, `ToolLoopCircuitBreaker`, `ThinkingTraceCompaction` & `ToolResultCompaction`)](STEP_11_43C_PMAD_AI_SLM_DELEGATING_CHAT_CLIENTS_OPT_IN.md) | Expose four opt-in (`default: false`) flags on `LocalOptimizationFlags` (`ToolCallHealing`, `ToolLoopCircuitBreaker`, `ThinkingTraceCompaction`, `ToolResultCompaction`) tailored for small local LLMs ($\le 8\text{B}$–$14\text{B}$), wiring `ToolCallHealingChatClient`, `ToolLoopCircuitBreakerChatClient`, `ThinkingTraceCompactionChatClient`, and `ToolResultCompactionChatClient` into the `IChatClient` pipeline (`MutateInPlace = false`) alongside Settings UI toggles and transcript frontmatter metadata. | Proposed |

---

## 3. Definition of Done (DoD) Checklist for Remaining Fixes

- [x] **Step 11.37A (HTTP Transport Hardening, `ConnectionRefused` Classification & 5-Minute Outage Retry Policy):**
  - [x] Introduce `LlmRetryPolicyOptions` with 5-minute outage defaults (`MaxRetries = 20`, `BaseDelay = 1s`, `MaxDelay = 30s`, `MaxRetryDuration = 300s`) and add `MaxRetryDuration` support to `RetryPolicyOptions`.
  - [x] Add `RetryNotificationContext` and notification callback support to `TransientHttpErrorRetryHandler` and wire live `AgentActivityKind.Notice` broadcasting in the LLM chat client factories.
  - [x] Enforce HTTP `Retry-After` header parsing (delta seconds and date) on 429 and 503 responses in `TransientHttpErrorRetryHandler`.
  - [x] Remove `.AddTransientHttpErrorRetry()` from fast UI liveness and catalog probes (`ILlmGatewayService`, `ILlmModelCatalogService`) in `InferenceServiceCollectionExtensions`.
  - [x] Update `TransientErrorDetector` to classify `SocketError.ConnectionRefused`, `TryAgain`, `HostNotFound`, `NoData`, `OperationAborted`, and `Interrupted` as transient.
  - [x] Disable built-in `System.ClientModel` retries (`maxRetries: 0`) in `OpenAiCompatibleChatClientFactory`.
  - [x] Harden `SocketsHttpHandler` in `OllamaHttpConstants` / chat client factories with `ConnectTimeout = 10s`, OS-level TCP Keep-Alive via `ConnectCallback`, and `PooledConnectionIdleTimeout = 1m`.
  - [x] Retain targeted 15-minute `HttpClient.Timeout` (`RequestTimeoutMinutes`) across Ollama and OpenAI-compatible factories.
  - [x] Add and update unit tests in `NightCodeForge.Core.Tests` and `NightCodeForge.Agents.Tests` using `FakeTimeProvider` or fast retry options; verify all tests pass via `dotnet test --filter "Category!=Acceptance&Category!=E2E"`.

- [ ] **Step 11.38B (Shared Base Class for `AcpAgentHarness` & `PiRpcAgentHarness`):**
  - [ ] Implement `ExternalProcessAgentHarnessBase` and refactor `AcpAgentHarness` and `PiRpcAgentHarness` to inherit from it.
  - [ ] Extract ACP `fs/*` and `terminal/*` host RPC methods into `IAcpHostRpcHandler` (`AcpHostRpcHandler`).
  - [ ] Reduce `AcpAgentHarness` constructor to `<= 8` parameters and remove it from `LegacyConstructorDebtAllowlist`.

- [ ] **Step 11.38C (Shared Agent Session & Toolset Factorization for `CrucibleAgentHarness`, `IdeationService` & `CanonicalTools`):**
  - [ ] Introduce `CanonicalToolScope` and `ICanonicalToolsetFactory`, reducing `CanonicalTools` constructor to `<= 8` parameters.
  - [ ] Extract `IAgentWorkspaceContextService`, `IHarnessAgentFactory`, and `ICrucibleConversationCoordinator`.
  - [ ] Refactor `CrucibleAgentHarness` (`23 -> <= 8` params) and `IdeationService` (`19 -> <= 8` params) to use the shared services and remove `CrucibleAgentHarness`, `IdeationService`, and `CanonicalTools` from `LegacyConstructorDebtAllowlist`.

- [ ] **Step 11.38D (`TaskOrchestrator` & `StartupRecoveryService` Decomposition and Zero-Exception `<= 10` Constructor Parameter Enforcement):**
  - [ ] Extract `ITaskWorkspacePreparer`, `ITaskMergeAndCascadeCoordinator`, and `ITaskLifecycleMutationCoordinator` from `TaskOrchestrator`, reducing its constructor from `19` to `<= 9` parameters.
  - [ ] Extract `IWorkspaceGitRecoveryService` from `StartupRecoveryService` (`11 -> <= 8` params) and `IContainerEnvironmentFactory` from `ExecutionEnvironmentProvider` (`10 -> <= 5` params).
  - [ ] Delete `LegacyConstructorDebtAllowlist` from `ConstructorParameterCeilingTests` so that zero classes in the solution exceed 10 constructor parameters.

- [ ] **Step 11.40 (Bash Timeout Escalation Ladder, In-Container Process Reaper, Stale Container Recycling & WSL2 Self-Healing):**
  - [ ] Add `ProcessKillWhitelist` (defaulting to `["csnav"]`) and `PidsLimit` (defaulting to `4096`) to `ContainerExecutorOptions`, and include `--init` and `--pids-limit` in `BuildDockerRunArgs`.
  - [ ] Implement Step 1 in-container process reaper (`SIGTERM` $\to$ 5s wait $\to$ `SIGKILL`) on `bash` timeout, preserving `PID 1`/`tail` and whitelisted processes and purging stale `index.lock` files.
  - [ ] Implement Step 2 container liveness probe (5s timeout), container force-recreation (`docker rm -f` + `EnsureContainerRunning`), and probe retry (5s timeout).
  - [ ] Implement Step 3 Windows-specific WSL2 shutdown escalation (`wsl --shutdown`), Docker daemon readiness check, and 30s probe retry.
  - [ ] Expand `TaskContainerReaper` and lifecycle hooks to tear down containers for `Failed`, `Ready`, and `WaitingForHuman` tasks and evict idle workspace containers.
  - [ ] Author unit tests in `NightCodeForge.Infrastructure.Tests` and `NightCodeForge.Core.Tests`.

- [x] **Step 11.41 (Task Review Specification Enforcement, Conversational Remediation & Unhandled Exception Shielding):**
  - [x] Remove naked `throw new WorkflowExecutionException` at `TaskOrchestrator.cs:1535`.
  - [x] Expose `int MaxRetries => WorkflowDefaults.DefaultMaxStageRetries;` on `IWorkflowStage` and track remediation attempts via `pipelineContext.IncrementRetry($"{stage.StageId}:missing-doc")`.
  - [x] Locate the latest stage conversation via `GetConversationsAsync`, inject a `StageTurnRole.User` remediation turn prompting the agent to edit `docs/tasks/*.md` or call `ask`, reopen the conversation as `StageExecutionStatus.Interrupted`, and populate `PipelineDataBagKeys.RecoveredSerializedMemory` (with fallback when no conversation record exists).
  - [x] Re-enter stage execution in-process (`isResumedRun = true`, `continue`), resuming the conversation with all preceding turns preserved and clearing `task.InterruptedStageId` upon consumption.
  - [x] When `remediationAttempt > effectiveMaxRetries`, mark the latest stage conversation as `StageExecutionStatus.Failed`, clear premature `ApprovedPlan` / `StageSummary` entries, and route deterministically through `HandleStageFatalFailureAsync`.
  - [x] Harden `CrucibleAgentHarness.BuildTaskReviewDirective()` and `StandardWorkflowDefinitions.cs` against conversational plain-text questions.
  - [x] Author unit tests in `NightCodeForge.Core.Tests` and update the 3 existing `WorkflowExecutionException` tests in `WorkflowSpecIntegrationTests.cs` (`NightCodeForge.Agents.Tests`).

- [ ] **Step 11.42 (Crucible Harness False-Completion Prevention, In-Session Conversational Continuation & Stage Artifact Gates):**
  - [ ] Implement `InterruptedGenerationDetector` in `NightCodeForge.Agents` to reliably identify truncated trailing markdown fences, backticks, bullet items, and unclosed tool tags.
  - [ ] Implement the in-harness truncated-stream continuation loop in `CrucibleAgentHarness` (governed by `MaxConsecutiveTruncationNudges = 3`, reset on tool calls), calling `FlushTurnToHistory()`, updating `historySnapshot`, and recreating the MAF `AgentSession` before each continuation pass.
  - [ ] Add `.scratch/**` and `docs/transcripts/**` exclusions to `GitDiffConstants.ExclusionPathspecs` and implement the harness-agnostic `Implementation` non-scratch diff gate in `TaskOrchestrator` (`implementation:empty-diff`), sharing the conversational remediation helper with Step 11.41's `TaskReview` gate.
  - [ ] Update `TestOrchestratorBuilder.CreateDefaultGitWorkspaceManager()` to return a non-empty default diff stat matching `FakeGitWorkspaceManager`.
  - [ ] Update rules and implementation directives in `PromptPrefixStabilizer` and `CrucibleAgentHarness.BuildImplementationDirective()` instructing models not to stop after intermediate planning monologues.
  - [ ] Author unit tests in `NightCodeForge.Agents.Tests` (for mid-thought stream continuation and circuit breaking) and `NightCodeForge.Core.Tests` (for orchestrator-level `Implementation` empty-diff conversational remediation and retry exhaustion).
  - [ ] Verify all tests pass via `dotnet test --filter "Category!=Acceptance&Category!=E2E"`.

- [ ] **Step 11.43A (`Pmad.AI` 0.0.2 Upgrade & `ReadOnlyCommandValidator` Guardrail Migration):**
  - [ ] Add `Pmad.AI` (`0.0.2`) package reference in `src/NightCodeForge.Agents/NightCodeForge.Agents.csproj`.
  - [ ] Replace the custom in-repo `ReadOnlyCommandValidator` implementation with `Pmad.AI.Guardrails.ReadOnlyCommandValidator` configured with NightCodeForge's `AllowedBinaries` and `csnav` `CommandValidationRule` (allowing read-only `csnav` subcommands and `server status|start|reload`, while blocking `skills` and `server stop`).
  - [ ] Wire `CanonicalTools.BashAsync` to validate read-only commands via the configured `Pmad.AI` validator and throw `InvalidOperationException` when disallowed.
  - [ ] Update and expand `ReadOnlyCommandValidatorTests` in `tests/NightCodeForge.Agents.Tests` to cover `Pmad.AI` 0.0.2 hardened rules (`git` flags/branch/tag/remote mutations, `find -exec`/`-delete`, `rg --pre`, `sed -i`, `csnav skills`/`server stop`, variable expansion, and process substitution).
  - [ ] Verify all non-E2E tests pass via `dotnet test --filter "Category!=Acceptance&Category!=E2E"`.

- [ ] **Step 11.43B (`Pmad.AI` `LenientToolDispatcher` & Delegating Chat Client Migration):**
  - [ ] Create NightCodeForge's declarative `ToolDispatcherConfiguration` builder covering all canonical tools, backlog mutation tools, parameter aliases, nested collection aliases, computed parameters, propagated parameters, and dropped-tool shell translations (`list_dir`, `grep_search`, `get_file_outline`, `find_files`).
  - [ ] Refactor `CanonicalAIFunctionFactory` to register its `AIFunction` instances directly with `Pmad.AI.Tools.LenientToolDispatcher` (eliminating duplicate tool handler lambdas) while preserving `OperationCanceledException`, `HumanInterruptionException`, and `RequestChangesException` propagation and `#42` / top-level array / null-alias handling.
  - [ ] Delete legacy in-repo `Dispatcher/LenientToolDispatcher.Backlog.cs`, `Dispatcher/ToolDefinition.cs`, and `Inference/LenientToolCallChatClient.cs`, and replace `Dispatcher/LenientToolDispatcher.cs` with `Pmad.AI.Tools.LenientToolDispatcher`.
  - [ ] Wire `LenientToolDispatchChatClient` into the `IChatClient` pipeline in `CrucibleAgentHarness` and `IdeationService`.
  - [ ] Update and expand `LenientToolDispatcherTests`, `BacklogToolWiringTests`, `IdeationServiceTests`, and `LenientToolDispatchChatClient` pipeline tests in `tests/NightCodeForge.Agents.Tests`.
  - [ ] Verify all non-E2E tests pass via `dotnet test --filter "Category!=Acceptance&Category!=E2E"`.

- [ ] **Step 11.43C (`Pmad.AI` Opt-In SLM Delegating Chat Clients — `ToolCallHealing`, `ToolLoopCircuitBreaker`, `ThinkingTraceCompaction` & `ToolResultCompaction`):**
  - [ ] Add `ToolCallHealing`, `ToolLoopCircuitBreaker`, `ThinkingTraceCompaction`, and `ToolResultCompaction` (`default: false`, opt-in) to `LocalOptimizationFlags` and `LocalOptimizationFlags.Clone()`.
  - [ ] Wire `ThinkingTraceCompactionChatClient`, `ToolResultCompactionChatClient`, `ToolCallHealingChatClient`, and `ToolLoopCircuitBreakerChatClient` into the `IChatClient` pipeline in `CrucibleAgentHarness` and `IdeationService` (preserving uncompacted `chatHistory` via `MutateInPlace = false`).
  - [ ] Update `SessionTranscriptService`, `TranscriptBrowserService`, `TranscriptMetadataCard.razor`, `GlobalOptimizationSettingsTab.razor`, and `AgentProfilesTab.razor` to serialize, parse, display, and toggle the four opt-in SLM flags.
  - [ ] Add unit tests in `NightCodeForge.Agents.Tests` and `NightCodeForge.Core.Tests` verifying default opt-out behavior, opt-in activation, non-destructive history preservation, and transcript frontmatter round-tripping.
  - [ ] Verify all non-E2E tests pass via `dotnet test --filter "Category!=Acceptance&Category!=E2E"`.

---

## 4. Completed Fixes Archive

All 27 previously completed Phase 11 onboarding, hardening, and bug-fix steps (`11.2` through `11.39B`) are archived in:
- **Summary & DoD Record:** [`docs/archive/11_EXTEND_FEATURES.md`](../archive/11_EXTEND_FEATURES.md)
- **Step Specifications:** [`docs/archive/phase-11/`](../archive/phase-11/)
