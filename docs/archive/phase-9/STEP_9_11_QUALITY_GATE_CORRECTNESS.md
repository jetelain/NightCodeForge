# 🎯 Step 9.11: Quality Gate Correctness & Metric Fidelity

> *Parent:* [Phase 9](../09_CODEBASE_REMEDIATION_AND_ROBUSTNESS.md) | *Source:* pre-Phase-10 foundation audit | *Status:* Completed

## 1. Problem

Phase 10 builds on the deterministic gates (ratchets, coverage, test counts). The audit found cases where a gate can pass incorrectly, record a wrong baseline, or drop multi-project code coverage from Coverlet.

## 2. Findings

| # | Location | Issue | Severity |
| :-- | :--- | :--- | :--- |
| Q1 | `DeterministicTestStage` (~L125) | `TestsBaselineTotal` is set from the **current** run if no baseline exists, even when that run failed or crashed. A broken first run (e.g. 0 tests) becomes the ratchet floor. The baseline should come only from a green run on the base branch. | High |
| Q2 | `DeterministicTestStage` | Exit code 0 with `totalTests == 0` returns `Success`. A runner that discovers nothing (wrong filter, build issue) passes silently, which is the "silent success" the policy forbids. | High |
| Q3 | `DeterministicTestStage` (coverage) | Recursive `*coverage*.xml` glob in `workDir` can pick up stale files from earlier runs or other projects. Files are not ordered or timestamp-filtered, and the first parseable file wins. Coverage should be taken from artifacts produced by this run, then merged or the minimum taken. | High |
| Q4 | `DeterministicTestStage` | Regex parsing of `dotnet test` output depends on locale and format (`int.TryParse` result is ignored, so parse failure becomes 0). A failed parse with a non-zero exit code reports "0 failed test(s)". | Medium |
| Q5 | `DeterministicTestStage` | `FailedTests` is capped at `Take(10)` with no marker that it was truncated. | Low |
| Q6 | `DeterministicBuildStage` (~L97-102) | The next-stage profile lookup falls back to `AgentProfiles.FirstOrDefault()`, a synthetic fallback that violates the "hard error on missing entity" rule and can preload the wrong model. | Medium |
| Q7 | `DeterministicBuildStage` L113 | `_ = PreloadModelAsync(..., "15m", CancellationToken.None)` is fire-and-forget. Exceptions are unobserved, it is not cancellable on shutdown, and `"15m"` is a magic string. | Medium |
| Q8 | `DeterministicTestStage` / `BuildStage` | `StageCommand` and `StageOutput` are stored unbounded in the DataBag, which is persisted to `PipelineDataJson` (see Step 9.9). Large build logs can bloat the row. | Medium |
| Q9 | `DeterministicCoverageGate.cs` L76-87 | Coverage regression check is silently bypassed if `CoverageBaselineLineRate` is missing because no coverage XML was generated on the initial run, defeating the gate entirely. Also uses raw string literal `"dotnet test --no-build"`. Redundant test runs: both `test` and `coverage` ran the complete test suite consecutively. | High |
| Q10 | `DeterministicSastStage.cs` L25/36 | Conflated formatting with SAST. Standard SDKs (e.g., .NET, Rust, Go) do not provide a built-in SAST tool out of the box. Asking an LLM to reformat code wastes tokens. SAST is an external extension point (e.g. SonarQube, Semgrep), while formatting should be applied automatically. | Medium |
| Q11 | `DeterministicTestStage.cs` (~L130-150) / `DeterministicCoverageGate.cs` | **Lack of Robust Coverlet Support & Multi-Project Aggregation:** The coverage parser only parses single-file Cobertura XML and halts on the first encountered file (`break`). In multi-project solutions (where `coverlet.collector` produces separate reports per test project in `TestResults/{guid}/coverage.cobertura.xml`), it picks an arbitrary project report and ignores the rest. It also lacks support for Coverlet console stdout table parsing, Coverlet OpenCover (`coverage.opencover.xml`), and Coverlet JSON (`coverage.json`). | High |
| Q12 | Synthetic Conversation Telemetry | Composite deterministic gates previously emitted a single generic turn. Sub-stage commands and outputs (build, test, format, sast) were not individually visible in the synthetic conversation. | Medium |

## 3. Deliverables

1. **Baseline policy:** record `TestsBaselineTotal` and `CoverageBaselineLineRate` only on a successful run (`res.Success && failedTests == 0`) and `totalTests > 0`.
2. **Zero-tests policy:** if the runner exits 0 but reports 0 tests, fail the stage unless the profile or workspace configuration explicitly declares `AllowNoTests`.
3. **Stale artifact elimination:** scan only coverage files newer than the stage start time (`file.LastWriteTimeUtc >= stageStartTimeUtc - 2s`), or run the test command with a dedicated isolated results directory. Parse errors are logged and treated as a gate failure when coverage is required.
4. **Comprehensive Coverlet Code Coverage Ingestion:**
   - **Multi-Project Coverage Aggregation:** Parse all discovered fresh coverage files across all test assemblies. Aggregate total valid lines (`lines-valid`) and total covered lines (`lines-covered`) across all reports to calculate the true aggregate solution line rate (`sum(covered) / sum(valid)`), rather than breaking on the first file found.
   - **Coverlet Collector & Cobertura XML:** Full support for `dotnet test --collect:"XPlat Code Coverage"` producing `TestResults/**/coverage.cobertura.xml`.
   - **Coverlet OpenCover XML:** Support OpenCover format (`/p:CoverletOutputFormat=opencover`) parsing `<Summary sequenceCoverage="..." numSequencePoints="..." visitedSequencePoints="..." />`.
   - **Coverlet JSON:** Support native Coverlet JSON output (`coverage.json`) by traversing module line visit dictionaries.
   - **Coverlet Console stdout Fallback:** Parse standard Coverlet console summary tables directly from test runner stdout (e.g. `| Total | XX.X% |`) when no coverage files are written to disk.
   - **Default Test Command with Coverage:** Configure `WorkflowDefaults.DefaultCoverageTestCommand` (`dotnet test --no-build --collect:"XPlat Code Coverage"`).
