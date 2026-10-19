# NightCodeForge.Agents.Acp

> **Assembly:** `NightCodeForge.Agents` | **Layer:** Agents / Protocol Harnesses  
> **Role:** Implements the Agent Client Protocol (ACP) adapter for external coding agents.

---

## 1. Core Responsibility

`NightCodeForge.Agents.Acp` integrates external coding agents adhering to the Agent Client Protocol (ACP). It supports protocol negotiation (ACP v2 with v1 fallback), spawns agent CLI processes (e.g. Gemini ACP), proxies filesystem and terminal operations into the task's execution environment, and streams activity to the orchestrator.

## 2. Key Types & Contracts

| Type | Kind | Description |
| :--- | :--- | :--- |
| `AcpAgentHarness` | Class | `IAgentHarness` implementation managing external ACP agent subprocesses and JSON-RPC message loops. |
| `AcpHarnessOptions` | Class | Configuration options specifying agent binary path, arguments, and process factory. |
| `AcpProtocolModels` | Class | Data models representing ACP JSON-RPC requests, notifications, tool invocations, and session states. |

## 3. Dependencies & Interaction Graph

- **Inbound (Who uses this):** `HarnessRegistry`, `WorkflowStageFactory`.
- **Outbound (What this relies on):** `NightCodeForge.Core.Interfaces.IAgentHarness`, `NightCodeForge.Core.Execution.IExecutionEnvironmentProvider`, `NightCodeForge.Agents.Transcripts`.

## 4. Invariants & Architectural Rules

- **Rule 1 (Sandboxed Redirection):** All tool calls initiated by the external ACP agent (e.g. file edits, bash commands) must be intercepted and routed through the task worktree sandbox.
- **Rule 2 (Protocol Version Negotiation):** Connection handshakes must negotiate the highest supported protocol version (v2) and fall back gracefully to v1.
- **Rule 3 (Process Lifecycle Cleanup):** Subprocesses must be killed cleanly on cancellation or session completion to prevent orphaned CLI processes.

## 5. Usage Example

```csharp
// Executing a stage using the ACP agent harness
var result = await acpHarness.ExecuteTurnAsync(
    sessionContext,
    prompt: "Refactor database repositories to use EF Core compiled queries",
    cancellationToken);
```
