# 🏛️ Step 9.5: Standard Workflow Domain Extraction & Canonical Stage Identifiers

> **Phase 9 Implementation Step**  
> *Parent Plan:* [Phase 9 Master Plan](../09_CODEBASE_REMEDIATION_AND_ROBUSTNESS.md)  
> *Source Policy:* [AGENTS.md](../../../AGENTS.md)  
> *Status:* Completed | *Target Framework:* .NET 10 (C# 13)

---

## 1. Objective

Decouple domain workflow definitions from the infrastructure persistence layer:
1. Move the hardcoded standard 3-agent pipeline and stage metadata out of `AppDbContext.EnsureDefaultWorkflowsAsync` into a dedicated domain factory class (`StandardWorkflowDefinitions` in `NightCodeForge.Core.Domain`), mirroring the clean design of `AgentProfile.CreateStandardGlobalProfiles(...)`.
2. Introduce canonical machine identifiers in `StandardStageIds` for `Merge = "merge"` and `Ideation = "ideation"`, eliminating raw string literals across the engine, activity streamers, and UI components.
3. Align `InMemoryWorkflowRepository` and test scenarios to utilize the canonical domain factory.

---

## 2. Technical Scope & Specifications

### 2.1 Extract `StandardWorkflowDefinitions` in Domain Assembly
* **Target File:** `src/NightCodeForge.Core/Domain/StandardWorkflowDefinitions.cs` (New File)
* **Context:**
  Currently, `AppDbContext.cs` lines 386–490 define ~110 lines of domain entities, stage JSON configuration schemas, prompt templates, and agent directives directly in the infrastructure layer.
* **Remediation:**
  * Create `StandardWorkflowDefinitions` in `NightCodeForge.Core.Domain`:
    ```csharp
    namespace NightCodeForge.Core.Domain;

    public static class StandardWorkflowDefinitions
    {
        public const string Standard3AgentCode = "standard-3-agent";

        public static WorkflowDefinition CreateStandard3AgentWorkflow()
        {
            var workflow = new WorkflowDefinition
            {
                Name = "Standard 3-Agent Pipeline",
                Code = Standard3AgentCode,
                Icon = "⚡",
                Description = "Default autonomous pipeline: ForgeTask Review -> Pre-Impl Baseline -> Implementation -> Post-Impl Gates -> Code Review & Fix -> Post-Review Verification.",
                IsDefault = true,
                WorkspaceId = null,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                Stages = new List<StageDefinition>
                {
                    // 1. Task Review (Agentic)
                    // 2. Pre-Implementation Baseline (Deterministic Composite)
                    // 3. Implementation (Agentic)
                    // 4. Post-Implementation Gates (Deterministic Composite)
                    // 5. Code Review & Fix (Agentic)
                    // 6. Post-Review Verification (Deterministic Composite)
                }
            };

            foreach (var stage in workflow.Stages)
            {
                stage.Workflow = workflow;
            }

            return workflow;
        }
    }
    ```
  * In `AppDbContext.EnsureDefaultWorkflowsAsync`, replace the inline entity construction with `StandardWorkflowDefinitions.CreateStandard3AgentWorkflow()`.

### 2.2 Centralize Missing Canonical Stage Identifiers in `StandardStageIds`
* **Target File:** `src/NightCodeForge.Core/Domain/StandardStageIds.cs`
* **Context:**
  `StandardStageIds` defines 7 stage IDs but omits `Merge` and `Ideation`. Consequently, `"merge"` and `"ideation"` are scattered as magic string literals across multiple layers.
* **Remediation:**
  * Add canonical constants to `StandardStageIds`:
    ```csharp
    public const string Merge = "merge";
    public const string Ideation = "ideation";
    ```
  * Refactor occurrences of `"merge"`:
    * `NightCodeForge.Core.Workflow.TaskOrchestrator` (line 978: `StandardStageIds.Merge`)
    * `NightCodeForge.Infrastructure.Hubs.SignalRActivityBridgeService` (line 124: `StandardStageIds.Merge`)
    * `NightCodeForge.Web.Components.Monitoring.PipelineStepper` (line 65: `MergeStageId = StandardStageIds.Merge`)
  * Refactor occurrences of `"ideation"`:
    * `NightCodeForge.Agents.Ideation.IdeationService` (lines 112, 120, 182, 205, 451, 659, etc.)
    * `NightCodeForge.Core.Domain.AgentProfile` (line 68: `Id = StandardStageIds.Ideation`)
    * `NightCodeForge.Infrastructure.Data.Repositories.EfCoreTaskRepository` (line 303: `stageId: StandardStageIds.Ideation`)
    * `NightCodeForge.Core.Repositories.ITaskRepository` (line 456)

### 2.3 Align In-Memory Repositories & Scenario Tests
* **Target Files:**
  * `src/NightCodeForge.Core/Repositories/InMemoryWorkflowRepository.cs`
  * `src/NightCodeForge.ScenarioRunner/Scenarios/EndToEndWorkflowScenario.cs`
  * `tests/NightCodeForge.Core.Tests/Workflow/TestWorkflowPipelines.cs`
* **Remediation:**
  * Update `InMemoryWorkflowRepository`: when `GetDefaultWorkflowWithStagesAsync()` is called on an empty repository, seed and return `StandardWorkflowDefinitions.CreateStandard3AgentWorkflow()` rather than `null`.
  * Update test scenario initializations to consume `StandardWorkflowDefinitions.CreateStandard3AgentWorkflow()`.

---

## 3. Verification & Testing

* Execute `dotnet test --filter "Category!=Acceptance&Category!=E2E"`.
* Verify database seeding tests (`DatabaseSeedingTests`) pass.
* Verify workflow pipeline provider tests pass.
* Ensure 0 build warnings or broken references across all projects.
