# NightCodeForge.Agents.Dispatcher

> **Assembly:** `NightCodeForge.Agents` | **Layer:** Agents / Tool Dispatching  
> **Role:** Provides lenient tool call dispatching, parameter casing normalization, and JSON argument repair.

---

## 1. Core Responsibility

`NightCodeForge.Agents.Dispatcher` bridges the gap between imperfect local SLM outputs and strict C# method signatures. It normalizes argument naming differences (camelCase, snake_case, PascalCase), extracts JSON embedded within markdown or string blocks, repairs truncated JSON payloads, and dispatches corrected arguments to the underlying tool handlers.

## 2. Key Types & Contracts

| Type | Kind | Description |
| :--- | :--- | :--- |
| `LenientToolDispatcher` | Class | Parses, repairs, and dispatches tool call requests against registered tool definitions. |
| `ToolDefinition` | Class | Metadata model defining expected tool names, required parameters, and accepted aliases. |

## 3. Dependencies & Interaction Graph

- **Inbound (Who uses this):** `LenientToolCallChatClient`, `CrucibleAgentHarness`.
- **Outbound (What this relies on):** `System.Text.Json`, `NightCodeForge.Agents.Tools`.

## 4. Invariants & Architectural Rules

- **Rule 1 (Zero Failure on Case Variations):** Parameter casing discrepancies (e.g. `path` vs `filePath` vs `file_path`) must be resolved transparently according to alias rules.
- **Rule 2 (JSON Sanitization):** Leading/trailing markdown fences (````json ... ````) must be stripped before JSON deserialization.
- **Rule 3 (Explicit Unrepairable Errors):** If a tool call cannot be repaired, return a structured, helpful diagnostic message to the agent rather than crashing the harness.

## 5. Usage Example

```csharp
// Dispatching a lenient tool call from raw LLM output
var result = await dispatcher.DispatchAsync(
    toolName: "read",
    rawArgumentsJson: "{\"filePath\": \"src/Program.cs\", \"start_line\": 1, \"end_line\": 50}",
    cancellationToken);
```
