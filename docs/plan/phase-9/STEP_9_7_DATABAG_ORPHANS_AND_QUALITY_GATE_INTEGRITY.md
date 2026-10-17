# 📦 Step 9.7: DataBag Orphan Remediation & Quality Gate Handoff Integrity

> **Phase 9 Implementation Step**  
> *Parent Plan:* [Phase 9 Master Plan](../09_CODEBASE_REMEDIATION_AND_ROBUSTNESS.md)  
> *Source Policy:* [AGENTS.md](../../../AGENTS.md)  
> *Status:* Completed | *Target Framework:* .NET 10 (C# 13)

---

## 1. Objective

Systematically remediate all orphan, write-only, and read-only constants in [`PipelineDataBagKeys`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Workflow/PipelineDataBagKeys.cs). Fix critical bugs where extracted failure lines ([`FailedTests`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Workflow/PipelineDataBagKeys.cs#L34)) and compiler warnings ([`WarningsNew`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Workflow/PipelineDataBagKeys.cs#L42)) were never delivered to the agent fixing the failure; fix deterministic execution tool turn logging in conversation records ([`StageCommand`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Workflow/PipelineDataBagKeys.cs#L46) & [`StageOutput`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Workflow/PipelineDataBagKeys.cs#L47)); fix test metric reporting in [`DeterministicTestStage`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Workflow/Stages/DeterministicTestStage.cs) and [`TaskReviewService`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Services/TaskReviewService.cs); replace raw strings with type-safe constants; prune dead phantom keys; and fix host recovery deterministic metrics reset in [`StartupRecoveryService`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Services/StartupRecoveryService.cs).

---

## 2. Analysis of Current State & Hidden Bugs

### 2.1 Bug 1: Test Failure Diagnostic Lines (`FailedTests`) Never Delivered to Fixing Agent
* **Current Behavior:** [`DeterministicTestStage`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Workflow/Stages/DeterministicTestStage.cs#L158-L160) parses failing test lines using regex and saves them via `bag.Set(PipelineDataBagKeys.FailedTests, failedLines)`.
* **The Bug:** Nothing in production ever reads `PipelineDataBagKeys.FailedTests`. When looping back to the implementation stage, [`TaskOrchestrator`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Workflow/TaskOrchestrator.cs#L715-L720) only appends the generic message `"Deterministic tests failed: 1 failed test(s) out of 10."` to `CompilerErrors`. [`DefaultBootstrapPromptBuilder`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Workflow/DefaultBootstrapPromptBuilder.cs#L31) never receives `FailedTests`.
* **Impact:** The repairing agent is never given the names of failing tests or stack traces, forcing it to guess what failed.

### 2.2 Bug 2: Warning Ratchet Always Emits Generic Text (`WarningsNew` Asymmetry)
* **Current Behavior:** [`DeterministicWarningRatchetStage`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Workflow/Stages/DeterministicWarningRatchetStage.cs#L34-L36) attempts to read `bag.Get<List<string>>(PipelineDataBagKeys.WarningsNew)` to report the new warnings.
* **The Bug:** [`DeterministicBuildStage`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Workflow/Stages/DeterministicBuildStage.cs#L119-L126) counts warnings and sets `WarningsCurrentTotal`, but **never** populates `PipelineDataBagKeys.WarningsNew`.
* **Impact:** In production, `WarningsNew` is always null/empty; ratchet failure messages always fall back to `"{delta} new warning(s) found"` without listing the offending warning lines.

### 2.3 Bug 3: Deterministic Conversation Turn Tool Record Always Defaults to `"dotnet build"`
* **Current Behavior:** In [`TaskOrchestrator.cs`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Workflow/TaskOrchestrator.cs#L464-L468), when logging deterministic execution turns:
  ```csharp
  var cmd = pipelineContext.DataBag.Get<string>(PipelineDataBagKeys.StageCommand(stage.StageId)) 
      ?? pipelineContext.DataBag.Get<string>(PipelineDataBagKeys.BuildCommand) 
      ?? "dotnet build";
  var output = pipelineContext.DataBag.Get<string>(PipelineDataBagKeys.StageOutput(stage.StageId)) 
      ?? (result.IsSuccess ? "Verification completed successfully with exit code 0." : result.ErrorMessage ?? "Deterministic step failed");
  ```
* **The Bug:** No deterministic stage (`DeterministicBuildStage`, `DeterministicTestStage`, `DeterministicSastStage`, etc.) ever writes to `StageCommand(stageId)` or `StageOutput(stageId)` or `BuildCommand`.
* **Impact:** Every deterministic tool turn logged in database `Conversations` displays `Execute dotnet build` (even for Python `pytest`, Rust `cargo test`, or Go `golangci-lint`), and the real stdout/stderr output is permanently discarded.

### 2.4 Bug 4: Test Metric Reporting Corrupts UI Review Room & Raw String Mismatches
* **Current Behavior:** [`DeterministicTestStage.cs`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Workflow/Stages/DeterministicTestStage.cs#L98-L113) parses `passedTests` and measures execution time, but only stores `TestsCurrentTotal` and `TestsCurrentFailed`. It **never** stores `tests.current.passed` or `tests.current.duration_ms`.
* **The Bug:** In [`TaskReviewService.cs`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Services/TaskReviewService.cs#L127-L128):
  ```csharp
  int passedTests = bag.TryGet<int>("tests.current.passed", out var pt) ? pt :
      (bag.TryGet<int>("tests.passed", out pt) ? pt : totalTests);
  ```
  Because `tests.current.passed` is never saved, `passedTests` defaults to `totalTests`! If 1 test failed out of 10, the Review room displays: Total Tests: 10, Passed: 10, Failed: 1 (11 total).
* **Impact:** Corrupted test telemetry in the Web UI review room and raw magic strings throughout `TaskReviewService`.

### 2.5 Bug 5: Host Crash Recovery Fails to Reset Real Deterministic Metrics
* **Current Behavior:** In [`StartupRecoveryService.cs`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Services/StartupRecoveryService.cs#L118-L122):
  ```csharp
  // Deterministic Stage: Reset metrics and retries to execute freshly
  bag.Remove(PipelineDataBagKeys.DeterministicMetrics);
  ```
* **The Bug:** `DeterministicMetrics` is a phantom key that is never set by any stage.
* **Impact:** When recovering an orphaned task on host reboot, actual deterministic stage results (`TestsCurrentTotal`, `CoverageCurrentLineRate`, `WarningsCurrentTotal`, `CompilerErrors`, failure reasons) remain dirty in the DataBag.

### 2.6 Pure Dead / Phantom Keys
* [`AllowLoopBack`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Workflow/PipelineDataBagKeys.cs#L20) (`"Stage.AllowLoopBack"`): Never set, never read anywhere.
* [`MaxLoopBackAttempts`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Workflow/PipelineDataBagKeys.cs#L21) (`"Stage.MaxLoopBackAttempts"`): Never set, never read anywhere.
* [`StrictCompilationGate`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Workflow/PipelineDataBagKeys.cs#L22) (`"Stage.StrictCompilationGate"`): Never set, never read anywhere.
* [`QuestionAnswer(int questionId)`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Workflow/PipelineDataBagKeys.cs#L48): Set in `TaskOrchestrator` on human answer, but never read anywhere (prompt builder reads from `task.Questions`).

---

## 3. Technical Scope & Implementation Specifications

### 3.1 Constants Refactoring in `PipelineDataBagKeys`
1. **Add Missing Canonical Constants:**
   ```csharp
   public const string TestsCurrentPassed = "tests.current.passed";
   public const string TestsCurrentDurationMs = "tests.current.duration_ms";
   public const string CoverageDelta = "coverage.delta";
   public const string WarningsDelta = "warnings.delta";
   public const string ReviewFiles = "ReviewFiles";
   public const string CoverageFailureReason = "coverage.failure.reason";
   ```
2. **Prune Dead / Phantom Keys:**
   * Remove `AllowLoopBack`, `MaxLoopBackAttempts`, `StrictCompilationGate`.
   * Deprecate / remove `DeterministicMetrics`.
   * Prune `QuestionAnswer(int questionId)` helper.
   * Rename `GateFailureReason` to `CoverageFailureReason` (or alias it) so each stage owns a distinct failure reason key.

### 3.2 Deterministic Stage Observability & Handoff
1. **Deterministic Stages (`DeterministicBuildStage`, `DeterministicTestStage`, `DeterministicSastStage`, `DeterministicCoverageGate`, `DeterministicScriptStage`):**
   * Upon executing command in environment:
     ```csharp
     bag.Set(PipelineDataBagKeys.StageCommand(StageId), command);
     bag.Set(PipelineDataBagKeys.StageOutput(StageId), fullOutput);
     ```
2. **`DeterministicBuildStage` Warning Parsing:**
   * Extract warning text lines:
     ```csharp
     var warningLines = warnings.Select(w => w.Value.Trim()).Distinct().Take(20).ToList();
     bag.Set(PipelineDataBagKeys.WarningsNew, warningLines);
     ```
3. **`DeterministicTestStage` Metrics Persistence:**
   * Record `TestsCurrentPassed`:
     ```csharp
     bag.Set(PipelineDataBagKeys.TestsCurrentPassed, passedTests);
     bag.Set(PipelineDataBagKeys.TestsCurrentDurationMs, (int)stopwatch.ElapsedMilliseconds);
     ```
4. **`TaskOrchestrator` Diagnostic Handoff on Loopback:**
   * When looping back due to quality gate failure:
     ```csharp
     var errors = pipelineContext.DataBag.Get<List<string>>(PipelineDataBagKeys.CompilerErrors) ?? new List<string>();
     var failedTests = pipelineContext.DataBag.Get<List<string>>(PipelineDataBagKeys.FailedTests);
     if (failedTests is { Count: > 0 })
     {
         errors.AddRange(failedTests.Select(ft => $"[{stage.StageId} test failure] {ft}"));
     }
     var newWarnings = pipelineContext.DataBag.Get<List<string>>(PipelineDataBagKeys.WarningsNew);
     if (newWarnings is { Count: > 0 } && string.Equals(stage.StageId, StandardStageIds.WarningRatchet, StringComparison.OrdinalIgnoreCase))
     {
         errors.AddRange(newWarnings.Select(w => $"[warning ratchet] {w}"));
     }
     if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
     {
         errors.Add($"[{stage.StageId} attempt {retries}] {result.ErrorMessage}");
     }
     pipelineContext.DataBag.Set(PipelineDataBagKeys.CompilerErrors, errors);
     ```

### 3.3 Prompt Builder Diagnostic Model Enrichment
1. In [`DefaultBootstrapPromptBuilder.cs`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Workflow/DefaultBootstrapPromptBuilder.cs):
   * Add `failed_tests` and `failure_reason` to the Scriban context template model:
     ```csharp
     var failedTests = context.DataBag.Get<List<string>>(PipelineDataBagKeys.FailedTests);
     var buildFailure = context.DataBag.Get<string>(PipelineDataBagKeys.BuildFailureReason);
     var testFailure = context.DataBag.Get<string>(PipelineDataBagKeys.TestFailureReason);
     ```
   * Pass them into the model so templates can render dedicated failure sections.

### 3.4 Web UI & `TaskReviewService` Alignment
1. In [`TaskReviewService.cs`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Services/TaskReviewService.cs):
   * Replace all magic string literals (`"tests.current.total"`, `"tests.current.passed"`, `"coverage.current.line_rate"`, `"ReviewFiles"`, etc.) with `PipelineDataBagKeys` constants.
   * Fix `passedTests` fallback logic: if `tests.current.passed` is missing, calculate `Math.Max(0, totalTests - failedTests)` instead of blindly assigning `totalTests`.

### 3.5 Startup Recovery Deterministic Metrics Reset
1. In [`StartupRecoveryService.cs`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Services/StartupRecoveryService.cs):
   * Clean all active deterministic stage keys:
     ```csharp
     bag.Remove(PipelineDataBagKeys.TestsCurrentTotal);
     bag.Remove(PipelineDataBagKeys.TestsCurrentPassed);
     bag.Remove(PipelineDataBagKeys.TestsCurrentFailed);
     bag.Remove(PipelineDataBagKeys.TestsCurrentDurationMs);
     bag.Remove(PipelineDataBagKeys.CoverageCurrentLineRate);
     bag.Remove(PipelineDataBagKeys.WarningsCurrentTotal);
     bag.Remove(PipelineDataBagKeys.WarningsNew);
     bag.Remove(PipelineDataBagKeys.FailedTests);
     bag.Remove(PipelineDataBagKeys.BuildFailureReason);
     bag.Remove(PipelineDataBagKeys.TestFailureReason);
     bag.Remove(PipelineDataBagKeys.CoverageFailureReason);
     bag.Remove(PipelineDataBagKeys.SastFailureReason);
     bag.Remove(PipelineDataBagKeys.RatchetFailureReason);
     ```

---

## 4. Verification & Testing Criteria

1. **Unit Tests:**
   * Test that `DeterministicBuildStage` sets `StageCommand`, `StageOutput`, and `WarningsNew`.
   * Test that `DeterministicTestStage` sets `StageCommand`, `StageOutput`, `TestsCurrentPassed`, and `TestsCurrentDurationMs`.
   * Test that `TaskOrchestrator` enriches `CompilerErrors` with `FailedTests` on loopback.
   * Test that `TaskReviewService` accurately computes gate metrics using typed constants.
   * Test that `StartupRecoveryService` thoroughly purges deterministic metrics on reboot recovery.
2. **Quality Gate Execution Tests:**
   * Execute unit and integration test suite:
     `dotnet test --filter "Category!=Acceptance&Category!=E2E"`
   * Verify zero regressions.
