# NightCodeForge.Core.Workflow

> **Assembly:** `NightCodeForge.Core` | **Layer:** Core / Workflow Engine  
> **Role:** Implements the state machine, stage lifecycle execution, prompt template compilation, and pipeline orchestration.

---

## 1. Core Responsibility

`NightCodeForge.Core.Workflow` provides the pipeline execution engine that moves tasks through ordered lifecycle stages (e.g. Planning, Implementation, Review, Verification). It manages transient execution data across stages (`PipelineDataBag`), renders contextual prompt templates via Scriban, handles human interruption pauses, and governs iterative loopbacks upon failed quality gates.

## 2. Key Types & Contracts

| Type | Kind | Description |
| :--- | :--- | :--- |
| `IWorkflowPipeline` | Interface | Executes the workflow stage sequence against an active `PipelineContext`. |
| `WorkflowPipelineEngine` | Class | Default implementation executing stages, handling retries, loopbacks, and records. |
| `IWorkflowStage` | Interface | Contract for an individual executable stage unit (agent harness invocation or command execution). |
| `PipelineContext` | Class | Holds runtime contextual state for a task execution (task, workspace, data bag, cancellation token). |
| `PipelineDataBagKeys` | Static Class | Well-known keys stored in `PipelineDataBag` (e.g. Git diffs, test diagnostics, review comments). |
| `ITaskOrchestrator` | Interface | Orchestrates end-to-end task setup, worktree allocation, pipeline dispatch, and result finalization. |
| `IPromptTemplateRenderer` | Interface | Compiles and renders Scriban prompt templates populated with runtime context. |
| `HumanInterruptionException` | Class | Control-flow exception thrown when an agent calls `ask`, safely pausing the pipeline. |
| `RequestChangesException` | Class | Exception triggering backward loopbacks to an earlier stage (e.g. implementation retry). |

## 3. Dependencies & Interaction Graph

- **Inbound (Who uses this):** Background job processors (Hangfire in `Infrastructure`), and API controllers / UI endpoints.
- **Outbound (What this relies on):** `Domain`, `Repositories`, `Interfaces`, `Streaming`, and `Scriban` template engine.

## 4. Invariants & Architectural Rules

- **Rule 1 (Idempotent Checkpoints):** Stage completion records must be persisted immediately after each stage finishes to guarantee crash resilience.
- **Rule 2 (Interruption Safety):** When `HumanInterruptionException` occurs, intermediate changes must be preserved in git worktree WIP commits and the task marked `Paused`.
- **Rule 3 (Bounded Retries):** Stage loopbacks and retries must strictly respect `MaxIterations` configured in `StageDefinition` to prevent infinite LLM loops.

## 5. Usage Example

```csharp
// Executing a pipeline context
var context = new PipelineContext(task, workspace, cancellationToken);
context.DataBag.Set(PipelineDataBagKeys.TaskDescription, task.Description);

var result = await pipeline.ExecuteAsync(context);
if (result.IsInterrupted)
{
    logger.LogInformation("Pipeline paused for human clarification.");
}
```
