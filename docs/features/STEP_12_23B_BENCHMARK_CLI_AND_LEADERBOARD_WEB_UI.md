# 🏁 Step 12.23B: `ncf benchmark` CLI & `/benchmarks` Leaderboard Web UI

> **Feature Implementation Step (Part 2 of 2 — CLI Commands, Leaderboard Dashboard & Patch Diff Inspector)**  
> *Parent Plan:* [Features & Integrations Roadmap](README.md)  
> *Prerequisite Step:* [Step 12.23A: Crucible Benchmark Engine, Leak-Free Workspaces & Container Verifier](STEP_12_23A_CRUCIBLE_BENCHMARK_ENGINE_AND_CONTAINER_VERIFIER.md)  
> *Source Policy:* [AGENTS.md](../../AGENTS.md)  
> *Project Target:* `src/NightCodeForge.Cli`, `src/NightCodeForge.Web`, `tests/NightCodeForge.Web.Tests` | *Status:* Planned | *Target Framework:* .NET 10 (C# 13)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Objective

With the core Crucible Benchmark Engine (`ICrucibleBenchmarkRunner`) and containerized verifier in place ([Step 12.23A](STEP_12_23A_CRUCIBLE_BENCHMARK_ENGINE_AND_CONTAINER_VERIFIER.md)), this step (Part 2 of Step 12.23) exposes benchmark execution, reporting, and visual inspection across both the **`ncf` CLI** and the **NightCodeForge Web UI (`/benchmarks`)**:
1. **`ncf benchmark` CLI Commands (`src/NightCodeForge.Cli`):**
   - `ncf benchmark run` to execute SWE-Sharp-Bench / custom JSONL suites across a matrix of models with live terminal progress and automatic export of `summary.md`, `results.json`, `transcripts/`, and `predictions.jsonl`.
   - `ncf benchmark report <resultsDir>` to render a formatted comparative leaderboard table.
2. **Web UI Model Benchmarks Page (`/benchmarks` — `Benchmarks.razor` & `IBenchmarkDashboardService`):**
   - Launch new benchmark runs on the single-worker `llm-inference` queue so benchmark evaluations never collide with active task executions for GPU VRAM.
   - Display a side-by-side **Model Leaderboard** (`Pass@1 %`, `Patch Generation %`, `Avg Duration`, `Avg Turns`, `Avg Tokens`, `Tokens/s`, `Edit Failure %`, `csnav Calls`, `Dispatcher Repairs`).
   - Provide an **Instance $\times$ Model Matrix** with a drill-down drawer comparing **Model Patch vs. Gold Patch**, verification test logs, and the full Crucible transcript.

---

## 2. Technical Specifications

### 2.1 CLI Commands (`src/NightCodeForge.Cli`)
Update `NightCodeForge.Cli.csproj` to reference `NightCodeForge.Agents` and `NightCodeForge.Infrastructure` and add:
- `ncf benchmark run --dataset <path.jsonl> --models <model1,model2> [--filter <id1,id2>] [--limit <N>] [--verify|--no-verify] [--skills|--no-skills] [--out <dir>]`
  - Streams live progress per instance and model to the terminal.
  - Writes `<out>/summary.md`, `<out>/results.json`, `<out>/transcripts/`, and `<out>/<candidate>/predictions.jsonl`.
- `ncf benchmark report <resultsDir>`
  - Prints a formatted Markdown/ASCII comparative leaderboard table comparing all evaluated models.

### 2.2 Web UI Page (`/benchmarks` — `Benchmarks.razor`)
In accordance with [AGENTS.md](../../AGENTS.md), all run orchestration, result file loading, and matrix formatting reside in `IBenchmarkDashboardService` in `NightCodeForge.Web/Services`:
1. **New Benchmark Run Launcher:**
   - Select dataset file path (`.jsonl` / `.json`) or pick a subset of `instance_id`s.
   - Multi-select candidate models from the discovered LLM catalog (`ILlmModelCatalogService`), with per-candidate toggles for `ThinkingEffort`, `EnableStackSkills` (`csharp-nav` / `csnav` & `codecondenser`), and `LocalOptimizationFlags`.
   - Enqueues the benchmark job on the `llm-inference` queue.
2. **Model Leaderboard & Comparison Matrix:**
   - Side-by-side comparison table across models showing **Pass@1 (`% Resolved`)**, **Patch Generation %**, **Avg Duration**, **Avg Turns**, **Avg Tokens**, **Tokens/s**, **Edit Failure %**, **`csnav` Calls**, and **Dispatcher Repairs**.
3. **Per-Instance Drill-Down & Diff Comparator:**
   - Matrix grid (`Instances` $\times$ `Models`) with outcome badges (`✅ Resolved`, `❌ Tests Failed`, `⚠️ Build Failed`, `⭕ Empty Patch`, `⏱️ Timeout`).
   - Clicking any cell opens a drawer showing:
     - **Model Patch vs. Gold Patch** side-by-side diff (`DiffViewer.razor`).
     - **Verification Test Output** (`FailToPass` / `PassToPass` results).
     - **Full Crucible Reasoning & Tool Trace** (`TranscriptViewer`).

---

## 3. Definition of Done (DoD) Checklist

- [ ] Update `NightCodeForge.Cli.csproj` and implement `ncf benchmark run` and `ncf benchmark report` in [`Program.cs`](../../src/NightCodeForge.Cli/Program.cs).
- [ ] Implement `IBenchmarkDashboardService` in `NightCodeForge.Web/Services`.
- [ ] Create `src/NightCodeForge.Web/Components/Pages/Benchmarks.razor` (`@page "/benchmarks"`) and add `🏁 Benchmarks` to [`NavMenu.razor`](../../src/NightCodeForge.Web/Components/Layout/NavMenu.razor).
- [ ] Add unit tests for `ncf benchmark` CLI argument parsing/report formatting and bUnit tests in `NightCodeForge.Web.Tests` verifying `Benchmarks.razor` leaderboard rendering and instance diff/transcript drill-down.

