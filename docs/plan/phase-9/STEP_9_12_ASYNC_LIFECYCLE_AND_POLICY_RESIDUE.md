# ⏱️ Step 9.12: Async Lifecycle, Cancellation & Residual Policy Violations

> *Parent:* [Phase 9](../09_CODEBASE_REMEDIATION_AND_ROBUSTNESS.md) | *Source:* pre-Phase-10 foundation audit | *Status:* Planned

## 1. Problem

Steps 9.1-9.9 removed most policy debt, but the audit found async and cancellation hazards plus policy violations that slipped through.

## 2. Findings

| # | Location | Issue | Severity |
| :-- | :--- | :--- | :--- |
| A1 | `HangfireTaskJobQueue` | Jobs are enqueued with `CancellationToken.None`. Hangfire substitutes its own token only for `CancellationToken` parameters when the argument is `default`, so confirm this works. Add a test that shutdown or job deletion cancels the running stage. | Medium |
| A2 | Web: `Backlog.razor` (4 handlers), `Inbox.razor`, `Transcripts.razor`, `TopNavBar.razor` | `async void` event handlers. An exception in `LoadDataAsync` is unobserved and can terminate the Blazor circuit or process. Also called after disposal when the subscription is not removed. | High |
| A3 | `ToastService` L43, `DevQueue.razor` L97 | `_ = Task.Run(...)` fire-and-forget with no exception handling or cancellation. | Medium |
| A4 | `CrucibleAgentHarness` (L338, 368, 386, 545) | `CancellationToken.None` used for persistence and broadcast calls in the normal path. A cancelled stage can hang on a slow DB or hub call. Only the final "cancelled" save should use `None`, with a bounded timeout. | Medium |
| A5 | `StartupRecoveryService` L382-387 | Calls `Process.Start` directly from Core, bypassing `IProcessRunner`, so it is untestable and bypasses sandbox and timeout handling. | Medium |
| A6 | `StartupRecoveryService` L163 | `_pathResolver?.ResolveCruciblePath(...) ?? repoPath` is an optional dependency with a silent fallback. This contradicts Step 9.6 and AGENTS.md. | Medium |
| A7 | `GitSyncService`, `GitWorkspaceManager` | Shell-style escaping via `.Replace("\"", "\\\"")` (L583-585, 685) and `NightCodeForgeContainerExecutor` L263 (`export k="v"`). Quoting is incomplete (`$`, backticks, `\`), so commit messages or env values can be interpreted by the shell. Use argument arrays or proper single-quote escaping. | High (security) |
| A8 | `WorkspacePromptResolver` | `.Replace("-", "_")` stage-id mangling and hard-coded `.nightcodeforge`, `prompts` path segments are magic strings. Stage IDs are used in file paths without validation (path traversal if a stage ID contains `..`). | Medium |
| A9 | `SystemProcessRunner` L359-360 | Magic exit code `124` instead of `WorkflowDefaults.ProcessTimeoutExitCode`, and `CancellationToken.None` writes to a bounded channel can block forever if the reader is gone. | Low |
| A10 | `TaskOrchestrator.ProcessTaskAsync` | Does not catch `OperationCanceledException` and has no cancellation checks during stage loop. An unhandled cancellation leaves database conversation in `Status == Running` and drops all accumulated turn context. | High |

## 3. Deliverables

1. Replace each `async void` handler with a `void` wrapper that calls an `async Task` method inside `try/catch` with logging and a toast. Unsubscribe in `Dispose`.
2. Introduce a small `IBackgroundTaskRunner` (or similar) for tracked fire-and-forget work that observes and logs exceptions. Use it in A3 and the preload in Step 9.11.
3. Audit Hangfire cancellation (A1) and add a test proving cancellation propagates.
4. Crucible harness: pass the stage token everywhere except the terminal cancelled-state save, which uses a short linked timeout.
5. In `TaskOrchestrator.cs`: Catch `OperationCanceledException`, mark active conversation as Cancelled, persist the final state cleanly, and ensure unhandled cancellation exceptions do not corrupt task status.
6. Route `StartupRecoveryService` git operations through `IGitCliRunner` or `IProcessRunner`, and make `IWorkspacePathResolver` a required constructor dependency.
7. Replace manual quoting with argument arrays, or one shared `ShellQuoting` helper with tests covering `"`, `$`, backtick, `\`, newlines and `'`.
8. Add the constants `NightCodeForgeDirectories.Root` and `Prompts`. Validate stage IDs against `^[a-z0-9_-]+$` before building paths.
9. Use `WorkflowDefaults.ProcessTimeoutExitCode` and honour the cancellation token on channel writes.

## 4. Definition of Done

- [ ] Zero `async void` outside framework-required event signatures.
- [ ] No unobserved fire-and-forget tasks.
- [ ] Orchestrator catches and handles `OperationCanceledException` cleanly with DB state persistence.
- [ ] Shell escaping covered by tests, including injection payloads.
- [ ] No optional or `?.` service access in constructors or services.
- [ ] `dotnet test` passes (unit, integration and E2E).
