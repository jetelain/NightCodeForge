# NightCodeForge.Web

> **Assembly:** `NightCodeForge.Web` | **Layer:** UI / Presentation  
> **Role:** Interactive Blazor Web App serving real-time developer interfaces, task queues, and agent telemetry.

---

## 1. Core Responsibility

`NightCodeForge.Web` provides the graphical frontend for NightCodeForge. Built on Blazor Interactive Server, it delivers real-time Kanban task management (`DevQueue`), code diff review workflows, streaming agent thinking displays, human-in-the-loop interruption response modals, and administrative configuration panels without page reloads.

## 2. Key Types & Contracts

| Type | Kind | Description |
| :--- | :--- | :--- |
| `Program` | Entrypoint | ASP.NET Core host configuration, service registration, EF Core migration triggering, and route mapping. |
| `App.razor` | Component | Root HTML page shell with CSS stylesheets, scripts, and Blazor server runtime. |
| `Routes.razor` | Component | Router coordinating navigation, main layouts, and 404 handlers. |
| `OrchestratorHubClientConnection` | Class | Scoped client managing WebSockets and SignalR hub subscriptions to task rooms. |
| `WorkspaceStateService` | Class | Manages active workspace selection, persistent cookies/state, and workspace change events. |

## 3. Dependencies & Interaction Graph

- **Inbound (Who uses this):** Web browsers connecting over HTTP/HTTPS and WebSockets.
- **Outbound (What this relies on):** `NightCodeForge.Core`, `NightCodeForge.Infrastructure`, `NightCodeForge.Agents`, `Microsoft.AspNetCore.Components.Web`.

## 4. Invariants & Architectural Rules

- **Rule 1 (Interactive Server Scope):** UI state is maintained per user session in Blazor circuit scopes; scoped services must handle circuit disposal cleanly.
- **Rule 2 (Real-Time Push Over Polling):** Live agent progress must be streamed via SignalR rather than HTTP polling intervals.
- **Rule 3 (Safe Markdown Rendering):** All user and agent markdown outputs must be sanitized before rendering into the DOM to prevent XSS.

## 5. Usage Example

```powershell
# Running the Web application locally
dotnet run --project src/NightCodeForge.Web
# Listens on http://localhost:5000 (or configured launchSettings port)
```
