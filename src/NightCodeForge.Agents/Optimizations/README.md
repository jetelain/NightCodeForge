# NightCodeForge.Agents.Optimizations

> **Assembly:** `NightCodeForge.Agents` | **Layer:** Agents / Local Inference Optimizations  
> **Role:** Implements local LLM inference speedups, prompt cache stability, context compaction, and loop circuit breakers.

---

## 1. Core Responsibility

`NightCodeForge.Agents.Optimizations` maximizes performance, token efficiency, and reliability when executing workflows against local LLMs. It stabilizes prompt prefixes to preserve Ollama KV caches, enforces guided grammar constraints, and extracts structured Roslyn/build diagnostics.

## 2. Key Types & Contracts

| Type | Kind | Description |
| :--- | :--- | :--- |
| `PromptPrefixStabilizer` | Class | Enforces strict byte-identical prompt prefixes (system prompts, tool specs) across turns for KV cache hits. |
| `GuidedGrammarHelper` | Class | Formats JSON schema and grammar constraints for LLM sampling engines. |
| `RoslynSymbolIndexProvider` | Class | Lightweight C# symbol indexing using Roslyn for fast workspace code navigation. |
| `StructuredDiagnosticExtractor` | Class | Parses MSBuild and compiler error logs into structured diagnostic objects. |

## 3. Dependencies & Interaction Graph

- **Inbound (Who uses this):** `CrucibleAgentHarness`, `Inference.ILlmChatClientFactory`.
- **Outbound (What this relies on):** `Microsoft.Extensions.AI`, `Microsoft.CodeAnalysis.CSharp`, `NightCodeForge.Core.Domain`.

## 4. Invariants & Architectural Rules

- **Rule 1 (Cache Line Invariance):** System prompts and tool declarations must not inject dynamic timestamps or non-deterministic elements at the prefix to preserve prompt caching.
- **Rule 2 (Lossless Diagnostic Extraction):** Compaction of build logs must never strip compiler error codes (e.g. `CS0103`) or target file paths.

