# 🐙 Step 12.11A: GitHub Client, Workspace Settings & Pull Request Delivery (`MergeMode.ExternalPullRequest`)

> **Feature Implementation Step (Part 1 of 2 — GitHub Client, Settings UI & Outbound PR Delivery)** *(formerly Step 11.10)*  
> *Parent Plan:* [Features & Integrations Roadmap](README.md)  
> *Companion Step:* [Step 12.11B: GitHub Untrusted Issue Intake & Bi-Directional Task Mirroring](STEP_12_11B_GITHUB_UNTRUSTED_ISSUE_TRIAGE_AND_TASK_MIRRORING.md)  
> *Project Target:* `src/NightCodeForge.Infrastructure/GitHub`, `src/NightCodeForge.Core/Workflow`, `src/NightCodeForge.Web`, `tests/NightCodeForge.Infrastructure.Tests` | *Status:* Planned | *Target Framework:* .NET 10 (C# 13)  
> *Aligns With:* [AGENTS.md](../../AGENTS.md), [docs/PLAN.md](../PLAN.md)

---

## 1. Executive Objective

Provide a modular, **fully optional** GitHub client, **GitHub App Device Flow** authentication, workspace configuration UI, and outbound **Pull Request Delivery Mode (`MergeMode.ExternalPullRequest`)**:

1. **GitHub App Device Flow, Least-Privilege Permissions & Dedicated DPAPI-Protected Token Store (`IGitHubDeviceAuthService`, `IGitHubTokenStore`):**
   - Authenticate developers using a **GitHub App** (rather than a legacy OAuth App or manual Personal Access Token) via the [GitHub App Device Flow](https://docs.github.com/en/apps/creating-github-apps/writing-code-for-a-github-app/building-a-cli-with-a-github-app) (`ClientId = "Iv23lienpd2ZF3rkKyfg"`).
   - **Blast-Radius & Right Management Isolation:** Using a GitHub App instead of an OAuth App enforces fine-grained repository permissions (`Contents: Read & write`, `Pull requests: Read & write`, `Issues: Read & write`, `Metadata: Read-only`), restricts access strictly to the intersection of the authenticated user's rights and the specific repositories where the GitHub App is installed, and issues short-lived **8-hour user access tokens (`ghu_...`)** with rotating refresh tokens (`ghr_...`) to strictly limit the impact of a leaked token.
   - Persist the issued user-to-server token payload in a **dedicated file** (`%APPDATA%\NightCodeForge\config\github-oauth-token.dat` via `Environment.SpecialFolder.ApplicationData` on Windows; `~/.config/nightcodeforge/github-oauth-token.dat` on Linux/macOS), strictly separated from `GitHubIntegrationSettings`.
   - On Windows, encrypt and decrypt the token file at rest using **DPAPI** (`ProtectedData.Protect` / `ProtectedData.Unprotect` with `DataProtectionScope.CurrentUser`). On Unix platforms, enforce `0600` (`UserRead | UserWrite`) file permissions.
2. **GitHub Client Abstraction (`IGitHubClientService`) & Workspace Settings (`GitHubIntegrationSettings`):**
   - Provide a clean, DI-registered GitHub client (`HttpClient` or `Octokit`) resolving credentials from `IGitHubTokenStore` with fail-fast validation and per-workspace toggles in `/settings`.
3. **Pull Request Delivery Mode via `MergeMode.ExternalPullRequest`:**
   - Provide an alternative to direct squash-and-merge into the developer's local working copy: deliver completed tasks via **GitHub Pull Requests**.
   - Pushes the isolated feature branch (`feature/task-{id}`) to the remote GitHub repository.
   - Opens a GitHub Pull Request against `task.BaseBranch` with an automated markdown summary detailing the task objective, stage execution history, and deterministic quality gate metrics (coverage delta, warning ratchet, test results).
   - Surfaces a **"Create / Open Pull Request"** link in the Review Room UI (`Review.razor`).

*(Inbound untrusted GitHub Issue triage and bi-directional `ncf:*` label/comment mirroring are specified in [Step 12.11B](STEP_12_11B_GITHUB_UNTRUSTED_ISSUE_TRIAGE_AND_TASK_MIRRORING.md).)*

---

## 2. Configuration, GitHub App Device Flow & Token Storage

### 2.1 Workspace Configuration (`GitHubIntegrationSettings`)
Stored in workspace settings or JSON configuration. **Credentials/tokens are never stored in `GitHubIntegrationSettings`**:
```csharp
public class GitHubIntegrationSettings
{
    public const string DefaultClientId = "Iv23lienpd2ZF3rkKyfg";

    public bool Enabled { get; set; } = false;
    public string ClientId { get; set; } = DefaultClientId; // NightCodeForge GitHub App Client ID for Device Flow
    public string? RepositoryOwner { get; set; }
    public string? RepositoryName { get; set; }
    
    // Sub-feature A: Untrusted Issue Intake & Triage (Step 12.11B)
    public bool IssueTriageEnabled { get; set; } = false;
    public string? IssueFilterLabel { get; set; }
    public int PollingIntervalMinutes { get; set; } = 15;

    // Sub-feature B: PR Delivery (Step 12.11A)
    public bool PullRequestDeliveryEnabled { get; set; } = false;
    public bool CreateAsDraft { get; set; } = false;
    public bool CloseTaskOnPrMerge { get; set; } = true;

    // Sub-feature C: Task Mirroring (Step 12.11B)
    public bool TaskMirroringEnabled { get; set; } = false;
    public bool SyncStatusLabels { get; set; } = true;
    public bool PostMilestoneComments { get; set; } = true;
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
2. **Developer Verification & App Installation UI in `/settings`:**
   - Displays the `user_code` (e.g. `WDJB-MJHT`) with a 1-click **Copy Code** button and an **Open GitHub Device Activation** link (`https://github.com/login/device`).
   - Also provides a direct link to install/configure the NightCodeForge GitHub App on the target repository (`RepositoryOwner`/`RepositoryName`) if the app is not yet installed on that repository.
3. **Token Polling (`PollForAccessTokenAsync`):**
   - Polls `POST https://github.com/login/oauth/access_token` (`Accept: application/json`) with `client_id = "Iv23lienpd2ZF3rkKyfg"`, `device_code`, and `grant_type=urn:ietf:params:oauth:grant-type:device_code` at the server-specified `interval`.
   - Handles standard Device Flow states:
     - `authorization_pending`: Waits `interval` seconds before next poll.
     - `slow_down`: Increases polling interval by 5 seconds (or honors returned `interval`) before retrying.
     - `expired_token` / `access_denied`: Terminates polling and surfaces a clear actionable status in the UI.
   - Upon authorization, receives the expiring user-to-server token payload (`access_token` `ghu_...` valid for **8 hours / 28,800s**, `expires_in`, `refresh_token` `ghr_...` valid for **6 months**, `refresh_token_expires_in`), fetches the authenticated user metadata (`GET https://api.github.com/user`), and persists the full payload via `IGitHubTokenStore`.
4. **8-Hour Access Token Expiration & Refresh Token Rotation (`RefreshAccessTokenAsync` / `GetValidAccessTokenAsync`):**
   - Because GitHub App user access tokens (`ghu_...` issued for `Iv23lienpd2ZF3rkKyfg`) **expire after 8 hours**, every outbound GitHub API call and `git push` operation must obtain its token through a refresh-aware provider (`GetValidAccessTokenAsync`):
     - **Proactive Refresh Window:** If `AccessTokenExpiresAtUtc <= UtcNow + TimeSpan.FromMinutes(5)`, automatically exchanges the `refresh_token` before issuing the request.
     - **Refresh Endpoint:** Sends `POST https://github.com/login/oauth/access_token` (`Accept: application/json`) with `client_id = "Iv23lienpd2ZF3rkKyfg"`, `grant_type = "refresh_token"`, and `refresh_token = currentToken.RefreshToken` (no `client_secret` required for GitHub App Device Flow refresh).
     - **Single-Flight Concurrency Lock (`SemaphoreSlim(1, 1)`):** GitHub **rotates and invalidates** the old `refresh_token` on every refresh response, returning both a new `access_token` and a new `refresh_token`. Refresh operations must be serialized behind a process-wide `SemaphoreSlim(1, 1)` (re-reading the token after acquiring the lock) so concurrent callers (e.g., Hangfire background sync jobs, `TaskOrchestrator`, and UI requests) never race with a consumed `refresh_token`.
     - **Atomic Persistence:** Immediately persists the newly rotated `access_token`, `AccessTokenExpiresAtUtc`, `refresh_token`, and `RefreshTokenExpiresAtUtc` to `IGitHubTokenStore` before releasing the semaphore.
     - **Reactive `401 Unauthorized` Recovery & Fail-Fast:** If an API call returns `401 Unauthorized`, attempts a single synchronized refresh; if the `refresh_token` itself is expired (`RefreshTokenExpiresAtUtc <= UtcNow`) or rejected (`bad_refresh_token`), throws a domain-specific `GitHubAuthenticationException` instructing the user to re-authenticate in `/settings`.

### 2.3 Dedicated Token File & Windows DPAPI Protection (`IGitHubTokenStore`)
The OAuth token payload (`GitHubOAuthTokenPayload`: `AccessToken`, `AccessTokenExpiresAtUtc`, `RefreshToken`, `RefreshTokenExpiresAtUtc`, `TokenType`, `Scope`, `AuthenticatedUserLogin`) must **never** be stored inside `GitHubIntegrationSettings`, `appsettings.json`, or the SQLite database:

- **Dedicated File Location:**
  - **Windows:** Stored in `%APPDATA%\NightCodeForge\config\github-oauth-token.dat` (`Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NightCodeForge", "config", "github-oauth-token.dat")`).
  - **Linux / macOS:** Stored in `~/.config/nightcodeforge/github-oauth-token.dat` (`Environment.SpecialFolder.ApplicationData` / `~/.nightcodeforge/github-oauth-token.dat`).
- **Windows DPAPI Encryption:**
  - On Windows (`OperatingSystem.IsWindows()`), serialized token bytes are encrypted before writing using `System.Security.Cryptography.ProtectedData.Protect(plaintextBytes, optionalEntropy: null, DataProtectionScope.CurrentUser)` and decrypted on read via `ProtectedData.Unprotect(..., DataProtectionScope.CurrentUser)`.
- **Non-Windows File Permissions:**
  - On Linux/macOS, the file is created and restricted to `UnixFileMode.UserRead | UnixFileMode.UserWrite` (`0600`).
- **Atomic File Writes:**
  - Writes to a temporary sibling file (`.tmp`) and atomically replaces `github-oauth-token.dat` so a crash or power loss during an 8-hour token refresh never corrupts the stored `refresh_token`.
- **Fail-Fast & Sign-Out Lifecycle:**
  - If a GitHub operation is invoked when no valid token exists in `IGitHubTokenStore`, when decryption fails, or when both access and refresh tokens are expired, throws a domain-specific `GitHubAuthenticationException` (no silent fallback).
  - Calling `ClearTokenAsync` deletes the dedicated token file to disconnect the GitHub account cleanly.

### 2.4 Domain Alignment with `MergeMode`
GitHub PR delivery is unified with the core **[`MergeMode`](../../src/NightCodeForge.Core/Domain/Enums.cs)** enumeration:
```csharp
public enum MergeMode
{
    Automatic = 0,             // Auto-squashes and merges to base branch once status reaches Ready
    ManualReviewRequired = 1,  // Maintainer manually reviews diff/tests before triggering merge in UI
    ExternalPullRequest = 2    // Pushes branch to origin and opens a GitHub PR (enabled when GitHub is active)
}
```
- **`ForgeTask.MergeMode`:** Set to `MergeMode.ExternalPullRequest` when PR delivery is selected.
- **`ForgeTask.ExternalSource`:** `"GitHub"`.
- **`ForgeTask.ExternalReferenceId`:** `#123` (Issue or PR number).
- **`ForgeTask.PullRequestUrl`:** URL to created GitHub PR (e.g. `https://github.com/owner/repo/pull/45`).
- **UI Availability:** When `GitHubIntegrationSettings.Enabled && PullRequestDeliveryEnabled` is true and a valid token is present in `IGitHubTokenStore`, `MergeMode.ExternalPullRequest` is enabled as an option across `/tasks/new`, backlog promotion modals, proposal cards, and the task detail drawer.

---

## 3. Pull Request Delivery Specification

### 3.1 Pull Request Generation Flow
When a task reaches completion and `task.MergeMode == MergeMode.ExternalPullRequest`:
1. **Push Branch to GitHub Remote:** [`TaskOrchestrator`](../../src/NightCodeForge.Core/Workflow/TaskOrchestrator.cs) pushes `feature/task-{id}` directly to the remote GitHub repository (using the OAuth token from `IGitHubTokenStore` or configured `github` remote), ensuring the feature branch is published upstream to GitHub rather than solely to the local clone.
2. **Build PR Body:** Formats a structured PR description:
   ```markdown
   ## 🚀 Autonomous Implementation: Task #{TaskId} - {TaskTitle}
   
   Resolves #{IssueNumber}
   
   ### 📋 Summary
   {TaskDescription}
   
   ### 🛡️ Quality Gate Verification Results
   - **Build Status:** ✅ Passed
   - **Test Suite:** ✅ Passed (0 failures)
   - **Coverage Delta:** 📈 +1.2% (Baseline: 82.1% -> Current: 83.3%)
   - **Linter / Warning Ratchet:** ✅ 0 new compiler warnings
   
   ---
   *Generated by [NightCodeForge](https://github.com/jetelain/NightCodeForge)*
   ```
3. **Open Pull Request:** Invokes `IGitHubClientService.CreatePullRequestAsync` (`POST /repos/{owner}/{repo}/pulls`) with `head = feature/task-{id}`, `base = task.BaseBranch`, and stores `task.PullRequestUrl`.
4. **UI Presentation:** [`Review.razor`](../../src/NightCodeForge.Web/Components/Pages/Review.razor) renders an **"Open GitHub Pull Request"** badge/button linking to `task.PullRequestUrl`.

---

## 4. Implementation Checklist & Deliverables

- [ ] **GitHub App Device Flow, Refresh Token Rotation & DPAPI Token Store (`IGitHubDeviceAuthService`, `IGitHubTokenStore`):**
  - [ ] Implement `IGitHubDeviceAuthService` with `DefaultClientId = "Iv23lienpd2ZF3rkKyfg"` (`POST https://github.com/login/device/code` without `scope` parameter and polling `POST https://github.com/login/oauth/access_token` with `authorization_pending` / `slow_down` handling).
  - [ ] Implement proactive and reactive 8-hour access token refresh (`grant_type=refresh_token`) guarded by a single-flight `SemaphoreSlim(1, 1)` to safely persist rotated `refresh_token` values without race conditions.
  - [ ] Implement `IGitHubTokenStore` persisting the full GitHub App user token & refresh token payload atomically in `%APPDATA%\NightCodeForge\config\github-oauth-token.dat` (`Environment.SpecialFolder.ApplicationData` on Windows; `~/.config/nightcodeforge/github-oauth-token.dat` on Unix), encrypted at rest with Windows **DPAPI** (`ProtectedData.Protect` / `ProtectedData.Unprotect` under `DataProtectionScope.CurrentUser`) on Windows and `0600` file permissions on Unix.
- [ ] **GitHub Client Abstraction (`IGitHubClientService`):**
  - [ ] Implement client in `NightCodeForge.Infrastructure/GitHub` using `HttpClient` or `Octokit` resolving refresh-aware credentials via `IGitHubDeviceAuthService.GetValidAccessTokenAsync` with strict constructor DI and fail-fast validation (`CreatePullRequestAsync`, `ListIssuesAsync`, `CreateIssueAsync`, `UpdateIssueAsync`).
- [ ] **PR Delivery via `MergeMode.ExternalPullRequest`:**
  - [ ] Extend [`TaskOrchestrator`](../../src/NightCodeForge.Core/Workflow/TaskOrchestrator.cs) and `GitWorkspaceManager` to push `feature/task-{id}` to the remote GitHub repository and create rich markdown PRs when `MergeMode == MergeMode.ExternalPullRequest`.
- [ ] **Blazor UI Integration:**
  - [ ] Add GitHub Integration workspace settings tab (`Owner`, `Repo`, interactive GitHub App Device Flow connect/disconnect UI displaying `user_code`, `https://github.com/login/device` link, GitHub App repository installation guidance, connected user status badge, and sub-feature toggles).
  - [ ] Enable `MergeMode.ExternalPullRequest` option in task creation/approval selectors when `PullRequestDeliveryEnabled` is active.
  - [ ] Render "Open GitHub Pull Request" link in `Review.razor` and `TaskDetail.razor`.
- [ ] **Unit & Mock Tests:**
  - [ ] Unit tests for `IGitHubDeviceAuthService` (device code request, polling state machine, 8-hour access token expiration refresh, rotated `refresh_token` persistence, and concurrent refresh deduplication) and `IGitHubTokenStore` (atomic file persistence and Windows DPAPI round-trip protection).
  - [ ] Unit tests for `IGitHubClientService`, PR body markdown formatting, and `TaskOrchestrator` `MergeMode.ExternalPullRequest` execution.

