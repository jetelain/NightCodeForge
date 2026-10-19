# 🛡️ Step 10.4: Free & Open-Source SAST Quality Gate via Microsoft DevSkim CLI

> **Phase 10 Implementation Step**  
> *Parent Plan:* [Phase 10 Master Plan](../10_EXTEND_FEATURES.md)  
> *Source Policy:* [AGENTS.md](../../../AGENTS.md)  
> *Project Target:* `src/NightCodeForge.Core`, `src/NightCodeForge.Infrastructure`, `src/NightCodeForge.Web`, `tests/NightCodeForge.Core.Tests`  
> *Status:* Planned | *Target Framework:* .NET 10 (C# 13)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Objective

1. **Out-of-the-Box Deterministic SAST:** Activate the currently skipped [DeterministicSastStage](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Workflow/Stages/DeterministicSastStage.cs) by integrating **Microsoft DevSkim CLI** (`Microsoft.CST.DevSkim.CLI`) as the default, 100% free, offline, zero-token static application security testing analyzer.
2. **Multi-Language Security Coverage:** Scan workspace repositories across all supported languages (.NET C#, JavaScript, TypeScript, Python, Go, Rust, C/C++, Java) for common security vulnerabilities, including insecure cryptography, hardcoded credentials and secrets, SQL injection patterns, unsafe deserialization, insecure TLS/SSL ciphers, and deprecated APIs.
3. **Structured SARIF Ingestion & Finding Models:** Execute DevSkim CLI with SARIF output formatting (`-f sarif`), parsing results into strongly-typed domain finding records with workspace-relative file paths, exact line numbers, and actionable remediation guidance.
4. **Enforceable Quality Gate & Autonomous Loopback:** Enforce configurable severity failure thresholds (`Critical`, `Important`). When security vulnerabilities breach policy, fail the deterministic gate and route the task back to the implementation agent with structured security diagnostics to achieve self-healing code remediation.
5. **Web UI Observability:** Display SAST quality gate status, severity distributions, and expandable vulnerability inspection cards in the Review Room and Task Execution Inspector.

---

## 2. Problem Statement & Motivation

### 2.1 Current State in NightCodeForge
- [DeterministicSastStage](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Workflow/Stages/DeterministicSastStage.cs) was introduced in earlier phases and registered in [WorkflowStageRegistry](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Workflow/WorkflowStageRegistry.cs#L90-L95).
- However, all presets in [CodeStackPresets](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Domain/CodeStack.cs#L35-L76) currently set `SastCommand: null`.
- Consequently, the stage skips execution during pipeline runs:
  ```csharp
  var infoMsg = "Deterministic SAST gate skipped: no SAST analyzer command configured (extension point for SonarQube, Semgrep, Trivy, etc.).";
  ```
- Autonomous coding agents can inadvertently introduce critical security regressions (e.g. `MD5.Create()`, hardcoded secrets, weak random number generation, unescaped queries) that compile cleanly and pass functional unit tests, bypassing all existing gates.

### 2.2 Why Microsoft DevSkim?
- **100% Free & Open Source:** Licensed under MIT, actively maintained by Microsoft.
- **Completely Offline & Self-Contained:** Zero cloud dependencies, zero external API keys, zero network telemetry, matching NightCodeForge's strict private on-premise execution model.
- **Cross-Platform CLI:** Distributed as a standard .NET global tool (`dotnet tool install -g Microsoft.CST.DevSkim.CLI`) or standalone executable; trivial to bake into `ContainerSandbox` Docker images or invoke on host/WSL.
- **Rich Standardized Output:** Generates standardized SARIF (Static Analysis Results Interchange Format) v2.1.0 JSON reports.
- **Low Latency:** High-speed regex and AST rule evaluation that completes in seconds on average codebases without consuming GPU tokens.

---

## 3. Behavioral Specifications

### 3.1 CLI Invocation & Execution Environment
1. **Default Command Construction:**
   When `SastCommand` is not overridden in `.nightcodeforge/config.json`, the stage resolves a standard DevSkim CLI execution command:
   ```bash
   devskim analyze . -f sarif -o .nightcodeforge/sast-results.sarif --severity critical,important
   ```
2. **Execution Context:**
   The command runs inside the task's effective execution environment ([ContainerSandbox](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Infrastructure/Execution/ContainerSandboxEnvironment.cs), [WslDirect](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Infrastructure/Execution/WslDirectEnvironment.cs), or [BareHost](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Infrastructure/Execution/BareHostEnvironment.cs)) targeted at the workspace working directory.
3. **Graceful Fallback & Availability Check:**
   If `devskim` binary is not found on `PATH`, the runner checks for `dotnet devskim analyze` (local tool). If neither is available, the stage fails with a clear installation prerequisite message directing how to install the tool (`dotnet tool install -g Microsoft.CST.DevSkim.CLI`), unless `AllowMissingTools` is explicitly configured.

### 3.2 SARIF Parsing & Finding Domain Models
1. **Strongly-Typed Finding Record:**
   Define `SastFinding`:
   - `RuleId`: string (e.g. `DS126858`)
   - `RuleName`: string (e.g. `Weak/Broken Hash Algorithm`)
   - `Severity`: `SastSeverity` enum (`Critical`, `Important`, `Moderate`, `Low`, `Informational`)
   - `FilePath`: string (strictly relativized to workspace repository root)
   - `StartLine`: int
   - `EndLine`: int
   - `StartColumn`: int
   - `Snippet`: string? (fenced offending code snippet)
   - `Recommendation`: string? (remediation guidance from DevSkim rule description)
2. **SARIF Parser:**
   Implement `DevSkimSarifParser` to parse the SARIF JSON report:
   - Handle nested SARIF `runs`, `results`, `locations`, `physicalLocation`, and `region`.
   - Strip absolute path prefixes so findings reference clean workspace-relative paths (e.g. `src/Auth/TokenService.cs:42`).
   - Tolerate empty SARIF reports (0 findings) cleanly.

### 3.3 Quality Gate Evaluation & Autonomous Loopback
1. **Severity Threshold Policy:**
   - Default gate threshold: `SastSeverity.Important`.
   - Any finding with `Severity == Critical` or `Severity == Important` breaches policy and triggers gate failure.
   - Findings with lower severities (`Moderate`, `Low`, `Informational`) are captured in telemetry and metrics but do not fail the build unless configured to do so.
2. **Ignored Rules Configuration:**
   Support an optional `IgnoreRules` list in `DeterministicStageConfig` or workspace project config to suppress known false positives or non-applicable rules (e.g. test projects using dummy cryptographic keys).
3. **Loopback Diagnostic Artifact:**
   Upon policy breach:
   - Construct a structured markdown report summarizing:
     - Total findings count categorized by severity.
     - Formatted table of violations (Rule ID, Location, Description, Recommendation).
   - Store formatted report in [PipelineDataBagKeys.SastFailureReason](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Workflow/PipelineDataBagKeys.cs) and `PipelineDataBagKeys.StageOutput(StageId)`.
   - Return `StageExecutionResult.Failure(errorMessage, targetRoutingStageId: StandardStageIds.Implementation)`.
   - The implementation agent receives the exact security diagnostics in its next turn to fix the vulnerable code pattern.

### 3.4 DataBag Metrics & Telemetry
Persist the following keys in [PipelineDataBag](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Workflow/PipelineDataBag.cs):
- `sast.findings.total`: total number of findings detected
- `sast.findings.critical`: count of critical severity findings
- `sast.findings.important`: count of important severity findings
- `sast.findings.summary`: concise text summary (e.g. `"0 critical, 0 important findings (Passed)"`)

Broadcast real-time SignalR gate result to `ReceiveDeterministicGateResult`.

### 3.5 Web UI Integration
1. **Review Room (`Review.razor`):**
   - Add a dedicated SAST Security Gate card alongside Build, Test, and Coverage cards.
   - Display green checkmark when 0 critical/important findings are present.
   - Display amber/red warning with finding count when violations exist.
2. **Task Execution Inspector (`TaskExecutionInspector.razor`):**
   - When inspecting a SAST stage run, provide an expandable "Security Findings" view displaying each finding's rule ID, file, line number, and remediation advice.

---

## 4. Implementation Checklist & Deliverables

- [ ] **Core Models & Enums:**
  - [ ] Define `SastSeverity` enum (`Informational = 0`, `Low = 1`, `Moderate = 2`, `Important = 3`, `Critical = 4`).
  - [ ] Define `SastFinding` domain record.
  - [ ] Add `SastFindingsTotal`, `SastFindingsCritical`, `SastFindingsImportant` to `PipelineDataBagKeys`.
- [ ] **Parser Implementation:**
  - [ ] Implement `DevSkimSarifParser` in `NightCodeForge.Core/Analysis` (or `Stages`).
  - [ ] Unit tests for `DevSkimSarifParser` covering valid SARIF with multiple findings, empty SARIF, and relativized file paths.
- [ ] **DeterministicSastStage Upgrade:**
  - [ ] Update `DeterministicSastStage.cs` to resolve default DevSkim CLI command when no custom SAST command is provided.
  - [ ] Read and parse output SARIF file after process execution.
  - [ ] Enforce severity failure threshold and rule ignore filtering.
  - [ ] Format structured markdown diagnostics on failure and route loopback to `StandardStageIds.Implementation`.
- [ ] **Presets & Configuration:**
  - [ ] Update `CodeStackPresets.cs` to provide default `devskim` command across presets.
  - [ ] Support `sast_fail_severity` and `sast_ignore_rules` in `.nightcodeforge/config.json`.
- [ ] **Container & Environment Provisioning:**
  - [ ] Add `dotnet tool install -g Microsoft.CST.DevSkim.CLI` to `ContainerSandbox` Dockerfile specifications.
- [ ] **Web UI Components:**
  - [ ] Add SAST gate card to `Review.razor`.
  - [ ] Display findings list in `TaskExecutionInspector.razor`.
- [ ] **Automated Test Suite:**
  - [ ] Integration test executing `DeterministicSastStage` against a synthetic code snippet containing insecure cryptography (`MD5.Create()`), asserting gate failure, SARIF extraction, and loopback targeting.
  - [ ] Integration test executing against clean code asserting gate success.
  - [ ] Run test suite per AGENTS.md: `dotnet test --filter "Category!=Acceptance&Category!=E2E"`.
