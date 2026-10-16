# 🧹 Step 8.8: Legacy & Dead Code Cleanup

> **Technical Specification & Implementation Guide**  
> *Parent Plan:* [docs/plan/08_DATABASE_DRIVEN_WORKFLOWS_AND_STAGES.md](../08_DATABASE_DRIVEN_WORKFLOWS_AND_STAGES.md)  
> *Status:* Complete

---

## 1. Objective

Once Steps 8.1 through 8.7 are completed and verified, perform a dedicated cleanup pass to eliminate legacy boilerplate, obsolete pipeline factories, hardcoded stage conditionals, and deprecated classes introduced prior to the database-driven, template-based workflow architecture.

---

## 2. Cleanup Targets & Scope

### 2.1 Remove Hardcoded Agentic Stage Boilerplate
- **File:** `src/NightCodeForge.Core/Workflow/Stages/StandardStages.cs`
  - Remove `TaskReviewStage`, `ImplementationStage`, and `CodeReviewAndFixStage`.
  - All agentic stages are now instantiated as [GenericAgenticStage](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Workflow/Stages/GenericAgenticStage.cs) driven by [AgenticStageConfig](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Workflow/Configuration/AgenticStageConfig.cs).
  - Delete `StandardStages.cs` if no other types reside within it.

### 2.2 Deprecate / Remove Static Pipeline Factory
- **File:** `src/NightCodeForge.Core/Workflow/Default3AgentPipeline.cs`
  - Remove `Default3AgentPipeline` and `DefaultWorkflowPipeline`.
  - Pipelines are now constructed dynamically from `WorkflowDefinition` and `StageDefinition` via `IWorkflowPipelineProvider` and `IWorkflowStageFactory`.

### 2.3 Clean Up Hardcoded Stage Branching in `TaskOrchestrator`
- **File:** `src/NightCodeForge.Core/Workflow/TaskOrchestrator.cs`
  - Remove legacy hardcoded conditionals in `BuildBootstrapPrompt`:
    - `if (string.Equals(stageId, "task-review", ...))`
    - `if (string.Equals(stageId, "code-review-fix", ...))`
  - Ensure all prompt rendering routes cleanly through `IPromptTemplateRenderer` with Scriban templates provided in `AgenticStageConfig.PromptTemplate`.

### 2.4 Finalize Domain Model Aliases
- **Files:**
  - `src/NightCodeForge.Core/Domain/StageConversation.cs`
  - `src/NightCodeForge.Core/Domain/StageConversationTurn.cs`
- Verify all usages across `TaskOrchestrator`, `AppDbContext`, repositories, and Blazor components use `Conversation` and `ConversationTurn`.
- Remove obsolete type aliases or stubs.

### 2.5 Refactor Scenario Runners & Test References
- **Files:**
  - `src/NightCodeForge.ScenarioRunner/Scenarios/HumanInterruptionScenario.cs`
  - `src/NightCodeForge.ScenarioRunner/Scenarios/EndToEndWorkflowScenario.cs`
  - Unit and integration test fixtures that directly instantiated `new TaskReviewStage()`, `new ImplementationStage()`, etc.
- Update them to use `GenericAgenticStage` or the stage factory / in-memory workflow definitions.

---

## 3. Verification Criteria
1. `dotnet build` succeeds with **0 warnings** and **0 errors**.
2. No remaining references to `TaskReviewStage`, `ImplementationStage`, `CodeReviewAndFixStage`, or `Default3AgentPipeline` in production code.
3. Test suite runs green:
   ```powershell
   dotnet test --filter "Category!=Acceptance&Category!=E2E"
   ```
4. Full acceptance and scenario runners complete successfully without relying on deleted types.
