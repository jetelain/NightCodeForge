# NightCodeForge.Agents

> **Assembly:** `NightCodeForge.Agents` | **Layer:** Agents / AI Orchestration  
> **Role:** Implements pluggable agent harnesses, canonical developer tools, inference pipelines, and local LLM optimizations.

---

## 1. Core Responsibility

`NightCodeForge.Agents` provides the AI runtime execution engines for autonomous software engineering. It implements multiple agent harnesses (Crucible with Microsoft Agent Framework, ACP, PiRpc), provides canonical filesystem and terminal tools (`read`, `edit`, `write`, `bash`, `ask`), handles lenient JSON tool-call repair, streams real-time thinking and tool execution activity, and optimizes local LLM inference (prompt caching, guided grammar decoding).

## 2. Key Types & Contracts

| Type | Kind | Description |
| :--- | :--- | :--- |
| `CrucibleAgentHarness` | Class | Flagship agent harness leveraging Microsoft Agent Framework and local Ollama models. |
| `CanonicalTools` | Class | Implementation of canonical agent tools (`read`, `edit`, `write`, `bash`, `ask`, `propose_tasks`). |
| `LenientToolDispatcher` | Class | Normalizes tool arguments, repairs malformed JSON, and forgives parameter casing mismatches. |
| `OllamaChatClient` | Class | High-throughput streaming chat client communicating directly with Ollama endpoints. |
| `SessionTranscriptService` | Class | Records full agent turn histories and model call snapshots to disk in JSONL format. |

## 3. Dependencies & Interaction Graph

- **Inbound (Who uses this):** `NightCodeForge.Core.Workflow.WorkflowPipelineEngine`, `NightCodeForge.ScenarioRunner`.
- **Outbound (What this relies on):** `NightCodeForge.Core`, `Microsoft.Extensions.AI`, `System.Text.Json`, `Scriban`.

## 4. Invariants & Architectural Rules

- **Rule 1 (Lenient Tool Ingestion):** Agents with small parameter variances (casing, stringified JSON) must be forgiven and repaired by dispatchers rather than failing immediately.
- **Rule 2 (Prompt Cache Preservation):** Static prefixes (system prompts, tool definitions, repo rules) must remain invariant across turns to maximize Ollama prompt caching.
- **Rule 3 (Bounded Tool Output):** Commands executing via `bash` or reading large files must truncate output to prevent context window saturation.

## 5. Usage Example

```csharp
// Executing an agent harness step
var result = await agentHarness.ExecuteTurnAsync(
    sessionContext,
    prompt: "Implement the required unit tests for UserService.",
    cancellationToken);
```
