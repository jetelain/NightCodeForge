# 💡 Step 12.4: Ideation Quick Notes, Suggestions & Conversational Refinement

> **Phase 12 Implementation Step (Extend Features - Backlog Management & Dependencies)** *(formerly Step 11.5)*  
> *Parent Plan:* [Phase 12 Master Plan](../12_EXTEND_FEATURES.md)  
> *Project Target:* `src/NightCodeForge.Agents/Ideation`, `src/NightCodeForge.Agents/Tools`, `src/NightCodeForge.Core/Domain`, `src/NightCodeForge.Web/Components/Ideation`, `src/NightCodeForge.Web/Components/Pages`, `tests/NightCodeForge.Agents.Tests` | *Status:* Planned | *Target Framework:* .NET 10 (C# 13)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Objective

Transform the Ideation experience into a full-lifecycle feature discovery and refinement hub:

1. **Ideation Backlog & Quick Notes Intake:** Enable developers to rapidly capture spontaneous thoughts, feature ideas, and architectural notes into an **Ideation Backlog** (e.g. from mobile or desktop) without immediately launching an AI chat session.
2. **Three-Way Proposal Lifecycle:** In the Ideation chat proposal card, provide three clear actions:
   - **Approve (Promote to `Open`):** Promotes the draft into the active engineering queue ready for DAG scheduling and execution.
   - **Keep as Suggestion (`Draft` $\rightarrow$ `Suggested`):** Preserves the task in the Ideation Backlog / Suggestions tab for future evaluation without queuing it for autonomous execution.
   - **Discard (Delete):** Permanently purges the draft item from the database.
3. **Conversational Refinement on Suggested Tasks:** Allow maintainers to click **"Refine in Ideation"** on any suggested task or quick note. This launches an Ideation chat session pre-seeded with the task's context, description, and open questions, allowing developer and agent to iteratively polish the specification before approving it.
4. **Immediate Database Drafts & Scoped Agent Tools:** Write proposed tasks to SQLite immediately as [TaskOverallStatus.Draft](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Domain/Enums.cs) so subsequent tasks have concrete integer IDs (`depends_on: [104]`). Equip the read-only ideation agent with scoped `edit_task` and `remove_task` tools (with full backlog management and priority reworking specified in [Step 12.13](STEP_12_13_IDEATION_BACKLOG_MANAGEMENT_AND_TASK_MUTATION_TOOLS.md)).
5. **Automated TTL Pruning for Drafts:** Purge unapproved, unpromoted `Draft` tasks older than 24 hours via a background Hangfire recurring job (`IdeationDraftPruningService`), while `Suggested` tasks are preserved indefinitely.

---

## 2. Problem Statement & User Experience

### 2.1 The Need for Quick Notes ("Think Now, Chat Later")
- Developers often get spontaneous ideas while reviewing code, commuting, or testing on mobile via Tailscale (e.g. *"Support DevSkim in SAST", "Dual model setup for Qwen"*).
- Currently, creating a task requires either navigating through the AI Ideation chat or authoring markdown manually. There is no rapid scratchpad or quick note inbox.
- Developers need a 5-second capture input: type a title or note, tap "Save to Ideation Backlog", and return to it later.

### 2.2 The Binary Trap: Approve vs. Discard
- In ideation chat, an agent might propose 4 tasks. The user might want to run Task 1 immediately, discard Task 4, but keep Tasks 2 and 3 as future ideas without flooding the active execution queue.
- Forcing a binary choice between "Run Now" and "Delete" causes developers to lose valuable architectural proposals or prematurely enqueue tasks that aren't ready for code generation.

### 2.3 Conversational Task Refinement
- Once a task is kept as a suggestion or entered as a quick note, the developer wants to refine it later with an AI agent:
  - *"Let's revisit task #104. What interfaces will it need?"*
  - The developer clicks "Refine in Ideation" on the suggestion card, and the Ideation Agent is automatically initialized with the task's existing details, ready to explore code, propose sub-tasks, and update the task via `edit_task`.

---

## 3. High-Level Architecture & Lifecycle Flow

```mermaid
flowchart TD
    subgraph Intake ["Intake Channels"]
        QuickNote["Quick Note Input<br/>('Support DevSkim CLI')"]
        IdeationChat["Ideation Chat Session<br/>(User + Agent)"]
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
        SuggestionsList["Suggested Tasks View<br/>(Ideation Backlog)"]
        RefineBtn["Refine in Ideation<br/>(Pre-seeded Chat)"]
    end

    subgraph ExecutionQueue ["Autonomous Dev Queue"]
        ActiveQueue["Hangfire Worker Queue<br/>(Stages 1-6)"]
    end

    QuickNote -->|Save directly with Status = Suggested| TaskDB
    IdeationChat -->|propose_tasks() -> Status = Draft| TaskDB
    TaskDB --> DraftCard

    DraftCard --> ApproveBtn -->|Status = Open| ActiveQueue
    DraftCard --> KeepBtn -->|Status = Suggested| SuggestionsList
    DraftCard --> DiscardBtn -->|Delete from DB| TaskDB

    SuggestionsList --> RefineBtn -->|Seed Conversation| IdeationChat
    IdeationChat -->|edit_task(#104)| TaskDB
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
   - `Priority = 3` (Medium default)
3. **Ideation Backlog Tab:**
   In the Ideation page, provide two views/tabs:
   - **Active Ideation Chat:** The interactive conversation with agent and proposal cards.
   - **Ideation Backlog (Suggestions):** List of all tasks with `Status == TaskOverallStatus.Suggested`. Each card displays title, preview, creation timestamp, and actions:
     - 🚀 **Approve & Queue:** Promotes to `Open`, entering active dev queue.
     - 💬 **Refine in Ideation:** Launches an ideation chat pre-seeded with this suggestion.
     - 🗑️ **Delete:** Removes the suggestion.

### 4.2 Three-Way Action on Proposal Cards
When the agent proposes tasks during chat, `IdeationProposalCard.razor` renders three distinct buttons:
1. **Approve (Promote to Open):**
   - Sets `Status = TaskOverallStatus.Open`.
   - Allows maintainer to configure `MergeMode` (`Automatic`, `ManualReviewRequired`; `ExternalPullRequest` enabled via Step 12.11).
   - Task enters the active Kanban backlog and execution queue.
2. **Keep as Suggestion (`Draft` $\rightarrow$ `Suggested`):**
   - Sets `Status = TaskOverallStatus.Suggested`.
   - The card transitions to a "Saved to Suggestions" state with a link to the Ideation Backlog tab.
   - Excluded from active execution queue; protected from 24h TTL draft pruning.
3. **Discard (Delete):**
   - Deletes the `ForgeTask` record and associated `TaskDependency` entries from SQLite.
   - The card is removed from the proposal list with an undo snackbar.

### 4.3 Conversational Refinement of Suggested Tasks
1. **Refinement Initiation:**
   When the user clicks "Refine in Ideation" on a suggested task (or navigates to `/ideation?refineTaskId={id}`):
   - Starts or switches to an Ideation session linked to that task.
   - Injects a system notice into the conversation:
     ```markdown
     [System Notice]: The developer is refining suggested task #{task.Id}: '{task.Title}'.
     Current Description:
     {task.Description}
     
     Your objective is to inspect relevant files, discuss technical trade-offs, answer developer questions, and use the 'edit_task' tool to update the title, description, dependencies, or acceptance criteria based on the conversation.
     ```
2. **Agent Scoped Tooling:**
   The ideation agent has access to scoped mutation tools (see [Step 12.13](STEP_12_13_IDEATION_BACKLOG_MANAGEMENT_AND_TASK_MUTATION_TOOLS.md) for full backlog management specification):
   - `edit_task(task_id, title?, description?, priority?, depends_on_ids?)`: Updates the target task. Throws hard domain exception if `task_id` is currently in-progress (`InProgress`) or closed.
   - `remove_task(task_id)`: Removes the task if the user requests deletion during chat.
   - `propose_tasks(...)`: Allows proposing additional sub-tasks or follow-up items linked via DAG dependencies.

### 4.4 Automated TTL Pruning for Abandoned Drafts
- Recurring Hangfire job `IdeationDraftPruningService` executes daily.
- Finds tasks where `Status == TaskOverallStatus.Draft` and `CreatedAt < DateTime.UtcNow.AddHours(-24)`.
- Deletes matching records and their dependencies.
- **`Suggested` tasks (`Status == 0`) are NEVER pruned automatically.**

---

## 5. Implementation Checklist & Deliverables

- [ ] **Domain & Enums:**
  - [ ] Ensure `TaskOverallStatus.Draft = -1` and `TaskOverallStatus.Suggested = 0` are strictly differentiated across queries.
- [ ] **Ideation Proposal Card Actions:**
  - [ ] Add "Keep as Suggestion" button to `IdeationProposalCard.razor`.
  - [ ] Implement transition `Draft -> Suggested` in `ITaskRepository` / `IdeationService`.
  - [ ] Add "Approve" (`Draft -> Open`) with `MergeMode` selector.
  - [ ] Add "Discard" (`Delete`) with confirmation.
- [ ] **Quick Notes & Ideation Backlog UI:**
  - [ ] Add quick-capture input box in `Ideation.razor` (and nav bar shortcut).
  - [ ] Create `IdeationBacklogTab.razor` listing all `Suggested` tasks with filters.
  - [ ] Add "Refine in Ideation" action button on each suggestion card.
- [ ] **Conversational Refinement Engine:**
  - [ ] Support `refineTaskId` query parameter in `Ideation.razor`.
  - [ ] Pre-seed agent prompt context with task details when refining.
  - [ ] Integrate with `edit_task` and `remove_task` tools (specified in [Step 12.13](STEP_12_13_IDEATION_BACKLOG_MANAGEMENT_AND_TASK_MUTATION_TOOLS.md)).
- [ ] **Background Pruning:**
  - [ ] Implement Hangfire recurring job `IdeationDraftPruningService` purging `Draft` tasks older than 24h.
  - [ ] Verify `Suggested` tasks are immune to TTL pruning.
- [ ] **Automated Tests:**
  - [ ] Unit test: `edit_task` modifies `Draft` and `Suggested` tasks; fails when task is `InProgress` or `Closed`.
  - [ ] Unit test: "Keep as Suggestion" sets `Status = Suggested`.
  - [ ] Unit test: `IdeationDraftPruningService` deletes stale drafts and preserves suggestions.
  - [ ] Run test suite: `dotnet test --filter "Category!=Acceptance&Category!=E2E"`.
