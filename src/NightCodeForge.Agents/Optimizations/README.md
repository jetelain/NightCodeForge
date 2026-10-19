# NightCodeForge.Agents.Optimizations

> **Assembly:** `NightCodeForge.Agents` | **Layer:** Agents / Local Inference Optimizations  
> **Role:** Implements local LLM inference speedups, prompt cache stability, context compaction, and loop circuit breakers.

---

## 1. Core Responsibility

`NightCodeForge.Agents.Optimizations` maximizes performance, token efficiency, and reliability when executing workflows against local Small Language Models (SLMs). It stabilizes prompt prefixes to preserve Ollama KV caches, prunes past `<think>` reasoning blocks across multi-turn sessions, breaks recursive tool call loops, compacts voluminous tool outputs, and extracts structured Roslyn/build diagnostics.

## 2. Key Types & Contracts

| Type | Kind | Description |
| :--- | :--- | :--- |
| `PromptPrefixStabilizer` | Class | Enforces strict byte-identical prompt prefixes (system prompts, tool specs) across turns for KV cache hits. |
| `ThinkingTraceCompactionChatClient` | Class | Chat client middleware compacting or stripping `<think>` tags from prior turns to conserve context budget. |
| `ToolLoopCircuitBreaker` | Class | Intercepts repetitive tool execution loops (same tool and arguments) to prevent infinite token consumption. |
| `ToolResultCompactor` | Class | Truncates and summarizes oversized tool outputs before injecting into conversation history. |
| `RoslynSymbolIndexProvider` | Class | Lightweight C# symbol indexing using Roslyn for fast workspace code navigation. |
| `StructuredDiagnosticExtractor` | Class | Parses MSBuild and compiler error logs into structured diagnostic objects. |

## 3. Dependencies & Interaction Graph

- **Inbound (Who uses this):** `CrucibleAgentHarness`, `Inference.IOllamaChatClientFactory`.
- **Outbound (What this relies on):** `Microsoft.Extensions.AI`, `Microsoft.CodeAnalysis.CSharp`, `NightCodeForge.Core.Domain`.

## 4. Invariants & Architectural Rules

- **Rule 1 (Cache Line Invariance):** System prompts and tool declarations must not inject dynamic timestamps or non-deterministic elements at the prefix to preserve prompt caching.
- **Rule 2 (Loop Breaker Threshold):** Repeating an identical failed tool invocation 3 times must trip the circuit breaker and inject an intervention instruction.
- **Rule 3 (Lossless Diagnostic Extraction):** Compaction of build logs must never strip compiler error codes (e.g. `CS0103`) or target file paths.

## 5. Usage Example

```csharp
// Attaching the circuit breaker and thinking compactor to the chat client pipeline
var optimizedClient = new ThinkingTraceCompactionChatClient(
    new ToolLoopCircuitBreakerChatClient(baseClient, maxRepetitions: 3),
    compactionStrategy: CompactionStrategy.PrunePriorTurns);
```
