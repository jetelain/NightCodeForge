# 📊 Step 12.33B: Task Estimated Cost Telemetry, Roll-up Aggregates & UI Presentation

> **Feature Specification (Part 2 of 2 — Task Roll-up Metrics, Telemetry Events & Web UI Presentation)**  
> *Parent Plan:* [Features & Integrations Roadmap](README.md)  
> *Prerequisite Step:* [Step 12.33A: OllamaRouter Session Affinity Lease Release & Session Cost Tracking Engine](STEP_12_33A_OLLAMAROUTER_SESSION_AFFINITY_RELEASE_AND_COST_COLLECTION.md)  
> *Target Components:* `src/NightCodeForge.Core/Workflow`, `src/NightCodeForge.Web/Services`, `src/NightCodeForge.Web/Components/Pages`, `src/NightCodeForge.Web/Components/Backlog`, `tests/NightCodeForge.Web.Tests`  
> *Status:* Planned | *Target Framework:* .NET 10 (C# 13)  
> *Aligns With:* [AGENTS.md](../../AGENTS.md), [docs/PLAN.md](../PLAN.md)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Summary & Objectives

Following the implementation of the session cost capture engine in [Step 12.33A](STEP_12_33A_OLLAMAROUTER_SESSION_AFFINITY_RELEASE_AND_COST_COLLECTION.md), stage execution records and conversations now persist granular cost metrics (`Cost`, `ActualCloudCost`, `ElectricityCost`, `CloudSavings`, `Currency`).

Step 12.33B surfaces this telemetry to users:
1. **Task-Level Cost Aggregation:**
   Aggregate all stage runs belonging to a `ForgeTask` to produce an authoritative estimated total cost (summing local electricity cost and actual cloud spend, as well as total cloud savings).
2. **UI & Presentation Separation:**
   Adhere strictly to the [AGENTS.md](../../AGENTS.md) UI separation rule: extract all cost formatting, currency symbol handling, breakdown text generation, and null handling into a dedicated `TaskCostPresentationHelper` in `NightCodeForge.Web.Services` (or Core presentation models). Razor components (`.razor`) remain clean and focused on markup and layout.
3. **Task Detail & Drawer Visualization:**
   - Display an estimated task cost summary badge in [`TaskDetail.razor`](../../src/NightCodeForge.Web/Components/Pages/TaskDetail.razor) and [`TaskDetailDrawer.razor`](../../src/NightCodeForge.Web/Components/Backlog/TaskDetailDrawer.razor).
   - Provide an informative breakdown popover/tooltip: e.g. `Total: $0.042 (Cloud: $0.035, Electricity: $0.007, Saved: $0.120 vs all-cloud)`.
   - Render a per-stage cost badge in each row of the Execution Records timeline.
4. **Kanban & Backlog Visibility:**
   - In [`KanbanCard.razor`](../../src/NightCodeForge.Web/Components/Backlog/KanbanCard.razor), show a subtle cost pill (e.g. `💰 $0.04`) for tasks that have recorded LLM execution spend.
5. **Ideation Chat Cost Indicator:**
   - In [`Ideation.razor`](../../src/NightCodeForge.Web/Components/Pages/Ideation.razor), display the active or completed session's accumulated cost badge in the conversation header.
6. **Live Telemetry Activity Broadcast:**
   - Include cost metrics in the `AgentActivityEvent` published via `IAgentActivityStreamer` when a stage or conversation completes, ensuring web clients update live without page reloads.

---

## 2. Task Cost Aggregation Model

### 2.1 Aggregation Semantics
A `ForgeTask` may execute across multiple pipeline stages (e.g. `TaskReview`, `Architecture`, `Implementation`, `CodeReviewFix`), and certain stages may experience retries.
- **Authoritative Total Spend (All Attempts & Retries Included):**
  The total estimated cost of a task is defined as the sum of all `StageExecutionRecord` costs associated with the task across **all attempts** (including failed, cancelled, and superseded runs prior to successful completion):
  $$\text{TaskCost} = \sum_{r \in \text{ExecutionRecords}} r.\text{Cost} \quad (\text{where } r.\text{Cost} \ne \text{null})$$
  $$\text{TaskElectricityCost} = \sum_{r \in \text{ExecutionRecords}} r.\text{ElectricityCost}$$
  $$\text{TaskActualCloudCost} = \sum_{r \in \text{ExecutionRecords}} r.\text{ActualCloudCost}$$
  $$\text{TaskCloudSavings} = \sum_{r \in \text{ExecutionRecords}} r.\text{CloudSavings}$$
  *Rationale:* Failed or retried stages consumed real inference compute, electricity, and/or cloud tokens. Tracking the sum over all stage execution records gives users complete and truthful cost transparency for the entire lifecycle of the task.
- **Currency Handling:**
  In the vast majority of cases, all runs within a workspace use the same router currency (e.g. `"$"` or `"€"`). If mixed currencies exist, format with the primary currency or indicate mixed rates. If no pricing is configured on the router (all cost fields are null), the UI cleanly hides cost badges and displays token totals instead.

### 2.2 Task Cost Summary Record
Define a strongly-typed immutable summary record in Core or Web:
```csharp
public sealed record TaskCostSummary(
    double TotalCost,
    double ActualCloudCost,
    double ElectricityCost,
    double CloudSavings,
    string Currency,
    bool HasCostData)
{
    public static readonly TaskCostSummary Empty = new(0, 0, 0, 0, "$", HasCostData: false);
}
```

---

## 3. UI & Presentation Helper Specification

Per [AGENTS.md](../../AGENTS.md), non-trivial formatting and calculations must live outside Razor `@code` blocks:

### 3.1 `TaskCostPresentationHelper`
Create `NightCodeForge.Web.Services.TaskCostPresentationHelper`:
- `TaskCostSummary ComputeTaskCost(ForgeTask? task)`:
  Rolls up `task.ExecutionRecords` into `TaskCostSummary` (`HasCostData` is `true` only when at least one `StageExecutionRecord` has `Cost.HasValue`).
- `TaskCostSummary ComputeConversationCost(Conversation? conversation)`:
  Maps an Ideation or Stage `Conversation`'s accumulated cost fields (`Cost`, `ActualCloudCost`, `ElectricityCost`, `CloudSavings`, `Currency`) into a `TaskCostSummary` (`HasCostData = conversation?.Cost.HasValue == true`).
- `string FormatCost(double? cost, string? currency)`:
  Formats costs with appropriate precision (e.g. `$0.042` or `< $0.001` if positive but rounds to zero).
- `string BuildCostBreakdownTooltip(TaskCostSummary summary)`:
  Produces user-friendly tooltip content:
  `"Estimated Spend: $0.042 (Cloud: $0.035 | Electricity: $0.007 | Cloud Savings: $0.120)"`
- `string FormatStageCost(StageExecutionRecord record)`:
  Formats an individual stage record's cost (and currency) for timeline pills, or returns `string.Empty` when `record.Cost` is `null`.

---

## 4. Web UI Components Integration

### 4.1 `TaskDetail.razor` & `TaskDetailDrawer.razor`
- In the task header / metadata badge strip (`.page-header-badges` in `TaskDetail.razor` and `.drawer-badges` in `TaskDetailDrawer.razor`) alongside priority, branch, and status badges, render an estimated cost badge when `summary.HasCostData` is `true`:
  ```html
  <span class="forge-badge badge-neutral font-mono" title="@costTooltip">
      💰 @formattedCost
  </span>
  ```
- In the **Execution Records** timeline (`.records-timeline` in both `TaskDetail.razor` and `TaskDetailDrawer.razor`):
  Alongside `rec.DurationMs`, display the stage's cost badge (and token total when available):
  ```html
  @if (rec.Cost.HasValue)
  {
      <span class="font-mono text-muted" style="font-size: 0.78rem;" title="@TaskCostPresentationHelper.BuildStageCostTooltip(rec)">
          💰 @TaskCostPresentationHelper.FormatStageCost(rec)
      </span>
  }
  ```

### 4.2 `KanbanCard.razor`
- In `KanbanCard.razor`, inside `.card-status-row` alongside the task status and active stage badges:
  Whenever `TaskCostPresentationHelper.ComputeTaskCost(Task)` has `HasCostData == true` (across any column—including `Open` tasks paused after an earlier stage, `Failed`, `Implemented`, `Reviewed`, `Ready`, or `Closed`), render a compact cost badge (`💰 @formattedCost` with `@costTooltip` title).

### 4.3 `Ideation.razor` & `IdeationSessionState.cs`
- On `IdeationSessionState` (`src/NightCodeForge.Web/Services/IdeationSessionState.cs`), maintain `TaskCostSummary ActiveSessionCost { get; set; } = TaskCostSummary.Empty`, resetting it to `TaskCostSummary.Empty` in `Reset()`.
- In `Ideation.razor`:
  - When loading a conversation (`LoadConversationIntoSession(Conversation conv)`), set `Session.ActiveSessionCost = TaskCostPresentationHelper.ComputeConversationCost(conv)`.
  - When a turn finishes (`IdeationStreamUpdateKind.Completed` / turn persistence completion), refresh `Session.ActiveSessionCost` from the updated `Conversation` (or returned `IdeationResult` cost summary).
  - In `.ideation-header-status`, when `Session.ActiveSessionCost.HasCostData` is `true`, display the session cost badge with its breakdown tooltip (`Cloud`, `Electricity`, `Cloud Savings`).

---

## 5. Live Telemetry & Streaming

1. **`AgentActivityMetadataKeys` (`src/NightCodeForge.Core/Streaming/AgentActivityModels.cs`):**
   Add metadata key constants:
   - `public const string Cost = "cost";`
   - `public const string Currency = "currency";`
   - `public const string ElectricityCost = "electricityCost";`
   - `public const string ActualCloudCost = "actualCloudCost";`
   - `public const string CloudSavings = "cloudSavings";`
   - `public const string CachedInputTokens = "cachedInputTokens";`
2. **`CrucibleAgentHarness.cs` & `IdeationService.cs`:**
   - When emitting `AgentActivityKind.StageCompleted`, populate the activity metadata dictionary from `conversationState.LastUsageDelta` / `CurrentMetrics()` (when `Cost.HasValue` is `true`):
     - `[AgentActivityMetadataKeys.Cost] = metrics.Cost`
     - `[AgentActivityMetadataKeys.Currency] = metrics.Currency`
     - `[AgentActivityMetadataKeys.ElectricityCost] = metrics.ElectricityCost`
     - `[AgentActivityMetadataKeys.ActualCloudCost] = metrics.ActualCloudCost`
     - `[AgentActivityMetadataKeys.CloudSavings] = metrics.CloudSavings`
     - `[AgentActivityMetadataKeys.CachedInputTokens] = metrics.CachedInputTokens`
   - Ensure live listeners (such as signal streamers or browser sessions) receive immediate cost updates upon stage or turn conclusion.

---

## 6. Verification & Acceptance Criteria

### 6.1 Unit & Component Tests
1. **`TaskCostPresentationHelperTests`:**
   - Test empty records list returns `HasCostData: false`.
   - Test roll-up math across multiple stage records (including retried attempts).
   - Test formatting of small fractions (`< $0.001`), null currency, and breakdown tooltip strings.
2. **`TaskDetailCostDisplayTests` (bUnit or presentation test):**
   - Verify task cost badge appears when cost data exists and is omitted when unpriced.
   - Verify timeline per-stage cost pill renders correctly.
3. **`KanbanCardCostTests`:**
   - Verify Kanban card cost pill rendering.
4. **`LiveCostTelemetryTests`:**
   - Verify `AgentActivityEvent` emitted on stage completion contains populated cost metadata.

