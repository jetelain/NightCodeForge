# 🧪 Step 8.7: Comprehensive Hardening & Test Suite

> **Technical Specification & Implementation Guide**  
> *Parent Plan:* [docs/plan/08_DATABASE_DRIVEN_WORKFLOWS_AND_STAGES.md](../08_DATABASE_DRIVEN_WORKFLOWS_AND_STAGES.md)  
> *Status:* Ready for Implementation

---

## 1. Objective

Establish an exhaustive automated test suite covering database entities, schema migrations, stage registry factory resolution, dynamic orchestrator pipeline execution, Ideation conversation persistence, and UI rendering across all layers of NightCodeForge.

---

## 2. Test Suite Breakdown

### 2.1 Core Unit Tests (`NightCodeForge.Core.Tests`)
- **`WorkflowDefinitionTests.cs`**:
  - Validates entity property assignments, stage sequence ordering, and validation rules.
  - Verifies JSON configuration serialization/deserialization for `AgenticStageConfig` and `DeterministicStageConfig`.
- **`StageRegistryAndFactoryTests.cs`**:
  - Tests `IWorkflowStageRegistry` handler registration.
  - Tests `IWorkflowStageFactory.CreateStage` for all 10+ standard implementation types.
  - Asserts that invalid or unregistered implementation types throw descriptive exceptions.

### 2.2 Infrastructure & Persistence Tests (`NightCodeForge.Infrastructure.Tests`)
- **`WorkflowRepositoryTests.cs`**:
  - Validates SQLite WAL persistence of `WorkflowDefinition` and `StageDefinition`.
  - Verifies cascading delete on `WorkflowDefinition.Stages`.
  - Verifies foreign key behavior on `ForgeTask.WorkflowId`.
- **`DatabaseSeedingTests.cs`**:
  - Asserts that `EnsureDefaultWorkflowsAsync()` creates the standard 6-stage workflow idempotently.
  - Verifies that pre-existing tasks with `WorkflowId == 0` are backfilled properly.
- **`IdeationConversationPersistenceTests.cs`**:
  - Tests creating `StageConversation` with `ConversationType = Ideation` and `TaskId = null`.
  - Validates inserting turns (User, Assistant, Thinking, Tool, ToolResult) and rehydrating in correct chronological order.

### 2.3 Orchestrator Integration Tests (`NightCodeForge.Core.Tests` / `Agents.Tests`)
- **`TaskOrchestratorDynamicWorkflowTests.cs`**:
  - Runs a task through a custom 3-stage workflow (`task-review` -> `deterministic-build` -> `implementation`).
  - Verifies that orchestrator stops at stage 3 and transitions task to `Implemented`.
  - Tests dynamic failure loopbacks (e.g., failure in stage 4 routes back to stage 3 as specified in config).

### 2.4 Web Component & E2E Tests (`NightCodeForge.Web.Tests`)
- **`PipelineStepperDynamicTests.cs`**:
  - Renders `PipelineStepper` with custom 3-step, 5-step, and 7-step stage lists.
  - Asserts correct connector counts, badge labels (GPU vs CPU), and status circles.
- **`IdeationSessionPersistenceTests.cs`**:
  - Simulates an ideation chat session, re-initializes `Ideation.razor`, and verifies that all messages and proposals remain present.
- **Playwright E2E Integration**:
  - Verifies complete end-to-end task progression using dynamic workflows from the browser.

---

## 3. Execution & Verification Command
```powershell
dotnet test NightCodeForge.sln --configuration Release --verbosity normal
```
Gate threshold: **100% test pass rate with 0 regressions**.
