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
| **11.43C** | [⚡ Step 11.43C: `Pmad.AI` Opt-In SLM Delegating Chat Clients (`ToolCallHealing`, `ToolLoopCircuitBreaker`, `ThinkingTraceCompaction` & `ToolResultCompaction`)](STEP_11_43C_PMAD_AI_SLM_DELEGATING_CHAT_CLIENTS_OPT_IN.md) | Expose four opt-in (`default: false`) flags on `LocalOptimizationFlags` (`ToolCallHealing`, `ToolLoopCircuitBreaker`, `ThinkingTraceCompaction`, `ToolResultCompaction`) tailored for small local LLMs ($\le 8\text{B}$–$14\text{B}$), wiring `ToolCallHealingChatClient`, `ToolLoopCircuitBreakerChatClient`, `ThinkingTraceCompactionChatClient`, and `ToolResultCompactionChatClient` into the `IChatClient` pipeline (`MutateInPlace = false`) alongside Settings UI toggles and transcript frontmatter metadata. | Deferred (v0.2) |
| **11.49B** | [🧹 Step 11.49B: Retire `CancellationAwareStream` Workaround Upon `OllamaSharp` PR #400 Upgrade](STEP_11_49B_RETIRE_OLLAMASHARP_STREAM_CANCELLATION_WORKAROUND.md) | Once upstream [awaescher/OllamaSharp#400](https://github.com/awaescher/OllamaSharp/pull/400) (passing `CancellationToken` to `StreamReader.ReadLineAsync` on `.NET 7+`) is released on NuGet, upgrade `OllamaSharp`, delete `CancellationAwareStream` / `CancellationAwareHttpContent`, remove ambient `CancellationToken` propagation from `StreamingCancellationScope`, and verify `StreamingIdleWatchdogIntegrationTests` pass natively. | Proposed (Blocked on upstream release) |
| **11.55** | [🛡️ Step 11.55: Benchmark Verification Environment Parity, .NET SDK Resilience & Vitest Matching](STEP_11_55_BENCHMARK_VERIFICATION_ENVIRONMENT_AND_DOTNET_SDK_RESILIENCE.md) | Fix benchmark false negatives by configuring `core.longpaths true` on Windows checkouts, ensuring Python/Node/Rust verification dependency & `PATH` parity, handling `.NET` `global.json` SDK roll-forward without polluting patches, discovering subdirectory `.sln`/test projects, and normalizing Vitest ` > ` identifiers in `TestResultParsers`. | Proposed |
| **11.56** | [📊 Step 11.56: Benchmark Timeout Telemetry Preservation, Summary Aggregation & Language-Scoped Skills](STEP_11_56_BENCHMARK_TIMEOUT_TELEMETRY_AND_LANGUAGE_SCOPED_SKILLS.md) | Preserve accumulated token metrics on benchmark timeouts, exclude unstarted zero-token error runs from `AverageTokensPerSecond`, scope C#-only stack skills (`csharp-nav`, `codecondenser`) to C# repositories, and reconcile `DispatcherRepairs` telemetry with breakdown sub-counters. | Proposed |
| **11.57** | [🔧 Step 11.57: Crucible `ask` Tool Lenient Parsing, Error Diagnostics & Transcript Turn Timestamps](STEP_11_57_CRUCIBLE_ASK_TOOL_RESILIENCE_AND_TRANSCRIPT_TIMESTAMPS.md) | Upgrade `Pmad.AI` to `0.0.6` for `parameters`/`arguments` envelope unwrapping in `LenientToolDispatcher`, accept `{label, description}` objects in `QuestionItem.Options`, surface actionable tool binding diagnostics instead of `"Error: Function failed."`, and preserve real per-turn elapsed timestamps in Crucible transcripts. | Proposed |
| **11.58** | [🛡️ Step 11.58: Benchmark Multi-Stack Verification Parity, Solution Discovery & Verification Log Persistence](STEP_11_58_BENCHMARK_VERIFICATION_PARITY_AND_LOG_PERSISTENCE.md) | Deprioritize `docs` in subdirectory `.sln` discovery and prefer test-bearing solutions, replace `ExitCode`-only Mocha verification with bind-mount-safe structured test reporting, enforce Python runtime/dependency parity for legacy SWE-bench repos, and persist `VerificationLog` diagnostics to disk and `results.json`. | Completed |
| **11.59** | [🔧 Step 11.59: Benchmark ProjectConfig Propagation, Clean global.json Scoping & Code Review Verdict Alignment](STEP_11_59_BENCHMARK_PROJECT_CONFIG_GLOBAL_JSON_AND_CODE_REVIEW_ALIGNMENT.md) | Propagate language-accurate `WorkspaceProjectConfig` commands into benchmark sessions (avoiding `dotnet` fallbacks and missing `docs/tasks/` guidance), hide `DotNetGlobalJsonResilienceScope` edits from `git status`/`git diff`, and align `CodeReview` Tier 1/Tier 2 verdict and whitespace classification rules. | Completed |

---

## 3. Definition of Done (DoD) Checklist for Remaining Fixes

- [ ] **Step 11.43C (`Pmad.AI` Opt-In SLM Delegating Chat Clients — `ToolCallHealing`, `ToolLoopCircuitBreaker`, `ThinkingTraceCompaction` & `ToolResultCompaction`):**
  - [ ] Add `ToolCallHealing`, `ToolLoopCircuitBreaker`, `ThinkingTraceCompaction`, and `ToolResultCompaction` (`default: false`, opt-in) to `LocalOptimizationFlags` and `LocalOptimizationFlags.Clone()`.
  - [ ] Wire `ThinkingTraceCompactionChatClient`, `ToolResultCompactionChatClient`, `ToolCallHealingChatClient`, and `ToolLoopCircuitBreakerChatClient` into the `IChatClient` pipeline in `CrucibleAgentHarness` and `IdeationService` (preserving uncompacted `chatHistory` via `MutateInPlace = false`).
  - [ ] Update `SessionTranscriptService`, `TranscriptBrowserService`, `TranscriptMetadataCard.razor`, `GlobalOptimizationSettingsTab.razor`, and `AgentProfilesTab.razor` to serialize, parse, display, and toggle the four opt-in SLM flags.
  - [ ] Add unit tests in `NightCodeForge.Agents.Tests` and `NightCodeForge.Core.Tests` verifying default opt-out behavior, opt-in activation, non-destructive history preservation, and transcript frontmatter round-tripping.
  - [ ] Verify all non-E2E tests pass via `dotnet test --filter "Category!=Acceptance&Category!=E2E"`.

- [ ] **Step 11.49B (Retire `CancellationAwareStream` Workaround Upon `OllamaSharp` PR #400 Upgrade):**
  - [ ] Upgrade `OllamaSharp` to the NuGet release containing [awaescher/OllamaSharp#400](https://github.com/awaescher/OllamaSharp/pull/400).
  - [ ] Delete `src/NightCodeForge.Core/Resilience/CancellationAwareStream.cs` and remove `CancellationAwareHttpContent` wrapping from `TransientHttpErrorRetryHandler.SendAsync`.
  - [ ] Remove ambient `CancellationToken` propagation from `StreamingCancellationScope`, keeping only attempt/header lifecycle tracking for `PreHeaderPrefill` vs. `FirstTokenPrefill` diagnostics.
  - [ ] Remove `docs/fixes/OLLAMASHARP_STREAMING_CANCELLATION_PR_SPEC.md`.
  - [ ] Verify all `StreamingIdleWatchdogIntegrationTests` and non-E2E tests pass via `dotnet test --filter "Category!=Acceptance&Category!=E2E"`.

- [ ] **Step 11.55 (Benchmark Verification Environment Parity, .NET SDK Resilience & Vitest Matching):**
  - [ ] Configure `core.longpaths true` in `BenchmarkWorkspaceManager.ProvisionAsync` before `git checkout --detach FETCH_HEAD`.
  - [ ] Support deterministic per-stack dependency provisioning / container parity for Python, Node (JS/TS), and Rust (with `$HOME/.cargo/bin` on default `PATH`) without contaminating `model_patch`.
  - [ ] Add `.NET` `global.json` SDK roll-forward resilience for C# execution and verification without polluting `git diff` patches.
  - [ ] Support subdirectory `.sln`/test `.csproj` discovery in C# verification command resolution and repository-specific test config flags.
  - [ ] Normalize ` > ` in `TestAssertionEvaluator.NormalizeTestIdentifier` for Vitest JUnit XML matching.
  - [ ] Verify all non-E2E tests pass via `dotnet test --filter "Category!=Acceptance&Category!=E2E"`.

- [ ] **Step 11.56 (Benchmark Timeout Telemetry Preservation, Summary Aggregation & Language-Scoped Skills):**
  - [ ] Propagate real accumulated `PromptTokens`, `CompletionTokens`, `TotalTokens`, and `TokensPerSecond` on benchmark timeout / abnormal termination into `BenchmarkInstanceResult`.
  - [ ] Update `BenchmarkSummaryAggregator` so unstarted / zero-token error runs do not depress `AverageTokensPerSecond`.
  - [ ] Scope C# stack skills (`csharp-nav`, `codecondenser`) to C# repositories in benchmark policy/skill resolution.
  - [ ] Reconcile `DispatcherRepairs` (`TotalHits`) with explicit breakdown counters in `LenientDispatchTelemetry` / `ToolCallTelemetryBreakdown`.
  - [ ] Verify all non-E2E tests pass via `dotnet test --filter "Category!=Acceptance&Category!=E2E"`.

- [ ] **Step 11.57 (Crucible `ask` Tool Lenient Parsing, Error Diagnostics & Transcript Turn Timestamps):**
  - [ ] Upgrade `Pmad.AI` to `0.0.6` for built-in top-level `parameters` / `arguments` envelope unwrapping in `LenientToolDispatcher` (and project any new telemetry counter), with NightCodeForge regression tests for `ask`.
  - [ ] Support both `string` and `{label, description}` object entries for `QuestionItem.Options` in `ask` tool binding/parsing.
  - [ ] Surface actionable schema/parameter binding error diagnostics to the model instead of `"Error: Function failed."`.
  - [ ] Preserve and render real per-turn elapsed timestamps in Crucible transcripts (`CrucibleConversationCoordinator`, `InMemoryCrucibleConversationCoordinator`, `SessionTranscriptService`).
  - [ ] Verify all non-E2E tests pass via `dotnet test --filter "Category!=Acceptance&Category!=E2E"`.

- [x] **Step 11.58 (Benchmark Multi-Stack Verification Parity, Solution Discovery & Verification Log Persistence):**
  - [x] Update `.NET` subdirectory solution discovery to deprioritize `docs`/`doc`/`resources`/`scripts`/`tools` and prefer solutions containing test projects.
  - [x] Replace `ExitCode`-only Mocha verification on `axios__axios-5892` (and equivalent suites) with structured JUnit XML reporting or bind-mount-safe test scoping.
  - [x] Ensure Python benchmark verification uses compatible runtimes and pinned repository dependencies so legacy SWE-bench repos do not fail collection/import.
  - [x] Persist verification command stdout/stderr (`VerificationLog`) to disk under the benchmark output directory and reference the log path on `BenchmarkInstanceResult`.
  - [x] Verify all non-E2E tests pass via `dotnet test --filter "Category!=Acceptance&Category!=E2E"`.

- [x] **Step 11.59 (Benchmark ProjectConfig Propagation, Clean global.json Scoping & Code Review Verdict Alignment):**
  - [x] Supply language-accurate `WorkspaceProjectConfig` (build/test commands per stack) to benchmark sessions and avoid misleading non-TaskReview runs toward missing `docs/tasks/` paths.
  - [x] Hide `DotNetGlobalJsonResilienceScope` `global.json` relaxations from `git status` / `git diff` during agent execution while restoring exact original bytes and index state on disposal.
  - [x] Align `v1-code-review.jsonl` `RequestChanges` / `Approve` expectations and `CodeReviewAndFix` verdict/whitespace handling with Tier 1 vs. Tier 2 policy.
  - [x] Verify all non-E2E tests pass via `dotnet test --filter "Category!=Acceptance&Category!=E2E"`.

---

## 4. Completed Fixes Archive

All 48 completed Phase 11 onboarding, hardening, and bug-fix steps (`11.2` through `11.54`) are archived in:
- **Summary & DoD Record:** [`docs/archive/11_EXTEND_FEATURES.md`](../archive/11_EXTEND_FEATURES.md)
- **Step Specifications:** [`docs/archive/phase-11/`](../archive/phase-11/)
