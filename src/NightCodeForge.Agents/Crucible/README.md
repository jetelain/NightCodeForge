# NightCodeForge.Agents.Crucible

> **Assembly:** `NightCodeForge.Agents` | **Layer:** Agents / Harness Implementation  
> **Role:** Implements the primary Microsoft Agent Framework execution harness for autonomous software engineering.

---

## 1. Core Responsibility

`NightCodeForge.Agents.Crucible` hosts `CrucibleAgentHarness`, the core agent loop powering NightCodeForge's autonomous workflow stages. It orchestrates agent sessions by binding system prompts, repo rules, and tool functions into an iterative reasoning loop, while streaming activity events (`Thought`, `ToolCall`, `ToolResult`) to subscribers.

## 2. Key Types & Contracts

| Type | Kind | Description |
| :--- | :--- | :--- |
| `CrucibleAgentHarness` | Class | Primary implementation of `IAgentHarness` managing agent turns, tool invocations, and session completion. |
| `CrucibleHarnessConfig` | Class | Strongly-typed options defining default timeout, maximum turn count, and model parameters. |

## 3. Dependencies & Interaction Graph

- **Inbound (Who uses this):** `WorkflowStageFactory`, `HarnessRegistry`.
- **Outbound (What this relies on):** `NightCodeForge.Core.Interfaces.IAgentHarness`, `NightCodeForge.Agents.Inference`, `NightCodeForge.Agents.Tools`, `Microsoft.Extensions.AI`.

## 4. Invariants & Architectural Rules

- **Rule 1 (Turn Bound Enforcement):** Agent loops must enforce a maximum turn limit (`MaxTurns`) to prevent runaway execution loops.
- **Rule 2 (Streaming Observability):** Every thought chunk and tool call must be streamed immediately to `IAgentActivityStreamer`.
- **Rule 3 (Graceful Interruption):** When an agent invokes the `ask` tool, the harness must halt execution and throw `HumanInterruptionException` without losing context.

## 5. Usage Example

```csharp
// Executing a stage turn via Crucible
var harness = new CrucibleAgentHarness(chatClientFactory, toolFactory, streamer, logger);
var result = await harness.ExecuteTurnAsync(sessionContext, "Fix failing build tests", cancellationToken);
```
