# NightCodeForge.Core.Repositories

> **Assembly:** `NightCodeForge.Core` | **Layer:** Core / Data Access Contracts  
> **Role:** Defines domain repository contracts for persisting tasks, workspaces, workflows, and checkpoints.

---

## 1. Core Responsibility

`NightCodeForge.Core.Repositories` defines the persistence abstractions for all aggregate roots in NightCodeForge. It enables domain services and workflow orchestrators to retrieve and persist state without any direct dependency on Entity Framework Core, SQLite, or underlying relational database schemas.

## 2. Key Types & Contracts

| Type | Kind | Description |
| :--- | :--- | :--- |
| `ITaskRepository` | Interface | Persistence contract for `ForgeTask` entities, status queries, priority ordering, and history logs. |
| `IWorkspaceRepository` | Interface | Persistence contract for `Workspace` entities, filesystem path lookups, and branch settings. |
| `IWorkflowRepository` | Interface | Persistence contract for retrieving declarative `WorkflowDefinition` specifications. |
| `InMemoryWorkflowRepository` | Class | Thread-safe, in-memory implementation seeded with standard workflows for tests and standalones. |

## 3. Dependencies & Interaction Graph

- **Inbound (Who uses this):** `TaskOrchestrator`, `DependencyResolutionEngine`, `TaskMarkdownSyncService`, `NightCodeForge.Web`.
- **Outbound (What this relies on):** `NightCodeForge.Core.Domain`. Implemented by `NightCodeForge.Infrastructure.Data`.

## 4. Invariants & Architectural Rules

- **Rule 1 (Fail Fast on Missing Records):** If an entity requested by ID does not exist, throw a dedicated domain exception (e.g. `WorkflowNotFoundException`) rather than returning synthetic dummy records.
- **Rule 2 (Unit of Work Concurrency):** Repository calls operate within scoped transactions or contexts; race conditions during concurrent state updates must be guarded by locks.
- **Rule 3 (Pagination & Filter Efficiency):** Large task query methods must support filtering by workspace and status to avoid loading entire databases into memory.

## 5. Usage Example

```csharp
// Fetching a task and verifying existence
var task = await taskRepository.GetByIdAsync(taskId, cancellationToken);
if (task == null)
{
    throw new KeyNotFoundException($"ForgeTask with ID {taskId} was not found.");
}
```
