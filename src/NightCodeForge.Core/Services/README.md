# NightCodeForge.Core.Services

> **Assembly:** `NightCodeForge.Core` | **Layer:** Core / Domain Services  
> **Role:** Implements domain-level coordination services including startup recovery, agent harness registry, and Markdown task sync.

---

## 1. Core Responsibility

`NightCodeForge.Core.Services` implements cross-cutting domain services that operate across multiple aggregates and interfaces. This includes `StartupRecoveryService` to recover crashed or ungracefully terminated tasks upon boot, `HarnessRegistry` to locate agent harnesses dynamically, and `TaskMarkdownSyncService` for two-way synchronization between database tasks and repository markdown files.

## 2. Key Types & Contracts

| Type | Kind | Description |
| :--- | :--- | :--- |
| `IStartupRecoveryService` | Interface | Contract for recovering orphaned tasks and stale Git locks after host crashes or restarts. |
| `StartupRecoveryService` | Class | Identifies `InProgress` tasks on boot, purges stale `.git/index.lock` files, commits WIP recovery checkpoints, and re-queues tasks. |
| `HarnessRegistry` | Class | Thread-safe registry mapping harness identifiers (e.g. `crucible`, `acp`) to `IAgentHarness` instances. |
| `ITaskMarkdownSyncService` | Interface | Contract for synchronizing task entities with filesystem Markdown files (`tasks/TASK-*.md`). |
| `TaskMarkdownSyncService` | Class | Parses YAML frontmatter and markdown descriptions to ensure Git and DB stay consistent. |
| `GitLockPurgeHelper` | Class | Utility for safely finding and purging abandoned Git lock files in repositories and worktrees. |

## 3. Dependencies & Interaction Graph

- **Inbound (Who uses this):** Application startup lifecycle (`Program.cs`), `TaskOrchestrator`, `NightCodeForge.ScenarioRunner`.
- **Outbound (What this relies on):** `Domain`, `Repositories`, `Interfaces.IGitWorkspaceManager`, `Interfaces.IWorkspaceLockProvider`.

## 4. Invariants & Architectural Rules

- **Rule 1 (Crash Safety):** `StartupRecoveryService` must never leave a task in `InProgress` state; it must transition orphaned tasks to `Failed` or `Pending` with a `WIP-RECOVERY` commit.
- **Rule 2 (Atomic File Sync):** Markdown synchronization must parse frontmatter strictly; corrupted frontmatter throws `TaskMarkdownSyncException` rather than corrupting the database.
- **Rule 3 (Lock Hygiene):** Git lock files are only purged if no active process holds a handle or if the startup sequence confirms the task was killed.

## 5. Usage Example

```csharp
// Executing startup recovery on application launch
var recoveredCount = await recoveryService.RecoverOrphanedTasksAsync(cancellationToken);
logger.LogInformation("Startup recovery completed. Recovered {Count} orphaned tasks.", recoveredCount);
```
