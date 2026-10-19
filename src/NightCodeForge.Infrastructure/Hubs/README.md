# NightCodeForge.Infrastructure.Hubs

> **Assembly:** `NightCodeForge.Infrastructure` | **Layer:** Infrastructure / Real-Time Communication  
> **Role:** Hosts the SignalR `OrchestratorHub` and bridges domain telemetry events to connected web clients.

---

## 1. Core Responsibility

`NightCodeForge.Infrastructure.Hubs` establishes WebSocket communication between the backend workflow engine and the web frontend via ASP.NET Core SignalR. It enables clients to join task-specific real-time rooms (`JoinTaskGroup`) and broadcasts live LLM token streams, tool invocation events, stage transitions, and execution metrics without polling.

## 2. Key Types & Contracts

| Type | Kind | Description |
| :--- | :--- | :--- |
| `OrchestratorHub` | Class | SignalR hub managing client connections, group memberships per task, and message routing. |
| `IOrchestratorHubClient` | Interface | Strongly-typed client contract defining callbacks (`ReceiveActivity`, `ReceiveStageUpdated`, etc.). |
| `SignalRActivityBridgeService` | Class | Background service listening to `InMemoryAgentActivityStreamer` and dispatching events to SignalR clients. |

## 3. Dependencies & Interaction Graph

- **Inbound (Who uses this):** Web UI components (`DevQueue`, `Review`, `Monitoring`), Blazor SignalR client connections.
- **Outbound (What this relies on):** `NightCodeForge.Core.Streaming`, `Microsoft.AspNetCore.SignalR`.

## 4. Invariants & Architectural Rules

- **Rule 1 (Room Isolation):** Telemetry events for a specific task must only be dispatched to the SignalR group corresponding to that task (`task-{id}`) to prevent cross-talk.
- **Rule 2 (Reconnection Tolerance):** Web clients must be able to disconnect and reconnect seamlessly without corrupting hub state.
- **Rule 3 (Fire-and-Forget Safety):** Client network dropouts must not throw unhandled exceptions or disrupt running backend agent pipelines.

## 5. Usage Example

```csharp
// Broadcasting a stage update event from the bridge service
await hubContext.Clients
    .Group($"task-{taskId}")
    .ReceiveStageUpdated(taskId, stageId, StageStatus.InProgress);
```
