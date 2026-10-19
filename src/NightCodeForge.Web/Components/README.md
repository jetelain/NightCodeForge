# NightCodeForge.Web.Components

> **Assembly:** `NightCodeForge.Web` | **Layer:** UI / Razor Components  
> **Role:** Houses reusable Blazor components organized by functional domain (DevQueue, Review, Monitoring, Workspaces).

---

## 1. Core Responsibility

`NightCodeForge.Web.Components` contains the modular Razor UI components that form the NightCodeForge frontend. Components are grouped by functional domain to isolate concerns: task tracking, code review, live agent execution telemetry, human interruption prompts, and workspace administration.

## 2. Key Types & Subdomains

| Domain | Key Components | Description |
| :--- | :--- | :--- |
| `DevQueue` | `DevQueueBoard`, `TaskCard`, `CreateTaskModal` | Real-time Kanban board displaying tasks across execution states with drag-and-drop and action controls. |
| `Review` | `TaskReviewView`, `DiffViewer`, `ReviewCommentList` | Split-view git diff inspector with inline comments and change-request loopback triggers. |
| `Monitoring` | `LiveActivityFeed`, `ThinkingTraceCard`, `StageTimeline` | Streaming console displaying real-time `<think>` deltas, tool executions, and duration counters. |
| `Inbox` | `HumanQuestionModal`, `QuestionCard` | Prompt dialog allowing developers to answer agent `ask` interruptions and resume paused tasks. |
| `Workspaces` | `WorkspaceSelector`, `ProvisionWorkspaceModal` | Manages active workspace switching, Git clone discovery, and environment configuration. |
| `Ideation` | `IdeationChatModal`, `TaskProposalCard` | Interactive dialogue interface for exploring architecture and drafting backlog tasks. |
| `Settings` | `ModelSettingsForm`, `SearchSettingsForm` | Configuration forms for local Ollama endpoints, model selection, and prompt caching. |

## 3. Dependencies & Interaction Graph

- **Inbound (Who uses this):** `App.razor`, `Routes.razor`, and root page routes (`Pages/`).
- **Outbound (What this relies on):** `NightCodeForge.Web.Services`, `NightCodeForge.Core.Domain`, SignalR Hub connections.

## 4. Invariants & Architectural Rules

- **Rule 1 (Circuit Safe Disposal):** Components subscribing to events (SignalR, `WorkspaceStateService`) must implement `IDisposable` or `IAsyncDisposable` and unsubscribe to prevent memory leaks.
- **Rule 2 (UI Responsiveness):** Heavy calculations or blocking I/O must never run on the Blazor UI thread; invoke async services and call `InvokeAsync(StateHasChanged)`.
- **Rule 3 (Component Isolation):** Components must communicate through cascading parameters, events, or injected scoped services rather than direct parent-child references.

## 5. Usage Example

```razor
@* Embedding the live telemetry feed for an active task *@
<LiveActivityFeed TaskId="@selectedTaskId" IsActive="@isExecuting" />
```
