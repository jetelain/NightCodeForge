# NightCodeForge.Agents.Transcripts

> **Assembly:** `NightCodeForge.Agents` | **Layer:** Agents / Audit Transcripts  
> **Role:** Records full agent conversation sessions, tool invocations, and model snapshots as JSONL files.

---

## 1. Core Responsibility

`NightCodeForge.Agents.Transcripts` provides durable, on-disk recording of agent execution sessions. It writes structured JSONL logs containing full prompt text, system messages, model tool calls, and output snapshots to facilitate post-mortem debugging, benchmarking, and quality analysis without polluting the database.

## 2. Key Types & Contracts

| Type | Kind | Description |
| :--- | :--- | :--- |
| `SessionTranscriptService` | Class | Manages append-only JSONL transcript files per task and stage under `.nightcodeforge/transcripts/`. |
| `ModelCallSnapshot` | Record | Detailed record of an individual LLM request/response cycle, token metrics, and latency. |
| `TranscriptDiagnosticService` | Class | Analyzes completed transcripts for tool failure rates, repetition loops, and token usage anomalies. |
| `TranscriptStreamBuffer` | Class | Asynchronous buffer collecting transcript entries before committing to disk. |

## 3. Dependencies & Interaction Graph

- **Inbound (Who uses this):** `CrucibleAgentHarness`.
- **Outbound (What this relies on):** `System.IO`, `System.Text.Json`.

## 4. Invariants & Architectural Rules

- **Rule 1 (Append-Only Safety):** Transcript records must be flushed incrementally to survive abnormal process termination.
- **Rule 2 (Audit Fidelity):** Payloads must represent raw prompts and completions exactly as transmitted over the wire.
- **Rule 3 (Safe Disk Storage):** Transcript paths must be isolated per workspace and task to prevent file locking collisions.

## 5. Usage Example

```csharp
// Recording a completed model interaction snapshot
await transcriptService.RecordModelCallAsync(new ModelCallSnapshot
{
    TaskId = taskId,
    StageId = "impl",
    TurnIndex = turnNumber,
    Prompt = prompt,
    Response = fullResponseText,
    DurationMs = elapsedMs
}, cancellationToken);
```
