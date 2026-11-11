# 🐙 Step 12.11A: GitHub App Device Flow, DPAPI Token Store, REST Client & Workspace Settings UI

> **Feature Implementation Step (Part 1 of 4 — Authentication, Token Store, Client & Settings UI)** *(formerly Step 11.10)*  
> *Parent Plan:* [Features & Integrations Roadmap](README.md)  
> *Companion Steps:* [Step 12.11B: Outbound PR Delivery](STEP_12_11B_GITHUB_PULL_REQUEST_DELIVERY.md) | [Step 12.11C: Untrusted Issue Intake & Triage](STEP_12_11C_GITHUB_UNTRUSTED_ISSUE_INTAKE_AND_TRIAGE.md) | [Step 12.11D: Bi-Directional Task Mirroring & Multi-User Claiming](STEP_12_11D_GITHUB_TASK_MIRRORING_AND_MULTI_USER_CLAIMING.md)  
> *Project Target:* `src/NightCodeForge.Infrastructure/GitHub`, `src/NightCodeForge.Core/Domain`, `src/NightCodeForge.Web`, `tests/NightCodeForge.Infrastructure.Tests` | *Status:* Planned | *Target Framework:* .NET 10 (C# 13)  
> *Aligns With:* [AGENTS.md](../../AGENTS.md), [docs/PLAN.md](../PLAN.md)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Objective

Provide a self-contained, **fully optional** GitHub integration foundation consisting of **GitHub App Device Flow** authentication, a DPAPI-protected token store, a typed GitHub REST client (`IGitHubClientService`), per-workspace configuration (`GitHubIntegrationSettings`), and the `/settings` configuration UI (`GitHubSettingsTab.razor`)—with **zero impact on existing task orchestration or Git pipelines**:

1. **GitHub App Device Flow, Least-Privilege Permissions & Dedicated DPAPI-Protected Token Store (`IGitHubDeviceAuthService`, `IGitHubTokenStore`):**
   - Authenticate developers using a **GitHub App** (rather than a legacy OAuth App or manual Personal Access Token) via the [GitHub App Device Flow](https://docs.github.com/en/apps/creating-github-apps/writing-code-for-a-github-app/building-a-cli-with-a-github-app) (`ClientId = "Iv23lienpd2ZF3rkKyfg"`).
   - **Blast-Radius & Right Management Isolation:** Using a GitHub App instead of an OAuth App enforces fine-grained repository permissions (`Contents: Read & write`, `Pull requests: Read & write`, `Issues: Read & write`, `Administration: Read & write` for optional remote repository creation in [Step 12.32A](STEP_12_32A_WORKSPACE_WIZARD_REPO_DISCOVERY_AND_REMOTE_PROJECT_PICKER.md)/[12.32B](STEP_12_32B_WORKSPACE_TEMPLATES_NATIVE_CLI_SCAFFOLDING_AND_REMOTE_PUBLISH.md), `Metadata: Read-only`), restricts access strictly to the intersection of the authenticated user's rights and the specific repositories where the GitHub App is installed, and issues short-lived **8-hour user access tokens (`ghu_...`)** with rotating refresh tokens (`ghr_...`) to strictly limit the impact of a leaked token.
   - Persist the issued user-to-server token payload in a **dedicated file** (`%APPDATA%\NightCodeForge\config\github-oauth-token.dat` on Windows; `~/.config/NightCodeForge/config/github-oauth-token.dat` or platform `Environment.SpecialFolder.ApplicationData` equivalent on Linux/macOS), strictly separated from `GitHubIntegrationSettings`.
   - On Windows, encrypt and decrypt the token file at rest using **DPAPI** (`ProtectedData.Protect` / `ProtectedData.Unprotect` with `DataProtectionScope.CurrentUser` via the `System.Security.Cryptography.ProtectedData` package). On Unix platforms, enforce `0600` (`UserRead | UserWrite`) file permissions.
2. **GitHub Client Abstraction (`IGitHubClientService`) & Workspace Settings (`GitHubIntegrationSettings`):**
   - Provide a clean, DI-registered GitHub REST client resolving refresh-aware credentials from `IGitHubDeviceAuthService` / `IGitHubTokenStore` with fail-fast validation and per-workspace configuration persisted on the [`Workspace`](../../src/NightCodeForge.Core/Domain/Workspace.cs) entity and editable in `/settings`.

*(Outbound PR delivery via `MergeMode.ExternalPullRequest` is specified in [Step 12.11B](STEP_12_11B_GITHUB_PULL_REQUEST_DELIVERY.md); inbound untrusted GitHub Issue triage is in [Step 12.11C](STEP_12_11C_GITHUB_UNTRUSTED_ISSUE_INTAKE_AND_TRIAGE.md); bi-directional task mirroring and multi-user issue claiming are in [Step 12.11D](STEP_12_11D_GITHUB_TASK_MIRRORING_AND_MULTI_USER_CLAIMING.md).)*

---

## 2. Configuration, GitHub App Device Flow & Token Storage

### 2.1 Workspace Configuration (`GitHubIntegrationSettings`)
Persisted per workspace on the [`Workspace`](../../src/NightCodeForge.Core/Domain/Workspace.cs) entity (stored via EF Core JSON value conversion in [`AppDbContext`](../../src/NightCodeForge.Infrastructure/Data/AppDbContext.cs), with an EF Core migration and baseline check in `BaselineExistingPreMigrationDatabaseAsync`, and copied in [`EfCoreWorkspaceRepository`](../../src/NightCodeForge.Infrastructure/Data/Repositories/EfCoreWorkspaceRepository.cs)). **Credentials/tokens are never stored in `GitHubIntegrationSettings` or SQLite**:
```csharp
public class GitHubIntegrationSettings
{
    public const string DefaultClientId = "Iv23lienpd2ZF3rkKyfg";

    public bool Enabled { get; set; } = false;
    public string ClientId { get; set; } = DefaultClientId; // NightCodeForge GitHub App Client ID for Device Flow
    public string? RepositoryOwner { get; set; }
    public string? RepositoryName { get; set; }
    
    // Sub-feature A: Untrusted Issue Intake & Triage (Step 12.11C)
    public bool IssueTriageEnabled { get; set; } = false;
    public GitHubIssueTriageScope IssueTriageScope { get; set; } = GitHubIssueTriageScope.AssignedToMeOrUnassigned;
    public string? IssueFilterLabel { get; set; }
    public int PollingIntervalMinutes { get; set; } = 15;
    public List<int> TriagedIssueNumbers { get; set; } = new(); // Local watermark for triaged issues (including 0-proposal non-actionable issues when SyncStatusLabels is false)

    // Sub-feature B: PR Delivery (Step 12.11B)
    public bool PullRequestDeliveryEnabled { get; set; } = false;
    public bool CreateAsDraft { get; set; } = false;
    public bool CloseTaskOnPrMerge { get; set; } = true;

    // Sub-feature C: Task Mirroring & Multi-User Coordination (Step 12.11D)
    public bool TaskMirroringEnabled { get; set; } = false;
    public bool CreateGitHubIssuesForLocalTasks { get; set; } = false;
    public bool AutoAssignIssueOnStart { get; set; } = true;
    public bool SyncStatusLabels { get; set; } = true;
    public bool PostMilestoneComments { get; set; } = true;
}

public enum GitHubIssueTriageScope
{
    AssignedToMeOrUnassigned = 0, // Default: skips issues assigned to other GitHub users
    AssignedToMeOnly = 1,         // Only triages issues explicitly assigned to the authenticated user
    AllOpenIssues = 2             // Triages all matching open issues regardless of assignee
}
```

### 2.2 GitHub App Device Flow, Fine-Grained Permissions & 8-Hour Expiring Token Refresh (`IGitHubDeviceAuthService`)
Instead of using a legacy OAuth App (which grants broad `repo`-wide OAuth scopes across all of a user's repositories) or requiring developers to manually manage Personal Access Tokens, authentication uses a **GitHub App** following [Building a CLI with a GitHub App](https://docs.github.com/en/apps/creating-github-apps/writing-code-for-a-github-app/building-a-cli-with-a-github-app) (`ClientId = "Iv23lienpd2ZF3rkKyfg"`):

- **Why a GitHub App Instead of an OAuth App (Least Privilege & Leaked-Token Blast Radius):**
  - **Fine-Grained Permissions (No `scope` Parameter):** Permissions are defined on the GitHub App registration itself (`Contents: Read & write`, `Pull requests: Read & write`, `Issues: Read & write`, `Metadata: Read-only`) rather than requesting coarse OAuth scopes. Device Flow requests do **not** pass a `scope` parameter.
  - **Repository Installation Scoping:** A GitHub App user access token (`ghu_...`) can only act on repositories where **both** the authenticated user has access **and** the NightCodeForge GitHub App is explicitly installed. If a token is ever leaked, its blast radius is strictly bounded to the installed repositories and the app's fine-grained permissions.
  - **Short-Lived Tokens by Default:** Issued user access tokens (`ghu_...`) expire after **8 hours** (`28,800` seconds), paired with a single-use rotating refresh token (`ghr_...`, valid for **6 months**) that can be refreshed without a `client_secret`.

1. **Request Device & User Verification Codes (`RequestDeviceCodeAsync`):**
   - Sends `POST https://github.com/login/device/code` (`Accept: application/json`) with `client_id = "Iv23lienpd2ZF3rkKyfg"` (omitting `scope`, as GitHub Apps govern permissions at the app/installation level).
   - Returns `GitHubDeviceCodeResponse` (`DeviceCode`, `UserCode`, `VerificationUri` = `https://github.com/login/device`, `ExpiresIn`, `Interval`).
2. **Developer Verification & App Installation UI in `/settings` (`GitHubSettingsTab.razor`):**
   - Displays the `user_code` (e.g. `WDJB-MJHT`) with a 1-click **Copy Code** button and an **Open GitHub Device Activation** link (`https://github.com/login/device`).
   - Also provides a direct link to install/configure the NightCodeForge GitHub App on the target repository (`RepositoryOwner`/`RepositoryName`) if the app is not yet installed on that repository.
   - Keeps presentation logic separated from HTTP/polling orchestration per [AGENTS.md](../../AGENTS.md) UI separation rules.
3. **Token Polling (`PollForAccessTokenAsync`):**
   - Polls `POST https://github.com/login/oauth/access_token` (`Accept: application/json`) with `client_id = "Iv23lienpd2ZF3rkKyfg"`, `device_code`, and `grant_type=urn:ietf:params:oauth:grant-type:device_code` at the server-specified `interval`.
   - Handles standard Device Flow states:
     - `authorization_pending`: Waits `interval` seconds before next poll.
     - `slow_down`: Increases polling interval by 5 seconds (or honors returned `interval`) before retrying.
     - `expired_token` / `access_denied`: Terminates polling and surfaces a clear actionable status in the UI.
   - Upon authorization, receives the expiring user-to-server token payload (`access_token` `ghu_...` valid for **8 hours / 28,800s**, `expires_in`, `refresh_token` `ghr_...` valid for **6 months**, `refresh_token_expires_in`), fetches the authenticated user metadata (`GET https://api.github.com/user`), and persists the full payload via `IGitHubTokenStore`.
4. **8-Hour Access Token Expiration & Refresh Token Rotation (`RefreshAccessTokenAsync` / `GetValidAccessTokenAsync`):**
   - Because GitHub App user access tokens (`ghu_...` issued for `Iv23lienpd2ZF3rkKyfg`) **expire after 8 hours**, every outbound GitHub API call and authenticated Git operation must obtain its token through a refresh-aware provider (`GetValidAccessTokenAsync`):
     - **Proactive Refresh Window:** If `AccessTokenExpiresAtUtc <= UtcNow + TimeSpan.FromMinutes(5)`, automatically exchanges the `refresh_token` before issuing the request.
     - **Refresh Endpoint:** Sends `POST https://github.com/login/oauth/access_token` (`Accept: application/json`) with `client_id = "Iv23lienpd2ZF3rkKyfg"`, `grant_type = "refresh_token"`, and `refresh_token = currentToken.RefreshToken` (no `client_secret` required for GitHub App Device Flow refresh).
     - **Single-Flight Concurrency Lock (`SemaphoreSlim(1, 1)`):** GitHub **rotates and invalidates** the old `refresh_token` on every refresh response, returning both a new `access_token` and a new `refresh_token`. Refresh operations must be serialized behind a process-wide `SemaphoreSlim(1, 1)` (re-reading the token after acquiring the lock) so concurrent callers (e.g., Hangfire background sync jobs, `TaskOrchestrator`, and UI requests) never race with a consumed `refresh_token`.
     - **Atomic Persistence:** Immediately persists the newly rotated `access_token`, `AccessTokenExpiresAtUtc`, `refresh_token`, and `RefreshTokenExpiresAtUtc` to `IGitHubTokenStore` before releasing the semaphore.
     - **Reactive `401 Unauthorized` Recovery & Fail-Fast:** If an API call returns `401 Unauthorized`, attempts a single synchronized refresh; if the `refresh_token` itself is expired (`RefreshTokenExpiresAtUtc <= UtcNow`) or rejected (`bad_refresh_token`), throws a domain-specific `GitHubAuthenticationException` instructing the user to re-authenticate in `/settings`.

### 2.3 Dedicated Token File & Windows DPAPI Protection (`IGitHubTokenStore`)
The OAuth token payload (`GitHubOAuthTokenPayload`: `AccessToken`, `AccessTokenExpiresAtUtc`, `RefreshToken`, `RefreshTokenExpiresAtUtc`, `TokenType`, `Scope`, `AuthenticatedUserLogin`) must **never** be stored inside `GitHubIntegrationSettings`, `appsettings.json`, or the SQLite database:

- **Dedicated File Location:**
  - Default path resolved via `Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NightCodeForge", "config", "github-oauth-token.dat")` (`%APPDATA%\NightCodeForge\config\github-oauth-token.dat` on Windows; `~/.config/NightCodeForge/config/github-oauth-token.dat` on Linux; `~/Library/Application Support/NightCodeForge/config/github-oauth-token.dat` on macOS), with support for an explicit file path override in tests.
- **Windows DPAPI Encryption:**
  - Reference `System.Security.Cryptography.ProtectedData` in [`NightCodeForge.Infrastructure.csproj`](../../src/NightCodeForge.Infrastructure/NightCodeForge.Infrastructure.csproj).
  - On Windows (`OperatingSystem.IsWindows()`), serialized token bytes are encrypted before writing using `System.Security.Cryptography.ProtectedData.Protect(plaintextBytes, optionalEntropy: null, DataProtectionScope.CurrentUser)` and decrypted on read via `ProtectedData.Unprotect(..., DataProtectionScope.CurrentUser)`.
- **Non-Windows File Permissions:**
  - On Linux/macOS, the file is created and restricted to `UnixFileMode.UserRead | UnixFileMode.UserWrite` (`0600`).
- **Atomic File Writes:**
  - Writes to a temporary sibling file (`.tmp`) and atomically replaces `github-oauth-token.dat` (`File.Move(..., overwrite: true)`) so a crash or power loss during an 8-hour token refresh never corrupts the stored `refresh_token`.
- **Fail-Fast & Sign-Out Lifecycle:**
  - If a GitHub operation is invoked when no valid token exists in `IGitHubTokenStore`, when decryption fails, or when both access and refresh tokens are expired, throws a domain-specific `GitHubAuthenticationException` (no silent fallback).
  - Calling `ClearTokenAsync` deletes the dedicated token file to disconnect the GitHub account cleanly.

### 2.4 GitHub REST Client Abstraction (`IGitHubClientService`)
Implemented in `src/NightCodeForge.Infrastructure/GitHub` using `HttpClient` (`Accept: application/vnd.github+json`, `X-GitHub-Api-Version: 2022-11-28`, `User-Agent: NightCodeForge`) resolving refresh-aware credentials via `IGitHubDeviceAuthService.GetValidAccessTokenAsync`:
- **Pull Requests:**
  - `CreatePullRequestAsync(string owner, string repo, CreateGitHubPullRequestRequest request, CancellationToken ct)` (`POST /repos/{owner}/{repo}/pulls`)
  - `UpdatePullRequestAsync(string owner, string repo, int prNumber, UpdateGitHubPullRequestRequest request, CancellationToken ct)` (`PATCH /repos/{owner}/{repo}/pulls/{prNumber}`)
  - `GetPullRequestAsync(string owner, string repo, int prNumber, CancellationToken ct)` (`GET /repos/{owner}/{repo}/pulls/{prNumber}`, including `Merged` and `MergeCommitSha`)
- **Issues, Labels & Comments:**
  - `ListIssuesAsync(string owner, string repo, GitHubIssueFilter filter, CancellationToken ct)` (`GET /repos/{owner}/{repo}/issues`, filtering out Pull Requests where `pull_request != null`)
  - `GetIssueAsync(string owner, string repo, int issueNumber, CancellationToken ct)` (`GET /repos/{owner}/{repo}/issues/{issueNumber}`)
  - `CreateIssueAsync(string owner, string repo, CreateGitHubIssueRequest request, CancellationToken ct)` (`POST /repos/{owner}/{repo}/issues`)
  - `UpdateIssueAsync(string owner, string repo, int issueNumber, UpdateGitHubIssueRequest request, CancellationToken ct)` (`PATCH /repos/{owner}/{repo}/issues/{issueNumber}` for state, labels, and assignees)
  - `AddIssueCommentAsync(string owner, string repo, int issueNumber, string body, CancellationToken ct)` (`POST /repos/{owner}/{repo}/issues/{issueNumber}/comments`)
- **Repository Probe / Connection Test:**
  - `GetRepositoryAsync(string owner, string repo, CancellationToken ct)` (`GET /repos/{owner}/{repo}`) to validate repository access and GitHub App installation status from `/settings`.

---

## 3. Implementation Checklist & Deliverables

- [ ] **GitHub App Device Flow, Refresh Token Rotation & DPAPI Token Store (`IGitHubDeviceAuthService`, `IGitHubTokenStore`):**
  - [ ] Add `System.Security.Cryptography.ProtectedData` to `NightCodeForge.Infrastructure.csproj`.
  - [ ] Implement `IGitHubDeviceAuthService` with `DefaultClientId = "Iv23lienpd2ZF3rkKyfg"` (`POST https://github.com/login/device/code` without `scope` parameter and polling `POST https://github.com/login/oauth/access_token` with `authorization_pending` / `slow_down` handling).
  - [ ] Implement proactive and reactive 8-hour access token refresh (`grant_type=refresh_token`) guarded by a single-flight `SemaphoreSlim(1, 1)` to safely persist rotated `refresh_token` values without race conditions.
  - [ ] Implement `IGitHubTokenStore` persisting the full GitHub App user token & refresh token payload atomically in `%APPDATA%\NightCodeForge\config\github-oauth-token.dat` (`Environment.SpecialFolder.ApplicationData`), encrypted at rest with Windows **DPAPI** (`ProtectedData.Protect` / `ProtectedData.Unprotect` under `DataProtectionScope.CurrentUser`) on Windows and `0600` file permissions on Unix.
- [ ] **Workspace Settings Model & EF Core Persistence (`GitHubIntegrationSettings`):**
  - [ ] Add `GitHubIntegrationSettings` and `GitHubIssueTriageScope` on [`Workspace`](../../src/NightCodeForge.Core/Domain/Workspace.cs).
  - [ ] Add EF Core migration, update `AppDbContext.BaselineExistingPreMigrationDatabaseAsync`, and update `EfCoreWorkspaceRepository`.
- [ ] **GitHub Client Abstraction (`IGitHubClientService`):**
  - [ ] Implement `IGitHubClientService` in `NightCodeForge.Infrastructure/GitHub` using `HttpClient` resolving refresh-aware credentials via `IGitHubDeviceAuthService.GetValidAccessTokenAsync` with strict constructor DI and fail-fast domain exceptions (`GitHubAuthenticationException`, `GitHubApiException`).
- [ ] **Workspace Settings UI (`GitHubSettingsTab.razor`):**
  - [ ] Add GitHub Integration workspace settings tab (`GitHubSettingsTab.razor` in [`Settings.razor`](../../src/NightCodeForge.Web/Components/Pages/Settings.razor) under Workspace Scope) with `RepositoryOwner`, `RepositoryName`, connection test button, interactive GitHub App Device Flow connect/disconnect UI (`user_code`, copy button, `https://github.com/login/device` link, GitHub App repository installation link, connected user status badge), and sub-feature toggles.
- [ ] **Unit & Mock Tests:**
  - [ ] Unit tests for `IGitHubDeviceAuthService` (device code request, polling state machine, 8-hour access token expiration refresh, rotated `refresh_token` persistence, and concurrent refresh deduplication).
  - [ ] Unit tests for `IGitHubTokenStore` (atomic file persistence, missing/corrupt token fail-fast, and Windows DPAPI round-trip protection).
  - [ ] Unit tests for `IGitHubClientService` (PR, Issue, Comment, and Repository endpoints, plus reactive `401 Unauthorized` refresh retry).

