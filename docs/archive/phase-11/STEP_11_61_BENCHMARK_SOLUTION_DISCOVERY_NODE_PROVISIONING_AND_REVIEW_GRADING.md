# ðŸ›¡ï¸ Step 11.61: Benchmark Solution Discovery, Dataset Sync, Node Provisioning & Review Grading Accuracy

> **Parent Plan:** [docs/archive/11_EXTEND_FEATURES.md](../11_EXTEND_FEATURES.md)  
> **Target Framework:** .NET 10 (C# 13)  
> **Status:** Completed  
> **Target Projects:** `NightCodeForge.Benchmark.Cli`, `NightCodeForge.Benchmark.Cli.Tests`, `NightCodeForge.Core`, `NightCodeForge.Core.Tests`, `NightCodeForge.Agents`, `NightCodeForge.Agents.Tests`, `benchmarks`  
> **Dependencies:** [Step 11.58](STEP_11_58_BENCHMARK_VERIFICATION_PARITY_AND_LOG_PERSISTENCE.md), [Step 11.59](STEP_11_59_BENCHMARK_PROJECT_CONFIG_GLOBAL_JSON_AND_CODE_REVIEW_ALIGNMENT.md), `BenchmarkTestCommandResolver`, `BenchmarkVerifier`, `BenchmarkDatasetLoader`, `BenchmarkDatasetValidator`, `CrucibleBenchmarkRunner`, `CrucibleAgentHarness`, `StandardWorkflowDefinitions`  
> **Aligns With:** `AGENTS.md`, `docs/PLAN.md`

> [!IMPORTANT]
> **Specification Disclaimer:** This document is a behavioral specification. It defines required behaviors, architectural invariants, and verification criteria. Specific internal helper names and implementation details remain at the discretion of the implementer within these constraints.

---

## 1. Context & Motivation

Analysis of the November 24 `v1-smoke` benchmark runs (`Flint:latest` at 9/28 and `Qwen3.8-27B:latest` at 13/28) revealed five harness, dataset-synchronization, and grading bugs causing deterministic false negatives or unrunnable test suites (skills/tooling discovery such as `csharp-nav` and `codecondenser` is explicitly deferred to a separate future effort):

1. **Subdirectory `.sln` Discovery Picks Auxiliary Analyzer Solution (`spectreconsole__spectre-console-1338`, `spectreconsole__spectre-console-1458`):**
   - `BenchmarkTestCommandResolver.DiscoverDotNetTargets` orders subdirectory solutions by `(IsLowPriorityTarget, SolutionReferencesTestProject desc, CountPathSegments, OrdinalIgnoreCase)`.
   - In `spectreconsole/spectre.console`, both `src/Spectre.Console.Analyzer.sln` (referencing `Spectre.Console.Analyzer.Tests`) and `src/Spectre.Console.sln` (referencing `Spectre.Console.Tests` and the main library) tie on the first three criteria, so alphabetical ordering picks `src/Spectre.Console.Analyzer.sln` (`'A' < 's'`). Verification only builds and runs 28 analyzer tests and never executes `Spectre.Console.Tests`.
2. **Stale Materialized Local Datasets Silently Ignore Manifest Updates (`axios__axios-5892`, `pallets__flask-5014`, `psf__requests-1921`):**
   - Step 11.58 updated `benchmarks/datasets/v1-smoke.manifest.json` (switching `axios__axios-5892` from `ExitCode` to `JUnitXml` with `mocha-junit-reporter` and setting `python:3.9` / `python:3.11` on `psf__requests-1921` and `pallets__flask-5014`).
   - However, the already-materialized `~/.nightcodeforge/benchmarks/datasets/v1-smoke.jsonl` (created earlier) remained stale on disk with `test_result_format: ExitCode` and empty `custom_container_image`. Consequently, both models ran `axios__axios-5892` under `ExitCode`â€”passing 155/156 tests (including all 5 `GZIP decompression` tests) and failing `0/3` F2P solely because 1 unrelated test (`should support sockets`) fails with `ENOTSUP ./test.sock` on WSL/Windows bind mountsâ€”while both Python tasks ran on Python 3.12 instead of `3.9`/`3.11`.
3. **Anti-Cheat Host Blocking Breaks Node Native Postinstall Scripts (`facebook__docusaurus-9897`):**
   - `facebook/docusaurus` starts without `node_modules` or global `yarn`. When the container runs `yarn install`, `sharp`'s postinstall attempts to download prebuilt binaries from GitHub Releases, which `BlockedAntiCheatHosts` redirects/blocks (`sharp: Installation error: connect ECONNREFUSED 127.0.0.1:443`). This blocks both agent test runs and `WrapNodeContainerCommand` verification.
4. **Overly Strict `EvaluateExpectedFindings` AND-Logic & Misleading `Approve` F2P Totals (`ncf-cr-csharp-008`, `ncf-cr-csharp-001`, `ncf-cr-py-001`, `ncf-cr-ts-001`):**
   - On `ncf-cr-csharp-008` (single-file PR modifying `src/Polly.Core/Retry/RetryHelper.cs`), `Qwen3.8-27B` issued `request_changes` with exact root-cause diagnostics (`DecorrelatedJitterBackoffV2`, `double.IsNaN`, `double.IsInfinity`, `Inf - Inf = NaN`, `(long)NaN = long.MinValue`, `Math.Abs` `OverflowException`, and missing `RetryHelperTests.cs` tests), yet scored `0.0` on expected findings because the literal string `RetryHelper.cs` was not repeated in the feedback body (`fileMatched && symbolMatched && keywordsMatched`).
   - On `Approve` reviews where tests are intentionally not run, `VerifyCodeReviewAsync` reports `fail_to_pass_passed: 0` against `fail_to_pass_total: N`, producing confusing `0/1`, `0/2`, or `0/6` ratios on passed instances.
5. **Missing `TaskReview` Difficulty Rubric & Brittle Exact-Match Grading (`ncf-tr-ts-002`, `ncf-tr-csharp-003`):**
   - The `TaskReview` stage directive instructs the model to evaluate complexity (`'standard'`, `'difficult'`, or `'expert'`) and escalate when warranted, without defining criteria separating `difficult` from `expert`. On `ncf-tr-ts-002`, both models escalated `standard` -> `difficult` with thorough reactivity batching analysis, yet hard-failed solely on `Expected difficulty 'expert' but got 'difficult'`.

---

## 2. Required Behaviors & Constraints

### 2.1 Primary `.NET` Solution Selection
- Update subdirectory `.sln`/`.slnx` ranking in `BenchmarkTestCommandResolver` so that, when multiple solutions exist outside the repository root, selection ranks candidates using the following precedence:
  1. Non-low-priority targets before low-priority targets (`docs`, `doc`, `resources`, `scripts`, `tools`, `sample`, `benchmark`, `example`).
  2. Solutions referencing at least one test project before solutions referencing none.
  3. Non-auxiliary solutions before auxiliary/specialized solutions (e.g., solution filenames containing `.Analyzer`, `.Sandbox`, `.Benchmarks`, or `.Samples`).
  4. Higher count of referenced test projects (descending), then higher total referenced project count (descending).
  5. Fewer path segments (shallower depth), then case-insensitive ordinal tie-breaker.
- Ensure auxiliary solution deprioritization applies strictly to `.sln`/`.slnx` selection and does not suppress legitimate analyzer test `.csproj` projects when falling back to individual test projects in repositories without a `.sln`/`.slnx`.
- Ensure both agent `WorkspaceProjectConfig` resolution and verifier test execution select `src/Spectre.Console.sln` over `src/Spectre.Console.Analyzer.sln` for `spectreconsole/spectre.console`.

### 2.2 Manifest-Overlay & Stale Dataset Prevention
- Prevent materialized local `.jsonl` datasets (such as `~/.nightcodeforge/benchmarks/datasets/v1-smoke.jsonl`) from silently running with stale `custom_container_image`, `custom_test_command`, `test_result_format`, `category`, or crafted review definitions after repository-tracked manifests (`benchmarks/datasets/<suite>.manifest.json`) or crafted `.jsonl` files are updated.
- Support explicit `--manifest <path>` on `ncf-bench run` and `ncf-bench dataset validate` (failing fast if an explicitly provided manifest path does not exist), and wire `benchmarks/run-benchmark.ps1` to pass `--manifest` automatically when the repository contains `benchmarks/datasets/<Suite>.manifest.json`.
- When `--manifest` is omitted, perform best-effort manifest discovery (checking adjacent to the dataset file and under the repository's `benchmarks/datasets/<suite>.manifest.json`), proceeding cleanly without overlay for ad-hoc datasets that have no matching manifest.
- Apply deterministic manifest metadata overrides at dataset load/run/validate time: non-null manifest metadata fields (`custom_container_image`, `custom_test_command`, `test_result_format`, `category`, `language`, `repo`) override stale values from materialized `.jsonl` entries without requiring users to re-download and re-import external SWE-bench JSONL archives.
- When sibling repository-tracked crafted datasets (`v1-code-review.jsonl` / `v1-task-review.jsonl`) exist alongside the resolved manifest, overlay the latest crafted `CodeReview` / `TaskReview` definitions (`expected_verdict`, `expected_difficulty`, `expected_findings`, `candidate_patch`, `patch`, `task_specification`, `custom_container_image`, etc.) onto matching materialized suite entries (with manifest metadata overrides applied on top) so review dataset updates take effect immediately without manual re-materialization.
- Emit a clear diagnostic log/message whenever manifest or crafted-dataset overlays are applied or when a materialized dataset entry diverges from the pinned manifest.

### 2.3 Anti-Cheat-Compatible Node Dependency Provisioning
- Ensure Node/TypeScript/JavaScript monorepos (such as `facebook__docusaurus-9897`) can install dependencies and execute unit tests cleanly while `BlockedAntiCheatHosts` remains active:
  - In `WrapNodeContainerCommand`, when `yarn install` / `pnpm install` / `npm ci` / `npm install` fails (e.g., due to blocked native postinstall downloads such as `sharp` from GitHub), fall back directly to `--ignore-scripts` (preferring `--frozen-lockfile` / lockfile-preserving flags first before an unlocked `--ignore-scripts` retry) rather than retrying a full non-`--ignore-scripts` install.
  - Guard against incomplete `node_modules` left behind by failed agent installs (which `git clean -fd` preserves across resets into verification): detect incomplete `node_modules` (e.g., missing `node_modules/.bin` or package-manager install integrity marker) and run/repair installation with `--ignore-scripts` fallback rather than skipping install solely because `node_modules` exists, while still reusing an intact `node_modules` directory.
  - Ensure `yarn` / `pnpm` provisioning into the isolated user-writable npm prefix works reliably under non-root UID/GID even when `BlockedAntiCheatHosts` is enabled, and supply concise Node sandbox setup guidance in benchmark `WorkspaceProjectConfig`/session prompt framing (advising `--ignore-scripts` fallback on blocked native postinstall downloads and avoiding privileged global installs) so models do not stall in multi-minute retry loops.

### 2.4 Code Review Expected-Finding Matching, Rejection Reason Preservation & `Approve` Metric Hygiene
- Refine `BenchmarkVerifier.EvaluateExpectedFindings` so accurate reviews are not penalized with `0.0` solely for omitting a redundant filename on a single-file diff, while preserving strict grounding:
  - When a finding specifies both `file_path` and `symbol_or_line_hint` alongside keywords, allow strong corroborating evidence (matching `symbol_or_line_hint` AND substantial keyword coverageâ€”such as multiple/majority keywords rather than a single generic token like `"test"`) to satisfy the finding anchor even when the literal filename is not repeated, while preserving file-path grounding requirements for findings without `symbol_or_line_hint` (such as `TaskReview` findings).
  - Preserve structured rejection `reason` from `request_changes(feedback, reason)` end-to-end through `CrucibleAgentHarness`, `AgentRunResult`, and `BenchmarkAgentOutcome`, and include `ReviewerFeedback`, structured rejection `reason`, `Summary`, and `ModelPatch` in the evaluated corpus.
- When `VerifyCodeReviewAsync` skips test execution (i.e., `ActualVerdict != MinorFix` or verification tests disabled), report `FailToPassTotal = 0`, `FailToPassPassed = 0`, `PassToPassTotal = 0`, and `PassToPassPassed = 0` so `results.json` does not show `0/N` test ratios on passed `Approve` or `RequestChanges` instances.

### 2.5 Explicit `TaskReview` Difficulty Rubric & Calibrated Grading
- Update the `TaskReview` stage directive in `StandardWorkflowDefinitions` (and keep `BenchmarkStagePolicyResolver` consistent, preserving existing YAML frontmatter and `ask` tool invariants) to include concise, concrete criteria distinguishing `standard`, `difficult`, and `expert` complexity tiers (e.g., single-module localized change vs. multi-file/stateful/API-boundary change vs. concurrency, compiler/scheduler internals, or cross-cutting invariants).
- Calibrate `VerifyTaskReviewAsync` difficulty grading (and audit `v1-task-review.jsonl` expectationsâ€”including `ncf-tr-csharp-003` and `ncf-tr-ts-002`â€”against the new rubric for consistency):
  - Exact tier match passes the difficulty check with full credit (`1.0`).
  - Adjacent high-complexity escalation (`expected = expert` with `actual = difficult`, or `expected = difficult` with `actual = expert`) passes the difficulty gate (no binary hard failure when escalation above `standard` was triggered) with proportional partial credit (strictly between `0.0` and `1.0`).
  - Missed escalation (`expected = difficult|expert` with `actual = standard`) or false escalation (`expected = standard` with `actual = difficult|expert`) fails the difficulty gate (`Passed = false`, `0.0` credit).

---

## 3. Definition of Done (DoD) Checklist

- [x] Update `BenchmarkTestCommandResolver.DiscoverDotNetTargets` with the full subdirectory `.sln`/`.slnx` precedence (deprioritizing low-priority and auxiliary `.Analyzer`/`.Sandbox`/`.Benchmarks`/`.Samples` solutions, ranking by referenced test-project count then total project count, and preserving test `.csproj` fallback), with unit tests verifying `src/Spectre.Console.sln` beats `src/Spectre.Console.Analyzer.sln`.
- [x] Add `--manifest` support (`run` and `dataset validate`, wired in `benchmarks/run-benchmark.ps1`), manifest metadata overlay, and sibling crafted-dataset synchronization in `NightCodeForge.Benchmark.Cli` with divergence diagnostics so updated manifest/review fields take effect without manual re-imports, with unit tests.
- [x] Update `WrapNodeContainerCommand` with direct `--ignore-scripts` fallback when native postinstall downloads are blocked, incomplete `node_modules` detection/repair, and concise Node sandbox setup guidance in benchmark prompt/config framing while keeping `BlockedAntiCheatHosts` intact, with unit tests covering Docusaurus-style `yarn install` fallback and partial `node_modules` recovery.
- [x] Update `BenchmarkVerifier.EvaluateExpectedFindings` so findings with `symbol_or_line_hint` and substantial keyword matches are not zeroed out for omitting the literal filename (preserving strict grounding when no symbol hint exists), propagate structured rejection `reason` from `CrucibleAgentHarness` into `AgentRunResult`/`BenchmarkAgentOutcome`/evaluated corpus, and set `FailToPassTotal`/`PassToPassTotal` to `0` when tests are skipped on `Approve`/`RequestChanges`, with unit tests (`ncf-cr-csharp-008` regression test).
- [x] Add explicit `standard`/`difficult`/`expert` rubric criteria to `StandardWorkflowDefinitions`'s `TaskReview` stage directive, audit `v1-task-review.jsonl` against the rubric, and calibrate `VerifyTaskReviewAsync` difficulty scoring (passing adjacent `difficult`/`expert` tiers with partial credit while failing missed or false escalations), with unit tests.
- [x] Verify all non-E2E tests pass via `dotnet test --filter "Category!=Acceptance&Category!=E2E"`.
