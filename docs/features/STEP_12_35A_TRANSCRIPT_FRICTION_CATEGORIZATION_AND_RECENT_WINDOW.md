# 📊 Step 12.35A: Deterministic Transcript Friction Categorization, Actionable Breakdowns & Recent-Session Windowing

> **Feature & Diagnostics Specification (Part 1 of 2 — Deterministic Friction Engine & UI/CLI Windowing)**  
> *Parent Plan:* [Features & Integrations Roadmap](README.md)  
> *Companion Step:* [Step 12.35B: Idle-GPU Nightwatch Deep Transcript & Workspace Analysis (Replacing Legacy AI Diagnostics)](STEP_12_35B_NIGHTWATCH_IDLE_DEEP_ANALYSIS_AND_LEGACY_DIAGNOSTIC_REPLACEMENT.md)  
> *Related Steps:* [Step 12.34C: Nightwatch Telemetry UI & Harness Issue Ledger](STEP_12_34C_NIGHTWATCH_TELEMETRY_HARNESS_ISSUES_UI_AND_SELF_HEALING_BACKLOG.md)  
> *Source Policy:* [AGENTS.md](../../AGENTS.md) | [docs/ARCHITECTURE.md](../ARCHITECTURE.md)  
> *Target Projects:* `src/NightCodeForge.Core`, `src/NightCodeForge.Web`, `src/NightCodeForge.Cli`, `tests/NightCodeForge.Cli.Tests`, `tests/NightCodeForge.Web.Tests` | *Status:* Planned | *Target Framework:* .NET 10 (C# 13)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Problem Statement & Motivation

In `Session Transcripts > Analysis & Friction` (`Transcripts.razor`, `TranscriptAnalysisViewer.razor`, `FleetAnalysisDashboard.razor`, and `TranscriptAnalysisService.cs`), the current deterministic analyzer has three structural limitations:

1. **Mixed Recommendation Targets:** `TranscriptAnalysis.Recommendations` is a flat `IReadOnlyList<string>` that mixes advice meant for the **user's codebase** (e.g., splitting high-churn files, fixing compiler errors) with advice meant for **NightCodeForge itself** (e.g., pre-injecting `git diff` in code review stages, fixing KV-cache history rewrites, tuning compaction thresholds). Users cannot easily tell what they should fix in their repo versus what belongs in NightCodeForge's harness/prompts.
2. **Coarse Error Counts Instead of Actionable Breakdowns:** The scorecard reports aggregate counts (`EditFailures`, `BashErrors`, `ToolErrors`, `CompilerErrors`, `TestFailures`), without surfacing *which* specific files, *which* tools, *which* bash commands, or *which* compiler error codes caused the friction.
3. **Unbounded Transcript Scanning:** `TranscriptAnalysisService.AnalyzeDirectoryAsync` and `TranscriptBrowserService.GetTranscriptsAsync` / `GetFleetAnalysisAsync` read and parse every `*.md` transcript file in the workspace directory without a recency cap. As workspaces accumulate hundreds of historical sessions, Fleet Analysis slows down and old resolved patterns dilute current friction signals.

---

## 2. Detailed Requirements & Specifications

### 2.1 Categorized Recommendations (`NightCodeForge.Core`)

1. **Recommendation Target Enum & Structured Record:**
   - Replace (clean break, per `AGENTS.md`) the untyped `IReadOnlyList<string> Recommendations` on `TranscriptAnalysis` with structured `FrictionRecommendation` records (and/or separate typed properties) categorized by `RecommendationTarget`:
     - `RecommendationTarget.UserCodebase` — Actionable improvements to the target repository (e.g., splitting oversized/high-churn files, resolving recurring compiler diagnostics or test failures, fixing build/test scripts).
     - `RecommendationTarget.NightCodeForge` — Actionable improvements to NightCodeForge's harness, stage prompts, tool schemas, context compaction, or KV-cache prefix stability.
   - Each `FrictionRecommendation` includes:
     - `Target` (`RecommendationTarget`)
     - `Category` (`FrictionCategory`: `FileArchitecture`, `Tooling`, `ShellCommand`, `CompilerDiagnostic`, `TestSuite`, `PromptOrStage`, `ContextAndCache`)
     - `Title` (`string`)
     - `ActionableDetail` (`string` — concrete next step referencing the specific file, tool, command, or error code)
     - `Subject` (`string?` — e.g., file path, tool name, command prefix, or diagnostic code for grouping/deduplication across the fleet)
2. **Deterministic Attribution Rules:**
   - **User Codebase (`UserCodebase`):**
     - High-churn / high-edit-failure source files (`>= 3` edits or `>= 2` edit mismatches on a single file) -> recommend splitting/modularizing that specific file path.
     - Recurring compiler error codes (e.g., `CS0117`, `CS0246`, `CS1061`, TypeScript `TSxxxx`) -> surface the top codes and affected files.
     - Failing test suites / specific test names -> list failing test identifiers.
   - **NightCodeForge (`NightCodeForge`):**
     - `ranStaleTest` (`dotnet test --no-build` immediately after edits) -> prompt/stage directive improvement for NCF.
     - `HistoryRewrites > 10` -> NCF middleware / reasoning pruning KV-cache invalidation.
     - `CompactionEvents > 5` -> NCF context budget / compaction tuning.
     - Code-review stage `git diff` exploration overhead -> NCF stage prompt pre-injection.
     - High failure rate on a specific canonical tool (e.g., `edit` mismatch rate, `read` path errors) or malformed/truncated tool calls -> NCF tool ergonomics / prompt guidance / output parsing.
     - Oversized tool outputs (`> 10,000` chars) driving premature compaction -> NCF CLI output filtering (e.g., Step 12.6) or tool truncation limits.
     - Slow LLM inference turns (`> 30s` following `HistoryRewrites`) -> NCF model preloading / KV-cache stability.
     - Repeated identical tool call loops or dead-end searches (`>= 3`) -> NCF prompt directives or symbol-map/repo-context pre-injection.

---

### 2.2 Actionable Deterministic Friction Breakdowns (`TranscriptAnalysisService`)

Extend `TranscriptAnalysis` with deterministic, structured breakdown collections (extracted purely via regex/parsing, zero LLM calls).

- **Section Boundary & Non-Double-Counting Rule:** Before extracting turn, tool, file, bash, compiler, and test metrics, split the raw markdown at `### Model calls` (and strip `### Environment` / `### MAF Pipeline Configuration`) so that tool invocations, outputs, compiler diagnostics, and exit codes are only counted once from the primary conversation turns—never double-counted from `### Model calls` request snapshots (which are only inspected for `HistoryRewriteRegex()`). When correlating tool calls with outputs, support both paired turn headers (`### [MM:SS] 🔧 TOOL` followed by `### [MM:SS] ↳ TOOL_RESULT` / `❌ TOOL_ERROR`) and inline `**Tool Output:**` blocks without double-counting `**Tool Invocation:**` lines inside `TOOL_RESULT` / `TOOL_ERROR` turns:

1. **File Friction Breakdown (`IReadOnlyList<FileFrictionEntry>`):**
   - Track per-file: `FilePath`, `ReadCount`, `EditCount`, `EditFailureCount`, `EditedWithoutReadCount`, and computed `IsSplitCandidate` (`EditCount >= 3 || EditFailureCount >= 2` or repeated re-reads `>= 3` without intervening edits).
   - Ordered by `(EditFailureCount * 3 + EditCount)` descending, capped to top entries per session and aggregated across the fleet.
2. **Tool Friction Breakdown (`IReadOnlyList<ToolFrictionEntry>`):**
   - Correlate each `TOOL` invocation with its corresponding `TOOL_RESULT` / `TOOL_ERROR` / `**Tool Output:**` block to track per-tool:
     - `ToolName`, `InvocationCount`, `FailureCount`, `FailureRatePercent`, `OversizedOutputCount` (`> 10,000` chars), and `SampleError` (truncated first error line).
   - Surface tools with `FailureCount > 0` or `OversizedOutputCount > 0`, ordered by `FailureCount` descending.
3. **Bash Command Friction Breakdown (`IReadOnlyList<BashCommandFrictionEntry>`):**
   - Correlate `bash` tool invocations with their exit codes, output sizes, and timeout/hang markers (e.g., non-zero `Exit Code: N`, `timed out`, `sentinel timeout`, `SIGINT`, killed/hung process indicators).
   - Normalize commands to a stable signature (e.g., leading command + primary subcommand such as `dotnet test`, `dotnet build`, `git diff`, `npm run build`, stripping transient paths/flags) and record:
     - `CommandPattern`, `ExecutionCount`, `FailureCount`, `HangOrTimeoutCount`, `OversizedOutputCount`, `LastExitCode`.
4. **Compiler Error Breakdown (`IReadOnlyList<CompilerErrorEntry>`):**
   - Extract diagnostic codes (C# `CS\d+`, MSBuild `MSB\d+`, TypeScript `TS\d+`, or generic compiler error codes) along with a normalized message snippet and optional file path.
   - Group by `DiagnosticCode` with `Count` and `SampleMessage`, ordered by `Count` descending.
5. **Loop, Search & Test-Oscillation Signals (`TranscriptBehavioralSignals`):**
   - **Repeated Identical Tool Loops:** Detect `(ToolName, NormalizedArgs)` invoked `>= 3` times in a session (`IReadOnlyList<RepeatedToolLoopEntry>`).
   - **Dead-End Searches:** Count `grep`/`glob` invocations returning zero matches and `read` invocations returning file-not-found (`DeadEndSearchCount`).
   - **Test-Fix-Test Oscillation:** Count test-runner cycles (`TestRunCycles`) and detect whether failing test identifiers stayed identical across consecutive runs vs. shifted to new tests (regression ping-pong).
   - **Self-Reverted Edits & Malformed Turns:** Count `git checkout -- <file>` / `git restore` self-reverts, malformed/unparsed tool calls, max-token truncations, and disproportionate thinking-to-action ratios.
6. **Turn & Tool Latency Breakdown (from `[ mm:ss ]` Turn Offsets):**
   - Parse consecutive `### [ mm:ss ]` turn header offsets (parsing `totalSeconds = minutes * 60 + seconds` directly rather than `TimeSpan.Parse`, since `SessionTranscriptService.FormatOffset` emits unbounded total minutes such as `[ 75:12 ]` for runs exceeding an hour) to compute per-turn elapsed durations:
     - **Slow Tool Executions:** Tool invocation -> tool result deltas exceeding a threshold (e.g., `> 60s`), attributed to the tool/command.
     - **Slow LLM Inference Turns:** User/Tool -> Assistant deltas exceeding a threshold (e.g., `> 30s`), flagging potential cold-loads or KV-cache misses.

---

### 2.3 Recent-Transcript Windowing & Limit

1. **Default Recency Cap & Total-Count Metadata:**
   - Introduce a `maxTranscripts` parameter (default `50` via a shared constant, e.g. `TranscriptAnalysisDefaults.DefaultRecentLimit = 50`, with `null` or `<= 0` meaning unlimited when explicitly requested) on:
     - `ITranscriptAnalysisService.AnalyzeDirectoryAsync(string directoryPath, int? maxTranscripts = ..., CancellationToken cancellationToken = default)`
     - `ITranscriptBrowserService.GetTranscriptsAsync(string? workspacePath = null, int? maxTranscripts = ..., CancellationToken cancellationToken = default)`
     - `ITranscriptBrowserService.GetFleetAnalysisAsync(string? workspacePath = null, int? maxTranscripts = ..., CancellationToken cancellationToken = default)`
   - Expose the pre-slice total candidate transcript count (`M`) alongside the returned windowed slice (`N`) (e.g., via a `TranscriptWindowResult<T>(IReadOnlyList<T> Items, int TotalAvailableCount)` wrapper or companion count property) so the UI can render `"Showing latest N of M transcripts"` without performing a second directory scan.
2. **Fast Pre-Parse Ordering:**
   - Before reading full file contents, sort candidate `*.md` files by parsed filename date/task ID or `File.GetLastWriteTimeUtc(file)` descending, and take only the top `maxTranscripts` files so older transcripts are never read or parsed unnecessarily.
3. **UI & CLI Controls:**
   - In `Transcripts.razor` (and Fleet Analysis header), display a **Recent Window** selector (`25`, `50` [default], `100`, `All`) showing `"Showing latest N of M transcripts"`.
   - In `ncf transcript` (`TranscriptCommand.cs`), add `--limit <N>` (default `50`, `0` for all) and update CLI output to group recommendations by `[User Codebase]` vs `[NightCodeForge]` and print the top friction files, tools, failing/hanging bash commands, and compiler error codes.

---

### 2.4 UI Presentation Updates (`NightCodeForge.Web`)

1. **Presentation Helper (`TranscriptPresentationHelper`):**
   - Extract any fleet aggregation, recommendation grouping, and badge formatting out of `.razor` `@code` blocks into a testable helper/service in `NightCodeForge.Web/Services` (per `AGENTS.md` UI/Presentation separation rule).
2. **`TranscriptAnalysisViewer.razor` & `FleetAnalysisDashboard.razor`:**
   - Split the Recommendations panel into two distinct, clearly labeled sections:
     - **🏗️ User Codebase Recommendations** (files to split, compiler errors to fix, failing tests).
     - **⚙️ NightCodeForge Harness & Prompt Recommendations** (prompt tuning, tool friction, compaction/KV-cache).
   - Add dedicated actionable breakdown panels (rendered when non-empty):
     - **High-Friction Files** (with `Split Candidate` & `Edit Without Read` badges when thresholds met).
     - **Tool Friction & Output Bloat** (tool name, calls, failures, failure rate %, oversized output count).
     - **Failing & Hanging Bash Commands** (command pattern, fail/hang counts, exit code).
     - **Top Compiler Errors** (diagnostic code badge, count, sample message).
     - **Loops, Dead-End Searches & Latency Spikes** (repeated identical tool calls, dead-end search count, test-oscillation indicator, and slow `[ mm:ss ]` tool/LLM turns).
3. **Cross-Agent Synergy (`Crucible` $\rightarrow$ `Nightwatch` & `Ideation`):**
   - Expose a lightweight helper on `ITranscriptAnalysisService` (e.g., `GetWorkspaceFrictionHotspotsAsync`) that aggregates top `IsSplitCandidate` files and recurring `UserCodebase` compiler/test friction across the recent window so:
     - **Nightwatch (`NightwatchService` & `INightwatchIdleAnalysisService`):** Receives deterministic friction breakdowns directly during reactive stage failure triage ([Step 12.34A](STEP_12_34A_NIGHTWATCH_DOMAIN_LEDGER_AND_ORCHESTRATOR_REACTIVE_TRIAGE.md)) and idle deep analysis ([Step 12.35B](STEP_12_35B_NIGHTWATCH_IDLE_DEEP_ANALYSIS_AND_LEGACY_DIAGNOSTIC_REPLACEMENT.md)).
     - **Ideation (`IIdeationConversationCoordinator`, [Step 12.31](STEP_12_31_INTERACTIVE_IDEATION_AGENT_COORDINATOR_AND_WARM_EXECUTION_CONTEXT.md)):** Injects a compact `<workspace_friction_hotspots>` hint when present so the Ideation Agent knows which files in the repository are experiencing high edit churn or split-candidate pressure when planning new tasks with the developer.

---

## 3. Definition of Done (DoD) Checklist

- [ ] Add `RecommendationTarget`, `FrictionCategory`, `FrictionRecommendation`, `FileFrictionEntry`, `ToolFrictionEntry`, `BashCommandFrictionEntry`, `CompilerErrorEntry`, `RepeatedToolLoopEntry`, and `TranscriptBehavioralSignals` to `NightCodeForge.Core` and update `TranscriptAnalysis`.
- [ ] Implement deterministic per-file (including edit-without-read), per-tool (including oversized outputs), bash command (failure + hang/timeout), compiler error, loop/dead-end-search, test-oscillation, and `[ mm:ss ]` latency extraction in `TranscriptAnalysisService`, plus `UserCodebase` vs `NightCodeForge` recommendation categorization.
- [ ] Add `maxTranscripts` recent-window limiting (pre-parse sort + slice, default `50`, preserving `TotalAvailableCount` for `"Showing latest N of M transcripts"`) across `ITranscriptAnalysisService`, `ITranscriptBrowserService`, `Transcripts.razor`, and `TranscriptCommand.cs` (`--limit`).
- [ ] Update `TranscriptAnalysisViewer.razor`, `FleetAnalysisDashboard.razor`, and `TranscriptPresentationHelper` to display categorized recommendations and the actionable breakdown panels.
- [ ] Update and expand unit/component tests in `NightCodeForge.Cli.Tests` and `NightCodeForge.Web.Tests`.

