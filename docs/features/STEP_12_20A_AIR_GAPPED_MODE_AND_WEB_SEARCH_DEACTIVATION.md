# 🛡️ Step 12.20A: Air-Gapped Mode & External Web Search Deactivation

> **Feature Implementation Step (Part 1 of 2 — Air-Gapped Configuration, Agent Tool Filtering & Settings Banner)**  
> *Parent Plan:* [Features & Integrations Roadmap](README.md)  
> *Companion Step:* [Step 12.20B: `mdfetch` Documentation Whitelist & SSRF Governance](STEP_12_20B_MDFETCH_DOCUMENTATION_WHITELIST_AND_SSRF_GOVERNANCE.md)  
> *Related Steps:* [Step 12.19: Air-Gapped Offline Web App Hardening](../archive/phase-12/STEP_12_19_AIR_GAPPED_OFFLINE_WEB_APP_HARDENING.md)  
> *Source Policy:* [AGENTS.md](../../AGENTS.md)  
> *Project Target:* `src/NightCodeForge.Core`, `src/NightCodeForge.Agents`, `src/NightCodeForge.Web`, `tests/NightCodeForge.Core.Tests`, `tests/NightCodeForge.Agents.Tests`, `tests/NightCodeForge.Web.Tests` | *Status:* ✅ Completed | *Target Framework:* .NET 10 (C# 13)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Objective

1. **Air-Gapped Search Deactivation (Zero Cloud Phone-Home):** Automatically deactivate external web searching (`https://ollama.com/api/web_search`) when NightCodeForge is running in an air-gapped, isolated, or offline environment to prevent connection timeouts, pipeline stalls, and data leakage.
2. **Unified Air-Gapped Configuration (`SecurityOptions`):** Leverage the centralized [`SecurityOptions.AirGappedMode`](../../src/NightCodeForge.Core/Security/SecurityOptions.cs) configuration (`Security:AirGappedMode` in [`appsettings.json`](../../src/NightCodeForge.Web/appsettings.json) and `NCF_AIR_GAPPED` environment variable override, introduced in [Step 12.19](../archive/phase-12/STEP_12_19_AIR_GAPPED_OFFLINE_WEB_APP_HARDENING.md)) and introduce `AirGappedOperationException` in `NightCodeForge.Core`.
3. **Fail-Fast Agent & Tooling Guardrails:** Ensure [`CanonicalAIFunctionFactory`](../../src/NightCodeForge.Agents/Tools/CanonicalAIFunctionFactory.cs) (used by [`CanonicalToolsetFactory`](../../src/NightCodeForge.Agents/Tools/CanonicalToolsetFactory.cs) across both [`CrucibleAgentHarness`](../../src/NightCodeForge.Agents/Crucible/CrucibleAgentHarness.cs) and [`IdeationService`](../../src/NightCodeForge.Agents/Ideation/IdeationService.cs)) omits `web_search` from advertised tool sets when disabled, [`CanonicalTools.WebSearchAsync`](../../src/NightCodeForge.Agents/Tools/CanonicalTools.cs) returns a clear model-visible error if the LLM still invokes `web_search` via [`LenientToolDispatcher`](../../src/NightCodeForge.Agents/Dispatcher/LenientToolDispatcher.cs), and [`OllamaWebSearchService.SearchAsync`](../../src/NightCodeForge.Agents/Tools/OllamaWebSearchService.cs) fails fast with `AirGappedOperationException` if called directly.
4. **Settings UX Transparency:** Update [`WebSearchSettingsTab.razor`](../../src/NightCodeForge.Web/Components/Settings/WebSearchSettingsTab.razor) to display an informative Air-Gapped status banner, disabling cloud provider inputs and test controls with clear explanatory tooltips.

*(Internal documentation fetching governance for `mdfetch` and `MdfetchSecuritySettingsTab.razor` are specified in [Step 12.20B](STEP_12_20B_MDFETCH_DOCUMENTATION_WHITELIST_AND_SSRF_GOVERNANCE.md).)*

---

## 2. Architecture & Technical Specification

### 2.1 Air-Gapped Security Configuration & Domain Exception
Reuse [`SecurityOptions`](../../src/NightCodeForge.Core/Security/SecurityOptions.cs) in `NightCodeForge.Core.Security` (bound from `"Security"` in [`appsettings.json`](../../src/NightCodeForge.Web/appsettings.json) and `NCF_AIR_GAPPED` environment variable):
```json
{
  "Security": {
    "AirGappedMode": false,
    "EnableContentSecurityPolicy": true
  }
}
```
- Register `services.BindConfigurationSectionAsDefault<SecurityOptions>(SecurityOptions.SectionName)` in [`ServiceCollectionExtensions.AddNightCodeForgeAgents`](../../src/NightCodeForge.Agents/ServiceCollectionExtensions.cs) so `IOptions<SecurityOptions>` is always available in both Web and non-Web hosts.
- Add `AirGappedOperationException` in `NightCodeForge.Core.Exceptions` (inheriting from `InvalidOperationException` or `Exception`) to represent operations prohibited by air-gapped policy.

### 2.2 Web Search Deactivation Logic
1. **`IWebSearchService` Contract ([`IWebSearchService.cs`](../../src/NightCodeForge.Agents/Tools/IWebSearchService.cs)):**
   - Expose `bool IsEnabled { get; }` and `string? DisabledReason { get; }` alongside `ProviderName` and `IsConfigured` (update [`OllamaWebSearchService`](../../src/NightCodeForge.Agents/Tools/OllamaWebSearchService.cs) and `MockWebSearchService`, maintaining zero optional constructor parameters per [AGENTS.md](../../AGENTS.md)).
   - When `AirGappedMode` is `false`: `IsEnabled` equals `IsConfigured`, and `DisabledReason` is `null` when configured (or explains that no API key is configured).
2. **Behavior when `SecurityOptions.AirGappedMode` is active:**
   - `IsEnabled` returns `false` (even if an API key is configured).
   - `DisabledReason` returns `"Web search is disabled in Air-Gapped mode (external cloud search to ollama.com is prohibited)."`.
   - Calling `SearchAsync(...)` fails fast immediately throwing `AirGappedOperationException` before any API key check or outbound HTTP request.
3. **Agent Toolset Filtering & Dispatch ([`CanonicalAIFunctionFactory.cs`](../../src/NightCodeForge.Agents/Tools/CanonicalAIFunctionFactory.cs) & [`CanonicalTools.cs`](../../src/NightCodeForge.Agents/Tools/CanonicalTools.cs)):**
   - Expose `bool IsWebSearchEnabled => _webSearchService.IsEnabled;` on [`CanonicalTools`](../../src/NightCodeForge.Agents/Tools/CanonicalTools.cs).
   - In [`CanonicalAIFunctionFactory.CreateTools`](../../src/NightCodeForge.Agents/Tools/CanonicalAIFunctionFactory.cs) (which builds the `AITool` list for [`CanonicalToolsetFactory`](../../src/NightCodeForge.Agents/Tools/CanonicalToolsetFactory.cs), [`CrucibleAgentHarness`](../../src/NightCodeForge.Agents/Crucible/CrucibleAgentHarness.cs), and [`IdeationService`](../../src/NightCodeForge.Agents/Ideation/IdeationService.cs)), check `!tools.IsWebSearchEnabled` (instead of only `!tools.IsWebSearchConfigured`). When `false`, omit `web_search` from the advertised tool definitions sent to the LLM.
   - In [`CanonicalTools.WebSearchAsync`](../../src/NightCodeForge.Agents/Tools/CanonicalTools.cs), if the LLM still emits a `web_search` tool call while air-gapped (checked before `IsConfigured`, or catching `AirGappedOperationException`), return `"Error: Web search is unavailable because the system is operating in Air-Gapped mode."`.
4. **Web Search Settings UI ([`WebSearchSettingsTab.razor`](../../src/NightCodeForge.Web/Components/Settings/WebSearchSettingsTab.razor)):**
   - When `AirGappedMode` is active (`!WebSearchService.IsEnabled` with air-gapped reason / `IOptions<SecurityOptions>.Value.AirGappedMode`), display a warning banner:
     `"🛡️ Air-Gapped Mode Active: External web search via ollama.com is currently deactivated to prevent external network traffic."`
   - Disable the endpoint input, API key input, save/reset buttons, and search test controls with descriptive tooltips.

---

## 3. Definition of Done (DoD) Checklist

- [x] Add `AirGappedOperationException` in `NightCodeForge.Core` and bind default [`SecurityOptions`](../../src/NightCodeForge.Core/Security/SecurityOptions.cs) (`Security:AirGappedMode` + `NCF_AIR_GAPPED` override) in `NightCodeForge.Agents`.
- [x] Update [`IWebSearchService`](../../src/NightCodeForge.Agents/Tools/IWebSearchService.cs), [`OllamaWebSearchService`](../../src/NightCodeForge.Agents/Tools/OllamaWebSearchService.cs), and `MockWebSearchService` with `IsEnabled`, `DisabledReason`, and fail-fast `AirGappedOperationException`.
- [x] Update [`CanonicalTools`](../../src/NightCodeForge.Agents/Tools/CanonicalTools.cs) and [`CanonicalAIFunctionFactory`](../../src/NightCodeForge.Agents/Tools/CanonicalAIFunctionFactory.cs) to omit `web_search` from advertised tools when `!IsWebSearchEnabled` and return the air-gapped error message if invoked via `LenientToolDispatcher`.
- [x] Update [`WebSearchSettingsTab.razor`](../../src/NightCodeForge.Web/Components/Settings/WebSearchSettingsTab.razor) with the Air-Gapped warning banner and disabled input state.
- [x] Add unit and bUnit tests in `NightCodeForge.Core.Tests`, `NightCodeForge.Agents.Tests`, and `NightCodeForge.Web.Tests` verifying `AirGappedOperationException`, tool list exclusion, hallucinated call error handling, and `WebSearchSettingsTab.razor` rendering.

---

## 4. Implementation Notes

- **Domain Exception (`NightCodeForge.Core.Exceptions.AirGappedOperationException`):**
  - Inherits from `InvalidOperationException` and provides standard constructor overloads (default, custom message, and inner exception).
  - Explicitly represents policy-prohibited external egress and prevents unintended fallback behavior.
- **Centralized Options Binding (`NightCodeForge.Agents.ServiceCollectionExtensions`):**
  - Bound `SecurityOptions.SectionName` as default configuration so both web and worker/agent hosts obtain typed `IOptions<SecurityOptions>` without duplicating registration logic.
- **Service Guardrails (`OllamaWebSearchService` & `MockWebSearchService`):**
  - `IsEnabled` returns `false` whenever `SecurityOptions.AirGappedMode` is active, regardless of whether credentials are set.
  - `DisabledReason` prioritizes the Air-Gapped deactivation explanation before unconfigured credentials.
  - `SearchAsync` performs immediate fail-fast throwing of `AirGappedOperationException` before inspecting endpoint parameters or issuing HTTP requests.
- **Agent Advertising & Hallucination Mitigation:**
  - `CanonicalAIFunctionFactory.CreateTools` filters out `web_search` whenever `!tools.IsWebSearchEnabled`.
  - `CanonicalTools.WebSearchAsync` catches direct invocations or dispatcher calls when air-gapped and safely returns `"Error: Web search is unavailable because the system is operating in Air-Gapped mode."`.
- **UI Settings Experience (`WebSearchSettingsTab.razor`):**
  - Evaluates air-gapped state via `SecurityOptions.AirGappedMode` and service state.
  - Renders red-tinted warning banner (`.air-gapped-warning-banner`), updates badge to `🛡️ Search Deactivated (Air-Gapped)`, and cleanly disables all credential inputs and connectivity test buttons with informative tooltips.
- **Test Verification:**
  - Unit tests in `AirGappedSecurityTests` (Core), `CanonicalAIFunctionFactoryTests`, `OllamaWebSearchServiceTests`, and `CrucibleTransientRetryTests` (Agents), and bUnit component tests in `SettingsComponentTests` (Web) all pass.

