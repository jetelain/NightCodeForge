# 🧠 Step 12.27: Dynamic Ollama & OllamaRouter Thinking Effort Discovery & Mapping (`/api/show`)

> **Feature Specification (Local LLM Reasoning & Dynamic Capability Discovery)**  
> *Parent Plan:* [Phase 12 Master Plan](../12_EXTEND_FEATURES.md)  
> *Target Framework:* .NET 10 (C# 13) | *Status:* Completed  
> *Target Components:* `src/NightCodeForge.Core`, `src/NightCodeForge.Infrastructure`, `src/NightCodeForge.Agents`, `src/NightCodeForge.Web`, `tests/NightCodeForge.Agents.Tests`, `tests/NightCodeForge.Infrastructure.Tests`  
> *Aligns With:* [AGENTS.md](../../../AGENTS.md), [docs/PLAN.md](../../PLAN.md)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Summary & Problem Statement

### 1.1 The Limitation of Static & Hard-Coded Reasoning Mapping
In local and router-driven inference (Ollama and OllamaRouter), modern reasoning models (such as `Qwen 3.8`, `DeepSeek R1`, `Qwen 2.5 Max`, and custom fine-tunes) use Ollama's `think` parameter to control reasoning behavior.

Currently, [`OllamaCapabilityResolver`](../../../src/NightCodeForge.Agents/Inference/OllamaCapabilityResolver.cs#L118-L125) maps NightCodeForge's [`ThinkingEffort`](../../../src/NightCodeForge.Core/Interfaces/ILlmProviderOptions.cs#L6-L13) enum to fixed strings and relies on static substring profiles in [`LlmProviderOptions.CreateDefaultModelProfiles()`](../../../src/NightCodeForge.Core/Inference/LlmProviderOptions.cs#L137-L152):
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

This static approach causes three major runtime problems:
1. **Invalid Parameters on Non-Standard Levels:** Models such as **Qwen 3.8** (and new model variants not matching hardcoded substring keys) support `[false, "low", "medium", "xhigh"]` with default `"medium"`. Sending `"high"` for `ThinkingEffort.High` when a model is not matched in `ModelProfiles` is **not in the model's valid values list** (where `"xhigh"` is expected), leading to Ollama parameter rejection, degraded reasoning, or fallback to zero reasoning.
2. **Type Incompatibility on Boolean-Only & Non-Thinking Models:**
   - Some reasoning models or quantization runtimes only accept a boolean toggle (`think: true` or `think: false`, values `[false, true]`). Sending string levels (`"medium"` / `"high"`) causes HTTP 400/500 errors on strict endpoints.
   - Non-thinking models (`SupportsThinking == false`) can reject requests that include unsupported `think` string levels.
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

By parsing this object in [`OllamaShowParser`](../../../src/NightCodeForge.Infrastructure/Inference/OllamaShowParser.cs), caching it alongside model context-window metadata in [`ILlmContextWindowCache`](../../../src/NightCodeForge.Core/Interfaces/ILlmContextWindowCache.cs) during both catalog enumeration and on-demand gateway resolution, and applying a deterministic mapping algorithm, NightCodeForge maps `None`, `Low`, `Medium`, `High`, and `CustomBudget` accurately to each model's native capability without manual configuration.

---

## 2. Detailed Behavioral & Architectural Specifications

### 2.1 Metadata Parsing in `OllamaShowParser`

1. **Define `ModelThinkingCapability` in `NightCodeForge.Core.Interfaces`:**
   - Place the capability contract in `NightCodeForge.Core.Interfaces` (alongside [`AvailableModelInfo`](../../../src/NightCodeForge.Core/Interfaces/ILlmModelCatalogService.cs) and [`ILlmContextWindowCache`](../../../src/NightCodeForge.Core/Interfaces/ILlmContextWindowCache.cs)) so all layers (`Core`, `Infrastructure`, `Agents`, `Web`) can reference it without circular dependencies:
     ```csharp
     public record ModelThinkingCapability(
         bool SupportsThinking,
         IReadOnlyList<string> StringLevels,
         bool SupportsBooleanOnly,
         string? DefaultLevel)
     {
         public static ModelThinkingCapability None { get; } = new(false, Array.Empty<string>(), false, null);

         /// <summary>Formatted badge text for UI presentation (keeps presentation formatting out of Razor @code blocks).</summary>
         public string FormattedBadge => !SupportsThinking
             ? string.Empty
             : (StringLevels.Count > 0
                 ? $"🧠 Reasoning: {string.Join(", ", StringLevels)}"
                 : "🧠 Reasoning: boolean (on/off)");
     }
     ```

2. **Extend `OllamaShowParser` (`NightCodeForge.Infrastructure.Inference`):**
   - Add `public static ModelThinkingCapability ParseThinkingCapability(JsonElement root)` (and keep `SupportsThinking(JsonElement root)` forwarding to `ParseThinkingCapability(root).SupportsThinking`).
   - Inspect the `thinking` object from the `POST /api/show` response JSON (checking root first, then `model_info` / `details` if nested):
     - Extract `values`: An array containing valid entries (e.g., `[false, "low", "medium", "xhigh"]` or `[false, true]`).
     - Extract `default`: The default value when `think` is omitted or set to `true` (e.g., `"medium"` or `true`).
     - Normalization rules when `thinking.values` is present:
       - Collect all non-empty string elements (trimmed, lowercase, preserving order or distinct canonical order) into `StringLevels`.
       - If `StringLevels.Count > 0`: `SupportsThinking = true`, `SupportsBooleanOnly = false`, and `DefaultLevel` is the string value of `default` (if `default` is a non-empty string) or `null`.
       - If `StringLevels.Count == 0` and `values` contains boolean `true`: `SupportsThinking = true`, `SupportsBooleanOnly = true`, `DefaultLevel = null`.
       - If `StringLevels.Count == 0` and `values` only contains `false` (or is empty): `SupportsThinking = false`, `SupportsBooleanOnly = false`, `DefaultLevel = null`.
   - **Legacy `capabilities: ["thinking"]` Fallback:**
     - If the `thinking` object is absent (e.g., older Ollama versions or models that only expose `"capabilities": ["completion", "thinking"]`), check the `capabilities` array.
     - When `"thinking"` is present in `capabilities` without a `thinking` object, return:
       `new ModelThinkingCapability(SupportsThinking: true, StringLevels: Array.Empty<string>(), SupportsBooleanOnly: true, DefaultLevel: null)`.
     - When neither `thinking` object nor `"thinking"` capability is present, return `ModelThinkingCapability.None`.
   - Ensure the parser is defensive and never throws exceptions on malformed or unrecognized JSON fields.

### 2.2 Deterministic Dynamic Mapping (`OllamaThinkingEffortMapper`)

1. **Mapping Contract & Rules:**
   - Create `OllamaThinkingEffortMapper` in `NightCodeForge.Agents.Inference` (or `NightCodeForge.Core.Inference`) implementing deterministic mapping from `ThinkingEffort` + `ModelThinkingCapability` to the target model's native `think` parameter value (`object?`):

| Model Capability State | `ThinkingEffort.None` | `ThinkingEffort.Low` | `ThinkingEffort.Medium` | `ThinkingEffort.High` / `CustomBudget` |
| :--- | :--- | :--- | :--- | :--- |
| `[false, "low", "medium", "xhigh"]` (`default: "medium"`) | `false` | `"low"` | `"medium"` | `"xhigh"` |
| `[false, "low", "medium", "high"]` | `false` | `"low"` | `"medium"` | `"high"` |
| `[false, "minimal", "low", "medium", "high", "xhigh"]` | `false` | `"low"` | `"medium"` | `"xhigh"` |
| `[false, "low", "high"]` (`default: "low"` or `"high"`) | `false` | `"low"` | `DefaultLevel` (or median) | `"high"` |
| `[false, true]` or Legacy `capabilities: ["thinking"]` (`SupportsBooleanOnly = true`) | `false` | `true` | `true` | `true` |
| Confirmed Non-Thinking (`SupportsThinking = false`) | `null` (omit `think`) | `null` (omit `think`) | `null` (omit `think`) | `null` (omit `think`) |

   > **Note on Uncached vs. Confirmed Non-Thinking Models:**
   > - When a model **has been probed** via `/api/show` and confirmed not to support thinking (`SupportsThinking == false`), `OllamaThinkingEffortMapper` returns `null`, and `OllamaCapabilityResolver` omits `"think"` from `ChatOptions.AdditionalProperties` (or leaves `AdditionalProperties` without `"think"`) so non-thinking models never receive unsupported `think` parameters.
   > - When a model **has not yet been probed/cached** (`TryGetThinkingCapability` returns `false`), `OllamaCapabilityResolver` falls back to `LlmProviderOptions.ModelProfiles` and standard effort resolution (`false` for `ThinkingEffort.None`, or `"low"` / `"medium"` / `"high"` / `"xhigh"`), preserving full compatibility with standalone unit tests and offline configurations.
   > - Special case for Legacy `capabilities: ["thinking"]` (`SupportsBooleanOnly = true` and `StringLevels.Count == 0`): if a matching static profile exists in `LlmProviderOptions.ModelProfiles` with explicit `SupportedEfforts` (e.g., an older Ollama server serving `qwen3.8` without the newer `thinking` JSON block), `OllamaCapabilityResolver` may consult the static profile; otherwise it emits boolean `true` / `false`.

2. **Ranked Matching Algorithm (for `StringLevels.Count > 0`):**
   - Rank canonical levels by ordinal magnitude: `["minimal", "low", "medium", "high", "xhigh", "maximum"]`.
   - **`ThinkingEffort.None`:** Always returns `false` (`bool`).
   - **`ThinkingEffort.High` / `CustomBudget`:** Resolves to the highest-ranked level available in `StringLevels` (e.g., `"xhigh"` if present; else `"high"`; else the highest level in `StringLevels`).
   - **`ThinkingEffort.Low`:** Resolves to `"low"` if present in `StringLevels`; if absent, selects the lowest positive level in `StringLevels` (e.g., `"minimal"`).
   - **`ThinkingEffort.Medium`:** Resolves to `DefaultLevel` if `DefaultLevel` is non-empty and present in `StringLevels`; otherwise resolves to `"medium"` if present in `StringLevels`; otherwise selects the median level between lowest and highest.
   - **Direct String Effort Resolution (`ResolveReasoningEffort(string? modelId, string? requestedEffort)`):**
     - When a string effort (such as `"high"`, `"medium"`, `"low"`, `"minimal"`, `"xhigh"`) is resolved against a cached `ModelThinkingCapability` with `StringLevels.Count > 0`:
       - `"high"` or `"xhigh"` or `"maximum"` when requesting high/max effort maps to the highest supported level if `"high"` is not in `StringLevels` (so requesting `"high"` on `[ "low", "medium", "xhigh" ]` resolves to `"xhigh"`).
       - If the requested string is directly in `StringLevels`, return it.
       - Otherwise, resolve to the closest rank in `StringLevels` (preferring higher rank on tie).

3. **Output Parameter Typing:**
   - The mapped value is stored in `AdditionalPropertiesDictionary["think"]`:
     - `bool` (`false` or `true`),
     - `string` (`"low"`, `"medium"`, `"xhigh"`, etc.), or
     - omitted when `null` (`SupportsThinking == false`).

### 2.3 Caching, Cold-Start Gateway Probing & Service Integration

1. **Extend `ILlmContextWindowCache` (`NightCodeForge.Core.Interfaces` / `NightCodeForge.Infrastructure.Inference`):**
   - Extend [`ILlmContextWindowCache`](../../../src/NightCodeForge.Core/Interfaces/ILlmContextWindowCache.cs) and [`LlmContextWindowCache`](../../../src/NightCodeForge.Infrastructure/Inference/LlmContextWindowCache.cs) to also store and retrieve `ModelThinkingCapability` per model (normalized by trimmed lowercase model name):
     ```csharp
     bool TryGetThinkingCapability(string modelName, out ModelThinkingCapability capability);
     void SetThinkingCapability(string modelName, ModelThinkingCapability capability);
     ```

2. **Catalog Discovery (`OllamaModelCatalogService` & `OllamaRouterModelCatalogService`):**
   - Inject `ILlmContextWindowCache` as a required constructor parameter in both [`OllamaModelCatalogService`](../../../src/NightCodeForge.Infrastructure/Inference/OllamaModelCatalogService.cs) and [`OllamaRouterModelCatalogService`](../../../src/NightCodeForge.Infrastructure/Inference/OllamaRouterModelCatalogService.cs) (adhering to [AGENTS.md](../../../AGENTS.md): no optional constructor parameters in services).
   - In `OllamaModelCatalogService.BuildModelInfoAsync`:
     - Parse both `context = OllamaShowParser.ParseContextWindow(doc.RootElement)` and `thinking = OllamaShowParser.ParseThinkingCapability(doc.RootElement)`.
     - Cache both in `ILlmContextWindowCache` (`SetContextWindow` when `context > 0`, and `SetThinkingCapability`).
     - Pass `SupportsReasoning: thinking.SupportsThinking` and `ThinkingCapability: thinking` to `AvailableModelInfo`.
   - In `OllamaRouterModelCatalogService.GetAvailableModelsAsync`:
     - Currently, `OllamaRouterModelCatalogService` only calls `GET /api/router/models` and hardcodes `SupportsReasoning: true`.
     - Enhance it to also probe `POST /api/show` (with `MaxDegreeOfParallelism = 4`, matching `OllamaModelCatalogService`) for each distinct router model to discover its `ModelThinkingCapability` (and fallback context window if router context window is unset).
     - Cache the discovered `ContextWindowTokens` and `ModelThinkingCapability` in `ILlmContextWindowCache`.
     - If `POST /api/show` fails for a router model, fall back gracefully without failing catalog enumeration.

3. **On-Demand Gateway Probing for Cold-Start Background Workers (`ILlmGatewayService` & `HarnessAgentFactory`):**
   - Background Hangfire workers executing stages via [`HarnessAgentFactory.CreateAsync`](../../../src/NightCodeForge.Agents/Inference/HarnessAgentFactory.cs) may run before any UI catalog refresh has occurred.
   - In [`OllamaGatewayServiceBase`](../../../src/NightCodeForge.Infrastructure/Inference/OllamaGatewayServiceBase.cs), when `ProbeShowContextWindowAsync` (or a dedicated `/api/show` capability probe) parses `POST /api/show`, also parse and cache `ModelThinkingCapability` via `_contextWindowCache.SetThinkingCapability(normalizedModelName, capability)`.
   - Add `Task<ModelThinkingCapability?> ResolveThinkingCapabilityAsync(string modelName, CancellationToken cancellationToken = default)` to [`ILlmGatewayService`](../../../src/NightCodeForge.Core/Interfaces/IOllamaRouterGatewayService.cs#L116-L132):
     - Implemented in `OllamaGatewayServiceBase`: checks `_contextWindowCache.TryGetThinkingCapability`; if not cached, probes `POST /api/show` (also caching context window if discovered), caches the resulting `ModelThinkingCapability`, and returns it (or `null` if unreachable).
     - Implemented in [`OpenAiCompatibleGatewayService`](../../../src/NightCodeForge.Infrastructure/Inference/OpenAiCompatibleGatewayService.cs): returns `Task.FromResult<ModelThinkingCapability?>(null)`.
   - In [`HarnessAgentFactory.CreateAsync`](../../../src/NightCodeForge.Agents/Inference/HarnessAgentFactory.cs#L105):
     - Ensure `_gatewayService.ResolveThinkingCapabilityAsync(effectiveModel, cancellationToken)` is awaited **before** calling `_capabilityResolver.CreateChatOptions(profile, effectiveModel)` so that cold-start agent sessions always have the model's `/api/show` thinking capability warmed in `ILlmContextWindowCache`.

4. **Integration into `OllamaCapabilityResolver`:**
   - Inject `ILlmContextWindowCache` as a required constructor parameter into [`OllamaCapabilityResolver`](../../../src/NightCodeForge.Agents/Inference/OllamaCapabilityResolver.cs):
     `public OllamaCapabilityResolver(IOptions<LlmProviderOptions> options, ILlmContextWindowCache capabilityCache)`
   - Update DI registration in [` NightCodeForge.Agents/ServiceCollectionExtensions.cs`](../../../src/NightCodeForge.Agents/ServiceCollectionExtensions.cs) (which resolves `ILlmContextWindowCache` from DI) and update test builders ([`TestCrucibleHarnessBuilder`](../../../tests/NightCodeForge.Agents.Tests/Fixtures/TestCrucibleHarnessBuilder.cs), [`TestIdeationServiceBuilder`](../../../tests/NightCodeForge.Agents.Tests/Fixtures/TestIdeationServiceBuilder.cs)) and [`ModelCapabilityResolver`](../../../src/NightCodeForge.Agents/Inference/ModelCapabilityResolver.cs).
   - Precedence in `OllamaCapabilityResolver`:
     1. **Discovered `ModelThinkingCapability` in `ILlmContextWindowCache`:**
        - If `capabilityCache.TryGetThinkingCapability(effectiveModel, out var capability)` succeeds:
          - Unless `capability.SupportsBooleanOnly && capability.StringLevels.Count == 0` AND a static `ModelCapabilityProfile` in `LlmProviderOptions.ModelProfiles` explicitly defines `SupportedEfforts` for this model, use `OllamaThinkingEffortMapper` with `capability`.
          - If the mapped value is `null` (`SupportsThinking == false`), do not set `"think"` in `AdditionalProperties`.
          - Otherwise set `AdditionalProperties["think"] = mappedValue` (`bool` or `string`).
     2. **Uncached Fallback:**
        - If the model is not in `ILlmContextWindowCache`, retain existing fallback resolution via `LlmProviderOptions.ModelProfiles` and standard effort ranks (`"low"`, `"medium"`, `"high"`, `"xhigh"`), and `["think"] = false` when `ThinkingEffort.None`.

5. **`AvailableModelInfo` & Declarative Display in `AgentProfilesTab.razor`:**
   - Add `ModelThinkingCapability? ThinkingCapability = null` to [`AvailableModelInfo`](../../../src/NightCodeForge.Core/Interfaces/ILlmModelCatalogService.cs).
   - Keep presentation formatting inside `ModelThinkingCapability.FormattedBadge` (in `NightCodeForge.Core.Interfaces`) in compliance with [AGENTS.md](../../../AGENTS.md) UI/Presentation separation rules (no parsing or string-building logic in `.razor` `@code` blocks).
   - In [`AgentProfilesTab.razor`](../../../src/NightCodeForge.Web/Components/Settings/AgentProfilesTab.razor), alongside the discovered context window badge, render the model's thinking capability badge when non-empty (e.g., `🧠 Reasoning: low, medium, xhigh` or `🧠 Reasoning: boolean (on/off)`).

---

## 3. Verification & Test Strategy

1. **`OllamaShowParserTests` (`NightCodeForge.Infrastructure.Tests`):**
   - Parse JSON payload from Qwen 3.8 (`{"thinking":{"values":[false,"low","medium","xhigh"],"default":"medium"}}`) and verify `SupportsThinking = true`, `StringLevels = ["low", "medium", "xhigh"]`, `DefaultLevel = "medium"`, `SupportsBooleanOnly = false`.
   - Parse JSON payload with boolean values (`{"thinking":{"values":[false, true],"default":true}}`) and verify `SupportsThinking = true`, `SupportsBooleanOnly = true`, `StringLevels = []`.
   - Parse legacy payload (`{"capabilities":["completion","thinking"]}`) without `thinking` object and verify `SupportsThinking = true`, `SupportsBooleanOnly = true`, `StringLevels = []`, `DefaultLevel = null`.
   - Parse non-thinking model payload (`{"capabilities":["completion"]}`) and verify `SupportsThinking = false`, `SupportsBooleanOnly = false`, `StringLevels = []`.

2. **`OllamaThinkingEffortMapperTests` (`NightCodeForge.Agents.Tests`):**
   - Test Qwen 3.8 capability (`[false, "low", "medium", "xhigh"]`, `default: "medium"`):
     - `ThinkingEffort.None` $\to$ `false`
     - `ThinkingEffort.Low` $\to$ `"low"`
     - `ThinkingEffort.Medium` $\to$ `"medium"`
     - `ThinkingEffort.High` $\to$ `"xhigh"`
     - `ThinkingEffort.CustomBudget` $\to$ `"xhigh"`
   - Test standard model with `[false, "low", "medium", "high"]`:
     - `ThinkingEffort.High` $\to$ `"high"`
   - Test 2-level model with `[false, "low", "high"]` (`default: "low"`):
     - `ThinkingEffort.Medium` $\to$ `"low"`
   - Test boolean-only model (`[false, true]`):
     - `ThinkingEffort.None` $\to$ `false`
     - `ThinkingEffort.Low` $\to$ `true`
     - `ThinkingEffort.Medium` $\to$ `true`
     - `ThinkingEffort.High` $\to$ `true`
   - Test confirmed non-thinking model (`SupportsThinking = false`):
     - `ThinkingEffort.None` / `Low` / `Medium` / `High` $\to$ `null`

3. **Resolver, Catalog & Gateway Integration Tests (`NightCodeForge.Agents.Tests` & `NightCodeForge.Infrastructure.Tests`):**
   - Verify `OllamaCapabilityResolver.CreateChatOptions` generates `chatOptions.AdditionalProperties["think"] = "xhigh"` for any dynamically discovered model with `["low", "medium", "xhigh"]` (even with an arbitrary name like `"custom-reasoner:latest"` not in `ModelProfiles`) when `ThinkingEffort.High` is configured.
   - Verify `OllamaCapabilityResolver.CreateChatOptions` generates `chatOptions.AdditionalProperties["think"] = true` (boolean) for a cached boolean-only model when `ThinkingEffort.Medium` or `High` is configured.
   - Verify `OllamaCapabilityResolver.CreateChatOptions` omits `"think"` when a cached model has `SupportsThinking = false`.
   - Verify `OllamaModelCatalogService` and `OllamaRouterModelCatalogService` populate `ILlmContextWindowCache` with `ModelThinkingCapability` and set `AvailableModelInfo.ThinkingCapability`.
   - Verify `OllamaGatewayServiceBase.ResolveThinkingCapabilityAsync` probes `/api/show` on cache miss and caches the result.

---

## 4. Definition of Done (DoD) Checklist

- [x] Define `ModelThinkingCapability` record (with `FormattedBadge`) in `NightCodeForge.Core.Interfaces` and add `ThinkingCapability` to `AvailableModelInfo`.
- [x] Extend `OllamaShowParser` with `ParseThinkingCapability(JsonElement root)` handling `thinking.values`, `thinking.default`, legacy `capabilities: ["thinking"]`, and non-thinking payloads.
- [x] Extend `ILlmContextWindowCache` and `LlmContextWindowCache` with `TryGetThinkingCapability` and `SetThinkingCapability`.
- [x] Implement `OllamaThinkingEffortMapper` mapping `ThinkingEffort` (`None`, `Low`, `Medium`, `High`, `CustomBudget`) and string efforts deterministically based on `ModelThinkingCapability`.
- [x] Update `OllamaModelCatalogService` and `OllamaRouterModelCatalogService` to inject `ILlmContextWindowCache`, probe `POST /api/show`, and cache `ModelThinkingCapability`.
- [x] Add `ResolveThinkingCapabilityAsync` to `ILlmGatewayService` / `OllamaGatewayServiceBase` and invoke it in `HarnessAgentFactory.CreateAsync` before `CreateChatOptions` so cold-start worker runs always warm the capability cache.
- [x] Update `OllamaCapabilityResolver` to require `ILlmContextWindowCache` and resolve `"think"` parameters via `OllamaThinkingEffortMapper` with fallback to `ModelProfiles` when uncached.
- [x] Display `ModelThinkingCapability.FormattedBadge` in `AgentProfilesTab.razor`.
- [x] Author unit and integration tests in `OllamaShowParserTests`, `OllamaThinkingEffortMapperTests`, `OllamaCapabilityResolverTests`, and `LlmModelCatalogServiceTests`.
- [x] Verify all tests pass via `dotnet test --filter "Category!=Acceptance&Category!=E2E"`.
