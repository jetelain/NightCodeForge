# NightCodeForge.Core

> **Assembly:** `NightCodeForge.Core` | **Layer:** Core / Domain  
> **Role:** Defines domain entities, interfaces, workflow pipeline orchestration, and business abstractions free of external framework dependencies.

---

## 1. Core Responsibility

`NightCodeForge.Core` encapsulates the enterprise and application business logic for NightCodeForge. It defines domain aggregates (tasks, workspaces, workflows, stage records), dependency resolution algorithms, workflow execution lifecycle contracts, concurrency management primitives, and repository interfaces without depending on concrete infrastructure (like EF Core, CLI binaries, or network APIs).

## 2. Key Types & Contracts

| Type | Kind | Description |
| :--- | :--- | :--- |
| `ForgeTask` | Class | Core aggregate representing an autonomous development task, its state, branch, and metadata. |
| `Workspace` | Class | Represents a Git-backed workspace, root path, default branches, and settings. |
| `WorkflowDefinition` | Class | Declarative specification of multi-stage pipelines, stages, and transition policies. |
| `IWorkflowPipeline` | Interface | Executes sequential and looped workflow stages for a task execution context. |
| `ITaskOrchestrator` | Interface | High-level orchestrator coordinating task scheduling, git branches, and pipeline dispatch. |
| `IDependencyResolutionEngine` | Interface | Topological sorter and cycle detector for task DAG dependencies. |
| `IWorkspaceLockProvider` | Interface | Concurrency coordinator managing per-workspace and per-task worktree synchronization. |
| `IAgentHarness` | Interface | Core contract for pluggable AI agent execution runtimes. |

## 3. Dependencies & Interaction Graph

- **Inbound (Who uses this):** `NightCodeForge.Infrastructure`, `NightCodeForge.Agents`, `NightCodeForge.Web`, `NightCodeForge.ScenarioRunner`, and test projects.
- **Outbound (What this relies on):** Minimal external dependencies: `Scriban` (templating), `Microsoft.Extensions.Logging.Abstractions`, `Microsoft.Extensions.DependencyInjection.Abstractions`. No database or network client packages.

## 4. Invariants & Architectural Rules

- **Rule 1 (Clean Architecture):** Core must never reference outer layers (`Infrastructure`, `Agents`, `Web`).
- **Rule 2 (Fail Fast):** Methods must throw dedicated domain exceptions (`WorkflowNotFoundException`, `CircularDependencyException`, etc.) on invalid state; synthetic fallback values are strictly forbidden.
- **Rule 3 (Cancellation Support):** All asynchronous methods must accept a `CancellationToken` and propagate it to sub-operations.
- **Rule 4 (No Optional DI):** Service constructors must require all mandatory dependencies without optional parameters (`= null`).

## 5. Usage Example

```csharp
// Example: Executing a task workflow via core abstractions
public async Task RunTaskAsync(int taskId, ITaskOrchestrator orchestrator, CancellationToken ct)
{
    var result = await orchestrator.ExecuteTaskAsync(taskId, ct);
    if (!result.IsSuccess)
    {
        throw new WorkflowExecutionException($"Task {taskId} failed: {result.FailureReason}");
    }
}
```