5. **Single Test Execution (`test` vs `test-with-coverage`):**
   - Eliminated duplicate test execution where `test` and `coverage` both ran the test suite.
   - Renamed `deterministic-coverage` to `deterministic-test-with-coverage` (backed by [DeterministicTestWithCoverageGate](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Workflow/Stages/DeterministicTestWithCoverageGate.cs)).
   - In composite pipelines, users use `test-with-coverage` to run tests and enforce coverage ratchets and test count anti-cheating, or replace it with `test` (`deterministic-test`) to run tests without coverage.
6. **Graceful Behavior on Missing Coverage:**
   - If coverage collection is missing or misconfigured (e.g., repository lacks `coverlet.collector`), `DeterministicTestWithCoverageGate` emits a clear, visible warning and passes without blocking the pipeline.
   - If coverage baseline exists and current coverage regresses, it strictly fails.
7. **Dedicated Automated Code Formatting Stage (`deterministic-format`):**
   - Added [DeterministicFormatStage](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Workflow/Stages/DeterministicFormatStage.cs) to automatically apply code formatting (e.g. `dotnet format`, `ruff format .`, `cargo fmt`, `gofmt -w .`) before compilation and testing.
   - Relieves the LLM agent and code reviewer from mechanical reformatting chores.
   - Skips cleanly with an informational message if no formatter command is configured for the stack.
8. **SAST as an Optional Extension Point (`deterministic-sast`):**
   - Decoupled formatting from SAST. Standard SDKs do not include a SAST analyzer by default; SAST is intended for dedicated security tools (SonarQube, Semgrep, Trivy).
   - `CodeStackPresets` default `SastCommand` to `null`.
   - [DeterministicSastStage](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Workflow/Stages/DeterministicSastStage.cs) skips gracefully with an informational log when no command is configured. When configured, it enforces exit code 0.
9. **Updated Default Workflow Sequences:**
   - Pre-Implementation Baseline: `Stages = "build,test-with-coverage"`
   - Post-Implementation Gates: `Stages = "format,build,test-with-coverage,sast"`
10. **Verbatim Command & Output Telemetry in Synthetic Conversations:**
    - [CompositeDeterministicStage](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Workflow/Stages/CompositeDeterministicStage.cs) records every executed sub-command via [StageCommandExecution](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Workflow/StageCommandExecution.cs) in `PipelineDataBagKeys.StageCommands`.
    - [TaskOrchestrator](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Workflow/TaskOrchestrator.cs) creates distinct `StageTurnRole.Tool` conversation turns for each command launched, making all sub-stage commands and outputs directly visible in the UI.
11. **Structured Test Diagnostics:** Replace brittle regex parsing with structured sources where possible (`--logger trx` or TRX files) via [TestDiagnosticsParser](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Workflow/TestDiagnosticsParser.cs) and fall back to regex with locale-invariant parsing. Log when parsing fails instead of defaulting to 0.
12. **Failure Truncation Clarity:** Mark truncated failure lists (`"… and N more"`) and add a `FailedTestsTruncated` flag.
13. **Eliminate Profile Fallback:** Remove `FirstOrDefault()` profile fallback. Throw `AgentProfileNotFoundException` or skip the preload with an explicit log.
14. **Tracked Model Preload:** Introduce `WorkflowDefaults.ModelPreloadKeepAlive` (`"15m"`), run through a tracked background helper that observes exceptions, and honour the application stopping token.
15. **Bounded Log Persistence:** Cap persisted `StageOutput` (tail-truncate to `WorkflowDefaults.MaxStageOutputChars` [32 KB], with full logs kept in conversation turns).
16. **Targeted Tests:** Add unit tests covering Coverlet multi-project aggregation, Cobertura XML, OpenCover XML, Coverlet JSON, and Coverlet console summary table parsing in [QualityGateCorrectnessTests](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/tests/NightCodeForge.Core.Tests/QualityGateCorrectnessTests.cs).

## 4. Definition of Done

- [x] A failing or empty first run never creates a baseline.
- [x] Exit 0 with zero tests fails the gate unless explicitly allowed (`AllowNoTests`).
- [x] Stale coverage files cannot influence the metric.
- [x] Coverlet code coverage fully supported across Cobertura, OpenCover, JSON, and stdout console tables.
- [x] Multi-test-project solutions aggregate coverage lines across all projects rather than picking the first file.
- [x] Coverage gate emits graceful warning if coverage is unmeasured, and fails on regression.
- [x] `deterministic-coverage` renamed to `deterministic-test-with-coverage` to avoid double test suite execution.
- [x] `deterministic-format` stage directly applies code formatting before build/test.
- [x] `deterministic-sast` serves as an optional security extension point and skips gracefully when unconfigured.
- [x] Sub-stage commands and outputs in composite gates are individually captured and rendered as tool turns in synthetic conversations.
- [x] No fire-and-forget without exception observation.
- [x] `dotnet test --filter "Category!=Acceptance&Category!=E2E"` passes (all 954 unit and integration tests passing).
