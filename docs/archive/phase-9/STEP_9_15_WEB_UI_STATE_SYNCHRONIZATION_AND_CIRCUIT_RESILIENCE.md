# 🖥️ Step 9.15: Web UI State Synchronization & Circuit Resilience

> **Phase 9 Implementation Step**  
> *Parent Plan:* [Phase 9 Master Plan](../09_CODEBASE_REMEDIATION_AND_ROBUSTNESS.md)  
> *Source Policy:* [AGENTS.md](../../../AGENTS.md)  
> *Status:* Completed | *Target Framework:* .NET 10 (C# 13)

---

## 1. Problem Statement

During the foundation audit of `src/NightCodeForge.Web/` and related orchestrator telemetry bridges:

1. **Task Execution Inspector Desynchronization on Stage Start/Complete:**
   When navigating to `/inspector` or `/tasks/{id}`, the Task Execution Inspector fails to update in real-time as workflow steps start and complete. This stems from a compounding chain of issues:
   - [`TaskExecutionInspector.razor`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Monitoring/TaskExecutionInspector.razor) injects [`OrchestratorHubClientConnection`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Services/OrchestratorHubClientConnection.cs) and wires event handlers, but **never calls `await HubClient.StartAsync()`**. The SignalR client remains disconnected if the user did not navigate through `/monitoring`.
   - `TaskExecutionInspector.razor` **never calls `await HubClient.JoinTaskGroupAsync(TaskId)`**. Fine-grained token streams, tool events, logs, and stage transitions targeted at `task-{TaskId}` are never delivered.
   - When switching inspected tasks via the dropdown or parameter updates, `TaskExecutionInspector` does not leave the previous task group or join the new one.
   - SignalR auto-reconnect does not re-subscribe to active task groups on the server.
   - [`EfCoreTaskRepository.GetByIdAsync`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Infrastructure/Data/Repositories/EfCoreTaskRepository.cs#L19-L33) omits `.AsNoTracking()`. Because Blazor Server circuits share a scoped `AppDbContext`, queries return the tracked entity cached in memory (`_context.Tasks.Local`). Subsequent database reads during stage transitions return stale status, stage IDs, and navigation collections.
   - [`TaskOrchestrator.cs`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Workflow/TaskOrchestrator.cs) never broadcasts `StageCompleted` for deterministic stages (build, test, lint, ratchet, sast, coverage, verification) or standard stage handoffs, leaving the UI unaware of stage completion.
   - The container page [`TaskInspector.razor`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Pages/TaskInspector.razor) does not observe stage transitions, leaving the task selector dropdown and active task indicator stale.

2. **Workspace Reactivity Gaps:**
   Multiple primary UI pages ([`Settings.razor`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Pages/Settings.razor) and [`Review.razor`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Pages/Review.razor)) do not observe [`WorkspaceStateService.OnWorkspaceChanged`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Services/WorkspaceStateService.cs). When a user changes the active workspace in the top navigation bar, the Settings page and the Review Room remain locked to the previous workspace until manually reloaded.

3. **Unsupervised Background Loops in UI:**
   [`DevQueue.razor`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Pages/DevQueue.razor#L96-L115) runs an unobserved `_ = Task.Run(...)` periodic loop. If an unhandled exception occurs (e.g. transient DB lock), the auto-refresh loop dies silently with no diagnostic log or restart.

4. **Disposal Leaks:**
   Components that fail to implement `IDisposable` or properly unsubscribe from singleton/scoped services retain circuit references in Blazor Server memory.

---

## 2. Findings Inventory

| # | Location | Issue | Severity |
| :-- | :--- | :--- | :--- |
| U1 | [`Settings.razor`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Pages/Settings.razor#L164-L172) | Injects `WorkspaceStateService` but does not subscribe to `OnWorkspaceChanged`. Switching active workspace in navbar leaves workspace tab showing previous workspace. Missing `IDisposable`. | Medium |
| U2 | [`Review.razor`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Pages/Review.razor#L187-L195) | Subscribes to `CommentState` but fails to subscribe to `WorkspaceState.OnWorkspaceChanged`. Changing active workspace leaves Review Room viewing previous workspace's tasks. | Medium |
| U3 | [`DevQueue.razor`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Pages/DevQueue.razor#L96-L115) | Background `PeriodicTimer` loop inside `Task.Run` has unobserved non-cancellation exceptions. If `RefreshQueueStateAsync` throws, the loop halts permanently with no log. | Medium |
| U4 | [`ToastService.cs`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Services/ToastService.cs#L43-L48) | Unobserved fire-and-forget `Task.Run` timer with no cancellation handling or bounded lifecycle. | Low |
| U5 | [`TriageService.cs`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Agents/Triage/TriageService.cs#L52) | Hardcoded `"main"` branch fallback instead of canonical `WorkflowDefaults.DefaultBaseBranch`. | Low |
| U6 | [`TaskExecutionInspector.razor`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Monitoring/TaskExecutionInspector.razor#L349-L370) | SignalR connection lifecycle omitted: never calls `HubClient.StartAsync()`, never calls `JoinTaskGroupAsync(TaskId)`, never cleans up groups on disposal or parameter switch. Reconnect drops task group subscriptions. | High |
| U7 | [`EfCoreTaskRepository.cs`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Infrastructure/Data/Repositories/EfCoreTaskRepository.cs#L19-L33) | `GetByIdAsync` lacks `.AsNoTracking()`. In long-lived Blazor circuit scopes, subsequent calls to `GetByIdAsync` during stage transitions return stale in-memory cached entity state from EF Core ChangeTracker instead of live SQLite database records. | High |
| U8 | [`TaskOrchestrator.cs`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Workflow/TaskOrchestrator.cs#L440-L500) | Missing `StageCompleted` broadcasts: deterministic stages (build, test, lint, ratchet, sast, coverage, verification) and intermediate workflow stage completion in `TaskOrchestrator` do not broadcast `StageCompleted` via `_streamer.BroadcastAsync(...)`, so `ReceiveStageTransition(..., "Completed")` is never sent. | High |
| U9 | [`TaskInspector.razor`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Pages/TaskInspector.razor#L86-L106) | Container page does not subscribe to `HubClient.OnStageTransitionReceived` or `WorkerState.OnWorkerStateChanged`, leaving dropdown task status `#id - Title (Status)` and active running task button frozen during pipeline progression. | Medium |

---

## 3. Technical Specifications & Architecture

### 3.1 Task Execution Inspector SignalR Lifecycle & Group Management
1. In [`OrchestratorHubClientConnection.cs`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Services/OrchestratorHubClientConnection.cs):
   - Maintain a synchronized `HashSet<int> _activeTaskGroups = new();`.
   - In `JoinTaskGroupAsync(int taskId)`:
     - Record `_activeTaskGroups.Add(taskId);`.
     - If `State == HubConnectionState.Connected`, invoke `JoinTaskGroup` on the hub.
   - In `LeaveTaskGroupAsync(int taskId)`:
     - Remove `_activeTaskGroups.Remove(taskId);`.
     - If `State == HubConnectionState.Connected`, invoke `LeaveTaskGroup` on the hub.
   - In `StartAsync()`:
     - After `_hubConnection.StartAsync()`, iterate through all registered `_activeTaskGroups` and rejoin them on the server.
   - In `_hubConnection.Reconnected` event handler:
     - Rejoin all registered `_activeTaskGroups` automatically on the newly established connection.

2. In [`TaskExecutionInspector.razor`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Monitoring/TaskExecutionInspector.razor):
   - Track `private int? _joinedTaskId;`.
   - In `OnInitializedAsync()`:
     - Register typed SignalR event delegates.
     - Call `await HubClient.StartAsync();`.
     - If `TaskId > 0`: `_joinedTaskId = TaskId; await HubClient.JoinTaskGroupAsync(TaskId);`.
   - In `OnParametersSetAsync()`:
     - If `_joinedTaskId != TaskId`:
       - If `_joinedTaskId.HasValue`, `await HubClient.LeaveTaskGroupAsync(_joinedTaskId.Value);`.
       - `_joinedTaskId = TaskId;`.
       - If `TaskId > 0`, `await HubClient.JoinTaskGroupAsync(TaskId);`.
       - Reload task data.
   - In `DisposeAsync()`:
     - If `_joinedTaskId.HasValue`, `await HubClient.LeaveTaskGroupAsync(_joinedTaskId.Value);`.
     - Unsubscribe typed SignalR event delegates.

### 3.2 Repository AsNoTracking Query Cleanliness for Circuit Scopes
1. In [`EfCoreTaskRepository.cs`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Infrastructure/Data/Repositories/EfCoreTaskRepository.cs#L19-L33):
   - Update `GetByIdAsync`:
     ```csharp
     return await _context.Tasks
         .AsNoTracking()
         .AsSplitQuery()
         .Include(i => i.Workspace)
         .Include(i => i.Workflow)
             .ThenInclude(w => w!.Stages)
         .Include(i => i.Dependencies)
         .Include(i => i.Questions)
         .Include(i => i.Checkpoints)
         .Include(i => i.ExecutionRecords)
         .Include(i => i.ExecutionLogs)
         .Include(i => i.Conversations)
         .FirstOrDefaultAsync(i => i.Id == id, cancellationToken);
     ```
   - This ensures calls in long-lived Blazor circuits never retrieve stale tracked snapshots from the EF ChangeTracker, while `UpdateAsync` continues to function properly via its existing detached entity attachment logic.

### 3.3 TaskOrchestrator Stage Completion Telemetry Broadcasts
1. In [`TaskOrchestrator.cs`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Workflow/TaskOrchestrator.cs):
   - When a deterministic stage completes (around line 495):
     - Broadcast `Streaming.AgentActivityKind.StageCompleted`:
       ```csharp
       await _streamer.BroadcastAsync(new Streaming.AgentActivityEvent(
           Guid.NewGuid().ToString("N"),
           task.Id,
           stage.StageId,
           Streaming.AgentActivityKind.StageCompleted,
           summary,
           DateTime.UtcNow,
           new Dictionary<string, object?> { ["StageKind"] = "Deterministic" }), cancellationToken);
       ```
   - When any workflow stage completes successfully and the pipeline transitions to the next stage (around line 655):
     - Broadcast `Streaming.AgentActivityKind.StageCompleted` if not already emitted by the stage harness, ensuring the UI receives the explicit `Completed` transition before the subsequent `StageStarted` event arrives.

### 3.4 TaskInspector.razor Live State Reactivity
1. In [`TaskInspector.razor`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Pages/TaskInspector.razor):
   - Inject `OrchestratorHubClientConnection HubClient`.
   - In `OnInitializedAsync()`:
     - Subscribe `HubClient.OnStageTransitionReceived += HandleStageTransition;`.
     - Ensure `HubClient.StartAsync()` is called.
   - In `HandleStageTransition(int taskId, string stageId, string stageKind, string status)`:
     - Find the corresponding task in `_availableTasks` and update its status/current stage.
     - Call `InvokeAsync(StateHasChanged)`.
   - In `Dispose()`:
     - Unsubscribe `HubClient.OnStageTransitionReceived -= HandleStageTransition;`.

### 3.5 Synchronized Workspace Reactivity in Settings & Review
1. In [`Settings.razor`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Pages/Settings.razor):
   - Add `@implements IDisposable`.
   - In `OnInitialized()`: subscribe `WorkspaceState.OnWorkspaceChanged += HandleWorkspaceChanged;`.
   - In `HandleWorkspaceChanged()`: invoke `InvokeAsync(StateHasChanged);`.
   - In `Dispose()`: unsubscribe from `WorkspaceState.OnWorkspaceChanged`.
2. In [`Review.razor`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Pages/Review.razor):
   - In `OnInitialized()`: subscribe `WorkspaceState.OnWorkspaceChanged += HandleWorkspaceChanged;`.
   - In `HandleWorkspaceChanged()`: reload pending reviews via `InvokeAsync(async () => { await LoadReviewDataAsync(); StateHasChanged(); });`.
   - In `Dispose()`: unsubscribe cleanly from `WorkspaceState.OnWorkspaceChanged` and `CommentState.OnCommentsChanged`.

### 3.6 Resilient Background Loop in DevQueue
1. In [`DevQueue.razor`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Pages/DevQueue.razor):
   - Catch general exceptions inside the `while` loop, log a structured warning, and continue to subsequent ticks instead of allowing the background task to crash.
   - Implement clean disposal disposing the `PeriodicTimer` and canceling `_cts`.

### 3.7 Default Branch Constants
1. In [`TriageService.cs`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Agents/Triage/TriageService.cs):
   - Replace magic literal `"main"` with `WorkflowDefaults.DefaultBaseBranch`.

---

## 4. Definition of Done (DoD) Checklist

- [x] Navigating directly to `/inspector` or `/tasks/{id}` automatically starts `HubClient` and joins the task SignalR group.
- [x] Switching tasks in the inspector cleanly leaves the previous task group and joins the new task group.
- [x] SignalR auto-reconnection automatically rejoins all active task groups on the server.
- [x] `EfCoreTaskRepository.GetByIdAsync` uses `.AsNoTracking()`, guaranteeing fresh state is read from the SQLite database on each refresh.
- [x] `TaskOrchestrator` broadcasts `StageCompleted` for deterministic stages and workflow stage handoffs.
- [x] Pipeline stepper, turns, and terminal logs in `TaskExecutionInspector.razor` update immediately in real-time when a stage starts or completes without manual browser reload.
- [x] `TaskInspector.razor` dropdown status and active running task button update dynamically on stage transitions.
- [x] Switching active workspace in the top navigation bar immediately updates `Settings.razor` and `Review.razor`.
- [x] `Settings.razor` and `Review.razor` implement `IDisposable` with zero event handler leaks.
- [x] `DevQueue.razor` periodic refresh loop survives transient exceptions with structured logging.
- [x] No magic `"main"` literals remain in `TriageService`.
- [x] `dotnet test --filter "Category!=Acceptance&Category!=E2E"` passes.
