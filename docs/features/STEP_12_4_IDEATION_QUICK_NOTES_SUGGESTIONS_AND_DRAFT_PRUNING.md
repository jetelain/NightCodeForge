# 💡 Step 12.4: Ideation Quick Notes, Tri-State Proposal Cards & 24h Draft Pruning

> **Feature Implementation Step (Backlog Management & Ideation Lifecycle)** *(formerly Step 11.5)*  
> *Parent Plan:* [Features & Integrations Roadmap](README.md)  
> *Companion Steps:* [Step 12.13A: Ideation Backlog Mutation Tools & Prompt Policy](../archive/phase-12/STEP_12_13A_IDEATION_BACKLOG_MUTATION_TOOLS_AND_PROMPT_POLICY.md) | [Step 12.13B: "Refine in Ideation" Workflow & Mutation UI](../archive/phase-12/STEP_12_13B_REFINE_IN_IDEATION_WORKFLOW_AND_MUTATION_UI.md)  
> *Project Target:* `src/NightCodeForge.Core/Domain`, `src/NightCodeForge.Infrastructure/Services`, `src/NightCodeForge.Web/Components/Ideation`, `src/NightCodeForge.Web/Components/Pages` | *Status:* Planned | *Target Framework:* .NET 10 (C# 13)  
> *Aligns With:* [AGENTS.md](../../AGENTS.md), [docs/PLAN.md](../PLAN.md)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Objective

Transform the Ideation proposal and quick-capture experience with a clean three-state lifecycle (`Draft`, `Suggested`, `Open`):

1. **Ideation Backlog & Quick Notes Intake:** Enable developers to rapidly capture spontaneous thoughts, feature ideas, and architectural notes into an **Ideation Backlog** (`Status = TaskOverallStatus.Suggested`) from desktop or mobile without immediately launching an AI chat session.
2. **Three-Way Proposal Lifecycle on `IdeationProposalCard.razor`:**
   - **Approve (Promote to `Open`):** Promotes the draft into the active engineering queue ready for DAG scheduling and execution.
   - **Keep as Suggestion (`Draft` $\rightarrow$ `Suggested`):** Preserves the task in the Ideation Backlog / Suggestions tab for future evaluation without queuing it for autonomous execution.
   - **Discard (Delete):** Permanently purges the draft item from the database.
3. **Immediate Database Drafts:** Write proposed tasks to SQLite immediately as [`TaskOverallStatus.Draft`](../../src/NightCodeForge.Core/Domain/Enums.cs) so subsequent tasks in the same conversation have concrete integer IDs (`depends_on: [104]`).
4. **Automated TTL Pruning for Abandoned Drafts:** Purge unapproved, unpromoted `Draft` tasks older than 24 hours via a background Hangfire recurring job (`IdeationDraftPruningService`), while `Suggested` tasks are preserved indefinitely.

*(Scoped agent mutation tools `get_task`/`edit_task`/`set_task_priority`/`remove_task` are specified in [Step 12.13A](../archive/phase-12/STEP_12_13A_IDEATION_BACKLOG_MUTATION_TOOLS_AND_PROMPT_POLICY.md), and the "Refine in Ideation" `/ideation?taskId={id}` workflow is specified in [Step 12.13B](../archive/phase-12/STEP_12_13B_REFINE_IN_IDEATION_WORKFLOW_AND_MUTATION_UI.md).)*

---

## 2. Problem Statement & User Experience

### 2.1 The Need for Quick Notes ("Think Now, Chat Later")
- Developers often get spontaneous ideas while reviewing code, commuting, or testing on mobile via Tailscale (e.g. *"Support DevSkim in SAST", "Dual model setup for Qwen"*).
- Currently, creating a task requires either navigating through the AI Ideation chat or authoring a full task form.
- Developers need a 5-second capture input: type a title or note, tap "Save to Ideation Backlog", and return to it later.

### 2.2 The Binary Trap: Approve vs. Discard
- In ideation chat, an agent might propose 4 tasks. The user might want to run Task 1 immediately, discard Task 4, but keep Tasks 2 and 3 as future ideas without flooding the active execution queue.
- Forcing a binary choice between "Run Now" and "Delete" causes developers to lose valuable architectural proposals or prematurely enqueue tasks that aren't ready for code generation.

---

## 3. High-Level Architecture & Lifecycle Flow

```mermaid
flowchart TD
    subgraph Intake ["Intake Channels"]
        QuickNote["Quick Note Input\n('Support DevSkim CLI')"]
        IdeationChat["Ideation Chat Session\n(User + Agent)"]
    end

    subgraph Database ["SQLite Database"]
        TaskDB[("ForgeTask")]
    end

    subgraph Proposals ["Ideation Proposal Card"]
        DraftCard["Draft Task Card (#104)"]
        ApproveBtn["Approve (-> Open)"]
        KeepBtn["Keep as Suggestion (-> Suggested)"]
        DiscardBtn["Discard (Delete)"]
    end

    subgraph Backlog ["Ideation Backlog & Suggestions"]
        SuggestionsList["IdeationBacklogTab.razor\n(Status == Suggested)"]
    end

    subgraph ExecutionQueue ["Autonomous Dev Queue"]
        ActiveQueue["Hangfire Worker Queue\n(Stages 1-6)"]
    end

    QuickNote -->|Save directly with Status = Suggested| TaskDB
    IdeationChat -->|propose_tasks() -> Status = Draft| TaskDB
    TaskDB --> DraftCard

    DraftCard --> ApproveBtn -->|Status = Open| ActiveQueue
    DraftCard --> KeepBtn -->|Status = Suggested| SuggestionsList
    DraftCard --> DiscardBtn -->|Delete from DB| TaskDB
```

---

## 4. Behavioral Specifications

### 4.1 Quick Notes Intake ("Ideation Backlog")
1. **Quick Capture Bar:**
   Provide a lightweight quick-note component accessible from the Ideation page (`/ideation`) and the top navigation bar:
   - Input: Title (required), Brief Note / Context (optional).
   - Action: "Save to Ideation Backlog".
2. **Database Persistence:**
   Saved directly into `ForgeTask`:
   - `Status = TaskOverallStatus.Suggested`
   - `MergeMode = MergeMode.Automatic` (default)
   - `Priority = 3` (Normal default)
3. **Ideation Backlog Tab (`IdeationBacklogTab.razor`):**
   In the Ideation page, provide two views/tabs:
   - **Active Ideation Chat:** The interactive conversation with agent and proposal cards.
   - **Ideation Backlog (Suggestions):** List of all tasks with `Status == TaskOverallStatus.Suggested`. Each card displays title, preview, creation timestamp, and actions:
     - 🚀 **Approve & Queue:** Promotes to `Open`, entering the active dev queue.
     - 💡 **Refine in Ideation:** Links to `/ideation?taskId={id}` ([Step 12.13B](../archive/phase-12/STEP_12_13B_REFINE_IN_IDEATION_WORKFLOW_AND_MUTATION_UI.md)).
     - 🗑️ **Delete:** Removes the suggestion.

### 4.2 Three-Way Action on Proposal Cards
When the agent proposes tasks during chat, [`IdeationProposalCard.razor`](../../src/NightCodeForge.Web/Components/Ideation/IdeationProposalCard.razor) renders three distinct buttons:
1. **Approve (Promote to Open):**
   - Sets `Status = TaskOverallStatus.Open`.
   - Allows maintainer to configure `MergeMode` (`Automatic`, `ManualReviewRequired`; `ExternalPullRequest` enabled via Step 12.11A).
   - Task enters the active Kanban backlog and execution queue.
2. **Keep as Suggestion (`Draft` $\rightarrow$ `Suggested`):**
   - Sets `Status = TaskOverallStatus.Suggested`.
   - The card transitions to a "Saved to Suggestions" state with a link to the Ideation Backlog tab.
   - Excluded from active execution queue; protected from 24h TTL draft pruning.
3. **Discard (Delete):**
   - Deletes the `ForgeTask` record and associated `TaskDependency` entries from SQLite.
   - The card is removed from the proposal list.

### 4.3 Automated TTL Pruning for Abandoned Drafts (`IdeationDraftPruningService`)
- Recurring Hangfire job `IdeationDraftPruningService` executes daily.
- Finds tasks where `Status == TaskOverallStatus.Draft` and `CreatedAt < DateTime.UtcNow.AddHours(-24)`.
- Deletes matching records and their dependencies.
- **`Suggested` tasks (`Status == 0`) are NEVER pruned automatically.**

---

## 5. Implementation Checklist & Deliverables

- [ ] **Domain & Enums:**
  - [ ] Ensure `TaskOverallStatus.Draft = -1` and `TaskOverallStatus.Suggested = 0` are strictly differentiated across queries.
- [ ] **Ideation Proposal Card Actions:**
  - [ ] Add "Keep as Suggestion" button (`Draft -> Suggested`) to [`IdeationProposalCard.razor`](../../src/NightCodeForge.Web/Components/Ideation/IdeationProposalCard.razor).
  - [ ] Implement transition `Draft -> Suggested` in `ITaskRepository` / `IdeationService`.
  - [ ] Ensure "Approve" (`Draft -> Open` with `MergeMode` selector) and "Discard" (`Delete`) work cleanly on persisted `Draft` records.
- [ ] **Quick Notes & Ideation Backlog UI:**
  - [ ] Add quick-capture input box in [`Ideation.razor`](../../src/NightCodeForge.Web/Components/Pages/Ideation.razor) (and nav bar shortcut).
  - [ ] Create `IdeationBacklogTab.razor` listing all `Suggested` tasks with filters and `Approve & Queue` / `Delete` actions.
- [ ] **Background Pruning:**
  - [ ] Implement Hangfire recurring job `IdeationDraftPruningService` purging `Draft` tasks older than 24h while preserving `Suggested` tasks.
- [ ] **Automated Tests:**
  - [ ] Unit test: "Keep as Suggestion" sets `Status = Suggested` and excludes task from active execution queue.
  - [ ] Unit test: `IdeationDraftPruningService` deletes stale `Draft` tasks older than 24h and preserves `Suggested` tasks.
  - [ ] bUnit tests for `IdeationProposalCard.razor` and `IdeationBacklogTab.razor`.

