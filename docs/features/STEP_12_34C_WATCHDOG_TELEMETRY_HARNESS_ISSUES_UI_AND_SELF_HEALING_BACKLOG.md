# 📊 Step 12.34C: Watchdog Telemetry UI, Harness Issue Ledger (`/diagnostics/watchdog`) & Self-Improvement Task Promotion

> **Feature & Developer Ergonomics Specification (Part 3 of 3 — Web UI Dashboard, Settings, Timeline Badges & Self-Healing Backlog Loop)**  
> *Parent Plan:* [Features & Integrations Roadmap](README.md)  
> *Prerequisite Steps:* [Step 12.34A: Autonomous Forge Watchdog — Harness Issue Ledger, Tiered Triage Engine & Orchestrator Failure Recovery](STEP_12_34A_WATCHDOG_DOMAIN_LEDGER_AND_ORCHESTRATOR_REACTIVE_TRIAGE.md) | [Step 12.34B: Agent-Invocable Watchdog SOS Tool (`report_environment_issue`) & In-Flight Remediation](STEP_12_34B_AGENT_WATCHDOG_SOS_TOOL_AND_ENVIRONMENT_REMEDIATION.md)  
> *Related Steps:* [Step 12.13B: "Refine in Ideation" Workflow & Mutation UI](../archive/phase-12/STEP_12_13B_REFINE_IN_IDEATION_WORKFLOW_AND_MUTATION_UI.md) | [Step 12.22B: Interactive Container Diagnostic Bash Console Web UI (`/diagnostics/shell`)](STEP_12_22B_DIAGNOSTIC_BASH_CONSOLE_WEB_UI.md)  
> *Source Policy:* [AGENTS.md](../../AGENTS.md) | [docs/ARCHITECTURE.md](../ARCHITECTURE.md)  
> *Target Projects:* `src/NightCodeForge.Core`, `src/NightCodeForge.Infrastructure`, `src/NightCodeForge.Web`, `tests/NightCodeForge.Web.Tests`, `tests/NightCodeForge.Acceptance.Tests` | *Status:* Planned | *Target Framework:* .NET 10 (C# 13)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Objective

Once the Forge Watchdog ([Step 12.34A](STEP_12_34A_WATCHDOG_DOMAIN_LEDGER_AND_ORCHESTRATOR_REACTIVE_TRIAGE.md)) and the Agent SOS tool ([Step 12.34B](STEP_12_34B_AGENT_WATCHDOG_SOS_TOOL_AND_ENVIRONMENT_REMEDIATION.md)) are collecting structured `HarnessIssue` and `WatchdogInterventionRecord` telemetry, maintainers need:
1. **Transparency on Autonomous Interventions:** Clear visual indicators on `TaskDetail.razor`, `TaskDetailDrawer.razor`, `TaskExecutionInspector.razor`, and `/monitoring` whenever the Watchdog restarts a stage, resets a pipeline, recreates a container, or purges locks—so autonomous administrative actions are never silent.
2. **A Dedicated Harness Issue Ledger & Triage UI (`/diagnostics/watchdog`):** A central dashboard aggregating recurring harness, container, parser, and provider issues ranked by frequency and severity, showing root-cause analysis and auto-remediation success rates.
3. **Closing the Self-Improvement Loop (1-Click Task Creation):** The ability to turn any recurring `HarnessIssue` into a structured `ForgeTask` (or open it in Ideation Chat) with one click so NightCodeForge can be used to fix its own harness bugs.
4. **Watchdog Governance Settings (`WatchdogSettingsTab.razor`):** UI controls in `/settings` to tune retry budgets, toggle Tier 1 / Tier 2 triage, and select the Watchdog LLM model/routing target.

---

## 2. UI & Service Architecture

```mermaid
flowchart LR
    subgraph WebUI ["Blazor Presentation Layer (NightCodeForge.Web)"]
        Dashboard["WatchdogDashboard.razor<br/>(/diagnostics/watchdog)<br/>- KPI Summary Cards<br/>- Filterable Harness Issues Table<br/>- Issue Detail &amp; Intervention Drawer<br/>- 1-Click 'Promote to Task'"]
        TaskViews["TaskDetail.razor / TaskDetailDrawer.razor /<br/>TaskExecutionInspector.razor<br/>- 🐕 Watchdog Intervention Badges<br/>- Recovery Hint &amp; Linked HarnessIssue Banner"]
        SettingsTab["WatchdogSettingsTab.razor<br/>(/settings)<br/>- Retry Budgets &amp; Toggles<br/>- Model &amp; Routing Target Selector"]
    end

    subgraph PresentationServices ["Presentation &amp; Application Services (AGENTS.md Compliant)"]
        Helper["WatchdogPresentationHelper<br/>- Category/Severity/Action badge styling<br/>- Markdown task description builder for HarnessIssue promotion"]
        DashService["IWatchdogDashboardService<br/>- KPI roll-ups &amp; filtered ledger queries<br/>- Issue status transitions (Resolve, Ignore)<br/>- PromoteIssueToTaskAsync()"]
    end

    subgraph Data ["Persistence &amp; Orchestration"]
        Repo["IWatchdogRepository / ITaskRepository<br/>(HarnessIssues &amp; WatchdogInterventions)"]
    end

    Dashboard --> DashService
    Dashboard --> Helper
    TaskViews --> DashService
    TaskViews --> Helper
    SettingsTab --> DashService
    DashService --> Repo
```

---

## 3. Detailed Requirements & Specifications

### 3.1 Presentation Separation & Service Contracts (`AGENTS.md` Compliance)

1. **`WatchdogPresentationHelper` (`NightCodeForge.Web/Services` or `NightCodeForge.Core`):**
   - Pure, stateless helper class (unit-testable without UI rendering context) providing:
     - Human-readable labels, icons, and CSS badge classes for `HarnessIssueCategory`, `HarnessIssueSeverity`, `HarnessIssueStatus`, `WatchdogAction`, and `WatchdogTriggerSource`.
     - `BuildTaskProposalFromHarnessIssue(HarnessIssue issue, IReadOnlyList<WatchdogInterventionRecord> recentInterventions)`: Generates a structured markdown task title and description (Problem Statement, Component, Root Cause Analysis, Diagnostic Snippet, Occurrence Statistics, and Suggested Harness Fix) ready to be saved as a `ForgeTask`.
2. **`IWatchdogDashboardService`:**
   - Provides UI-ready snapshots and mutations:
     - `GetDashboardSnapshotAsync(int? workspaceId = null, HarnessIssueStatus? statusFilter = null, HarnessIssueCategory? categoryFilter = null, CancellationToken ct = default)` returning:
       - `WatchdogKpiSummary`: `TotalInterventionsCount`, `AutoRecoveredStagesCount`, `AutoRestartedPipelinesCount`, `OpenHarnessIssuesCount`, `CriticalOrHighIssuesCount`, `SelfHealingSuccessRatePercent`.
       - `IReadOnlyList<HarnessIssueSummaryItem>`: Ordered by `Status == Open` first, then `Severity DESC`, `OccurrenceCount DESC`, `LastSeenAt DESC`.
       - `IReadOnlyList<WatchdogInterventionSummaryItem>`: Recent Watchdog actions across tasks with links to `/tasks/{taskId}` and `/inspector/{taskId}`.
     - `GetIssueDetailAsync(int harnessIssueId, CancellationToken ct = default)`: Returns the `HarnessIssue` and its linked `WatchdogInterventionRecord` history.
     - `UpdateIssueStatusAsync(int harnessIssueId, HarnessIssueStatus newStatus, CancellationToken ct = default)`: Marks an issue as `Resolved`, `Ignored`, or re-opens it to `Open`.
     - `PromoteIssueToTaskAsync(int harnessIssueId, int targetWorkspaceId, TaskOverallStatus initialStatus = TaskOverallStatus.Suggested, int priority = TaskPriority.Normal, CancellationToken ct = default)`: Creates a `ForgeTask` using `WatchdogPresentationHelper.BuildTaskProposalFromHarnessIssue`, links `HarnessIssue.PromotedForgeTaskId = createdTask.Id`, sets `HarnessIssue.Status = HarnessIssueStatus.PromotedToTask`, syncs the task markdown to disk, and returns the created `ForgeTask.Id`.
     - `ManualInvokeWatchdogOnTaskAsync(int taskId, CancellationToken ct = default)`: Allows a maintainer to manually trigger Watchdog triage on a `Failed` task from the UI.

---

### 3.2 Watchdog & Harness Issue Ledger Page (`/diagnostics/watchdog`)

1. **Navigation & Routing:**
   - Add `WatchdogDashboard.razor` at route `/diagnostics/watchdog` (and link it from the sidebar navigation / `/monitoring` header with an open high-severity issue count badge if $> 0$).
2. **KPI Header Cards:**
   - Display 4 metric cards:
     - **🐕 Total Watchdog Interventions** (with breakdown of Tier 1 heuristic vs Tier 2 LLM triage).
     - **♻️ Auto-Recovered Runs** (count of Watchdog-restarted stages/pipelines that subsequently passed, plus Self-Healing Success Rate %).
     - **⚠️ Open Harness Issues** (with count of `High` / `Critical` severity items).
     - **🛠️ Promoted to Tasks** (count of harness issues converted into backlog tasks).
3. **Filterable Harness Issues Table & Detail Drawer:**
   - Filter bar by Workspace (`All` or specific workspace), `Status` (`Open`, `PromotedToTask`, `Resolved`, `Ignored`), `Category`, and `Severity`.
   - Table columns: `Severity`, `Category`, `Component`, `Title`, `Occurrences` (`N×`), `Auto-Healed` (`M / N`), `Last Seen` (with link to last affected `Task #ID`), `Status`, and quick actions.
   - Clicking a row opens the **Harness Issue Detail Drawer** displaying:
     - Full `RootCauseAnalysis`, `SuggestedHarnessFix`, and formatted `SampleDiagnosticSnippet`.
     - Chronological list of linked `WatchdogInterventionRecord` items (Task `#ID`, Stage, Trigger Source, Action Taken, Rationale).
     - Action buttons:
       - **➕ Promote to Backlog Task** (select target workspace and `Suggested` vs `Open`, or navigate to `/tasks/{PromotedForgeTaskId}` if already promoted).
       - **💡 Refine in Ideation** (navigates to `/w/{wsId}/ideation?taskId={PromotedForgeTaskId}` after creating a `Suggested` task).
       - **✅ Mark Resolved** / **🔇 Ignore** / **🔄 Reopen**.
4. **Recent Watchdog Interventions Feed:**
   - A secondary tab or panel on `/diagnostics/watchdog` listing recent `WatchdogInterventionRecord` events across the forge so maintainers can audit every automatic stage restart, pipeline reset, lock purge, or container recreation.

---

### 3.3 Task Detail, Inspector & Settings Integration

1. **Task Detail & Inspector Watchdog Timeline / Banner:**
   - In [`TaskDetail.razor`](../../src/NightCodeForge.Web/Components/Pages/TaskDetail.razor), [`TaskDetailDrawer.razor`](../../src/NightCodeForge.Web/Components/Shared/TaskDetailDrawer.razor), and [`TaskExecutionInspector.razor`](../../src/NightCodeForge.Web/Components/Pages/TaskExecutionInspector.razor):
     - Render a `🐕 Watchdog` badge on stages/tasks that have recorded `WatchdogInterventionRecord` entries.
     - Display a collapsible **Watchdog Interventions** section showing each intervention's timestamp, trigger (`Reactive Failure` vs `Agent SOS`), action taken, rationale, injected recovery hint, and a clickable link to the associated `HarnessIssue` in `/diagnostics/watchdog?issueId={id}`.
     - On `Failed` tasks where the Watchdog budget was exhausted (or Watchdog was disabled), provide a **🐕 Ask Watchdog to Triage & Recover** button Alongside `Retry Stage` and `Restart Pipeline`.
2. **Global Watchdog Settings Tab (`WatchdogSettingsTab.razor`):**
   - Register a **🐕 Watchdog** tab in [`Settings.razor`](../../src/NightCodeForge.Web/Components/Pages/Settings.razor) allowing maintainers to configure `WatchdogOptions`:
     - Enable/disable Autonomous Watchdog (`Enabled`).
     - Enable/disable Tier 1 Deterministic Fast-Path (`EnableDeterministicFastPath`) and Tier 2 LLM Triage (`EnableLlmTriage`).
     - Configure circuit-breaker budgets: `MaxStageRestartsPerStage` (`0..5`, default `2`) and `MaxPipelineRestartsPerTask` (`0..3`, default `1`).
     - Configure optional `ModelOverride` and `RoutingTarget` (`Auto`, `Local`, `Remote`, `Cloud`) for Tier 2 LLM triage.

---

## 4. Definition of Done (DoD) Checklist

- [ ] Implement `WatchdogPresentationHelper` and `IWatchdogDashboardService` (`WatchdogDashboardService`) keeping all formatting, aggregation, and markdown task generation out of `.razor` `@code` blocks in accordance with [AGENTS.md](../../AGENTS.md).
- [ ] Build `WatchdogDashboard.razor` (`/diagnostics/watchdog`) with KPI summary cards, filterable Harness Issues table, Issue Detail & Intervention history drawer, and 1-click **Promote to Backlog Task** / **Refine in Ideation** actions.
- [ ] Surface Watchdog intervention badges, rationale/recovery-hint banners, and deep links to `/diagnostics/watchdog` in `TaskDetail.razor`, `TaskDetailDrawer.razor`, and `TaskExecutionInspector.razor`.
- [ ] Build `WatchdogSettingsTab.razor` and register it in `Settings.razor` to manage `WatchdogOptions` persisted in `SystemSettings`.
- [ ] Author unit tests for `WatchdogPresentationHelper` and `WatchdogDashboardService`, bUnit component tests for `WatchdogDashboard.razor` and `WatchdogSettingsTab.razor` in `NightCodeForge.Web.Tests`, and an LLM-free Playwright E2E test in `NightCodeForge.Acceptance.Tests` verifying `/diagnostics/watchdog` filtering, issue status updates, and 1-click promotion of a `HarnessIssue` into a backlog `ForgeTask`.

