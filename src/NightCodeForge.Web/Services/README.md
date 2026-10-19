# NightCodeForge.Web.Services

> **Assembly:** `NightCodeForge.Web` | **Layer:** UI / Presentation Services  
> **Role:** Implements scoped presentation services, UI state coordination, SignalR subscriptions, and toast notifications.

---

## 1. Core Responsibility

`NightCodeForge.Web.Services` provides scoped services that bridge Razor components with domain logic and real-time backend events. It manages client circuit state (such as active workspace selection), encapsulates DevQueue board mutations, coordinates task diff review sessions, manages SignalR client connection lifecycles, and triggers UI toast notifications.

## 2. Key Types & Contracts

| Type | Kind | Description |
| :--- | :--- | :--- |
| `WorkspaceStateService` | Class | Scoped service maintaining the current active workspace and firing `OnWorkspaceChanged` events. |
| `DevQueueService` | Class | Manages task query filters, drag-and-drop state transitions, and enqueuing tasks from the board. |
| `TaskReviewService` | Class | Loads git diff models, submits inline review comments, and triggers approval or change requests. |
| `ToastService` | Class | Emits transient alert banners (success, warning, error) displayed globally in the layout. |
| `OrchestratorHubClientConnection` | Class | Encapsulates the `HubConnection` lifecycle, handling connection recovery and task room subscriptions. |
| `WorkspaceProvisioningService` | Class | Validates repository paths, probes branch structure, and creates new workspace database entities. |

## 3. Dependencies & Interaction Graph

- **Inbound (Who uses this):** Razor components across `DevQueue`, `Review`, `Workspaces`, and `Layout`.
- **Outbound (What this relies on):** `NightCodeForge.Core.Repositories`, `NightCodeForge.Core.Workflow`, `Microsoft.AspNetCore.SignalR.Client`.

## 4. Invariants & Architectural Rules

- **Rule 1 (Scoped Lifetime Safety):** State services tied to user interactions must be registered with `Scoped` lifetime to avoid leaking state across concurrent web users.
- **Rule 2 (Reactivity via Events):** State mutations (e.g. switching workspaces) must trigger event handlers so all listening components re-render predictably.
- **Rule 3 (Fail Fast Validation):** UI actions (e.g. creating invalid dependencies) must throw explicit validation errors and display informative toasts rather than silently discarding user input.

## 5. Usage Example

```csharp
// Subscribing to workspace changes within a Blazor component
@inject WorkspaceStateService WorkspaceState

protected override void OnInitialized()
{
    WorkspaceState.OnWorkspaceChanged += HandleWorkspaceChanged;
}

public void Dispose()
{
    WorkspaceState.OnWorkspaceChanged -= HandleWorkspaceChanged;
}
```
