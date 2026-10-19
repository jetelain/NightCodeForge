# NightCodeForge.Agents.Streaming

> **Assembly:** `NightCodeForge.Agents` | **Layer:** Agents / Streaming Telemetry  
> **Role:** Projects raw LLM completion chunks into typed domain activity events.

---

## 1. Core Responsibility

`NightCodeForge.Agents.Streaming` transforms asynchronous streaming chunks from language model completions into structured domain events. It aggregates raw token deltas, extracts thinking boundaries, detects incoming tool calls, and projects them into typed `AgentActivityEvent` items for downstream UI consumption.

## 2. Key Types & Contracts

| Type | Kind | Description |
| :--- | :--- | :--- |
| `AgentStreamProjector` | Class | Projects `StreamingChatCompletionUpdate` items into structured `AgentActivityEvent` objects. |

## 3. Dependencies & Interaction Graph

- **Inbound (Who uses this):** `CrucibleAgentHarness`, `ThinkingAndActivityTracingChatClient`.
- **Outbound (What this relies on):** `Microsoft.Extensions.AI`, `NightCodeForge.Core.Streaming.AgentActivityEvent`.

## 4. Invariants & Architectural Rules

- **Rule 1 (Zero Token Loss):** Projections must preserve the exact text of streaming updates without dropping deltas during high-throughput generation.
- **Rule 2 (Non-Throwing Pipeline):** Formatting or projection anomalies must be logged and handled gracefully to prevent terminating active LLM streams.
- **Rule 3 (Order Preservation):** Chunks must be emitted in the exact sequence received from the inference stream.

## 5. Usage Example

```csharp
// Projecting a completion chunk into an activity event
await foreach (var update in chatClient.CompleteStreamingAsync(messages, options, ct))
{
    await projector.ProjectChunkAsync(taskId, stageId, update, ct);
}
```
