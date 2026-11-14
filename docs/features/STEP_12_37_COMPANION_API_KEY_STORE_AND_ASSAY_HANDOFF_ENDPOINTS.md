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
   - Automatically generates and persists a 32-byte (256-bit) base64url default API key in `<ContentRoot>/config/default-key.dat` wrapped at rest via `IKeyProtector` (`DpapiKeyProtector` using Windows DPAPI `DataProtectionScope.CurrentUser` on Windows; `PlainKeyProtector` with `0600` owner-only file permissions on Unix), plus optional operator-created user keys in `<ContentRoot>/config/api-keys.json` (storing SHA-256 hashes only).
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

1. **Key Format (`ApiKeyCrypto`):**
   - Keys are 32 random bytes (`RandomNumberGenerator.GetBytes(32)`) encoded as unpadded base64url (43 characters, alphabet `[A-Za-z0-9_-]`).
   - `IsValidApiKeyFormat(string? key)` performs a fast structural check (length `== 43`, valid base64url characters, decodes to 32 bytes) before constant-time comparison.
   - `HashKey(string key)` computes `SHA256.HashData(Encoding.UTF8.GetBytes(key))` for user-created keys.

2. **At-Rest Protection (`IKeyProtector`):**
   - `DpapiKeyProtector` (`[SupportedOSPlatform("windows")]`): Wraps/unwraps bytes using `ProtectedData.Protect` / `ProtectedData.Unprotect` with `DataProtectionScope.CurrentUser` so only processes running under the same Windows user account can unwrap `config/default-key.dat`.
   - `PlainKeyProtector`: Pass-through protector used on Unix platforms (where atomic file creation applies `UnixFileMode.UserRead | UnixFileMode.UserWrite` `0600` permissions) and in unit tests.

3. **Store Semantics (`IApiKeyStore`):**
   - **File Paths:**
     - `DefaultKeyFilePath`: `<ContentRoot>/config/default-key.dat`
     - `UserKeysFilePath`: `<ContentRoot>/config/api-keys.json`
   - **Lazy Load-or-Create (Fail-Open):**
     - On first access, if `config/default-key.dat` is missing, corrupt, or cannot be unwrapped, generates a fresh 43-char base64url key, atomically writes the protected JSON envelope `{"key": "<rawKey>", "generatedAt": "<ISO-8601-UTC>"}` to `config/default-key.dat`, and logs generation at `Information` level. Startup is never blocked by a corrupt key file.
   - **Validation (`ValidateKey(string? presentedKey)`):**
     - Rejects null/whitespace or structurally invalid keys via `ApiKeyCrypto.IsValidApiKeyFormat`.
     - Compares `presentedKey` against `_defaultKey` using `CryptographicOperations.FixedTimeEquals`.
     - If not matched, hashes `presentedKey` with SHA-256 and compares against non-expired entries in `config/api-keys.json` using `CryptographicOperations.FixedTimeEquals`.

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

### 2.2 Companion Authentication Filter (`CompanionApiKeyEndpointFilter`)

All endpoints under `/api/companion` require a valid API key:
1. Extract key from `Authorization: Bearer <token>` (case-insensitive scheme prefix) or fallback header `X-Api-Key: <token>`.
2. Validate via `IApiKeyStore.ValidateKey(token)`.
3. If missing or invalid, return HTTP `401 Unauthorized` with JSON body `{"error": "Unauthorized: valid NightCodeForge API key required."}` and `WWW-Authenticate: Bearer`.

---

### 2.3 Companion HTTP Endpoints (`/api/companion/*`)

#### 1. `GET /api/companion/config`
Returns the effective LLM inference configuration so companion tools (`NightCodeAssay`) can connect to the same Ollama or OpenAI-compatible provider without duplicating configuration:

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
- `endpoint` returns `LlmProviderOptions.EffectiveEndpoint` (or `LlmProviderOptions.Endpoint` for `Ollama` / `OllamaRouter`).
- Note: `NightCodeAssay` maps both `"Ollama"` and `"OllamaRouter"` to standard `OllamaSharp` (`OllamaApiClient`) without any router headers, and `"OpenAiCompatible"` to `Microsoft.Extensions.AI.OpenAI`.

#### 2. `GET /api/companion/workspaces`
Queries `IWorkspaceRepository.GetAllAsync()` and returns all registered workspaces ordered by `Name`, along with counts of tasks currently in `Ready` (Pending Review) status:

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

#### 3. `POST /api/companion/workspaces/{workspaceId:int}/triage`
Stages a new Triage submission draft in the specified workspace seeded with an exploratory test report from `NightCodeAssay`:

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
  1. Verify workspace `{workspaceId}` exists via `IWorkspaceRepository.GetByIdAsync(workspaceId)`; return `404 Not Found` if absent.
  2. Validate `reportMarkdown` is non-empty; return `400 Bad Request` if blank.
  3. Build the report title: use `request.Title.Trim()` if provided, otherwise `"Assay Report #{DateTime.UtcNow:MMdd-HHmm}"`.
  4. Resolve `baseBranch`: use `request.BaseBranch.Trim()` if provided, otherwise `workspace.DefaultBaseBranch ?? "main"`.
  5. Store the staged triage draft in a singleton `ICompanionTriageDraftStore` (thread-safe in-memory store keeping recent drafts with a 12-character hex/base64url `draftId` and 24-hour TTL) so the HTTP endpoint responds immediately (`201 Created`) without blocking on a long-running sandboxed agent run.
  6. Return HTTP `201 Created`:
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

Enhance `Triage.razor` to support direct deep-linking to a staged companion triage draft:
1. Add `[SupplyParameterFromQuery(Name = "draftId")] public string? DraftId { get; set; }` and `@inject ICompanionTriageDraftStore CompanionTriageDraftStore`.
2. In `OnInitializedAsync()` / `OnParametersSet()`, when `!string.IsNullOrWhiteSpace(DraftId)` matches a draft in `CompanionTriageDraftStore` belonging to the active workspace:
   - Pre-fill `_model.Title = draft.Title`, `_model.Description = draft.ReportMarkdown`, and `_model.BaseBranch = draft.BaseBranch` so the exploratory report is immediately loaded in the **Submit External Report** form for the maintainer to review and click **🛡️ Launch Sandboxed Triage ➔**.

---

## 3. Implementation Checklist & Deliverables

- [ ] Implement `ApiKeyCrypto`, `IKeyProtector` (`DpapiKeyProtector`, `PlainKeyProtector`), and `IApiKeyStore` (`ApiKeyStore`) writing `<ContentRoot>/config/default-key.dat` and `<ContentRoot>/config/api-keys.json`.
- [ ] Register `IKeyProtector`, `IApiKeyStore`, and `ICompanionTriageDraftStore` as singletons in DI (`ServiceCollectionExtensions` / `Program.cs`).
- [ ] Implement `CompanionEndpoints` (`/api/companion/config`, `/api/companion/workspaces`, `POST /api/companion/workspaces/{workspaceId:int}/triage`) protected by `CompanionApiKeyEndpointFilter` and map them in `Program.cs`.
- [ ] Add `?draftId={id}` query parameter handling and `ICompanionTriageDraftStore` form pre-filling in `Triage.razor`.
- [ ] Add unit and integration tests in `NightCodeForge.Infrastructure.Tests` and `NightCodeForge.Web.Tests` covering `ApiKeyCrypto`, `ApiKeyStore` load/rotation/validation, `401` rejection on unauthenticated `/api/companion/*` requests, config/workspace discovery payloads, triage draft creation, and `?draftId={id}` deep-link loading in `Triage` tests.
