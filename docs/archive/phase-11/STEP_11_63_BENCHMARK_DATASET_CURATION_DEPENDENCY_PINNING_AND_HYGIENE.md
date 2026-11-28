# ðŸ›¡ï¸ Step 11.63: Modern-Era Dataset Curation, Python Dependency Pinning, Verification Workspace Hygiene, MaxTurns Diagnostics & TaskReview Anchor Grounding

> **Parent Plan:** [docs/archive/11_EXTEND_FEATURES.md](../11_EXTEND_FEATURES.md)  
> **Target Framework:** .NET 10 (C# 13)  
> **Status:** Completed  
> **Target Projects:** `NightCodeForge.Core`, `NightCodeForge.Benchmark.Cli`, `NightCodeForge.Benchmark.Cli.Tests`  
> **Dependencies:** [Step 11.58](STEP_11_58_BENCHMARK_VERIFICATION_PARITY_AND_LOG_PERSISTENCE.md), [Step 11.61](STEP_11_61_BENCHMARK_SOLUTION_DISCOVERY_NODE_PROVISIONING_AND_REVIEW_GRADING.md), [Step 11.62](STEP_11_62_BENCHMARK_CUSTOM_IMAGE_ABI_RUSTUP_MOCHA_AND_REVIEW_GRADING.md), `StandardWorkflowDefinitions`, `BenchmarkDatasetModels`, `BenchmarkDatasetManifestLoader`, `BenchmarkDatasetLoader`, `BenchmarkDatasetJsonlWriter`, `ExternalBenchmarkDatasetImporterBase`, `BenchmarkTestCommandResolver`, `BenchmarkStagePolicyResolver`, `BenchmarkWorkspaceManager`, `BenchmarkVerifier`, `CrucibleBenchmarkRunner`  
> **Aligns With:** `AGENTS.md`, `docs/PLAN.md`

> [!IMPORTANT]
> **Specification Disclaimer:** This document is a behavioral specification. It defines required behaviors, architectural invariants, and verification criteria. Specific internal helper names and implementation details remain at the discretion of the implementer within these constraints.

---

## 1. Context & Motivation

Post-Step-11.62 `v1-smoke` runs (`v1-smoke-Qwen3.8-27B-latest-20261125-215112` scoring `19/28` and `v1-smoke-Flint-latest-20261125-215052` scoring `15/28` on `feat/bench @ 7b9ddd5`) eliminated all container runtime crashes and ABI/toolchain errors, exposing six remaining dataset, environment-pinning, verification-hygiene, and grading issues:

1. **Unpinned `Werkzeug >= 3.0` Breaks Legacy Flask 2.3 Verification (`pallets__flask-5014` â€” 2 false negatives):**
   - `WrapPythonContainerCommand` installs `requirements/tests.txt` (which does not pin Werkzeug) followed by `pip install -q -e .`. Flask 2.3's `pyproject.toml` declares `Werkzeug >= 2.2.2` with no upper bound, so `pip` pulls Werkzeug 3.x where `werkzeug.__version__` was removed, crashing `src/flask/testing.py:117` (`AttributeError: module 'werkzeug' has no attribute '__version__'`) across 48 tests even when both models produced the exact valid fix.
2. **Legacy 2014 `cacert.pem` & External `httpbin.org` Dependency (`psf__requests-1921`, `psf__requests-2931` â€” 2 false negatives):**
   - `psf__requests-1921` (2014) bundles a 2014 `requests/cacert.pem` that fails TLS verification (`SSLCertVerificationError`) against live `https://httpbin.org` in `test_mixed_case_scheme_acceptable` and `test_pyopenssl_redirect`, and fails `pip install -r requirements.txt` on `pexpect-u` (`use_2to3 is invalid`), despite both models passing `6/6` `fail_to_pass` and `105/107` `pass_to_pass`. Modern `python:3.11` containers also provide `/etc/ssl/certs/ca-certificates.crt` which should be exposed via `REQUESTS_CA_BUNDLE` when unset, while ancient 2014/2015 `httpbin`-coupled `psf/requests` tasks should be replaced with modern offline Python tasks.
3. **Crafted `ncf-cr-py-001` PR Diff Contains a Real Regression Bug (`v1-code-review.jsonl` â€” 2 false negatives):**
   - `ncf-cr-py-001` specifies `expected_verdict: "Approve"`, yet its `candidate_patch` calls `dict(...)` instead of `dict_class(...)` inside `merge_setting`, silently breaking `CaseInsensitiveDict` and `OrderedDict` semantics. Both models caught this defect, applied the `dict_class(...)` `MinorFix`, and passed `6/6` tests, scoring `0.00` due to expected-verdict mismatch.
4. **Pre-Existing Linux / .NET 10 Source Generator Snapshot Ordering Failures (`MessagePack-CSharp` `1798` / `2031` â€” 2 false negatives):**
   - `MessagePack.SourceGenerator.Tests` cases backed by `CSharpSourceGeneratorVerifier` (`allowReordering: false` against unsorted embedded resources) fail 61/84 tests on the unmodified `BaseCommit` under Linux / .NET 10, making `messagepack-csharp__messagepack-csharp-1798` impossible for even the gold patch to pass.
5. **Cargo 1.100 Build Directory Layout Breaks Ripgrep 13 Integration Test Harness (`burntsushi__ripgrep-2576`, `2209`, `ncf-cr-rust-001`) & Verification Artifact Leak:**
   - Modern Cargo (`1.100.0`) places integration test binaries in `target/debug/build/ripgrep/<hash>/out/integration-<hash>` rather than `target/debug/deps/`. Ripgrep 13.0.0's `tests/util.rs` resolves `env::current_exe().parent().join("../rg")`, causing 281/285 integration tests to panic with `NotFound`.
   - Furthermore, `BenchmarkWorkspaceManager.ResetToBaseAsync` used `git clean -fd`, preserving gitignored `target/`, which allowed an agent-created symlink inside `target/debug/build/` to leak into `BenchmarkVerifier` and mask the broken test harness.
6. **Underspecified Image-Only Problem Statement (`facebook__docusaurus-9897`) & Secondary Review/Diagnostic Gaps:**
   - `facebook__docusaurus-9897` stores its entire repro snippet inside a GitHub screenshot image URL (`![image](...)`) rather than text, leaving text-only agents unable to identify `unwrapMdxCodeBlocks`.
   - `ncf-tr-rust-002` (`WaitingForHuman` on Flint) matched `DelayQueue` and 3/4 keywords in its `ask` questions, but failed `Missing expected finding anchors: tokio-util/src/time/delay_queue.rs` because `EvaluateExpectedFindings` did not run identifier-bounded file-stem corroboration without `symbol_or_line_hint` and did not match separator-less PascalCase stems (`delay_queue` â†” `DelayQueue`).
   - Exhausting `MaxTurns` (`100` turns) with no patch surfaced only `"Model produced an empty patch."` without indicating that the turn budget was exhausted.

---

## 2. Required Behaviors & Constraints

### 2.1 Manifest-Level Python Dependency Pinning (`pip_constraints`) & System CA Bundle
- Support an optional `pip_constraints` string-array field across `BenchmarkDatasetConstants`, `BenchmarkManifestEntry`, `BenchmarkInstance`, `BenchmarkDatasetManifestLoader`, `BenchmarkDatasetLoader` (including manifest overlay & diff diagnostics), `BenchmarkDatasetJsonlWriter`, and `ExternalBenchmarkDatasetImporterBase`.
- `BenchmarkTestCommandResolver.WrapWithContainerDependencySetup` MUST accept optional `pipConstraints` (preserving existing 3-argument callers via overload/default) and pass them into `WrapPythonContainerCommand`:
  - When `REQUESTS_CA_BUNDLE` is unset and `/etc/ssl/certs/ca-certificates.crt` exists in the container, export `REQUESTS_CA_BUNDLE=/etc/ssl/certs/ca-certificates.crt`.
  - When `pipConstraints` is non-empty, materialize `/tmp/ncf-pip-constraints.txt`, export `PIP_CONSTRAINT=/tmp/ncf-pip-constraints.txt`, pass `-c /tmp/ncf-pip-constraints.txt` to venv `pip install` calls, and ensure stale `/tmp/ncf-bench-venv` state without matching constraints is invalidated cleanly.
- `BenchmarkVerifier` MUST supply `instance.PipConstraints` during verification, and `BenchmarkStagePolicyResolver.GetSandboxEnvironmentGuidance` MUST surface active `pip_constraints` in the agent sandbox bootstrap guidance so agent test runs match verification.

### 2.2 Verification Workspace Build-Artifact Hygiene
- `BenchmarkWorkspaceManager.ResetToBaseAsync` MUST purge gitignored compiled build artifacts (`target/`, `bin/`, `obj/`, stale caches, symlinks) via `git clean -fdx -e node_modules -e .venv` while preserving `node_modules` and `.venv` so Node/Python dependency provisioning remains cached without allowing agent-mutated `target/` artifacts to leak into `BenchmarkVerifier`.

### 2.3 `TaskReview` `WaitingForHuman` Anchor Grounding & Prompt Guidance
- `BenchmarkVerifier.MatchesIdentifierBoundedFileStem` MUST preserve all existing specificity/length/boundary guards (`MinimumFileStemCorroborationLength = 5`, `GenericFileStems`, identifier boundaries rejecting `RetryHelperTests` or substring collisions like `"catalog"`/`"https"`), while also matching separator-stripped forms when a file stem contains `_` or `-` (e.g., `delay_queue` matching identifier-bounded `DelayQueue`, while rejecting `DelayQueueTests`).
- `BenchmarkVerifier.EvaluateExpectedFindings` MUST compute `hasStrongKeywordCoverage` whenever `hasFile && hasKeywords` (with `>= 2` independent keywords and majority coverage) and allow identifier-bounded `fileStemMatched` or `fileMatched` corroboration even when `symbol_or_line_hint` is omitted (such as `TaskReview` findings), while continuing to reject ungrounded keywords that lack any file/stem/symbol match.
- `StandardWorkflowDefinitions`'s `TaskReview` `StageDirective` MUST instruct agents to cite affected file path(s) and key symbol(s) inside `ask` prompts/rationales when requesting clarification.

### 2.4 Distinct `MaxTurns` Exhaustion Diagnostics
- When an agent finishes `CrucibleBenchmarkRunner` with `AgentRunStatus.Completed`, fails verification, and reached `turns >= candidate.MaxTurns`, `BenchmarkInstanceResult.ErrorMessage` MUST explicitly surface that the `MaxTurns` budget was exhausted (`Agent reached MaxTurns limit ({turns}/{candidate.MaxTurns}) without completing the task.`) alongside any verification error rather than reporting only `"Model produced an empty patch."`.

### 2.5 Modern-Era Dataset Curation & Live Gold Validation (`v1-smoke`, `v1-full`, `v1-code-review.jsonl`)
- **`v1-smoke.manifest.json` & `v1-full.manifest.json`** MUST maintain tier sizes (`28` and `90`), superset parity (`v1-smoke` âŠ‚ `v1-full` with identical fields), and stack-mix invariants while replacing problematic historical/broken instances:
  - Replace screenshot-only `facebook__docusaurus-9897` with `facebook__docusaurus-10130` in `v1-smoke` and add `facebook__docusaurus-10309` in `v1-full` (using `yarn build:packages` + `--` delimiter for scoped Jest test execution).
  - Replace Cargo-1.100-incompatible `burntsushi__ripgrep-2576` and `burntsushi__ripgrep-2209` (plus `sharkdp__bat-2650`/`2201` in `v1-full`) with modern Rust instances (`tokio-rs__tokio-6752` in `v1-smoke`; `tokio-rs__tokio-6838`, `tokio-rs__tokio-7139`, `tokio-rs__tokio-6724`, `tokio-rs__tokio-6603` in `v1-full`, using `--manifest-path tokio/Cargo.toml` to disambiguate workspace crate versions).
  - Replace Linux/.NET 10 source-generator/snapshot-broken `messagepack-csharp__messagepack-csharp-1798`/`2031`/`1962`/`1842` with clean C# instances passing on .NET 10 (`messagepack-csharp__messagepack-csharp-2045` in `v1-smoke`/`v1-full`; `serilog__serilog-1955` and `polly-2068` alongside `messagepack-csharp__messagepack-csharp-2022` in `v1-full`).
  - Replace network/legacy-CA-dependent or legacy Python/slow Monorepo JS instances (`psf__requests-1921`/`2931`/`6028`, `pytest-dev__pytest-7324`, `pylint-dev__pylint-7080`/`4970`; `axios__axios-4731`, `babel__babel-15445`/`13928`) with modern offline verified instances (`pytest-dev__pytest-10356` in `v1-smoke`; `pylint-dev__pylint-8898`, `pytest-dev__pytest-10081`, `sympy__sympy-24661`/`24539`/`24443`/`24213` in `v1-full`; `axios__axios-5085`, `immutable-js__immutable-js-2005`, `vuejs__core-11899` in `v1-full`), assign `custom_container_image: "python:3.11"` across Python entries, and pin `pip_constraints` (`Werkzeug<3.0` on `pallets__flask-5014` & `ncf-cr-py-002`; `MarkupSafe<2.1` + `Werkzeug<2.1` on `psf__requests-5414` & `ncf-cr-py-001`).
- **Additional Container/Parser Hygiene Discovered During Validation:**
  - `WrapPythonContainerCommand` injects a fallback `SETUPTOOLS_SCM_PRETEND_VERSION=9.9.9` guard when `setuptools-scm` metadata exists in a tagless shallow clone so pytest `minversion` checks pass cleanly.
  - `WrapNodeContainerCommand` unconditionally guards both `npm_config_cache` (`/tmp/ncf-npm-cache`) and `npm_config_store_dir` (`/tmp/ncf-pnpm-store`) before checking `node_modules` readiness (fixing UID-1000 `EACCES` on root-owned `/cache/pnpm`), and ensures standalone `yarn`/`pnpm` availability even when `node_modules` is preserved.
  - `BenchmarkWorkspaceManager.DeleteDirectoryRobustly` iteratively unlinks reparse points/junctions non-recursively and deletes directories bottom-up so cyclic `pnpm` workspace junctions never stall cleanup.
  - `TestAssertionEvaluator.TryEvaluateExpectedIdentifier` safely matches upstream SWE-bench_Verified truncated pytest parameter IDs containing unbalanced `[` prefixes against passing parameterized outcomes.
- **`v1-code-review.jsonl` (and manifests):**
  - Rebase `ncf-cr-py-001` (`Approve`) onto an authentic, clean, offline modern Python PR (`psf__requests-6028`, `python:3.11`, `pip_constraints: ["MarkupSafe<2.1", "Werkzeug<2.1"]`) with zero defects in `candidate_patch`.
  - Pin `"pip_constraints": ["Werkzeug<3.0"]` on `ncf-cr-py-002` (`pallets/flask`, `python:3.11`).
  - Update `ncf-cr-rust-001` (`MinorFix`, `burntsushi/ripgrep`) to verify via an in-crate unit test inside `crates/regex/src/word.rs` (`cargo test -p grep-regex --no-fail-fast`) instead of spawning `tests/util.rs`'s `../rg` binary.
- Re-materialize local `~/.nightcodeforge/benchmarks/datasets/v1-smoke.jsonl` and `v1-full.jsonl`, and empirically validate every smoke instance and every updated/unvalidated full instance via live containerized `ncf-bench dataset validate`.

---

## 3. Definition of Done (DoD) Checklist

- [x] Add `pip_constraints` schema/manifest/overlay/importer/writer support, wire `REQUESTS_CA_BUNDLE` + `/tmp/ncf-pip-constraints.txt` / `PIP_CONSTRAINT` into `BenchmarkTestCommandResolver` and `BenchmarkVerifier`, and surface active `pip_constraints` in `BenchmarkStagePolicyResolver.GetSandboxEnvironmentGuidance`, with unit and live acceptance tests.
- [x] Update `BenchmarkWorkspaceManager.ResetToBaseAsync` to use `git clean -fdx -e node_modules -e .venv` (and harden `DeleteDirectoryRobustly` against cyclic junctions), with unit tests verifying `target/` cleanup, cyclic reparse-point cleanup, and `node_modules`/`.venv` preservation.
- [x] Update `BenchmarkVerifier.MatchesIdentifierBoundedFileStem` and `EvaluateExpectedFindings` for separator-normalized stem matching (`delay_queue` â†” `DelayQueue`) and strong-keyword corroboration on `TaskReview` findings, and instruct `TaskReview` `StageDirective` to cite file paths/symbols in `ask`, with regression tests.
- [x] Enrich `CrucibleBenchmarkRunner` `ErrorMessage` when `turns >= candidate.MaxTurns` on failed `Completed` runs, with unit tests.
- [x] Curate `v1-smoke.manifest.json`, `v1-full.manifest.json`, and `v1-code-review.jsonl` to replace/adapt broken historical instances, harden Python/Node container wrappers and truncated-bracket pytest matching (`TestAssertionEvaluator`), re-materialize local `v1-smoke.jsonl` and `v1-full.jsonl`, and verify live `ncf-bench dataset validate` passes across `v1-smoke` and `v1-full`.
- [x] Verify all non-E2E/non-Acceptance tests (`dotnet test --filter "Category!=Acceptance&Category!=E2E"`), Acceptance tests (`dotnet test --filter "Category=Acceptance"`), and the full test suite (`dotnet test`) pass.
