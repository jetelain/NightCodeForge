# 🎨 Step 8.6: Dynamic Blazor UI Integration & Workflow Management

> **Technical Specification & Implementation Guide**  
> *Parent Plan:* [docs/plan/08_DATABASE_DRIVEN_WORKFLOWS_AND_STAGES.md](../08_DATABASE_DRIVEN_WORKFLOWS_AND_STAGES.md)  
> *Status:* Ready for Implementation

---

## 1. Objective

Upgrade the NightCodeForge Blazor UI components to dynamically render and configure workflows and stages: make `PipelineStepper.razor` render arbitrary stage sequences, update `TaskExecutionInspector.razor`, add a workflow selector to task creation flows, and provide a dedicated **Workflow Management** view in Settings.

---

## 2. Component Enhancements

### 2.1 `PipelineStepper.razor` (Dynamic Stage Binding)
- Remove hardcoded `CreateDefaultStages()` static array.
- Accept parameter: `[Parameter] public IReadOnlyList<StageDefinition>? WorkflowStages { get; set; }`
- Fallback gracefully: if not provided or empty, resolve from standard default stages.
- Dynamically render:
  - Header: `#@stage.SequenceOrder` and badge (`⚡ GPU` for Agentic, `⚙️ CPU` for Deterministic).
  - Title: `@stage.DisplayName`.
  - Icon circle: renders `@stage.Icon` (e.g. 🔍, ⚙️, 💻, 🧪, 🧐, 🛡️) when idle or completed, with spinner pulse during execution and status overlay indicators (✓, ✕, ✋).
  - Active connectors between consecutive stages (`@stage.SequenceOrder < WorkflowStages.Count`).

### 2.2 `TaskExecutionInspector.razor`
- When loading task details, include `task.Workflow` and `task.Workflow.Stages.OrderBy(s => s.SequenceOrder)`.
- Pass dynamic stages into `PipelineStepper`.
- Display active workflow name, icon, and badge in the task inspector header bar:
  `@task.Workflow.Icon Workflow: @task.Workflow.Name (@task.Workflow.Stages.Count stages)`

### 2.3 Task Creation Workflows Selector
- **`TaskCreationModal.razor` & Backlog UI**:
  - Load available workflows (`IWorkflowRepository.GetWorkflowsAsync()`).
  - Provide a `<select>` dropdown displaying `@w.Icon @w.Name` to pick the target workflow (defaulting to `Workspace.DefaultWorkflowId` or the global default).
- **Ideation Proposal Ingestion**:
  - When proposals are ingested from Ideation, attach the workspace default workflow automatically.

### 2.4 New Settings Tab: `WorkflowManagementTab.razor`
- Add new tab in `/settings`: **"Workflows & Stages"** (`🔀 Workflows`).
- Features:
  - List of available workflows displaying `@w.Icon @w.Name`, code, stage count, and default badge.
  - Interactive stage inspector: expandable tree/card list showing each stage in order:
    - Order, Icon, StageId, DisplayName, Kind (GPU/CPU badge), C# Implementation Type.
    - Configuration details: Agent profile / required mode for Agentic, Action / commands / thresholds for Deterministic.
  - Action: "Clone Workflow" (creates custom editable workspace copy).

---

## 3. Verification Criteria
1. Tasks with 3-stage, 5-stage, and 6-stage workflows display accurately in `PipelineStepper` with matching connectors.
2. Maintainers can select different workflows when creating a task in Backlog.
3. The Settings Workflow tab allows inspecting all stage parameters and configuration payloads.
