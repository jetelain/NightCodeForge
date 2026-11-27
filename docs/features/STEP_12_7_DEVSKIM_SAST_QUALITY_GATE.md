# 🛡️ Step 12.7: Free & Open-Source SAST Quality Gate via Microsoft DevSkim CLI

> **Phase 12 Implementation Step (Extend Features - Intelligence & Quality Gates)** *(formerly Step 11.4)*  
> *Parent Plan:* [Features & Integrations Roadmap](README.md)  
> *Related Steps:* [Step 12.28A: DiffGuard CLI Security Scanner](STEP_12_28A_DIFFGUARD_SECURITY_SCANNER_AND_POST_REVIEW_GUARDRAIL.md)  
> *Source Policy:* [AGENTS.md](../../AGENTS.md)  
> *Project Target:* `src/NightCodeForge.Core`, `src/NightCodeForge.Infrastructure`, `src/NightCodeForge.Web`, `tests/NightCodeForge.Core.Tests`, `tests/NightCodeForge.Infrastructure.Tests`, `tests/NightCodeForge.Web.Tests` | *Status:* Planned | *Target Framework:* .NET 10 (C# 13)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Objective

1. **Out-of-the-Box Deterministic SAST:** Activate the currently skipped [`DeterministicSastStage`](../../src/NightCodeForge.Core/Workflow/Stages/DeterministicSastStage.cs) by integrating **Microsoft DevSkim CLI** (`Microsoft.CST.DevSkim.CLI`) as the default, 100% free, offline, zero-token static application security testing analyzer for Stage 4 (`PostImplementationGates`).
2. **Multi-Language Security Coverage:** Scan workspace repositories across supported languages (.NET C#, JavaScript, TypeScript, Python, Go, Rust, C/C++, Java) for common security vulnerabilities including insecure cryptography, hardcoded credentials and secrets, SQL injection patterns, unsafe deserialization, insecure TLS/SSL ciphers, and deprecated APIs.
3. **Structured SARIF 2.1.0 Ingestion & Finding Models:** Execute DevSkim CLI with SARIF output written to a temporary file outside the repository worktree, parsing results into strongly-typed domain finding records with workspace-relative paths, exact line/column spans, code snippets, and remediation guidance.
4. **Enforceable Quality Gate & Autonomous Loopback:** Enforce configurable severity thresholds (default `Important` and `Critical`). When policy is breached, fail the deterministic gate and route the task back to `StandardStageIds.Implementation` with structured security diagnostics for autonomous self-healing remediation. *(Distinct from [Step 12.28A](STEP_12_28A_DIFFGUARD_SECURITY_SCANNER_AND_POST_REVIEW_GUARDRAIL.md), which runs `diffguard` post-review at Stage 6 to audit Git diffs and downgrade auto-merge.)*
5. **Web UI Observability:** Surface SAST quality gate status, severity distributions, and expandable security findings in the Review Room ([`Review.razor`](../../src/NightCodeForge.Web/Components/Pages/Review.razor)) and Task Execution Inspector ([`StageConversationViewer.razor`](../../src/NightCodeForge.Web/Components/Monitoring/StageConversationViewer.razor) / [`TaskExecutionInspector.razor`](../../src/NightCodeForge.Web/Components/Monitoring/TaskExecutionInspector.razor)).

---

## 2. Problem Statement & Current State

- [`DeterministicSastStage`](../../src/NightCodeForge.Core/Workflow/Stages/DeterministicSastStage.cs) is already registered in [`WorkflowStageRegistry`](../../src/NightCodeForge.Core/Workflow/WorkflowStageRegistry.cs) and wired as the `sast` sub-stage of `PostImplementationGates` (`format,build,test-with-coverage,sast`) in [`StandardWorkflowDefinitions`](../../src/NightCodeForge.Core/Domain/StandardWorkflowDefinitions.cs).
- Because all presets in [`CodeStackPresets`](../../src/NightCodeForge.Core/Domain/CodeStack.cs) currently set `SastCommand: null`, the stage skips every run with `"Deterministic SAST gate skipped: no SAST analyzer command configured..."`.
- Autonomous coding agents can introduce critical security regressions (e.g. `MD5.Create()`, hardcoded secrets, weak PRNGs, unescaped SQL) that compile cleanly and pass functional unit tests.
- **Why Microsoft DevSkim CLI:** MIT-licensed, completely offline (no cloud API keys or telemetry), distributed as a standard .NET global tool (`Microsoft.CST.DevSkim.CLI`), outputs standardized SARIF 2.1.0, and evaluates rules across multiple languages in seconds with zero GPU token consumption.

---

## 3. Behavioral Specifications

### 3.1 Command Resolution, Execution Environment & DevSkim CLI Invocation
1. **Resolution Precedence:**
   - **Explicit Opt-Out (`SastEnabled == false`):** When SAST is disabled in `.nightcodeforge/config.json`, `PipelineDataBag`, or `StageDefinition.ConfigurationJson`, skip execution gracefully, record `SkippedCommandSuffix` on `StageCommand(StageId)`, set `sast.gate.status = "skipped"`, clear `SastFailureReason`, and return success.
   - **Custom `SastCommand` Override:** Keep `CodeStackPreset.SastCommand` as `null` across stack presets so explicit custom overrides (via constructor, `.nightcodeforge/config.json`, `DeterministicStageConfig.Command`, or `PipelineDataBagKeys.SastCommand`) are cleanly distinguished from the built-in DevSkim runner. Custom commands execute inside the workspace's resolved `IExecutionEnvironment` with exit-code pass/fail semantics.
   - **Built-In DevSkim Default:** When `SastEnabled` is not `false` and no custom `SastCommand` is configured, execute DevSkim externally against the task's on-disk worktree directory via a Core runner abstraction (e.g. `IDevSkimSastRunner` in `NightCodeForge.Core`, implemented in `NightCodeForge.Infrastructure`).
2. **Host / WSL Execution & Non-Interactive `PATH`:**
   - Running DevSkim externally on disk avoids requiring the .NET SDK inside non-.NET guest containers (Node.js, Python, Rust, Go).
   - **Windows:** Invoke via `wsl.exe` (honoring `workspace.WslDistribution` / default WSL distribution via `IProcessRunner` and [`WslPathConverter`](../../src/NightCodeForge.Infrastructure/Git/WslPathConverter.cs)), converting the worktree path to its WSL path.
   - **Linux / macOS:** Invoke `devskim` directly on the host against the worktree path.
   - **PATH Hygiene:** Prepend `$HOME/.dotnet/tools` to `PATH` during non-interactive `bash -c` / process invocations so globally installed `.NET` tools are reliably discovered.
3. **Exact DevSkim CLI Arguments & Worktree Hygiene:**
   - Construct the DevSkim CLI invocation using verified flags (`-I` source path, uppercase `-O` output file, `-f sarif` format, `-g` ignore globs, and optional `--ignore-rule-ids`):
     ```bash
     devskim analyze -I <sourcePath> -O <tempSarifPath> -f sarif -g "<ignoreGlobs>" [--ignore-rule-ids "<ignoreRules>"]
     ```
   - Update [`DeterministicStageConstants.DefaultSastCommand`](../../src/NightCodeForge.Core/Workflow/DeterministicStageConstants.cs) to match valid CLI flags (replacing the invalid `--source` flag) so [`CompositeDeterministicStage`](../../src/NightCodeForge.Core/Workflow/Stages/CompositeDeterministicStage.cs) logs accurate command metadata.
   - **Capture All Severities in SARIF:** Do **not** pass `-s Critical,Important` to `devskim analyze`; let DevSkim emit all severities into SARIF so `Moderate`, `Low`, and `Informational` counts are accurately captured in telemetry while NightCodeForge enforces the configurable failure threshold.
   - **Do Not Pass `-E`:** Keep process exit code `0` for clean CLI completion so tool/runtime failures remain distinct from finding threshold breaches.
   - **Zero Worktree Pollution:** Write the SARIF output file to a unique temporary path **outside** the repository worktree (e.g., `/tmp/ncf-sast-{taskId}-{guid}.sarif`), read it back, and delete it in a `finally` block so untracked files never contaminate `git status`, diffs, or WIP checkpoints.
   - **Default Ignore Globs:** Always exclude VCS and build output directories by default (`**/.git/**,**/bin/**,**/obj/**,**/node_modules/**,**/TestResults/**,**/target/**,**/.nightcodeforge/**`), merged with any workspace-configured `sastIgnoreGlobs`.
4. **Companion Provisioning & Missing Tool Policy:**
   - Register `Microsoft.CST.DevSkim.CLI` (`ToolCommand: "devskim"`, `PackageId: "Microsoft.CST.DevSkim.CLI"`, `ApplicableStacks: [CodeStack.Universal]`) in [`CompanionToolInstaller`](../../src/NightCodeForge.Infrastructure/Execution/CompanionToolInstaller.cs) (`ICompanionToolInstaller`).
   - Verify tool availability before scanning:
     - If `devskim` is missing and `SastAllowMissingTool == true`, skip gracefully with `UnavailableCommandSuffix`, set `sast.gate.status = "skipped"`, log a warning, and record an explanatory message (never report `"Passed"` when skipped).
     - Otherwise (default), fail fast with an actionable prerequisite error instructing how to install `dotnet tool install -g Microsoft.CST.DevSkim.CLI` (including inside WSL2 on Windows) — no silent fallback ([AGENTS.md](../../AGENTS.md)).

### 3.2 Configuration Schema (`.nightcodeforge/config.json`, Stage Config & DataBag)
Extend [`WorkspaceProjectConfig`](../../src/NightCodeForge.Core/Domain/CodeStack.cs) (deserialized case-insensitively) and support stage/DataBag overrides:
- `sastEnabled` (`bool?`, default `true`)
- `sastFailSeverity` (`string?`, default `"Important"`; valid values: `Critical`, `Important`, `Moderate`, `Low`, `Informational` — fail fast with a clear validation error on unrecognized values)
- `sastIgnoreRules` (`List<string>?`, rule IDs to suppress, e.g., `["DS126858"]`)
- `sastIgnoreGlobs` (`List<string>?`, additional path globs to exclude)
- `sastAllowMissingTool` (`bool?`, default `false`)
- `sastTimeoutSeconds` (`int?`, default `WorkflowDefaults.DefaultSastTimeoutSeconds`)

Update [`WorkflowStageRegistry`](../../src/NightCodeForge.Core/Workflow/WorkflowStageRegistry.cs) and [`DeterministicStageConfig`](../../src/NightCodeForge.Core/Workflow/Configuration/DeterministicStageConfig.cs) so `StageDefinition.ConfigurationJson` parameters (such as custom `Command`, `LoopBackStageId`, or SAST options in `ExtraParameters`) are honored when constructing `DeterministicSastStage`.

### 3.3 SARIF 2.1.0 Parsing & Domain Models
1. **Domain Models & Enums (`NightCodeForge.Core`):**
   - `SastSeverity` enum: `Informational = 0`, `Low = 1`, `Moderate = 2`, `Important = 3`, `Critical = 4`.
   - `SastFinding` record:
     - `RuleId` (`string`, e.g. `DS126858`)
     - `RuleName` (`string`, e.g. `Weak/Broken Hash Algorithm`)
     - `Severity` (`SastSeverity`)
     - `FilePath` (`string`, strictly normalized and relativized to workspace repository root with `/` separators)
     - `StartLine` (`int`)
     - `EndLine` (`int`)
     - `StartColumn` (`int`)
     - `Snippet` (`string?`)
     - `Recommendation` (`string?`, remediation guidance extracted from rule metadata / result message / fixes)
2. **SARIF Parser (`DevSkimSarifParser`):**
   - Parse SARIF 2.1.0 `runs[]` metadata (`tool.driver.rules[]` for rule names, descriptions, tags, and help/remediation text) and `results[]` (`locations[].physicalLocation.artifactLocation.uri`, `region`, `contextRegion`/`snippet`, `message.text`, `suppressions`).
   - **Severity Resolution:** Map DevSkim native severity (`properties.DevSkimSeverity` or rule tags: `Critical` → `Critical`, `Important` → `Important`, `Moderate` → `Moderate`, `BestPractice` → `Low`, `ManualReview` → `Informational`) with deterministic fallback to SARIF `level` (`error` → `Important`, `warning` → `Moderate`, `note`/`none` → `Informational`).
   - **Inline Suppressions:** Respect SARIF `suppressions[]` (e.g., inline `// DevSkim: ignore DS...` comments) so suppressed occurrences are excluded from active findings.
   - **Path Normalization:** Decode `file://` URIs and percent-encoding, normalize directory separators to `/`, and strip both Windows and WSL worktree root prefixes.
   - **Error Semantics:** Tolerate empty `runs`/`results` (0 findings) cleanly; fail fast with a descriptive exception/error when expected SARIF output is missing or contains invalid JSON (no bare `catch { }`).

### 3.4 Quality Gate Evaluation, Loopback, DataBag & SignalR Telemetry
1. **Threshold & Rule Filtering:**
   - Apply case-insensitive `SastIgnoreRules` filtering as defense-in-depth on parsed findings.
   - Default failure threshold: `SastSeverity.Important`. Any non-ignored finding with `Severity >= effectiveFailThreshold` breaches policy and fails the gate.
   - Findings below threshold (`Moderate`, `Low`, `Informational`) are retained in telemetry and findings lists without failing the gate.
2. **DataBag Persistence ([`PipelineDataBagKeys`](../../src/NightCodeForge.Core/Workflow/PipelineDataBagKeys.cs) & [`PipelineDataBag`](../../src/NightCodeForge.Core/Workflow/PipelineDataBag.cs)):**
   - Define named constants (no magic strings/numbers):
     - `sast.gate.status`: `"passed"`, `"failed"`, or `"skipped"`
     - `sast.findings.total`: total active findings count
     - `sast.findings.critical`: count of `Critical` findings
     - `sast.findings.important`: count of `Important` findings
     - `sast.findings.moderate`: count of `Moderate` findings
     - `sast.findings.low`: count of `Low` findings
     - `sast.findings.informational`: count of `Informational` findings
     - `sast.findings.summary`: concise summary string (e.g., `"0 critical, 0 important findings (Passed)"`)
     - `sast.findings`: serialized bounded list of `SastFinding` (ordered by severity descending, then `FilePath`, then `StartLine`, capped by a named constant to prevent DataBag bloat)
     - `sast.failure.reason`: structured failure diagnostics (cleared on pass/skip)
   - Ensure [`StartupRecoveryService`](../../src/NightCodeForge.Core/Services/StartupRecoveryService.cs) resets stale SAST result keys alongside `SastFailureReason` when resetting interrupted/recovered task state where appropriate.
3. **Loopback Diagnostic Artifact:**
   - On policy breach:
     - Format a structured markdown report containing severity counts and a violation table (Rule ID, Severity, `FilePath:StartLine`, Message/Description, Recommendation).
     - Store in `PipelineDataBagKeys.SastFailureReason` and `PipelineDataBagKeys.StageOutput(StageId)`.
     - Resolve target stage (`bag.Get<string>(PipelineDataBagKeys.LoopBackId) ?? LoopBackStageId`, defaulting to `StandardStageIds.Implementation`) and return `StageExecutionResult.Failure(errorMessage, loopBackStageId: targetStage)` so [`CompositeDeterministicStage`](../../src/NightCodeForge.Core/Workflow/Stages/CompositeDeterministicStage.cs) and [`TaskOrchestrator`](../../src/NightCodeForge.Core/Workflow/TaskOrchestrator.cs) route the failure and diagnostics back to the Implementation agent.
4. **Real-Time SignalR Telemetry:**
   - Emit deterministic gate completion telemetry via `IAgentActivityStreamer` (keeping `NightCodeForge.Core` independent of SignalR) and bridge it in [`SignalRActivityBridgeService`](../../src/NightCodeForge.Infrastructure/Hubs/SignalRActivityBridgeService.cs) to [`IOrchestratorHubClient.ReceiveDeterministicGateResult`](../../src/NightCodeForge.Infrastructure/Hubs/IOrchestratorHubClient.cs) (`gateName = "sast"`, `passed`, `metricsSummary`).

### 3.5 Web UI Integration
Adhering to the **UI and Presentation separation** policy ([AGENTS.md](../../AGENTS.md)), keep all parsing and mapping in `NightCodeForge.Core` / `NightCodeForge.Web/Services`:
1. **Review Room ([`Review.razor`](../../src/NightCodeForge.Web/Components/Pages/Review.razor)):**
   - Extend [`QualityGateMetrics`](../../src/NightCodeForge.Core/Domain/TaskReviewModels.cs) and [`QualityGateMetricsExtractor`](../../src/NightCodeForge.Core/Workflow/QualityGateMetricsExtractor.cs) to extract SAST gate status and severity counts from `task.PipelineDataJson`.
   - Render a dedicated 4th **SAST Security** card in `#gate-metrics-grid` alongside Code Coverage, Compiler Warnings, and Test Suite:
     - Green pass state when SAST passed with 0 breaching findings.
     - Neutral/amber state when SAST was skipped or has only below-threshold findings.
     - Red fail state with violation counts when threshold-breaching findings exist.
2. **Task Execution Inspector ([`StageConversationViewer.razor`](../../src/NightCodeForge.Web/Components/Monitoring/StageConversationViewer.razor) & [`TaskExecutionInspector.razor`](../../src/NightCodeForge.Web/Components/Monitoring/TaskExecutionInspector.razor)):**
   - Extract SAST DataBag deserialization and presentation formatting into a dedicated Web helper (e.g., `SastPresentationHelper` in `src/NightCodeForge.Web/Services/`).
   - When inspecting either a standalone `deterministic-sast` run or a composite stage run (such as `post-implementation-gates`) that executed a `sast` sub-stage, display an expandable **"Security Findings"** section listing each finding's severity badge, Rule ID, `FilePath:StartLine`, optional code snippet, and remediation advice.

---

## 4. Implementation Checklist & Deliverables

- [ ] **Core Models, Keys & Configuration:**
  - [ ] Define `SastSeverity` enum, `SastFinding` record, and SAST fields on [`QualityGateMetrics`](../../src/NightCodeForge.Core/Domain/TaskReviewModels.cs) + [`QualityGateMetricsExtractor`](../../src/NightCodeForge.Core/Workflow/QualityGateMetricsExtractor.cs).
  - [ ] Add SAST DataBag keys, status constants, and findings cap constants to [`PipelineDataBagKeys`](../../src/NightCodeForge.Core/Workflow/PipelineDataBagKeys.cs) / [`DeterministicStageConstants`](../../src/NightCodeForge.Core/Workflow/DeterministicStageConstants.cs) (and fix `DefaultSastCommand`).
  - [ ] Extend [`WorkspaceProjectConfig`](../../src/NightCodeForge.Core/Domain/CodeStack.cs) (`sastEnabled`, `sastFailSeverity`, `sastIgnoreRules`, `sastIgnoreGlobs`, `sastAllowMissingTool`, `sastTimeoutSeconds`) and [`DeterministicStageConfig`](../../src/NightCodeForge.Core/Workflow/Configuration/DeterministicStageConfig.cs).
- [ ] **SARIF Parser & DevSkim Runner:**
  - [ ] Implement `DevSkimSarifParser` in `NightCodeForge.Core` (`DevSkimSeverity` + SARIF level mapping, `suppressions[]` handling, Windows/WSL/URI path relativization, empty/malformed handling).
  - [ ] Define `IDevSkimSastRunner` in `NightCodeForge.Core` and implement it in `NightCodeForge.Infrastructure` (WSL on Windows via `WslPathConverter`, host on Linux/macOS, `$HOME/.dotnet/tools` `PATH` injection, temp SARIF path outside worktree with `finally` cleanup, default ignore globs).
  - [ ] Register `Microsoft.CST.DevSkim.CLI` (`devskim`) in [`CompanionToolInstaller`](../../src/NightCodeForge.Infrastructure/Execution/CompanionToolInstaller.cs).
- [ ] **Stage, Registry, Recovery & Telemetry Wiring:**
  - [ ] Upgrade [`DeterministicSastStage`](../../src/NightCodeForge.Core/Workflow/Stages/DeterministicSastStage.cs) (opt-out skip, custom command fallback via `IExecutionEnvironment`, default DevSkim runner, missing-tool policy, threshold & ignore filtering, markdown loopback report, DataBag & activity event emission).
  - [ ] Update [`WorkflowStageRegistry`](../../src/NightCodeForge.Core/Workflow/WorkflowStageRegistry.cs), [`StartupRecoveryService`](../../src/NightCodeForge.Core/Services/StartupRecoveryService.cs), and [`SignalRActivityBridgeService`](../../src/NightCodeForge.Infrastructure/Hubs/SignalRActivityBridgeService.cs) (`ReceiveDeterministicGateResult` dispatch).
- [ ] **Web UI Integration:**
  - [ ] Add `SastPresentationHelper` in `src/NightCodeForge.Web/Services/`.
  - [ ] Add SAST Security gate card to [`Review.razor`](../../src/NightCodeForge.Web/Components/Pages/Review.razor).
  - [ ] Add expandable Security Findings view in [`StageConversationViewer.razor`](../../src/NightCodeForge.Web/Components/Monitoring/StageConversationViewer.razor) / [`TaskExecutionInspector.razor`](../../src/NightCodeForge.Web/Components/Monitoring/TaskExecutionInspector.razor).
- [ ] **Automated Test Suite:**
  - [ ] Unit tests for `DevSkimSarifParser` (DevSkimSeverity & SARIF level mapping, inline suppressions, Windows/WSL/`file://` path relativization, empty SARIF, malformed JSON fail-fast).
  - [ ] Unit tests for `DeterministicSastStage` (DevSkim runner path, threshold & ignore rules, `sastEnabled: false` skip, missing tool fail-fast vs `sastAllowMissingTool: true` skip, custom `SastCommand`, DataBag cap, loopback routing; update obsolete no-command skip test per clean-break policy).
  - [ ] Unit tests for `QualityGateMetricsExtractor`, `SastPresentationHelper`, and `SignalRActivityBridgeService` (`ReceiveDeterministicGateResult`).
  - [ ] bUnit tests for [`Review.razor`](../../src/NightCodeForge.Web/Components/Pages/Review.razor) SAST gate card and inspector findings panel.
  - [ ] Integration tests verifying synthetic vulnerable code (`MD5.Create()`) triggers gate failure + SARIF extraction + loopback, clean code passes, and zero untracked SARIF files remain in the worktree.
  - [ ] Run fast tests during development (`dotnet test --filter "Category!=Acceptance&Category!=E2E"`) and the full test suite on completion per [AGENTS.md](../../AGENTS.md).
