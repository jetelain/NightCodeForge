# 🔇 Step 9.10: Silent Failure Elimination & Git Merge Safety

> *Parent:* [Phase 9](../09_CODEBASE_REMEDIATION_AND_ROBUSTNESS.md) | *Source:* pre-Phase-10 foundation audit | *Status:* Complete

## 1. Problem

The audit found ~115 bare `catch { }` blocks across `src/`. Many are legitimate best-effort cleanup (process kill, temp dir delete), but several hide real failures and violate the fail-fast policy in [AGENTS.md](../../../AGENTS.md).

## 2. Findings

| # | Location | Issue | Severity |
| :-- | :--- | :--- | :--- |
| F1 | `GitWorkspaceManager.SquashAndMergeAsync` (~L450-485) | `reset --hard` / `clean -fd` failures are swallowed, so the merge proceeds on a dirty canonical clone. | High |
| F2 | same method | `checkout baseBranch` falls back to `checkout -B baseBranch` on **any** `GitCommandException`. If the base branch is missing, it is silently created from the current HEAD, so the squash lands on a synthetic branch. | High |
| F3 | same method | Both `fetch host-origin` and `fetch origin` of the feature branch are swallowed. A missing feature branch surfaces later as an opaque `merge --squash` error. | High |
| F4 | `WorkspacePromptResolver` (3 sites) | Unreadable or locked prompt override files silently fall back to the default prompt, so the user's customisation is ignored with no trace. | Medium |
| F5 | `DeterministicTestStage` (coverage scan, `ParseCoberturaLineRate`) | Coverage parse errors are swallowed, so coverage metrics silently stay stale or absent. | Medium |
| F6 | `StartupRecoveryService` L186 | A malformed `.git` file is ignored, and recovery then silently skips lock purging. | Medium |
| F7 | `LenientToolDispatcher` L267/L626, `CanonicalTools` L638, `PerServiceCallChatHistoryPersistingChatClient` L62, `CrucibleAgentHarness` L1203, `GitSyncService` (L130/148/164/181/196), `WorkspaceProvisioningService` (L138/152/164), `TranscriptBrowserService` | Silent swallow with no logging, so failures are undiagnosable (history persistence loss, git remote detection failure, and line ending hygiene probe failure). | Medium |
| F8 | `ExecutionEnvironmentProvider` L273, `GitMirrorSyncService` L300 | Same, in infrastructure. | Low |
| F9 | `AcpAgentHarness.cs` L760 | Protocol version negotiation response parsing failure is swallowed with empty `catch { }` and silently defaults to protocol v1. | Medium |
| F10 | `CodeStack.cs` L109 | JSON syntax/deserialization errors in `.nightcodeforge/config.json` are swallowed with empty `catch { }`, silently ignoring user repo configs and reverting to defaults with no log. | Medium |
| F11 | `GitWorkspaceManager.SanitizeMessage` L583 | Escapes `"` with `\"` even though `ProcessStartSpec.ArgumentList` is passed as an array directly to `git`, resulting in literal backslashes contaminating git commit messages. | Medium |
| F12 | `TaskOrchestrator.cs` L240-246 | When stage agent profile is not found, falls back to `workspace.AgentProfiles?.FirstOrDefault()` (an arbitrary profile) rather than failing fast with `AgentProfileNotFoundException`. | High |
| F13 | `TopNavBar.razor` L114 & `Backlog.razor` L231 | Swallowed workspace load error in navbar (`catch { _workspaces = Array.Empty<Workspace>(); }`) and raw `Console.WriteLine` in `Backlog.razor` instead of structured logging. | Low |

## 3. Deliverables

1. Classify each bare `catch` as **(a) best-effort cleanup**, **(b) expected-and-handled** or **(c) hiding a failure**. For (a) and (b), narrow it to specific exception types and log at Debug/Warning with context. Fix every (c).
2. `SquashAndMergeAsync`:
   - Fail hard if `reset --hard` or `clean -fd` fails.
   - Create a missing base branch only if explicitly allowed. Otherwise throw a dedicated `BaseBranchNotFoundException`.
   - If the feature branch is missing locally and on all remotes, throw `FeatureBranchNotFoundException`.
3. `WorkspacePromptResolver`: log a warning naming the file that failed. If an override exists but cannot be read, surface a hard error instead of falling back.
4. `CodeStack.cs`: Log a warning on unreadable or invalid `.nightcodeforge/config.json` files and raise an explicit warning rather than silently discarding user configuration.
5. `GitWorkspaceManager.SanitizeMessage`: Remove manual quote escaping that corrupts commit messages when argument arrays are used.
6. `TaskOrchestrator.cs`: Eliminate arbitrary `FirstOrDefault()` agent profile fallback and ensure `AgentProfileNotFoundException` is thrown when a stage profile is missing.
7. `AcpAgentHarness.cs`: Log warning when protocol negotiation response parsing fails.
8. Add the `.editorconfig` / analyzer rule to flag empty catch blocks (`CA1031` or `IDE0072`-style) as a warning, so new ones do not creep in.
9. Tests:
   - Squash-merge with a dirty canonical clone.
   - Squash-merge with a missing base branch.
   - Squash-merge with a missing feature branch.
   - An unreadable prompt override.
   - Invalid `.nightcodeforge/config.json` syntax.
   - Missing stage agent profile raises `AgentProfileNotFoundException`.

## 4. Definition of Done

- [x] No bare `catch { }` remains without an inline justification and a log call.
- [x] `SquashAndMergeAsync` has no swallowed failure on the happy path.
- [x] New exception types are mapped to UI-visible messages.
- [x] Git commit messages are not corrupted with escaped backslashes.
- [x] Missing agent profiles in `TaskOrchestrator` fail fast with `AgentProfileNotFoundException`.
- [x] `dotnet test` passes (unit, integration and E2E).
