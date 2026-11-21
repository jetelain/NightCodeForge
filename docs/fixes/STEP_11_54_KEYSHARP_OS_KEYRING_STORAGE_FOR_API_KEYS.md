# 🔐 Step 11.54: Store OpenAI-Compatible & Ollama Cloud Search API Keys in OS Keyring via `Pmad.KeySharp`

> **Parent Plan:** [docs/fixes/README.md](README.md)  
> **Roadmap Stream:** `v0.1.0` — **1. Common Trunk** ([docs/PLAN.md](../PLAN.md))  
> **Target Framework:** .NET 10 (C# 13)  
> **Status:** Planned (v0.1.0 Common Trunk)  
> **Target Projects:** `NightCodeForge.Core`, `NightCodeForge.Infrastructure`, `NightCodeForge.Agents`, `NightCodeForge.Web`, `NightCodeForge.Core.Tests`, `NightCodeForge.Infrastructure.Tests`, `NightCodeForge.Agents.Tests`, `NightCodeForge.Web.Tests`  
> **Dependencies:** `Pmad.KeySharp` (already referenced in [`NightCodeForge.Infrastructure.csproj`](../../src/NightCodeForge.Infrastructure/NightCodeForge.Infrastructure.csproj), introduced in [Step 12.11A](../archive/phase-12/STEP_12_11A_GITHUB_CLIENT_DEVICE_AUTH_AND_SETTINGS.md))  
> **Aligns With:** [`AGENTS.md`](../../AGENTS.md), [`docs/PLAN.md`](../PLAN.md)

> [!IMPORTANT]
> **Specification Disclaimer:** This document is a behavioral specification. It defines required behaviors, architectural invariants, security boundaries, and verification criteria. Specific internal type names, method signatures, and code structure remain at the discretion of the implementer within these constraints.

---

## 1. Context & Motivation

NightCodeForge already protects GitHub App OAuth credentials in the native operating system keyring via `Pmad.KeySharp` ([`GitHubTokenStore`](../../src/NightCodeForge.Infrastructure/GitHub/GitHubTokenStore.cs), [Step 12.11A](../archive/phase-12/STEP_12_11A_GITHUB_CLIENT_DEVICE_AUTH_AND_SETTINGS.md)) and protects local companion API authentication using `Pmad.LocalApi.Security` (`default-key.dat`, [Step 12.37](../archive/phase-12/STEP_12_37_COMPANION_API_KEY_STORE_AND_ASSAY_HANDOFF_ENDPOINTS.md)).

However, two external service credentials are still persisted in plaintext on disk:

1. **OpenAI-Compatible API Key (`OpenAiCompatibleOptions.ApiKey`):** Serialized in plaintext to `<ContentRoot>/config/config.json` by [`LlmConfigurationWriter`](../../src/NightCodeForge.Infrastructure/Configuration/LlmConfigurationWriter.cs) and stored on [`OpenAiCompatibleOptions`](../../src/NightCodeForge.Core/Inference/OpenAiCompatibleOptions.cs).
2. **Ollama Cloud Search API Key (`WebSearchSettings.ApiKey`):** Serialized in plaintext to the SQLite `AppDbContext.SystemSettings` table (`Key = "WebSearchSettings"`) by [`WebSearchSettingsService`](../../src/NightCodeForge.Infrastructure/Data/WebSearchSettingsService.cs) and exposed on [`WebSearchSettings`](../../src/NightCodeForge.Core/Domain/WebSearchSettings.cs).

Persisting third-party API keys in plaintext JSON configuration files or SQLite tables increases accidental disclosure risk (local backups, diagnostics/log exports, shared configs, or SQLite database inspection).

### Why This Belongs in `v0.1.0` — 1. Common Trunk

Positioning this fix in **`v0.1.0` — 1. Common Trunk** freezes the `NightCodeForge.Core` and `NightCodeForge.Agents` credential abstractions and removes secret fields from `config/config.json` and SQLite schemas **before** parallel `v0.1.0` streams (Installer `12.18*`, Benchmark `12.23*`, Features) and post-`v0.1.0` security/observability features (Nightwatch `12.34A` `ncf-config/` & `query_database`, DiffGuard `12.28A`) build on top of those contracts.

---

## 2. Required Behavior & Constraints

### 2.1 Native OS Keyring Secret Store (`Pmad.KeySharp`)

- **Shared `Pmad.KeySharp` Backend:** Reuse the existing `Pmad.KeySharp` dependency (`KeySharp.Keyring` — Windows Credential Manager, macOS Keychain `SecItem`, Linux `libsecret` Secret Service) already referenced in [`NightCodeForge.Infrastructure.csproj`](../../src/NightCodeForge.Infrastructure/NightCodeForge.Infrastructure.csproj).
- **Core Abstraction & Infrastructure Implementation:**
  - Define a dedicated secret-storage abstraction in `NightCodeForge.Core` and register its `Pmad.KeySharp`-backed singleton implementation in `NightCodeForge.Infrastructure` (`ServiceCollectionExtensions.AddNightCodeForgeCoreAndInfrastructure`).
  - Use named constants (no magic strings) for keyring coordinates (`Package = "NightCodeForge"`, dedicated service identifiers, and username keys) and provide constructor overrides for isolated test coordinates (e.g., `Package = "NightCodeForge.Tests"`).
  - Leave existing GitHub App OAuth keyring coordinates (`NightCodeForge` / `GitHubAppOAuth` / `oauth-token` in [`GitHubTokenStore`](../../src/NightCodeForge.Infrastructure/GitHub/GitHubTokenStore.cs)) untouched, and do **not** alter `Pmad.LocalApi.Security` companion `default-key.dat` storage ([Step 12.37](../archive/phase-12/STEP_12_37_COMPANION_API_KEY_STORE_AND_ASSAY_HANDOFF_ENDPOINTS.md)).
- **Endpoint-Scoped OpenAI-Compatible API Keys:**
  - Store OpenAI-compatible API keys scoped by **canonicalized/normalized endpoint URL** rather than as a single global key.
  - Endpoint normalization MUST consistently trim whitespace, lowercase the scheme and host, omit default ports (`80` for `http`, `443` for `https`), strip trailing slashes from the path, and strip any query string, fragment, or embedded userinfo.
  - This guarantees that users can keep independent credentials for multiple OpenAI-compatible providers (e.g., OpenAI, OpenRouter, DeepSeek, local authenticated gateways) and that switching `OpenAiCompatible.Endpoint` never leaks an existing key to a different host.
  - Persisting an OpenAI-compatible key with an empty, whitespace, or invalid endpoint MUST fail fast with an argument/validation exception rather than storing under a synthetic key.
- **Ollama Cloud Search API Key:**
  - Store the Ollama Cloud Search API key under its own dedicated service/username coordinates in the OS keyring.
- **Thread-Safe Caching & Live Change Notifications:**
  - Because `Pmad.KeySharp` operations perform native OS IPC and may block, maintain a thread-safe in-memory cache populated on initial access and updated atomically on save/delete so synchronous hot paths (such as `OllamaWebSearchService.IsConfigured` / `IsEnabled` and chat client creation) never perform repeated native keyring calls.
  - Emit a change notification event whenever an OpenAI-compatible key or Ollama Cloud Search key is stored, rotated, or deleted so cached consumers (specifically session-less clients cached inside [`LlmChatClientFactoryBase`](../../src/NightCodeForge.Agents/Inference/LlmChatClientFactories.cs) / [`OpenAiCompatibleChatClientFactory`](../../src/NightCodeForge.Agents/Inference/OpenAiCompatibleChatClientFactory.cs)) evict stale clients and adopt updated credentials immediately without requiring an application restart.
- **Fail-Fast Error Handling, Headless Compatibility & Secret Hygiene:**
  - `KeyringException` with `ErrorType.NotFound` MUST map cleanly to absent (`null` / `false`) on reads and be idempotent on delete.
  - Expose keyring availability (`Keyring.IsAvailable()`).
  - Any write or delete failure—and any unexpected keyring error (`ErrorType.AccessDenied`, `ErrorType.PasswordTooLong`, `ErrorType.GenericError`, or `ErrorType.Unavailable` during save/delete)—MUST log and fail fast with a dedicated domain exception in `NightCodeForge.Core.Exceptions` (no bare `catch { }` blocks, and **never** fall back to storing plaintext secrets in `config/config.json` or SQLite).
  - On **reads** when `Keyring.IsAvailable()` is `false` or `Keyring.GetPassword` fails with `ErrorType.Unavailable` (e.g., headless Linux containers or CI environments lacking `libsecret`), log a prominent warning once and permit process environment variable fallback (`OPENAI_API_KEY` / `OLLAMA_API_KEY`) to continue working without crashing startup.
  - Raw API key values MUST NEVER be written to application logs, exception messages, session transcripts, or telemetry events.

### 2.2 Clean-Break Removal from Plaintext Persistence (`config/config.json` & SQLite `SystemSettings`)

Per the **Clean Break Policy** in [`AGENTS.md`](../../AGENTS.md):

- **OpenAI-Compatible Configuration ([`OpenAiCompatibleOptions`](../../src/NightCodeForge.Core/Inference/OpenAiCompatibleOptions.cs) & [`LlmConfigurationWriter`](../../src/NightCodeForge.Infrastructure/Configuration/LlmConfigurationWriter.cs)):**
  - Remove the `ApiKey` property from `OpenAiCompatibleOptions` so it is structurally impossible for configuration binding or JSON serialization to persist an OpenAI API key into `config/config.json`.
  - `LlmConfigurationWriter.SaveProviderConfigurationAsync` MUST remain strictly responsible for non-secret configuration and MUST explicitly strip any legacy `"ApiKey"` property (case-insensitive) from the `LlmProvider.OpenAiCompatible` JSON node when saving `config/config.json`, ensuring prior plaintext secrets on disk are scrubbed cleanly.
- **Ollama Cloud Search Configuration ([`WebSearchSettings`](../../src/NightCodeForge.Core/Domain/WebSearchSettings.cs) & [`WebSearchSettingsService`](../../src/NightCodeForge.Infrastructure/Data/WebSearchSettingsService.cs)):**
  - Remove the `ApiKey` property from `WebSearchSettings` so `WebSearchSettingsService` persists only non-secret search parameters (`Provider`, `Endpoint`, `DefaultMaxResults`) into SQLite `AppDbContext.SystemSettings`.
  - When `WebSearchSettingsService.UpdateSettingsAsync` persists to SQLite, ensure any legacy `"ApiKey"` field is scrubbed from the stored JSON row.
  - Stop loading `WebSearch:Ollama:ApiKey` or `OLLAMA_API_KEY` from plaintext JSON configuration files (`appsettings.json` / `config/config.json`).
- **Legacy Plaintext Detection & Operator Warning:**
  - During startup ([`Program.cs`](../../src/NightCodeForge.Web/Program.cs)) and `WebSearchSettingsService.LoadSettingsAsync`, inspect whether a legacy plaintext `ApiKey` still exists in configuration (`LlmProvider:OpenAiCompatible:ApiKey`, `WebSearch:Ollama:ApiKey`) or in the SQLite `"WebSearchSettings"` JSON payload.
  - If detected, log a prominent warning (strictly without logging the secret value) instructing the operator to re-save the key via Settings (stored in the OS keyring) or provide it via environment variable and remove the plaintext entry from disk/DB. Do **not** silently load API keys from plaintext JSON or SQLite.

### 2.3 Credential Resolution Precedence & Runtime Consumers

Define deterministic, uniform resolution precedence across all consumers using named constants for environment variable names (`OPENAI_API_KEY` and [`WebSearchSettings.OllamaApiKeyEnvironmentVariable`](../../src/NightCodeForge.Core/Domain/WebSearchSettings.cs) = `"OLLAMA_API_KEY"`):

- **OpenAI-Compatible API Key Precedence:**
  1. OS keyring entry matching the canonicalized active `Endpoint`.
  2. `OPENAI_API_KEY` process environment variable.
  3. Unconfigured (`null`).
- **Ollama Cloud Search API Key Precedence:**
  1. Explicit constructor override (retained solely for isolated unit tests on `OllamaWebSearchService`).
  2. OS keyring entry for Ollama Cloud Search.
  3. `OLLAMA_API_KEY` process environment variable.
  4. Unconfigured (`null`).

All runtime consumers MUST resolve credentials via the secret store / resolution pipeline:

1. **[`OpenAiCompatibleChatClientFactory`](../../src/NightCodeForge.Agents/Inference/OpenAiCompatibleChatClientFactory.cs):**
   - Resolve the effective API key for `options.EffectiveEndpoint` from the secret store (with `OPENAI_API_KEY` fallback).
   - Fall back to [`KnownDefaultModels.LocalAuthBypassKey`](../../src/NightCodeForge.Core/Domain/KnownDefaultModels.cs) **only** when no key is configured, preserving unauthenticated local runtime compatibility (LM Studio, local vLLM) where `ApiKeyCredential` forbids empty strings.
   - Subscribe to secret-store change notifications (or key cache revisions) so cached session-less `IChatClient` instances in `LlmChatClientFactoryBase` are invalidated whenever OpenAI-compatible credentials change.
2. **[`OpenAiCompatibleModelCatalogService`](../../src/NightCodeForge.Infrastructure/Inference/OpenAiCompatibleModelCatalogService.cs):**
   - Resolve the effective API key for the active endpoint from the secret store (with `OPENAI_API_KEY` fallback), omitting the `Authorization` header when unconfigured.
   - Read live `LlmProviderOptions` (via `IOptionsMonitor<LlmProviderOptions>`) alongside live secret-store lookups so probing/verifying a newly edited endpoint and newly saved keyring key works immediately in Settings without restarting the host.
3. **[`OllamaWebSearchService`](../../src/NightCodeForge.Agents/Tools/OllamaWebSearchService.cs):**
   - Resolve the effective API key via the secret store (respecting explicit test constructor override first, then OS keyring, then `OLLAMA_API_KEY` environment variable).
   - Preserve [`SecurityOptions.AirGappedMode`](../../src/NightCodeForge.Core/Security/SecurityOptions.cs) precedence (`IsEnabled == false` when air-gapped, regardless of whether a key exists in the OS keyring or environment).
4. **[`CompanionEndpoints`](../../src/NightCodeForge.Web/Endpoints/CompanionEndpoints.cs) (`GET /api/companion/config`):**
   - Keep `NightCodeAssay` companion discovery working ([Step 12.37](../archive/phase-12/STEP_12_37_COMPANION_API_KEY_STORE_AND_ASSAY_HANDOFF_ENDPOINTS.md)) while protecting OS keyring hygiene: resolve `openAiCompatible.apiKey` from the secret store **only when `options.ProviderType == LlmProviderType.OpenAiCompatible`**.
   - When `ProviderType` is `Ollama` or `OllamaRouter`, return `apiKey: null` without touching the OS keyring so local provider setups never trigger keyring unlock prompts or leak dormant cloud keys.

### 2.4 Settings UI Security, Ergonomics & Presentation Separation

Both [`OllamaRouterGatewayCard.razor`](../../src/NightCodeForge.Web/Components/Settings/OllamaRouterGatewayCard.razor) and [`WebSearchSettingsTab.razor`](../../src/NightCodeForge.Web/Components/Settings/WebSearchSettingsTab.razor) MUST adopt a **write-only / non-echoed credential UX**:

- **Never Echo Stored Keys:** Already-persisted API keys retrieved from the OS keyring MUST NEVER be rendered or bound into HTML `<input>` values in the Blazor DOM.
- **Credential Source Status Badges:** Clearly indicate the active source (`Stored in OS Keyring`, `Inherited from OPENAI_API_KEY` / `Inherited from OLLAMA_API_KEY`, `OS Keyring Unavailable`, or `Not Configured`), and state explicitly that keys are persisted securely in the operating system keyring via `Pmad.KeySharp`—never in `config/config.json` or SQLite.
- **Preserve-on-Blank / Replace / Explicit Clear Semantics:**
  - The password input starts empty on initial load (with a Show/Hide toggle covering only newly typed text prior to saving).
  - **Save with blank input:** Preserves any existing OS keyring entry for that endpoint/service without overwriting or deleting it.
  - **Save with non-empty input:** Trims and stores the new key in the OS keyring, clears the input field, and refreshes the status badge.
  - **Explicit Clear/Remove action:** Deletes the stored entry from the OS keyring and updates the status badge.
- **Endpoint-Scoped Behavior (`OllamaRouterGatewayCard.razor`):**
  - Query OS keyring presence only when `LlmProviderType.OpenAiCompatible` is selected.
  - Refresh the credential status badge whenever the user edits the Endpoint URL so the badge accurately reflects whether the target endpoint already has a key in the keyring (never silently copying or transferring a key from one endpoint to another).
- **Air-Gapped Behavior (`WebSearchSettingsTab.razor`):**
  - Preserve full Air-Gapped mode lockdown (disable key input, Save, Clear/Remove, and Test controls).
  - Ensure **Test Search Connection** remains enabled whenever a key is stored in the OS keyring, newly typed in the input, or inherited from `OLLAMA_API_KEY` (and Air-Gapped mode is inactive).
- **UI/Presentation Separation ([`AGENTS.md`](../../AGENTS.md)):**
  - Extract endpoint normalization, credential-source status resolution, badge metadata mapping, and save/clear orchestration into dedicated presentation/helper services (e.g., under `src/NightCodeForge.Web/Services/` or `NightCodeForge.Core`) so Razor `@code` blocks remain thin and unit-testable without requiring UI rendering context.

### 2.5 Architectural Constraints & Documentation Hygiene

- **Constructor Parameter Ceiling ([`AGENTS.md`](../../AGENTS.md) & [`ConstructorParameterCeilingTests`](../../tests/NightCodeForge.Web.Tests/Architecture/ConstructorParameterCeilingTests.cs)):**
  - All instance constructors across `Core`, `Infrastructure`, `Agents`, and `Web` MUST have `<= 10` parameters and zero optional parameters.
  - Note that [`OpenAiCompatibleChatClientFactory`](../../src/NightCodeForge.Agents/Inference/OpenAiCompatibleChatClientFactory.cs) currently has **9** constructor parameters (adding the secret store reaches the 10-parameter ceiling, or existing collaborators may be grouped to stay well within the limit).
- **Constants Over Magic Strings:** Centralize keyring package/service/username identifiers, environment variable names (`OPENAI_API_KEY`, `OLLAMA_API_KEY`), and legacy JSON property keys (`"ApiKey"`) as constants.
- **Documentation Sync:** Update [`docs/CRUCIBLE.md`](../CRUCIBLE.md) (around line 218) so `web_search` authentication accurately documents OS keyring (`Pmad.KeySharp`) and `OLLAMA_API_KEY` environment variable storage rather than `appsettings.json`.

---

## 3. Definition of Done (DoD) Checklist

- [ ] **Core Contract, Exception & `Pmad.KeySharp` Secret Store:**
  - [ ] Define the secret-storage interface in `NightCodeForge.Core`, dedicated domain exception in `NightCodeForge.Core.Exceptions`, and endpoint URL normalization rules.
  - [ ] Implement the `Pmad.KeySharp`-backed singleton store in `NightCodeForge.Infrastructure` using constants for coordinates (with constructor overrides for test isolation), thread-safe in-memory caching, change notifications, `Keyring.IsAvailable()` awareness, `ErrorType.Unavailable` read fallback to environment variables, and fail-fast domain exceptions on write/delete/unexpected errors.
  - [ ] Register the store in `ServiceCollectionExtensions.AddNightCodeForgeCoreAndInfrastructure` without altering `GitHubTokenStore` or `Pmad.LocalApi.Security` (`default-key.dat`).
- [ ] **Clean-Break Plaintext Removal & Legacy Scrubbing:**
  - [ ] Remove `ApiKey` from `OpenAiCompatibleOptions` (`src/NightCodeForge.Core/Inference/OpenAiCompatibleOptions.cs`) and scrub legacy `"ApiKey"` JSON properties in `LlmConfigurationWriter.SaveProviderConfigurationAsync`.
  - [ ] Remove `ApiKey` from `WebSearchSettings` (`src/NightCodeForge.Core/Domain/WebSearchSettings.cs`), stop reading `WebSearch:Ollama:ApiKey` / `OLLAMA_API_KEY` from JSON config in `WebSearchSettingsService`, and scrub legacy `"ApiKey"` fields when persisting to SQLite `SystemSettings`.
  - [ ] Add redacted startup (`Program.cs`) and load (`WebSearchSettingsService.LoadSettingsAsync`) warnings when legacy plaintext `ApiKey` entries are found on disk or in SQLite.
- [ ] **Runtime Consumers:**
  - [ ] Update `OpenAiCompatibleChatClientFactory` to resolve endpoint-scoped keys from the secret store (`OPENAI_API_KEY` fallback, `LocalAuthBypassKey` only when unconfigured), invalidate cached session-less clients on key change, and stay `<= 10` constructor parameters (no optional parameters).
  - [ ] Update `OpenAiCompatibleModelCatalogService` to resolve live options (`IOptionsMonitor<LlmProviderOptions>`) and endpoint-scoped keys from the secret store.
  - [ ] Update `OllamaWebSearchService` to resolve keys from the secret store (with explicit test override and `OLLAMA_API_KEY` fallback) while preserving `AirGappedMode` gating.
  - [ ] Update `CompanionEndpoints` (`/api/companion/config`) to resolve `openAiCompatible.apiKey` from the secret store only when `ProviderType == LlmProviderType.OpenAiCompatible` (returning `null` otherwise).
- [ ] **Settings UI & Presentation Separation:**
  - [ ] Extract presentation/orchestration helpers so Razor components remain thin per `AGENTS.md`.
  - [ ] Update `OllamaRouterGatewayCard.razor` and `WebSearchSettingsTab.razor` to use write-only/non-echoed key inputs, credential-source status badges, preserve-on-blank Save semantics, explicit Clear/Remove keyring deletion, endpoint-change status refresh, and Air-Gapped mode lockdown.
- [ ] **Documentation & Automated Verification:**
  - [ ] Update `docs/CRUCIBLE.md` to reflect OS keyring (`Pmad.KeySharp`) and environment variable authentication for `web_search`.
  - [ ] Add isolated `Pmad.KeySharp` OS keyring integration tests (`NightCodeForge.Tests` package with unique per-test coordinates and `Dispose()` cleanup) verifying round-trip, endpoint normalization, overwrite, `NotFound`, and idempotent delete.
  - [ ] Add/update unit and component tests across `NightCodeForge.Core.Tests`, `NightCodeForge.Infrastructure.Tests`, `NightCodeForge.Agents.Tests`, and `NightCodeForge.Web.Tests` (using mocked/fake secret stores so unit tests never touch the host OS keyring) verifying plaintext exclusion in `config/config.json` and SQLite, legacy scrubbing & redacted warnings, keyring/env-var precedence, headless `Unavailable` read fallback & write fail-fast, chat client cache invalidation, `CompanionEndpoints` provider gating, UI write-only/preserve/replace/clear/air-gapped flows, and `ConstructorParameterCeilingTests`.
  - [ ] Verify all non-E2E tests pass via `dotnet test --filter "Category!=Acceptance&Category!=E2E"`, then run the full test suite including E2E tests (`dotnet test`) upon completion.
