# NightCodeForge.Infrastructure.Git

> **Assembly:** `NightCodeForge.Infrastructure` | **Layer:** Infrastructure / Source Control  
> **Role:** Implements Git CLI operations, native worktree lifecycles, branch management, and WIP commit squashing.

---

## 1. Core Responsibility

`NightCodeForge.Infrastructure.Git` drives all source control interactions using native `git` CLI invocations via `CliWrap`. It provisions isolated native worktrees (`task-{id}`) to enable concurrent task execution without dirtying the primary repository checkout, automates incremental WIP checkpoint commits, calculates diffs, and orchestrates final squash-merges into target branches.

## 2. Key Types & Contracts

| Type | Kind | Description |
| :--- | :--- | :--- |
| `GitWorkspaceManager` | Class | Creates, manages, and cleans up task worktrees; creates WIP checkpoint commits and squashes branches. |
| `GitCliRunner` | Class | Executes arbitrary Git CLI commands with argument escaping, timeout handling, and exit code validation. |
| `GitMirrorSyncService` | Class | Synchronizes local workspace repositories with upstream remotes or source working trees. |
| `WslPathConverter` | Static Class | Converts Windows filesystem paths (`C:\...`) to POSIX WSL paths (`/mnt/c/...`) and vice versa. |
| `LoggingGitAlertNotifier` | Class | Captures and logs non-fatal Git warnings and merge conflicts. |

## 3. Dependencies & Interaction Graph

- **Inbound (Who uses this):** `TaskOrchestrator`, `StartupRecoveryService`, `NightCodeForge.Web`.
- **Outbound (What this relies on):** `NightCodeForge.Core.Interfaces.IGitWorkspaceManager`, `CliWrap`, local `git` binary installed on host/WSL.

## 4. Invariants & Architectural Rules

- **Rule 1 (Worktree Isolation):** All code edits, builds, and test runs occur exclusively inside dedicated task worktrees; the primary repository HEAD is never checked out to a feature branch directly.
- **Rule 2 (Crash Checkpoints):** Work in progress must be committed with `WIP: [stage]` checkpoints prior to human interruptions or stage transitions.
- **Rule 3 (Safe Lock Cleanup):** Abandoned `.git/worktrees/<name>/locked` and `index.lock` files must be purged safely during recovery without affecting active sibling worktrees.

## 5. Usage Example

```csharp
// Creating an isolated worktree for a task
var worktreePath = await gitWorkspaceManager.CreateTaskWorktreeAsync(
    workspace,
    task,
    baseBranch: "main",
    cancellationToken);

// Worktree is now ready at ~/.nightcodeforge/wks/{workspaceId}/worktrees/task-{taskId}
```
