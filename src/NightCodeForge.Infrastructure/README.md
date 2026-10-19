# NightCodeForge.Infrastructure

> **Assembly:** `NightCodeForge.Infrastructure` | **Layer:** Infrastructure  
> **Role:** Provides concrete implementations for data access (EF Core/SQLite), Git CLI operations, Hangfire queues, execution environments, and SignalR.

---

## 1. Core Responsibility

`NightCodeForge.Infrastructure` contains all external framework adapters, file system drivers, database contexts, background workers, and network gateways. It implements the contracts defined in `NightCodeForge.Core`, enabling the application to persist data in SQLite, manipulate Git repositories via CLI wrappers, execute commands across BareHost, WSL, or Docker containers, and distribute real-time events via SignalR.

## 2. Key Types & Contracts

| Type | Kind | Description |
| :--- | :--- | :--- |
| `AppDbContext` | Class | Entity Framework Core context configuring SQLite database persistence with WAL pragma. |
| `GitWorkspaceManager` | Class | Implements `IGitWorkspaceManager` for isolated Git worktree provisioning and WIP squashing. |
| `BareHostExecutionEnvironment` | Class | Executes commands natively on the host operating system. |
| `WslExecutionEnvironment` | Class | Translates paths and executes commands within Windows Subsystem for Linux distributions. |
| `NightCodeForgeContainerExecutor` | Class | Containerized execution environment running inside isolated Docker containers. |
| `HangfireTaskJobQueue` | Class | Enqueues and schedules pipeline jobs across prioritized Hangfire queues (`llm-urgent`, `llm-inference`). |
| `OrchestratorHub` | Class | ASP.NET Core SignalR hub broadcasting agent thought deltas and stage lifecycle events. |
| `OllamaRouterGatewayService` | Class | HTTP gateway communicating with Ollama inference router instances. |

## 3. Dependencies & Interaction Graph

- **Inbound (Who uses this):** `NightCodeForge.Web`, `NightCodeForge.ScenarioRunner`.
- **Outbound (What this relies on):** `NightCodeForge.Core`, `Microsoft.EntityFrameworkCore.Sqlite`, `Hangfire.Core`, `CliWrap`, `Microsoft.AspNetCore.SignalR`.

## 4. Invariants & Architectural Rules

- **Rule 1 (Pragma Integrity):** SQLite connections must enforce Write-Ahead Logging (`PRAGMA journal_mode=WAL;`) and foreign keys (`PRAGMA foreign_keys=ON;`).
- **Rule 2 (Worktree Isolation):** Task execution must never modify the base repository working directory directly; work is performed in ephemeral native worktrees (`task-{id}`).
- **Rule 3 (Fail Fast Infrastructure):** Failed external processes or missing CLI binaries must throw explicit exceptions (`GitCommandException`, `FileNotFoundException`) rather than failing silently.

## 5. Usage Example

```csharp
// Registering infrastructure services in DI
services.AddNightCodeForgeInfrastructure(configuration);
services.AddHangfireInfrastructure(configuration);
```
