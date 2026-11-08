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
   - Persist the issued user-to-server token payload in a **dedicated file** (`%APPDATA%\NightCodeForge\config\github-oauth-token.dat` on Windows; `~/.config/NightCodeForge/config/github-oauth-token.dat` or platform `Environment.SpecialFolder.ApplicationData` equivalent on Linux/macOS), strictly separated from `GitHubIntegrationSettings`.
   - On Windows, encrypt and decrypt the token file at rest using **DPAPI** (`ProtectedData.Protect` / `ProtectedData.Unprotect` with `DataProtectionScope.CurrentUser` via the `System.Security.Cryptography.ProtectedData` package). On Unix platforms, enforce `0600` (`UserRead | UserWrite`) file permissions.
2. **GitHub Client Abstraction (`IGitHubClientService`) & Workspace Settings (`GitHubIntegrationSettings`):**
   - Provide a clean, DI-registered GitHub client resolving credentials from `IGitHubDeviceAuthService` / `IGitHubTokenStore` with fail-fast validation and per-workspace configuration persisted on the [`Workspace`](../../src/NightCodeForge.Core/Domain/Workspace.cs) entity and editable in `/settings`.
3. **Pull Request Delivery Mode via `MergeMode.ExternalPullRequest`:**
   - Provide an alternative to direct squash-and-merge into the developer's local working copy: deliver completed tasks via **GitHub Pull Requests**.
   - Pushes the isolated feature branch (`feature/task-{id}`) to the remote GitHub repository (`https://github.com/{RepositoryOwner}/{RepositoryName}.git`) using ephemeral HTTP header authentication without persisting tokens in `.git/config`.
   - Opens (or updates) a GitHub Pull Request against `task.BaseBranch` with an automated markdown summary detailing the task objective, stage execution history, and deterministic quality gate metrics (coverage delta, warning ratchet, test results).
   - Surfaces **"Open GitHub Pull Request"** / **"Create GitHub Pull Request"** actions in the Review Room UI (`Review.razor`) and Task Details (`TaskDetail.razor` / `TaskDetailDrawer.razor`).

*(Inbound untrusted GitHub Issue triage and bi-directional `ncf:*` label/comment mirroring are specified in [Step 12.11B](STEP_12_11B_GITHUB_UNTRUSTED_ISSUE_TRIAGE_AND_TASK_MIRRORING.md).)*

---

## 2. Configuration, GitHub App Device Flow & Token Storage

### 2.1 Workspace Configuration (`GitHubIntegrationSettings`)
Persisted per workspace on the [`Workspace`](../../src/NightCodeForge.Core/Domain/Workspace.cs) entity (stored via EF Core JSON value conversion in [`AppDbContext`](../../src/NightCodeForge.Infrastructure/Data/AppDbContext.cs), with an EF Core migration and baseline check in `BaselineExistingPreMigrationDatabaseAsync`). **Credentials/tokens are never stored in `GitHubIntegrationSettings` or SQLite**:
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

### 2.4 Domain, Persistence & Markdown Sync Alignment (`MergeMode.ExternalPullRequest`)
GitHub PR delivery is unified with the core **[`MergeMode`](../../src/NightCodeForge.Core/Domain/Enums.cs)** enumeration:
```csharp
public enum MergeMode
{
    Automatic = 0,             // Auto-squashes and merges to base branch once status reaches Ready
    ManualReviewRequired = 1,  // Maintainer manually reviews diff/tests before triggering merge in UI
    ExternalPullRequest = 2    // Pushes branch to GitHub remote and opens a GitHub PR (enabled when GitHub PR delivery is active)
}
```
- **[`ForgeTask`](../../src/NightCodeForge.Core/Domain/ForgeTask.cs) Properties & Database Persistence:**
  - `ForgeTask.MergeMode`: Set to `MergeMode.ExternalPullRequest` when PR delivery is selected.
  - `ForgeTask.ExternalSource`: External provider identifier (e.g. `"GitHub"`), nullable `string?` (max length `64`).
  - `ForgeTask.ExternalReferenceId`: Unprefixed external issue identifier (e.g. `"123"` for GitHub Issue `#123`), nullable `string?` (max length `128`). Kept distinct from Pull Request tracking so a task originating from Issue `#123` can also open Pull Request `#45` without overwriting its upstream issue link.
  - `ForgeTask.PullRequestNumber`: Created GitHub Pull Request number (e.g. `45`), nullable `int?`.
  - `ForgeTask.PullRequestUrl`: Full URL to the created GitHub PR (e.g. `"https://github.com/owner/repo/pull/45"`), nullable `string?` (max length `1024`).
  - Copy these properties in [`TaskWorkspacePreparer.CopyTaskMetadata`](../../src/NightCodeForge.Core/Workflow/TaskWorkspacePreparer.cs) and [`InMemoryTaskRepository`](../../src/NightCodeForge.Core/Repositories/ITaskRepository.cs), and expose `PullRequestUrl` / `PullRequestNumber` on [`TaskReviewSnapshot`](../../src/NightCodeForge.Core/Domain/TaskReviewModels.cs).
