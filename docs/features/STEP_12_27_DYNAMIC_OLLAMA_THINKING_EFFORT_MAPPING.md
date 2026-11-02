# 🧠 Step 12.27: Dynamic Ollama & OllamaRouter Thinking Effort Discovery & Mapping (`/api/show`)

> **Feature Specification (Local LLM Reasoning & Dynamic Capability Discovery)**  
> *Parent Plan:* [Features & Integrations Roadmap](README.md)  
> *Target Framework:* .NET 10 (C# 13) | *Status:* Planned  
> *Target Components:* `src/NightCodeForge.Core`, `src/NightCodeForge.Infrastructure`, `src/NightCodeForge.Agents`, `tests/NightCodeForge.Agents.Tests`, `tests/NightCodeForge.Infrastructure.Tests`  
> *Aligns With:* [AGENTS.md](../../AGENTS.md), [docs/PLAN.md](../PLAN.md)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Summary & Problem Statement

### 1.1 The Limitation of Hard-Coded Reasoning Mapping
In local and router-driven inference (Ollama and OllamaRouter), modern reasoning models (such as `Qwen 3.8`, `DeepSeek R1`, `Qwen 2.5 Max`, and custom fine-tunes) use Ollama's `think` parameter to control reasoning behavior.

Currently, [`OllamaCapabilityResolver`](../../src/NightCodeForge.Agents/Inference/OllamaCapabilityResolver.cs#L118-L125) hardcodes an inflexible mapping from NightCodeForge's [`ThinkingEffort`](../../src/NightCodeForge.Core/Domain/AgentProfile.cs) enum:
```csharp
private static string? MapThinkingEffort(ThinkingEffort effort) => effort switch
{
    ThinkingEffort.Low => "low",
    ThinkingEffort.Medium => "medium",
    ThinkingEffort.High => "high",
    ThinkingEffort.CustomBudget => "high",
    _ => null
};
```

This hardcoded mapping causes two major runtime problems:
1. **Invalid Parameters on Non-Standard Levels:** Models such as **Qwen 3.8** support `[false, "low", "medium", "xhigh"]` with default `"medium"`. NightCodeForge sends `"high"` for `ThinkingEffort.High`, which is **not in the model's valid values list** (where `"xhigh"` is expected), leading to Ollama parameter rejection, degraded reasoning, or fallback to zero reasoning.
2. **Type Incompatibility on Boolean-Only Models:** Some reasoning models or quantization runtimes only accept a boolean toggle (`think: true` or `think: false`, values `[false, true]`). Sending string levels (`"medium"` / `"high"`) causes HTTP 400/500 errors on strict endpoints.
3. **Redundant Static Profile Dictionaries:** Developers must manually configure `ModelProfiles` in `appsettings.json` for each newly pulled model instead of having the system discover supported thinking parameters dynamically.

### 1.2 The Solution: Dynamic Discovery via `POST /api/show`
Ollama exposes the model's exact supported thinking configuration via its metadata endpoint (`POST /api/show`). For example, for Qwen 3.8, the response contains:
```json
{
  "thinking": {
    "values": [false, "low", "medium", "xhigh"],
    "default": "medium"
  }
}
```
Or for boolean models:
```json
{
  "thinking": {
    "values": [false, true],
    "default": true
  }
}
```

By parsing this object in [`OllamaShowParser`](../../src/NightCodeForge.Infrastructure/Inference/OllamaShowParser.cs), caching it alongside model metadata, and applying an intuitive, deterministic mapping system, NightCodeForge maps `Low`, `Medium`, and `High` effortlessly and accurately to each model's native capability without any manual configuration.

---

## 2. Detailed Behavioral & Architectural Specifications

### 2.1 Metadata Parsing in `OllamaShowParser`

1. **Extend `OllamaShowParser` (`NightCodeForge.Infrastructure.Inference`):**
   - Update `OllamaShowParser` to parse the `thinking` property from the `POST /api/show` response JSON (under root or `model_info` / `details`).
   - Extract:
     - `values`: An array containing valid entries (e.g. `[false, "low", "medium", "xhigh"]` or `[false, true]`).
     - `default`: The default value when `think` is omitted or set to `true` (e.g. `"medium"` or `true`).
   - Define a lightweight model thinking capability contract in `NightCodeForge.Core.Inference` / `Interfaces`:
     ```csharp
     public record ModelThinkingCapability(
         bool SupportsThinking,
         IReadOnlyList<string> StringLevels,
         bool SupportsBooleanOnly,
         string? DefaultLevel);
     ```
   - Ensure the parser is defensive:
     - If the `thinking` object is absent, check the legacy `capabilities: ["thinking"]` array.
     - Never throw exceptions on malformed or unrecognized JSON fields.

### 2.2 Deterministic Dynamic Mapping (`OllamaThinkingEffortMapper`)

1. **Clean Mapping Rules:**
   - Create `OllamaThinkingEffortMapper` in `NightCodeForge.Agents.Inference` (or `NightCodeForge.Core.Inference`) implementing deterministic mapping from `ThinkingEffort` to the target model's actual supported value:

| Model's Supported Values (`values`) | `ThinkingEffort.None` | `ThinkingEffort.Low` | `ThinkingEffort.Medium` | `ThinkingEffort.High` / `CustomBudget` |
| :--- | :--- | :--- | :--- | :--- |
| `[false, "low", "medium", "xhigh"]` | `false` | `"low"` | `"medium"` | `"xhigh"` |
| `[false, "low", "medium", "high"]` | `false` | `"low"` | `"medium"` | `"high"` |
| `[false, "minimal", "low", "medium", "high", "xhigh"]` | `false` | `"low"` | `"medium"` | `"xhigh"` |
| `[false, "low", "high"]` | `false` | `"low"` | default | `"high"` |
| `[false, true]` (Boolean toggle only) | `false` | `true` | `true` | `true` |
| Absent / Unknown / No Thinking | `false` | `null` | `null` | `null` |

2. **Ranked Matching Algorithm:**
   - When the model provides a list of string levels:
     - Rank canonical levels by ordinal magnitude: `["minimal", "low", "medium", "high", "xhigh", "maximum"]`.
     - **`ThinkingEffort.None`:** Always returns `false`.
     - **`ThinkingEffort.High` / `CustomBudget`:** Resolves to the highest available level in the model's supported list (e.g. `"xhigh"` if present; else `"high"`; else the highest level in the list).
     - **`ThinkingEffort.Low`:** Resolves to `"low"` if present; if absent, the lowest positive level in the model's list.
     - **`ThinkingEffort.Medium`:** Resolves to the model's declared `default` level if specified in `thinking.default`; otherwise, resolves to `"medium"` if present; otherwise, selects the median level between lowest and highest.

3. **Output Parameter Typing:**
   - The returned object must be directly compatible with `AdditionalPropertiesDictionary["think"]`:
     - Can be `bool` (`false` or `true`).
     - Can be `string` (`"low"`, `"medium"`, `"xhigh"`, etc.).

### 2.3 Caching & Service Integration

1. **Model Capability Caching (`ILlmModelCapabilityCache` / `ILlmContextWindowCache`):**
   - Extend the model capability cache (in `NightCodeForge.Infrastructure.Inference`) so that when [`OllamaModelCatalogService`](../../src/NightCodeForge.Infrastructure/Inference/OllamaModelCatalogService.cs) or [`OllamaRouterModelCatalogService`](../../src/NightCodeForge.Infrastructure/Inference/OllamaRouterModelCatalogService.cs) queries `POST /api/show`, the parsed `ModelThinkingCapability` is stored alongside `ContextWindowTokens`.
   - Provide an accessor: `bool TryGetThinkingCapability(string modelId, out ModelThinkingCapability capability)`.

2. **Integration into `OllamaCapabilityResolver`:**
   - Inject the capability cache into `OllamaCapabilityResolver`.
   - In `BuildReasoningProperties(string? modelId, string? requestedEffort)` and `CreateChatOptions(AgentProfile? profile, string? modelId)`:
     - Look up the model's `ModelThinkingCapability` from cache.
     - If available, use `OllamaThinkingEffortMapper` with the model's discovered capability.
     - If not yet cached (e.g. standalone call before catalog refresh), fall back safely to standard defaults (`"low"`, `"medium"`, `"xhigh"`), ensuring zero breaking changes.

3. **`AvailableModelInfo` Telemetry & Metadata:**
   - Populate `AvailableModelInfo.Metadata["ThinkingLevels"]` and `AvailableModelInfo.Metadata["ThinkingDefault"]` during catalog enumeration.
   - In `AgentProfilesTab.razor`, display a subtle badge or tooltip next to the model name indicating discovered thinking modes (e.g., `🧠 Reasoning: low, medium, xhigh`).

---

## 3. Verification & Test Strategy

1. **`OllamaShowParserTests` (`NightCodeForge.Infrastructure.Tests`):**
   - Parse JSON payload from Qwen 3.8 (`{"thinking":{"values":[false,"low","medium","xhigh"],"default":"medium"}}`) and verify `StringLevels = ["low", "medium", "xhigh"]`, `DefaultLevel = "medium"`, `SupportsBooleanOnly = false`.
   - Parse JSON payload with boolean values (`{"thinking":{"values":[false, true]}}`) and verify `SupportsBooleanOnly = true`.
   - Parse legacy payload (`{"capabilities":["thinking"]}`) without `thinking` object and verify graceful fallback.
   - Parse non-thinking model payload (no thinking keys) and verify `SupportsThinking = false`.

2. **`OllamaThinkingEffortMapperTests` (`NightCodeForge.Agents.Tests`):**
   - Test Qwen 3.8 capability:
     - `ThinkingEffort.None` $\to$ `false`
     - `ThinkingEffort.Low` $\to$ `"low"`
     - `ThinkingEffort.Medium` $\to$ `"medium"`
     - `ThinkingEffort.High` $\to$ `"xhigh"`
     - `ThinkingEffort.CustomBudget` $\to$ `"xhigh"`
   - Test standard model with `"high"`:
     - `ThinkingEffort.High` $\to$ `"high"`
   - Test boolean-only model:
     - `ThinkingEffort.None` $\to$ `false`
     - `ThinkingEffort.Low` $\to$ `true`
     - `ThinkingEffort.Medium` $\to$ `true`
     - `ThinkingEffort.High` $\to$ `true`

3. **Integration Test (`NightCodeForge.Agents.Tests`):**
   - Verify `OllamaCapabilityResolver.CreateChatOptions` generates `chatOptions.AdditionalProperties["think"] = "xhigh"` for Qwen 3.8 when `ThinkingEffort.High` is configured on the agent profile.

---

## 4. Definition of Done (DoD) Checklist

- [ ] Extend `OllamaShowParser` to parse the `thinking` object (`values` array and `default` string/bool) from Ollama `POST /api/show`.
- [ ] Define `ModelThinkingCapability` record in `NightCodeForge.Core.Inference` / `Interfaces`.
- [ ] Implement `OllamaThinkingEffortMapper` mapping `ThinkingEffort` (`None`, `Low`, `Medium`, `High`) dynamically based on the model's supported levels.
- [ ] Store discovered thinking capabilities in the model capability cache during `OllamaModelCatalogService` and `OllamaRouterModelCatalogService` probes.
- [ ] Update `OllamaCapabilityResolver` to use `OllamaThinkingEffortMapper` with cached model capabilities.
- [ ] Expose discovered thinking levels in `AvailableModelInfo.Metadata` and display in `AgentProfilesTab.razor`.
- [ ] Author unit tests in `OllamaShowParserTests` and `OllamaThinkingEffortMapperTests`.
- [ ] Verify all tests pass via `dotnet test --filter "Category!=Acceptance&Category!=E2E"`.

