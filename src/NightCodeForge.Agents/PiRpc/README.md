# NightCodeForge.Agents.PiRpc

> **Assembly:** `NightCodeForge.Agents` | **Layer:** Agents / Protocol Harnesses  
> **Role:** Implements the Pi coding-agent daemon bridge communicating over line-delimited JSON (JSONL).

---

## 1. Core Responsibility

`NightCodeForge.Agents.PiRpc` provides an adapter for headless Pi coding-agent runtimes. It spawns the agent daemon process, establishes bidirectional communication over standard I/O using JSONL RPC messages, streams activity tokens in real-time, and captures complete execution traces.

## 2. Key Types & Contracts

| Type | Kind | Description |
| :--- | :--- | :--- |
| `PiRpcAgentHarness` | Class | `IAgentHarness` implementation managing the Pi JSONL RPC process lifecycle and session turns. |
| `PiRpcHarnessOptions` | Class | Configuration options specifying the Node runtime entrypoint, script path, and daemon parameters. |

## 3. Dependencies & Interaction Graph

- **Inbound (Who uses this):** `HarnessRegistry`, `WorkflowStageFactory`.
- **Outbound (What this relies on):** `NightCodeForge.Core.Interfaces.IAgentHarness`, `NightCodeForge.Core.Streaming`, `NightCodeForge.Agents.Transcripts`.

## 4. Invariants & Architectural Rules

- **Rule 1 (Stream Demuxing):** Incoming stdout JSONL messages must be demuxed into activity events, tool calls, and completion notifications without line truncation.
- **Rule 2 (Graceful Shutdown):** When session completes or cancels, send a graceful shutdown RPC command before forcibly terminating the daemon process.
- **Rule 3 (Environment Variable Sanitization):** Inference endpoints and tokens passed to the Pi process must be passed via secure environment blocks.

## 5. Usage Example

```csharp
// Running an automated stage via the Pi RPC harness
var result = await piRpcHarness.ExecuteTurnAsync(
    sessionContext,
    prompt: "Optimize database queries in Repositories/TaskRepository.cs",
    cancellationToken);
```
