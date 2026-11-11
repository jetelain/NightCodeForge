# 🖥️ Step 12.18B: OllamaRouter Setup Wizard Integration, Role Recommendation Sync & Custom Protocol Shortcut

> **Feature Implementation Step (Part 2 of 3 — Delegated OllamaRouter Setup Handoff, Recommendations Sync & `ollamarouter://` Shortcut)**  
> *Parent Plan:* [Features & Integrations Roadmap](README.md)  
> *Prerequisite Step:* [Step 12.18A: Installer Core Services](STEP_12_18A_INSTALLER_CORE_HARDWARE_RELEASE_AND_PROVISIONER.md)  
> *Companion Step:* [Step 12.18C: Avalonia OOBE Setup Wizard & Maintenance Hub UI](STEP_12_18C_INSTALLER_AVALONIA_OOBE_WIZARD_AND_MAINTENANCE_HUB.md)  
> *Source Policy:* [AGENTS.md](../../AGENTS.md)  
> *Project Target:* `src/NightCodeForge.Core`, `src/NightCodeForge.Infrastructure`, `src/NightCodeForge.Web`, `src/NightCodeForge.Installer`, `tests/NightCodeForge.Infrastructure.Tests`, `tests/NightCodeForge.Web.Tests`, `tests/NightCodeForge.Installer.Tests` | *Status:* Planned | *Target Framework:* .NET 10 (C# 13)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Objective

Local Ollama port migration (`:11435`), LAN/Cloud target provisioning, GPU/VRAM hardware inspection, candidate model micro-benchmarking, and role-to-model recommendations are now handled directly by **OllamaRouter's standalone 0-VRAM Avalonia Desktop App & 4-Step Setup Wizard (`OllamaRouter.App` / `ollamarouter setup --wizard --wait`)**.

Consequently, NightCodeForge drops duplicate in-app fleet management (formerly Step 12.15) and in-house micro-benchmarking, and instead implements a clean **3-part integration with OllamaRouter**:
1. **Delegated Setup Wizard Handoff (`IOllamaRouterSetupHandoffService`):** Launch `ollamarouter setup --wizard --wait` from `NightCodeForge.Installer` (or CLI), wait for the user to complete OllamaRouter's 4-step wizard, and inspect the exit code (`0` = completed).
2. **Role Recommendation Synchronization (`IOllamaRouterRecommendationSyncService`):** Query `GET /api/router/recommendations` from OllamaRouter to retrieve the benchmark-calibrated native models and context ceilings (`software_architect`, `senior_developer`, `junior_developer`, `agentic_chat`, `basic`) and persist them into NightCodeForge's `config/config.json` user overlay (`LlmProviderOptions`).
3. **Custom Protocol Shortcut (`ollamarouter://`) in NightCodeForge Web UI (`OllamaRouterGatewayCard.razor`):** Replace the obsolete `/monitor` web link with a custom protocol shortcut (`ollamarouter://open` and `ollamarouter://wizard`) that launches `OllamaRouter.App` directly from the browser, alongside a 1-click **"Sync Recommended Models"** button.

---

## 2. Technical Specifications

### 2.1 Delegated Setup Wizard Handoff (`IOllamaRouterSetupHandoffService`)

During initial installation (or when triggered from the NightCodeForge Maintenance Hub), `NightCodeForge.Installer` delegates LLM and fleet setup to OllamaRouter:

1. **Invocation Contract:**
   - Resolves `ollamarouter` on `PATH` or from the platform installation directory (`%LOCALAPPDATA%\OllamaRouter\ollamarouter.exe` on Windows, `~/.ollamarouter/ollamarouter` on Linux/macOS).
   - Executes:
     ```text
     ollamarouter setup --wizard --wait
     ```
   - `ollamarouter` launches `ollamarouter-app --wizard` (rendered in 0-VRAM software mode) and waits for the wizard window to close:
     - **Wizard Step 1 (Local Environment & Ollama Port Check):** Verifies Ollama is installed, configures `OLLAMA_HOST=127.0.0.1:11435` (or `0.0.0.0:11435`), `OLLAMA_FLASH_ATTENTION=1`, and `OLLAMA_KV_CACHE_TYPE=q8_0`, restarts local `ollama` on `:11435`, and starts `ollamarouter-server` on `:11434`.
     - **Wizard Step 2 (Fleet Nodes & Cloud Providers):** Adds local (`:11435`), remote LAN Ollama nodes, Cloud Overflow (`ollama.com`), and/or OpenAI-compatible providers.
     - **Wizard Step 3 (Hardware Discovery & Micro-Benchmarking):** Detects GPU/VRAM per node (`GET /api/router/fleet/hardware`) and runs candidate model micro-benchmarks (`POST /api/router/benchmark`) measuring `tokens/sec` ($\ge 20\text{ t/s}$ viability gate) across the context ladder.
     - **Wizard Step 4 (Role Recommendations & Finish):** Generates and activates role recommendations (`POST /api/router/recommendations/generate` and `POST /api/router/recommendations/apply`), writing `<stateDir>/recommended.json` and exiting with code `0`.
2. **Handoff Result:**
   - When `ollamarouter setup --wizard --wait` exits with code `0`, `NightCodeForge.Installer` immediately runs `IOllamaRouterRecommendationSyncService.SyncRecommendationsToConfigAsync()` to populate NightCodeForge's `config/config.json`.
   - If the process exits with a non-zero code (user cancelled or closed early), the installer surfaces a clear status message allowing the user to retry the wizard or skip local LLM setup.

```csharp
namespace NightCodeForge.Installer.Core.Services;

public sealed record OllamaRouterWizardHandoffResult(
    bool Succeeded,
    int ExitCode,
    string? ErrorMessage,
    RouterRoleRecommendationsSummary? SyncedRecommendations);

public interface IOllamaRouterSetupHandoffService
{
    bool IsOllamaRouterInstalled();
    Task<OllamaRouterWizardHandoffResult> RunSetupWizardAndSyncAsync(
        CancellationToken cancellationToken = default);
}
```

---

### 2.2 Role Recommendation Synchronization (`GET /api/router/recommendations`)

OllamaRouter exposes its active recommendation matrix at `GET /api/router/recommendations` (an open, unauthenticated read-only endpoint):

#### Sample `GET /api/router/recommendations` Response
```json
{
  "generatedAt": "2026-11-11T10:30:00.0000000+00:00",
  "roles": {
    "basic": {
      "role": "basic",
      "model": "gemma4:e4b",
      "recommendedContextWindow": 131072,
      "description": "Fast auxiliary tasks",
      "nodes": [
        { "target": "Local", "contextCeiling": 131072, "baselineContext": null, "tokensPerSecond": 68.4, "vramMB": 16384 }
      ]
    },
    "agentic_chat": {
      "role": "agentic_chat",
      "model": "qwen3.8:27b",
      "recommendedContextWindow": 262144,
      "description": "Interactive ideation and chat",
      "nodes": [
        { "target": "Local", "contextCeiling": 49152, "baselineContext": null, "tokensPerSecond": 24.1, "vramMB": 16384 },
        { "target": "Server", "contextCeiling": 262144, "baselineContext": null, "tokensPerSecond": 42.8, "vramMB": 98304 }
      ]
    },
    "software_architect": {
      "role": "software_architect",
      "model": "qwen3.8:27b",
      "recommendedContextWindow": 262144,
      "description": "Architecture, task review, and code review",
      "nodes": [
        { "target": "Local", "contextCeiling": 49152, "baselineContext": null, "tokensPerSecond": 24.1, "vramMB": 16384 },
        { "target": "Server", "contextCeiling": 262144, "baselineContext": null, "tokensPerSecond": 42.8, "vramMB": 98304 }
      ]
    },
    "senior_developer": {
      "role": "senior_developer",
      "model": "qwen3.8:27b",
      "recommendedContextWindow": 262144,
      "description": "Complex code implementation",
      "nodes": [
        { "target": "Local", "contextCeiling": 49152, "baselineContext": null, "tokensPerSecond": 24.1, "vramMB": 16384 },
        { "target": "Server", "contextCeiling": 262144, "baselineContext": null, "tokensPerSecond": 42.8, "vramMB": 98304 }
      ]
    },
    "junior_developer": {
      "role": "junior_developer",
      "model": "qwen3.6:35b-a3b",
      "recommendedContextWindow": 131072,
      "description": "High-speed MoE implementation",
      "nodes": [
        { "target": "Local", "contextCeiling": 131072, "baselineContext": 65536, "tokensPerSecond": 54.0, "vramMB": 16384 }
      ]
    }
  }
}
```

#### Role Mapping to NightCodeForge (`LlmProviderOptions` & `config/config.json`)
| NightCodeForge Setting | OllamaRouter Role Lookup Priority | Purpose |
| :--- | :--- | :--- |
| **`ProviderType`** | `LlmProviderType.OllamaRouter` | Routes all local/mesh inference through OllamaRouter (`http://localhost:11434`). |
| **`ArchitectModelId`** | `roles["software_architect"].Model` $\rightarrow$ `roles["agentic_chat"].Model` | Used for `triage`, `ideation`, `task-review`, `code-review-and-fix`, and `Difficult` tier escalation. |
| **`CoderModelId`** | `roles["senior_developer"].Model` $\rightarrow$ `roles["junior_developer"].Model` | Used for `implementation` (`Standard` tier). |
| **`DefaultModelId`** | `roles["agentic_chat"].Model` $\rightarrow$ `ArchitectModelId` $\rightarrow$ `CoderModelId` | Fallback model across pipeline stages. |
| **`ContextWindowTokens`** | `roles["software_architect"].RecommendedContextWindow` (min `131072` when available, fallback `131072`) | Baseline context window (while OllamaRouter dynamically clamps/injects per-node context ceilings at runtime). |

> **Why Native Model Names Matter:** OllamaRouter's recommendation engine assigns **native model names** (e.g., `qwen3.8:27b`) rather than synthetic aliases so `agentic_chat`, `software_architect`, and `senior_developer` share the exact same hot model in GPU VRAM without triggering Ollama model evictions, while OllamaRouter enforces per-node context ceilings (`<stateDir>/recommended.json`) automatically during Stage-1 routing and `num_ctx` sizing.

---

### 2.3 Custom Protocol Shortcut (`ollamarouter://`) & Web UI Integration (`OllamaRouterGatewayCard.razor`)

1. **Custom URI Protocol Contract (`ollamarouter://`):**
   - Registered in the OS by the OllamaRouter installation/setup process (`HKCU\Software\Classes\ollamarouter` on Windows, `x-scheme-handler/ollamarouter` desktop entry on Linux):
     - `ollamarouter://open` — Opens the `OllamaRouter.App` desktop dashboard (Monitor & Savings, Fleet & Cloud Targets, Model Cartography & Preload, Settings & API Keys).
     - `ollamarouter://wizard` — Opens `OllamaRouter.App` directly in the 4-step Setup Wizard (`--wizard`).
2. **Updates to [`OllamaRouterGatewayCard.razor`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Settings/OllamaRouterGatewayCard.razor):**
   - Replace the obsolete `<a href="@GetRouterMonitorUrl()" target="_blank">↗ Open Router Monitor</a>` link (`${baseUrl}/monitor`, which was removed when `ollamarouter-server` became headless) with:
     - **`🖥️ Open OllamaRouter App`** (`href="ollamarouter://open"`, without `target="_blank"` so the browser invokes the registered OS protocol handler cleanly without leaving a blank tab).
     - **`🧙 Run Router Setup Wizard`** (`href="ollamarouter://wizard"`).
     - **`🔄 Sync Recommended Models`** button: queries `GET /api/router/recommendations` via `ILlmGatewayService` / `IOllamaRouterRecommendationSyncService`, populates `_defaultModelId`, `_architectModelId`, and `_coderModelId` (enabling `_differentiateRoles` when both Architect and Coder models are returned), and displays a toast summarizing the imported role models.
   - Per `AGENTS.md`, protocol URI constants and recommendation-to-options mapping logic must reside in a testable service/helper class outside `.razor` `@code` blocks.

---

## 3. Implementation Checklist & Deliverables

- [ ] Add `OllamaApiPaths.RouterRecommendations = "/api/router/recommendations"` and recommendation response DTOs (`RouterRecommendationsDto`, `RouterRoleRecommendationDto`, `RouterNodeRoleProfileDto`), plus `GetRecommendationsAsync` on `ILlmGatewayService` / `OllamaRouterGatewayService`.
- [ ] Implement `IOllamaRouterRecommendationSyncService` to map `GET /api/router/recommendations` onto `LlmProviderOptions` (`ArchitectModelId`, `CoderModelId`, `DefaultModelId`, `ContextWindowTokens`) and persist via `ILlmConfigurationWriter`.
- [ ] Update `OllamaRouterGatewayCard.razor` to replace the removed `/monitor` link with `ollamarouter://open` (**Open OllamaRouter App**), `ollamarouter://wizard` (**Run Setup Wizard**), and a **Sync Recommended Models** action.
- [ ] Implement `IOllamaRouterSetupHandoffService` in `NightCodeForge.Installer` invoking `ollamarouter setup --wizard --wait` and syncing recommendations into `config/config.json` upon exit code `0`.
- [ ] Add unit tests in `NightCodeForge.Infrastructure.Tests`, `NightCodeForge.Web.Tests`, and `NightCodeForge.Installer.Tests` covering `/api/router/recommendations` deserialization, role-to-options mapping, `ollamarouter://` shortcut rendering, and `ollamarouter setup --wizard --wait` exit-code handling.
