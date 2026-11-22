# 🏁 Step 12.23C: Multi-Stack Benchmark Datasets, Importers & Crafted Scenarios

> **Feature Implementation Step (Part 3 of 3 — Dataset Curation, External Importers, Crafted Review Scenarios & Pre-Flight Validation)**  
> *Parent Plan:* [Features & Integrations Roadmap](README.md)  
> *Prerequisite Steps:* [Step 12.23A: Standalone Crucible Benchmark Engine, Mock DB & Verifier](STEP_12_23A_CRUCIBLE_BENCHMARK_ENGINE_AND_CONTAINER_VERIFIER.md) | [Step 12.23B: Standalone Benchmark CLI (`NightCodeForge.Benchmark.Cli`)](STEP_12_23B_BENCHMARK_CLI_AND_LEADERBOARD_WEB_UI.md)  
> *Source Policy:* [AGENTS.md](../../AGENTS.md)  
> *Project Target:* `benchmarks/datasets/`, `src/NightCodeForge.Benchmark.Cli`, `tests/NightCodeForge.Benchmark.Cli.Tests` | *Status:* Planned | *Target Framework:* .NET 10 (C# 13)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Objective

Provide curated, reproducible benchmark datasets tailored to NightCodeForge's target technology stacks—**C# first**, alongside **TypeScript/JavaScript**, **Python**, and **Rust**—across all three evaluated problem categories (**Implementation**, **Code Review**, and **ForgeTask Review**):
1. **Tiered Suite Strategy:** Define a fast **`v1-smoke`** regression suite (~24–30 instances, ~30–60 min/model) for catching regressions across NightCodeForge commits/branches, and a broader **`v1-full`** suite (~80–100 instances, overnight) for statistically sound local model selection.
2. **External Implementation Dataset Importers (`IBenchmarkDatasetImporter`):** Normalize pinned subsets from `microsoft/SWE-Sharp-Bench` (C#), `SWE-bench Multilingual` / `Multi-SWE-bench` (TypeScript, JavaScript, Rust), and `SWE-bench Verified` (Python) into the unified `BenchmarkInstance` JSONL schema without committing large third-party patch dumps into git.
3. **Crafted NightCodeForge-Native Review Scenarios:** Supply authored **Code Review** (`Approve` / `MinorFix` / `RequestChanges`) and **ForgeTask Review** (`Ready` / `Ready + Difficulty Escalation` / `NeedsClarification`) scenarios exercising NightCodeForge's exact stage contracts, tool rules, and regression-test policy.
4. **Pre-Flight Dataset Validation (`ncf-bench dataset validate`):** Verify inside `NightCodeForgeContainerExecutor` that every `Implementation` gold patch passes `FailToPass`/`PassToPass`, every `CodeReview` candidate patch applies cleanly, and every `TaskReview` fixture has valid repository/frontmatter setup before burning GPU time on model sweeps.

---

## 2. Technical Specifications

### 2.1 Tiered Suite Composition & Stack Mix
| Suite Tier | Total Size | Intended Cadence | Stack Distribution Target |
| :--- | :--- | :--- | :--- |
| **`v1-smoke`** | ~24–30 instances | PR / branch / release regression gate | ~50–55% `csharp`, ~20% `typescript`/`javascript`, ~15% `python`, ~10–15% `rust` |
| **`v1-full`** | ~80–100 instances | Overnight local LLM selection sweeps | ~50–55% `csharp`, ~20% `typescript`/`javascript`, ~15% `python`, ~10–15% `rust` |

Each tier spans all three `BenchmarkProblemCategory` types so regressions or model weaknesses in planning (`TaskReview`), coding (`Implementation`), or review gating (`CodeReview`) are measured independently and in aggregate.

### 2.2 External Implementation Importers (`IBenchmarkDatasetImporter`)
Implement an extensible importer pipeline normalizing external `.json`/`.jsonl` exports into NightCodeForge's `BenchmarkInstance` JSONL format using versioned selection manifests:
- **C# / .NET (`swe-sharp-bench`):** Import from `microsoft/SWE-Sharp-Bench` exports. Selection manifests curate Linux-container-compatible, fast-building OSS repositories (excluding Windows-only or heavy external-service dependencies) with `Language = "csharp"` and `TestResultFormat = Trx`.
- **TypeScript / JavaScript & Rust (`swebench-multilingual`, `multi-swe-bench`):** Import TypeScript, JavaScript, and Rust instances from `SWE-bench Multilingual` or `Multi-SWE-bench` exports. Selection manifests exclude slow headless-browser suites from `v1-smoke`, attach stack-specific `CustomTestCommand` (e.g., `vitest` / `jest` with JUnit XML reporter, or `cargo test`), and set `Language` (`typescript`, `javascript`, `rust`) and `TestResultFormat` (`JUnitXml`, `CargoTest`, or `ExitCode`).
- **Python (`swebench-verified`):** Import human-verified Python instances with `Language = "python"`, `TestResultFormat = JUnitXml`, and `pytest --junitxml=...` test commands.
- **Fail-Fast Validation:** Importers fail fast if a manifest references missing `instance_id`s, empty `base_commit`, or unsupported language/test format metadata.

### 2.3 Crafted Code Review Scenarios (15–24 Instances)
Derive instances anchored on the same cached OSS repositories (C# primary, plus TypeScript/JavaScript, Python, and Rust) to evaluate Tier-1 surgical fixes and Tier-2 rejections:
- **~25% `Approve`:** Verified `GoldPatch`es from the Implementation suite. Measures false-positive rejection rates and unnecessary churn.
- **~35% `MinorFix` (Tier 1):** Verified `GoldPatch`es modified with a single localized flaw (e.g., missing null/boundary guard, minor edge-case defect, or `.editorconfig`/lint violation). Expects surgical `edit` + `bash` test verification without invoking `request_changes`.
- **~40% `RequestChanges` (Tier 2):** Plausible-looking flawed `CandidatePatch`es (missing core acceptance criterion, swallowed exception, concurrency/security flaw, or real defective model patches). Expects `request_changes` invocation with grounded `ExpectedFindings` anchors and enforcement of the mandatory regression-test feedback policy.

### 2.4 Crafted ForgeTask Review Scenarios (12–18 Instances)
Craft NCF-native specification scenarios anchored on the cached OSS repositories (C#-heavy for `csnav` validation, plus TypeScript/JavaScript, Python, and Rust):
- **~35% `Ready` (Standard):** Clear requirement where the agent must locate real symbols/files, enrich `docs/tasks/<instance_id>.md` with a grounded implementation plan and testable acceptance criteria, preserve YAML frontmatter `id`/`status`, and leave production code untouched.
- **~25% `Ready` (Difficulty Escalation):** Deceptively short task description whose true codebase impact spans multiple modules or subtle concurrency invariants; expects `difficulty:` in YAML frontmatter to be escalated to `difficult` or `expert`.
- **~40% `NeedsClarification`:** Intentionally ambiguous or contradictory specification (e.g., conflicting API contract, unspecified breaking-change or security policy); expects invocation of the `ask` tool with codebase-grounded questions rather than guessing or emitting plain-text questions.

### 2.5 Repository Layout & `ncf-bench dataset` CLI Commands
- **Repository Storage (`benchmarks/datasets/`):**
  - Store **pinned selection manifests** (`v1-smoke.manifest.json`, `v1-full.manifest.json`, containing selected `instance_id`s, `Language`, `CustomContainerImage`, `CustomTestCommand`, and `TestResultFormat` overrides) and **crafted Code Review / ForgeTask Review JSONL files** in git under `benchmarks/datasets/`.
  - Materialize imported external Implementation JSONL files locally under `~/.nightcodeforge/benchmarks/datasets/` (outside git) so third-party patches and test suites are not duplicated in the NightCodeForge repository.
- **CLI Dataset Subcommands:**
  - `ncf-bench dataset import --source <swe-sharp-bench|swebench-multilingual|multi-swe-bench|swebench-verified> --input <path> --manifest <manifest.json> --out <path.jsonl>`
    - Normalizes selected external instances into `BenchmarkInstance` JSONL with pinned stack metadata.
  - `ncf-bench dataset validate --dataset <path.jsonl> [--category <implementation|code-review|task-review>] [--filter <id1,id2>] [--limit <N>]`
    - Provisions leak-free ephemeral workspaces via `IBenchmarkWorkspaceManager` and executes deterministic pre-flight verification inside `NightCodeForgeContainerExecutor`:
      - **`Implementation`:** Verifies `FailToPass` fails on `BaseCommit` (+ `TestPatch`), passes after applying `GoldPatch` + `TestPatch`, and `PassToPass` passes.
      - **`CodeReview`:** Verifies `CandidatePatch` applies cleanly to `BaseCommit` and produces a valid diff (plus optional test check for `Approve` / `MinorFix` fixtures).
      - **`TaskReview`:** Verifies `BaseCommit` checkout and valid `docs/tasks/<instance_id>.md` frontmatter and structure.
    - Reports per-instance pass/fail diagnostics and exits non-zero if any validated instance fails pre-flight verification.

---

## 3. Definition of Done (DoD) Checklist

- [ ] Define pinned selection manifests for `v1-smoke` (~24–30 instances) and `v1-full` (~80–100 instances) in `benchmarks/datasets/` following the C#-first + TS/JS + Python + Rust target distribution.
- [ ] Implement `IBenchmarkDatasetImporter` with normalized adapters for `swe-sharp-bench`, `swebench-multilingual` / `multi-swe-bench`, and `swebench-verified`, and expose `ncf-bench dataset import`.
- [ ] Author initial crafted `CodeReview` (15–24 instances across `Approve`, `MinorFix`, `RequestChanges`) and `TaskReview` (12–18 instances across `Ready`, `Ready` difficulty escalation, `NeedsClarification`) scenario datasets under `benchmarks/datasets/`.
- [ ] Implement pre-flight gold-patch & fixture validation exposed via `ncf-bench dataset validate`.
- [ ] Add unit tests in `tests/NightCodeForge.Benchmark.Cli.Tests` covering importer normalization, manifest filtering/fail-fast errors, crafted scenario schema validation, and `ncf-bench dataset` CLI argument parsing and pre-flight execution.
