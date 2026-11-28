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
| **11.60** | [🐳 Step 11.60: Base Image Timezone Data (`tzdata`/`tzdata-legacy`), Host-Mounted Rust Cache & Python CodeReview Image Parity](STEP_11_60_BASE_IMAGE_TZDATA_AND_HOST_MOUNTED_RUST.md) | Bump the managed base image to `ncf-agent-base:v6` with `tzdata`, `tzdata-legacy`, and `/cache/cargo`, mount a pre-created writable `CARGO_HOME=/cache/cargo` cache alongside read-only host Rust SDKs with mountable-toolchain pre-flight checks, and wire/harden legacy Python `CodeReview` custom images. | Completed |
| **11.61** | [🛡️ Step 11.61: Benchmark Solution Discovery, Dataset Sync, Node Provisioning & Review Grading Accuracy](STEP_11_61_BENCHMARK_SOLUTION_DISCOVERY_NODE_PROVISIONING_AND_REVIEW_GRADING.md) | Fix `.NET` subdirectory solution ranking (`Spectre.Console.sln` vs `Spectre.Console.Analyzer.sln`), apply manifest + crafted-dataset overlays (`--manifest`) to prevent stale local JSONL runs, add `--ignore-scripts` and partial `node_modules` recovery for Node postinstalls, propagate `request_changes` `reason` and relax `EvaluateExpectedFindings` / `Approve` test ratios, and add a `TaskReview` difficulty rubric with calibrated grading. | Proposed |
| **11.62** | [🛡️ Step 11.62: Custom Image Library ABI Isolation, Distro Rustup Discovery, Mocha Reporter Isolation, Review Corpus Preservation & Agent Command Hygiene](STEP_11_62_BENCHMARK_CUSTOM_IMAGE_ABI_RUSTUP_MOCHA_AND_REVIEW_GRADING.md) | Exclude host OS multiarch shared-library mounts (and detect mount drift) on non-managed custom images (`OPENSSL_3.3.0` fix on Python), discover and `PATH`-wire distro-packaged `rustup` toolchains (`$RUSTUP_HOME/toolchains/.../bin`), prevent isolated Node reporter peer-dep shadowing (`mocha@12` ESM) with workspace `NODE_PATH` and stale-prefix cleanup, preserve visible pre-`ask` assistant analysis (and `Question.OptionsJson`) on `WaitingForHuman` alongside identifier-bounded file-stem corroboration, and strip verifier-only reporter flags / enforce lockfile-aware monorepo package builds in agent `<execution_guidelines>`. | Completed |
| **11.63** | [🛡️ Step 11.63: Modern-Era Dataset Curation, Python Dependency Pinning, Verification Workspace Hygiene, MaxTurns Diagnostics & TaskReview Anchor Grounding](STEP_11_63_BENCHMARK_DATASET_CURATION_DEPENDENCY_PINNING_AND_HYGIENE.md) | Add manifest-level `pip_constraints` + `REQUESTS_CA_BUNDLE` export, purge gitignored `target/`/`bin`/`obj` in `ResetToBaseAsync` (`-e node_modules -e .venv`), support separator-normalized `TaskReview` file-stem corroboration (`delay_queue` ↔ `DelayQueue`) and `ask` path-citation guidance, surface explicit `MaxTurns` exhaustion diagnostics, harden Python/Node container wrappers & cyclic reparse-point cleanup, and curate + live-validate modern-era `v1-smoke`, `v1-full`, and `v1-code-review.jsonl` datasets. | Completed |
| **11.64** | [🛡️ Step 11.64: Container Shell Command Framing, Sandbox Runtime Parity, MSBuild Crash Resilience & Agent Hygiene](STEP_11_64_CONTAINER_SHELL_FRAMING_ENV_PARITY_MSBUILD_RESILIENCE_AND_AGENT_HYGIENE.md) | Isolate `PersistentContainerShellSession` command framing from unbalanced backticks/quotes (`csnav` ``ReadOnlyDictionary`2`` hang) while keeping child `stdin` at `/dev/null`, export `DOTNET_ROLL_FORWARD=LatestMajor` and writable `/tmp/ncf-pnpm-store` & `/tmp/ncf-npm-cache` fallbacks in agent containers, retry/classify `MSB4166` crashes as `Error` (preserving `VerificationLog`), remove `--no-incremental` from agent `<execution_guidelines>` (keeping it on deterministic stages) + align `--no-build` and `dist/` hygiene, split `StandardWorkflowDefinitions` `CodeReview` directives into database-driven tool-active vs. XML-fallback branches via `StageDirectiveRenderer`, and rank `RoslynSymbolIndexProvider` production symbols first. | Completed |
| **11.65** | [📊 Step 11.65: Benchmark Dataset Parity, Bug-Triggering MinorFix Validation, Scoring Fairness & Telemetry Accuracy](STEP_11_65_BENCHMARK_DATASET_PARITY_SCORING_FAIRNESS_AND_TELEMETRY_ACCURACY.md) | Rebase `ncf-tr-py-001` onto a Python 3.11-compatible commit and pin `python:3.11-bookworm` on Python `TaskReview` instances, add disjoint bug-triggering `test_patch` coverage to `MinorFix` reviews and verify pre-fix failure in `BenchmarkDatasetValidator`, gate `Implementation` `DeterministicScore` on `fail_to_pass` and symmetrize `TaskReview` verdict scoring, calibrate `TaskReview` `expected_difficulty` rubric/labels, add C# `pass_to_pass` regression tests, track pure-LLM `InferenceDurationMs` in `ThinkingAndActivityTracingChatClient` for `TokensPerSecond`, and distinguish container/WSL vs host tool versions in provenance. | Completed |
| **11.66** | [🛡️ Step 11.66: Tool Binding Diagnostics, Question Parser, Symmetric Container Drift & Lifecycle Hardening](STEP_11_66_TOOL_BINDING_PARSER_DRIFT_AND_CONTAINER_HARDENING.md) | Fix `DiagnosticBindingAIFunction` union recursion/`[AIParameterName]`/negative-prefix sniffing, harden `QuestionItemParser` root `"question"` prose/null handling and `LenientAskAIFunction` `IEnumerable<QuestionItem>` rewriting, make `ContainerMountAndDriftBuilder` drift detection symmetric (`NetworkMode`, `ReadonlyRootfs`, `BlockedHosts`, `MountHostDotnetTools`, `ProtectCloneWorkingTree`), harden `EnsureHostPackageCacheDirectoriesOnceAsync` WSL symlink guards & permission scoping, align `DOTNET_ROLL_FORWARD` & tilde expansion parity, flush turns on `CompleteWithRequestChangesAsync`, drain `InMemoryCrucibleConversationCoordinator.BeginAsync`, align `ExecutionEnvironmentProvider` lock/eviction scopes, and balance `RoslynSymbolIndexProvider` ranking with named constants. | Completed |

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
  - [x] Supply a language-accurate `WorkspaceProjectConfig` (build/test commands per stack) to benchmark sessions and avoid misleading non-TaskReview runs toward missing `docs/tasks/` paths.
  - [x] Hide `DotNetGlobalJsonResilienceScope` `global.json` relaxations from `git status` / `git diff` during agent execution while restoring exact original bytes and index state on disposal.
  - [x] Align `v1-code-review.jsonl` `RequestChanges` / `Approve` expectations and `CodeReviewAndFix` verdict/whitespace handling with Tier 1 vs. Tier 2 policy.
  - [x] Verify all non-E2E tests pass via `dotnet test --filter "Category!=Acceptance&Category!=E2E"`.

- [x] **Step 11.60 (Base Image Timezone Data (`tzdata`/`tzdata-legacy`), Host-Mounted Rust Cache & Python CodeReview Image Parity):**
  - [x] Bump default base image to `ncf-agent-base:v6` / `nightcodeforge-agent:v6`, install `tzdata` and `tzdata-legacy`, pre-create `/cache/cargo` with `ENV CARGO_HOME=/cache/cargo`, and add `v5` legacy alias resolution, with unit tests.
  - [x] Wire persistent writable `/cache/cargo` (`CARGO_HOME=/cache/cargo`, pre-created non-root host cache, drift detection, preamble override respect) alongside read-only host `~/.cargo` / `~/.rustup` SDK mounts, and add a fast pre-flight diagnostic for `Implementation`/`CodeReview` Rust instances in `ContainerSandbox` lacking a mountable host `cargo`/`rustc` toolchain, with unit tests.
  - [x] Add compatible `custom_container_image` values (`python:3.9` for `ncf-cr-py-001`, `python:3.11` for `ncf-cr-py-002`) across `v1-code-review.jsonl` and matching manifests, and harden non-managed custom images against unavailable `en_US.UTF-8` locale exports and missing `/ncf-home`/`/cache/*` directories, with unit and dataset parity tests.
  - [x] Verify all non-E2E tests pass via `dotnet test --filter "Category!=Acceptance&Category!=E2E"`.

- [ ] **Step 11.61 (Benchmark Solution Discovery, Dataset Sync, Node Provisioning & Review Grading Accuracy):**
  - [ ] Update `BenchmarkTestCommandResolver.DiscoverDotNetTargets` to deprioritize low-priority and auxiliary `.Analyzer`/`.Sandbox`/`.Benchmarks`/`.Samples` solutions, rank by referenced test-project count then total project count, and preserve test `.csproj` fallback, with unit tests.
  - [ ] Add `--manifest` support (`run` and `dataset validate`, wired in `benchmarks/run-benchmark.ps1`), manifest metadata overlay, and sibling crafted-dataset synchronization so updated manifest/review fields apply without manual re-imports, with unit tests.
  - [ ] Update `WrapNodeContainerCommand` with direct `--ignore-scripts` fallback when native postinstall downloads are blocked by `BlockedAntiCheatHosts`, incomplete `node_modules` detection/repair, and Node sandbox setup guidance in benchmark prompt/config framing, with unit tests.
  - [ ] Update `BenchmarkVerifier.EvaluateExpectedFindings` so findings with `symbol_or_line_hint` and substantial keyword matches are not zeroed out for omitting the literal filename (preserving strict grounding without symbol hints), propagate `request_changes` `reason` through `CrucibleAgentHarness`/`AgentRunResult`/`BenchmarkAgentOutcome`/evaluated corpus, and set `FailToPassTotal`/`PassToPassTotal` to `0` when tests are skipped on `Approve`/`RequestChanges`, with unit tests.
  - [ ] Add explicit `standard`/`difficult`/`expert` rubric criteria to `StandardWorkflowDefinitions`'s `TaskReview` stage directive, audit `v1-task-review.jsonl`, and calibrate `VerifyTaskReviewAsync` difficulty scoring (passing adjacent `difficult`/`expert` tiers with partial credit while failing missed or false escalations), with unit tests.
  - [ ] Verify all non-E2E tests pass via `dotnet test --filter "Category!=Acceptance&Category!=E2E"`.

- [x] **Step 11.62 (Custom Image Library ABI Isolation, Distro Rustup Discovery, Mocha Reporter Isolation, Review Corpus Preservation & Agent Command Hygiene):**
  - [x] Exclude host OS multiarch shared-library directories (`/usr/lib/x86_64-linux-gnu`, `/usr/lib/aarch64-linux-gnu`, and equivalent `*-linux-gnu*` paths) from bind-mounting into non-managed custom container images (`!IsManagedBaseImage`) while preserving managed-image mounts, and flag stale host shared-library mounts as drift in `EvaluateMountDrift`, with unit tests.
  - [x] Extend `HostSdkDiscoveryService`, `ContainerMountAndDriftBuilder`, `ContainerEnvironmentFactory`, `PersistentContainerShellSession`, and `BenchmarkToolchainPreflightValidator` to discover, mount, add to `PATH` (without nested bind-mount conflicts or `/usr/bin` mounts), and validate distro-packaged `rustup` toolchains (`$RUSTUP_HOME/toolchains/<active>/bin`) alongside `sh.rustup.rs` (`~/.cargo/bin`) installs while rejecting both non-rustup `/usr/bin/{cargo,rustc}` and toolchain-less `rustup`, with unit tests.
  - [x] Update isolated Node reporter provisioning in `BenchmarkTestCommandResolver` so `mocha-junit-reporter` and `jest-junit` install without conflicting peer dependencies (`mocha@12` ESM shadowing), include workspace `node_modules` on `NODE_PATH`, and guard/prune stale shadowed runner prefixes, with unit tests.
  - [x] Preserve visible pre-`ask` assistant analysis text across `CrucibleAgentHarness` -> `AgentRunResult` -> `BenchmarkAgentOutcome` -> `VerifyTaskReviewAsync` / `BenchmarkLlmJudge` (including parsed `Question.OptionsJson`), and refine `EvaluateExpectedFindings` to allow strong independent keyword + identifier-bounded file-stem/type corroboration alongside symbol corroboration while rejecting ungrounded/short-stem matches, with regression tests covering `ncf-cr-csharp-008` and `ncf-tr-rust-002`.
  - [x] Strip verifier-only reporter/logger flags, env vars, and dangling `--` separators in `ResolveAgentTestCommand` (keeping `ResolveTestCommand` intact), and enforce lockfile-aware monorepo package build script selection (`build:packages`) in `BenchmarkStagePolicyResolver`, with unit tests.
  - [x] Add non-CI `[Trait("Category", "Acceptance")]` live-container integration tests verifying `python:3.9`/`python:3.11` `_ssl` + `pip` & drift, live host `rustup` `cargo`/`rustc` execution in `ncf-agent-base:v6`, and live Node 18 `mocha-junit-reporter` (with stale-prefix recovery) and `jest-junit` JUnit XML generation & parsing.
  - [x] Verify CI-scoped tests (`dotnet test --filter "Category!=Acceptance&Category!=E2E"`), local Step 11.62 `Category=Acceptance` tests (`dotnet test --filter "Category=Acceptance"`), and full-suite completion checks pass.

- [x] **Step 11.63 (Modern-Era Dataset Curation, Python Dependency Pinning, Verification Workspace Hygiene, MaxTurns Diagnostics & TaskReview Anchor Grounding):**
  - [x] Add `pip_constraints` schema/manifest/overlay/importer/writer support, wire `REQUESTS_CA_BUNDLE` + `/tmp/ncf-pip-constraints.txt` / `PIP_CONSTRAINT` into `BenchmarkTestCommandResolver` and `BenchmarkVerifier`, and surface active `pip_constraints` in `BenchmarkStagePolicyResolver.GetSandboxEnvironmentGuidance`.
  - [x] Update `BenchmarkWorkspaceManager.ResetToBaseAsync` to run `git clean -fdx -e node_modules -e .venv` (and harden `DeleteDirectoryRobustly` against cyclic junctions), preventing agent-side `target/` artifact leaks into verification.
  - [x] Update `BenchmarkVerifier.MatchesIdentifierBoundedFileStem` and `EvaluateExpectedFindings` for separator-normalized stem corroboration (`delay_queue` ↔ `DelayQueue`) on `TaskReview` findings, and add `ask` file-path citation guidance in `StandardWorkflowDefinitions`.
  - [x] Surface distinct `MaxTurns` exhaustion diagnostics in `CrucibleBenchmarkRunner` when `turns >= candidate.MaxTurns`.
  - [x] Curate `v1-smoke.manifest.json`, `v1-full.manifest.json`, and `v1-code-review.jsonl` to replace/adapt broken historical instances, harden Python/Node container wrappers & truncated-bracket pytest matching, re-materialize local datasets, and pass live containerized `ncf-bench dataset validate`.
  - [x] Verify all non-E2E/non-Acceptance, Acceptance, and full-suite tests pass.

- [x] **Step 11.64 (Container Shell Command Framing, Sandbox Runtime Parity, MSBuild Crash Resilience & Agent Hygiene):**
  - [x] Isolate `PersistentContainerShellSession.ExecuteFramedCommandAsync` against unbalanced backticks/quotes/heredocs (`csnav` ``ReadOnlyDictionary`2`` hang) while preserving child `stdin` at `/dev/null`, add unit tests and live `ncf-agent-base:v6` unbalanced-backtick/`csnav decompile` acceptance tests.
  - [x] Export `DOTNET_ROLL_FORWARD=LatestMajor` and add writable-directory probing with `/tmp/ncf-pnpm-store` and `/tmp/ncf-npm-cache` fallbacks for `npm_config_store_dir` / `npm_config_cache` in `PersistentContainerShellSession.WriteEnvironmentPreambleAsync`.
  - [x] Add `MSB4166` / `MSB5021` single-node (`-m:1`) retry and infrastructure `Error` classification (preserving `VerificationLog`) in `BenchmarkVerifier` / `CrucibleBenchmarkRunner`.
  - [x] Remove `--no-incremental` from agent `<execution_guidelines>` (preserving `DeterministicStageConstants.DefaultBuildCommand` on deterministic stages), append `--no-build` to custom agent C# `dotnet test` commands, omit unnecessary Node `npm run build` commands when no package build script exists, and exclude `dist/` artifacts from patch extraction.
  - [x] Model dual tool-active vs. tool-less `<request_changes>` branches directly in `StandardWorkflowDefinitions` via `StageDirectiveRenderer` across `TaskOrchestrator`, `DefaultBootstrapPromptBuilder`, `CrucibleAgentHarness`, `ExternalProcessAgentHarnessBase`, and `BenchmarkStagePolicyResolver`, preserving `explicitTargetRouting` and strict pseudo-XML tag routing in `StageSummaryExtractor.Extract`.
  - [x] Update `RoslynSymbolIndexProvider` to filter stop-words, match dot-separated identifier segments, and rank production source symbols ahead of `bench/`/`samples/`/`tests/`.
  - [x] Verify non-E2E/non-Acceptance, Acceptance, and full-suite tests pass.

- [x] **Step 11.65 (Benchmark Dataset Parity, Bug-Triggering MinorFix Validation, Scoring Fairness & Telemetry Accuracy):**
  - [x] Rebase `ncf-tr-py-001` onto a Python 3.11-compatible `psf/requests` commit and pin `python:3.11-bookworm` on Python `TaskReview` entries across `v1-task-review.jsonl`, `v1-smoke.manifest.json`, and `v1-full.manifest.json`.
  - [x] Add disjoint bug-triggering `test_patch` + partitioned `fail_to_pass` / `pass_to_pass` assertions to `MinorFix` entries in `v1-code-review.jsonl` and enforce pre-fix failure on `CandidatePatch + TestPatch` alongside post-fix pass on `GoldPatch + TestPatch` in `BenchmarkDatasetValidator`.
  - [x] Fix `Implementation` `DeterministicScore` (`f2pRate * p2pRate`) and make `TaskReview` verdict scoring symmetric.
  - [x] Reclassify `ncf-tr-csharp-003` to `standard` and refine `StandardWorkflowDefinitions` `TaskReview` rubric wording.
  - [x] Populate `pass_to_pass` regression checks on C# `v1-smoke` and `v1-full` `Implementation` entries where applicable, track pure-LLM `InferenceDurationMs` in `ThinkingAndActivityTracingChatClient` -> `AgentRunMetrics` for `TokensPerSecond`, clarify host vs container/WSL `.NET` SDK versions in `BenchmarkProvenanceProvider`, re-materialize datasets, and pass live `ncf-bench dataset validate`.
  - [x] Verify non-E2E/non-Acceptance, Acceptance, and full-suite tests pass.

- [x] **Step 11.66 (Tool Binding Diagnostics, Question Parser, Symmetric Container Drift & Lifecycle Hardening):**
  - [x] Fix union schema depth recursion, `[AIParameterNameAttribute]` lookup, and `-?[0-9]` JSON sniffing in `DiagnosticBindingAIFunction`.
  - [x] Preserve sibling `options`/`rationale` on root `"question"` prose/nested JSON and handle `"question": null` in `QuestionItemParser`, and prefer exact `"questions"` / rewrite non-`IReadOnlyList<QuestionItem>` enumerables in `WorkflowInteractionToolModule.LenientAskAIFunction`.
  - [x] Make container drift detection symmetric in `ContainerMountAndDriftBuilder` (online network/env recovery, `ReadonlyRootfs`, cleared/reduced `BlockedHosts`, re-enabled `MountHostDotnetTools`, stale `ProtectCloneWorkingTree`, and consistent null `runInspect` guards).
  - [x] Add per-target caching, WSL symlink checks, scoped `0777` permission widening, and per-path error isolation (with transient `wsl.exe` retry) in `ContainerEnvironmentFactory.EnsureHostPackageCacheDirectoriesOnceAsync`.
  - [x] Export `DOTNET_ROLL_FORWARD=LatestMajor` in `ContainerMountAndDriftBuilder.BuildDockerRunArgs`, expand tilde paths consistently in `ResolveRustupHomePath`/`BuildVolumeMounts`/`ComputePathEnvironmentVariable`, and replace remaining `"none"`/`"RUSTUP_HOME"`/dotnet suffix literals with constants.
  - [x] Harden `BenchmarkTestCommandResolver.WrapPythonContainerCommand` so `pytest --version` probes executability and `pip_constraints` runs unconditionally enforce the isolated venv.
  - [x] Call `FlushTurnToHistory()` in `CrucibleAgentHarness.CompleteWithRequestChangesAsync`, await `state.DrainAsync()` in `InMemoryCrucibleConversationCoordinator.BeginAsync`, and align lock vs eviction scope in `ExecutionEnvironmentProvider`.
  - [x] Balance `RoslynSymbolIndexProvider` composite ranking, fix source-rooted `Sandbox/` classification, replace magic scoring/category values with named constants/enum, and short-circuit `AgentsMdService.StripNoIncrementalFlag`.
  - [x] Verify non-E2E tests (`dotnet test --filter "Category!=Acceptance&Category!=E2E"`) during development and run the full test suite (`dotnet test`) upon completion.

---

## 4. Completed Fixes Archive

All 48 completed Phase 11 onboarding, hardening, and bug-fix steps (`11.2` through `11.54`) are archived in:
- **Summary & DoD Record:** [`docs/archive/11_EXTEND_FEATURES.md`](../archive/11_EXTEND_FEATURES.md)
- **Step Specifications:** [`docs/archive/phase-11/`](../archive/phase-11/)
