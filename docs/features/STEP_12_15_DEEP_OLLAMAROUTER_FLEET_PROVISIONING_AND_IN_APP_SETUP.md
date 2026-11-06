# 🌐 Step 12.15: Deep OllamaRouter Fleet Provisioning & In-App Setup

> **Phase 12 Feature Specification (Fleet Infrastructure, In-App Management & Security)**  
> *Parent Plan:* [Features & Integrations Roadmap](README.md)  
> *Target Projects:* [`src/NightCodeForge.Core`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core), [`src/NightCodeForge.Infrastructure`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Infrastructure), [`src/NightCodeForge.Web`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web), [`tests/NightCodeForge.Web.Tests`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/tests/NightCodeForge.Web.Tests)  
> *Status:* Planned | *Target Framework:* .NET 10 (C# 13)  
> *Dependencies & OpenAPI References:* [Step 11.33: Unified LLM Provider & Dynamic Fleet Integration](../archive/phase-11/STEP_11_33_UNIFIED_LLM_PROVIDER_DYNAMIC_CONTEXT_AND_FLEET_INTEGRATION.md), [Step 12.5: Dual Model Role Configuration](STEP_12_5_DUAL_MODEL_ROLE_CONFIGURATION_AND_DIFFICULTY_ESCALATION.md), [OllamaRouter Management OpenAPI 3.1.1 Spec](http://localhost:11434/api/router/openapi.json)  
> *Aligns With:* [AGENTS.md](../../AGENTS.md), [docs/PLAN.md](../PLAN.md)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Summary & Problem Statement

In **Step 11.33**, NightCodeForge established baseline fleet observability: reading `/api/router/info` and `/api/router/fleet` to query cluster topology, target liveness, and dynamic context limits.

However, configuring the multi-node fleet—adding local or remote GPU compute targets, updating hardware hints, managing cloud overflow endpoints and API keys, or fine-tuning dynamic context and resilience thresholds—previously required shell access to the host running OllamaRouter to edit JSON configuration files on disk.

OllamaRouter has introduced a comprehensive, secure Management REST API documented under its **OpenAPI 3.1.1 specification** (`GET /api/router/openapi.json`). This API operates on OllamaRouter's reloadable user layer (`config/config.json` and `config/api-keys.json`), hot-applying configuration updates without daemon restarts and enforcing Bearer API key authentication.

**Step 12.15 delivers Deep In-App OllamaRouter Provisioning directly from the NightCodeForge Web UI (`/settings`)**:
1. **Zero-Terminal Fleet Target Management (`POST`/`PUT`/`DELETE /api/router/targets` + `GET /api/router/config`):** Add, probe, configure, enable/disable, and remove LAN compute daemons.
2. **Pre-Flight Target Probing (`testConnectionFirst = true`):** When adding or testing a target, OllamaRouter tests reachability, measures round-trip latency (`latencyMs`), queries discovered models, and evaluates target liveness before committing to disk.
3. **Live Configuration Updates (`GET`/`PUT /api/router/config`):** Read and partially update runtime sections (`cloud`, `dynamicContext`, `recording`, `resilience`, `pricing`) with immediate in-memory activation and durable disk persistence.
4. **API Key Security, Local DPAPI Default-Key Discovery & Management (`/api/router/api-keys`):** Manage bearer tokens required to control protected router endpoints, including automatic discovery and DPAPI decryption of the local OllamaRouter default key (`%LOCALAPPDATA%\OllamaRouter\config\default-key.dat`), key generation, expiration auditing, revocation, and default-key rotation.
5. **Cost & Token Pricing Calibration:** Configure per-million token input/output pricing and currency symbol to project savings and track cloud expenditures.

---

## 2. OllamaRouter Management REST API Contracts

All management routes operate over HTTP on the configured OllamaRouter endpoint (e.g. `http://localhost:11434` or remote gateway). Protected management routes require HTTP header `Authorization: Bearer <ApiKey>` (or `X-Api-Key: <ApiKey>`). When `apiSecurity.requireKeyForAllEndpoints` is enabled on the router, all router and inference routes require authentication.

### 2.1 Target Node Provisioning Routes

> **Note on Reading Targets:** OllamaRouter does not expose a separate `GET /api/router/targets` route. Configured target definitions (`RouterTargetConfigDto`, excluding the `Cloud` relay pseudo-target) are returned in the `targets` array of `GET /api/router/config` (protected), while live target liveness, latency, hardware, and in-flight execution state are returned by `GET /api/router/fleet` (via `ILlmGatewayService.GetGatewayStatusAsync`).

| Method | Route | Description | Auth Required |
| :--- | :--- | :--- | :--- |
| `POST` | `/api/router/targets` | Add and optionally probe a new compute target. Returns `AddTargetResponse` (`201 Created` on success, `400` on validation error, `409` if duplicate name, `422` if connection probe fails, `500` on persistence failure). | Yes |
| `PUT` | `/api/router/targets/{name}` | Partially update an existing target (`url`, `enabled`, `hardwareHint`, `maxParallel`). Returns `UpdateTargetResponse` (`200 OK`, `400` if all fields null/invalid, `404` if unknown target, `500` on persistence failure). | Yes |
| `DELETE` | `/api/router/targets/{name}` | Remove a target from routing and persist to disk. Returns `DeleteTargetResponse` (`200 OK`, `404` if unknown target, `500` on persistence failure). | Yes |

#### Add Target Payload (`AddTargetRequest`)
```json
{
  "name": "Server-Rack-1",
  "url": "http://192.168.1.120:11434",
  "maxParallel": 2,
  "testConnectionFirst": true,
  "hardwareHint": "96gb"
}
```

#### Add Target Response (`AddTargetResponse`)
```json
{
  "success": true,
  "connectionTested": true,
  "liveness": "Online",
  "modelsDiscovered": 8,
  "target": {
    "name": "Server-Rack-1",
    "url": "http://192.168.1.120:11434",
    "enabled": true,
    "hardwareHint": "96gb",
    "maxParallel": 2
  },
  "latencyMs": 14,
  "error": null
}
```

### 2.2 Live Router Configuration Routes

| Method | Route | Description | Auth Required |
| :--- | :--- | :--- | :--- |
| `GET` | `/api/router/config` | Read full live configuration (`targets`, `cloud`, `dynamicContext`, `recording`, `resilience`, `pricing`, `apiSecurity`). No secret key material is ever exposed. | Yes |
| `PUT` | `/api/router/config` | Partial per-section update. Any non-null section (`cloud`, `dynamicContext`, `recording`, `resilience`, `pricing`) is merged into `config/config.json` and hot-applied to memory immediately, echoing `UpdateRouterConfigResponse` with the full updated `config`. | Yes |

#### Live Configuration Response (`RouterConfigResponse` from `GET /api/router/config`)
```json
{
  "targets": [
    {
      "name": "Server-Rack-1",
      "url": "http://192.168.1.120:11434",
      "enabled": true,
      "hardwareHint": "96gb",
      "maxParallel": 2
    }
  ],
  "cloud": {
    "enabled": true,
    "defaultModel": "qwen2.5-coder:32b",
    "preferredRelayTarget": "Local",
    "overflowConfigured": true
  },
  "dynamicContext": {
    "enabled": true,
    "denseClampEnabled": true,
    "autoShrinkCooldownTurns": 3,
    "autoShrinkIdleTimeoutSeconds": 60
  },
  "recording": {
    "enabled": false,
    "outputDirectory": null,
    "maxFiles": 500
  },
  "resilience": {
    "maxConsecutiveFailures": 3,
    "consecutiveFailuresTokenTolerance": 4000
  },
  "pricing": {
    "promptPricePerMillion": 0.20,
    "completionPricePerMillion": 0.80,
    "pricePerMillion": 0.50,
    "currency": "$"
  },
  "apiSecurity": {
    "requireKeyForAllEndpoints": false,
    "defaultKeyRotationHours": 24
  }
}
```

#### Partial Configuration Update Payload (`UpdateRouterConfigRequest` for `PUT /api/router/config`)
> **Note on `cloud` section:** OllamaRouter's cloud overflow delegates to the selected relay node's `ollama.com` sign-in (`preferredRelayTarget` and `defaultModel`). It does not take external OpenAI/OpenRouter endpoints or API keys; passing `""` for `defaultModel` clears it to `null`, and passing `""` for `preferredRelayTarget` resets it to `"Local"`.

```json
{
  "cloud": {
    "enabled": true,
    "defaultModel": "qwen2.5-coder:32b",
    "preferredRelayTarget": "Local"
  },
  "dynamicContext": {
    "enabled": true,
    "denseClampEnabled": true,
    "autoShrinkCooldownTurns": 3,
    "autoShrinkIdleTimeoutSeconds": 60
  },
  "recording": {
    "enabled": false,
    "outputDirectory": null,
    "maxFiles": 500
  },
  "resilience": {
    "maxConsecutiveFailures": 3,
    "consecutiveFailuresTokenTolerance": 4000
  },
  "pricing": {
    "promptPricePerMillion": 0.20,
    "completionPricePerMillion": 0.80,
    "pricePerMillion": 0.50,
    "currency": "$"
  }
}
```

### 2.3 API Key Management Routes & Local Default-Key Discovery (`default-key.dat`)

| Method | Route | Description | Auth Required |
| :--- | :--- | :--- | :--- |
| `GET` | `/api/router/api-keys` | List masked user API keys (`keys: [{ name, createdAt, expiresAt? }]`). Never returns hashes or raw keys. Client derives `IsExpired` from `expiresAt <= UtcNow`. | Yes |
| `POST` | `/api/router/api-keys` | Generate a new named API key (`name`, optional `validFor` hours `>= 1`). Returns `ApiKeyCreatedResponse` (`{ success, name, key, createdAt, expiresAt? }`) with the raw base64url `key` exactly once in the `201 Created` response (`400` invalid input, `409` duplicate name). | Yes |
| `DELETE` | `/api/router/api-keys/{name}` | Revoke and delete a user API key. Returns `ApiKeyDeletedResponse` (`{ success, error? }`, `200 OK` or `404 Not Found`). | Yes |
| `POST` | `/api/router/api-keys/default/rotate` | Immediately rotate the default API key (previous default key is invalidated instantly with no grace period; returns `ApiKeyRotatedResponse` `{ success, key, generatedAt }`). | Yes |

#### Local Default-Key Discovery (`%LOCALAPPDATA%\OllamaRouter\config\default-key.dat`)
When OllamaRouter runs on the same machine under the same Windows user account, it persists its auto-generated default API key to:
`%LOCALAPPDATA%\OllamaRouter\config\default-key.dat` (e.g. `C:\Users\<User>\AppData\Local\OllamaRouter\config\default-key.dat`, resolved via `Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OllamaRouter", "config", "default-key.dat")`).

- **At-Rest Protection & Envelope Format:**
  - On Windows (`OperatingSystem.IsWindows()`), the file bytes are encrypted with Windows DPAPI via `ProtectedData.Unprotect(rawBytes, optionalEntropy: null, scope: DataProtectionScope.CurrentUser)` (`System.Security.Cryptography.ProtectedData`). On non-Windows platforms, the file contains plain UTF-8 bytes (`0600` permissions).
  - Unwrapping the payload yields a UTF-8 JSON envelope:
    ```json
    {
      "key": "<43-char base64url key>",
      "generatedAt": "2026-11-06T16:00:00.0000000+00:00"
    }
    ```
- **Resolution & Rotation Recovery Behavior in NightCodeForge:**
  1. **Explicit Key Precedence:** If `LlmProviderOptions.ApiKey` is explicitly configured in NightCodeForge (`config/config.json`), that key is used.
  2. **Zero-Config Local Fallback:** When `LlmProviderOptions.ApiKey` is null/empty and the local `default-key.dat` file exists and can be unwrapped, NightCodeForge automatically uses the `key` from `default-key.dat` for Bearer authentication against OllamaRouter.
  3. **Seamless Rotation Reload on `401 Unauthorized`:** Because OllamaRouter periodically rotates `default-key.dat` (every `apiSecurity.defaultKeyRotationHours`, default 24h) or when `POST /api/router/api-keys/default/rotate` is called, any cached default key becomes invalid immediately. When authenticating via the local `default-key.dat` (or on a `401` response), NightCodeForge re-reads `default-key.dat` from disk so rotated default keys are picked up automatically without manual reconfiguration.

---

## 3. UI & Experience Specifications

### 3.1 Target Node Management (`OllamaRouterFleetManager.razor`)

Inside `Settings.razor` / `OllamaRouterGatewayCard.razor` under the **OllamaRouter** tab:
1. **Compute Nodes Grid:**
   - Combines editable target configurations from `GET /api/router/config` (`RouterTargetConfigDto`) with live runtime status from `ILlmGatewayService.GetGatewayStatusAsync()` (`GET /api/router/fleet`). The `Cloud` relay pseudo-target reported by `/api/router/fleet` is displayed read-only and excluded from target CRUD mutations.
   - Visual cards for each configured node displaying Name, URL, Status Badge (`Online` in emerald, `Offline` in rose, `Probing` in amber, `Disabled` in gray), Latency (`14ms`), and In-Flight Request slots (`0 / 2`).
   - Hardware badge: `8 GB (Mobile/Laptop)`, `16 GB (Workstation)`, `96 GB (High-VRAM Server)`, or custom `hardwareHint`.
   - **Inline Actions:**
     - **Toggle Enable/Disable:** Switches target availability in the router via `PUT /api/router/targets/{name}` with `{ "enabled": !target.Enabled }`.
     - **Refresh / Re-probe:** Refreshes fleet status (`GetGatewayStatusAsync`) and router config (`GetRouterConfigAsync`) to update liveness and latency badges.
     - **Edit Node Modal:** Edits `url`, `maxParallel` (`>= 1`), and `hardwareHint` (sending `""` to clear the hint) via `PUT /api/router/targets/{name}`.
     - **Delete Node:** Confirms and removes the target node via `DELETE /api/router/targets/{name}`.
2. **Add Node Modal (`AddComputeNodeModal.razor`):**
   - Inputs:
     - Target Name (non-empty, `<= 64` chars, no `/`, `\`, `:`, not `"Cloud"`, e.g. `gpu-server-01`).
     - Target URL (absolute `http`/`https` URI, e.g. `http://192.168.1.150:11434`).
     - Max Parallel Concurrency (`>= 1`, default: 1; 2–4 for multi-GPU).
     - Hardware Hint Selector (`8gb`, `16gb`, `24gb`, `48gb`, `96gb`, `Custom`).
     - "Test Connection First" checkbox (default: checked).
   - "Test & Add" Button:
     - On click, invokes `POST /api/router/targets`.
     - If probe succeeds (`201 Created`), displays discovered models count and latency, closing modal.
     - If probe fails (`422 Unprocessable Entity`), displays error details with option to "Save anyway without testing" (re-submitting with `testConnectionFirst: false`).

### 3.2 Cloud Overflow & API Key Management Cards

1. **Cloud Overflow Card (`CloudOverflowConfigCard.razor`):**
   - Displays read-only status badge for `overflowConfigured` (indicating whether the relay target is signed in to Ollama Cloud).
   - Toggle switch to enable/disable cloud overflow (`enabled`).
   - Default cloud model input (`defaultModel`, e.g. `qwen2.5-coder:32b`).
   - Preferred Relay Target selector (`preferredRelayTarget`, selecting from configured fleet targets or `"Local"`).
   - "Save Cloud Settings" button issuing `PUT /api/router/config` with `cloud` (`UpdateCloudConfigRequest`) payload.
2. **API Keys Card (`OllamaRouterApiKeysCard.razor`):**
   - **Local Default Key Status Banner:** Detects whether `%LOCALAPPDATA%\OllamaRouter\config\default-key.dat` is available and readable via DPAPI (`DataProtectionScope.CurrentUser`), displaying a status badge (e.g., `Local Default Key Detected (generated <timestamp>)`) and indicating whether NightCodeForge is currently using the auto-discovered local default key or an explicit `LlmProviderOptions.ApiKey` override.
   - Active router management API key input bound to `LlmProviderOptions.ApiKey` (persisted to `config/config.json` via `ILlmConfigurationWriter.SaveProviderConfigurationAsync`, or left blank to use the local `default-key.dat` fallback) with masked password input, show/hide toggle, and a "Use Local Default Key" action that clears the manual override.
   - Displays read-only `apiSecurity` status (`requireKeyForAllEndpoints`, `defaultKeyRotationHours`) from `GET /api/router/config`.
   - Table of user keys on the router (`name`, `createdAt`, `expiresAt`, and derived `IsExpired` badge).
   - "Create New API Key" modal with key `name` and optional expiry hours (`validFor >= 1`). When created (`201 Created`), displays a one-time modal with the copyable raw `key` (and an option to apply it immediately as NightCodeForge's active `LlmProviderOptions.ApiKey`).
   - "Rotate Default Key" button with confirmation alert: when rotated via `POST /api/router/api-keys/default/rotate`, displays the new `key` in a one-time modal, automatically refreshes the local `default-key.dat` cache if using the local default key, and offers to update `LlmProviderOptions.ApiKey` via `ILlmConfigurationWriter` if a manual override was configured (since the previous default key is invalidated immediately without a grace period).

### 3.3 Dynamic Context, Resilience & Pricing Cards

1. **Dynamic Context Card:**
   - Toggle for `Enabled` and `DenseClampEnabled`.
   - Sliders/numeric inputs for `AutoShrinkCooldownTurns` (`>= 0`, e.g. 1 to 10 turns, default 3) and `AutoShrinkIdleTimeoutSeconds` (`>= 0`, e.g. 15 to 300s, default 60s).
2. **Resilience & Circuit Breaker Card:**
   - Numeric input for `MaxConsecutiveFailures` (`>= 0`, default 3) before node quarantine.
   - Numeric input for `ConsecutiveFailuresTokenTolerance` (`>= 0`, default 4000 tokens).
3. **Pricing Calibration Card:**
   - Inputs for `PromptPricePerMillion`, `CompletionPricePerMillion`, and fallback `PricePerMillion` (`>= 0`) with non-empty currency symbol selector (`$`, `€`, `£`, `¥`).

### 3.4 UI & Presentation Separation (`AGENTS.md`)

Per `AGENTS.md`, Razor components (`.razor`) must remain focused on presentation, layout, and user interaction. Merging `GET /api/router/config` targets with `GET /api/router/fleet` runtime status, validating target names/URLs, parsing HTTP error responses (`400`/`401`/`404`/`409`/`422`/`500`), and computing key expiration states must be encapsulated in dedicated service/helper classes outside `@code` blocks so they can be unit-tested without UI rendering context.

---

## 4. Definition of Done (DoD) Checklist

- [ ] **Phase 1: Gateway Models, Local Default-Key Reader & Management Client Service in `NightCodeForge.Core` & `NightCodeForge.Infrastructure`:**
  - [ ] Add `ApiKey` (`string?`) to `LlmProviderOptions` (`NightCodeForge.Core.Inference`) and `LlmEndpointOptions` (`NightCodeForge.Infrastructure.Inference`), persisting updates to `config/config.json` via `ILlmConfigurationWriter`.
  - [ ] Implement a local default-key reader (`IOllamaRouterDefaultKeyProvider` / `OllamaRouterDefaultKeyProvider`) that locates `%LOCALAPPDATA%\OllamaRouter\config\default-key.dat`, unprotects the bytes via Windows DPAPI (`ProtectedData.Unprotect` with `DataProtectionScope.CurrentUser` on Windows, plain UTF-8 on non-Windows), and parses the `{ key, generatedAt }` JSON envelope.
  - [ ] Attach `Authorization: Bearer <ApiKey>` in `LlmEndpointHttpClient` / `OllamaRouterManagementService` and `OllamaRouterChatClientFactory` using `LlmProviderOptions.ApiKey` when configured, or falling back to the local `default-key.dat` key (and re-reading `default-key.dat` on `401 Unauthorized` when the default key rotates).
  - [ ] Author DTO models matching the OllamaRouter OpenAPI 3.1.1 schemas: `AddTargetRequest`, `AddTargetResponse`, `RouterTargetConfigDto`, `UpdateTargetRequest`, `UpdateTargetResponse`, `DeleteTargetResponse`, `RouterConfigResponse`, `UpdateRouterConfigRequest`, `UpdateRouterConfigResponse`, `RouterCloudConfigDto`, `UpdateCloudConfigRequest`, `RouterDynamicContextConfigDto`, `RouterRecordingConfigDto`, `RouterResilienceConfigDto`, `RouterPricingConfigDto`, `ApiSecurityConfigDto`, `ApiKeyInfoResponse` (with computed `IsExpired`), `ApiKeysListResponse`, `ApiKeyCreateRequest`, `ApiKeyCreatedResponse`, `ApiKeyDeletedResponse`, `ApiKeyRotatedResponse`.
  - [ ] Introduce `IOllamaRouterManagementService` and `OllamaRouterManagementService` (or extend the router gateway service) with target management methods:
    - `Task<AddTargetResponse> AddTargetAsync(AddTargetRequest request, CancellationToken ct = default);`
    - `Task<UpdateTargetResponse> UpdateTargetAsync(string name, UpdateTargetRequest request, CancellationToken ct = default);`
    - `Task<DeleteTargetResponse> DeleteTargetAsync(string name, CancellationToken ct = default);`
  - [ ] Add live configuration methods:
    - `Task<RouterConfigResponse?> GetRouterConfigAsync(CancellationToken ct = default);`
    - `Task<UpdateRouterConfigResponse> UpdateRouterConfigAsync(UpdateRouterConfigRequest request, CancellationToken ct = default);`
  - [ ] Add API key management methods:
    - `Task<ApiKeysListResponse?> GetApiKeysAsync(CancellationToken ct = default);`
    - `Task<ApiKeyCreatedResponse> CreateApiKeyAsync(ApiKeyCreateRequest request, CancellationToken ct = default);`
    - `Task<ApiKeyDeletedResponse> DeleteApiKeyAsync(string name, CancellationToken ct = default);`
    - `Task<ApiKeyRotatedResponse> RotateDefaultApiKeyAsync(CancellationToken ct = default);`
  - [ ] Add unit tests in `NightCodeForge.Infrastructure.Tests` covering all endpoints, Bearer authentication, local `default-key.dat` envelope parsing/DPAPI unwrapping, `401` rotation reload, and error status code handling (`400`, `401`, `404`, `409`, `422`, `500`).

- [ ] **Phase 2: Blazor Web UI Components in `NightCodeForge.Web`:**
  - [ ] Implement `OllamaRouterFleetManager.razor` combining live `GetGatewayStatusAsync()` telemetry with `GetRouterConfigAsync()` target definitions, liveness badges, latency pills, and quick enable/disable toggle.
  - [ ] Implement `AddComputeNodeModal.razor` with live connectivity probe (`testConnectionFirst`), `422` "Save anyway without testing" fallback, and hardware hint dropdown.
  - [ ] Implement `CloudOverflowConfigCard.razor` for `enabled`, `defaultModel`, `preferredRelayTarget`, and `overflowConfigured` status display.
  - [ ] Implement `OllamaRouterApiKeysCard.razor` with local `default-key.dat` detection status, `LlmProviderOptions.ApiKey` override configuration, user key listing, creation modal, one-time raw `key` display, and default key rotation with 1-click NCF key sync.
  - [ ] Implement `OllamaRouterTuningCard.razor` for dynamic context sliders, resilience thresholds, and token pricing rates.
  - [ ] Integrate all cards into `Settings.razor` / `OllamaRouterGatewayCard.razor` under the OllamaRouter tab.
  - [ ] Author component unit tests in `NightCodeForge.Web.Tests`.
