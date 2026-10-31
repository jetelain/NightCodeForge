# 🛡️ Step 12.20A: Air-Gapped Mode & External Web Search Deactivation

> **Feature Implementation Step (Part 1 of 2 — Air-Gapped Configuration, Agent Tool Filtering & Settings Banner)**  
> *Parent Plan:* [Features & Integrations Roadmap](README.md)  
> *Companion Step:* [Step 12.20B: `mdfetch` Documentation Whitelist & SSRF Governance](STEP_12_20B_MDFETCH_DOCUMENTATION_WHITELIST_AND_SSRF_GOVERNANCE.md)  
> *Related Steps:* [Step 12.19: Air-Gapped Offline Web App Hardening](../archive/phase-12/STEP_12_19_AIR_GAPPED_OFFLINE_WEB_APP_HARDENING.md)  
> *Source Policy:* [AGENTS.md](../../AGENTS.md)  
> *Project Target:* `src/NightCodeForge.Core`, `src/NightCodeForge.Agents`, `src/NightCodeForge.Web`, `tests/NightCodeForge.Core.Tests`, `tests/NightCodeForge.Web.Tests` | *Status:* Planned | *Target Framework:* .NET 10 (C# 13)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Objective

1. **Air-Gapped Search Deactivation (Zero Cloud Phone-Home):** Automatically deactivate external web searching (`https://ollama.com/api/web_search`) when NightCodeForge is running in an air-gapped, isolated, or offline environment to prevent connection timeouts, pipeline stalls, and data leakage.
2. **Unified Air-Gapped Configuration:** Expose a centralized `NetworkSecurity:AirGappedMode` configuration option in `appsettings.json` and environment variable override (`NCF_AIR_GAPPED=true`).
3. **Fail-Fast Agent & Tooling Guardrails:** Ensure [`CrucibleAgentHarness`](../../src/NightCodeForge.Agents/Crucible/CrucibleAgentHarness.cs) and [`LenientToolDispatcher`](../../src/NightCodeForge.Agents/Tools/LenientToolDispatcher.cs) omit `web_search` from tool sets when disabled, and fail fast with `AirGappedOperationException` if `SearchAsync` is called directly.
4. **Settings UX Transparency:** Update [`WebSearchSettingsTab.razor`](../../src/NightCodeForge.Web/Components/Shared/Settings/WebSearchSettingsTab.razor) to display an informative Air-Gapped status banner, disabling cloud provider inputs with clear explanatory tooltips.

*(Internal documentation fetching governance for `mdfetch` and `MdfetchSecuritySettingsTab.razor` are specified in [Step 12.20B](STEP_12_20B_MDFETCH_DOCUMENTATION_WHITELIST_AND_SSRF_GOVERNANCE.md).)*

---

## 2. Architecture & Technical Specification

### 2.1 Air-Gapped Network Configuration Model
Add `NetworkSecurityOptions` in `NightCodeForge.Core`:
```json
{
  "NetworkSecurity": {
    "AirGappedMode": false
  }
}
```
When `NetworkSecurity:AirGappedMode` is `true` (or environment variable `NCF_AIR_GAPPED=true` is set), the application enters strict air-gapped mode.

### 2.2 Web Search Deactivation Logic
1. **`IWebSearchService` Contract:**
   - Expose `bool IsEnabled { get; }` and `string? DisabledReason { get; }`.
2. **Behavior when `AirGappedMode` is active:**
   - `IsEnabled` returns `false`.
   - `DisabledReason` returns `"Web search is disabled in Air-Gapped mode (external cloud search to ollama.com is prohibited)."`.
   - Calling `SearchAsync(...)` fails fast immediately throwing `AirGappedOperationException`.
3. **Agent Harness Tool Filtering:**
   - In [`CrucibleAgentHarness`](../../src/NightCodeForge.Agents/Crucible/CrucibleAgentHarness.cs) (and `IdeationService`), check `IWebSearchService.IsEnabled`. When `false`, omit `web_search` from the tool definitions sent to the LLM.
   - If the LLM still emits a `web_search` call, return `"Error: Web search is unavailable because the system is operating in Air-Gapped mode."`.
4. **Web Search Settings UI ([`WebSearchSettingsTab.razor`](../../src/NightCodeForge.Web/Components/Shared/Settings/WebSearchSettingsTab.razor)):**
   - When `AirGappedMode` is active, display a warning banner:
     `"🛡️ Air-Gapped Mode Active: External web search via ollama.com is currently deactivated to prevent external network traffic."`
   - Disable the provider selector, API key input, and search test button with descriptive tooltips.

---

## 3. Definition of Done (DoD) Checklist

- [ ] Add `NetworkSecurityOptions` (`AirGappedMode` + `NCF_AIR_GAPPED=true` override) and `AirGappedOperationException` in `NightCodeForge.Core`.
- [ ] Update `IWebSearchService` and `OllamaWebSearchService` with `IsEnabled`, `DisabledReason`, and fail-fast `AirGappedOperationException`.
- [ ] Update `CrucibleAgentHarness` and `LenientToolDispatcher` to omit/reject `web_search` when disabled.
- [ ] Update `WebSearchSettingsTab.razor` with the Air-Gapped warning banner and disabled input state.
- [ ] Add unit and bUnit tests verifying `AirGappedOperationException`, tool list exclusion, and `WebSearchSettingsTab.razor` rendering.

