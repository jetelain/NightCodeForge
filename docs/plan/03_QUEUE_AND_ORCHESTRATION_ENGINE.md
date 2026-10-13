# ⚡ Phase 3: Hangfire Queue & Orchestration Engine

> **Implementation Roadmap & Technical Specification**  
> *Parent Plan:* [docs/PLAN.md](../PLAN.md) | *Status:* Completed

---

## 1. Executive Objective

Implement the non-blocking asynchronous development queue and central orchestrator for **NightCodeForge**. Enforce strict single-GPU concurrency (`WorkerCount = 1`) to eliminate local VRAM thrashing, provide dual-priority queueing (`llm-urgent` vs. `llm-inference`), capture turn-by-turn execution metrics, manage the clean interruption lifecycle (`WaitingForHuman`), and ensure automated crash recovery upon unexpected host reboots.

---

## 2. Detailed Task Breakdown

```mermaid
flowchart TD
    subgraph Step1 ["1. Queue Concurrency Engine"]
        Hangfire["Hangfire with SQLite Storage\n- LLM Inference: WorkerCount = 1 (llm-urgent, llm-inference)\n- Utility: WorkerCount = 4 (default)\n- AutomaticRetry = 0"]
    end

    subgraph Step2 ["2. Central Issue Orchestrator"]
        Orchestrator["IssueOrchestrator\n- DAG Readiness Check\n- Workspace Lock Acquisition\n- Stage Execution & Harness Invocation\n- Step Metrics Recording"]
        Lifecycle{"Stage Execution Result?"}
        Wait["WaitingForHuman:\n- Save Question & Checkpoint\n- Git WIP Commit\n- Clean Job Success (Free GPU!)"]
        Complete["StageCompleted:\n- Advance Pipeline / Transition to Review"]
        Fail["Failed:\n- Log Error & Transition to Failed"]
    end

    subgraph Step3 ["3. Resumption & Review Lifecycle"]
        Answer["SubmitHumanAnswerAsync:\n- Save Answer -> Enqueue to llm-urgent"]
        RequestChanges["RequestChangesAsync:\n- Reviewer Notes -> Re-route to Stage 2 at llm-urgent"]
        Approve["CompleteIssueAndCascadeAsync:\n- Squash & Merge -> Unlock DAG Dependents"]
    end

    subgraph Step4 ["4. Startup Crash Recovery"]
        Recovery["IStartupRecoveryService\n- Purge Stale .git/*.lock\n- Rescue Uncommitted Edits (WIP-RECOVERY)\n- Rehydrate Session Context\n- Head-of-Line llm-urgent Enqueue"]
    end

    Step1 --> Step2
    Orchestrator --> Lifecycle
    Lifecycle --> Wait
    Lifecycle --> Complete
    Lifecycle --> Fail
    Wait --> Answer
    Complete --> Approve
    Complete --> RequestChanges
    Step1 --> Recovery
```

### Step 3.1: Hangfire Queue Architecture (`NightCodeForge.Infrastructure/BackgroundJobs`)
* **Package:** `Hangfire.AspNetCore` and `Hangfire.Storage.SQLite`.
* **Two Dedicated Background Processing Servers:**
  ```csharp
  // 1. LLM Inference Server (Strict VRAM Protection)
  app.UseHangfireServer(new BackgroundJobServerOptions
  {
      ServerName = "NCF-LLM-Inference-Worker",
      WorkerCount = 1,
      Queues = new[] { "llm-urgent", "llm-inference" }
  });

  // 2. Utility & Git Operations Server
  app.UseHangfireServer(new BackgroundJobServerOptions
  {
      ServerName = "NCF-Utility-Worker",
      WorkerCount = 4,
      Queues = new[] { "default" }
  });
  ```
* **Job Retry Policy:**
  - LLM inference jobs are decorated with `[AutomaticRetry(Attempts = 0)]`.
  - Unhandled exceptions transition the issue cleanly to `Failed` to prevent duplicate code edits or runaway loops.

### Step 3.2: Central Issue Orchestrator (`NightCodeForge.Core/Workflow`)
* **Contract:** `IIssueOrchestrator`
  ```csharp
  public interface IIssueOrchestrator
  {
      Task ProcessIssueAsync(int issueId, CancellationToken cancellationToken);
      Task SubmitHumanAnswerAsync(int questionId, string answer, CancellationToken cancellationToken);
      Task RequestChangesAsync(int issueId, string revisionNotes, CancellationToken cancellationToken);
      Task CompleteIssueAndCascadeAsync(int issueId, string finalCommitMessage, CancellationToken cancellationToken);
  }
  ```
