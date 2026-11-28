# 🏁 Step 12.23B: Standalone Benchmark CLI (`NightCodeForge.Benchmark.Cli`)

> **Feature Implementation Step (Part 2 of 3 — Dedicated Standalone CLI Commands & Cross-Version Comparative Reporting)**  
> *Parent Plan:* [Phase 12 (Archive): Completed Features & Integrations](../12_EXTEND_FEATURES.md)  
> *Prerequisite Step:* [Step 12.23A: Standalone Crucible Benchmark Engine, Mock DB & Verifier](STEP_12_23A_CRUCIBLE_BENCHMARK_ENGINE_AND_CONTAINER_VERIFIER.md)  
> *Companion Step:* [Step 12.23C: Multi-Stack Benchmark Datasets, Importers & Crafted Scenarios](STEP_12_23C_BENCHMARK_DATASETS_IMPORTERS_AND_SCENARIOS.md)  
> *Source Policy:* [AGENTS.md](../../../AGENTS.md)  
> *Project Target:* `src/NightCodeForge.Benchmark.Cli`, `src/NightCodeForge.Infrastructure`, `tests/NightCodeForge.Benchmark.Cli.Tests` | *Status:* Completed | *Target Framework:* .NET 10 (C# 13)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Objective

With the core Crucible Benchmark Engine (`ICrucibleBenchmarkRunner`), mock/in-memory DB layer, and verifiers in place ([Step 12.23A](STEP_12_23A_CRUCIBLE_BENCHMARK_ENGINE_AND_CONTAINER_VERIFIER.md)), this step (Part 2 of Step 12.23) exposes benchmark execution and comparative reporting in a **dedicated standalone CLI** (`NightCodeForge.Benchmark.Cli`, command `ncf-bench`), keeping the main `NightCodeForge.Cli` (`ncf`) lightweight and dependency-free (strictly CLI-only, with no Benchmark Web UI or Hangfire dependency):
1. **Dedicated `benchmark run` CLI:**
   - Evaluate **Implementation**, **Code Review**, and **ForgeTask Review** suites across a set of **local/configured models** using `NightCodeForge.Agents` (`CrucibleAgentHarness`) and mock/in-memory repositories (zero dependency on `NightCodeForge.Web`, Hangfire, or SQLite).
   - Record `BenchmarkRunProvenance` (NightCodeForge version, git commit, git branch, provider, active skills/flags), stream live terminal progress, and export `results.json` (`BenchmarkRunReport`), `summary.md`, `transcripts/`, and `predictions.jsonl`.
2. **Cross-Version `benchmark report` CLI:**
   - Render a formatted comparative leaderboard table across evaluated **models** (overall and grouped by `Language`: C#, TypeScript/JavaScript, Python, Rust) within a run or across **multiple result directories** (different NightCodeForge versions, commits, or branches) to select the best models for local inference and catch harness/tooling regressions against a `--baseline` run (`Pass@1 / Accuracy %`, `Verdict / Patch %`, `Judge Score`, `Avg Duration`, `Avg Turns`, `Avg Tokens`, `Tokens/s`, `Edit Failure %`, `csnav Calls`, `codecondenser Calls`, `Dispatcher Repairs`).

---

## 2. Technical Specifications

### 2.1 Dedicated Project & DB-Free Infrastructure DI
- Create a dedicated executable project `src/NightCodeForge.Benchmark.Cli` (`NightCodeForge.Benchmark.Cli.csproj`, `ToolCommandName` = `ncf-bench`) referencing `NightCodeForge.Core`, `NightCodeForge.Agents`, and `NightCodeForge.Infrastructure`. Do **not** add `Agents`, `Infrastructure`, EF Core, or LLM dependencies to `src/NightCodeForge.Cli`.
- Extract a DB-free `AddNightCodeForgeExecutionInfrastructure()` registration in `NightCodeForge.Infrastructure` providing Git, Docker container executor (`NightCodeForgeContainerExecutor` / `IContainerEnvironmentFactory`), process runner, host SDK discovery, and workspace path resolution **without** EF Core/SQLite (`AppDbContext`), SignalR, or Hangfire.
- Register in-memory/mock stubs in `NightCodeForge.Benchmark.Cli` for `ITaskRepository`, `IWorkspaceRepository`, `IWorkflowRepository`, `ITaskCancellationManager`, and `IOperationsControlService`, alongside `AddNightCodeForgeAgents(providerType)`.

### 2.2 Standalone CLI Commands
- `ncf-bench run --dataset <path.jsonl|json> (--models <m1,m2> | --config <candidates.json>) [--provider <ollama|ollama-router|openai>] [--endpoint <url>] [--category <implementation|code-review|task-review>] [--filter <id1,id2>] [--limit <N>] [--verify|--no-verify] [--skills|--no-skills] [--timeout <seconds>] [--concurrency <N>] [--resume <resultsDir>] [--judge-model <model>] [--judge-provider <provider>] [--judge-endpoint <url>] [--out <dir>]`
  - **Model & Provider Matrix:** Supports quick `--models <m1,m2>` sweeps (using `--provider`, `--endpoint`, and API keys via `NCF_BENCHMARK_API_KEY` / `OPENAI_API_KEY` environment variables) or a `--config <candidates.json>` manifest specifying per-model `ThinkingEffort`, `ThinkingTokenBudget`, `ContextWindowTokens`, `MaxOutputTokens`, `Temperature`, `MaxTurns`, `OptimizationFlags`, and `RoutingTarget`.
  - **Sequential-by-Default & Resumable:** Defaults `--concurrency` to `1` so local GPU VRAM contention does not distort latency and `Tokens/s` measurements; `--resume <resultsDir>` skips `(candidate, instance)` pairs already completed in `results.json`.
  - **Artifacts Written:** Captures `BenchmarkRunProvenance` (detected NCF version, `git rev-parse HEAD`, current branch), streams live progress per instance/model, and writes `<out>/results.json`, `<out>/summary.md`, `<out>/transcripts/`, and `<out>/<candidate>/predictions.jsonl`.
- `ncf-bench report <resultsDir1> [<resultsDir2> ...] [--baseline <resultsDir>] [--format markdown|ascii|json]`
  - **Single-run mode:** Prints a comparative leaderboard across all models evaluated in `<resultsDir1>`, including a per-`Language` (`csharp`, `typescript`, `javascript`, `python`, `rust`) breakdown table.
  - **Multi-run / regression mode:** Compares models across multiple result directories tagged by each run's `BenchmarkRunProvenance` (`GitBranch @ GitCommit` / `NcfVersion`), highlighting metric deltas against `--baseline` (defaulting to the first directory when multiple are supplied) to detect regressions across NightCodeForge commits.
- *(Dataset import and pre-flight validation commands `ncf-bench dataset import` and `ncf-bench dataset validate` are specified in [Step 12.23C](STEP_12_23C_BENCHMARK_DATASETS_IMPORTERS_AND_SCENARIOS.md).)*

---

## 3. Definition of Done (DoD) Checklist

- [x] Create dedicated `src/NightCodeForge.Benchmark.Cli` (`ncf-bench`) and `tests/NightCodeForge.Benchmark.Cli.Tests` projects without bloating `src/NightCodeForge.Cli`.
- [x] Extract DB-free `AddNightCodeForgeExecutionInfrastructure()` in `NightCodeForge.Infrastructure` and wire `NightCodeForge.Benchmark.Cli` DI with `NightCodeForge.Agents` and mock/in-memory DB stubs (no SQLite, Hangfire, SignalR, or `NightCodeForge.Web` dependency).
- [x] Implement `run` and `report` commands supporting `Implementation`, `CodeReview`, and `TaskReview` datasets, `--models` / `--config` + `--provider` / `--endpoint` configuration, `--resume`, optional `--judge-*` flags, `BenchmarkRunProvenance` capture, and single-run + cross-commit baseline regression reporting.
- [x] Add unit tests verifying CLI argument parsing, candidate config loading, DB-free DI resolution, resume filtering, and comparative + regression report formatting.


