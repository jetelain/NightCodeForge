# 🏁 Step 12.23B: `ncf benchmark` CLI & `/benchmarks` Leaderboard Web UI

> **Feature Implementation Step (Part 2 of 2 — Strictly Standalone CLI Commands & Comparative Reporting)**  
> *Parent Plan:* [Features & Integrations Roadmap](README.md)  
> *Prerequisite Step:* [Step 12.23A: Standalone Crucible Benchmark Engine, Mock DB & Verifier](STEP_12_23A_CRUCIBLE_BENCHMARK_ENGINE_AND_CONTAINER_VERIFIER.md)  
> *Source Policy:* [AGENTS.md](../../AGENTS.md)  
> *Project Target:* `src/NightCodeForge.Cli` (or standalone `src/NightCodeForge.Benchmark.Cli`), `tests/NightCodeForge.Cli.Tests` | *Status:* Planned | *Target Framework:* .NET 10 (C# 13)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Objective

With the core Crucible Benchmark Engine (`ICrucibleBenchmarkRunner`), mock/in-memory DB layer, and verifiers in place ([Step 12.23A](STEP_12_23A_CRUCIBLE_BENCHMARK_ENGINE_AND_CONTAINER_VERIFIER.md)), this step (Part 2 of Step 12.23) exposes benchmark execution and comparative reporting as a **strictly standalone CLI** (with Web UI `/benchmarks` and Hangfire queue integration deferred until after `v0.1.0`):
1. **Standalone `benchmark run` CLI:**
   - Execute **Implementation**, **Code Review**, and **Task Review** suites across a matrix of **models** and **tooling versions / configurations** using `NightCodeForge.Agents` and mock/in-memory repositories (zero dependency on `NightCodeForge.Web`, Hangfire, or SQLite).
   - Stream live terminal progress and automatically export `summary.md`, `results.json`, `transcripts/`, and `predictions.jsonl`.
2. **Standalone `benchmark report` CLI:**
   - Render a formatted comparative leaderboard table comparing models and tooling versions (`Pass@1 / Accuracy %`, `Patch Generation %`, `Avg Duration`, `Avg Turns`, `Avg Tokens`, `Tokens/s`, `Edit Failure %`, `csnav Calls`, `Dispatcher Repairs`).

---

## 2. Technical Specifications

### 2.1 Standalone CLI Commands
Host in a standalone CLI entry point (referencing `NightCodeForge.Core`, `NightCodeForge.Agents`, and `NightCodeForge.Infrastructure` for Git/Docker execution, with in-memory/mock DB registrations):
- `ncf benchmark run --dataset <path.jsonl> --models <model1,model2> [--category implementation|code-review|task-review] [--tooling-versions <tag1,tag2>] [--filter <id1,id2>] [--limit <N>] [--verify|--no-verify] [--skills|--no-skills] [--out <dir>]`
  - Streams live progress per instance, model, and tooling version to the terminal.
  - Writes `<out>/summary.md`, `<out>/results.json`, `<out>/transcripts/`, and `<out>/<candidate>/predictions.jsonl`.
- `ncf benchmark report <resultsDir>`
  - Prints a formatted Markdown/ASCII comparative leaderboard table comparing all evaluated models and tooling versions.

---

## 3. Definition of Done (DoD) Checklist

- [ ] Wire standalone CLI DI container with `NightCodeForge.Agents`, container/git execution services, and mock/in-memory DB stubs (no SQLite, Hangfire, or `NightCodeForge.Web` dependency).
- [ ] Implement `benchmark run` and `benchmark report` CLI commands supporting `Implementation`, `CodeReview`, and `TaskReview` categories and model $\times$ tooling-version matrix sweeps.
- [ ] Add unit tests verifying CLI argument parsing, mock DB wiring, matrix expansion, and comparative report formatting.

