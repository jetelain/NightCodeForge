# 💡 Step 10.5: Interactive Ideation Database Tasks & Lifecycle Management

> **Phase 10 Implementation Step**  
> *Parent Plan:* [Phase 10 Master Plan](../10_EXTEND_FEATURES.md)  
> *Project Target:* `src/NightCodeForge.Agents/Ideation`, `src/NightCodeForge.Agents/Tools`, `src/NightCodeForge.Core/Domain`, `src/NightCodeForge.Web/Components/Ideation`, `tests/NightCodeForge.Agents.Tests` | *Status:* Planned

---

## 1. Executive Objective

Transform the Ideation chat experience by immediately persisting proposed backlog items to the database with a dedicated **`Draft`** state, enabling robust DAG dependency construction and iterative conversational refinement:

1. **Immediate Database Task Allocation:**
   - When the ideation agent proposes a task, it is immediately written to the database with [TaskOverallStatus.Draft](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Domain/Enums.cs).
   - The assigned database integer `Id` (e.g. `#104`) is immediately returned to the LLM agent in the tool response.
   - Subsequent tasks can reference concrete integer IDs in their dependency declarations (`depends_on: [104]`), eliminating brittle and ambiguous title-based matching.

2. **Scoped Backlog Manipulation Tools for Read-Only Agents:**
   - Even though the Ideation Agent operates in `AgentMode.ReadOnly` (preventing codebase modifications), it is granted scoped authority over its unapproved draft tasks via two new tools:
     - **`edit_task(task_id, title?, description?, priority?, depends_on_ids?)`**: Updates a draft task if the developer requests changes during subsequent conversational turns.
     - **`remove_task(task_id)`**: Retracts a draft task if the developer rejects the idea or if the agent consolidates tasks.
   - **Strict Safety Gate:** These tools strictly reject modifications to any task that has already been approved, promoted to `Suggested`/`Open`, or started execution.

3. **User Discard & Automated Draft Expiration:**
   - **UI Discard Action:** The user can click "Discard" on a proposal card in the Ideation UI to permanently remove the draft from the database.
   - **Automated TTL Pruning:** A background Hangfire recurring job (`IdeationDraftPruningService`) purges unapproved `Draft` tasks older than a configurable retention threshold (default: 24 hours / 1 day) to prevent database bloat.

4. **Strict Isolation from Backlog & Visual DAG:**
   - `Draft` tasks are conversational in-flight artifacts visible **exclusively within the Ideation Chat interface**.
   - They are **strictly filtered out and excluded** from the Kanban Backlog ([Backlog.razor](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Pages/Backlog.razor)), the Kanban board, the Visual DAG dependency visualizer ([DagVisualizer.razor](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Backlog/DagVisualizer.razor)), and active queue dispatchers.
   - A task only enters the Backlog and Visual DAG once approved and promoted by the maintainer into `Suggested` or `Open`.

---

## 2. Current State vs. Target State

| Dimension | Current State | Target State |
| :--- | :--- | :--- |
| **Persistence Timing** | Stored in transient memory list (`PendingProposals`) or data bag; only committed to DB after explicit user clicks or triage completion. | Immediately saved to SQLite as a `ForgeTask` in `Draft` status upon agent tool invocation. |
| **Dependency References** | Relies on fuzzy string matching via `DependsOnTitles` (breaks if titles change or duplicate). | Uses exact numeric task IDs (`DependsOnTaskIds: [102, 103]`) backed by foreign keys in `TaskDependency`. |
| **Conversational Iteration** | Agent cannot edit or retract a proposed task if user asks: *"Actually, split task 1 into two"* or *"Change the description of the API task"*. | Agent uses `edit_task` and `remove_task` tools to revise or remove draft items dynamically during chat. |
| **Unapproved Task Cleanup** | Incomplete ideation sessions linger in UI state without cleanup. | Stale drafts automatically expire and prune after 24h via Hangfire scheduled job; explicit "Discard" deletes immediately. |

---

## 3. High-Level Architecture & Lifecycle Flow