* **Step-by-Step Execution Lifecycle:**
  1. **Readiness & Workspace Locking:**
     - Checks DAG readiness: verifies all dependencies are `Completed`.
     - Acquires workspace lock via `IWorkspaceLockProvider.AcquireLockAsync(issue.WorkspaceId)`.
  2. **Branch & Environment Setup:**
     - Invokes `IGitWorkspaceManager.CheckoutIssueBranchAsync`.
  3. **Stage Execution & Metric Capture:**
     - Loads current stage from `IWorkflowPipeline` and resolves the stage's `AgentProfile`.
     - Records start timestamp; executes `stage.ExecuteAsync(pipelineContext)`.
     - Records end timestamp, calculates `DurationMs`, and extracts token counts (`PromptTokens`, `CompletionTokens`, `TotalTokens`).
     - Saves `StageExecutionRecord` to `AppDbContext`.
  4. **Result Evaluation:**
     - **`WaitingForHuman`:**
       - Intercepted from `HumanInterruptionException` (via `ask`).
       - Creates Git WIP commit via `IGitWorkspaceManager.CreateWipCheckpointAsync`.
       - Persists `Question`, serialized memory in `StageCheckpoint`, and updates issue status to `BlockedQuestion`.
       - **Hangfire Clean Return:** Returns `StageExecutionResult.WaitingForHuman()`; Hangfire marks the job as `Succeeded`. **The single GPU worker is immediately freed for the next queued task.**
     - **`StageCompleted`:**
       - Saves stage checkpoint.
       - Advances issue to the next stage in the pipeline.
       - If the completed stage is the final pipeline stage: transitions issue to `Review` and generates review diff.
     - **`Failed`:**
       - Logs error in `ExecutionLog` and transitions issue to `Failed`.

### Step 3.3: Preemptive Resumption & Cascade Unblocking
1. **`SubmitHumanAnswerAsync`:**
   - Persists human answer in `Question.Answer` and marks `Status = Answered`.
   - Transitions issue status to `Pending`.
   - Enqueues job to **`llm-urgent`** queue. When the GPU worker finishes its active turn, it immediately resumes this issue before touching standard backlog items.
2. **`RequestChangesAsync`:**
   - Reviewer enters revision notes in the Review Room modal.
   - Transitions issue from `Review` back to Stage 2 (`ImplementationStage`).
   - Injects revision notes into `PipelineContext.HandoverArtifacts.ReviewerFeedback`.
   - Enqueues to **`llm-urgent`**.
3. **`CompleteIssueAndCascadeAsync`:**
   - Executes `IGitWorkspaceManager.SquashAndMergeAsync` to merge the feature branch into `baseBranch`.
   - Sets issue status to `Completed`.
   - Calls `IDependencyResolutionEngine.ResolveCascadeUnblockedIssuesAsync(issueId)`.
   - Newly unblocked issues transition to `Pending` and are enqueued to `llm-inference`.

### Step 3.4: Startup Recovery & Crash Resilience (`IStartupRecoveryService`)
* **Contract:** `IStartupRecoveryService`
  ```csharp
  public interface IStartupRecoveryService
  {
      Task ReconcileWorkspacesAsync(CancellationToken cancellationToken = default);
  }
  ```
* **Execution Procedure (Invoked in `Program.cs` before Hangfire starts):**
  1. **Stale Lock Cleanup:** Scans target repositories for dangling `.git/index.lock` or `.git/refs/heads/*.lock` files left by abrupt power loss or Windows Update reboot cycles, safely purging them.
  2. **Uncommitted Working Tree Rescue:** Inspects working tree with `git status --porcelain`. If dirty files exist, creates an automated rescue commit:
     `git add -A && git commit -m "WIP-RECOVERY: [Crash Recovery] Preserving uncommitted workspace changes" --no-verify`
  3. **State Reconciliation:** Queries database for issues left in `Running` status:
     - For **Agentic Stages:** Rehydrates conversation memory from `STAGE_CHECKPOINT.SerializedMemory`, appends a `[System Notice: Host restarted unexpectedly; workspace restored]`, and enqueues to `llm-urgent`.
     - For **Deterministic Stages:** Resets metrics and re-enqueues to `llm-urgent` to execute freshly.
  4. **Broadcast & Audit:** Logs recovery event in `ExecutionLog` and broadcasts status via SignalR.

---

## 3. Verification & Test Plan

| Test Class | Focus Area | Scenarios Covered |
| :--- | :--- | :--- |
| **`HangfireQueuePriorityTests`** | Concurrency | Priority ordering (`llm-urgent` dequeued before `llm-inference`), `WorkerCount = 1` serial execution across multiple issues. |
| **`IssueOrchestratorTests`** | Lifecycle | Clean `WaitingForHuman` completion releasing worker, stage advancement, final diff generation on transition to `Review`. |
| **`ResumptionTests`** | Human-in-the-Loop | Answer submission, transition from `BlockedQuestion` to `Pending`, prompt context rehydration with human reply. |
| **`CascadeUnblockingTests`** | DAG Integration | Merge squashing, dependency state cascade, unlocking dependent tasks into `llm-inference`. |
| **`StartupRecoveryTests`** | Crash Resilience | Stale `.git/index.lock` purging, dirty working tree rescue commit, re-enqueueing orphaned running tasks to `llm-urgent`. |

---

## 4. Definition of Done (DoD) Checklist

- [x] Hangfire configured with SQLite storage and two isolated background processing servers.
- [x] `WorkerCount = 1` verified: zero concurrent LLM inference jobs across all workspaces.
- [x] Human interruption (`ask`) cleanly completes Hangfire job as `Succeeded`, releasing GPU.
- [x] Answering a question enqueues issue to `llm-urgent` with top priority.
- [x] Requesting changes in review room routes issue back to Stage 2 at `llm-urgent`.
- [x] Approving an issue merges branch and cascades unblocking to dependent tasks.
- [x] `IStartupRecoveryService` purges stale locks and rescues uncommitted files upon boot.
- [x] All queue and orchestrator unit/integration tests pass 100%.
