# 📊 Step 12.35A: Deterministic Transcript Friction Categorization, Actionable Breakdowns & Recent-Session Windowing

> **Feature & Diagnostics Specification (Part 1 of 2 — Deterministic Friction Engine & UI/CLI Windowing)**  
> *Parent Plan:* [Phase 12 Master Plan](../12_EXTEND_FEATURES.md)  
> *Companion Step:* [Step 12.35B: Idle-GPU Nightwatch Deep Transcript & Workspace Analysis (Replacing Legacy AI Diagnostics)](../../features/STEP_12_35B_NIGHTWATCH_IDLE_DEEP_ANALYSIS_AND_LEGACY_DIAGNOSTIC_REPLACEMENT.md)  
> *Related Steps:* [Step 12.34C: Nightwatch Telemetry UI & Harness Issue Ledger](../../features/STEP_12_34C_NIGHTWATCH_TELEMETRY_HARNESS_ISSUES_UI_AND_SELF_HEALING_BACKLOG.md)  
> *Source Policy:* [AGENTS.md](../../../AGENTS.md) | [docs/ARCHITECTURE.md](../../ARCHITECTURE.md)  
> *Target Projects:* `src/NightCodeForge.Core`, `src/NightCodeForge.Agents`, `src/NightCodeForge.Web`, `src/NightCodeForge.Cli`, `tests/NightCodeForge.Core.Tests`, `tests/NightCodeForge.Agents.Tests`, `tests/NightCodeForge.Cli.Tests`, `tests/NightCodeForge.Web.Tests` | *Status:* Completed | *Target Framework:* .NET 10 (C# 13)

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

1. **Recommendation Target Enum & Structured Record (`TranscriptAnalysisModels.cs`):**
   - Replace (clean break, per `AGENTS.md`) the untyped `IReadOnlyList<string> Recommendations` on `TranscriptAnalysis` with `IReadOnlyList<FrictionRecommendation> Recommendations` (and convenience filtered views `UserCodebaseRecommendations` and `NightCodeForgeRecommendations`) categorized by `RecommendationTarget`:
     - `RecommendationTarget.UserCodebase` — Actionable improvements to the target repository (e.g., splitting oversized/high-churn files, resolving recurring compiler diagnostics or test failures, fixing build/test scripts).
     - `RecommendationTarget.NightCodeForge` — Actionable improvements to NightCodeForge's harness, stage prompts, tool schemas, context compaction, or KV-cache prefix stability.
   - Each `FrictionRecommendation` includes:
     - `Target` (`RecommendationTarget`)
     - `Category` (`FrictionCategory`: `FileArchitecture`, `Tooling`, `ShellCommand`, `CompilerDiagnostic`, `TestSuite`, `PromptOrStage`, `ContextAndCache`)
     - `Title` (`string`)
     - `ActionableDetail` (`string` — concrete next step referencing the specific file, tool, command, or error code)
     - `Subject` (`string? = null` — e.g., file path, tool name, command prefix, or diagnostic code for grouping/deduplication across the fleet)
2. **Deterministic Attribution Rules:**
   - **User Codebase (`UserCodebase`):**
     - High-churn / high-edit-failure / repeatedly re-read source files (`IsSplitCandidate`: `EditCount >= 3 || EditFailureCount >= 2 || ConsecutiveReReadCount >= 3` without intervening edits on a source file) $\rightarrow$ `FrictionCategory.FileArchitecture`: recommend splitting/modularizing that specific file path (`Subject = FilePath`).
     - Recurring compiler error codes (e.g., `CS0117`, `CS0246`, `CS1061`, `MSBxxxx`, TypeScript `TSxxxx`) $\rightarrow$ `FrictionCategory.CompilerDiagnostic`: surface the top codes, sample messages, and affected files (`Subject = DiagnosticCode`).
     - Failing test suites / specific test names (especially when failing tests oscillate or repeat across runs) $\rightarrow$ `FrictionCategory.TestSuite`: list failing test identifiers (`Subject = TestName`).
   - **NightCodeForge (`NightCodeForge`):**
     - `ranStaleTest` (`dotnet test --no-build` immediately after source edits without a preceding `dotnet build`) $\rightarrow$ `FrictionCategory.PromptOrStage`: prompt/stage directive improvement for NCF.
     - `HistoryRewrites > 10` $\rightarrow$ `FrictionCategory.ContextAndCache`: NCF middleware / reasoning pruning KV-cache invalidation.
     - `CompactionEvents > 5` $\rightarrow$ `FrictionCategory.ContextAndCache`: NCF context budget / compaction tuning.
     - Code-review stage `git diff` exploration overhead (`gitDiffCommands >= 3 && editedFiles.Count <= 1` on `IsCodeReviewStage`) $\rightarrow$ `FrictionCategory.PromptOrStage`: NCF stage prompt pre-injection.
     - Edit mismatches (`EditFailures > 0`, especially `EditedWithoutReadCount > 0`), high failure rate on a specific canonical tool (e.g., `read` path errors), or malformed/truncated tool calls $\rightarrow$ `FrictionCategory.Tooling`: NCF tool ergonomics / prompt guidance / output parsing.
     - Hanging or timed-out bash commands (`HangOrTimeoutCount > 0`) $\rightarrow$ `FrictionCategory.ShellCommand`: NCF non-interactive flags / command timeout guardrails.
     - Oversized tool outputs (`> 10,000` chars) driving premature compaction $\rightarrow$ `FrictionCategory.Tooling`: NCF CLI output filtering (e.g., Step 12.6) or tool truncation limits.
     - Slow LLM inference turns (`> 30s` User/Tool $\rightarrow$ Assistant delta) or slow tool executions (`> 60s`) $\rightarrow$ `FrictionCategory.ContextAndCache` / `FrictionCategory.Tooling`: NCF model preloading, KV-cache stability, or command scoping.
     - Repeated identical tool call loops or dead-end searches (`>= 3`) $\rightarrow$ `FrictionCategory.PromptOrStage`: NCF prompt directives or symbol-map/repo-context pre-injection.

---

### 2.2 Actionable Deterministic Friction Breakdowns (`TranscriptAnalysisService`)

Extend `TranscriptAnalysis` with deterministic, structured breakdown collections (extracted purely via regex/parsing, zero LLM calls; default to empty collections/signals when not explicitly supplied in test constructors).

- **Section Boundary & Non-Double-Counting Rule:**
  1. Before extracting turn, tool, file, bash, compiler, and test metrics, split the raw markdown at `### Model calls` (and strip `### Environment` and `### MAF Pipeline Configuration` sections from the conversation body) so that tool invocations, outputs, compiler diagnostics, and exit codes are only counted once from the primary conversation turns—never double-counted from `### Model calls` request snapshots.
  2. Evaluate `HistoryRewriteRegex()` against the `### Model calls` section when present, falling back to full `content` only when a minimal test snippet omits the `### Model calls` section header (preserving compatibility with existing unit tests that write `#### [ 00:03 ] Call 3` directly).
  3. **Three-Pattern Turn Correlation (Zero Double-Counting):** `SessionTranscriptService`, `CrucibleConversationCoordinator`, `IdeationService`, and existing tests emit tool calls in three structural patterns:
     - *Pattern A (`ASSISTANT` $\rightarrow$ `TOOL`):* `### [MM:SS] 🤖 ASSISTANT` contains `**Tool Invocation:** \`name\`` + ```` ```json { ... } ``` ````, followed by `### [MM:SS] 🔧 TOOL` echoing `**Tool Invocation:** \`name\`` + `**Tool Output:**`.
     - *Pattern B (`TOOL` $\rightarrow$ `TOOL_RESULT` / `TOOL_ERROR`):* `### [MM:SS] 🔧 TOOL` (or `🤖 ASSISTANT`) contains `**Tool Invocation:** \`name\`` + JSON args, followed by `### [MM:SS] ↳ TOOL_RESULT` or `❌ TOOL_ERROR` containing `**Tool:** \`name\`` + fenced output (and a trailing `**Tool Invocation:** \`name\`` line emitted by `SessionTranscriptService`).
     - *Pattern C (Single Inline `TOOL` Turn):* `### [MM:SS] 🔧 TOOL` contains both `**Tool Invocation:** \`name\`` + JSON args and `**Tool Output:**` in the same turn.
     Correlate each invocation with its output across all three patterns without double-counting `ToolUsage` or `InvocationCount` when `**Tool Invocation:**` is echoed inside the result turn:

1. **File Friction Breakdown (`IReadOnlyList<FileFrictionEntry> FileFriction`):**
   - Track per-file (accepting both canonical `read`/`edit`/`write` and `ReadFile`/`EditFile`/`WriteFile` tool names):
     - `FilePath` (`string`), `ReadCount` (`int`), `EditCount` (`int`), `EditFailureCount` (`int`), `EditedWithoutReadCount` (`int` — edits attempted on a file before any prior `read` of that file in the session), `ConsecutiveReReadCount` (`int` — max consecutive reads of the same file without an intervening edit to that file), and computed `IsSplitCandidate` (`!IsNonSourceFile(FilePath) && (EditCount >= 3 || EditFailureCount >= 2 || ConsecutiveReReadCount >= 3)`).
   - Ordered by `(EditFailureCount * 3 + EditCount * 2 + ReadCount)` descending, capped to top entries per session (e.g., `15`) and aggregated across the fleet.
2. **Tool Friction Breakdown (`IReadOnlyList<ToolFrictionEntry> ToolFriction`):**
   - Correlate each tool invocation with its corresponding `TOOL_RESULT` / `TOOL_ERROR` / `**Tool Output:**` block to track per-tool:
     - `ToolName` (`string`), `InvocationCount` (`int`), `FailureCount` (`int`), `FailureRatePercent` (`double`), `OversizedOutputCount` (`int`, output `> 10,000` chars), and `SampleError` (`string?`, truncated first error line).
   - Surface tools with `FailureCount > 0` or `OversizedOutputCount > 0`, ordered by `FailureCount` descending then `OversizedOutputCount` descending.
3. **Bash Command Friction Breakdown (`IReadOnlyList<BashCommandFrictionEntry> BashCommandFriction`):**
   - Correlate `bash` tool invocations with their exit codes, output sizes, and timeout/hang markers (non-zero `Exit Code: N`, `timed out`, `sentinel timeout`, `SIGINT`, killed/hung process indicators).
   - Normalize commands to a stable signature (leading command + primary subcommand such as `dotnet test`, `dotnet build`, `git diff`, `git status`, `npm run build`, `csnav`, stripping transient paths/flags) and record:
     - `CommandPattern` (`string`), `ExecutionCount` (`int`), `FailureCount` (`int`), `HangOrTimeoutCount` (`int`), `OversizedOutputCount` (`int`), `LastExitCode` (`int?`).
4. **Compiler Error Breakdown (`IReadOnlyList<CompilerErrorEntry> CompilerErrorBreakdown`):**
   - Extract diagnostic codes (C# `CS\d+`, MSBuild `MSB\d+`, NuGet `NU\d+`, TypeScript `TS\d+`) along with a normalized message snippet and optional source file path when present on the diagnostic line.
   - Group by `DiagnosticCode` into `CompilerErrorEntry(string DiagnosticCode, int Count, string SampleMessage, string? SampleFilePath = null)`, ordered by `Count` descending.
5. **Loop, Search & Test-Oscillation Signals (`TranscriptBehavioralSignals BehavioralSignals`):**
   - **Repeated Identical Tool Loops:** Detect `(ToolName, NormalizedArgs)` invoked `>= 3` times in a session (`IReadOnlyList<RepeatedToolLoopEntry> RepeatedToolLoops`, where `RepeatedToolLoopEntry(string ToolName, string NormalizedArgsSummary, int RepeatCount)`).
   - **Dead-End Searches:** Count `grep`/`glob`/`bash` search invocations returning zero matches and `read` invocations returning file-not-found (`int DeadEndSearchCount`).
   - **Test-Fix-Test Oscillation:** Count test-runner cycles (`int TestRunCycles`), collect distinct `IReadOnlyList<string> FailingTests`, and detect whether failing test runs repeated/oscillated across `>= 2` failed test runs (`bool HasTestOscillation`).
   - **Self-Reverted Edits & Malformed Turns:** Count `git checkout -- <file>` / `git restore` self-reverts (`int SelfRevertCount`) and malformed/unparsed or truncated tool calls (`int MalformedOrTruncatedToolCount`).
6. **Turn & Tool Latency Breakdown (from `[ mm:ss ]` Turn Offsets):**
   - Parse consecutive `### [ mm:ss ]` turn header offsets (computing `totalSeconds = minutes * 60 + seconds` directly via integer parsing rather than `TimeSpan.Parse`, since `SessionTranscriptService.FormatOffset` emits unbounded total minutes such as `[ 75:12 ]` for runs exceeding an hour) into `SlowTurnEntry(string StartOffset, string EndOffset, string Label, int DurationSeconds)` collections on `TranscriptBehavioralSignals`:
     - **Slow Tool Executions (`IReadOnlyList<SlowTurnEntry> SlowToolExecutions`):** Tool invocation $\rightarrow$ tool result deltas exceeding `60s` (`TranscriptAnalysisDefaults.SlowToolExecutionThresholdSeconds = 60`), attributed to the tool/command.
     - **Slow LLM Inference Turns (`IReadOnlyList<SlowTurnEntry> SlowLlmTurns`):** User/Tool result $\rightarrow$ Thinking/Assistant/Tool invocation deltas exceeding `30s` (`TranscriptAnalysisDefaults.SlowLlmTurnThresholdSeconds = 30`), flagging potential cold-loads or KV-cache misses.

---

### 2.3 Recent-Transcript Windowing & Limit

1. **Default Recency Cap & Total-Count Metadata (`TranscriptAnalysisDefaults` & `TranscriptWindowResult<T>`):**
   - Define `TranscriptAnalysisDefaults` in `NightCodeForge.Core.Services` with named constants (`DefaultRecentLimit = 50`, `OversizedToolOutputCharThreshold = 10_000`, `SlowToolExecutionThresholdSeconds = 60`, `SlowLlmTurnThresholdSeconds = 30`, and `WindowPresets = [25, 50, 100]`).
   - Define `TranscriptWindowResult<T>(IReadOnlyList<T> Items, int TotalAvailableCount, int? AppliedLimit = null)` in `NightCodeForge.Core.Services` so callers receive both the windowed slice (`N = Items.Count`) and the pre-slice total candidate transcript count (`M = TotalAvailableCount`) from a single directory scan.
   - Support `maxTranscripts` (default `TranscriptAnalysisDefaults.DefaultRecentLimit = 50`, with `null` or `<= 0` meaning unlimited/`All`) across:
     - `ITranscriptAnalysisService.AnalyzeDirectoryAsync(string directoryPath, int? maxTranscripts = TranscriptAnalysisDefaults.DefaultRecentLimit, CancellationToken cancellationToken = default)` (plus overload `AnalyzeDirectoryAsync(string directoryPath, CancellationToken cancellationToken)` and `AnalyzeDirectoryWindowAsync(string directoryPath, int? maxTranscripts = TranscriptAnalysisDefaults.DefaultRecentLimit, CancellationToken cancellationToken = default)`)
     - `ITranscriptBrowserService.GetTranscriptsAsync(string? workspacePath = null, int? maxTranscripts = TranscriptAnalysisDefaults.DefaultRecentLimit, CancellationToken cancellationToken = default)` and `GetTranscriptsWindowAsync(string? workspacePath = null, int? maxTranscripts = TranscriptAnalysisDefaults.DefaultRecentLimit, CancellationToken cancellationToken = default)`
     - `ITranscriptBrowserService.GetFleetAnalysisAsync(string? workspacePath = null, int? maxTranscripts = TranscriptAnalysisDefaults.DefaultRecentLimit, CancellationToken cancellationToken = default)` and `GetFleetAnalysisWindowAsync(string? workspacePath = null, int? maxTranscripts = TranscriptAnalysisDefaults.DefaultRecentLimit, CancellationToken cancellationToken = default)`
2. **Fast Pre-Parse Ordering & Candidate Discovery:**
   - Before reading full file contents, discover candidate `*.md` files across the target directory (or workspace candidate directories), deduplicate by file name (case-insensitive), exclude obvious non-transcript root doc files (e.g. `AGENTS.md`, `README.md`), sort candidates by parsed filename date (`yyyy-MM-dd`), `task-<id>` descending, `run<N>` descending, and `File.GetLastWriteTimeUtc(file)` descending, record `TotalAvailableCount` (`M`), and read/parse at most `maxTranscripts` valid transcripts (`N`) so older historical transcripts are never read or parsed unnecessarily.
   - In `TranscriptBrowserService.GetCandidateDirectories(string? workspacePath)`, when an explicit `workspacePath` is provided and at least one workspace transcript directory exists (`docs/transcripts`, `.nightcodeforge/transcripts`, or `.nightcodeforge/worktrees/*/docs/transcripts`), scope discovery to that workspace's directories rather than mixing in fallback `Directory.GetCurrentDirectory()` / `AppDomain.CurrentDomain.BaseDirectory` transcripts.
3. **UI & CLI Controls:**
   - In `Transcripts.razor` (and `FleetAnalysisDashboard.razor` header), display a **Recent Window** selector (`25`, `50` [default], `100`, `All`) alongside a `"Showing latest N of M transcripts"` badge; changing the window reloads the windowed session list and fleet analysis.
   - In `ncf transcript` (`TranscriptCommand.cs` & `Program.ParseTranscriptOptions`), add `--limit <N>` / `--limit=<N>` (default `50`, `0` for all), display `"Showing latest N of M transcript(s)"` when `TotalAvailableCount > Items.Count`, group CLI recommendations under `[User Codebase]` vs `[NightCodeForge]`, and print the top friction files, tools, failing/hanging bash commands, and compiler error codes.

---

### 2.4 UI Presentation Updates (`NightCodeForge.Web`) & Cross-Agent Synergy

1. **Presentation Helper (`TranscriptPresentationHelper`):**
   - Extract fleet aggregation (merging `FileFriction`, `ToolFriction`, `BashCommandFriction`, `CompilerErrorBreakdown`, `BehavioralSignals`, and deduplicating `FrictionRecommendation`s by `(Target, Category, Subject ?? Title)` with occurrence counts), recommendation grouping, and badge formatting out of `.razor` `@code` blocks into a unit-testable static/service helper `TranscriptPresentationHelper` in `NightCodeForge.Web/Services` (per `AGENTS.md` UI/Presentation separation rule).
2. **`TranscriptAnalysisViewer.razor` & `FleetAnalysisDashboard.razor`:**
   - Split the Recommendations panel into two distinct, clearly labeled sections:
     - **🏗️ User Codebase Recommendations** (files to split, compiler errors to fix, failing tests).
     - **⚙️ NightCodeForge Harness & Prompt Recommendations** (prompt tuning, tool friction, compaction/KV-cache).
   - Add dedicated actionable breakdown panels (rendered when non-empty):
     - **High-Friction Files** (showing reads, edits, edit failures, and `Split Candidate` / `Edited Without Read` badges when thresholds are met).
     - **Tool Friction & Output Bloat** (tool name, calls, failures, failure rate %, oversized output count, sample error).
     - **Failing & Hanging Bash Commands** (command pattern, executions, fail/hang counts, oversized outputs, last exit code).
     - **Top Compiler Errors** (diagnostic code badge, count, sample message/file).
     - **Loops, Dead-End Searches & Latency Spikes** (repeated identical tool calls, dead-end search count, test-oscillation badge, self-revert count, and slow `[ mm:ss ]` tool/LLM turns).
3. **Cross-Agent Synergy (`Crucible` $\rightarrow$ `Ideation` & `Nightwatch`):**
   - Expose `GetWorkspaceFrictionHotspotsAsync(string workspacePath, int? maxTranscripts = TranscriptAnalysisDefaults.DefaultRecentLimit, CancellationToken cancellationToken = default)` on `ITranscriptAnalysisService` returning a `WorkspaceFrictionHotspots` record (containing top `IsSplitCandidate` files, top recurring `CompilerErrorEntry` items, recurring failing tests, and a compact `FormatPromptDigest()` / `<workspace_friction_hotspots>` XML builder that returns `string.Empty` when no hotspots exist).
   - Register `services.TryAddSingleton<ITranscriptAnalysisService, TranscriptAnalysisService>();` in `AddNightCodeForgeAgents` (`src/NightCodeForge.Agents/ServiceCollectionExtensions.cs`).
   - **Constructor Ceiling Guard (`AGENTS.md` $\le 10$ Parameters):** Because `IdeationService` currently has **10** constructor parameters (`ConstructorParameterCeilingTests.MaxConstructorParameters = 10`), do **not** add `ITranscriptAnalysisService` directly to `IdeationService`'s constructor. Instead, inject `ITranscriptAnalysisService` into `AgentWorkspaceContextService` (increasing its constructor from 6 to 7 parameters), expose `BuildFrictionHotspotsDigestAsync(string repoPath, CancellationToken cancellationToken = default)` on `IAgentWorkspaceContextService` (or include it in `IdeationService` / future `IIdeationConversationCoordinator` via `_workspaceContext`), and append the `<workspace_friction_hotspots>` section to the Ideation system prompt when non-empty so the Ideation Agent knows which files are experiencing high edit churn or split-candidate pressure when planning tasks.
   - **Nightwatch Readiness ([Step 12.34A](../../features/STEP_12_34A_NIGHTWATCH_DOMAIN_LEDGER_AND_ORCHESTRATOR_REACTIVE_TRIAGE.md) & [Step 12.35B](../../features/STEP_12_35B_NIGHTWATCH_IDLE_DEEP_ANALYSIS_AND_LEGACY_DIAGNOSTIC_REPLACEMENT.md)):** `GetWorkspaceFrictionHotspotsAsync` and `AnalyzeDirectoryWindowAsync` provide the deterministic friction digest consumed by `NightwatchService` during reactive stage failure triage and by `INightwatchIdleAnalysisService` during idle deep analysis.

---

## 3. Definition of Done (DoD) Checklist

- [x] Add `TranscriptAnalysisDefaults`, `TranscriptWindowResult<T>`, `RecommendationTarget`, `FrictionCategory`, `FrictionRecommendation`, `FileFrictionEntry`, `ToolFrictionEntry`, `BashCommandFrictionEntry`, `CompilerErrorEntry`, `RepeatedToolLoopEntry`, `SlowTurnEntry`, `TranscriptBehavioralSignals`, and `WorkspaceFrictionHotspots` to `NightCodeForge.Core`, and update `TranscriptAnalysis` (`Recommendations` typed as `IReadOnlyList<FrictionRecommendation>` plus breakdown properties).
- [x] Implement section boundary splitting (`### Model calls` / `### Environment` / `### MAF Pipeline Configuration`), 3-pattern turn correlation, and deterministic per-file (including edit-without-read and split candidates), per-tool (including oversized outputs), bash command (failure + hang/timeout), compiler error, loop/dead-end-search, test-oscillation, and `[ mm:ss ]` latency extraction in `TranscriptAnalysisService`, plus `UserCodebase` vs `NightCodeForge` recommendation categorization and `GetWorkspaceFrictionHotspotsAsync`.
- [x] Add `maxTranscripts` recent-window limiting (pre-parse sort + slice, default `50`, preserving `TotalAvailableCount` for `"Showing latest N of M transcripts"`) across `ITranscriptAnalysisService`, `ITranscriptBrowserService`, `Transcripts.razor`, `FleetAnalysisDashboard.razor`, and `TranscriptCommand.cs` / `Program.ParseTranscriptOptions` (`--limit`).
- [x] Update `TranscriptAnalysisViewer.razor`, `FleetAnalysisDashboard.razor`, and `TranscriptPresentationHelper` to display categorized recommendations (`🏗️ User Codebase` vs `⚙️ NightCodeForge`) and the actionable breakdown panels.
- [x] Register `ITranscriptAnalysisService` in `AddNightCodeForgeAgents` and wire `<workspace_friction_hotspots>` into Ideation via `IAgentWorkspaceContextService` (obeying the 10-parameter constructor ceiling tested by `ConstructorParameterCeilingTests`).
- [x] Update and expand unit/component tests across `NightCodeForge.Core.Tests`, `NightCodeForge.Agents.Tests`, `NightCodeForge.Cli.Tests`, and `NightCodeForge.Web.Tests`.

