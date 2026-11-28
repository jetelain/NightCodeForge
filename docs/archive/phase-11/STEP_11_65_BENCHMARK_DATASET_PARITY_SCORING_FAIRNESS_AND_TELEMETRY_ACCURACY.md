# ðŸ“Š Step 11.65: Benchmark Dataset Parity, Bug-Triggering MinorFix Validation, Scoring Fairness & Telemetry Accuracy

> **Parent Plan:** [docs/archive/11_EXTEND_FEATURES.md](../11_EXTEND_FEATURES.md)  
> **Target Framework:** .NET 10 (C# 13)  
> **Status:** Completed  
> **Target Projects:** `NightCodeForge.Core`, `NightCodeForge.Agents`, `NightCodeForge.Benchmark.Cli`, `NightCodeForge.Core.Tests`, `NightCodeForge.Agents.Tests`, `NightCodeForge.Benchmark.Cli.Tests`, `benchmarks/datasets/`  
> **Dependencies:** [Step 11.61](STEP_11_61_BENCHMARK_SOLUTION_DISCOVERY_NODE_PROVISIONING_AND_REVIEW_GRADING.md), [Step 11.62](STEP_11_62_BENCHMARK_CUSTOM_IMAGE_ABI_RUSTUP_MOCHA_AND_REVIEW_GRADING.md), [Step 11.63](STEP_11_63_BENCHMARK_DATASET_CURATION_DEPENDENCY_PINNING_AND_HYGIENE.md), [Step 11.64](STEP_11_64_CONTAINER_SHELL_FRAMING_ENV_PARITY_MSBUILD_RESILIENCE_AND_AGENT_HYGIENE.md), `BenchmarkVerifier`, `BenchmarkDatasetValidator`, `BenchmarkSummaryAggregator`, `BenchmarkReportFormatter`, `CrucibleBenchmarkRunner`, `IBenchmarkProvenanceProvider`, `BenchmarkProvenanceProvider`, `ThinkingAndActivityTracingChatClient`, `InMemoryCrucibleConversationCoordinator`, `CrucibleAgentHarness`, `AgentRunMetrics`, `StandardWorkflowDefinitions`, `CuratedDatasetsValidationTests`  
> **Aligns With:** `AGENTS.md`, `docs/PLAN.md`

> [!IMPORTANT]
> **Specification Disclaimer:** This document is a behavioral specification. It defines required behaviors, architectural invariants, and verification criteria. Specific internal helper names and implementation details remain at the discretion of the implementer within these constraints.

---

## 1. Context & Motivation

Analysis of the post-Step-11.63 `v1-smoke` benchmark runs (`v1-smoke-Qwen3.8-27B-latest-20261126-212337` and `v1-smoke-Flint-latest-20261126-212416` on `feat/bench @ b3b522f`) identified five dataset-parity, validation, scoring, and telemetry inaccuracies:

1. **Python `TaskReview` Entries Omit `custom_container_image: "python:3.11-bookworm"` & `ncf-tr-py-001` Uses Python 3.9-Era `collections.Mapping` (`Qwen / ncf-tr-py-001` false negative):**
   - While all Python `Implementation` and `CodeReview` entries in `v1-smoke.manifest.json` and `v1-full.manifest.json` specify `"custom_container_image": "python:3.11-bookworm"`, `ncf-tr-py-001` (`psf/requests` @ `3c88e52`, 2014) and `ncf-tr-py-002` (`pytest-dev/pytest` @ `aa55975`, 2022) omit it and therefore run in the default image with Python 3.12.
   - Furthermore, `psf/requests` @ `3c88e52` still imports `collections.MutableMapping` / `collections.Mapping` directly (removed in Python 3.10+), so simply switching `3c88e52` to `python:3.11-bookworm` still fails `import requests`. Both models hit import failures; Qwen explicitly expanded its `ncf-tr-py-001` specification to include *"Prerequisite: Python 3.12 import compatibility (7 files)"* and escalated `difficulty: difficult`, failing `Expected difficulty 'standard' but got 'difficult'`.
2. **`MinorFix` Code Review Tests Already Pass on Buggy `candidate_patch` (`v1-code-review.jsonl`):**
   - `MinorFix` entries in `v1-code-review.jsonl` (`ncf-cr-csharp-003`, `ncf-cr-rust-001`, `ncf-cr-csharp-010`, `ncf-cr-js-002`, etc.) currently have no `test_patch` exercising the injected defect, and their `fail_to_pass` tests already pass against `BaseCommit + CandidatePatch`.
   - Furthermore, `BenchmarkDatasetValidator.ValidateCodeReviewAsync` only verifies that `GoldPatch` passes tests, without verifying that `CandidatePatch` actually fails `fail_to_pass`. As a result, any non-empty edit mentioning the expected keywords scores `1.00` without actually fixing the bug.
3. **Distorted `DeterministicScore` Formulas (`Implementation` & `TaskReview`):**
   - **Implementation (`pass_to_pass` dilution):** `BenchmarkVerifier` computes `(failToPassPassed + passToPassPassed) / (failToPassTotal + passToPassTotal)`. On `Qwen / pytest-dev__pytest-10356`, failing the sole target test (`fail_to_pass = 0/1`) while passing `79/79` `pass_to_pass` tests produced `DeterministicScore = 0.99`, inflating Qwen's Python `Det. Score` to `0.95` despite `50%` Pass@1.
   - **TaskReview (verdict asymmetry):** `BenchmarkVerifier.VerifyTaskReviewAsync` appends `frontmatterPreserved` and `nonTrivialEnrichment` to `checks` only when `actualVerdict == Ready`. Consequently, wrongly predicting `Ready` on a `NeedsClarification` task (`Qwen / ncf-tr-rust-002`) scores `0.75` (`3/4`), whereas wrongly predicting `NeedsClarification` on a `Ready` task scores `0.50` (`1/2`).
4. **Borderline `TaskReview` `expected_difficulty` Calibration (`ncf-tr-csharp-003`):**
   - `ncf-tr-csharp-003` (`App-vNext/Polly` `DecorrelatedJitterBackoffV2`) is a ~5-line single-method overflow guard with isolated unit tests, which matches the `TaskReview` rubric's `'standard'` criteria (*"localized single-module/single-file changes... and isolated unit tests"*) while also touching `'difficult'` (*"non-trivial numeric/encoding/edge-case invariants"*). Qwen delivered a valid `Ready` spec and matched all anchors (`0.80` score), yet failed Pass@1 on `Expected difficulty 'difficult' but got 'standard'`.
5. **Missing C# `pass_to_pass` Regression Coverage & Telemetry/Provenance Accuracy:**
   - Six of eight C# `v1-smoke` `Implementation` instances (`autofac-1424`, `messagepack-2045`, `restsharp-1676`, `serilog-2103`, `spectre-console-1338`, `spectre-console-1458`) have `pass_to_pass = 0`, providing no regression signal on existing C# tests.
   - `CrucibleBenchmarkRunner.ResolveEffectiveMetrics` divides `completionTokens` by total wall-clock `agentDurationMs` (including container builds, test runs, and command waits), while `ThinkingAndActivityTracingChatClient` wraps the inner model calls directly and can measure pure LLM inference duration across turns.
   - Because `.NET` SDKs are bind-mounted from the host/WSL `/usr/share/dotnet` into `ncf-agent-base:v6`, `BenchmarkProvenanceProvider` currently records only host `dotnet --version` (`11.0.100`) in `detected_tool_versions` without distinguishing the containerized/WSL SDK (`10.0.115`) actually used inside benchmark containers.

---

## 2. Required Behaviors & Constraints

### 2.1 Python `TaskReview` Container Image & Commit Compatibility Parity
- All Python `TaskReview` entries in `benchmarks/datasets/v1-task-review.jsonl`, `benchmarks/datasets/v1-smoke.manifest.json`, and `benchmarks/datasets/v1-full.manifest.json` (`ncf-tr-py-001`, `ncf-tr-py-002`, and any future Python `TaskReview` entries) MUST specify `"custom_container_image": "python:3.11-bookworm"` (and matching `pip_constraints` where needed; note that `TaskReview` runs offline with `disableNetwork: true`).
- Rebase `ncf-tr-py-001` onto a Python 3.11-compatible `psf/requests` commit (e.g., `0192aac24123735b3eaf9b08df46429bb770c283` / `v2.27.0` or `39d0fdd9096f7dceccbc8f82e1eda7dd64717a8e`, where `requests/compat.py` imports from `collections.abc`) so `import requests` and codebase inspection work cleanly under `python:3.11-bookworm` without spurious Python compatibility detours.
- Update `CuratedDatasetsValidationTests` (and dataset parity tests) to verify that all Python entries across `Implementation`, `CodeReview`, and `TaskReview` specify `"custom_container_image": "python:3.11-bookworm"`.

### 2.2 Bug-Triggering `test_patch` & Pre-Fix Failure Validation for `MinorFix` Code Reviews
- Every `MinorFix` entry in `benchmarks/datasets/v1-code-review.jsonl` (`ncf-cr-csharp-003`, `ncf-cr-csharp-004`, `ncf-cr-csharp-005`, `ncf-cr-csharp-010`, `ncf-cr-js-002`, `ncf-cr-rust-001`, and any additional `MinorFix` entries) MUST include:
  - A `test_patch` placed in dedicated test file path(s) **strictly disjoint from all paths modified by `CandidatePatch` and `GoldPatch`** (because `BenchmarkVerifier.ResetAndApplyTestPatchAsync` resets every path in `TestPatch` back to `BaseCommit` before applying `TestPatch` â€” e.g., for `ncf-cr-rust-001`, place the bug-triggering test in a dedicated integration test under `crates/regex/tests/` exercising public `RegexMatcherBuilder` word-boundary matching rather than editing `crates/regex/src/word.rs` or candidate-modified test files).
  - Updated `fail_to_pass` containing the new bug-reproducing test(s) that **fail** on `BaseCommit + CandidatePatch + TestPatch` and **pass** on `BaseCommit + GoldPatch + TestPatch`, moving any pre-existing tests that already pass on `CandidatePatch` into `pass_to_pass`.
- `BenchmarkDatasetValidator.ValidateCodeReviewAsync` MUST verify for `MinorFix` instances with test assertions that:
  1. `BaseCommit + CandidatePatch + TestPatch` **fails** `fail_to_pass` (`FailToPassPassed == 0` when `FailToPass.Count > 0`, or overall suite fails when `FailToPass.Count == 0`, proving the test catches the injected bug and preventing false-positive passes), and
  2. `BaseCommit + GoldPatch + TestPatch` **passes** all `fail_to_pass` and `pass_to_pass` assertions.
- `CuratedDatasetsValidationTests` MUST validate unified diff integrity for every `MinorFix` `test_patch` and assert path disjointness between `test_patch` and `CandidatePatch`/`GoldPatch`.

### 2.3 Fair & Un-Diluted `DeterministicScore` Formulas
- **Implementation (`fail_to_pass` Gating):** `BenchmarkVerifier` MUST NOT allow `pass_to_pass` counts to mask `fail_to_pass` failures. Compute `DeterministicScore` across the four assertion-count cases as:
  - `failToPassTotal > 0 && passToPassTotal > 0`: `failToPassRate * passToPassRate`
  - `failToPassTotal > 0 && passToPassTotal == 0`: `failToPassRate`
  - `failToPassTotal == 0 && passToPassTotal > 0`: `passToPassRate`
  - `failToPassTotal == 0 && passToPassTotal == 0`: `exitCode == 0 ? 1.0 : 0.0`
  So failing all `fail_to_pass` tests always yields `0.00` regardless of how many `pass_to_pass` tests pass.
- **TaskReview (Verdict Symmetry):** `BenchmarkVerifier.VerifyTaskReviewAsync` MUST weight verdict correctness (`verdictMatched`), anchor coverage (`anchorScore`), and `Ready`-specific quality checks symmetrically so that when `verdictMatched == false`, `Ready`-specific checks (`frontmatterPreserved`, `nonTrivialEnrichment`, `difficultyScore`) do NOT contribute to `DeterministicScore`. Both wrong-verdict directions (`Ready` on a `NeedsClarification` task vs `NeedsClarification` on a `Ready` task) MUST produce symmetric scores (`<= 0.50` for identical `anchorScore`), while `scopeValid == false` remains `0.00` and a full passing review remains `1.00`.

### 2.4 `TaskReview` Difficulty Calibration & Rubric Clarity
- Audit `benchmarks/datasets/v1-task-review.jsonl` `expected_difficulty` labels alongside `StandardWorkflowDefinitions`'s `TaskReview` rubric using consistent single-tier alignment:
  - Reclassify `ncf-tr-csharp-003` (`App-vNext/Polly` `DecorrelatedJitterBackoffV2`) to `"standard"` (a ~5-line localized single-method guard with isolated unit tests).
  - Refine `StandardWorkflowDefinitions`'s `TaskReview` rubric wording so localized single-method/single-file changes with isolated unit tests map unambiguously to `'standard'`, while `'difficult'` applies to multi-file or stateful changes, complex encoding/boundary mechanics (such as `ncf-tr-rust-001`), or cross-component API coordination, preserving `CuratedDatasetsValidationTests` tier-balance assertions (`readyEscalatedCount >= 3` in `v1-full` and escalated coverage in `v1-smoke`).

### 2.5 C# `pass_to_pass` Regression Coverage, LLM `TokensPerSecond` & Container Provenance
- Populate deterministic `pass_to_pass` regression tests on the C# `v1-smoke` `Implementation` instances that currently have `pass_to_pass = 0` (`autofac__autofac-1424`, `messagepack-csharp__messagepack-csharp-2045`, `restsharp__restsharp-1676`, `serilog__serilog-2103`, `spectreconsole__spectre-console-1338`, `spectreconsole__spectre-console-1458`) across BOTH `v1-smoke.manifest.json` AND `v1-full.manifest.json` (preserving superset parity in `CuratedDatasetsValidationTests`) where stable non-flaky suite tests exist, re-materialize local `~/.nightcodeforge/benchmarks/datasets/*.jsonl`, and validate live via `ncf-bench dataset validate`.
- Track accumulated pure-LLM inference duration (`InferenceDurationMs`) across turns inside `ThinkingAndActivityTracingChatClient` (measuring only `GetResponseAsync` / `GetStreamingResponseAsync` model inference latency, excluding tool execution time), propagate `InferenceDurationMs` onto `AgentRunMetrics` (appended with default `0` to preserve existing callers) in both `CrucibleAgentHarness.CurrentMetrics()` and `InMemoryCrucibleConversationCoordinator.SaveTranscriptAsync` (preserving accurate timing on timeouts/aborts), and update `CrucibleBenchmarkRunner.ResolveEffectiveMetrics` to compute `BenchmarkInstanceResult.TokensPerSecond` from `InferenceDurationMs` when positive (falling back to wall-clock `agentDurationMs` only when `InferenceDurationMs <= 0`).
- Update `IBenchmarkProvenanceProvider` / `BenchmarkProvenanceProvider` to accept `ExecutionMode` and distinguish host vs containerized/WSL tool versions (`dotnet_host` vs `dotnet_container`, probing the WSL/container-mounted `.NET` SDK runtime when `ContainerSandbox` or `WslDirect` is active rather than assuming `dotnet` is baked into `ncf-agent-base:v6` or recording only the host SDK version), so `results.json` (and `summary.md` via `BenchmarkReportFormatter` if surfaced) accurately reflects the `.NET` SDK version running inside benchmark containers.

---

## 3. Definition of Done (DoD) Checklist

- [x] Rebase `ncf-tr-py-001` onto a Python 3.11-compatible `psf/requests` commit and pin `"custom_container_image": "python:3.11-bookworm"` on all Python `TaskReview` entries in `v1-task-review.jsonl`, `v1-smoke.manifest.json`, and `v1-full.manifest.json`, with dataset parity unit tests across all categories.
- [x] Add disjoint, bug-triggering `test_patch` + partitioned `fail_to_pass` / `pass_to_pass` assertions to every `MinorFix` entry in `v1-code-review.jsonl`, enforce pre-fix failure on `CandidatePatch + TestPatch` alongside post-fix pass on `GoldPatch + TestPatch` in `BenchmarkDatasetValidator.ValidateCodeReviewAsync`, and assert diff integrity and path disjointness in `CuratedDatasetsValidationTests`, with unit tests.
- [x] Update `BenchmarkVerifier` `DeterministicScore` calculations for `Implementation` (`f2pRate * p2pRate` when both totals > 0) and symmetric `TaskReview` verdict scoring (`<= 0.50` on mismatched verdicts), with unit tests.
- [x] Reclassify `ncf-tr-csharp-003` to `standard` and refine `StandardWorkflowDefinitions` `TaskReview` rubric wording so `standard` vs `difficult` tier boundaries are unambiguous, with unit tests.
- [x] Add `pass_to_pass` regression coverage to the `v1-smoke` and `v1-full` C# `Implementation` manifest entries where applicable, track pure-LLM `InferenceDurationMs` in `ThinkingAndActivityTracingChatClient` -> `AgentRunMetrics` for `TokensPerSecond`, distinguish host vs container/WSL `.NET` SDK versions in `BenchmarkProvenanceProvider`, re-materialize datasets, and pass live `ncf-bench dataset validate`.
- [x] Verify CI-scoped tests (`dotnet test --filter "Category!=Acceptance&Category!=E2E"`), Acceptance tests (`dotnet test --filter "Category=Acceptance"`), and full test suite (`dotnet test`) pass.
