# ✂️ Step 9.3: Clean Break & Dead Code Pruning

> **Phase 9 Implementation Step**  
> *Parent Plan:* [Phase 9 Master Plan](../09_CODEBASE_REMEDIATION_AND_ROBUSTNESS.md)  
> *Source Policy:* [AGENTS.md](../../../AGENTS.md)  
> *Status:* Planned | *Target Framework:* .NET 10 (C# 13)

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

### 2.5 Clean Up Legacy Stage Aliases in UI
* **Target Files:**
  * `src/NightCodeForge.Web/Components/Monitoring/StageRunSelector.razor`
  * `src/NightCodeForge.Web/Components/Monitoring/StageConversationViewer.razor`
* **Context:**
  Switch expressions contain obsolete aliases like `"pr-review" or "code-review-fix"` and `"deterministic-pre-check" or "deterministic-pre-impl"`.
* **Remediation:**
  * Retain only canonical stage IDs matching `StandardStageIds`.

---

## 3. Verification & Testing

* Execute `dotnet test --filter "Category!=Acceptance&Category!=E2E"`.
* Ensure solution builds with 0 warnings or errors.
