# NightCodeForge.Core.Concurrency

> **Assembly:** `NightCodeForge.Core` | **Layer:** Core / Synchronization  
> **Role:** Provides concurrency control, keyed mutual exclusion locks, and task cancellation token coordination.

---

## 1. Core Responsibility

`NightCodeForge.Core.Concurrency` manages thread synchronization and process-level mutual exclusion across workspaces and tasks. It ensures that while multiple tasks in the same workspace can run concurrently within isolated Git worktrees, operations targeting shared resources (such as mirror sync, branch squashing, or base repository actions) are strictly serialized. It also tracks active cancellation tokens per task.

## 2. Key Types & Contracts

| Type | Kind | Description |
| :--- | :--- | :--- |
| `WorkspaceLockProvider` | Class | Manages fine-grained `SemaphoreSlim` keyed locks for workspaces and individual task worktrees. |
| `TaskCancellationManager` | Class | Thread-safe registry of `CancellationTokenSource` instances per active task ID for graceful cancellation. |

## 3. Dependencies & Interaction Graph

- **Inbound (Who uses this):** `TaskOrchestrator`, `SignalRActivityBridgeService`, `NightCodeForge.Infrastructure.Git`, `NightCodeForge.Web`.
- **Outbound (What this relies on):** `Interfaces.IWorkspaceLockProvider`, `Interfaces.ITaskCancellationManager`, and standard .NET threading primitives.

## 4. Invariants & Architectural Rules

- **Rule 1 (Lock Scope Cleanliness):** All acquired locks implement `IDisposable` or `IAsyncDisposable` and must be consumed within a `using` or `await using` block to prevent deadlocks on exception.
- **Rule 2 (Independent Task Worktrees):** Parallel tasks in the same workspace must only lock their own `task-{id}` worktree lock unless mutating the shared base Git directory.
- **Rule 3 (Token Propagation):** Cancelling a task via `TaskCancellationManager.CancelTaskAsync` must trigger cancellation immediately across the associated stage, process, and inference stream.

## 5. Usage Example

```csharp
// Acquiring an isolated lock for a specific task's worktree
await using (await lockProvider.AcquireTaskLockAsync(workspaceId, taskId, cancellationToken))
{
    // Safe to perform git mutations in the task worktree
    await gitService.CommitAsync(worktreePath, "Checkpoint commit", cancellationToken);
}
```
