# 🏁 Step 12.23A: Crucible Benchmark Engine, Leak-Free Workspaces & Container Verifier

> **Feature Implementation Step (Part 1 of 2 — Dataset Loader, Leak-Free Git Provisioner, Stateless Crucible Runner & Verifier)**  
> *Parent Plan:* [Features & Integrations Roadmap](README.md)  
> *Companion Step:* [Step 12.23B: `ncf benchmark` CLI & `/benchmarks` Leaderboard Web UI](STEP_12_23B_BENCHMARK_CLI_AND_LEADERBOARD_WEB_UI.md)  
> *Related Steps:* [Step 11.33: Unified LLM Provider Architecture](../archive/phase-11/STEP_11_33_UNIFIED_LLM_PROVIDER_DYNAMIC_CONTEXT_AND_FLEET_INTEGRATION.md) | [Step 11.34: Live Git Diff Worktree & Untracked Files](../archive/phase-11/STEP_11_34_LIVE_GIT_DIFF_WORKTREE_AND_UNTRACKED_FILES.md) | [Step 12.14: Global Skills Injection & Stack Tooling](../archive/phase-12/STEP_12_14_GLOBAL_SKILLS_INJECTION_AND_STACK_TOOLING.md)  
> *Source Policy:* [AGENTS.md](../../AGENTS.md)  
> *Project Target:* `src/NightCodeForge.Core`, `src/NightCodeForge.Infrastructure`, `src/NightCodeForge.Agents`, `tests/NightCodeForge.Agents.Tests` | *Status:* Planned | *Target Framework:* .NET 10 (C# 13)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Objective

Provide a dedicated, **strictly standalone** benchmark execution engine that evaluates **"Implementation"**, **"Code Review"**, and **"Task Review"** problems directly through `NightCodeForge.Agents` (`CrucibleAgentHarness` and related agent classes) using a **mock / in-memory database**—without invoking the 6-stage NightCodeForge orchestrator, Hangfire, `NightCodeForge.Web`, or real SQLite `Workspace`/`ForgeTask` rows:
1. **Multi-Category Dataset Ingestion (`IBenchmarkDatasetLoader`):** Parse `.jsonl` and `.json` files across three problem categories:
   - **Implementation** (SWE-Sharp-Bench / SWE-bench format: `instance_id`, `repo`, `base_commit`, `problem_statement`, `hints_text`, `patch` $\rightarrow$ `GoldPatch`, `test_patch`, `fail_to_pass`, `pass_to_pass`).
   - **Code Review** (problem statement + candidate diff / branch, expected review verdict and findings).
   - **Task Review** (task specification + repository context, expected specification-enforcement / readiness verdict).
2. **Leak-Free Ephemeral Workspace Provisioning (`IBenchmarkWorkspaceManager`):** Maintain a local repository cache (`~/.nightcodeforge/benchmarks/repos/{owner}__{repo}`) and initialize each benchmark instance workspace via `git init` + `git fetch <cachedRepoPath> <BaseCommit>` + `git checkout --detach FETCH_HEAD` so zero commits, tags, or branches after `BaseCommit` exist in `.git`.
3. **Stateless Crucible Runner with Mock DB (`ICrucibleBenchmarkRunner`):** Wire `CrucibleAgentHarness` against mock/in-memory implementations of `ITaskRepository`, `IWorkspaceRepository`, `ICrucibleConversationCoordinator` (capturing turns in memory and markdown transcripts), `ITaskCancellationManager`, and `IOperationsControlService`. Evaluate across both **model configurations** and **tooling versions / flags** (e.g., stack skills `csnav`/`codecondenser` on/off or versioned, `LocalOptimizationFlags`), exporting `predictions.jsonl` for implementation runs.
4. **Built-In Verification & Telemetry Scorecard:**
   - For **Implementation**: Apply `model_patch` + `TestPatch` inside `NightCodeForgeContainerExecutor`, run `FailToPass` and `PassToPass` tests.
   - For **Code Review** and **Task Review**: Evaluate the agent's review verdict and structured findings against the dataset's expected ground-truth outcome.
   - Compute `BenchmarkInstanceResult` (`ToolCallTelemetryBreakdown` including `CsNavBashCalls`, `CodeCondenserBashCalls`, `EditFailures`, and `DispatcherRepairs`) and `BenchmarkCandidateSummary`.

*(The standalone Benchmark CLI commands `run` and `report` are specified in [Step 12.23B](STEP_12_23B_BENCHMARK_CLI_AND_LEADERBOARD_WEB_UI.md).)*

---

## 2. Architecture & Technical Specification

### 2.1 Domain Models (`NightCodeForge.Core.Benchmarking`)
- `BenchmarkProblemCategory`: `Implementation`, `CodeReview`, `TaskReview`.
- `BenchmarkInstance`: `InstanceId`, `Category`, `Repo`, `BaseCommit`, `ProblemStatement`, `HintsText`, `GoldPatch`, `TestPatch`, `FailToPass`, `PassToPass`, `CandidatePatch` (for `CodeReview`), `TaskSpecification` (for `TaskReview`), `ExpectedVerdict`, `ExpectedFindings`, `CustomContainerImage`, `CustomTestCommand`.
- `BenchmarkModelCandidate`: `CandidateId`, `DisplayName`, `ModelName`, `ThinkingEffort`, `ThinkingTokenBudget`, `ContextWindowTokens`, `Temperature`, `MaxTurns`, `InstanceTimeoutSeconds`, `OptimizationFlags`, `EnableStackSkills`, `ToolingVersionTag`.
- `BenchmarkRunRequest`: `SuiteName`, `DatasetPath`, `InstanceIdFilter`, `MaxInstances`, `Candidates`, `IncludeHints`, `VerifyWithTests`, `ExecutionMode`.
- `ToolCallTelemetryBreakdown`, `BenchmarkInstanceOutcome`, `BenchmarkInstanceResult`, and `BenchmarkCandidateSummary`.

### 2.2 Leak-Free Ephemeral Instance Repository (`IBenchmarkWorkspaceManager`)
```bash
git init <instanceWorkspacePath>
git -C <instanceWorkspacePath> fetch <cachedRepoPath> <BaseCommit>
git -C <instanceWorkspacePath> checkout --detach FETCH_HEAD
git -C <instanceWorkspacePath> remote remove origin 2>/dev/null || true
```
Guarantees zero git history leakage after `BaseCommit` and provides a self-contained `.git` directory inside `<instanceWorkspacePath>` mounted directly at `/workspace` inside `NightCodeForgeContainerExecutor`.

### 2.3 Single-Shot Crucible Execution & Patch Extraction (`ICrucibleBenchmarkRunner`)
1. **Decoupled Turn Persistence:** Extract an `IConversationTurnSink` abstraction (or optional persistence callback) in `CrucibleAgentHarness` so benchmark runs capture turns in memory and markdown transcripts without inserting fake `Workspace` or `ForgeTask` entities into SQLite.
2. **Tool & Skill Gating:**
   - Permitted tools: `["read", "edit", "write", "bash"]`, plus `"load_skill"` when `EnableStackSkills == true`.
   - Excluded tools: `ask`, `propose_tasks`, `request_changes`, `web_search`.
   - When `EnableStackSkills == true`: enable `csharp-nav` and `codecondenser` skills (excluding `mdfetch`) and mount `~/.dotnet/tools` (`csnav`, `codecondenser`) into the container. When `false`, omit `load_skill` and do not mount `~/.dotnet/tools`.
3. **Patch Extraction (`model_patch`) & `predictions.jsonl`:**
   - Stage untracked new source files with `git add -N`, exclude `bin/`, `obj/`, `TestResults/`, `*.trx`, and run `git diff <BaseCommit>`.
   - Append `{"instance_id": "...", "model_name_or_path": "...", "model_patch": "..."}` to `<runOutputDir>/<candidateSlug>/predictions.jsonl`.
4. **Containerized Verification:**
   - Reset workspace to `BaseCommit`, apply `model_patch` and `TestPatch` via `git apply`, run `dotnet build` and `dotnet test --no-build --filter "<FullyQualifiedName>"` for all `FailToPass` and `PassToPass` tests inside `NightCodeForgeContainerExecutor`, and clean up the container and ephemeral workspace after saving logs.

---

## 3. Definition of Done (DoD) Checklist

- [ ] Define `BenchmarkInstance`, `BenchmarkModelCandidate`, `BenchmarkRunRequest`, `BenchmarkInstanceResult`, `ToolCallTelemetryBreakdown`, and `BenchmarkCandidateSummary` in `NightCodeForge.Core.Benchmarking`.
- [ ] Implement `IBenchmarkDatasetLoader` supporting SWE-Sharp-Bench and SWE-bench `.jsonl` / `.json` datasets (including JSON string arrays for `fail_to_pass` / `pass_to_pass`).
- [ ] Implement `IBenchmarkWorkspaceManager` with cached canonical clones and leak-free `git init` + `git fetch <cachedRepoPath> <BaseCommit>` instance workspaces.
- [ ] Decouple `CrucibleAgentHarness` single-session execution from `ITaskRepository` and implement `ICrucibleBenchmarkRunner` with tool/skill gating, `predictions.jsonl` export, containerized `FailToPass`/`PassToPass` verification, `LenientToolDispatcher` repair counting, and `BenchmarkCandidateSummary` aggregation.
- [ ] Add unit tests in `NightCodeForge.Agents.Tests` covering JSONL parsing, zero post-`BaseCommit` git leakage, non-interactive tool/skill gating, `predictions.jsonl` generation, containerized test verification, and leaderboard aggregation.

