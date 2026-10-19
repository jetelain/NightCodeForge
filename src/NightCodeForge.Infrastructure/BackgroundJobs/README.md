# NightCodeForge.Infrastructure.BackgroundJobs

> **Assembly:** `NightCodeForge.Infrastructure` | **Layer:** Infrastructure / Background Processing  
> **Role:** Configures Hangfire background processing, single-worker priority queues, and job enqueuing.

---

## 1. Core Responsibility

`NightCodeForge.Infrastructure.BackgroundJobs` orchestrates background task execution using Hangfire backed by SQLite. It enforces strict concurrency serialization for LLM inference workloads by defining dedicated, prioritized queues (`llm-urgent` for human-interrupted resumptions and `llm-inference` for normal workflow progression) configured with a single worker to prevent GPU VRAM contention.

## 2. Key Types & Contracts

| Type | Kind | Description |
| :--- | :--- | :--- |
| `HangfireQueues` | Static Class | Defines queue name constants: `LlmUrgent` (`"llm-urgent"`) and `LlmInference` (`"llm-inference"`). |
| `HangfireTaskJobQueue` | Class | Implements `ITaskJobQueue` to enqueue task orchestration jobs into Hangfire with priority routing. |
| `HangfireConfigurationExtensions` | Static Class | Configures Hangfire services, SQLite storage provider, dashboard, and worker concurrency limits. |

## 3. Dependencies & Interaction Graph

- **Inbound (Who uses this):** `NightCodeForge.Web`, `TaskOrchestrator`, `StartupRecoveryService`.
- **Outbound (What this relies on):** `Hangfire.Core`, `Hangfire.Storage.SQLite`, `NightCodeForge.Core.Workflow.ITaskJobQueue`.

## 4. Invariants & Architectural Rules

- **Rule 1 (Single-Worker Serialization):** The LLM inference queues must have concurrency limited to 1 worker per queue to avoid saturating local GPU VRAM and Ollama router instances.
- **Rule 2 (Priority Routing):** Resumed tasks following a human question answer must enqueue to `llm-urgent` ahead of newly queued pending tasks.
- **Rule 3 (Idempotent Job Dispatch):** Enqueued jobs must carry task identifiers and re-verify task status upon execution to prevent duplicate pipeline runs.

## 5. Usage Example

```csharp
// Enqueuing a task for background processing
await taskJobQueue.EnqueueTaskAsync(taskId, priority: TaskJobPriority.High, cancellationToken);
```
