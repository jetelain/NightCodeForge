# NightCodeForge.Core.Dependencies

> **Assembly:** `NightCodeForge.Core` | **Layer:** Core / Dependency Resolution  
> **Role:** Resolves directed acyclic graphs (DAG) of task dependencies, detects cycles, and evaluates execution readiness.

---

## 1. Core Responsibility

`NightCodeForge.Core.Dependencies` provides topological sorting and dependency resolution for engineering tasks. It ensures that child tasks only become eligible for execution (`ReadyForExecution = true`) once all parent tasks have successfully completed and their code changes have been merged. It strictly prevents cyclic task relationships by detecting cycles before persistence.

## 2. Key Types & Contracts

| Type | Kind | Description |
| :--- | :--- | :--- |
| `IDependencyResolutionEngine` | Interface | Contract for evaluating task dependencies, topological ordering, and cycle detection. |
| `DependencyResolutionEngine` | Class | Implementation utilizing Kahn's algorithm or DFS to detect cycles and calculate runnable tasks. |
| `ITaskDependencyStore` | Interface | Storage abstraction for loading parent-child relationship graphs for a workspace. |
| `CircularDependencyException` | Class | Domain exception thrown when a dependency link would introduce a directed cycle. |

## 3. Dependencies & Interaction Graph

- **Inbound (Who uses this):** `TaskOrchestrator`, `TaskMarkdownSyncService`, `NightCodeForge.Web` (DevQueue board).
- **Outbound (What this relies on):** `Domain.ForgeTask`, `Domain.TaskDependency`, and `Exceptions`.

## 4. Invariants & Architectural Rules

- **Rule 1 (Cycle Prohibition):** Creating or updating any task dependency link that results in a cycle must fail fast with `CircularDependencyException`.
- **Rule 2 (Deterministic Ordering):** Topological sorts must produce deterministic execution sequences when resolving parallel branch paths.
- **Rule 3 (Cascade Activation):** When a parent task transitions to `Completed`, all dependent children must be re-evaluated for readiness without race conditions.

## 5. Usage Example

```csharp
// Validating that adding a new dependency does not create a cycle
var isCyclic = await dependencyEngine.HasCycleAsync(workspaceId, parentTaskId, childTaskId, ct);
if (isCyclic)
{
    throw new CircularDependencyException($"Adding dependency {parentTaskId}->{childTaskId} creates a cycle.");
}

await dependencyStore.AddDependencyAsync(parentTaskId, childTaskId, ct);
```
