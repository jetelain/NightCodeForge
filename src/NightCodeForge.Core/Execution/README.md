# NightCodeForge.Core.Execution

> **Assembly:** `NightCodeForge.Core` | **Layer:** Core / Process Execution  
> **Role:** Provides low-level operating system process launching, argument quoting, and background task management.

---

## 1. Core Responsibility

`NightCodeForge.Core.Execution` provides process execution primitives and shell argument sanitization. It implements `SystemProcessRunner` to launch OS processes with asynchronous stdout and stderr streaming, exit code capture, and timeout enforcement, as well as helpers for cross-platform shell quoting (PowerShell, Bash, CMD).

## 2. Key Types & Contracts

| Type | Kind | Description |
| :--- | :--- | :--- |
| `SystemProcessRunner` | Class | Executes OS processes asynchronously with stream redirection, process killing on token cancellation, and error handling. |
| `IBackgroundTaskRunner` | Interface | Contract for executing fire-and-forget or background operations with managed logging. |
| `BackgroundTaskRunner` | Class | Implementation providing exception handling and logging for detached tasks. |
| `ShellQuoting` | Static Class | Cross-platform argument escaping for Bash, Windows CMD, and PowerShell scripts. |
| `SingleExecutionEnvironmentProvider` | Class | Provides a single static `IExecutionEnvironment` instance for lightweight setups and tests. |

## 3. Dependencies & Interaction Graph

- **Inbound (Who uses this):** `NightCodeForge.Infrastructure.Execution`, `NightCodeForge.Agents.Tools.BashTool`, `CliWrap` adapters.
- **Outbound (What this relies on):** `Interfaces.IProcessRunner`, `Interfaces.IExecutionEnvironment`, `System.Diagnostics.Process`.

## 4. Invariants & Architectural Rules

- **Rule 1 (Process Tree Termination):** When a cancellation token fires, `SystemProcessRunner` must terminate the entire child process tree (`Kill(entireProcessTree: true)`) to prevent zombie processes.
- **Rule 2 (No Shell Injection):** Commands constructed for execution environments must escape parameters via `ShellQuoting` or structured argument lists.
- **Rule 3 (Buffer Safety):** Output stream reading must not cause deadlocks when stdout or stderr fill OS pipe buffers.

## 5. Usage Example

```csharp
// Executing a command line process with redirected outputs
var result = await processRunner.RunAsync(new ProcessStartInfo
{
    FileName = "git",
    Arguments = "status --porcelain",
    WorkingDirectory = repoPath
}, cancellationToken);

if (result.ExitCode != 0)
{
    throw new InvalidOperationException($"Git failed: {result.StandardError}");
}
```
