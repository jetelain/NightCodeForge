# NightCodeForge.Core.Interfaces

> **Assembly:** `NightCodeForge.Core` | **Layer:** Core / Abstractions  
> **Role:** Defines contracts for infrastructure adapters, execution environments, Git operations, and external services.

---

## 1. Core Responsibility

`NightCodeForge.Core.Interfaces` establishes the Dependency Inversion boundary between core business logic and external infrastructure. By specifying contracts for Git worktrees, process execution, agent harnesses, background locks, and inference gateways, it decouples the domain from concrete implementations (e.g. LibGit2Sharp vs. Git CLI, local host vs. Docker container).

## 2. Key Types & Contracts

| Type | Kind | Description |
| :--- | :--- | :--- |
| `IAgentHarness` | Interface | Executes an agent turn given a prompt, session context, and allowed tools. |
| `IExecutionEnvironment` | Interface | Environment abstraction (BareHost, WSL, Container) providing process execution and file paths. |
| `IGitWorkspaceManager` | Interface | Manages isolated native Git worktrees per task (`task-{id}`) and WIP checkpoints. |
| `IGitCliRunner` | Interface | Direct Git CLI command runner executing queries, branch operations, and diffs. |
| `IProcessRunner` | Interface | Low-level abstraction for launching operating system processes with stdout/stderr capture. |
| `IWorkspaceLockProvider` | Interface | Concurrency contract granting mutual exclusion locks across workspaces and tasks. |
| `ITaskCancellationManager` | Interface | Coordinates real-time task cancellation tokens and graceful execution interruption. |
| `ILlmGatewayService` | Interface | Gateway interface communicating with the Ollama router endpoint. |

## 3. Dependencies & Interaction Graph

- **Inbound (Who uses this):** `Workflow`, `Services`, `NightCodeForge.Agents`, `NightCodeForge.Web`.
- **Outbound (What this relies on):** `NightCodeForge.Core.Domain` and standard system abstractions. Implemented by `NightCodeForge.Infrastructure` and `NightCodeForge.Agents`.

## 4. Invariants & Architectural Rules

- **Rule 1 (Strict Asynchrony):** All I/O-bound contracts must return `Task` or `Task<T>` and accept a `CancellationToken`.
- **Rule 2 (No Leaky Implementation Types):** Interfaces must never expose implementation-specific types (e.g., CliWrap types, EF Core classes, or raw HTTP response objects).
- **Rule 3 (Zero Optional Defaults):** Contract signatures must not use optional default values that could conceal missing parameters.

## 5. Usage Example

```csharp
// Executing a command in the task's execution environment
var execEnv = await envProvider.GetEnvironmentAsync(workspace, ct);
var result = await execEnv.ExecuteProcessAsync(new ProcessStartInfo
{
    FileName = "dotnet",
    Arguments = "test --no-build",
    WorkingDirectory = worktreePath
}, ct);
```
