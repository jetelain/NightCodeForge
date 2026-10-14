# 🔀 Phase 2: Git CLI Subsystem & Ext4 Workspace Mirroring (CliWrap)

> **Implementation Roadmap & Technical Specification**  
> *Parent Plan:* [docs/PLAN.md](../PLAN.md) | *Status:* Completed

---

## 1. Executive Objective

Deliver a robust, native Git CLI engine (strictly without LibGit2Sharp) capable of safe context switching, WIP checkpointing, and cross-filesystem Git mirroring via **CliWrap**. This phase completely eliminates the notorious **WSL2 9p filesystem performance bottleneck** on Windows hosts by maintaining an isolated Git clone on ext4 with automated local remote push/fetch synchronization.

---

## 2. Detailed Task Breakdown

```mermaid
flowchart TD
    subgraph Step1 ["1. Git CLI Runner"]
        CliWrap["GitCliRunner (CliWrap)\n- Timeout Governance\n- Stdout/Stderr Capture\n- Typed GitCommandException"]
    end

    subgraph Step2 ["2. Git Workspace Manager"]
        Branches["Branch Isolation (feature/issue-{id})"]
        Wip["WIP Checkpoint Commits (git add -A && git commit)"]
        Diffs["Noise-Filtered Diff Generation (-U2, lockfile exclusions)"]
        Squash["Squash & Merge Engine"]
    end

    subgraph Step3 ["3. Ext4 Mirroring Protocol"]
        MirrorSetup["Ext4 Mirror Creation (/var/ncf/workspaces/{id}/repo)"]
        ConfigUpdate["git config receive.denyCurrentBranch updateInstead"]
        SyncPush["Bidirectional Push/Fetch Sync"]
        DirtyFallback["Dirty Host Tree Fallback (feature/issue-{id}-ncf)"]
    end

    subgraph Step4 ["4. Integration Tests"]
        Tests["NightCodeForge.Infrastructure.Tests\n(Real temp Git repos & WSL2 sync)"]
    end

    Step1 --> Step2
    Step2 --> Step3
    Step3 --> Step4
```

### Step 2.1: Native Git CLI Runner (`NightCodeForge.Infrastructure/Git`)
* **Contract:** `IGitCliRunner`
  ```csharp
  public interface IGitCliRunner
  {
      Task<GitCommandResult> ExecuteAsync(
          string workingDirectory, 
          IEnumerable<string> argumentsList, 
          int? timeoutSeconds = null, 
          CancellationToken cancellationToken = default);
  }
  ```
* **Implementation Details:**
  - Delegates execution to `IProcessRunner` with structured `ArgumentList` (supports WSL2 path translation and native host execution).
  - Applies a default timeout policy (30 seconds for standard commands; 120 seconds for deep clones/fetches).
  - Captures exact exit codes. Throws `GitCommandException` containing the full command string, argument list, working directory, exit code, and stderr on non-zero exit codes.
  - Enforces `PAGER=cat` and `GIT_TERMINAL_PROMPT=0` to guarantee commands never hang awaiting interactive terminal input.

### Step 2.2: Git Workspace Manager (`NightCodeForge.Infrastructure/Git`)
* **Contract:** `IGitWorkspaceManager`
  ```csharp
  public interface IGitWorkspaceManager
  {
      Task EnsureRepositoryValidAsync(string repoPath, CancellationToken cancellationToken = default);
      Task CheckoutIssueBranchAsync(Workspace workspace, int issueId, string baseBranch, CancellationToken cancellationToken = default);
      Task<string> CreateWipCheckpointAsync(Workspace workspace, int issueId, string stageName, string summary, CancellationToken cancellationToken = default);
      Task<string> GenerateDiffAsync(Workspace workspace, int issueId, string baseBranch, CancellationToken cancellationToken = default);
      Task SquashAndMergeAsync(Workspace workspace, int issueId, string baseBranch, string commitMessage, CancellationToken cancellationToken = default);
      Task RevertOrCleanAsync(Workspace workspace, CancellationToken cancellationToken = default);
  }
  ```
