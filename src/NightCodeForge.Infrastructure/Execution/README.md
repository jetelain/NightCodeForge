# NightCodeForge.Infrastructure.Execution

> **Assembly:** `NightCodeForge.Infrastructure` | **Layer:** Infrastructure / Execution Environments  
> **Role:** Implements execution environments (BareHost, WSL, Docker Container), SDK discovery, and process buffering.

---

## 1. Core Responsibility

`NightCodeForge.Infrastructure.Execution` manages the execution environments where autonomous agent commands, builds, and test suites are run. It abstracts differences between local bare-metal host execution (`BareHostExecutionEnvironment`), Windows Subsystem for Linux (`WslExecutionEnvironment`), and sandboxed Docker containers (`NightCodeForgeContainerExecutor`), while ensuring bounded memory usage via `HeadTailBuffer`.

## 2. Key Types & Contracts

| Type | Kind | Description |
| :--- | :--- | :--- |
| `ExecutionEnvironmentProvider` | Class | Resolves and instantiates the required `IExecutionEnvironment` based on the workspace configuration. |
| `BareHostExecutionEnvironment` | Class | Executes processes directly on the local operating system host. |
| `WslExecutionEnvironment` | Class | Dispatches processes into a designated WSL distribution using translated paths and POSIX bash shells. |
| `NightCodeForgeContainerExecutor` | Class | Executes tasks inside ephemeral Docker containers with bind mounts and isolated network tiers. |
| `HeadTailBuffer` | Class | Bounded circular memory buffer retaining first N and last M characters of process outputs to prevent memory overflow. |
| `HostSdkDiscoveryService` | Class | Detects installed SDKs (.NET, Node, Python, Rust) across host and WSL environments. |
| `WorkspacePathResolver` | Class | Resolves physical and guest paths for workspaces, worktrees, and cache directories. |

## 3. Dependencies & Interaction Graph

- **Inbound (Who uses this):** `TaskOrchestrator`, `NightCodeForge.Agents.Tools.BashTool`, `NightCodeForge.ScenarioRunner`.
- **Outbound (What this relies on):** `NightCodeForge.Core.Interfaces.IExecutionEnvironment`, `NightCodeForge.Core.Execution.SystemProcessRunner`, `CliWrap`.

## 4. Invariants & Architectural Rules

- **Rule 1 (Bounded Buffering):** Agent tool command output must never read unbounded streams directly into memory; use `HeadTailBuffer` to avoid context window blowup.
- **Rule 2 (Sandbox Guarantee):** Container environments must enforce resource boundaries and avoid mounting privileged host volumes.
- **Rule 3 (Path Fidelity):** Path translations between Windows host and WSL POSIX paths must be verified before command execution to prevent execution in incorrect directories.

## 5. Usage Example

```csharp
// Resolving the configured execution environment for a workspace
var environment = await environmentProvider.GetEnvironmentAsync(workspace, cancellationToken);
var execResult = await environment.ExecuteProcessAsync(new ProcessStartInfo
{
    FileName = "dotnet",
    Arguments = "build --configuration Release",
    WorkingDirectory = worktreePath
}, cancellationToken);
```
