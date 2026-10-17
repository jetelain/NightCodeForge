# ✂️ Step 9.3: Clean Break & Dead Code Pruning

> **Phase 9 Implementation Step**  
> *Parent Plan:* [Phase 9 Master Plan](../09_CODEBASE_REMEDIATION_AND_ROBUSTNESS.md)  
> *Source Policy:* [AGENTS.md](../../../AGENTS.md)  
> *Status:* Completed | *Target Framework:* .NET 10 (C# 13)

---

## 1. Objective

Apply the Clean Break policy: since NightCodeForge is in active early development, eliminate all backward compatibility layers, dead legacy classes, obsolete prompt formatters, and duplicate subclasses that add complexity or slow down iteration.

---

## 2. Technical Scope & Specifications

### 2.1 Prune Superseded Hardcoded Composite Stages
* **Target Files:**
  * `src/NightCodeForge.Core/Workflow/Stages/DeterministicPreImplementationStage.cs`
  * `src/NightCodeForge.Core/Workflow/Stages/DeterministicPostImplementationStage.cs`
  * `src/NightCodeForge.Core/Workflow/Stages/DeterministicPostReviewStage.cs`
* **Context:**
  Step 8.3 introduced [CompositeDeterministicStage](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Workflow/Stages/CompositeDeterministicStage.cs) and registered `"deterministic-composite"` in `WorkflowStageRegistry`. The old hardcoded composite classes are completely unreferenced in production (the database seeds `deterministic-composite` for steps 2, 4, and 6) and exist only in test helpers and one scenario file.
* **Remediation:**
  * Delete all three files.
  * In `TestWorkflowPipelines.cs` and `EndToEndWorkflowScenario.cs`, replace references with `CompositeDeterministicStage` or discrete deterministic stages.
  * Remove their transient registrations in `NightCodeForge.Infrastructure/ServiceCollectionExtensions.cs`.

### 2.2 Prune Redundant `SystemProcessRunner` Subclass
* **Target File:** `src/NightCodeForge.Infrastructure/Execution/SystemProcessRunner.cs`
* **Context:**
  This 15-line file merely creates an empty subclass of `NightCodeForge.Core.Execution.SystemProcessRunner` solely to bind `ILogger<SystemProcessRunner>`.
* **Remediation:**
  * Delete `src/NightCodeForge.Infrastructure/Execution/SystemProcessRunner.cs`.
  * Register `NightCodeForge.Core.Execution.SystemProcessRunner` directly in `ServiceCollectionExtensions.cs`.

### 2.3 Prune Legacy String-Concatenation Prompt Formatter
* **Target File:** `src/NightCodeForge.Core/Workflow/DefaultBootstrapPromptBuilder.cs`
* **Context:**
  Lines 65–118 define `BuildLegacyFormat`, a 55-line fallback that manually concatenates strings if `stage.PromptTemplate` is empty. Every stage in the database and test suite now defines a standard Scriban template.
* **Remediation:**
  * Remove `BuildLegacyFormat` and require `stage.PromptTemplate` (or throw if missing).
  * Use `IPromptTemplateRenderer` (Scriban) as the single source of truth for prompt construction.

### 2.4 Prune Sync-over-Async Interface Method
* **Target File:** `src/NightCodeForge.Core/Workflow/IWorkflowPipelineProvider.cs`
* **Context:**
  `IWorkflowPipelineProvider.GetPipeline(ForgeTask task) => GetPipelineAsync(task).GetAwaiter().GetResult();` is a synchronous-over-asynchronous anti-pattern uncalled anywhere in the production engine.
* **Remediation:**
  * Remove `GetPipeline` from `IWorkflowPipelineProvider`.

### 2.5 Clean Up Legacy Stage Aliases & Drive Dynamic Labels in UI
* **Target Files:**
  * `src/NightCodeForge.Web/Components/Monitoring/StageRunSelector.razor`
  * `src/NightCodeForge.Web/Components/Monitoring/StageConversationViewer.razor`
  * `src/NightCodeForge.Web/Components/Monitoring/WorkerStatusCard.razor`
  * `src/NightCodeForge.Web/Components/Monitoring/TaskExecutionInspector.razor`
  * `tests/NightCodeForge.Web.Tests/TaskExecutionInspectorComponentTests.cs`
* **Context:**
  `StageRunSelector.razor` and `StageConversationViewer.razor` currently duplicate a hardcoded `FormatStageName(string stageId)` switch statement mapping stage IDs to hardcoded numbered labels (`"1. ForgeTask Review"`, `"2. Pre-Check Gate"`, `"4. Post-Check Gate"`, `"5. Pull Request Review"`, `"7. Squash & Merge"`) and obsolete aliases (`"deterministic-pre-check"`, `"deterministic-post-check"`, `"pr-review"`).
  This prevents database-driven extensible workflows and stages (introduced in Phase 8) from rendering their actual database `DisplayName` and sequence order.
  Additionally, `WorkerStatusCard.razor` uses brittle substring heuristics (`Contains("preimpl") || Contains("gate") || Contains("merge")`) to guess whether a stage is GPU/Agentic or CPU/Deterministic.
* **Remediation:**
  * Add parameter `[Parameter] public IReadOnlyList<StageDefinition>? WorkflowStages { get; set; }` to `StageRunSelector.razor` and `StageConversationViewer.razor`.
  * Pass `WorkflowStages="@_workflowStages"` from `TaskExecutionInspector.razor` to both child components.
  * In `FormatStageName`, look up the matching `StageDefinition` in `WorkflowStages` and format dynamic labels as `$"{(stage.SequenceOrder > 0 ? $"{stage.SequenceOrder}. " : "")}{stage.DisplayName}"`.
  * If the stage is not found in `WorkflowStages` (or during isolated unit testing), fall back gracefully to canonical display names or raw `stageId` without obsolete hardcoded aliases or hardcoded numbers.
  * In `WorkerStatusCard.razor`, replace fragile substring heuristics with explicit `WorkflowStageKind` checking.
  * Update `TaskExecutionInspectorComponentTests.cs` to pass `WorkflowStages` and assert database-driven labels.

---

## 3. Verification & Testing

* Execute `dotnet test --filter "Category!=Acceptance&Category!=E2E"`.
* Verify dynamic stage labels render properly in bUnit component tests.
* Ensure solution builds with 0 warnings or errors.
