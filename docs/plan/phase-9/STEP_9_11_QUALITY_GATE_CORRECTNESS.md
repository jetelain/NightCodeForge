# 🎯 Step 9.11: Quality Gate Correctness & Metric Fidelity

> *Parent:* [Phase 9](../09_CODEBASE_REMEDIATION_AND_ROBUSTNESS.md) | *Source:* pre-Phase-10 foundation audit | *Status:* Planned

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
| Q9 | `DeterministicCoverageGate.cs` L76-87 | Coverage regression check is silently bypassed if `CoverageBaselineLineRate` is missing because no coverage XML was generated on the initial run, defeating the gate entirely. Also uses raw string literal `"dotnet test --no-build"`. | High |
| Q10 | `DeterministicSastStage.cs` L25/36 | Defaults to `"echo 'SAST check completed.'"` (a synthetic passing check). If SAST is included in a workflow definition without explicit command, it silently passes without executing any actual analysis. | Medium |
| Q11 | `DeterministicTestStage.cs` (~L130-150) / `DeterministicCoverageGate.cs` | **Lack of Robust Coverlet Support & Multi-Project Aggregation:** The coverage parser only parses single-file Cobertura XML and halts on the first encountered file (`break`). In multi-project solutions (where `coverlet.collector` produces separate reports per test project in `TestResults/{guid}/coverage.cobertura.xml`), it picks an arbitrary project report and ignores the rest. It also lacks support for Coverlet console stdout table parsing, Coverlet OpenCover (`coverage.opencover.xml`), and Coverlet JSON (`coverage.json`). | High |

## 3. Deliverables

1. **Baseline policy:** record `TestsBaselineTotal` and `CoverageBaselineLineRate` only on a successful run (`res.Success && failedTests == 0`) and `totalTests > 0`.
2. **Zero-tests policy:** if the runner exits 0 but reports 0 tests, fail the stage unless the profile explicitly declares `AllowNoTests`.
3. **Stale artifact elimination:** scan only coverage files newer than the stage start time (`file.LastWriteTimeUtc >= stageStartTimeUtc`), or run the test command with a dedicated isolated results directory. Parse errors are logged and treated as a gate failure when coverage is required.
4. **Comprehensive Coverlet Code Coverage Ingestion:**
   - **Multi-Project Coverage Aggregation:** Parse all discovered fresh coverage files across all test assemblies. Aggregate total valid lines (`lines-valid`) and total covered lines (`lines-covered`) across all reports to calculate the true aggregate solution line rate (`sum(covered) / sum(valid)`), rather than breaking on the first file found.
   - **Coverlet Collector & Cobertura XML:** Full support for `dotnet test --collect:"XPlat Code Coverage"` producing `TestResults/**/coverage.cobertura.xml`.
   - **Coverlet OpenCover XML:** Support OpenCover format (`/p:CoverletOutputFormat=opencover`) parsing `<Summary sequenceCoverage="..." numSequencePoints="..." visitedSequencePoints="..." />`.
   - **Coverlet JSON:** Support native Coverlet JSON output (`coverage.json`) by traversing module line visit dictionaries.
   - **Coverlet Console stdout Fallback:** Parse standard Coverlet console summary tables directly from test runner stdout (e.g. `| Total | XX.X% |`) when no coverage files are written to disk.
   - **Default Test Command with Coverage:** Configure `WorkflowDefaults.DefaultCoverageTestCommand` (e.g. `dotnet test --no-build --collect:"XPlat Code Coverage"`) and ensure `DeterministicCoverageGate` uses this default instead of raw `dotnet test --no-build`.
5. **No Silent Bypass on Missing Coverage:** If `DeterministicCoverageGate` is in the workflow pipeline and no coverage metric can be extracted from Coverlet or any test output, the gate fails fast rather than silently passing.
6. **Real SAST Execution:** Replace synthetic `"echo 'SAST check completed.'"` in `DeterministicSastStage` with stack-resolved SAST commands from `CodeStackPresets` (e.g. `dotnet format --verify-no-changes`, `ruff check .`, `npm run lint`) or fail if SAST is required but unconfigured.
7. **Structured Test Diagnostics:** Replace brittle regex parsing with structured sources where possible (`--logger trx` or `--report-trx`) and fall back to regex with locale-invariant parsing. Log when parsing fails instead of defaulting to 0.
8. **Failure Truncation Clarity:** Mark truncated failure lists (`"… and N more"`) and add a `FailedTestsTruncated` flag.
9. **Eliminate Profile Fallback:** Remove `FirstOrDefault()` profile fallback. Throw `AgentProfileNotFoundException` or skip the preload with an explicit log.
10. **Tracked Model Preload:** Introduce `WorkflowDefaults.ModelPreloadKeepAlive`, run through a tracked background helper that observes exceptions, and honour the application stopping token.
11. **Bounded Log Persistence:** Cap persisted `StageOutput` (tail-truncate to `WorkflowDefaults.MaxStageOutputChars`, with the full log kept in the transcript or turn record).
12. **Targeted Tests:** Add unit tests covering Coverlet multi-project aggregation, Cobertura XML, OpenCover XML, Coverlet JSON, and Coverlet console summary table parsing.

## 4. Definition of Done

- [ ] A failing or empty first run never creates a baseline.
- [ ] Exit 0 with zero tests fails the gate.
- [ ] Stale coverage files cannot influence the metric.
- [ ] Coverlet code coverage fully supported across Cobertura, OpenCover, JSON, and stdout console tables.
- [ ] Multi-test-project solutions aggregate coverage lines across all projects rather than picking the first file.
- [ ] Coverage gate fails if coverage is required but unmeasured (no silent bypass).
- [ ] SAST stage executes real analyzers instead of synthetic echo commands.
- [ ] No fire-and-forget without exception observation.
- [ ] `dotnet test` passes (unit, integration and E2E).
