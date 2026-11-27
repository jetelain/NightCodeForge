# 🛡️ Step 12.7A: Unified Security Findings Foundation & Diff-Scoped DevSkim SAST Backend

> **Phase 12 Implementation Step (Part 1 of 3 — Unified Security Findings Domain, Shared SARIF 2.1.0 Parser, Diff-Scoped Microsoft DevSkim CLI Backend, Stage 4 & Stage 6 Re-Run, Autonomous Loopback & Telemetry)**  
> *Parent Plan:* [Features & Integrations Roadmap](README.md)  
> *Companion Steps:* [Step 12.7B: DiffGuard CLI Security Scanner & Post-Review Guardrail Backend](STEP_12_7B_DIFFGUARD_POST_REVIEW_GUARDRAIL_BACKEND.md) | [Step 12.7C: Unified Security Review UI & Settings](STEP_12_7C_UNIFIED_SECURITY_REVIEW_AND_SETTINGS_UI.md)  
> *Source Policy:* [AGENTS.md](../../AGENTS.md)  
> *Project Target:* [`src/NightCodeForge.Core`](../../src/NightCodeForge.Core), [`src/NightCodeForge.Infrastructure`](../../src/NightCodeForge.Infrastructure), [`tests/NightCodeForge.Core.Tests`](../../tests/NightCodeForge.Core.Tests), [`tests/NightCodeForge.Infrastructure.Tests`](../../tests/NightCodeForge.Infrastructure.Tests) | *Status:* Planned | *Target Framework:* .NET 10 (C# 13)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Objective

1. **Unified Security Findings Foundation:** Define one shared security finding domain model, report schema, settings service contract, and SARIF 2.1.0 parser in `NightCodeForge.Core` so **DevSkim SAST** (Step 12.7A), **DiffGuard Guardrail** (Step 12.7B), and the **Unified Security Review & Settings UI** (Step 12.7C) share a single schema with zero duplicated models or parsers.
2. **Out-of-the-Box Deterministic SAST:** Activate the currently skipped [`DeterministicSastStage`](../../src/NightCodeForge.Core/Workflow/Stages/DeterministicSastStage.cs) by integrating **Microsoft DevSkim CLI** (`Microsoft.CST.DevSkim.CLI`) as the 100% free, offline, zero-token static application security testing analyzer across .NET C#, JavaScript, TypeScript, Python, Go, Rust, C/C++, and Java.
3. **Diff-Scoped Analysis:** Scope DevSkim analysis strictly to files added or modified in the task's Git diff against `BaseBranch` (including uncommitted worktree edits, excluding deleted files and ignored globs) so pre-existing findings in untouched repository files never cause noisy LLM loopbacks or clutter the Review Room.
4. **Stage 4 + Stage 6 Verification:** Run the `sast` sub-stage in both **Stage 4 (`PostImplementationGates`)** and **Stage 6 (`PostReviewVerification`)** so minor fixes applied by the Code Review agent in Stage 5 (`CodeReviewAndFix`) are re-verified by DevSkim before review or merge.
5. **Clear Enforcement Semantics (Autonomous LLM Loopback):**
   - DevSkim findings at or above the configured failure threshold (default `High` / DevSkim `Important`) carry `Disposition = SecurityFindingDisposition.AgentLoopback`, fail the deterministic gate, and route the task back to `StandardStageIds.Implementation` with structured remediation diagnostics for autonomous LLM repair.
   - Findings below threshold carry `Disposition = SecurityFindingDisposition.Advisory` and are persisted into the unified report for Review Room visibility (Step 12.7C) without failing the gate.
   - *(Contrast with [Step 12.7B](STEP_12_7B_DIFFGUARD_POST_REVIEW_GUARDRAIL_BACKEND.md): DiffGuard blocking points carry `Disposition = SecurityFindingDisposition.HumanReviewRequired`, never loop back to the coding LLM, and instead downgrade `MergeMode.Automatic` to `MergeMode.ManualReview` for human review.)*

---

## 2. Problem Statement & Current State

- [`DeterministicSastStage`](../../src/NightCodeForge.Core/Workflow/Stages/DeterministicSastStage.cs) is already registered in [`WorkflowStageRegistry`](../../src/NightCodeForge.Core/Workflow/WorkflowStageRegistry.cs) and wired as the `sast` sub-stage of `PostImplementationGates` (`format,build,test-with-coverage,sast`) in [`StandardWorkflowDefinitions`](../../src/NightCodeForge.Core/Domain/StandardWorkflowDefinitions.cs).
- Because all presets in [`CodeStackPresets`](../../src/NightCodeForge.Core/Domain/CodeStack.cs) currently set `SastCommand: null`, the stage skips every run with `"Deterministic SAST gate skipped: no SAST analyzer command configured..."`.
- Meanwhile, [`StandardStageIds.PostReviewVerification`](../../src/NightCodeForge.Core/Domain/StandardWorkflowDefinitions.cs#L115-L124) currently runs only `build,test-with-coverage`, leaving Stage 5 minor fixes unverified by SAST.
- Autonomous coding agents can introduce critical security regressions (e.g. `MD5.Create()`, hardcoded secrets, weak PRNGs, unescaped SQL) that compile cleanly and pass functional unit tests.
- **Why Microsoft DevSkim CLI:** MIT-licensed, completely offline (no cloud API keys or telemetry), distributed as a standard .NET global tool (`Microsoft.CST.DevSkim.CLI`), outputs standardized SARIF 2.1.0, and evaluates rules across multiple languages in seconds with zero GPU token consumption.

---

## 3. Unified Security Findings Domain & Shared SARIF 2.1.0 Parser (`NightCodeForge.Core`)

### 3.1 Canonical Security Enums & Finding Models
Define strongly-typed shared models in `NightCodeForge.Core` used identically by DevSkim (12.7A), DiffGuard (12.7B), and the Web UI (12.7C):

1. **`SecurityFindingSeverity` Enum:**
   - `Info = 0`, `Low = 1`, `Medium = 2`, `High = 3`, `Critical = 4`
   - *(Note: DevSkim's native `Important` severity maps to `SecurityFindingSeverity.High`. When parsing configuration strings, accept `"Important"` as an alias for `High` and `"Informational"` / `"ManualReview"` as aliases for `Info`, `"BestPractice"` as an alias for `Low`, and `"Moderate"` as an alias for `Medium`.)*
2. **`SecurityFindingSource` Enum:**
   - `DevSkimSast`
   - `CustomSast`
   - `DiffGuardStatic`
   - `DiffGuardLlmJudge`
3. **`SecurityFindingDisposition` Enum:**
   - `Advisory = 0` — informational/below-threshold point; never blocks pipeline or auto-merge; displayed in Review Room.
   - `AgentLoopback = 1` — blocking SAST point; fails the deterministic stage and loops back to `Implementation` for autonomous LLM remediation.
   - `HumanReviewRequired = 2` — blocking post-review guardrail point (Step 12.7B); never loops back to the LLM; downgrades `MergeMode.Automatic` to `MergeMode.ManualReview` and flags for human review.
4. **`SecurityFindingCategory` Enum:**
   - `SecurityVulnerability` (SAST code vulnerability rules)
   - `HardcodedNetworkEndpoint` (`NET-*`)
   - `BackdoorPattern` (`BD-*`, `LLM-*`)
   - `SuspiciousPayload` (`PAY-*` excluding binary artifacts)
   - `BinaryArtifact` (`PAY-BIN-*`)
   - `UnverifiedDependency` (`DEP-NEW-*`)
   - `DependencyDowngrade` (`DEP-DOWN-*`)
   - `ExcessiveBlastRadius` (`SCOPE-*`)
5. **`SecurityScanStatus` Enum:**
   - `Passed`, `Failed`, `Warning`, `Skipped`
6. **`SecurityFinding` Record:**
   - `SecurityFindingSource Source`
   - `string RuleId` (e.g. `DS126858`, `NET-URL-001`, `LLM-JUDGE-001`)
   - `string Title`
   - `SecurityFindingCategory Category`
   - `SecurityFindingSeverity Severity`
   - `SecurityFindingDisposition Disposition`
   - `string? FilePath` (strictly normalized and relativized to workspace repository root with `/` separators)
   - `int? StartLine`
   - `int? EndLine`
   - `int? StartColumn`
   - `string Message`
   - `string? Snippet`
   - `string? Recommendation`
   - `string? AllowlistTarget` (pre-extracted 1-click suppression/allowlist target so UI components perform zero parsing: e.g., `RuleId` for DevSkim, domain/package/IP for DiffGuard in Step 12.7B)
7. **`SecurityScannerReport` Record:**
   - `SecurityFindingSource Source`
   - `string ScannerName` (e.g. `"DevSkim SAST"`, `"DiffGuard"`)
   - `SecurityScanStatus Status`
   - `string StageId`
   - `int BlockingCount`
   - `int AdvisoryCount`
   - `int ScannedFilesCount`
   - `double DurationMs`
   - `List<SecurityFinding> Findings` (ordered by `Disposition` descending, then `Severity` descending, then `FilePath`, then `StartLine`; capped by a named constant such as `SecurityConstants.MaxPersistedFindingsPerReport` to prevent `PipelineDataBag` bloat)
   - `string MarkdownSummary`
   - Extensible scan metadata properties shared across scanners:
     - `string? JudgeModelId`
     - `int AddedLinesCount`
     - `int DeletedLinesCount`
     - `double CodebaseImpactPercent`
     - `bool AutoMergeDowngraded`
     - `string? Notice` (e.g. skip reason or same-model-family warning)
8. **`TaskSecurityReport` & `TaskSecurityReportExtractor` (`NightCodeForge.Core`):**
   - Aggregates `SecurityScannerReport? SastReport` (from `PipelineDataBagKeys.SastReport`) and `SecurityScannerReport? GuardrailReport` (from `PipelineDataBagKeys.SecurityGuardrailReport`, populated in Step 12.7B) into one unified task-level model:
     - `IReadOnlyList<SecurityFinding> AllFindings` (combined, deterministically ordered)
     - `IReadOnlyList<SecurityFinding> HumanReviewFindings` (`Disposition == HumanReviewRequired`)
     - `IReadOnlyList<SecurityFinding> UnresolvedAgentBlockers` (`Disposition == AgentLoopback`)
     - `IReadOnlyList<SecurityFinding> AdvisoryFindings` (`Disposition == Advisory`)
     - `bool HasAnyExecution` (`SastReport != null || GuardrailReport != null`)
     - `bool HasHumanReviewBlockers`
     - `bool HasUnresolvedAgentBlockers`
     - `bool AutoMergeDowngraded`
     - `SecurityScanStatus OverallStatus` (`Failed` if any `HumanReviewRequired` or unresolved `AgentLoopback` blocker exists; `Warning` if any `Advisory` finding exists or an enabled scanner was `Skipped`; `Passed` otherwise).
   - Provide `TaskSecurityReportExtractor.Extract(ForgeTask task)` (and overload for `PipelineDataBag`) so both backend orchestration and Review Room presentation consume the exact same normalized view.

### 3.2 Shared SARIF 2.1.0 Parser (`Sarif210Parser`) & `DevSkimSarifMapper`
1. **Shared `Sarif210Parser` (`NightCodeForge.Core`):**
   - Parse SARIF 2.1.0 `runs[]` metadata (`tool.driver.rules[]` for rule names, short/full descriptions, tags, and help/remediation text) and `results[]` (`ruleId`, `level`, `properties`, `locations[].physicalLocation.artifactLocation.uri`, `region`, `contextRegion`/`snippet`, `message.text`, `suppressions[]`).
   - **Inline Suppressions:** Respect SARIF `suppressions[]` (e.g., inline `// DevSkim: ignore DS...` comments) so suppressed occurrences are excluded from parsed active results.
   - **Path Normalization:** Decode `file://` URIs and percent-encoding, normalize directory separators to `/`, and strip both Windows and WSL worktree root prefixes into clean workspace-relative paths.
   - **Error Semantics:** Tolerate empty `runs`/`results` (0 findings) cleanly; fail fast with a descriptive domain exception when expected SARIF output is missing or contains invalid JSON (no bare `catch { }`).
2. **`DevSkimSarifMapper`:**
   - Map DevSkim native severity (`properties.DevSkimSeverity` or rule tags: `Critical` → `Critical`, `Important` → `High`, `Moderate` → `Medium`, `BestPractice` → `Low`, `ManualReview` → `Info`) with deterministic fallback to SARIF `level` (`error` → `High`, `warning` → `Medium`, `note`/`none` → `Info`).
   - Populate `Source = SecurityFindingSource.DevSkimSast`, `Category = SecurityFindingCategory.SecurityVulnerability`, `Recommendation` from rule help/fix metadata, and `AllowlistTarget = RuleId`.

---

## 4. Diff-Scoped DevSkim Execution, Configuration & Pipeline Behavior

### 4.1 Diff-Scoped Target File Resolution
1. **Scoped to Task Diff Files:**
   - Before running DevSkim, resolve the set of currently existing repository files modified or added in the task's branch/worktree relative to `task.BaseBranch` (combining committed branch changes `baseBranch...HEAD` and any uncommitted/untracked changes in the task worktree, excluding deleted files and ignored globs).
   - Normalize all resolved paths to workspace-relative `/` paths (case-appropriate per OS, deduplicated).
   - **Zero Changed Files Fast-Path:** If 0 scannable diff files exist, skip invoking `devskim` and emit a clean `SecurityScanStatus.Passed` `SecurityScannerReport` (`ScannedFilesCount = 0`).
   - **Strict Diff Filtering:** Run DevSkim (targeting the diff files or filtering parsed SARIF results strictly against the resolved diff-file set) so pre-existing vulnerabilities in untouched repository files never fail the gate, trigger LLM loopbacks, or appear in the Review Room.

### 4.2 Command Resolution, Execution Environment & DevSkim CLI Invocation
1. **Resolution Precedence:**
   - **Explicit Opt-Out (`SastOptions.Enabled == false`):** Skip execution gracefully, record `SkippedCommandSuffix` on `StageCommand(StageId)`, set `sast.gate.status = "skipped"`, persist a `SecurityScanStatus.Skipped` `SecurityScannerReport` in `PipelineDataBagKeys.SastReport`, clear `SastFailureReason`, and return success.
   - **Custom `SastCommand` Override:** Keep `CodeStackPreset.SastCommand` as `null` across stack presets so explicit custom overrides (via constructor, `.nightcodeforge/config.json`, `DeterministicStageConfig.Command`, `SecuritySastOptions.CustomCommand`, or `PipelineDataBagKeys.SastCommand`) are cleanly distinguished from the built-in DevSkim runner. Custom commands execute inside the workspace's resolved `IExecutionEnvironment` with exit-code pass/fail semantics (`Source = CustomSast`).
   - **Built-In DevSkim Default:** When SAST is enabled and no custom `SastCommand` is configured, execute DevSkim externally against the task's on-disk worktree directory via `IDevSkimSastRunner` (`NightCodeForge.Core` interface, `NightCodeForge.Infrastructure` implementation).
2. **Host / WSL Execution & Non-Interactive `PATH`:**
   - Running DevSkim externally on disk avoids requiring the .NET SDK inside non-.NET guest containers (Node.js, Python, Rust, Go).
   - **Windows:** Invoke via `wsl.exe` (honoring `workspace.WslDistribution` / default WSL distribution via `IProcessRunner` and [`WslPathConverter`](../../src/NightCodeForge.Infrastructure/Git/WslPathConverter.cs)), converting worktree paths to WSL paths.
   - **Linux / macOS:** Invoke `devskim` directly on the host against the worktree path.
   - **PATH Hygiene:** Prepend `$HOME/.dotnet/tools` to `PATH` during non-interactive `bash -c` / process invocations so globally installed `.NET` tools are reliably discovered.
3. **Exact DevSkim CLI Arguments & Worktree Hygiene:**
   - Construct the DevSkim CLI invocation using verified flags (`-I` source path, uppercase `-O` output file, `-f sarif` format, `-g` ignore globs, and optional `--ignore-rule-ids`):
     ```bash
     devskim analyze -I <sourcePath> -O <tempSarifPath> -f sarif -g "<ignoreGlobs>" [--ignore-rule-ids "<ignoreRules>"]
     ```
   - Update [`DeterministicStageConstants.DefaultSastCommand`](../../src/NightCodeForge.Core/Workflow/DeterministicStageConstants.cs) to match valid CLI flags (replacing the invalid `--source` flag) so [`CompositeDeterministicStage`](../../src/NightCodeForge.Core/Workflow/Stages/CompositeDeterministicStage.cs) logs accurate command metadata.
   - **Capture All Severities in SARIF:** Do **not** pass `-s Critical,Important` to `devskim analyze`; let DevSkim emit all severities into SARIF so `Medium` (`Moderate`), `Low` (`BestPractice`), and `Info` (`ManualReview`) advisory points are captured for the Review Room while NightCodeForge enforces the configurable failure threshold.
   - **Do Not Pass `-E`:** Keep process exit code `0` for clean CLI completion so tool/runtime failures remain distinct from finding threshold breaches.
   - **Zero Worktree Pollution:** Write the SARIF output file to a unique temporary path **outside** the repository worktree (e.g., `/tmp/ncf-sast-{taskId}-{guid}.sarif`), read it back, and delete it in a `finally` block so untracked files never contaminate `git status`, diffs, or WIP checkpoints.
   - **Default Ignore Globs:** Always exclude VCS and build output directories by default (`**/.git/**,**/bin/**,**/obj/**,**/node_modules/**,**/TestResults/**,**/target/**,**/.nightcodeforge/**`), merged with any configured `IgnoreGlobs`.
4. **Companion Provisioning & Missing Tool Policy:**
   - Register `Microsoft.CST.DevSkim.CLI` (`ToolCommand: "devskim"`, `PackageId: "Microsoft.CST.DevSkim.CLI"`, `ApplicableStacks: [CodeStack.Universal]`) in [`CompanionToolInstaller`](../../src/NightCodeForge.Infrastructure/Execution/CompanionToolInstaller.cs) (`ICompanionToolInstaller`).
   - Verify tool availability before scanning:
     - If `devskim` is missing and `AllowMissingTool == true`, skip gracefully with `UnavailableCommandSuffix`, set `sast.gate.status = "skipped"`, persist a `SecurityScanStatus.Skipped` `SecurityScannerReport` with an explanatory `Notice`, and log a warning (never report `"Passed"` when skipped).
     - Otherwise (default), fail fast with an actionable prerequisite error instructing how to install `dotnet tool install -g Microsoft.CST.DevSkim.CLI` (including inside WSL2 on Windows) — no silent fallback ([AGENTS.md](../../AGENTS.md)).

### 4.3 Unified Settings Foundation (`SecuritySettings` / `SecuritySastOptions` & `ISecuritySettingsService`)
Define the unified security configuration contracts in `NightCodeForge.Core` (implemented in `NightCodeForge.Infrastructure` and extended with `SecurityGuardrailOptions` in Step 12.7B, then surfaced in one UI tab in Step 12.7C):

1. **`SecuritySastOptions`:**
   - `bool Enabled = true`
   - `SecurityFindingSeverity FailSeverity = SecurityFindingSeverity.High` *(equivalent to DevSkim `Important`)*
   - `List<string> IgnoreRules` (e.g., `["DS126858"]`)
   - `List<string> IgnoreGlobs`
   - `bool AllowMissingTool = false`
   - `int TimeoutSeconds = WorkflowDefaults.DefaultSastTimeoutSeconds`
   - `string? CustomCommand`
2. **`SecuritySettings` Container:**
   - Holds `SecuritySastOptions Sast { get; set; } = new();` (and will hold `SecurityGuardrailOptions Guardrail` in Step 12.7B).
3. **`ISecuritySettingsService`:**
   - Resolves effective global settings and optional per-workspace overrides (e.g. persisted in app config / `.nightcodeforge/security.json` and merging `.nightcodeforge/config.json` SAST fields).
   - Provides `AddSastIgnoredRuleAsync(string ruleId, int? workspaceId = null, ...)` for 1-click rule suppression (consumed in Step 12.7C) and `ResetToDefaultsAsync(...)`.
4. **`.nightcodeforge/config.json`, Stage Config & DataBag Compatibility:**
   - Extend [`WorkspaceProjectConfig`](../../src/NightCodeForge.Core/Domain/CodeStack.cs) (deserialized case-insensitively) with `sastEnabled`, `sastFailSeverity`, `sastIgnoreRules`, `sastIgnoreGlobs`, `sastAllowMissingTool`, `sastTimeoutSeconds`.
   - Validate `sastFailSeverity` against `SecurityFindingSeverity` (plus DevSkim aliases `Important` → `High`, `Moderate` → `Medium`, `BestPractice` → `Low`, `ManualReview`/`Informational` → `Info`); fail fast with a clear validation error on unrecognized values.
   - Update [`WorkflowStageRegistry`](../../src/NightCodeForge.Core/Workflow/WorkflowStageRegistry.cs) and [`DeterministicStageConfig`](../../src/NightCodeForge.Core/Workflow/Configuration/DeterministicStageConfig.cs) so `StageDefinition.ConfigurationJson` parameters (such as custom `Command`, `LoopBackStageId`, or SAST options in `ExtraParameters`) are honored when constructing `DeterministicSastStage`.

### 4.4 Pipeline Wiring (Stage 4 + Stage 6), Loopback, DataBag & SignalR Telemetry
1. **Stage 4 & Stage 6 Workflow Definition ([`StandardWorkflowDefinitions`](../../src/NightCodeForge.Core/Domain/StandardWorkflowDefinitions.cs)):**
   - Keep `sast` in **Stage 4 (`PostImplementationGates`)**: `"Stages": "format,build,test-with-coverage,sast"` (`LoopBackStageId = StandardStageIds.Implementation`).
   - Add `sast` to **Stage 6 (`PostReviewVerification`)**: `"Stages": "build,test-with-coverage,sast"` (`LoopBackStageId = StandardStageIds.Implementation`) so minor code edits made by the Code Review agent in Stage 5 (`CodeReviewAndFix`) are re-scanned before entering the Review Room or auto-merge.
   - *(Note: Step 12.7B appends `security-guardrail` after `sast` in Stage 6: `"build,test-with-coverage,sast,security-guardrail"`.)*
2. **Threshold Evaluation & Disposition Assignment:**
   - Apply case-insensitive `IgnoreRules` filtering as defense-in-depth on diff-scoped findings.
   - Tag findings with `Severity >= effectiveFailThreshold` as `Disposition = SecurityFindingDisposition.AgentLoopback`.
   - Tag findings with `Severity < effectiveFailThreshold` as `Disposition = SecurityFindingDisposition.Advisory`.
3. **DataBag Persistence ([`PipelineDataBagKeys`](../../src/NightCodeForge.Core/Workflow/PipelineDataBagKeys.cs)):**
   - Define named constants (no magic strings/numbers):
     - `PipelineDataBagKeys.SastReport` (`"sast.report"`) — serialized `SecurityScannerReport` (latest run overwrites prior run so Stage 6 re-run updates the task's current SAST state).
     - `PipelineDataBagKeys.SastGateStatus` (`"sast.gate.status"`) — `"passed"`, `"warning"`, `"failed"`, or `"skipped"`.
     - `PipelineDataBagKeys.SastFindingsSummary` (`"sast.findings.summary"`) — concise summary string.
     - `PipelineDataBagKeys.SastFailureReason` (`"sast.failure.reason"`) — structured markdown diagnostics (cleared on pass/skip).
   - Ensure [`StartupRecoveryService`](../../src/NightCodeForge.Core/Services/StartupRecoveryService.cs) clears stale `SastReport`, `SastGateStatus`, `SastFindingsSummary`, and `SastFailureReason` when resetting interrupted/recovered task state where appropriate.
4. **Loopback Diagnostic Artifact:**
   - On policy breach (`BlockingCount > 0`):
     - Format a structured markdown report containing severity counts and a violation table (Rule ID, Severity, `FilePath:StartLine`, Message/Description, Recommendation).
     - Store in `PipelineDataBagKeys.SastFailureReason` and `PipelineDataBagKeys.StageOutput(StageId)` (preserving historical Stage 4 loopback transcripts/outputs even after subsequent remediation passes).
     - Resolve target stage (`bag.Get<string>(PipelineDataBagKeys.LoopBackId) ?? LoopBackStageId`, defaulting to `StandardStageIds.Implementation`) and return `StageExecutionResult.Failure(errorMessage, loopBackStageId: targetStage)`.
5. **Real-Time SignalR Telemetry:**
   - Emit deterministic gate completion telemetry via `IAgentActivityStreamer` and bridge it in [`SignalRActivityBridgeService`](../../src/NightCodeForge.Infrastructure/Hubs/SignalRActivityBridgeService.cs) to [`IOrchestratorHubClient.ReceiveDeterministicGateResult`](../../src/NightCodeForge.Infrastructure/Hubs/IOrchestratorHubClient.cs) (`gateName = "sast"`, `passed`, `metricsSummary`).

---

## 5. Implementation Checklist & Deliverables

- [ ] **Unified Core Security Domain & Shared SARIF Parser:**
  - [ ] Define `SecurityFindingSeverity`, `SecurityFindingSource`, `SecurityFindingDisposition`, `SecurityFindingCategory`, `SecurityScanStatus`, `SecurityFinding`, `SecurityScannerReport`, and `TaskSecurityReport` in `NightCodeForge.Core`.
  - [ ] Implement `Sarif210Parser` (`runs[]`, `rules[]`, `suppressions[]`, `file://` URI decoding, Windows/WSL path relativization, empty/malformed fail-fast) and `DevSkimSarifMapper` in `NightCodeForge.Core`.
  - [ ] Implement `TaskSecurityReportExtractor` in `NightCodeForge.Core`.
- [ ] **Settings, Keys & Configuration:**
  - [ ] Add `SastReport`, `SastGateStatus`, `SastFindingsSummary`, `SastFailureReason`, and cap constants to [`PipelineDataBagKeys`](../../src/NightCodeForge.Core/Workflow/PipelineDataBagKeys.cs) / [`DeterministicStageConstants`](../../src/NightCodeForge.Core/Workflow/DeterministicStageConstants.cs) (and fix `DefaultSastCommand`).
  - [ ] Define `SecuritySastOptions`, `SecuritySettings`, and `ISecuritySettingsService` (Core + Infrastructure) with `AddSastIgnoredRuleAsync` and `.nightcodeforge/config.json` merge/validation.
  - [ ] Extend [`WorkspaceProjectConfig`](../../src/NightCodeForge.Core/Domain/CodeStack.cs) and [`DeterministicStageConfig`](../../src/NightCodeForge.Core/Workflow/Configuration/DeterministicStageConfig.cs).
- [ ] **DevSkim Runner & Companion Provisioning:**
  - [ ] Define `IDevSkimSastRunner` in `NightCodeForge.Core` and implement it in `NightCodeForge.Infrastructure` (diff-file scope resolution, WSL on Windows via `WslPathConverter`, host on Linux/macOS, `$HOME/.dotnet/tools` `PATH` injection, temp SARIF outside worktree with `finally` cleanup, default ignore globs).
  - [ ] Register `Microsoft.CST.DevSkim.CLI` (`devskim`) in [`CompanionToolInstaller`](../../src/NightCodeForge.Infrastructure/Execution/CompanionToolInstaller.cs).
- [ ] **Stage, Workflow, Recovery & Telemetry Wiring:**
  - [ ] Upgrade [`DeterministicSastStage`](../../src/NightCodeForge.Core/Workflow/Stages/DeterministicSastStage.cs) (opt-out skip, custom command fallback via `IExecutionEnvironment`, diff-scoped DevSkim runner, missing-tool policy, threshold & ignore filtering, disposition tagging, markdown loopback report, `SecurityScannerReport` DataBag persistence & activity telemetry).
  - [ ] Update [`StandardWorkflowDefinitions`](../../src/NightCodeForge.Core/Domain/StandardWorkflowDefinitions.cs) so Stage 6 (`PostReviewVerification`) includes `sast` (`build,test-with-coverage,sast`), plus [`WorkflowStageRegistry`](../../src/NightCodeForge.Core/Workflow/WorkflowStageRegistry.cs), [`StartupRecoveryService`](../../src/NightCodeForge.Core/Services/StartupRecoveryService.cs), and [`SignalRActivityBridgeService`](../../src/NightCodeForge.Infrastructure/Hubs/SignalRActivityBridgeService.cs).
- [ ] **Automated Test Suite:**
  - [ ] Unit tests for `Sarif210Parser` & `DevSkimSarifMapper` (DevSkim severity & alias mapping, SARIF level fallback, inline suppressions, Windows/WSL/`file://` path relativization, empty SARIF, malformed JSON fail-fast).
  - [ ] Unit tests for `TaskSecurityReportExtractor` and `ISecuritySettingsService` (global + workspace overrides, `.nightcodeforge/config.json` merge, rule allowlist persistence, invalid severity fail-fast).
  - [ ] Unit tests for `DeterministicSastStage` (diff-file scoping excluding untouched pre-existing findings, Stage 4 & Stage 6 execution, threshold & ignore rules, `Enabled: false` skip, missing tool fail-fast vs `AllowMissingTool: true` skip, custom `SastCommand`, DataBag cap, loopback routing; update obsolete no-command skip test per clean-break policy).
  - [ ] Unit tests for `SignalRActivityBridgeService` (`ReceiveDeterministicGateResult` dispatch).
  - [ ] Integration tests verifying synthetic vulnerable code in a task diff (`MD5.Create()`) triggers gate failure + SARIF extraction + loopback, pre-existing vulnerable code in an untouched file does not fail the gate, clean diffs pass, and zero untracked SARIF files remain in the worktree.
  - [ ] Run fast tests during development (`dotnet test --filter "Category!=Acceptance&Category!=E2E"`) and the full test suite on completion per [AGENTS.md](../../AGENTS.md).
