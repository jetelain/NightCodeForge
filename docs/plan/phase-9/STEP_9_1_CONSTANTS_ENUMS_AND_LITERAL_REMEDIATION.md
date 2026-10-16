# 🏷️ Step 9.1: Constants, Enums & Literal Remediation

> **Phase 9 Implementation Step**  
> *Parent Plan:* [Phase 9 Master Plan](../09_CODEBASE_REMEDIATION_AND_ROBUSTNESS.md)  
> *Source Policy:* [AGENTS.md](../../../AGENTS.md)  
> *Status:* Planned | *Target Framework:* .NET 10 (C# 13)

---

## 1. Objective

Eliminate all magic strings and numbers scattered across domain logic, workflow orchestration, agent harnesses, tool execution, and the Blazor Web UI. Centralize all identifiers, DataBag keys, harness IDs, stage types, and default numeric thresholds into strongly-typed constant and enum classes.

---

## 2. Technical Scope & Specifications

### 2.1 New Constant & Enum Definitions in `NightCodeForge.Core`

1. **`StandardStageIds` (`NightCodeForge.Core.Domain`):**
   ```csharp
   namespace NightCodeForge.Core.Domain;

   public static class StandardStageIds
   {
       public const string TaskReview = "task-review";
       public const string PreImplementationBaseline = "deterministic-pre-impl";
       public const string Implementation = "implementation";
       public const string PostImplementationGates = "deterministic-post-impl";
       public const string CodeReviewAndFix = "code-review-fix";
       public const string PostReviewVerification = "deterministic-post-review";
       public const string Triage = "triage";
   }
   ```

2. **`PipelineDataBagKeys` (`NightCodeForge.Core.Workflow`):**
   ```csharp
   namespace NightCodeForge.Core.Workflow;

   public static class PipelineDataBagKeys
   {
       public const string LastHumanAnswer = "LastHumanAnswer";
       public const string ApprovedPlan = "Stage1.ApprovedPlan";
       public const string CompilerErrors = "diagnostics.compiler_errors";
       public const string PendingProposals = "PendingProposals";
       public const string TaskMarkdownPath = "TaskMarkdownPath";
       public const string SerializedMemory = "SerializedMemory";
       public const string ReviewerFeedback = "ReviewerFeedback";
       public const string ReviewDiff = "ReviewDiff";
       public const string MaxRetries = "Workflow.MaxRetries";
       public const string RetryCount = "Workflow.RetryCount";
       public const string StageRetries = "Workflow.StageRetries";
       public const string LoopBackId = "Stage.LoopBackId";
       public const string AllowLoopBack = "Stage.AllowLoopBack";
       public const string MaxLoopBackAttempts = "Stage.MaxLoopBackAttempts";
       public const string StrictCompilationGate = "Stage.StrictCompilationGate";
       public const string BuildCommand = "build.command";
       public const string TestCommand = "test.command";

       public static string StageSummary(string stageId) => $"StageSummary_{stageId}";
       public static string StageCommand(string stageId) => $"{stageId}.command";
       public static string StageOutput(string stageId) => $"{stageId}.output";
   }
   ```

3. **`StandardHarnessIds` (`NightCodeForge.Core.Domain`):**
   ```csharp
   namespace NightCodeForge.Core.Domain;

   public static class StandardHarnessIds
   {
       public const string Crucible = "crucible";
       public const string Acp = "acp";
       public const string PiRpc = "pirpc";
   }
   ```

4. **`StageImplementationTypes` (`NightCodeForge.Core.Workflow`):**
   ```csharp
   namespace NightCodeForge.Core.Workflow;

   public static class StageImplementationTypes
   {
       public const string Agentic = "agentic";
       public const string DeterministicBuild = "deterministic-build";
       public const string DeterministicTest = "deterministic-test";
       public const string DeterministicCoverage = "deterministic-coverage";
       public const string DeterministicSast = "deterministic-sast";
       public const string DeterministicRatchet = "deterministic-ratchet";
       public const string DeterministicComposite = "deterministic-composite";
       public const string DeterministicScript = "deterministic-script";
   }
   ```

5. **`WorkflowDefaults` (`NightCodeForge.Core.Workflow`):**
   ```csharp
   namespace NightCodeForge.Core.Workflow;

   public static class WorkflowDefaults
   {
       public const int DefaultMaxStageRetries = 3;
       public const int DefaultCommandTimeoutSeconds = 60;
       public const int DefaultBuildTimeoutSeconds = 120;
       public const int DefaultTestTimeoutSeconds = 120;
       public const int ProcessTimeoutExitCode = 124;
       public const int DefaultOutputSummaryTruncationLength = 300;
   }
   ```

---

## 3. Refactoring Targets

* **`NightCodeForge.Core`**:
  * Replace `"task-review"`, `"implementation"`, `"code-review-fix"` in `TaskOrchestrator.cs`, `WorkflowPipelineEngine.cs`, `DefaultBootstrapPromptBuilder.cs`, and `DeterministicWarningRatchetStage.cs`.
  * Replace DataBag literal strings with `PipelineDataBagKeys.*`.
  * Replace `"agentic"`, `"deterministic-build"` in `WorkflowStageRegistry.cs` with `StageImplementationTypes.*`.
* **`NightCodeForge.Infrastructure`**:
  * Replace stage IDs and harness IDs in `AppDbContext.cs` (`EnsureDefaultWorkflowsAsync`, `EnsureStandardProfilesAsync`).
* **`NightCodeForge.Agents`**:
  * Replace `"crucible"`, `"acp"`, `"pirpc"` across harnesses and `TriageService.cs`.
  * Replace magic timeout numbers in `CanonicalTools.cs` with `WorkflowDefaults.*`.
* **`NightCodeForge.Web`**:
  * Replace `"task-review"`, etc., in `Monitoring.razor`, `StageRunSelector.razor`, `StageConversationViewer.razor`, and `ABTestingComparatorTab.razor`.
* **Scenario Runner & Test Projects**:
  * Update tests and test scenarios to use the canonical constants.

---

## 4. Verification & Testing

* Execute `dotnet test --filter "Category!=Acceptance&Category!=E2E"`.
* Perform ripgrep scan to ensure zero occurrences of target raw literals remain.
