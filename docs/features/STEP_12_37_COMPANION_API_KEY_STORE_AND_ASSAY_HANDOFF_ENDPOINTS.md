# 🧪 Step 12.37: Companion API Key Store (`default-key.dat`) & NightCode Assay Handoff Endpoints (`/api/companion/*`)

> **Feature Implementation Step (Companion Tool Authentication, Configuration Discovery & Ideation Handoff)**  
> *Parent Plan:* [Features & Integrations Roadmap](README.md)  
> *Source Policy:* [AGENTS.md](../../AGENTS.md)  
> *Project Target:* `src/NightCodeForge.Core`, `src/NightCodeForge.Infrastructure`, `src/NightCodeForge.Web`, `tests/NightCodeForge.Infrastructure.Tests`, `tests/NightCodeForge.Web.Tests` | *Status:* Planned | *Target Framework:* .NET 10 (C# 13)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Objective

**NightCode Assay** is a lightweight companion desktop application (`C:\Users\Julien\source\repos\jetelain\NightCodeAssay`) designed for manual exploratory testing of software built by **NightCodeForge**. To avoid duplicating LLM configuration or repository settings across both applications while enabling 1-click feedback handoff into NightCodeForge's Ideation chat, this step introduces:

1. **OllamaRouter-Style Local API Key Store (`IApiKeyStore`, `IKeyProtector`, `ApiKeyCrypto`):**
   - Automatically generates and persists a 32-byte (256-bit) base64url default API key in `<ContentRoot>/config/default-key.dat` wrapped at rest via `IKeyProtector` (`DpapiKeyProtector` using Windows DPAPI `DataProtectionScope.CurrentUser` on Windows; `PlainKeyProtector` with `0600` owner-only file permissions on Unix), plus optional operator-created user keys in `<ContentRoot>/config/api-keys.json` (storing SHA-256 hashes only).
   - Follows the exact same envelope format (`{"key": "...", "generatedAt": "..."}`) and constant-time validation (`CryptographicOperations.FixedTimeEquals`) as `OllamaRouter`, enabling local companion tools running under the same OS user (`NightCodeAssay`, `ncf` CLI) to authenticate automatically without manual key copy-paste.
2. **Bearer / API Key Authentication Filter for `/api/companion/*`:**
   - Protects all `/api/companion/*` endpoints via constant-time key validation (`Authorization: Bearer <key>` or `X-Api-Key: <key>`), returning `401 Unauthorized` when missing or invalid.
3. **Companion Configuration & Workspace Discovery Endpoints (`GET /api/companion/config` & `GET /api/companion/workspaces`):**
   - Allows `NightCodeAssay` to discover the active LLM provider (`ProviderType`, `EffectiveEndpoint`, `DefaultModelId`, `ArchitectModelId`, `CoderModelId`, and `OpenAiCompatible` endpoint/API key) and the list of registered workspaces (`Id`, `Name`, `Emoji`, `ColorCode`, `RepositoryPath`, `DefaultBaseBranch`).
4. **1-Click Exploratory Report $\rightarrow$ Ideation Session Handoff (`POST /api/companion/workspaces/{workspaceId:int}/ideation`):**
   - Creates a new Ideation `Conversation` for the target workspace pre-seeded with the exploratory test report Markdown, and supports selecting/loading that conversation via `?conversationId={id}` in `Ideation.razor` with the report pre-filled in the prompt box (or auto-dispatched when `autoSend: true`).

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

#### 3. `POST /api/companion/workspaces/{workspaceId:int}/ideation`
Creates a new Ideation session in the specified workspace seeded with an exploratory test report from `NightCodeAssay`:

- **Request Payload (`CreateCompanionIdeationRequest`):**
  ```json
  {
    "title": "Assay Exploratory Report — 2026-11-14 09:05",
    "reportMarkdown": "# Exploratory Test Report — 2026-11-14\n...",
    "sessionDirectoryPath": "C:\\Users\\Julien\\...\\sessions\\2026-11-14_09-05"
  }
  ```
- **Validation & Behavior:**
  1. Verify workspace `{workspaceId}` exists via `IWorkspaceRepository.GetByIdAsync(workspaceId)`; return `404 Not Found` if absent.
  2. Validate `reportMarkdown` is non-empty; return `400 Bad Request` if blank.
  3. Build the conversation title: use `request.Title.Trim()` if provided, otherwise `"Assay Report #{DateTime.UtcNow:MMdd-HHmm}"`.
  4. Build the seeded ideation prompt instructing the Architect agent to analyze the exploratory testing report, inspect referenced code/logs on the base branch, and propose structured backlog tasks (`propose_tasks`):
     ```text
     I just completed an exploratory testing session with NightCode Assay. Please analyze the following findings, correlate them with the codebase, and propose actionable backlog tasks (bugs, UI/UX fixes, or enhancements):

     {reportMarkdown}
     ```
  5. Create the `Conversation` via `ITaskRepository.StartIdeationConversationAsync(workspaceId, title)` and store the seeded prompt in `Conversation.Summary` (persisted via `ITaskRepository.UpdateConversationAsync`) so `Ideation.razor` can pre-fill the chat input when the user opens the session URL in their browser.
  6. Return HTTP `201 Created`:
     ```json
     {
       "conversationId": 42,
       "workspaceId": 1,
       "title": "Assay Exploratory Report — 2026-11-14 09:05",
       "ideationUrl": "/w/1/ideation?conversationId=42"
     }
     ```

---

### 2.4 `Ideation.razor` Deep-Link Support (`?conversationId={id}`)

Enhance `Ideation.razor` to support direct deep-linking to a specific Ideation conversation:
1. Add `[SupplyParameterFromQuery(Name = "conversationId")] public int? ConversationId { get; set; }`.
2. In `ReloadWorkspaceContextAsync()`, when `ConversationId.HasValue` matches a conversation belonging to the active workspace:
   - Load that conversation via `TaskRepository.GetConversationWithTurnsAsync(ConversationId.Value)` and call `LoadConversationIntoSession(conv)`.
   - If the conversation has zero turns (`conv.Turns.Count == 0`) and `!string.IsNullOrWhiteSpace(conv.Summary)`, populate `_userInput = conv.Summary` so the exploratory report is immediately ready in the input box for the maintainer to review and press **Send** (and clear `conv.Summary` once the first turn is dispatched).

---

## 3. Implementation Checklist & Deliverables

- [ ] Implement `ApiKeyCrypto`, `IKeyProtector` (`DpapiKeyProtector`, `PlainKeyProtector`), and `IApiKeyStore` (`ApiKeyStore`) writing `<ContentRoot>/config/default-key.dat` and `<ContentRoot>/config/api-keys.json`.
- [ ] Register `IKeyProtector` and `IApiKeyStore` as singletons in DI (`ServiceCollectionExtensions` / `Program.cs`).
- [ ] Implement `CompanionEndpoints` (`/api/companion/config`, `/api/companion/workspaces`, `POST /api/companion/workspaces/{workspaceId:int}/ideation`) protected by `CompanionApiKeyEndpointFilter` and map them in `Program.cs`.
- [ ] Add `?conversationId={id}` query parameter handling and `Conversation.Summary` seed prompt pre-filling in `Ideation.razor`.
- [ ] Add unit and integration tests in `NightCodeForge.Infrastructure.Tests` and `NightCodeForge.Web.Tests` covering `ApiKeyCrypto`, `ApiKeyStore` load/rotation/validation, `401` rejection on unauthenticated `/api/companion/*` requests, config/workspace discovery payloads, ideation session creation, and `?conversationId={id}` deep-link loading in `IdeationPageTests`.
