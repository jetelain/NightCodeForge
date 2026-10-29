# NightCodeForge.Agents.Inference

> **Assembly:** `NightCodeForge.Agents` | **Layer:** Agents / Model Inference  
> **Role:** Provides LLM chat client integrations, streaming adapters, reasoning token extraction, and capability resolvers.

---

## 1. Core Responsibility

`NightCodeForge.Agents.Inference` encapsulates communication with local language model runtimes (Ollama). It implements `Microsoft.Extensions.AI.IChatClient` adapters, wraps clients with pipeline middleware (such as `ThinkingAndActivityTracingChatClient` for extracting `<think>` reasoning traces and `LenientToolCallChatClient` for forgiving syntax errors), and resolves model capabilities based on model families.

## 2. Key Types & Contracts

| Type | Kind | Description |
| :--- | :--- | :--- |
| `ILlmChatClientFactory` | Interface | Factory contract for generating configured `IChatClient` instances for target models. |
| `OllamaChatClient` | Class | High-performance HTTP client communicating with Ollama's `/api/chat` streaming endpoint. |
| `ThinkingAndActivityTracingChatClient` | Class | Middleware extracting thinking blocks (`<think>...</think>`) and emitting activity events. |
| `LenientToolCallChatClient` | Class | Middleware detecting and repairing unstructured tool calls emitted in standard message content. |
| `ModelCapabilityResolver` | Class | Detects model features (native tool calling, context window sizes, optimal temperature). |
| `LlmProviderOptions` | Class | Strongly-typed configuration options for endpoints, timeouts, and fallback policies. |

## 3. Dependencies & Interaction Graph

- **Inbound (Who uses this):** `CrucibleAgentHarness`, `IdeationService`, `TriageService`.
- **Outbound (What this relies on):** `Microsoft.Extensions.AI`, `NightCodeForge.Core.Interfaces.IOllamaRouterGatewayService`.

## 4. Invariants & Architectural Rules

- **Rule 1 (Streaming Integrity):** All chat calls must stream incremental chunks to avoid blocking threads on long generational responses.
- **Rule 2 (Reasoning Trace Separation):** Thought tokens (`<think>`) must be extracted and published as thoughts, not passed as final conversational output.
- **Rule 3 (Fail Fast on Missing Endpoint):** If the inference endpoint is unreachable, fail fast rather than hanging indefinitely.

## 5. Usage Example

```csharp
// Creating a chat client configured with telemetry and reasoning tracing
var chatClient = chatClientFactory.CreateClient("Qwen3.8-27B:latest");
await foreach (var update in chatClient.CompleteStreamingAsync(chatMessages, options, cancellationToken))
{
    Console.Write(update.Text);
}
```
