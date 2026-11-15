# 🧪 Step 12.37: Companion API Key Store (`default-key.dat`) & NightCode Assay Handoff Endpoints (`/api/companion/*`)

> **Feature Implementation Step (Companion Tool Authentication, Configuration Discovery & Triage Handoff)**  
> *Parent Plan:* [Features & Integrations Roadmap](README.md)  
> *Source Policy:* [AGENTS.md](../../AGENTS.md)  
> *Aligns With:* [AGENTS.md](../../AGENTS.md), [docs/PLAN.md](../PLAN.md)  
> *Project Target:* `src/NightCodeForge.Core`, `src/NightCodeForge.Infrastructure`, `src/NightCodeForge.Web`, `tests/NightCodeForge.Infrastructure.Tests`, `tests/NightCodeForge.Web.Tests` | *Status:* Planned | *Target Framework:* .NET 10 (C# 13)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Objective

**NightCode Assay** is a lightweight companion desktop application (`C:\Users\Julien\source\repos\jetelain\NightCodeAssay`) designed for manual exploratory testing of software built by **NightCodeForge**. To avoid duplicating LLM configuration or repository settings across both applications while enabling 1-click feedback handoff into NightCodeForge's Sandboxed Triage portal, this step introduces:

1. **Local API Key Store via `Pmad.LocalApi.Security` (`v0.1.0`):**
   - Uses `Pmad.LocalApi.Security` (`IApiKeyStore`, `ApiKeyStore`, `IKeyProtector`, `KeyProtectorFactory`, `ApiKeyHeaderParser`) to automatically generate and persist a 32-byte (256-bit, 43-character unpadded base64url) default API key in `<ContentRoot>/config/default-key.dat` wrapped at rest via `IKeyProtector` (`DpapiKeyProtector` using Windows DPAPI `DataProtectionScope.CurrentUser` + restricted NTFS ACLs on Windows; `PlainKeyProtector` with `0600` owner-only file permissions on Unix), plus optional operator-created named user keys in `<ContentRoot>/config/api-keys.json` (format v2 storing per-key 16-byte random salts and salted SHA-256 hashes).
   - Uses the standard `default-key.dat` JSON envelope (`{"key": "...", "generatedAt": "..."}`) and constant-time validation (`CryptographicOperations.FixedTimeEquals`), enabling local companion tools running under the same OS user (`NightCodeAssay`, `ncf` CLI) to discover, unwrap, and authenticate automatically via `Pmad.LocalApi.Security` (`DefaultKeyReader`) or `Pmad.LocalApi.Client` (`LocalApiKeyDelegatingHandler`) without manual key copy-paste.
2. **Bearer / API Key Authentication Filter for `/api/companion/*`:**
   - Protects all `/api/companion/*` endpoints by extracting tokens via `ApiKeyHeaderParser.ExtractKey` (`Authorization: Bearer <key>` or `X-Api-Key: <key>`) and validating via `IApiKeyStore.ValidateKey`, returning `401 Unauthorized` when missing or invalid.
3. **Companion Configuration & Workspace Discovery Endpoints (`GET /api/companion/config` & `GET /api/companion/workspaces`):**
   - Allows `NightCodeAssay` to discover the active LLM provider (`ProviderType`, `EffectiveEndpoint`, `DefaultModelId`, `ArchitectModelId`, `CoderModelId`, and `OpenAiCompatible` endpoint/API key) and the list of registered workspaces (`Id`, `Name`, `Emoji`, `ColorCode`, `RepositoryPath`, `DefaultBaseBranch`).
4. **1-Click Exploratory Report $\rightarrow$ Sandboxed Triage Handoff (`POST /api/companion/workspaces/{workspaceId:int}/triage`):**
   - Stages a new triage draft for the target workspace pre-seeded with the exploratory test report Markdown in `ICompanionTriageDraftStore`, and supports selecting/loading that draft via `?draftId={id}` in [`Triage.razor`](../../src/NightCodeForge.Web/Components/Pages/Triage.razor) with the report pre-filled in the **Submit External Report** form.

---

## 2. Technical Specifications

### 2.1 API Key Store Integration via `Pmad.LocalApi.Security`

1. **Package Reference (`NightCodeForge.Infrastructure` & `NightCodeForge.Web`):**
   - Add `<PackageReference Include="Pmad.LocalApi.Security" Version="0.1.0" />` to [`NightCodeForge.Infrastructure.csproj`](../../src/NightCodeForge.Infrastructure/NightCodeForge.Infrastructure.csproj) (and to [`NightCodeForge.Web.csproj`](../../src/NightCodeForge.Web/NightCodeForge.Web.csproj) if `NightCodeForge.Web` references `Pmad.LocalApi.Security` types directly without relying solely on transitive reference).
   - Do **not** reimplement custom key cryptography, envelope serialization, DPAPI/Unix file permission protectors, or key store classes inside `NightCodeForge.Core` or `NightCodeForge.Infrastructure`; consume the `Pmad.LocalApi.Security` primitives directly.

2. **Consumed `Pmad.LocalApi.Security` Primitives & Signatures:**
   - **At-Rest Protection:** `IKeyProtector`, `DpapiKeyProtector` (Windows DPAPI `DataProtectionScope.CurrentUser` + restricted NTFS ACL), `PlainKeyProtector` (`0600` owner-only file permissions on Unix), and `KeyProtectorFactory.CreatePlatformDefault()`.
   - **Key Store:** `IApiKeyStore` and `ApiKeyStore`:
     ```csharp
     public ApiKeyStore(
         string configDirectory,
         IKeyProtector keyProtector,
         ILogger<ApiKeyStore>? logger = null,
         TimeProvider? timeProvider = null)
     ```
     - Manages `<ContentRoot>/config/default-key.dat` (32-byte / 43-char unpadded base64url auto-generated rotatable key) and `<ContentRoot>/config/api-keys.json` (format v2 named user keys stored with per-key 16-byte random salt and `SHA256(salt || UTF8(key))`).
     - Key methods on `IApiKeyStore`: `DefaultKeyFilePath`, `UserKeysFilePath`, `GetDefaultKey()`, `GetDefaultKeyGeneratedAt()`, `RotateDefaultKeyAsync()`, `RotateDefaultKeyIfStaleAsync(TimeSpan maxAge)`, `CreateUserKeyAsync(string name, int? validForHours)`, `GetUserKeys()`, `DeleteUserKeyAsync(string name)`, and `ValidateKey(string? presentedKey)` (constant-time comparison via `CryptographicOperations.FixedTimeEquals`; fails safe on corrupt `default-key.dat` or `api-keys.json` so startup is never blocked).
   - **Header Extraction:**
     ```csharp
     public static string? ExtractKey(string? authorizationHeader, string? xApiKeyHeader)
     ```
     - `ApiKeyHeaderParser.ExtractKey` extracts `Authorization: Bearer <key>` (case-insensitive scheme prefix, taking precedence) or falls back to `X-Api-Key: <key>` (trimmed), rejecting values with control characters.
   - **Companion Client Discovery:** Companion tools (`NightCodeAssay`, `ncf` CLI) read and unwrap `default-key.dat` automatically using `Pmad.LocalApi.Security` (`IDefaultKeyReader` / `DefaultKeyReader.CreateForApp(...)`) or `Pmad.LocalApi.Client` (`LocalApiKeyDelegatingHandler` with automatic `401` rotation retry).

3. **DI Registration ([`ServiceCollectionExtensions.cs`](../../src/NightCodeForge.Infrastructure/ServiceCollectionExtensions.cs) — `AddNightCodeForgeCoreAndInfrastructure`):**
   - Register `IKeyProtector` as a singleton using `KeyProtectorFactory.CreatePlatformDefault()`:
     ```csharp
     services.TryAddSingleton<IKeyProtector>(_ => KeyProtectorFactory.CreatePlatformDefault());
     ```
   - Register `IApiKeyStore` as a singleton resolving `contentRootPath` from `IHostEnvironment.ContentRootPath` (falling back to `AppContext.BaseDirectory` when `IHostEnvironment` is not registered, e.g. in standalone test containers), passing `<ContentRoot>/config`, `IKeyProtector`, `ILogger<ApiKeyStore>`, and `TimeProvider`:
     ```csharp
     services.TryAddSingleton<IApiKeyStore>(sp =>
     {
         var contentRootPath = sp.GetService<IHostEnvironment>()?.ContentRootPath ?? AppContext.BaseDirectory;
         return new ApiKeyStore(
             Path.Combine(contentRootPath, "config"),
             sp.GetRequiredService<IKeyProtector>(),
             sp.GetRequiredService<ILogger<ApiKeyStore>>(),
             sp.GetRequiredService<TimeProvider>());
     });
     ```
   - Register `ICompanionTriageDraftStore` (`CompanionTriageDraftStore`) as a singleton alongside `IKeyProtector` and `IApiKeyStore`:
     ```csharp
     services.TryAddSingleton<ICompanionTriageDraftStore, CompanionTriageDraftStore>();
     ```

4. **Startup Eager Initialization ([`Program.cs`](../../src/NightCodeForge.Web/Program.cs)):**
   - [`Program.cs`](../../src/NightCodeForge.Web/Program.cs) must call `app.Services.GetRequiredService<IApiKeyStore>().GetDefaultKey();` during startup initialization so `<ContentRoot>/config/default-key.dat` is created on disk immediately when `NightCodeForge` starts (before `NightCodeAssay` attempts to read `default-key.dat` from disk).
   - Note that `Pmad.LocalApi.Security.ApiKeyStore` initializes lazily on first operation and `ValidateKey(string? presentedKey)` pre-filters null/whitespace and invalid-format keys via `ApiKeyCrypto.IsValidApiKeyFormat` *before* calling `EnsureLoaded()`. Eagerly calling `GetDefaultKey()` at startup guarantees `default-key.dat` exists on disk even before any valid authenticated request arrives (and tests using `WebApplicationFactory` or calling [`Program.cs`](../../src/NightCodeForge.Web/Program.cs) startup will observe `default-key.dat` immediately after startup).

5. **Repository Hygiene ([`.gitignore`](../../.gitignore)) & Test Isolation:**
   - Update [`.gitignore`](../../.gitignore) under `# User-owned configuration layer (local overrides)` to ignore `config/default-key.dat`, `**/config/default-key.dat`, `config/api-keys.json`, and `**/config/api-keys.json` alongside `config/config.json` so machine-local API keys are never accidentally tracked by Git.
   - Unit and integration tests exercising `IApiKeyStore` or `/api/companion/*` must isolate `ContentRootPath` / `configDirectory` to a dedicated temporary directory under `Path.GetTempPath()` (and clean it up on test disposal) rather than writing to the shared `AppContext.BaseDirectory/config` folder.

---

### 2.2 Companion Authentication Filter (`CompanionApiKeyEndpointFilter`), Route Group & Routing Hygiene

Map all endpoints under a `/api/companion` route group in [`Program.cs`](../../src/NightCodeForge.Web/Program.cs) with `.AddEndpointFilter<CompanionApiKeyEndpointFilter>()` and `.DisableAntiforgery()`:
1. Extract key using `ApiKeyHeaderParser.ExtractKey(context.HttpContext.Request.Headers.Authorization.FirstOrDefault(), context.HttpContext.Request.Headers["X-Api-Key"].FirstOrDefault())` (`Authorization: Bearer <token>` with case-insensitive scheme prefix takes precedence, falling back to `X-Api-Key: <token>`).
2. Validate via `IApiKeyStore.ValidateKey(presentedKey)`.
3. If missing or invalid, return HTTP `401 Unauthorized` with JSON body `{"error": "Unauthorized: valid NightCodeForge API key required."}` and `WWW-Authenticate: Bearer`.
4. **StatusCodePagesMiddleware Compatibility:** Because [`Program.cs`](../../src/NightCodeForge.Web/Program.cs) registers `app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true)`, every `401 Unauthorized`, `404 Not Found`, and `400 Bad Request` response returned under `/api/companion/*` MUST write a JSON response body (`{"error": "..."}`, e.g., `Results.Json(..., statusCode: ...)`), never an empty body, so `StatusCodePagesMiddleware` does not intercept empty-body error status codes and rewrite them to HTML.
5. **Non-Workspace Route Registration ([`WorkspaceUrlHelper.cs`](../../src/NightCodeForge.Web/Services/WorkspaceUrlHelper.cs)):** Add `"api"` to `WorkspaceUrlHelper.NonWorkspaceRootSegments` so `/api/*` paths are recognized as non-workspace system routes and never rewritten under `/w/{workspaceId}/...`.

---

### 2.3 Companion HTTP Endpoints (`/api/companion/*`)

#### 1. `GET /api/companion/config`
Reads the live LLM inference configuration via `IOptionsMonitor<LlmProviderOptions>.CurrentValue` ([`LlmProviderOptions.cs`](../../src/NightCodeForge.Core/Inference/LlmProviderOptions.cs), [`OpenAiCompatibleOptions.cs`](../../src/NightCodeForge.Core/Inference/OpenAiCompatibleOptions.cs)) so companion tools (`NightCodeAssay`) can connect to the same Ollama or OpenAI-compatible provider without duplicating configuration:

```json
{
  "providerType": "OllamaRouter",
  "endpoint": "http://localhost:11434",
  "defaultModelId": "Qwen3.8-27B:latest",
  "architectModelId": "Qwen3.8-27B:latest",
  "coderModelId": "Qwen3.8-27B:latest",
  "openAiCompatible": {
    "endpoint": "https://api.openai.com/v1",
    "apiKey": null,
    "organizationId": null
  }
}
```
- `providerType` is serialized as a string (`options.ProviderType.ToString()`: `"Ollama"`, `"OllamaRouter"`, or `"OpenAiCompatible"`), never as a numeric enum value.
- `endpoint` returns `LlmProviderOptions.EffectiveEndpoint`.
- `defaultModelId` returns `LlmProviderOptions.DefaultModelId.Trim()` when non-whitespace, or `null` when unset.
- `architectModelId` returns `LlmProviderOptions.ArchitectModelId.Trim()` when non-whitespace, falling back to trimmed `LlmProviderOptions.DefaultModelId` (or `null` when both are unset).
- `coderModelId` returns `LlmProviderOptions.CoderModelId.Trim()` when non-whitespace, falling back to trimmed `LlmProviderOptions.DefaultModelId` (or `null` when both are unset).
- `openAiCompatible` returns `{ endpoint, apiKey, organizationId }` from `LlmProviderOptions.OpenAiCompatible` ([`OpenAiCompatibleOptions.cs`](../../src/NightCodeForge.Core/Inference/OpenAiCompatibleOptions.cs)).
- Note: `NightCodeAssay` maps both `"Ollama"` and `"OllamaRouter"` to standard `OllamaSharp` (`OllamaApiClient`) without any router headers, and `"OpenAiCompatible"` to `Microsoft.Extensions.AI.OpenAI`.

#### 2. `GET /api/companion/workspaces`
Queries `IWorkspaceRepository.GetAllAsync(cancellationToken)` and `ITaskRepository.GetByStatusAsync(TaskOverallStatus.Ready, cancellationToken)` once (avoiding N+1 per-workspace queries), filters pending review tasks via [`TaskBadgeCounter.IsPendingHumanReview`](../../src/NightCodeForge.Core/Workflow/TaskBadgeCounter.cs), and returns all registered [`Workspace`](../../src/NightCodeForge.Core/Domain/Workspace.cs) entries ordered by `Name` (`StringComparer.OrdinalIgnoreCase`) along with their `pendingReviewTaskCount`:

```json
{
  "workspaces": [
    {
      "id": 1,
      "name": "NightCodeForge",
      "emoji": "🔥",
      "colorCode": "#6366f1",
      "repositoryPath": "C:\\Users\\Julien\\source\\repos\\jetelain\\NightCodeForge",
      "defaultBaseBranch": "main",
      "pendingReviewTaskCount": 2
    }
  ]
}
```

#### 3. `POST /api/companion/workspaces/{workspaceId:int}/triage` & `ICompanionTriageDraftStore`
Stages a new Triage submission draft in the specified workspace seeded with an exploratory test report from `NightCodeAssay`:

- **Draft Store Contract (`NightCodeForge.Core.Services`):**
  ```csharp
  namespace NightCodeForge.Core.Services;

  public sealed record CompanionTriageDraft(
      string DraftId,
      int WorkspaceId,
      string Title,
      string ReportMarkdown,
      string BaseBranch,
      string? SessionDirectoryPath,
      DateTimeOffset CreatedAt);

  public interface ICompanionTriageDraftStore
  {
      CompanionTriageDraft CreateDraft(
          int workspaceId,
          string title,
          string reportMarkdown,
          string baseBranch,
          string? sessionDirectoryPath = null);

      CompanionTriageDraft? GetDraft(string? draftId);
  }
  ```
  - `CompanionTriageDraftStore` is a thread-safe singleton in-memory store whose constructor takes `TimeProvider timeProvider` (no optional parameters) for deterministic 24-hour TTL expiration pruning.
  - Generates a 12-character hex/base64url `DraftId` and prunes drafts older than 24 hours on access/insertion.

- **Request Payload (`CreateCompanionTriageRequest`):**
  ```json
  {
    "title": "Assay Exploratory Report — 2026-11-14 09:05",
    "reportMarkdown": "# Exploratory Test Report — 2026-11-14\n...",
    "baseBranch": "main",
    "sessionDirectoryPath": "C:\\Users\\Julien\\...\\sessions\\2026-11-14_09-05"
  }
  ```
- **Validation & Behavior:**
  1. Verify workspace `{workspaceId}` exists via `IWorkspaceRepository.GetByIdAsync(workspaceId, cancellationToken)`; return `404 Not Found` with JSON body `{"error": "Workspace not found."}` if absent.
  2. Validate `reportMarkdown` is non-empty; return `400 Bad Request` with JSON body `{"error": "reportMarkdown is required."}` if blank.
  3. Build the report title: use `request.Title.Trim()` if non-whitespace, otherwise `$"Assay Report #{timeProvider.GetUtcNow():MMdd-HHmm}"` (using the injected `TimeProvider`).
  4. Resolve `baseBranch`: use `request.BaseBranch.Trim()` if non-whitespace, otherwise `!string.IsNullOrWhiteSpace(workspace.DefaultBaseBranch) ? workspace.DefaultBaseBranch : "main"`.
  5. Normalize `sessionDirectoryPath`: use `request.SessionDirectoryPath.Trim()` when non-whitespace, otherwise `null`.
  6. Store the staged triage draft in `ICompanionTriageDraftStore` so the HTTP endpoint responds immediately (`201 Created`) without blocking on a long-running sandboxed agent run.
  7. Return HTTP `201 Created` (`Results.Created(triageUrl, response)`):
     ```json
     {
       "draftId": "7f9c2b1e4a8d",
       "workspaceId": 1,
       "title": "Assay Exploratory Report — 2026-11-14 09:05",
       "triageUrl": "/w/1/triage?draftId=7f9c2b1e4a8d"
     }
     ```

---

### 2.4 `Triage.razor` Deep-Link Support (`?draftId={id}`)

Enhance [`Triage.razor`](../../src/NightCodeForge.Web/Components/Pages/Triage.razor) and `TriageSubmissionModel` to support direct deep-linking to a staged companion triage draft while adhering to [`AGENTS.md`](../../AGENTS.md) UI/Presentation separation:
1. Extract draft-loading logic into `TriageSubmissionModel.TryApplyCompanionDraft(ICompanionTriageDraftStore draftStore, int? activeWorkspaceId, string? draftId, ref string? lastAppliedDraftId)` in [`TriageViewModels.cs`](../../src/NightCodeForge.Web/Services/TriageViewModels.cs):
   - Returns `false` if `string.IsNullOrWhiteSpace(draftId)`, if `activeWorkspaceId` is null, or if `string.Equals(lastAppliedDraftId, draftId, StringComparison.Ordinal)`.
   - Looks up `draftStore.GetDraft(draftId)`; if found and `draft.WorkspaceId == activeWorkspaceId.Value`, sets `Title = draft.Title`, `Description = draft.ReportMarkdown`, `BaseBranch = draft.BaseBranch`, clears `ExternalUrl = null` and `StackTrace = null`, records `lastAppliedDraftId = draftId`, and returns `true`.
   - Tracking `lastAppliedDraftId` prevents user edits or form resets in [`Triage.razor`](../../src/NightCodeForge.Web/Components/Pages/Triage.razor) from being overwritten on subsequent component re-renders unless `DraftId` changes.
2. In [`Triage.razor`](../../src/NightCodeForge.Web/Components/Pages/Triage.razor):
   - Add `[SupplyParameterFromQuery(Name = "draftId")] public string? DraftId { get; set; }` and `@inject ICompanionTriageDraftStore CompanionTriageDraftStore`.
   - Maintain `private string? _lastAppliedDraftId;` and call `_model.TryApplyCompanionDraft(CompanionTriageDraftStore, WorkspaceState.ActiveWorkspace?.Id, DraftId, ref _lastAppliedDraftId)` after `SyncBaseBranchFromActiveWorkspace()` in `OnInitializedAsync()`, `OnParametersSet()`, and `HandleWorkspaceChanged()`, clearing `_errorMessage = null` when `TryApplyCompanionDraft` returns `true` so the exploratory report is immediately loaded in the **Submit External Report** form for the maintainer to review and click **🛡️ Launch Sandboxed Triage ➔**.
3. Update [`TriagePageTests.cs`](../../tests/NightCodeForge.Web.Tests/TriagePageTests.cs) constructor DI setup to register `ICompanionTriageDraftStore` (`new CompanionTriageDraftStore(TimeProvider.System)`) when `@inject ICompanionTriageDraftStore` is added to [`Triage.razor`](../../src/NightCodeForge.Web/Components/Pages/Triage.razor).

---

## 3. Implementation Checklist & Deliverables

- [ ] Add `<PackageReference Include="Pmad.LocalApi.Security" Version="0.1.0" />` to [`NightCodeForge.Infrastructure.csproj`](../../src/NightCodeForge.Infrastructure/NightCodeForge.Infrastructure.csproj) (and [`NightCodeForge.Web.csproj`](../../src/NightCodeForge.Web/NightCodeForge.Web.csproj) if referenced directly).
- [ ] Register `IKeyProtector` (via `KeyProtectorFactory.CreatePlatformDefault()`), `IApiKeyStore` (configured for `<ContentRoot>/config`, resolving `IHostEnvironment.ContentRootPath` with `AppContext.BaseDirectory` fallback), and `ICompanionTriageDraftStore` (`CompanionTriageDraftStore`) as singletons in [`ServiceCollectionExtensions.cs`](../../src/NightCodeForge.Infrastructure/ServiceCollectionExtensions.cs) (`AddNightCodeForgeCoreAndInfrastructure`), and eagerly invoke `app.Services.GetRequiredService<IApiKeyStore>().GetDefaultKey()` during [`Program.cs`](../../src/NightCodeForge.Web/Program.cs) startup initialization.
- [ ] Update [`.gitignore`](../../.gitignore) under `# User-owned configuration layer (local overrides)` to ignore `config/default-key.dat`, `**/config/default-key.dat`, `config/api-keys.json`, and `**/config/api-keys.json`.
- [ ] Implement `CompanionTriageDraft`, `ICompanionTriageDraftStore`, and `CompanionTriageDraftStore` in `NightCodeForge.Core.Services`.
- [ ] Add `"api"` to `WorkspaceUrlHelper.NonWorkspaceRootSegments` in [`WorkspaceUrlHelper.cs`](../../src/NightCodeForge.Web/Services/WorkspaceUrlHelper.cs).
- [ ] Implement `CompanionEndpoints` (`/api/companion/config`, `/api/companion/workspaces`, `POST /api/companion/workspaces/{workspaceId:int}/triage`) protected by `CompanionApiKeyEndpointFilter` (extracting headers via `ApiKeyHeaderParser.ExtractKey`, validating via `IApiKeyStore.ValidateKey`, with `.DisableAntiforgery()` and JSON error bodies on all 4xx responses) and map them in [`Program.cs`](../../src/NightCodeForge.Web/Program.cs).
- [ ] Add `TriageSubmissionModel.TryApplyCompanionDraft` in [`TriageViewModels.cs`](../../src/NightCodeForge.Web/Services/TriageViewModels.cs) and wire `?draftId={id}` query parameter handling in [`Triage.razor`](../../src/NightCodeForge.Web/Components/Pages/Triage.razor) (updating [`TriagePageTests.cs`](../../tests/NightCodeForge.Web.Tests/TriagePageTests.cs) DI setup).
- [ ] Add unit and integration tests in `NightCodeForge.Infrastructure.Tests` and `NightCodeForge.Web.Tests` (isolating `ContentRootPath` / `configDirectory` to temp directories under `Path.GetTempPath()`) covering `Pmad.LocalApi.Security` DI resolution & `<ContentRoot>/config/default-key.dat` startup creation, `CompanionTriageDraftStore` TTL & retrieval, `401`/`404`/`400` JSON responses on `/api/companion/*` requests (for both `Authorization: Bearer` and `X-Api-Key`), config/workspace discovery payloads, triage draft creation, `"api"` non-workspace routing in `WorkspaceUrlHelper`, and `?draftId={id}` deep-link loading in `TriageSubmissionModel` / [`TriagePageTests.cs`](../../tests/NightCodeForge.Web.Tests/TriagePageTests.cs).