- **Markdown Sync & Backlog Tooling (`docs/tasks/*.md`):**
  - Update [`TaskMarkdownSyncService`](../../src/NightCodeForge.Core/Services/TaskMarkdownSyncService.cs) and [`TaskFrontmatter`](../../src/NightCodeForge.Core/Services/TaskFrontmatter.cs) to serialize and parse `MergeMode.ExternalPullRequest` as `"external_pull_request"` (alongside `"automatic"` and `"manual_review_required"`), and preserve `external_source`, `external_reference_id`, `pull_request_number`, and `pull_request_url` in frontmatter.
  - Update [`BacklogMutationToolService`](../../src/NightCodeForge.Agents/Tools/BacklogMutationToolService.cs) (`create_task` / `edit_task`) and [`KanbanCard.razor`](../../src/NightCodeForge.Web/Components/Backlog/KanbanCard.razor) (`"PR"` badge) to support `MergeMode.ExternalPullRequest`.
- **UI Availability:**
  - When `workspace.GitHubSettings is { Enabled: true, PullRequestDeliveryEnabled: true }` and a valid token is present in `IGitHubTokenStore`, `MergeMode.ExternalPullRequest` is enabled as an option across `/tasks/new` (`TaskCreationModal.razor`), backlog promotion modals (`MaintainerPromotionModal.razor`), proposal cards, `TaskDetail.razor`, and `TaskDetailDrawer.razor`.

---

## 3. Pull Request Delivery Specification

### 3.1 Pull Request Generation & Lifecycle Flow
To respect the maximum 10-constructor-parameter rule in [AGENTS.md](../../AGENTS.md) (since [`TaskMergeAndCascadeCoordinator`](../../src/NightCodeForge.Core/Workflow/TaskMergeAndCascadeCoordinator.cs) already has 10 parameters and [`TaskOrchestrator`](../../src/NightCodeForge.Core/Workflow/TaskOrchestrator.cs) has 9), encapsulate outbound PR delivery in a cohesive service (or route via [`ITaskWorkspacePreparer`](../../src/NightCodeForge.Core/Workflow/TaskWorkspacePreparer.cs)):

When a task completes all verification stages (`PostReviewVerification` passes) and `task.MergeMode == MergeMode.ExternalPullRequest`:
1. **Push Branch to GitHub Remote (Ephemeral Auth, Ext4 Mirror Compatible):**
   - Because `origin` in the canonical ext4 clone (`Ext4MirrorPath`) points to the local host repository path rather than GitHub, push `feature/task-{id}` directly to `https://github.com/{RepositoryOwner}/{RepositoryName}.git`.
   - Authenticate the `git push` command using the refresh-aware access token from `IGitHubDeviceAuthService.GetValidAccessTokenAsync` via an ephemeral per-command HTTP header (`git -c http.extraHeader="Authorization: Basic <base64(x-access-token:token)>" push https://github.com/{owner}/{repo}.git feature/task-{id}:refs/heads/feature/task-{id} --force-with-lease`) so the short-lived `ghu_...` token is **never** written into `.git/config`.
   - Also sync the feature branch to the local host repository (`PushToHostAsync`) so the developer's local repository has the branch ref.
2. **Build PR Body (Shared Quality Gate Metrics Extraction):**
   - Extract deterministic quality gate metrics from `PipelineDataBag` using a shared helper in `NightCodeForge.Core` (consolidating the logic currently in [`TaskReviewService.ExtractGateMetrics`](../../src/NightCodeForge.Web/Services/TaskReviewService.cs)).
   - Format a structured PR description (including `Resolves #{ExternalReferenceId}` only when `ExternalSource == "GitHub"` and `ExternalReferenceId` is populated):
     ```markdown
     ## 🚀 Autonomous Implementation: Task #{TaskId} - {TaskTitle}
     
     Resolves #{IssueNumber}
     
     ### 📋 Summary
     {TaskDescription}
     
     ### 🛡️ Quality Gate Verification Results
     - **Build Status:** ✅ Passed
     - **Test Suite:** ✅ Passed ({PassedTests} / {TotalTests}, 0 failures)
     - **Coverage Delta:** 📈 +1.2% (Baseline: 82.1% -> Current: 83.3%)
     - **Linter / Warning Ratchet:** ✅ 0 new compiler warnings
     
     ---
     *Generated by [NightCodeForge](https://github.com/jetelain/NightCodeForge)*
     ```
3. **Open or Update Pull Request & Transition to `Ready`:**
   - If `task.PullRequestNumber` / `task.PullRequestUrl` is not yet set, invoke `IGitHubClientService.CreatePullRequestAsync` (`POST /repos/{owner}/{repo}/pulls`) with `head = feature/task-{id}`, `base = task.BaseBranch`, `draft = settings.CreateAsDraft`, and store `task.PullRequestNumber` and `task.PullRequestUrl`.
   - If `task.PullRequestNumber` already exists (e.g., after a maintainer "Request Changes" loopback), pushing `feature/task-{id}` updates the PR head automatically, and `IGitHubClientService` updates the PR body/title with the latest gate metrics.
   - Transition `task.Status` to `TaskOverallStatus.Ready` so the task remains visible in the Review Room while the PR is open on GitHub.