* **Core Operations:**
  1. **Branch Checkout (`CheckoutIssueBranchAsync`):**
     - Checks if `feature/issue-{id}` exists. If not, creates it branching from `baseBranch`: `git checkout -B feature/issue-{id} {baseBranch}`.
  2. **WIP Checkpoint (`CreateWipCheckpointAsync`):**
     - Executes `git add -A` followed by `git commit -m "WIP: [Issue #{id}][Stage: {stageName}] {summary}" --no-verify`.
     - Returns the 40-character SHA commit hash.
  3. **Noise-Filtered Diff Generation (`GenerateDiffAsync`):**
     - Executes `git diff {baseBranch}...feature/issue-{id} -U2 -- . ":(exclude)package-lock.json" ":(exclude)pnpm-lock.yaml" ":(exclude)*.Designer.cs"`.
     - Filters noisy build artifacts and generated files to keep review diffs readable for both humans and AI auditor agents.
  4. **Squash and Merge (`SquashAndMergeAsync`):**
     - Checks out `baseBranch`, executes `git merge --squash feature/issue-{id}`, and commits with the final approved summary message.

### Step 2.3: Cross-Environment Working Clone Protocol (`GitMirrorSyncService`)
* **The Performance & Cross-Platform Problem:**
  Agents run strictly on Unix. On Windows hosts, direct access to NTFS via `/mnt/c/` suffers from the 9p latency bottleneck (up to 10x slower). NightCodeForge requires WSL2 on Windows and establishes isolated clones on ext4.
* **The Canonical Clone Architecture:**
  1. **Working Clone Path:**
     - The clone repository where the agent works is **always** `~/.nightcodeforge/wks/{workspaceId}` (in WSL2 ext4 on Windows, host home on Unix).
  2. **Remote URL Configuration (`host-origin`):**
     - On Windows: The remote pointing to the developer's workspace is transformed to its WSL path `/mnt/{transformed_windows_path}`:
       `git remote add host-origin /mnt/c/Users/.../TargetProject`
     - On Unix: The remote points directly to the developer's workspace path.
  3. **Git Execution Context (`push`, `pull`, `merge` on `main`):**
     - On Unix: Executed directly on the bare host.
     - On Windows: Executed strictly inside plain WSL2 (`wsl.exe -d <distrib> -- git ...`).
  4. **Crucible Host I/O Bridge (Windows):**
     - Crucible runs in-process on the Windows host and accesses the working clone via UNC share:
       `\\wsl.localhost\{distrib}\home\{username}\.nightcodeforge\wks\{workspaceId}\`
  5. **Host Configuration (`updateInstead`):**
     - Configures the developer's host repository: `git config receive.denyCurrentBranch updateInstead`.
     - This allows `git push host-origin feature/issue-{id}` to update the branch on the host repository even if the developer has that branch currently checked out in Visual Studio or VS Code.
  6. **Dirty Host Tree Fallback Protocol:**
     - If the developer has uncommitted edits to tracked files in their host IDE, Git's `updateInstead` rejects the push with an error.
     - `GitMirrorSyncService` catches the push rejection, immediately pushes to `refs/heads/feature/issue-{id}-ncf`, and emits a high-priority SignalR alert to the web dashboard:
       > *"⚠️ Host Working Tree Dirty: Changes pushed to `feature/issue-{id}-ncf`. Please commit or stash local edits in your host IDE to reconcile with the active agent branch."*
  7. **Synchronization Lifecycle:**
     - **Stage Inception:** `git fetch host-origin <baseBranch>` ensures the agent works on the latest base commit.
     - **Stage Checkpoint / Completion:** `git push host-origin feature/issue-{id}` keeps the host repository in sync.

---

## 3. Verification & Test Plan

| Test Class | Focus Area | Scenarios Covered |
| :--- | :--- | :--- |
| **`GitCliRunnerTests`** | Command Execution | Timeout cancellation, stdout/stderr capture, non-zero exit code exceptions, environment variable injection (`PAGER=cat`). |
| **`GitWorkspaceManagerTests`** | Branch & Checkpoints | Branch checkout from base, dirty file staging with `add -A`, WIP commit generation with `--no-verify`, squash merge on base branch. |
| **`NoiseFilteredDiffTests`** | Diff Generation | `-U2` context window reduction, exclusion of `package-lock.json` and generated files from diff payload. |
| **`GitMirrorSyncServiceTests`** | Ext4 Mirroring | Remote initialization, bidirectional fetch/push sync, `updateInstead` branch update, dirty tree rejection fallback. |

---

## 4. Definition of Done (DoD) Checklist

- [x] `GitCliRunner` wraps `CliWrap` with timeout governance and typed exceptions.
- [x] Safe branch isolation and WIP commit checkpointing verified.
- [x] Noise-filtered diff generation verified with lockfile exclusions.
- [x] Ext4 Git mirroring protocol verified with local remote fetch and push.
- [x] `denyCurrentBranch updateInstead` and dirty-tree fallback verified.
- [x] 100% of integration tests in `NightCodeForge.Infrastructure.Tests` pass using real temporary Git repositories.
