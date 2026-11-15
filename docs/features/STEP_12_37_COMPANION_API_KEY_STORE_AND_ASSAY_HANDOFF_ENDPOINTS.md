# 🧪 Step 12.37: Companion API Key Store (`default-key.dat`) & NightCode Assay Handoff Endpoints (`/api/companion/*`)

> **Feature Implementation Step (Companion Tool Authentication, Configuration Discovery & Triage Handoff)**  
> *Parent Plan:* [Features & Integrations Roadmap](README.md)  
> *Source Policy:* [AGENTS.md](../../AGENTS.md)  
> *Project Target:* `src/NightCodeForge.Core`, `src/NightCodeForge.Infrastructure`, `src/NightCodeForge.Web`, `tests/NightCodeForge.Infrastructure.Tests`, `tests/NightCodeForge.Web.Tests` | *Status:* Planned | *Target Framework:* .NET 10 (C# 13)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Objective

**NightCode Assay** is a lightweight companion desktop application (`C:\Users\Julien\source\repos\jetelain\NightCodeAssay`) designed for manual exploratory testing of software built by **NightCodeForge**. To avoid duplicating LLM configuration or repository settings across both applications while enabling 1-click feedback handoff into NightCodeForge's Sandboxed Triage portal, this step introduces:

1. **OllamaRouter-Style Local API Key Store (`IApiKeyStore`, `IKeyProtector`, `ApiKeyCrypto`):**
   - Automatically generates and persists a 32-byte (256-bit) base64url default API key in `<ContentRoot>/config/default-key.dat` wrapped at rest via `IKeyProtector` (`DpapiKeyProtector` using Windows DPAPI `DataProtectionScope.CurrentUser` on Windows; `PlainKeyProtector` with `0600` owner-only file permissions on Unix), plus optional operator-created user keys in `<ContentRoot>/config/api-keys.json` (storing salted SHA-256 hashes with a unique per-key random salt).
   - Follows the exact same envelope format (`{"key": "...", "generatedAt": "..."}`) and constant-time validation (`CryptographicOperations.FixedTimeEquals`) as `OllamaRouter`, enabling local companion tools running under the same OS user (`NightCodeAssay`, `ncf` CLI) to authenticate automatically without manual key copy-paste.
2. **Bearer / API Key Authentication Filter for `/api/companion/*`:**
   - Protects all `/api/companion/*` endpoints via constant-time key validation (`Authorization: Bearer <key>` or `X-Api-Key: <key>`), returning `401 Unauthorized` when missing or invalid.
3. **Companion Configuration & Workspace Discovery Endpoints (`GET /api/companion/config` & `GET /api/companion/workspaces`):**
   - Allows `NightCodeAssay` to discover the active LLM provider (`ProviderType`, `EffectiveEndpoint`, `DefaultModelId`, `ArchitectModelId`, `CoderModelId`, and `OpenAiCompatible` endpoint/API key) and the list of registered workspaces (`Id`, `Name`, `Emoji`, `ColorCode`, `RepositoryPath`, `DefaultBaseBranch`).
4. **1-Click Exploratory Report $\rightarrow$ Sandboxed Triage Handoff (`POST /api/companion/workspaces/{workspaceId:int}/triage`):**
   - Stages a new triage draft for the target workspace pre-seeded with the exploratory test report Markdown in `ICompanionTriageDraftStore`, and supports selecting/loading that draft via `?draftId={id}` in `Triage.razor` with the report pre-filled in the **Submit External Report** form.

---

## 2. Technical Specifications

### 2.1 API Key Cryptography & Protected Key Store (`IApiKeyStore` & `IKeyProtector`)

1. **Dependencies (`NightCodeForge.Core` & `NightCodeForge.Infrastructure`):**
   - Add `<PackageReference Include="System.Security.Cryptography.ProtectedData" Version="9.0.0" />` to `src/NightCodeForge.Core/NightCodeForge.Core.csproj` for `DpapiKeyProtector`.
   - Place `ApiKeyCrypto`, `IKeyProtector`, `DpapiKeyProtector`, `PlainKeyProtector`, and `IApiKeyStore` in `NightCodeForge.Core.Security`.
   - Place `ApiKeyStore` (which resolves `<ContentRoot>` via `IHostEnvironment`) in `NightCodeForge.Infrastructure.Security`.

2. **Key Format (`ApiKeyCrypto`):**
   - Keys are 32 random bytes (`RandomNumberGenerator.GetBytes(32)`) encoded as unpadded base64url (43 characters, alphabet `[A-Za-z0-9_-]`).
   - `IsValidApiKeyFormat(string? key)` performs a fast structural check (length `== 43`, valid base64url characters, decodes to 32 bytes) before constant-time comparison.
   - `HashKey(string key, ReadOnlySpan<byte> salt)` computes a salted SHA-256 hash (`SHA256` over `salt` + UTF-8 bytes of `key`) for user-created keys, using a unique cryptographically random per-key salt (at least 16 bytes via `RandomNumberGenerator.GetBytes(16)`) stored alongside the hash in `config/api-keys.json` to prevent dictionary and rainbow-table attacks. Unsalted hashes are not permitted.

3. **At-Rest Protection (`IKeyProtector`):**
   - `DpapiKeyProtector` (`[SupportedOSPlatform("windows")]`): Wraps/unwraps bytes using `ProtectedData.Protect` / `ProtectedData.Unprotect` with `DataProtectionScope.CurrentUser` so only processes running under the same Windows user account can unwrap `config/default-key.dat`.
   - `PlainKeyProtector`: Pass-through protector used on Unix platforms (where atomic file creation applies `UnixFileMode.UserRead | UnixFileMode.UserWrite` `0600` permissions) and in unit tests.
   - Select the singleton `IKeyProtector` in DI via `OperatingSystem.IsWindows()` (`DpapiKeyProtector` on Windows, `PlainKeyProtector` otherwise).

4. **Store Semantics (`IApiKeyStore` & `ApiKeyStore`):**
   - **File Paths:**
     - `DefaultKeyFilePath`: `<ContentRoot>/config/default-key.dat`
     - `UserKeysFilePath`: `<ContentRoot>/config/api-keys.json`
   - **Startup Eager Initialization + Lazy Safety (Fail-Open):**
     - `Program.cs` must call `app.Services.GetRequiredService<IApiKeyStore>().GetDefaultKey();` during startup initialization so `<ContentRoot>/config/default-key.dat` is created on disk immediately when `NightCodeForge` starts (before `NightCodeAssay` attempts to read `default-key.dat` from disk).
     - On first access (`GetDefaultKey()`, `GetDefaultKeyGeneratedAt()`, `RotateDefaultKeyAsync()`, or `ValidateKey(...)`), if `config/default-key.dat` is missing, corrupt, or cannot be unwrapped, `ApiKeyStore` generates a fresh 43-char base64url key, atomically writes the protected JSON envelope `{"key": "<rawKey>", "generatedAt": "<ISO-8601-UTC>"}` (with `0600` owner-only permissions on Unix) to `config/default-key.dat`, and logs generation at `Information` level. Startup is never blocked by a corrupt key file.
     - `ApiKeyStore.ValidateKey(string? presentedKey)` must call `EnsureLoaded()` before checking `presentedKey` (after null/whitespace guard or before returning false on invalid format) so `default-key.dat` is also guaranteed to exist in isolated test hosts on the first request.
   - **Validation (`ValidateKey(string? presentedKey)`):**
     - Calls `EnsureLoaded()`.
     - Rejects null/whitespace or structurally invalid keys via `ApiKeyCrypto.IsValidApiKeyFormat`.
     - Compares `presentedKey` against `_defaultKey` using `CryptographicOperations.FixedTimeEquals`.
     - If not matched, checks non-expired entries in `config/api-keys.json` by computing the salted SHA-256 hash of `presentedKey` with each entry's stored salt and comparing against its stored hash using `CryptographicOperations.FixedTimeEquals` (entries without a valid salt are rejected).
   - **`AGENTS.md` Constructor & Error-Handling Compliance:**
     - Constructors on `ApiKeyStore` must NOT use optional parameters (`= null`). Provide explicit constructor overloads:
       - `public ApiKeyStore(IHostEnvironment hostEnvironment, IKeyProtector keyProtector, ILogger<ApiKeyStore> logger)` (for DI)
       - `public ApiKeyStore(string contentRootPath, IKeyProtector keyProtector, ILogger<ApiKeyStore> logger)` (for unit tests)
     - No bare `catch { }` blocks without logging or explicit exception filters (including temp-file cleanup, which must catch and log at `Debug` level).

```csharp
namespace NightCodeForge.Core.Security;

public interface IKeyProtector
{
    byte[] Protect(byte[] plaintext);
    byte[] Unprotect(byte[] protectedData);
}

public interface IApiKeyStore
{
    string DefaultKeyFilePath { get; }
    string UserKeysFilePath { get; }
    string GetDefaultKey();
    DateTimeOffset GetDefaultKeyGeneratedAt();
    Task<string> RotateDefaultKeyAsync();
    bool ValidateKey(string? presentedKey);
}
```

---

### 2.2 Companion Authentication Filter (`CompanionApiKeyEndpointFilter`) & Route Group

Map all endpoints under a `/api/companion` route group in `Program.cs` with `.AddEndpointFilter<CompanionApiKeyEndpointFilter>()` and `.DisableAntiforgery()`:
1. Extract key from `Authorization: Bearer <token>` (case-insensitive scheme prefix) or fallback header `X-Api-Key: <token>`.
2. Validate via `IApiKeyStore.ValidateKey(token)`.
3. If missing or invalid, return HTTP `401 Unauthorized` with JSON body `{"error": "Unauthorized: valid NightCodeForge API key required."}` and `WWW-Authenticate: Bearer`.
4. **StatusCodePagesMiddleware Compatibility:** Because `Program.cs` registers `app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true)`, every `401 Unauthorized`, `404 Not Found`, and `400 Bad Request` response returned under `/api/companion/*` MUST write a JSON response body (`{"error": "..."}`, e.g., `Results.Json(..., statusCode: ...)`), never an empty body, so `StatusCodePagesMiddleware` does not intercept empty-body error status codes and rewrite them to HTML.

---

### 2.3 Companion HTTP Endpoints (`/api/companion/*`)

#### 1. `GET /api/companion/config`
Reads the live LLM inference configuration via `IOptionsMonitor<LlmProviderOptions>.CurrentValue` so companion tools (`NightCodeAssay`) can connect to the same Ollama or OpenAI-compatible provider without duplicating configuration:

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
- `endpoint` returns `LlmProviderOptions.EffectiveEndpoint`.
- `architectModelId` returns `LlmProviderOptions.ArchitectModelId` when non-whitespace, falling back to `LlmProviderOptions.DefaultModelId`.
- `coderModelId` returns `LlmProviderOptions.CoderModelId` when non-whitespace, falling back to `LlmProviderOptions.DefaultModelId`.
- `openAiCompatible` returns `{ endpoint, apiKey, organizationId }` from `LlmProviderOptions.OpenAiCompatible`.
- Note: `NightCodeAssay` maps both `"Ollama"` and `"OllamaRouter"` to standard `OllamaSharp` (`OllamaApiClient`) without any router headers, and `"OpenAiCompatible"` to `Microsoft.Extensions.AI.OpenAI`.

#### 2. `GET /api/companion/workspaces`
Queries `IWorkspaceRepository.GetAllAsync(cancellationToken)` and `ITaskRepository.GetByStatusAsync(TaskOverallStatus.Ready, cancellationToken)` once (avoiding N+1 per-workspace queries), filters pending review tasks via `TaskBadgeCounter.IsPendingHumanReview`, and returns all registered workspaces ordered by `Name` (`StringComparer.OrdinalIgnoreCase`) along with their `pendingReviewTaskCount`:

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
  3. Build the report title: use `request.Title.Trim()` if provided, otherwise `"Assay Report #{DateTime.UtcNow:MMdd-HHmm}"`.
  4. Resolve `baseBranch`: use `request.BaseBranch.Trim()` if provided, otherwise `!string.IsNullOrWhiteSpace(workspace.DefaultBaseBranch) ? workspace.DefaultBaseBranch : "main"`.
  5. Store the staged triage draft in `ICompanionTriageDraftStore` so the HTTP endpoint responds immediately (`201 Created`) without blocking on a long-running sandboxed agent run.
  6. Return HTTP `201 Created` (`Results.Created(triageUrl, response)`):
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

Enhance `Triage.razor` and `TriageSubmissionModel` to support direct deep-linking to a staged companion triage draft while adhering to `AGENTS.md` UI/Presentation separation:
1. Extract draft-loading logic into `TriageSubmissionModel.TryApplyCompanionDraft(ICompanionTriageDraftStore draftStore, int? activeWorkspaceId, string? draftId, ref string? lastAppliedDraftId)` in `src/NightCodeForge.Web/Services/TriageViewModels.cs`:
   - Returns `false` if `string.IsNullOrWhiteSpace(draftId)`, if `activeWorkspaceId` is null, or if `string.Equals(lastAppliedDraftId, draftId, StringComparison.Ordinal)`.
   - Looks up `draftStore.GetDraft(draftId)`; if found and `draft.WorkspaceId == activeWorkspaceId.Value`, sets `Title = draft.Title`, `Description = draft.ReportMarkdown`, `BaseBranch = draft.BaseBranch`, clears `ExternalUrl = null` and `StackTrace = null`, records `lastAppliedDraftId = draftId`, and returns `true`.
   - Tracking `lastAppliedDraftId` prevents user edits or form resets in `Triage.razor` from being overwritten on subsequent component re-renders unless `DraftId` changes.
2. In `Triage.razor`:
   - Add `[SupplyParameterFromQuery(Name = "draftId")] public string? DraftId { get; set; }` and `@inject ICompanionTriageDraftStore CompanionTriageDraftStore`.
   - Maintain `private string? _lastAppliedDraftId;` and call `_model.TryApplyCompanionDraft(CompanionTriageDraftStore, WorkspaceState.ActiveWorkspace?.Id, DraftId, ref _lastAppliedDraftId)` in `OnInitializedAsync()` / `OnParametersSet()` (and when workspace activates) so the exploratory report is immediately loaded in the **Submit External Report** form for the maintainer to review and click **🛡️ Launch Sandboxed Triage ➔**.
3. Update `tests/NightCodeForge.Web.Tests/TriagePageTests.cs` constructor DI setup to register `ICompanionTriageDraftStore` (`new CompanionTriageDraftStore(TimeProvider.System)`) when `@inject ICompanionTriageDraftStore` is added to `Triage.razor`.

---

## 3. Implementation Checklist & Deliverables

- [ ] Add `System.Security.Cryptography.ProtectedData` (`9.0.0`) to `NightCodeForge.Core.csproj` and implement `ApiKeyCrypto`, `IKeyProtector` (`DpapiKeyProtector`, `PlainKeyProtector`), and `IApiKeyStore` in `NightCodeForge.Core.Security`, plus `ApiKeyStore` in `NightCodeForge.Infrastructure.Security` writing `<ContentRoot>/config/default-key.dat` and `<ContentRoot>/config/api-keys.json`.
- [ ] Implement `CompanionTriageDraft`, `ICompanionTriageDraftStore`, and `CompanionTriageDraftStore` in `NightCodeForge.Core.Services`.
- [ ] Register `IKeyProtector`, `IApiKeyStore`, and `ICompanionTriageDraftStore` as singletons in DI (`ServiceCollectionExtensions` / `Program.cs`), and eagerly invoke `IApiKeyStore.GetDefaultKey()` during `Program.cs` startup initialization.
- [ ] Implement `CompanionEndpoints` (`/api/companion/config`, `/api/companion/workspaces`, `POST /api/companion/workspaces/{workspaceId:int}/triage`) protected by `CompanionApiKeyEndpointFilter` (with `.DisableAntiforgery()` and JSON error bodies on all 4xx responses) and map them in `Program.cs`.
- [ ] Add `TriageSubmissionModel.TryApplyCompanionDraft` in `TriageViewModels.cs` and wire `?draftId={id}` query parameter handling in `Triage.razor` (updating `TriagePageTests.cs` DI setup).
- [ ] Add unit and integration tests in `NightCodeForge.Infrastructure.Tests` and `NightCodeForge.Web.Tests` covering `ApiKeyCrypto` (including salted SHA-256 hashing/verification), `ApiKeyStore` load/rotation/validation, `CompanionTriageDraftStore` TTL & retrieval, `401`/`404`/`400` JSON responses on `/api/companion/*` requests, config/workspace discovery payloads, triage draft creation, and `?draftId={id}` deep-link loading in `TriageSubmissionModel` / `TriagePageTests`.