4. **Review Room UI Presentation (`Review.razor`):**
   - When `_snapshot.MergeMode == MergeMode.ExternalPullRequest` (or whenever `_snapshot.PullRequestUrl` is populated):
     - Render an **"Open GitHub Pull Request (#{PullRequestNumber})"** button/badge linking to `_snapshot.PullRequestUrl` (`target="_blank"`).
     - If `_snapshot.PullRequestUrl` is not yet created for a `Ready` task (e.g., if switched to `ExternalPullRequest` in `Ready` state), render a **"Create GitHub Pull Request"** button to trigger PR delivery on demand.
     - Retain **"Request Changes"** (to loop back to `Implementation`) and allow the maintainer to either squash-merge locally (`Approve & Merge`) or close the task once the external PR is merged.

---

## 4. Implementation Checklist & Deliverables

- [ ] **GitHub App Device Flow, Refresh Token Rotation & DPAPI Token Store (`IGitHubDeviceAuthService`, `IGitHubTokenStore`):**
  - [ ] Add `System.Security.Cryptography.ProtectedData` to `NightCodeForge.Infrastructure.csproj`.
  - [ ] Implement `IGitHubDeviceAuthService` with `DefaultClientId = "Iv23lienpd2ZF3rkKyfg"` (`POST https://github.com/login/device/code` without `scope` parameter and polling `POST https://github.com/login/oauth/access_token` with `authorization_pending` / `slow_down` handling).
  - [ ] Implement proactive and reactive 8-hour access token refresh (`grant_type=refresh_token`) guarded by a single-flight `SemaphoreSlim(1, 1)` to safely persist rotated `refresh_token` values without race conditions.
  - [ ] Implement `IGitHubTokenStore` persisting the full GitHub App user token & refresh token payload atomically in `%APPDATA%\NightCodeForge\config\github-oauth-token.dat` (`Environment.SpecialFolder.ApplicationData`), encrypted at rest with Windows **DPAPI** (`ProtectedData.Protect` / `ProtectedData.Unprotect` under `DataProtectionScope.CurrentUser`) on Windows and `0600` file permissions on Unix.
- [ ] **Domain Model, EF Core Migration & Markdown Sync:**
  - [ ] Add `MergeMode.ExternalPullRequest = 2`, `GitHubIntegrationSettings` on `Workspace`, and `ExternalSource`, `ExternalReferenceId`, `PullRequestNumber`, and `PullRequestUrl` on `ForgeTask`.
  - [ ] Add EF Core migration and update `AppDbContext.BaselineExistingPreMigrationDatabaseAsync`, `InMemoryTaskRepository`, `TaskWorkspacePreparer.CopyTaskMetadata`, `TaskMarkdownSyncService`, `BacklogMutationToolService`, and `KanbanCard.razor`.
- [ ] **GitHub Client Abstraction (`IGitHubClientService`):**
  - [ ] Implement client in `NightCodeForge.Infrastructure/GitHub` using `HttpClient` resolving refresh-aware credentials via `IGitHubDeviceAuthService.GetValidAccessTokenAsync` with strict constructor DI and fail-fast validation (`CreatePullRequestAsync`, `UpdatePullRequestAsync`, `GetPullRequestAsync`, `ListIssuesAsync`, `CreateIssueAsync`, `UpdateIssueAsync`, `AddIssueCommentAsync`).
- [ ] **PR Delivery via `MergeMode.ExternalPullRequest`:**
  - [ ] Implement ephemeral-auth branch push to `https://github.com/{Owner}/{Repo}.git` and automated PR creation/update upon Stage 6 completion without exceeding the 10-constructor-parameter limit on orchestrator services.
- [ ] **Blazor UI Integration:**
  - [ ] Add GitHub Integration workspace settings tab (`GitHubSettingsTab.razor` in `/settings` under Workspace Scope) with `RepositoryOwner`, `RepositoryName`, interactive GitHub App Device Flow connect/disconnect UI (`user_code`, copy button, `https://github.com/login/device` link, GitHub App repository installation link, connected user status badge), and sub-feature toggles.
  - [ ] Enable `MergeMode.ExternalPullRequest` option in task creation, promotion, and detail selectors when `PullRequestDeliveryEnabled` is active.
  - [ ] Render "Open GitHub Pull Request" / "Create GitHub Pull Request" actions in `Review.razor`, `TaskDetail.razor`, and `TaskDetailDrawer.razor`.
- [ ] **Unit & Mock Tests:**
  - [ ] Unit tests for `IGitHubDeviceAuthService` (device code request, polling state machine, 8-hour access token expiration refresh, rotated `refresh_token` persistence, and concurrent refresh deduplication) and `IGitHubTokenStore` (atomic file persistence and Windows DPAPI round-trip protection).
  - [ ] Unit tests for `IGitHubClientService`, PR body markdown formatting, `TaskMarkdownSyncService` `external_pull_request` round-trip, and `MergeMode.ExternalPullRequest` orchestration.