```mermaid
flowchart TD
    subgraph IdeationChat ["Ideation Conversation"]
        User["Developer Prompt<br/>('Add auth and rate limiting')"]
        Agent["Ideation Agent (ReadOnly)"]
    end

    subgraph Tools ["Ideation Task Tools"]
        Propose["propose_tasks(title, desc, deps)"]
        Edit["edit_task(id, title, desc, deps)"]
        Remove["remove_task(id)"]
    end

    subgraph Database ["SQLite Database"]
        TaskDB[("ForgeTask<br/>(Status = Draft)")]
        DepsDB[("TaskDependency<br/>(TaskId -> DependsOnTaskId)")]
    end

    subgraph UI ["Ideation UI (Blazor)"]
        Card["IdeationProposalCard<br/>(#104 [Draft])"]
        ApproveBtn["Approve / Promote<br/>(Draft -> Suggested / Open)"]
        DiscardBtn["Discard<br/>(Delete from DB)"]
    end

    subgraph Maintenance ["Background Pruning"]
        ReaperJob["Hangfire Job: IdeationDraftPruningService<br/>(Every 24h: Delete Drafts older than 1 day)"]
    end

    User --> Agent
    Agent --> Propose -->|Insert with Status=Draft| TaskDB
    Propose -->|Returns Task #104| Agent
    Agent -->|Next task depends_on: [104]| DepsDB
    Agent -->|User asks for changes| Edit -->|Updates Draft #104| TaskDB
    Agent -->|User rejects idea| Remove -->|Deletes Draft #104| TaskDB

    TaskDB --> Card
    Card --> ApproveBtn -->|Status = Suggested / Open| TaskDB
    Card --> DiscardBtn -->|Delete Task| TaskDB
    TaskDB -.->|Stale > 24h| ReaperJob
```

---

## 4. Technical Design Specifications

### 4.1 Domain Model Update ([Enums.cs](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Domain/Enums.cs))

```csharp
public enum TaskOverallStatus
{
    Draft = -1,            // In-flight proposal created during interactive ideation chat
    Suggested = 0,         // Vetted proposal awaiting maintainer approval into active backlog
    Rejected = 1,          // Manually rejected by maintainer
    Open = 2,              // Approved; ready for DAG dependency resolution and scheduling
    Reviewed = 3,          // Stage 1 completed
    Implemented = 4,       // Stage 3 completed
    Ready = 5,             // Stage 6 verified; awaiting review merge
    Closed = 6,            // Fully merged; terminal
    Cancelled = 7,         // Manually cancelled
    Failed = 8             // Fatal failure
}
```

---

### 4.2 Tool Signatures & Schemas

#### 1. `propose_tasks` (or `create_task`)
- **Action:** Inserts `ForgeTask` records with `Status = TaskOverallStatus.Draft` and creates associated `TaskDependency` entries.
- **Return to Agent:**
  ```json
  [
    { "id": 104, "title": "Implement JWT Token Handler", "status": "Draft" },
    { "id": 105, "title": "Configure Auth Middleware", "status": "Draft", "depends_on": [104] }
  ]
  ```
  *"Created Draft Task #104 and #105. You can reference ID 104 or 105 for downstream dependencies."*

#### 2. `edit_task`
```csharp
[Description("Edits an unapproved draft task created during this ideation session")]
public async Task<string> EditTaskAsync(
    int task_id,
    string? title = null,
    string? description = null,
    int? priority = null,
    IReadOnlyList<int>? depends_on_task_ids = null,
    CancellationToken ct = default);
```
- **Validation Guardrails:**
  - `task.WorkspaceId == context.Workspace.Id`
  - `task.Status == TaskOverallStatus.Draft` (fails fast if task is already `Suggested`, `Open`, or beyond).

#### 3. `remove_task`
```csharp
[Description("Removes an unapproved draft task created during this ideation session")]
public async Task<string> RemoveTaskAsync(
    int task_id,
    CancellationToken ct = default);
```
- **Validation Guardrails:**
  - Rejects if `task.Status != TaskOverallStatus.Draft`.
  - Removes associated dependencies cleanly.

---

### 4.3 Automated Pruning Service (`IdeationDraftPruningService`)

