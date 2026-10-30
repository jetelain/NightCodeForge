# NightCodeForge.Infrastructure.Inference

> **Assembly:** `NightCodeForge.Infrastructure` | **Layer:** Infrastructure / Model Inference Gateway  
> **Role:** Implements HTTP gateway communication with Ollama router instances for model discovery and lifecycle management.

---

## 1. Core Responsibility

`NightCodeForge.Infrastructure.Inference` provides communication channels to Ollama router instances. It implements `ILlmGatewayService` to query available local LLM models, inspect context window limits, initiate model warm-up (VRAM preloading via `/api/generate`), and configure routing endpoints.

## 2. Key Types & Contracts

| Type | Kind | Description |
| :--- | :--- | :--- |
| `OllamaRouterGatewayService` | Class | Implementation of `ILlmGatewayService` executing HTTP REST calls against Ollama router endpoints. |
| `OllamaRouterServices` | Static Class | Dependency injection extensions configuring HTTP client policies, retries, and endpoint settings. |

## 3. Dependencies & Interaction Graph

- **Inbound (Who uses this):** `NightCodeForge.Agents.Inference`, Web Settings controllers.
- **Outbound (What this relies on):** `System.Net.Http`, `NightCodeForge.Core.Interfaces.ILlmGatewayService`.

## 4. Invariants & Architectural Rules

- **Rule 1 (Timeout Resilience):** Long-running model load requests must use configurable HTTP timeouts and respect external cancellation tokens.
- **Rule 2 (Fail Fast on Unavailable Endpoint):** When Ollama is unreachable, throw an informative exception rather than hanging or returning default models.
- **Rule 3 (Zero Fallback Assumption):** Do not assume a model is loaded if the health or model catalog check fails.

## 5. Usage Example

```csharp
// Checking connectivity and loading model into VRAM
var isHealthy = await ollamaGateway.PingAsync(cancellationToken);
if (isHealthy)
{
    await ollamaGateway.WarmupModelAsync("Qwen3.8-27B:latest", cancellationToken);
}
```
