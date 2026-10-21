# 🎯 Step 11.17: Portal Task Scoping, Origin Provenance & Pipeline State Hygiene

> **Phase 11 Implementation Step (Post-MVP - Top Priority UX & Acceptance Polish)**  
> *Parent Plan:* [Phase 11 Master Plan](../11_EXTEND_FEATURES.md)  
> *Project Target:* `src/NightCodeForge.Core/Domain`, `src/NightCodeForge.Core/Repositories`, `src/NightCodeForge.Agents/Triage`, `src/NightCodeForge.Web/Components/Pages`, `src/NightCodeForge.Web/Components/Monitoring`, `src/NightCodeForge.Web/Services`  
> *Status:* Planned | *Target Framework:* .NET 10 (C# 13)

---

## 1. Executive Objective & Manual Acceptance Findings

During the initial manual acceptance pass across the NightCodeForge web dashboard, four critical UX and data-scoping discrepancies were identified across portals:

1. **Untrusted Intake & Sandboxed Triage Portal (`/triage`):**
   The "Proposed Backlog Tasks (10)" section currently queries and displays **all** tasks in `Suggested` status across the entire database, regardless of whether they originated from external bug reports, conversational ideation, manual creation, or batch imports. The portal must **only show proposed backlog items that originated from Untrusted Intake**.

2. **Task Execution Inspector (`/inspector`):**
   The task selection dropdown lists **all** workspace tasks, including idle `Suggested` and unstarted `Open` tasks that have never executed a single workflow stage. The inspector dropdown must **only list tasks that have at least one stage run** (i.e. in-flight, blocked/waiting for human, or closed/completed).

3. **Pipeline Stepper False "Running" State:**
   When inspecting an unstarted or queued task in the Task Inspector or Monitoring page, the first stage ("Task Review") falsely appears as actively **Running** (with an active spinner animation) even though no worker is running and no execution has occurred. This false state violates our deterministic state policy and must be eliminated.

4. **Review Room (`/review`):**
   The Review Room currently loads and allows code review of **all** tasks in the workspace, including unstarted `Suggested` and `Open` tasks that have no changes or execution runs. The Review Room must **only show tasks that have at least one stage run** (e.g. in flight, blocked, or ready for merge review).

This step resolves all four issues with clean architectural provenance tracking, repository query scoping, and strict execution state evaluation.

---

## 2. Architecture & Data Flow

```mermaid
flowchart TD
    subgraph IntakeSources ["Task Creation Channels"]
        TriageIntake["Untrusted Intake Portal<br/>(TriageService)"] -->|Sets Origin = UntrustedIntake| DB[("ForgeTask Entity")]
        IdeationChat["Ideation Service<br/>(propose_tasks)"] -->|Sets Origin = Ideation| DB
        ManualForm["CreateTask (/tasks/new)"] -->|Sets Origin = Manual| DB
        ImportCli["ncf import / Markdown Sync"] -->|Sets Origin = Import| DB
    end

    subgraph PortalScoping ["Scoped Portal Queries"]
        DB -->|Query: Status == Suggested & Origin == UntrustedIntake| TriageView["/triage Portal<br/>(Proposed Untrusted Tasks ONLY)"]
        DB -->|Query: HasExecutionRun == true| InspectorView["/inspector Dropdown<br/>(Executed, In-Flight & Rolled-Back Tasks ONLY)"]
        DB -->|Query: HasExecutionRun == true & Status in (Ready, Implemented, Reviewed, Closed)| ReviewView["/review Room<br/>(Reviewable Executed Tasks ONLY)"]
    end

    subgraph StepperHygiene ["Pipeline Stepper Execution State"]
        WorkerState["WorkerState / HubClient<br/>(Authoritative Active Worker)"] --> Stepper["PipelineStepper.razor"]
        DBTask["ForgeTask.Status / Checkpoints"] --> Stepper
        Stepper -->|Worker Active on Task| Pulse["Pulse Active Running Stage"]
        Stepper -->|Worker Idle & No Stage Status| Pending["Display Stages as Pending (No False Spinners)"]
        Stepper -->|IsWaitingForHuman == true| Blocked["Display WaitingForHuman (✋)"]
    end
```

---

## 3. Detailed Specifications

### 3.1 Task Origin Tracking (`TaskOrigin` Enum & Domain Entity)

To distinguish between tasks originating from untrusted bug reports, conversational ideation, manual developer input, and external tools without heuristic string matching:

1. **Define `TaskOrigin` in `NightCodeForge.Core.Domain`:**
   ```csharp
   namespace NightCodeForge.Core.Domain;

   /// <summary>
   /// Defines the ingestion source or authoring channel of a task.
   /// </summary>
   public enum TaskOrigin
   {
       Manual = 0,
       Ideation = 1,
       UntrustedIntake = 2,
       Import = 3,
       GitHubIssue = 4
   }
   ```

2. **Update [ForgeTask.cs](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Domain/ForgeTask.cs):**
   - Add property:
     ```csharp
     public TaskOrigin Origin { get; set; } = TaskOrigin.Manual;
     ```
   - Configure EF Core column mapping and index `(WorkspaceId, Origin, Status)` in `AppDbContext`.

3. **Update Markdown Sync & YAML Frontmatter:**
   - In `TaskMarkdownSyncService`, serialize `origin: untrusted_intake` (or `ideation`, `manual`, `import`) to YAML frontmatter.
   - On markdown intake, parse `origin` into `ForgeTask.Origin` (defaulting to `Manual` if absent).

4. **Update [TriageService.cs](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Agents/Triage/TriageService.cs):**
   - When converting `TaskProposal` to `ForgeTask`, explicitly stamp:
     ```csharp
     newTask.Origin = TaskOrigin.UntrustedIntake;
     ```
   - Provide `createdBy: AuditActors.TriageAgent` and `reason: "Created task proposal from untrusted intake triage"`.

5. **Update [Triage.razor](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Pages/Triage.razor):**
   - In `LoadExistingSuggestedTasksAsync()`:
     ```csharp
     var tasks = await TaskRepository.GetByWorkspaceIdAsync(workspace.Id);
     var untrustedSuggestions = tasks
         .Where(t => t.Status == TaskOverallStatus.Suggested && t.Origin == TaskOrigin.UntrustedIntake)
         .OrderByDescending(t => t.CreatedAt)
         .Take(10)
         .ToList();
     ```
   - Only tasks created via the Untrusted Intake triage workflow appear under "Proposed Backlog Tasks".

---

### 3.2 Executed Task Filtering for Inspector & Review Room (`HasExecutionRun` Flag)

#### 3.2.1 Problem Analysis: Status Ambiguity & Rollback Scenarios
In [Step 11.14 (Intelligent Task Dependency Detection)](phase-11/STEP_11_14_INTELLIGENT_TASK_DEPENDENCY_DETECTION.md), when the Stage 1 Task Review agent discovers unmerged prerequisite dependencies, the orchestrator suspends the task and **resets its status back to `TaskOverallStatus.Open`** until the prerequisites complete.

If task inspection were filtered merely by status (e.g. `Status != Open`), a rolled-back task would disappear from the inspector, preventing the developer from inspecting the Stage 1 conversation transcript, architectural reasoning, and dependency findings that caused the suspension.

Conversely, inspecting all `Open` tasks would pollute the dropdown with unstarted tasks that have never run a single stage.

#### 3.2.2 Explicit Domain Flags on `ForgeTask`
Add explicit workflow execution flags to [ForgeTask.cs](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Domain/ForgeTask.cs):
```csharp
namespace NightCodeForge.Core.Domain;

public class ForgeTask
{
    // ... existing properties ...

    /// <summary>
    /// Indicates whether an agentic or deterministic workflow execution has ever been initiated on this task.
    /// Remains true even if task status is subsequently rolled back to Open (e.g. due to unmerged DAG prerequisites).
    /// </summary>
    public bool HasExecutionRun { get; set; } = false;

    /// <summary>
    /// Highest workflow stage sequence order (1-7) reached during execution runs.
    /// </summary>
    public int MaxExecutedStageOrder { get; set; } = 0;
}
```

- Configure EF Core index `(WorkspaceId, HasExecutionRun)` in `AppDbContext`.
- In `TaskOrchestrator`: As soon as a task is picked up for stage execution, set `task.HasExecutionRun = true` and update `task.MaxExecutedStageOrder = Math.Max(task.MaxExecutedStageOrder, stageOrder)`.
- When Step 11.14 dynamically resets `task.Status = TaskOverallStatus.Open`, `task.HasExecutionRun` **remains `true`**, guaranteeing that the task and its Stage 1 review artifacts remain fully inspectable in the Task Execution Inspector.

#### 3.2.3 Repository & Query Scoping
Add explicit query to `ITaskRepository` and `EfCoreTaskRepository`:
```csharp
Task<IReadOnlyList<ForgeTask>> GetExecutedTasksByWorkspaceIdAsync(
    int workspaceId, 
    CancellationToken cancellationToken = default);
```
Implementation:
```csharp
public async Task<IReadOnlyList<ForgeTask>> GetExecutedTasksByWorkspaceIdAsync(int workspaceId, CancellationToken cancellationToken = default)
{
    return await _context.Tasks
        .Where(t => t.WorkspaceId == workspaceId && t.HasExecutionRun)
        .OrderByDescending(t => t.UpdatedAt)
        .ToListAsync(cancellationToken);
}
```

#### 3.2.4 Scoping in [TaskInspector.razor](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Pages/TaskInspector.razor)
- In `LoadAvailableTasksAsync()`: Call `TaskRepository.GetExecutedTasksByWorkspaceIdAsync(wsId)`.
- Unstarted `Suggested` and unstarted `Open` tasks (`HasExecutionRun == false`) are excluded.
- Tasks that have started running, tasks blocked on human input, completed/closed tasks, and suspended tasks rolled back to `Open` by Step 11.14 remain inspectable.
- If no executed tasks exist in the workspace, display a clean empty state card:
  > *"No executed tasks found. Once a task begins execution in the DevQueue or reaches a stage checkpoint, it will be inspectable here."*

#### 3.2.5 Scoping in [Review.razor](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Pages/Review.razor) & [TaskReviewService.cs](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Services/TaskReviewService.cs)
- In `TaskReviewService.GetPendingReviewsAsync(int? workspaceId)`:
  - Query tasks that have actually executed (`HasExecutionRun == true`) AND are in review-relevant statuses:
    ```csharp
    var candidates = allWorkspaceTasks
        .Where(t => t.HasExecutionRun && t.Status is TaskOverallStatus.Ready 
                                                 or TaskOverallStatus.Implemented 
                                                 or TaskOverallStatus.Reviewed 
                                                 or TaskOverallStatus.Closed)
        .OrderByDescending(t => t.UpdatedAt)
        .ToList();
    ```
  - Unstarted `Open` and `Suggested` tasks are strictly excluded from the review switcher and Review Room.

---

### 3.3 Pipeline Stepper State Hygiene (Eliminate False "Running" State)

#### 3.3.1 Problem Analysis
In [PipelineStepper.razor](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Monitoring/PipelineStepper.razor):
```csharp
if (!string.IsNullOrEmpty(CurrentStageId) && OverallStatus != TaskOverallStatus.Closed && OverallStatus != TaskOverallStatus.Ready && OverallStatus != TaskOverallStatus.Failed)
{
    ApplyStageTransition(CurrentStageId, CurrentStageStatus ?? StageExecutionStatus.Running);
}
```
Because new tasks default `CurrentStageId` to `"task_review"` upon creation, if `CurrentStageStatus` is null or omitted, the fallback `StageExecutionStatus.Running` forces the first stage step icon to render the animated `spinner-pulse` and `Running` status even when the worker is idle.

Additionally, in [Monitoring.razor](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Pages/Monitoring.razor):
The page auto-discovery logic synthetically marked any `Open` task with a non-empty `CurrentStageId` as `WorkerState.HandleWorkerStatus("Running", ...)` when opening the page, fabricating execution state for idle database records.

#### 3.3.2 Strict State Rules for [PipelineStepper.razor](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Monitoring/PipelineStepper.razor)
1. **Never Fall Back to `Running` Without Active Confirmation:**
   - If `CurrentStageStatus` is `null`, default to `StageExecutionStatus.Pending`.
   - A stage must ONLY display `Running` if:
     - `CurrentStageStatus == StageExecutionStatus.Running`, AND
     - The task is actively running on the worker (or SignalR broadcast confirmed the active stage).
2. **Waiting For Human Representation:**
   - If the task has `IsWaitingForHuman == true` or `CurrentStageStatus == StageExecutionStatus.WaitingForHuman`, render the stage as `WaitingForHuman` with `✋` overlay, never `Running`.
3. **Completed Task Pipelines:**
   - For `TaskOverallStatus.Closed`, all executed stages render `Completed` with `✓` overlay.
4. **Remove Synthetic Auto-Discovery in `Monitoring.razor`:**
   - In accordance with our **No Synthetic Values Policy**, remove the fallback that marks idle `Open` tasks as `Running`. The Hangfire worker status broadcast via SignalR is the single source of truth for active execution.

---

## 4. Definition of Done Checklist

- [ ] **Task Origin Domain & Ingestion:**
  - [ ] Add `TaskOrigin` enum (`Manual`, `Ideation`, `UntrustedIntake`, `Import`, `GitHubIssue`) and property `ForgeTask.Origin`.
  - [ ] Configure EF Core column and migration in `AppDbContext`.
  - [ ] Update `TriageService` to stamp `TaskOrigin.UntrustedIntake` on all triage task proposals.
  - [ ] Update `TaskMarkdownSyncService` to serialize and parse `origin` in YAML frontmatter.
- [ ] **Execution Tracking Flag & Scoping:**
  - [ ] Add `HasExecutionRun` (bool) and `MaxExecutedStageOrder` (int) to `ForgeTask` entity and EF Core schema index `(WorkspaceId, HasExecutionRun)`.
  - [ ] Update `TaskOrchestrator` to stamp `task.HasExecutionRun = true` upon stage execution initiation.
  - [ ] Ensure `task.HasExecutionRun` remains `true` when Step 11.14 rolls back task status to `TaskOverallStatus.Open` upon detecting unmerged dependencies.
  - [ ] Implement `GetExecutedTasksByWorkspaceIdAsync` on `ITaskRepository` querying `t.HasExecutionRun == true`.
  - [ ] Update `TaskInspector.razor` dropdown to only display tasks with `HasExecutionRun == true` (including suspended/rolled-back tasks).
  - [ ] Update `TaskReviewService.GetPendingReviewsAsync` to only return tasks with `HasExecutionRun == true` in reviewable statuses (`Ready`, `Implemented`, `Reviewed`, `Closed`).
  - [ ] Update `Triage.razor` to filter "Proposed Backlog Tasks" strictly by `Status == Suggested && Origin == UntrustedIntake`.
- [ ] **Execution State Hygiene:**
  - [ ] Fix `PipelineStepper.razor` to default null `CurrentStageStatus` to `Pending` instead of `Running`.
  - [ ] Purge synthetic "Running" auto-discovery logic from `Monitoring.razor`.
  - [ ] Verify idle tasks render all stages as Pending without active spinners.
- [ ] **Automated Tests:**
  - [ ] Unit test: `TriageService` assigns `TaskOrigin.UntrustedIntake` and appropriate audit actor.
  - [ ] Unit test: `ITaskRepository.GetExecutedTasksByWorkspaceIdAsync` filters by `HasExecutionRun`, excluding unstarted open/suggested tasks.
  - [ ] Unit test: Verify task with `HasExecutionRun == true` and `Status == Open` (rolled back by Step 11.14) is returned by `GetExecutedTasksByWorkspaceIdAsync`.
  - [ ] Component test: `Triage.razor` displays only untrusted intake tasks when database contains mixed suggestions.
  - [ ] Component test: `TaskInspector.razor` dropdown lists executed and rolled-back tasks, excluding unstarted tasks.
  - [ ] Component test: `PipelineStepper.razor` renders `Pending` when task has not commenced execution.
