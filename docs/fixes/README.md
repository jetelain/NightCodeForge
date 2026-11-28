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
| **11.67** | [🛡️ Step 11.67: Benchmark Anti-Cheat CDN/Audit Telemetry, Python Child Image & TaskReview Parity](STEP_11_67_BENCHMARK_ANTICHEAT_PYTHON_IMAGE_AND_TASKREVIEW_PARITY.md) | Extend `BlockedAntiCheatHosts` with package-browsing CDNs/source mirrors without blocking primary registries, add post-run bash anti-cheat audit telemetry across `results.json`/`summary.md`, synchronize `TaskReview` `<task>` `id`/`title` with on-disk frontmatter, align `TaskReview` network parity (`disableNetwork: false`) with strict out-of-scope modification zero-scoring, introduce an on-demand `ncf-bench-python-3.11:v1` child image (`ripgrep`, `git`, `jq`, `procps`, etc.), and make `/tmp/ncf-bench-venv` unconditional for all Python verification runs (`feat/bench`-only scope). | Completed |
| **11.68** | [🔒 Step 11.68: Clean Offline Benchmark Pre-Warm, Isolated Caches & Host Tool/Mount Hardening](STEP_11_68_OFFLINE_BENCHMARK_PREWARM_AND_CACHE_ISOLATION.md) | Phase A (`main` prerequisite): mount host `~/.dotnet` `:ro` with `DOTNET_CLI_HOME=/tmp/ncf-dotnet-home`, allowlist companion tools (`csnav`/`codecondenser`/`mdfetch`) to stop `ilspycmd` exposure, and add per-scope isolated package cache root overrides. Phase B (`feat/bench`): run deterministic online dependency pre-warm before launching offline (`--network none`) benchmark agents across all categories, backed by isolated `:ro` benchmark caches and target-repo package scrubbing. | Proposed |

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

- [x] **Step 11.67 (Benchmark Anti-Cheat CDN/Audit Telemetry, Python Child Image & TaskReview Parity — `feat/bench` only):**
  - [x] Add package-browsing CDNs and source mirrors (`unpkg.com`, `cdn.jsdelivr.net`, `esm.sh`, `skypack.dev`, `cdnjs.cloudflare.com`, `deps.dev`, `fuget.org`, `nuget.info`, `sourcegraph.com`, `grep.app`, `searchcode.com`) to `BenchmarkDefaults.BlockedAntiCheatHosts` while keeping primary package registries unblocked, and add post-run bash anti-cheat telemetry (`results.json`, `BenchmarkCandidateSummary`, `BenchmarkReportFormatter`), with unit tests.
  - [x] Synchronize `TaskReview` `ephemeralTask.Id` and full non-truncated `ephemeralTask.Title` from on-disk task markdown frontmatter in `CrucibleBenchmarkRunner`, with unit tests.
  - [x] Align `TaskReview` network access (`disableNetwork: false` with `BlockedAntiCheatHosts`) in `CrucibleBenchmarkRunner.BuildExecutionScope` and preserve/verify strict `0.0` `DeterministicScore` enforcement for `ModifiedFilesOutsideDocsTasks` in `BenchmarkVerifier.VerifyTaskReviewAsync`, with unit tests.
  - [x] Build on-demand `ncf-bench-python-3.11:v1` child image (`ripgrep`, `git`, `jq`, `procps`, `curl`, `ca-certificates`, `unzip` on `python:3.11-bookworm`) in `NightCodeForge.Benchmark.Cli`, make `/tmp/ncf-bench-venv` unconditional for unconstrained Python verification in `BenchmarkTestCommandResolver.WrapPythonContainerCommand`, update Python dataset/manifest entries (`v1-smoke.manifest.json`, `v1-full.manifest.json`, `v1-code-review.jsonl`, `v1-task-review.jsonl`) and re-materialize local datasets, with unit tests and live Python `ncf-bench dataset validate --verify` passing.
  - [x] Verify zero changes outside `NightCodeForge.Benchmark.Cli`, `NightCodeForge.Benchmark.Cli.Tests`, `benchmarks/datasets/`, and `docs/`, and run `dotnet test`.

- [ ] **Step 11.68 (Clean Offline Benchmark Pre-Warm, Isolated Caches & Host Tool/Mount Hardening — `main` then `feat/bench`):**
  - [ ] Phase A (`main`, merged into `feat/bench`): mount host user `.dotnet` root `:ro` with container-local `DOTNET_CLI_HOME=/tmp/ncf-dotnet-home`, allowlist host `.dotnet/tools` (`csnav`, `codecondenser`, `mdfetch`) to stop `ilspycmd` exposure, and support per-scope isolated package cache root overrides and `:ro` cache mounts with symmetric drift detection and unit tests.
  - [ ] Phase B (`feat/bench` — `NightCodeForge.Benchmark.Cli`): implement deterministic online dependency pre-warm across Python (`/workspace/.venv`), Node (`node_modules`), C# (`dotnet restore`), and Rust (`cargo fetch`) failing fast as `Error`, run agent containers offline (`--network none`) across all categories with offline env guards, wire isolated `~/.nightcodeforge/benchmarks/cache/*` directories (`:ro` during agent execution) and target-repo cache scrubbing, and verify with unit, `Category=Acceptance`, and full `dotnet test` suites.

---

## 4. Completed Fixes Archive

All 61 completed Phase 11 onboarding, hardening, and bug-fix steps (`11.2` through `11.66`) are archived in:
- **Summary & DoD Record:** [`docs/archive/11_EXTEND_FEATURES.md`](../archive/11_EXTEND_FEATURES.md)
- **Step Specifications:** [`docs/archive/phase-11/`](../archive/phase-11/)