Registered in Hangfire as a recurring daily job:
```csharp
public class IdeationDraftPruningService
{
    private readonly AppDbContext _dbContext;
    private readonly ILogger<IdeationDraftPruningService> _logger;

    public async Task PruneStaleDraftsAsync(int maxAgeHours = 24, CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow.AddHours(-maxAgeHours);
        var staleDrafts = await _dbContext.Tasks
            .Where(t => t.Status == TaskOverallStatus.Draft && t.CreatedAt < cutoff)
            .ToListAsync(ct);

        if (staleDrafts.Count > 0)
        {
            _dbContext.Tasks.RemoveRange(staleDrafts);
            await _dbContext.SaveChangesAsync(ct);
            _logger.LogInformation("Pruned {Count} stale ideation draft tasks older than {Cutoff}", staleDrafts.Count, cutoff);
        }
    }
}
```

---

---

### 4.4 UI Integration ([IdeationProposalCard.razor](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Ideation/IdeationProposalCard.razor))

- Displays badge: `[Draft #104]`
- Direct actions on the card:
  - **✅ Approve Task:** Transitions `Status` from `Draft` $\rightarrow$ `Suggested` (or `Open`).
  - **🗑️ Discard:** Deletes task immediately from database with a confirmation toast.
  - Renders dependency badges linking to parent task IDs (`Depends on #102`).

---

### 4.5 Backlog & Visual DAG Query Isolation

To ensure draft proposals do not pollute the engineering backlog or dependency graph before developer review:

1. **Kanban Backlog Isolation ([Backlog.razor](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Pages/Backlog.razor), [KanbanBoard.razor](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Backlog/KanbanBoard.razor)):**
   - The query loading tasks for the Kanban columns enforces:
     ```csharp
     tasks.Where(t => t.Status != TaskOverallStatus.Draft)
     ```
   - Draft tasks never appear in the "Suggested" or any other backlog column.

2. **Visual DAG Graph Isolation ([DagVisualizer.razor](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Backlog/DagVisualizer.razor)):**
   - DAG node generation ignores `TaskOverallStatus.Draft` tasks and their edges.
   - Prevents unapproved ideas from displaying phantom or circular dependency nodes in the project topological map.

3. **Repository Standard Queries ([ITaskRepository](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Repositories/ITaskRepository.cs)):**
   - `GetActiveTasksAsync` and `GetByWorkspaceIdAsync` (unless explicitly requested with `includeDrafts: true`) filter out `TaskOverallStatus.Draft`.
   - `IdeationService` queries drafts explicitly via dedicated method: `GetDraftTasksByWorkspaceIdAsync(workspaceId)`.

---

## 5. Implementation Checklist & Deliverables

- [ ] **Domain & Enums:**
  - Add `TaskOverallStatus.Draft = -1` to [Enums.cs](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Domain/Enums.cs).
  - Update EF Core mappings and queries (ensure `Draft` tasks are excluded from active backlog, Kanban, and DAG queries).
- [ ] **Backlog & Visual DAG Isolation:**
  - Add explicit `Status != TaskOverallStatus.Draft` filtering to `Backlog.razor`, `KanbanBoard.razor`, and `DagVisualizer.razor`.
  - Add `GetDraftTasksByWorkspaceIdAsync` to `ITaskRepository` for exclusive use in Ideation views.
- [ ] **Immediate Database Task Tools:**
  - Refactor `propose_tasks` to insert `ForgeTask` entities in `Draft` status and return numeric IDs.
  - Implement `edit_task` tool with status validation (`Draft` only).
  - Implement `remove_task` tool with status validation (`Draft` only).
  - Register new tools in `CanonicalAIFunctionFactory` for ideation agent profile.
- [ ] **Automated Pruning Service:**
  - Implement `IdeationDraftPruningService` and register Hangfire recurring schedule.
  - Add workspace/appsettings configuration for draft retention duration.
- [ ] **UI Updates:**
  - Update `Ideation.razor` and `IdeationProposalCard.razor` to support real-time draft updates, Discard button, and Approve button.
- [ ] **Unit & Integration Tests:**
  - Test immediate DB task creation and ID return.
  - Test DAG dependencies formed with numeric IDs.
  - Test guardrails preventing `edit_task` or `remove_task` on non-Draft tasks.
  - Test that `Backlog` and `DagVisualizer` queries strictly omit `Draft` tasks.
  - Test `IdeationDraftPruningService` purging tasks older than 24h.

