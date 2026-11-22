# 🏁 Step 12.23A: Crucible Benchmark Engine, Leak-Free Workspaces & Container Verifier

> **Feature Implementation Step (Part 1 of 3 — Dataset Loader, Leak-Free Git Provisioner, Stateless Crucible Runner & Verifiers)**  
> *Parent Plan:* [Features & Integrations Roadmap](README.md)  
> *Companion Steps:* [Step 12.23B: Standalone Benchmark CLI (`NightCodeForge.Benchmark.Cli`)](STEP_12_23B_BENCHMARK_CLI_AND_LEADERBOARD_WEB_UI.md) | [Step 12.23C: Multi-Stack Benchmark Datasets, Importers & Crafted Scenarios](STEP_12_23C_BENCHMARK_DATASETS_IMPORTERS_AND_SCENARIOS.md)  
> *Related Steps:* [Step 11.33: Unified LLM Provider Architecture](../archive/phase-11/STEP_11_33_UNIFIED_LLM_PROVIDER_DYNAMIC_CONTEXT_AND_FLEET_INTEGRATION.md) | [Step 11.34: Live Git Diff Worktree & Untracked Files](../archive/phase-11/STEP_11_34_LIVE_GIT_DIFF_WORKTREE_AND_UNTRACKED_FILES.md) | [Step 12.14: Global Skills Injection & Stack Tooling](../archive/phase-12/STEP_12_14_GLOBAL_SKILLS_INJECTION_AND_STACK_TOOLING.md)  
> *Source Policy:* [AGENTS.md](../../AGENTS.md)  
> *Project Target:* `src/NightCodeForge.Core`, `src/NightCodeForge.Infrastructure`, `src/NightCodeForge.Agents`, `tests/NightCodeForge.Agents.Tests` | *Status:* Planned | *Target Framework:* .NET 10 (C# 13)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Objective

Provide a dedicated, **strictly standalone** benchmark execution engine to select the best models for local inference and verify that NightCodeForge harness, prompt, and tooling evolutions across commits/branches do not regress. The engine evaluates **"Implementation"**, **"Code Review"**, and **"ForgeTask Review"** problems directly through `NightCodeForge.Agents` (`CrucibleAgentHarness` and related agent classes) using a **mock / in-memory database**—without invoking the 6-stage NightCodeForge orchestrator, Hangfire, `NightCodeForge.Web`, or real SQLite `Workspace`/`ForgeTask` rows:
1. **Multi-Category Dataset Ingestion (`IBenchmarkDatasetLoader`):** Parse `.jsonl` and `.json` files across three problem categories:
   - **Implementation** (SWE-Sharp-Bench / SWE-bench format: `instance_id`, `repo`, `base_commit`, `problem_statement`, `hints_text`, `patch` $\rightarrow$ `GoldPatch`, `test_patch`, `fail_to_pass`, `pass_to_pass`).
   - **Code Review** (problem statement + candidate diff materialized on a feature branch, expected review verdict and structured findings).
   - **ForgeTask Review** (task specification markdown + repository context, expected readiness/clarification verdict, difficulty, and structured findings).
2. **Leak-Free Ephemeral Workspace Provisioning (`IBenchmarkWorkspaceManager`):** Maintain a local repository cache (`~/.nightcodeforge/benchmarks/repos/{owner}__{repo}`) and initialize each benchmark instance workspace via `git init` + `git fetch <cachedRepoPath> <BaseCommit>` + `git checkout --detach FETCH_HEAD` so zero commits, tags, or branches after `BaseCommit` exist in `.git`, followed by category-specific branch/task-file materialization.
3. **Stateless Crucible Runner with Mock DB (`ICrucibleBenchmarkRunner`):** Wire `CrucibleAgentHarness` against mock/in-memory implementations of `ITaskRepository`, `IWorkspaceRepository`, `ICrucibleConversationCoordinator` (via decoupled turn persistence capturing turns in memory and markdown transcripts), `ITaskCancellationManager`, and `IOperationsControlService`. Sweep across **model candidates** using category-accurate NightCodeForge tool permissions and stage directives, capturing `BenchmarkRunProvenance` (NCF version, git commit, git branch) and exporting `predictions.jsonl` for patch-producing runs.
4. **Built-In Verification, Optional LLM-as-Judge & Telemetry Scorecard:**
   - For **Implementation**: Apply `model_patch` + `TestPatch` inside `NightCodeForgeContainerExecutor`, execute stack-appropriate tests (`CustomTestCommand` or default runner with `.trx`, JUnit XML, or `cargo test` reporting), and evaluate `FailToPass` and `PassToPass`.
   - For **Code Review** and **ForgeTask Review**: Always evaluate deterministic verdict, artifact/patch, and finding-anchor rules; optionally invoke an LLM-as-judge (`IBenchmarkLlmJudge`) when configured for qualitative rubric scoring.
   - Compute `BenchmarkInstanceResult`, `ToolCallTelemetryBreakdown` (wired directly to `LenientDispatchTelemetry` and `OnBashExecutionAudited`), `BenchmarkCandidateSummary`, and `BenchmarkRunReport`.

*(The dedicated standalone Benchmark CLI commands `run`, `report`, and `dataset` are specified in [Step 12.23B](STEP_12_23B_BENCHMARK_CLI_AND_LEADERBOARD_WEB_UI.md) and [Step 12.23C](STEP_12_23C_BENCHMARK_DATASETS_IMPORTERS_AND_SCENARIOS.md).)*

---

## 2. Architecture & Technical Specification

### 2.1 Domain Models & Dataset Schemas (`NightCodeForge.Core.Benchmarking`)
- `BenchmarkProblemCategory`: `Implementation`, `CodeReview`, `TaskReview`.
- `TestResultFormat`: `Trx` (.NET), `JUnitXml` (`pytest`, `vitest`, `jest`), `CargoTest` (Rust), `ExitCode`.
- `BenchmarkRunProvenance`: `NcfVersion`, `GitCommit`, `GitBranch`, `RunTimestampUtc`, `ProviderType`, `Endpoint`, `EnableStackSkills`, `OptimizationFlags`, `DetectedToolVersions`. Recorded on every run so cross-commit / cross-branch regression comparisons remain reproducible.
- `BenchmarkInstance`:
  - Common fields: `InstanceId` (`instance_id`), `Category` (`category`, optional when overridden at run level), `Language` (`language`: `csharp`, `typescript`, `javascript`, `python`, `rust`), `Repo` (`repo`), `BaseCommit` (`base_commit`), `ProblemStatement` (`problem_statement`), `HintsText` (`hints_text`), `CustomContainerImage` (`custom_container_image`), `CustomTestCommand` (`custom_test_command`), `TestResultFormat` (`test_result_format`).
  - `Implementation` fields: `GoldPatch` (`patch`), `TestPatch` (`test_patch`), `FailToPass` (`fail_to_pass`), `PassToPass` (`pass_to_pass`) — accepting both JSON arrays and JSON-encoded string arrays.
  - `CodeReview` fields: `CandidatePatch` (`candidate_patch`), `TaskSpecification` (`task_specification`, optional), `ExpectedVerdict` (`expected_verdict`: `Approve`, `MinorFix`, `RequestChanges`), `ExpectedFindings` (`expected_findings`: list of structured finding anchors — file path, symbol/line hint, severity, summary/keywords), plus optional `TestPatch`/`FailToPass`/`PassToPass` for verifying `MinorFix` edits.
  - `TaskReview` fields: `TaskSpecification` (`task_specification`, markdown body/frontmatter), `ExpectedVerdict` (`expected_verdict`: `Ready`, `NeedsClarification`), `ExpectedDifficulty` (`expected_difficulty`, optional `standard` | `difficult` | `expert`), `ExpectedFindings` (`expected_findings`: list of required architectural/file/ambiguity anchors).
- `BenchmarkModelCandidate`: `CandidateId`, `DisplayName`, `ModelName`, `ThinkingEffort`, `ThinkingTokenBudget`, `ContextWindowTokens`, `MaxOutputTokens`, `Temperature`, `MaxTurns`, `InstanceTimeoutSeconds`, `OptimizationFlags`, `EnableStackSkills`, `RoutingTarget` (`OllamaRoutingTarget`). *(No runtime harness or tooling-version matrix dimension; cross-version tooling/harness differences are measured across NCF commits via `BenchmarkRunProvenance`.)*
- `BenchmarkJudgeConfig`: Optional `ModelName`, `ProviderType`, `Endpoint`, `Temperature`, `TimeoutSeconds`.
- `BenchmarkRunRequest`: `SuiteName`, `DatasetPath`, `CategoryOverride`, `InstanceIdFilter`, `MaxInstances`, `Candidates`, `IncludeHints`, `VerifyWithTests`, `ExecutionMode`, `Concurrency` (default `1`), `JudgeConfig`, `ResumeFromDir`.
- `ToolCallTelemetryBreakdown`, `BenchmarkInstanceOutcome`, `BenchmarkInstanceResult` (including `ActualVerdict`, `DeterministicScore`, `JudgeScore`, `JudgeRationale`, `TaskDifficultyResult`, `ErrorMessage`), `BenchmarkCandidateSummary`, and `BenchmarkRunReport` (aggregating `Provenance`, `Summaries`, and `Results`).

### 2.2 Leak-Free Ephemeral Instance Repository (`IBenchmarkWorkspaceManager`)
```bash
git init <instanceWorkspacePath>
git -C <instanceWorkspacePath> config core.autocrlf false
git -C <instanceWorkspacePath> fetch <cachedRepoPath> <BaseCommit>
git -C <instanceWorkspacePath> checkout --detach FETCH_HEAD
git -C <instanceWorkspacePath> remote remove origin 2>/dev/null || true
```
Guarantees zero git history leakage after `BaseCommit` and provides a self-contained `.git` directory inside `<instanceWorkspacePath>` mounted directly at `/workspace` inside `NightCodeForgeContainerExecutor`.

**Category-Specific Materialization:**
- **`Implementation`:** Leaves the workspace detached at `BaseCommit`.
- **`CodeReview`:** Creates a base branch (e.g., `ncf-bench-base`) at `BaseCommit`, switches to a feature branch (e.g., `ncf-bench-candidate`), applies `CandidatePatch` via `git apply`, and commits it. Populates diff context (`git diff ncf-bench-base...HEAD` and `ReviewDiffStat`) so the review stage inspects the PR diff identically to production `code-review-fix`.
- **`TaskReview`:** Creates a working branch at `BaseCommit`, writes `TaskSpecification` to `docs/tasks/<instance_id>.md` (adding minimal YAML frontmatter `id` and `status` only if the fixture does not already include it), and sets `AgentSessionContext.TaskMarkdownPath`.

### 2.3 Single-Shot Crucible Execution, Category Policies & Patch Extraction (`ICrucibleBenchmarkRunner`)
1. **Decoupled Turn Persistence:** `CrucibleAgentHarness` already delegates persistence through `ICrucibleConversationCoordinator`, while `CrucibleConversationState` currently couples turn writes directly to `ITaskRepository`. Extract an `IConversationTurnSink` abstraction in `CrucibleConversationState` (or provide an in-memory `ICrucibleConversationCoordinator`) so benchmark runs capture turns in memory and markdown transcripts without inserting fake `Workspace` or `ForgeTask` entities into SQLite.
2. **Category-Specific Tool Gating & Directives:** Align `StageId`, `StageDirective`, `RequiredMode`, and `PermittedTools` with `StandardWorkflowDefinitions` so benchmarks faithfully exercise NightCodeForge's real stage prompts and tool contracts:
   - **`Implementation` (`StandardStageIds.Implementation`, `AgentMode.Full`):** Permitted tools `["read", "edit", "write", "bash"]`, plus `"load_skill"` when `EnableStackSkills == true`. Excluded: `ask`, `propose_tasks`, `request_changes`, `web_search`.
   - **`CodeReview` (`StandardStageIds.CodeReviewAndFix`, `AgentMode.Full`):** Permitted tools `["read", "edit", "write", "bash", "request_changes"]`, plus `"load_skill"` when `EnableStackSkills == true`. Excluded: `ask`, `propose_tasks`, `web_search`. Intercepts `RequestChangesException` / `ReviewerChangesRequestedInterruption` (and `<request_changes>` fallback block) as an explicit `RequestChanges` verdict with feedback.
   - **`TaskReview` (`StandardStageIds.TaskReview`):** Permitted tools `["read", "edit", "write", "bash", "ask"]`, plus `"load_skill"` when `EnableStackSkills == true`. Excluded: `propose_tasks`, `request_changes`, `web_search`. Intercepts `HumanInterruptionException` / `HumanClarificationInterruption` cleanly as a `NeedsClarification` verdict with captured questions; otherwise reads back `TaskMarkdownPath` on completion as `Ready`.
3. **Stack Skills & Container Mount Toggle:** When `EnableStackSkills == true`, enable `csharp-nav` and `codecondenser` skills (excluding `mdfetch`) and mount `~/.dotnet/tools` into `/opt/ncf-dotnet-tools`. Add an explicit option on `ContainerExecutorOptions` / `SandboxMountTable` to disable the `~/.dotnet/tools` mount when `EnableStackSkills == false` (instead of unconditionally mounting host tools).
4. **Telemetry Wiring:** Attach `LenientDispatchTelemetry` via `NightCodeForgeToolDispatcher` to record `DispatcherRepairs` and `EditFailures`, and subscribe to `CanonicalToolScope.OnBashExecutionAudited` to count `CsNavBashCalls` and `CodeCondenserBashCalls` deterministically without transcript regex scraping.
5. **Patch Extraction (`model_patch`) & `predictions.jsonl`:**
   - For `Implementation` (and `CodeReview` runs producing Tier-1 fixes), stage untracked new source files with `git add -N`, exclude `bin/`, `obj/`, `TestResults/`, `*.trx`, and run `git diff <BaseCommit>`.
   - Append `{"instance_id": "...", "model_name_or_path": "...", "model_patch": "..."}` to `<runOutputDir>/<candidateSlug>/predictions.jsonl`.

### 2.4 Verification, Optional LLM-as-Judge & Operational Rules (`IBenchmarkVerifier` & `IBenchmarkLlmJudge`)
1. **Implementation Verification (Multi-Stack):**
   - Reset workspace to `BaseCommit`, apply `model_patch` and `TestPatch` via `git apply`.
   - Inside `NightCodeForgeContainerExecutor`, execute `CustomTestCommand` when provided, or the stack default (`dotnet build` + `dotnet test --no-build --logger "trx;LogFileName=results.trx"` for C#; `pytest` / `vitest` / `jest` with JUnit XML for Python/TS/JS; `cargo test` for Rust).
   - Parse structured test results according to `TestResultFormat` (`.trx` for .NET, JUnit XML for `pytest`/`vitest`/`jest`, structured/exit-code parser for `cargo test`, avoiding OS/shell argument length limits from giant `--filter` strings) and evaluate all `FailToPass` and `PassToPass` assertions. Clean up the container and ephemeral workspace after saving logs.
2. **Code Review Verification:**
   - **Deterministic scoring (always runs):** Classify actual verdict (`RequestChanges` when `request_changes` / `<request_changes>` triggered; `MinorFix` when a non-empty code patch was applied; `Approve` otherwise), score verdict match against `ExpectedVerdict`, check `ExpectedFindings` anchors (referenced files, symbols, or finding keywords) against the review feedback/patch, and run tests if `MinorFix` produced a patch on an instance with test metadata.
   - **Optional LLM-as-judge:** When `BenchmarkJudgeConfig` is provided, grade review accuracy, false positives, and defect coverage (`0.0–1.0` + rationale) against `ProblemStatement`, `CandidatePatch`, `ExpectedVerdict`, and `ExpectedFindings`.
3. **ForgeTask Review Verification:**
   - **Deterministic scoring (always runs):** Classify actual verdict (`NeedsClarification` when `ask` triggered; `Ready` otherwise), verify zero modifications outside `docs/tasks/`, verify YAML frontmatter `id` and `status` preservation and non-trivial specification enrichment in `TaskMarkdownPath`, verify `ExpectedDifficulty` when specified, and check `ExpectedFindings` anchor coverage (affected files, requirements, ambiguities).
   - **Optional LLM-as-judge:** When `BenchmarkJudgeConfig` is provided, grade specification completeness, architectural accuracy, and question relevance (`0.0–1.0` + rationale).
4. **Resilience & `AGENTS.md` Constraints:**
   - Fail fast with explicit domain exceptions on malformed dataset records, unknown `BaseCommit`, missing container images, or unreachable inference/judge endpoints (never silently skip failures or insert synthetic placeholder scores).
   - Keep service constructors at or below 10 parameters without optional parameters.

---

## 3. Definition of Done (DoD) Checklist

- [ ] Define `BenchmarkInstance` (including `Language` and `TestResultFormat`), `BenchmarkModelCandidate`, `BenchmarkRunProvenance`, `BenchmarkJudgeConfig`, `BenchmarkRunRequest`, `BenchmarkInstanceResult`, `ToolCallTelemetryBreakdown`, `BenchmarkCandidateSummary`, and `BenchmarkRunReport` in `NightCodeForge.Core.Benchmarking`.
- [ ] Implement `IBenchmarkDatasetLoader` supporting `Implementation` (SWE-Sharp-Bench / SWE-bench `.jsonl` / `.json` with JSON string array handling), `CodeReview`, and `TaskReview` datasets with fail-fast schema validation.
- [ ] Implement `IBenchmarkWorkspaceManager` with cached canonical clones, leak-free `git init` + `git fetch <cachedRepoPath> <BaseCommit>` workspaces, `CodeReview` candidate branch materialization, and `TaskReview` markdown task file materialization.
- [ ] Decouple `CrucibleConversationState` turn persistence via `IConversationTurnSink` (or in-memory `ICrucibleConversationCoordinator`), add the `ContainerExecutorOptions` / `SandboxMountTable` mount toggle for `~/.dotnet/tools`, and implement `ICrucibleBenchmarkRunner` with category-specific tool/skill gating, `LenientDispatchTelemetry` + `OnBashExecutionAudited` telemetry wiring, and `predictions.jsonl` export.
- [ ] Implement `IBenchmarkVerifier` (containerized multi-stack `.trx` / JUnit XML / `cargo test` / `CustomTestCommand` verification for `Implementation`; deterministic verdict/anchor/file-scope verification for `CodeReview` and `TaskReview`) and optional `IBenchmarkLlmJudge`.
- [ ] Add unit tests in `NightCodeForge.Agents.Tests` covering dataset parsing across all 3 categories, zero post-`BaseCommit` git leakage, category-specific tool/skill gating, `predictions.jsonl` generation, `.trx` / JUnit XML / `cargo test` verification, deterministic + judge review scoring, telemetry capture, and summary aggregation.


