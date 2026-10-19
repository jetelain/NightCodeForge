# NightCodeForge.Core.Streaming

> **Assembly:** `NightCodeForge.Core` | **Layer:** Core / Real-time Telemetry  
> **Role:** Defines streaming activity events, telemetry data models, and event distribution contracts for real-time observability.

---

## 1. Core Responsibility

`NightCodeForge.Core.Streaming` provides the core contracts and data models for publishing real-time telemetry from running agents and workflows. As LLMs generate tokens, execute tools, or evaluate quality gates, streaming events are broadcast to decouple agent runtime engines from transport protocols (like SignalR websockets or console writers).

## 2. Key Types & Contracts

| Type | Kind | Description |
| :--- | :--- | :--- |
| `AgentActivityEvent` | Class / Record | Data envelope capturing an activity timestamp, task ID, stage ID, event type, and payload. |
| `AgentActivityType` | Enum | Categorizes the event: `Thought`, `ToolCall`, `ToolResult`, `StageStarted`, `StageCompleted`, `Error`. |
| `InMemoryAgentActivityStreamer` | Class | Thread-safe in-memory event bus broadcasting agent events to registered asynchronous subscribers. |

## 3. Dependencies & Interaction Graph

- **Inbound (Who uses this):** `IAgentHarness`, `NightCodeForge.Agents.Crucible`, `NightCodeForge.Infrastructure.Hubs` (`SignalRActivityBridgeService`).
- **Outbound (What this relies on):** Standard asynchronous LINQ and concurrency primitives (`System.Threading.Channels`).

## 4. Invariants & Architectural Rules

- **Rule 1 (Non-Blocking Dispatch):** Event publishing must not block LLM token generation or tool execution loops. Slow consumers must not throttle the agent.
- **Rule 2 (Chronological Ordering):** Events for a given task execution must preserve their causal and sequential order.
- **Rule 3 (Payload Scrubbing):** Sensitive tokens (passwords, private keys, API secrets) should be scrubbed before dispatching to open event streams.

## 5. Usage Example

```csharp
// Publishing a tool invocation event
await activityStreamer.PublishAsync(new AgentActivityEvent
{
    TaskId = taskId,
    StageId = "impl",
    Type = AgentActivityType.ToolCall,
    Content = "edit src/MyFile.cs",
    Timestamp = DateTimeOffset.UtcNow
}, cancellationToken);
```
