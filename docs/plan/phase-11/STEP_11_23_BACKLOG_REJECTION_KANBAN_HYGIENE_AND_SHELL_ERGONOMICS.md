# 🎯 Step 11.23: Backlog Task Rejection Lifecycle, Kanban State Hygiene & Shell UI Polish

> **Phase 11 Implementation Step (Post-MVP - Backlog Ergonomics & Shell Polish)**  
> *Parent Plan:* [Phase 11 Master Plan](../11_EXTEND_FEATURES.md)  
> *Project Target:* `src/NightCodeForge.Core`, `src/NightCodeForge.Web`, `tests/NightCodeForge.Web.Tests`, `tests/NightCodeForge.Core.Tests`  
> *Status:* Planned | *Target Framework:* .NET 10 (C# 13)

---

## 1. Executive Summary & Problem Statement

User feedback from live instances identified five concrete friction points and visual/functional bugs across task lifecycle management, backlog visualization, and web shell ergonomics:

1. **Missing Task Rejection Action in Backlog:**
   - **Current Limitation:** The system only supports rejecting tasks while in `TaskOverallStatus.Suggested` via `MaintainerPromotionModal`. Once a task is promoted to `Open` (or directly authored in the backlog), there is no UI action to reject the task.
   - **Engine Restriction:** `TaskOrchestrator.PromoteSuggestedTaskAsync` explicitly validates `task.Status == TaskOverallStatus.Suggested`, throwing an `InvalidOperationException` if called for an `Open` task. No general-purpose rejection method exists in `ITaskOrchestrator` or `ITaskMutationService`.
   - **Requirement:** Add an explicit, first-class maintainer action to reject tasks directly from the Backlog (`TaskDetailDrawer.razor`), the Task Detail page (`TaskDetail.razor`), and the Kanban board card (`KanbanCard.razor`). The action transitions the task to `TaskOverallStatus.Rejected`, records attribution in `TaskHistories`, un-enqueues any pending jobs from the DevQueue/Hangfire, updates disk markdown frontmatter (`status: rejected`), and notifies the maintainer.

2. **Priority Filter Text Contrast (Black Text on Dark Surface):**
   - **Current Bug:** In `Backlog.razor`, the Priority filter chips (`All`, `P1 Critical`, `P1–P2 High+`, `P3 Normal`, `P4–P5 Low`) are rendered using `<button class="stat-chip priority-filter-btn">`. While `.stat-chip` in `app.css` defines the container background and borders, browser user-agent stylesheets apply default button text styling (`color: ButtonText;` / black), making the text nearly unreadable against the `#161922` dark card background.
   - **Requirement:** Ensure all priority filter buttons and stat chips explicitly apply high-contrast white/light text (`color: var(--text-primary);` / `#ffffff`) across idle, active, and hover states.

3. **Display Error: Promoted Task 4 Erroneously Displayed as "In-Flight":**
   - **Current Bug:** When Task 4 was promoted from `Suggested` to `Open` in the live instance, it immediately appeared in the "In-Flight" column on `KanbanBoard.razor` (and incremented the In-Flight count in `Backlog.razor`), even though one of its prerequisites was not yet satisfied and its status on the task detail page was correctly `Open`.
   - **Root Cause:**
     1. In `KanbanBoard.razor` and `Backlog.razor`, tasks were partitioned into "Open" vs. "In-Flight" based solely on `string.IsNullOrWhiteSpace(t.CurrentStageId)`.
     2. In `TaskMarkdownSyncService.cs` and `TriageService.cs`, newly imported or created tasks had `CurrentStageId` pre-populated with `StandardStageIds.TaskReview` (`"task-review"`).
     3. Consequently, upon promotion, `!string.IsNullOrWhiteSpace(t.CurrentStageId)` evaluated to `true`, causing an unstarted, dependency-blocked task to be falsely categorized as In-Flight.
     4. Furthermore, `HasExecutionRun` (introduced in Step 11.17) is `false` for unstarted tasks, but was not checked during Kanban/backlog column categorization.
   - **Requirement:** Decouple stage configuration from active execution state. Open tasks that have not started execution (`HasExecutionRun == false`) or are blocked on prerequisites must always reside in the "Open (Backlog)" column. Clean up `TaskMarkdownSyncService` so unstarted tasks do not preemptively claim an active stage ID, and extract backlog status classification logic into a dedicated service adhering to UI/presentation separation.

4. **Sidebar Collapse Ergonomics (Invisible Re-Open Button):**
   - **Current Bug:** Clicking the sidebar toggle button collapses the layout grid column to `72px`. However, `.brand-header` contains `.brand-logo-wrap` (logo + "NightCodeForge" text, approx. 180px wide) followed by `.sidebar-toggle-btn`. Because `.forge-sidebar` has `overflow: hidden`, the text pushes the toggle button completely off-screen to the right. The button becomes invisible and inaccessible, preventing users from re-expanding the sidebar.
   - **Requirement:** Provide dedicated `.sidebar-collapsed` CSS rules in `app.css` that hide the text labels (`.brand-text`, nav link text), center the icons, and keep the sidebar toggle button permanently visible, centered, and clickable ($\ge 40\times 40\text{px}$ touch target) in both states.

5. **Top-Left Logo Replacement with SVG Favicon:**
   - **Current Limitation:** In `NavMenu.razor`, the brand icon renders a temporary unicode emoji (`🌙`).
   - **Requirement:** Replace the emoji with the vector SVG asset at `wwwroot/favicon.svg` (featuring the NightCodeForge golden crescent moon and cyan spark), styled cleanly at $32\times 32\text{px}$ with consistent glow and icon alignment.

---

## 2. Architecture & Data Flow

```mermaid
flowchart TD
    subgraph BacklogRejection ["1. Task Rejection Lifecycle Flow"]
        UI_Action["Maintainer clicks 'Reject Task'<br/>(TaskDetailDrawer / TaskDetail / KanbanCard)"]
        ConfirmModal["Rejection Reason Dialog<br/>(Optional/Mandatory Reason Input)"]
        Orchestrator["TaskOrchestrator.RejectTaskAsync(taskId, reason)"]
        QueueEvict["DevQueueService.EvictOrCancelJob(taskId)"]
        AuditHistory["TaskRepository.UpdateWithHistoryAsync<br/>(AuditActors.User, reason)"]
        DepStore["DependencyStore.UpdateTaskStatusAsync<br/>(TaskOverallStatus.Rejected)"]
        DiskSync["TaskMarkdownSyncService.SyncTaskMarkdownAndMirrorAsync<br/>(status: rejected)"]
        
        UI_Action --> ConfirmModal
        ConfirmModal --> Orchestrator
        Orchestrator --> QueueEvict
        Orchestrator --> AuditHistory
        Orchestrator --> DepStore
        Orchestrator --> DiskSync
    end

    subgraph KanbanHygiene ["2. Backlog Column Classification Hygiene"]
        TaskEntity["ForgeTask Entity"]
        Classifier["BacklogStatusClassifier.GetColumn(task, isBlocked)"]
        
        TaskEntity --> Classifier
        Classifier -->|Status == Suggested| Col1["Column 1: Suggested"]
        Classifier -->|Status == Open &amp; !HasExecutionRun| Col2["Column 2: Open (Backlog)<br/>(Includes blocked or waiting tasks)"]
        Classifier -->|Status in (Reviewed, Implemented) OR<br/>Status == Open &amp; HasExecutionRun &amp; Active| Col3["Column 3: In-Flight<br/>(Actively executing stages)"]
        Classifier -->|Status == Ready| Col4["Column 4: Ready for Review"]
        Classifier -->|Status in (Closed, Rejected, Failed)| Col5["Column 5: Closed &amp; Archived"]
    end

    subgraph ShellErgonomics ["3. Shell & Navigation Ergonomics"]
        Favicon["wwwroot/favicon.svg"] --> BrandHeader["NavMenu.razor Brand Header<br/>(&lt;img src='favicon.svg' class='brand-favicon'/&gt;)"]
        ToggleBtn["Sidebar Toggle Button (☰ / »)"] --> LayoutState["MainLayout: _isSidebarCollapsed"]
        LayoutState -->|Collapsed (72px)| CollapsedCSS[".sidebar-collapsed Styles<br/>- Hide .brand-text<br/>- Center Toggle Button<br/>- Icon-only Nav Links"]
        LayoutState -->|Expanded (260px)| ExpandedCSS["Expanded Styles<br/>- Full logo &amp; text<br/>- Right-aligned toggle button"]
    end
```

---

## 3. Detailed Specifications & Implementation Plan

### 3.1 Task Rejection Subsystem

#### Domain & Orchestration Requirements
1. **`ITaskOrchestrator` Extension:**
   - Define `Task RejectTaskAsync(int taskId, string? reason = null, CancellationToken cancellationToken = default);` in `ITaskOrchestrator.cs`.
   - Validates that the task exists and is **not already in a terminal state** (`TaskOverallStatus.Closed` or `TaskOverallStatus.Rejected`). Allows rejection of tasks in `Suggested`, `Open`, `Reviewed`, `Implemented`, `Ready`, or `Failed`.
   - Transitions `task.Status = TaskOverallStatus.Rejected` and updates `task.UpdatedAt = DateTime.UtcNow`.
   - If the task has an active or scheduled Hangfire job (`task.LastHangfireJobId`), cancels/deletes the job via Hangfire `BackgroundJobClient.Delete(jobId)` or `DevQueueService`.
   - Records the state mutation with history via `_taskRepository.UpdateWithHistoryAsync(..., changedBy: AuditActors.User, reason: reason ?? "Task manually rejected by maintainer.")`.
   - Updates `_dependencyStore.UpdateTaskStatusAsync(taskId, TaskOverallStatus.Rejected)`.
   - Synchronizes markdown frontmatter to disk (`status: rejected`) via `SyncTaskMarkdownAndMirrorAsync`.
   - Appends an `ExecutionLog` entry with `LogLevel.Warning` noting maintainer rejection and reason.

2. **`ITaskMutationService` Wrapper:**
   - In `NightCodeForge.Web.Services.TaskMutationService`, add `Task RejectTaskAsync(ForgeTask task, string? reason = null);` delegating to `ITaskOrchestrator.RejectTaskAsync`.

#### UI Components & User Experience
1. **`TaskDetailDrawer.razor`:**
   - In the footer actions bar, render a `🗑️ Reject Task` button (using `.forge-btn-danger`) whenever `Task.Status != TaskOverallStatus.Closed && Task.Status != TaskOverallStatus.Rejected`.
   - Clicking opens a lightweight rejection confirmation dialog or prompt allowing the maintainer to provide an optional reason.
   - Upon confirmation, invokes rejection, displays a warning toast (`ToastService.Show("Task Rejected", $"Task #{Task.Id} was moved to Archived/Rejected.", ToastLevel.Warning)`), and triggers `OnTaskUpdated`.

2. **`TaskDetail.razor`:**
   - In the header action bar, render the `🗑️ Reject Task` button when the task is not in a terminal state.
   - Rejection updates the task, displays toast, and refreshes the page state.

3. **`KanbanCard.razor`:**
   - Add a reject action to the card context or quick-actions bar (or next to priority chip) for non-terminal tasks.

---

### 3.2 Backlog Priority Filter Text Color & Visual Contrast

#### Problem & Root Cause Analysis
In `Backlog.razor`:
```html
<button type="button"
        class="stat-chip priority-filter-btn @(_priorityFilter == PriorityFilterMode.All ? "active" : "")"
        @onclick="() => FilterByPriority(PriorityFilterMode.All)">
    All
</button>
```
Because `<button>` elements default to user-agent text color `ButtonText` unless explicitly overridden, and `.stat-chip` in `app.css` only set text colors on `.stat-chip-label` and `.stat-chip-val`, the raw text inside `.priority-filter-btn` renders in black.

#### Required Changes in `app.css`
1. Ensure `.stat-chip` and `.priority-filter-btn` set explicit primary text color:
   ```css
   .stat-chip,
   .priority-filter-btn {
       color: var(--text-primary);
       font-family: inherit;
       font-weight: 500;
   }

   .priority-filter-btn.active {
       color: #ffffff;
       font-weight: 600;
   }
   ```
2. For specific priority filter modes when active or idle:
   - Ensure the dot and text contrast ratio $\ge 4.5:1$ against the dark background.
   - When not active, button text must be `#e2e8f0` (`var(--text-secondary)`) or `#ffffff`, never black.

---

### 3.3 Promoted Task Column Hygiene & Stage Normalization

#### Problem & Root Cause Analysis
1. In `TaskMarkdownSyncService.cs` (lines 1072, 1621) and `TriageService.cs` (line 180), new tasks were initialized with `CurrentStageId = StandardStageIds.TaskReview`.
2. In `KanbanBoard.razor` and `Backlog.razor`:
   ```csharp
   // Erroneous classification:
   var openTasks = Tasks.Where(t => t.Status == TaskOverallStatus.Open && string.IsNullOrWhiteSpace(t.CurrentStageId)).ToList();
   var inFlightTasks = Tasks.Where(t => t.Status == TaskOverallStatus.Reviewed || t.Status == TaskOverallStatus.Implemented || (t.Status == TaskOverallStatus.Open && !string.IsNullOrWhiteSpace(t.CurrentStageId))).ToList();
   ```
3. A task promoted to `Open` had `CurrentStageId == "task-review"`, so it was immediately placed in Column 3 ("In-Flight"), even though it had never executed (`HasExecutionRun == false`) and its prerequisites were unfulfilled.

#### Architectural Separation: `BacklogStatusClassifier`
Adhering to the rule that Razor components must remain presentation-focused, extract all column categorization logic into a dedicated helper in `NightCodeForge.Web.Services`:

```csharp
namespace NightCodeForge.Web.Services;

public enum BacklogKanbanColumn
{
    Suggested,
    Open,
    InFlight,
    ReadyForReview,
    Closed
}

public static class BacklogStatusClassifier
{
    public static BacklogKanbanColumn GetColumn(ForgeTask task)
    {
        if (task.Status == TaskOverallStatus.Suggested)
            return BacklogKanbanColumn.Suggested;

        if (task.Status == TaskOverallStatus.Ready)
            return BacklogKanbanColumn.ReadyForReview;

        if (task.Status is TaskOverallStatus.Closed or TaskOverallStatus.Rejected or TaskOverallStatus.Failed)
            return BacklogKanbanColumn.Closed;

        if (task.Status is TaskOverallStatus.Reviewed or TaskOverallStatus.Implemented)
            return BacklogKanbanColumn.InFlight;

        if (task.Status == TaskOverallStatus.Open)
        {
            // An Open task is only In-Flight if it has actually started execution
            if (task.HasExecutionRun && !string.IsNullOrWhiteSpace(task.CurrentStageId))
            {
                return BacklogKanbanColumn.InFlight;
            }

            return BacklogKanbanColumn.Open;
        }

        return BacklogKanbanColumn.Open;
    }

    public static bool IsInFlight(ForgeTask task) => GetColumn(task) == BacklogKanbanColumn.InFlight;
    public static bool IsOpenBacklog(ForgeTask task) => GetColumn(task) == BacklogKanbanColumn.Open;
}
```

#### TaskMarkdownSyncService Hygiene
In `TaskMarkdownSyncService.cs` and `TriageService.cs`:
- Unstarted tasks must **not** have `CurrentStageId` preset to `"task-review"`. Initialize `CurrentStageId = string.Empty`.
- `TaskOrchestrator.ProcessTaskAsync` already dynamically assigns the initial stage from the pipeline definition upon the first execution pass:
  ```csharp
  if (string.IsNullOrWhiteSpace(task.CurrentStageId))
  {
      var firstStage = pipeline.Stages.FirstOrDefault();
      if (firstStage != null) task.CurrentStageId = firstStage.StageId;
  }
  ```

#### DAG Layout Engine Alignment
In `DagLayoutEngine.cs`:
- Update `GetStatusLabel`:
  ```csharp
  if (task.Status == TaskOverallStatus.Open && task.HasExecutionRun && !string.IsNullOrWhiteSpace(task.CurrentStageId))
  {
      return $"In-Flight: {task.CurrentStageId}";
  }
  ```
  Unstarted Open tasks will display `🟢 Ready` (if unblocked) or `🔒 Blocked ({count})` (if prerequisites pending), never `In-Flight`.

---

### 3.4 Web Shell Ergonomics: Sidebar Collapse & Toggle Button Visibility

#### Problem & Root Cause Analysis
- `MainLayout.razor` binds `@(_isSidebarCollapsed ? "sidebar-collapsed" : "")` to `.forge-layout`.
- In `app.css`, `.forge-layout.sidebar-collapsed` sets `grid-template-columns: 72px 1fr;`.
- In `NavMenu.razor`:
  ```html
  <div class="brand-header">
      <NavLink class="brand-logo-wrap" href="" Match="NavLinkMatch.All">
          <div class="brand-icon">🌙</div>
          <span class="brand-text">NightCode<span>Forge</span></span>
      </NavLink>
      <button class="sidebar-toggle-btn" @onclick="ToggleSidebar" title="Toggle Sidebar">
          ☰
      </button>
  </div>
  ```
- Because `.brand-logo-wrap` has fixed width and text, it occupies the entire 72px and overflows. With `.forge-sidebar { overflow: hidden; }`, `.sidebar-toggle-btn` is clipped out of the viewport. The user cannot click it to re-open the menu.

#### Required Styling & Markup Updates
1. **Collapsed Brand Header Layout (`app.css`):**
   ```css
   .forge-layout.sidebar-collapsed .brand-header {
       padding: 0 0.5rem;
       justify-content: center;
       position: relative;
   }

   .forge-layout.sidebar-collapsed .brand-logo-wrap .brand-text {
       display: none;
   }

   .forge-layout.sidebar-collapsed .brand-logo-wrap {
       display: none; /* Hide logo link in 72px header to make toggle button prominent, or display icon only */
   }

   .forge-layout.sidebar-collapsed .sidebar-toggle-btn {
       width: 44px;
       height: 44px;
       display: flex;
       align-items: center;
       justify-content: center;
       font-size: 1.25rem;
       margin: 0 auto;
       border-radius: var(--radius-md);
       background: var(--bg-surface);
       color: var(--text-primary);
   }
   ```
2. **Collapsed Navigation Links (`app.css`):**
   ```css
   .forge-layout.sidebar-collapsed .forge-nav-link {
       justify-content: center;
       padding: 0.65rem 0;
   }

   .forge-layout.sidebar-collapsed .forge-nav-link span:not(.nav-icon) {
       display: none;
   }

   .forge-layout.sidebar-collapsed .nav-section-label {
       display: none;
   }

   .forge-layout.sidebar-collapsed .sidebar-footer {
       padding: 0.5rem;
       justify-content: center;
   }

   .forge-layout.sidebar-collapsed .sidebar-footer span {
       display: none;
   }
   ```
3. **Dynamic Toggle Icon / Accessibility:**
   - In `NavMenu.razor`, update toggle button title and text based on collapsed state:
     - Expanded: `☰` or `«` (title: "Collapse Sidebar")
     - Collapsed: `»` or `☰` (title: "Expand Sidebar")
   - Pass `IsCollapsed` parameter from `MainLayout` or state store to `NavMenu` so it can toggle ARIA attributes (`aria-expanded`).

---

### 3.5 Top-Left Brand Logo: Replace Emoji with SVG Favicon

#### Asset Integration
- NightCodeForge has a dedicated vector brand asset at `src/NightCodeForge.Web/wwwroot/favicon.svg`.
- Replace the emoji in `NavMenu.razor`:
  ```html
  <NavLink class="brand-logo-wrap" href="" Match="NavLinkMatch.All">
      <img src="favicon.svg" alt="NightCodeForge" class="brand-favicon" />
      <span class="brand-text">NightCode<span>Forge</span></span>
  </NavLink>
  ```
- Add CSS in `app.css`:
  ```css
  .brand-favicon {
      width: 32px;
      height: 32px;
      border-radius: var(--radius-md);
      box-shadow: 0 0 12px rgba(56, 189, 248, 0.25);
      flex-shrink: 0;
      transition: transform var(--transition-fast);
  }

  .brand-logo-wrap:hover .brand-favicon {
      transform: scale(1.05);
  }
  ```

---

## 4. Implementation Verification Plan

### 4.1 Automated Unit & Integration Tests

1. **BacklogStatusClassifier Tests (`BacklogStatusClassifierTests.cs`):**
   - Test 1: Task with `Status = TaskOverallStatus.Open`, `HasExecutionRun = false`, `CurrentStageId = "task-review"` $\rightarrow$ returns `BacklogKanbanColumn.Open`.
   - Test 2: Task with `Status = TaskOverallStatus.Open`, `HasExecutionRun = true`, `CurrentStageId = "task-review"` $\rightarrow$ returns `BacklogKanbanColumn.InFlight`.
   - Test 3: Task with `Status = TaskOverallStatus.Reviewed` $\rightarrow$ returns `BacklogKanbanColumn.InFlight`.
   - Test 4: Task with `Status = TaskOverallStatus.Suggested` $\rightarrow$ returns `BacklogKanbanColumn.Suggested`.
   - Test 5: Task with `Status = TaskOverallStatus.Rejected` $\rightarrow$ returns `BacklogKanbanColumn.Closed`.

2. **Task Orchestrator Rejection Tests (`TaskOrchestratorRejectionTests.cs`):**
   - Test 1: Rejecting an `Open` task sets `Status = TaskOverallStatus.Rejected`, records audit history with `AuditActors.User`, updates dependency store, and logs warning.
   - Test 2: Rejecting an `Open` task queued in Hangfire deletes/cancels the job.
   - Test 3: Rejecting a task already in `Closed` throws `InvalidOperationException`.

3. **Web Component Integration Tests (`BacklogPageRejectionAndFilterTests.cs`):**
   - Test 1: Priority filter buttons have non-empty text and white/light styling class.
   - Test 2: Task detail drawer renders `Reject Task` button for `Open` tasks and successfully invokes rejection callback.
   - Test 3: Promoted task with unready prerequisites renders in Column 2 (`Open`), not Column 3 (`In-Flight`).

4. **Visual & Shell Tests (`NavMenuShellTests.cs`):**
   - Test 1: `NavMenu.razor` renders `<img src="favicon.svg"` with class `brand-favicon`.
   - Test 2: Sidebar toggle button remains rendered and has proper ARIA attributes when collapsed.

---

## 5. Definition of Done

- [ ] Maintainers can reject any non-terminal task (Suggested, Open, Reviewed, Implemented, Ready) directly from `TaskDetailDrawer`, `TaskDetail`, and `KanbanCard`.
- [ ] Rejection records immutable history attribution, cleans up Hangfire/DevQueue jobs, and syncs markdown to disk.
- [ ] Priority filter chips in Backlog render white text on dark surfaces with proper contrast across all modes.
- [ ] Unstarted Open tasks (including promoted Task 4 with pending prerequisites) render in the "Open (Backlog)" column and never in "In-Flight".
- [ ] Sidebar toggle button remains fully visible, centered, and clickable when collapsed, smoothly transitioning between 72px and 260px.
- [ ] Top-left brand header uses `favicon.svg` instead of the emoji.
- [ ] All automated unit and integration tests pass cleanly (`dotnet test --filter "Category!=Acceptance&Category!=E2E"`).
