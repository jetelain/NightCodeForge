# 🛡️ Step 11.67: Benchmark Anti-Cheat CDN/Audit Telemetry, Python Child Image & TaskReview Parity

> **Parent Plan:** [docs/fixes/README.md](README.md)  
> **Target Framework:** .NET 10 (C# 13)  
> **Status:** Completed  
> **Target Projects:** `NightCodeForge.Benchmark.Cli`, `NightCodeForge.Benchmark.Cli.Tests`, `benchmarks/datasets/`  
> **Dependencies:** [Step 11.62](STEP_11_62_BENCHMARK_CUSTOM_IMAGE_ABI_RUSTUP_MOCHA_AND_REVIEW_GRADING.md), [Step 11.63](STEP_11_63_BENCHMARK_DATASET_CURATION_DEPENDENCY_PINNING_AND_HYGIENE.md), [Step 11.65](STEP_11_65_BENCHMARK_DATASET_PARITY_SCORING_FAIRNESS_AND_TELEMETRY_ACCURACY.md), [Step 11.66](STEP_11_66_TOOL_BINDING_PARSER_DRIFT_AND_CONTAINER_HARDENING.md), `BenchmarkConstants`, `CrucibleBenchmarkRunner`, `BenchmarkVerifier`, `BenchmarkTestCommandResolver`, `BenchmarkModels`, `BenchmarkSummaryAggregator`, `BenchmarkReportFormatter`  
> **Aligns With:** `AGENTS.md`, `docs/PLAN.md`

> [!IMPORTANT]
> **Specification Disclaimer:** This document is a behavioral specification. It defines required behaviors, architectural invariants, and verification criteria. Specific internal helper names and implementation details remain at the discretion of the implementer within these constraints.

> [!NOTE]
> **Branch Workflow Rule:** This step is strictly scoped to `feat/bench` (`src/NightCodeForge.Benchmark.Cli`, `tests/NightCodeForge.Benchmark.Cli.Tests`, `benchmarks/datasets/`, and `docs/`). It requires **zero** modifications to `NightCodeForge.Core`, `NightCodeForge.Agents`, or `NightCodeForge.Infrastructure`. Changes requiring core or container-infrastructure modifications are deferred to [Step 11.68](STEP_11_68_OFFLINE_BENCHMARK_PREWARM_AND_CACHE_ISOLATION.md) (implemented on `main` first and merged into `feat/bench`) and [Step 12.45A](../features/STEP_12_45A_CROSS_PLATFORM_LAYERED_CONTAINER_IMAGES_AND_TOOLCHAIN_VOLUMES.md).

---

## 1. Context & Motivation

Post-run analysis of the `v1-smoke` benchmark runs for `Qwen3.8-27B:latest` and `Flint:latest` (run against `c454971`) identified four concrete benchmark harness and dataset parity issues that can be fixed immediately within `NightCodeForge.Benchmark.Cli` and `benchmarks/datasets/`:

1. **Anti-Cheat Bypasses via CDNs & Unmonitored Registry/Cache Inspection:**
   - `BenchmarkDefaults.BlockedAntiCheatHosts` successfully blocked GitHub/GitLab/Bitbucket/HuggingFace, yet `Flint:latest` bypassed it on `vuejs__core-11915` by fetching `@vue/compiler-core` bundles from `unpkg.com` and `npm pack`, and on `pytest-dev__pytest-10356` by downloading upstream `pytest==7.2.0` wheels/sdists via `pip download`. It also inspected cached `.nupkg` archives via `ilspycmd` on `spectreconsole__spectre-console-1458`.
   - Primary package registries (`pypi.org`, `registry.npmjs.org`, `api.nuget.org`, `crates.io`) cannot be blocked while agents still install dependencies online without causing widespread false negatives; full registry blocking is therefore handled in [Step 11.68](STEP_11_68_OFFLINE_BENCHMARK_PREWARM_AND_CACHE_ISOLATION.md) via pre-warm + offline execution. However, package-browsing CDNs/source-search mirrors can be blocked immediately with zero false negatives, and post-run transcript/bash-audit telemetry can flag high-signal cheat attempts in reports.
2. **`TaskReview` `<task>` Metadata Contradiction (`ncf-tr-ts-002` False Failure):**
   - `CrucibleBenchmarkRunner.RunSingleEvaluationAsync` constructs `ephemeralTask` with hardcoded `Id = 0` and an 80-character truncated `ProblemStatement` title. For `TaskReview` instances whose on-disk markdown file (`docs/tasks/<id>.md`) declares a real `id` and `title` (e.g., `ncf-tr-ts-002` with `id: 202`), the `<task>` prompt block contradicts the specification on disk, causing cautious models to invoke `ask` (`NeedsClarification`) on a `Ready` task.
3. **`TaskReview` Network Mismatch vs. Real NightCodeForge Workflow:**
   - `CrucibleBenchmarkRunner.BuildExecutionScope` disables networking (`disableNetwork = true`) exclusively for `TaskReview`, diverging from real NightCodeForge workflows where `TaskReview` shares the same container network permissions as `Implementation` (which triggered `NU1900` NuGet Audit errors when models ran `dotnet build` to verify findings on `ncf-tr-csharp-001`). Aligning `TaskReview` network access while strictly enforcing code-change scope penalties (`ModifiedFilesOutsideDocsTasks`) restores workflow parity.
4. **`python:3.11-bookworm` Missing CLI Tooling & Unconstrained Verification Venv Guard:**
   - `python:3.11-bookworm` lacks `rg` (explicitly recommended by agent `<rules>`) and pre-installed Python test tooling, wasting turns and motivating agents to improvise host workarounds.
   - Step 11.66 already hardened `BenchmarkTestCommandResolver.WrapPythonContainerCommand` for `pip_constraints` runs and removed the stray `/home/gruearbre/.dotnet/pytest` shim, but **unconstrained** Python runs still gate venv creation on `if ! pytest --version >/dev/null 2>&1;`. If a child image pre-installs `pytest` or an agent/environment exposes a global `pytest`, verification would skip `-r requirements/...` and `pip install -e .`.

*(Note: Host `~/.dotnet` `:rw` isolation, `bash` 300s timeout floor, stack skill adoption, and `ncf-cr-js-001` grading boundaries are explicitly out of scope for this step.)*

---

## 2. Required Behaviors & Constraints

### 2.1 Immediate Anti-Cheat CDN/Mirror Blocklist & Audit Telemetry (`BenchmarkConstants`, `CrucibleBenchmarkRunner`, `BenchmarkModels`, `BenchmarkSummaryAggregator`, `BenchmarkReportFormatter`)
- **CDN & Source-Mirror Blocklist Expansion:**
  - Extend `BenchmarkDefaults.BlockedAntiCheatHosts` in `BenchmarkConstants.cs` with package-browsing CDNs and source-index mirrors that package managers do not require for dependency resolution:
    - `unpkg.com`, `cdn.jsdelivr.net`, `esm.sh`, `skypack.dev`, `cdnjs.cloudflare.com`, `deps.dev`, `fuget.org`, `nuget.info`, `sourcegraph.com`, `grep.app`, `searchcode.com`.
  - **Do NOT block** primary package registries (`pypi.org`, `files.pythonhosted.org`, `registry.npmjs.org`, `api.nuget.org`, `crates.io`, `static.crates.io`) in this step; online dependency installation must continue working without false negatives until Step 11.68 introduces pre-warmed offline execution.
- **Post-Run Anti-Cheat Bash Audit Telemetry:**
  - Inspect executed `bash` commands during `CrucibleBenchmarkRunner.BuildTelemetryBreakdown` (reusing the `BashAudit` metadata and `ToolArguments` command extraction pipeline already used for `csnav`/`codecondenser` counts, deduplicated per `CallId`).
  - Detect high-signal cheat/exfiltration patterns across distinct named signal categories, including:
    - Package archive downloads / metadata probing (`pip download`, `npm pack`, `npm view`, `npm info` targeting external packages).
    - CDN/source-host HTTP fetches (`unpkg.com`, `cdn.jsdelivr.net`, `esm.sh`, `skypack.dev`, `cdnjs.cloudflare.com`, `deps.dev`, `fuget.org`, `nuget.info`, `sourcegraph.com`, `grep.app`, `searchcode.com`, `github.com`, `raw.githubusercontent.com`, `gitlab.com`, `bitbucket.org`, `huggingface.co`).
    - External git remote queries (`git ls-remote`, `git fetch`, `git clone`, `git pull` targeting non-local/HTTP(S)/SSH remotes).
    - Decompilation or cached archive extraction of repo/dependency binaries (`ilspycmd`, inspecting/unzipping `.nupkg` from `/cache/nuget`, inspecting target-crate sources under `/cache/cargo` or `.cargo/registry`).
  - Ensure normal, legitimate commands (`pip install -e .`, `pip install -r requirements/...`, `npm ci`, `npm install`, `dotnet build`, `dotnet test`, `cargo test`, `curl`/`wget` against `localhost`/`127.0.0.1`) do **not** trigger false-positive alerts.
- **Reporting & Visibility Without Retroactive Score Mutation:**
  - Record `AntiCheatAlertCount` and distinct matched signal/category labels (e.g., `AntiCheatSignals`) on `ToolCallTelemetryBreakdown` / `BenchmarkInstanceResult` in `results.json`.
  - Aggregate total anti-cheat alert counts on `BenchmarkCandidateSummary` and surface them in `BenchmarkReportFormatter` (`summary.md`, console/ASCII tables, and JSON output) so compromised passes are immediately visible alongside `DispatcherRepairs`.
  - This telemetry is diagnostic/auditing only in Step 11.67 and MUST NOT retroactively alter `Passed` or `DeterministicScore`.
- **Unit Tests:**
  - Verify the expanded `BlockedAntiCheatHosts` set includes the CDN/mirror hosts and omits primary package registries.
  - Verify anti-cheat command pattern detection on positive examples (matching real patterns observed in `v1-smoke`) and negative examples (legitimate installs/builds/localhost checks), plus deduplication between `BashAudit` and `ToolArguments`.
  - Verify `BenchmarkSummaryAggregator` and `BenchmarkReportFormatter` aggregate and render anti-cheat alert counts cleanly.

### 2.2 `TaskReview` Frontmatter-Synchronized `<task>` Metadata (`CrucibleBenchmarkRunner`)
- When provisioning a `TaskReview` session in `CrucibleBenchmarkRunner.RunSingleEvaluationAsync`, parse `workspace.TaskMarkdownInitialContent` via `BenchmarkVerifier.ParseTaskMarkdown` before constructing `ephemeralTask`.
- If the parsed frontmatter specifies a valid numeric `id` (greater than `0`), assign it to `ephemeralTask.Id`; otherwise fall back to `0`.
- If the parsed frontmatter specifies a non-empty `title`, assign the full trimmed `title` verbatim to `ephemeralTask.Title` without applying the 80-character `TruncateTitle` truncation; otherwise fall back to `TruncateTitle(instance.ProblemStatement)`.
- Keep `Implementation` and `CodeReview` `ephemeralTask` construction unchanged (`Id = 0`, `TruncateTitle(instance.ProblemStatement)`).
- **Unit Tests:**
  - Verify `TaskReview` sessions propagate frontmatter `id` and full non-truncated `title` into `ForgeTask` / `<task>` scope.
  - Verify safe fallback when frontmatter is missing, malformed, or lacks `id`/`title`, and verify `Implementation`/`CodeReview` behavior remains unchanged.

### 2.3 `TaskReview` Network Parity & Code-Change Scope Enforcement (`CrucibleBenchmarkRunner`, `BenchmarkVerifier`)
- **Network Parity with `Implementation` and `CodeReview`:**
  - Update `CrucibleBenchmarkRunner.BuildExecutionScope` so `TaskReview` sets `disableNetwork: false` and applies `blockedHosts: BenchmarkDefaults.BlockedAntiCheatHosts`, matching `Implementation` and `CodeReview` (and aligning with real NightCodeForge workflow permissions).
- **Strict Code-Change Detection & Score Penalty:**
  - Preserve and verify `CrucibleBenchmarkRunner`'s extraction of `ModifiedFilesOutsideDocsTasks` (both working-tree changes and any committed diffs against `workspace.BaseCommit`) and `BenchmarkVerifier.VerifyTaskReviewAsync`'s scope enforcement (`scopeValid = outcome.ModifiedFilesOutsideDocsTasks.Count == 0` -> `Passed = false`, `DeterministicScore = 0.0`, and explicit failure reason listing modified/untracked out-of-scope paths).
  - Additionally, audit mid-run `edit` and `write` tool calls against files outside `docs/tasks/` (excluding transient scratch paths already cleaned before completion if any, or clearly distinguishing reverted production edits): if any file outside `docs/tasks/` remains modified/untracked on completion, `DeterministicScore` MUST be `0.0` (`Passed = false`); if mid-run out-of-scope `edit`/`write` calls occurred and were reverted, record a clear warning/diagnostic without breaking legitimate scratch verification under `/tmp`.
- **Unit Tests:**
  - Verify `BuildExecutionScope` produces `DisableNetwork == false` and `BlockedHosts == BlockedAntiCheatHosts` for `TaskReview`.
  - Verify `VerifyTaskReviewAsync` fails and zeroes `DeterministicScore` (even when `actualVerdict == expectedVerdict` and all anchor findings match) whenever `ModifiedFilesOutsideDocsTasks` is non-empty.

### 2.4 Benchmark Python Tooling Child Image & Unconditional Verification Venv (`NightCodeForge.Benchmark.Cli`, `benchmarks/datasets/`)
- **Benchmark-Scoped Python Child Image (`ncf-bench-python-3.11:v1`):**
  - Introduce a child image builder/ensurer inside `NightCodeForge.Benchmark.Cli` (invoking `docker` via `IProcessRunner`, supporting WSL2 `wsl.exe` routing on Windows and direct `docker` on Linux/macOS) that derives `ncf-bench-python-3.11:v1` from `python:3.11-bookworm` with standard CLI tools required by agent `<rules>` (`ripgrep`, `git`, `jq`, `procps`, `curl`, `ca-certificates`, `unzip`).
  - Use an async per-image-tag `SemaphoreSlim` lock so concurrent benchmark/validation workers never race `docker build` on the same tag, inspect existence via `docker image inspect` before building, and fail fast with a `BenchmarkException` (surfacing build stderr) if the build fails.
  - Intentionally keep the image tag **outside** `ContainerMountAndDriftBuilder.IsManagedBaseImage` (`ncf-agent-base` / `nightcodeforge-agent`) so Step 11.62's non-managed multiarch shared-library ABI isolation (`OPENSSL_3.3.0` fix) continues to apply automatically.
  - Ensure `BenchmarkToolchainPreflightValidator` (or runner provisioning before container creation) ensures `ncf-bench-python-3.11:v1` is built when an instance specifies that custom image in `ContainerSandbox` mode (using an explicit non-managed-image registry/resolver, not modifying `NightCodeForge.Infrastructure`).
- **Unconditional `/tmp/ncf-bench-venv` Provisioning in `WrapPythonContainerCommand`:**
  - Update the unconstrained branch of `BenchmarkTestCommandResolver.WrapPythonContainerCommand` so it also unconditionally provisions and prepends `/tmp/ncf-bench-venv/bin` to `PATH` (gating venv creation on `[ ! -x /tmp/ncf-bench-venv/bin/pytest ]` instead of `if ! pytest --version >/dev/null 2>&1`).
  - Guarantees that neither global tools in a child image nor any stray binary on `$PATH` can cause verification to skip `requirements/*.txt` or `pip install -e .`.
- **Dataset & Manifest Alignment:**
  - Update Python `custom_container_image` references from `python:3.11-bookworm` to `ncf-bench-python-3.11:v1` in `benchmarks/datasets/v1-smoke.manifest.json`, `benchmarks/datasets/v1-full.manifest.json`, `benchmarks/datasets/v1-code-review.jsonl`, and `benchmarks/datasets/v1-task-review.jsonl`, and re-materialize local `.nightcodeforge/benchmarks/datasets/*.jsonl`.
- **Unit & Live Validation Tests:**
  - Add unit tests for the Python child image Dockerfile content, tag locking/idempotent skip when already present, fail-fast build error reporting, unconditional `/tmp/ncf-bench-venv` command generation (constrained and unconstrained), and dataset/manifest parity tests asserting Python entries target `ncf-bench-python-3.11:v1`.
  - Verify live `ncf-bench dataset validate --verify` passes across the updated Python instances in `v1-smoke` / `v1-full`.

---

## 3. Definition of Done (DoD) Checklist

- [x] **Anti-Cheat Blocklist & Telemetry:** Add CDN/source-mirror hosts to `BenchmarkDefaults.BlockedAntiCheatHosts` (keeping primary package registries unblocked), detect high-signal cheat bash commands in `CrucibleBenchmarkRunner.BuildTelemetryBreakdown`, persist counts/signals in `results.json`, and display them in `BenchmarkReportFormatter` (`summary.md`/JSON/ASCII), with unit tests.
- [x] **`TaskReview` Frontmatter Sync:** Populate `TaskReview` `ephemeralTask.Id` and full non-truncated `ephemeralTask.Title` from on-disk task markdown frontmatter in `CrucibleBenchmarkRunner`, with unit tests.
- [x] **`TaskReview` Network Parity & Scope Enforcement:** Set `disableNetwork: false` with `BlockedAntiCheatHosts` for `TaskReview` in `CrucibleBenchmarkRunner.BuildExecutionScope`, preserve/verify `ModifiedFilesOutsideDocsTasks` hard `0.0` score penalty in `BenchmarkVerifier.VerifyTaskReviewAsync`, and add unit tests.
- [x] **Python Child Image & Unconditional Venv:** Add on-demand `ncf-bench-python-3.11:v1` child image provisioning (with `ripgrep`, `git`, `jq`, `procps`, `curl`, `ca-certificates`, `unzip` on `python:3.11-bookworm`, per-tag lock, fail-fast errors) in `NightCodeForge.Benchmark.Cli`, make `/tmp/ncf-bench-venv` unconditional for all Python verification runs in `BenchmarkTestCommandResolver.WrapPythonContainerCommand`, update Python dataset/manifest entries (`v1-smoke.manifest.json`, `v1-full.manifest.json`, `v1-code-review.jsonl`, `v1-task-review.jsonl`) and re-materialize local datasets, with unit tests and live Python `ncf-bench dataset validate --verify` passing.
- [x] **Branch Boundary & Full Verification:** Confirm zero changes outside `src/NightCodeForge.Benchmark.Cli`, `tests/NightCodeForge.Benchmark.Cli.Tests`, `benchmarks/datasets/`, and `docs/`, run `dotnet test --filter "Category!=Acceptance&Category!=E2E"` during development, and run the full test suite (`dotnet test`) upon completion with all tests passing.
