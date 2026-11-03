# Step 11.38B — Shared Base Class for `AcpAgentHarness` & `PiRpcAgentHarness` (`ExternalProcessAgentHarnessBase`)

**Status:** Completed (Fix / Code Factorization & Constructor Diet)
**Prerequisite:** Step 11.38A

---

## 1. Problem Statement

[`AcpAgentHarness`](../../src/NightCodeForge.Agents/Acp/AcpAgentHarness.cs) (816 lines, **11 constructor parameters**) and [`PiRpcAgentHarness`](../../src/NightCodeForge.Agents/PiRpc/PiRpcAgentHarness.cs) (538 lines, **8 constructor parameters**) share ~180 lines of nearly line-for-line identical code:

1. **Identical Process & Session Lifecycle (`RunStageAsync`):**
   - `Stopwatch` initialization, `sessionId` generation, and `AgentActivityKind.StageStarted` broadcast.
   - `CustomProcessFactory` execution branch (`process.Start()`, UTF-8 `StreamWriter` / `StreamReader`, `finally { process.Kill(entireProcessTree: true); process.Dispose(); }`).
   - `context.Profile.HarnessConfigurationJson` parsing for `executablePath` / `executable` / `command` and `arguments` / `args`.
   - Cross-platform working directory resolution on Windows when `context.WorkspacePath` starts with `/` or `~` via `IWorkspacePathResolver.ResolveCruciblePath`.
   - Starting `IProcessStreamSession` via `IProcessRunner.StartSession(ProcessStartSpec)`.
2. **Identical Prompt Enrichment & History Formatting:**
   - Resolving `CodeStackPresets.ResolveConfig` and `AgentsMdService.FormatExecutionGuidelines`.
   - Formatting `[Stage Directive]`, `[Resumed Conversation History]` via `ConversationHistorySerializer.FormatForPrompt`, `<environment>` PWD instructions, and `[Human Answer]` prefixing.
3. **Identical Activity Broadcasting & Markdown Transcript Persistence:**
   - `BroadcastAsync` via `IAgentActivityStreamer` and `SaveTranscriptAsync` via `SessionTranscriptService`.
4. **Excessive Responsibilities in `AcpAgentHarness` (11 Constructor Parameters):**
   - In addition to ACP JSON-RPC session management, `AcpAgentHarness` directly handles host-side file reading/writing (`fs/read_text_file`, `fs/write_text_file` with `IEditorConfigService` and `IEditorConfigFileApplier`) and host/sandbox terminal management (`terminal/create`, `terminal/run`, `terminal/output`, `terminal/wait_for_exit`, `terminal/release`, `terminal/kill` with `IExecutionEnvironmentProvider` and an unreachable `IProcessRunner` fallback branch), pushing its constructor to 11 parameters.

---

## 2. Architectural Goals

1. **Extract `ExternalProcessAgentHarnessBase`:** Centralize external process lifecycle management, `HarnessConfigurationJson` parsing, workspace path resolution, prompt assembly, activity broadcasting, and transcript persistence into a single abstract base class shared by `AcpAgentHarness` and `PiRpcAgentHarness`.
2. **Extract `IAcpHostRpcHandler` (`AcpHostRpcHandler`):** Move ACP host-side filesystem (`fs/*`) and terminal (`terminal/*`) JSON-RPC method execution into a dedicated collaborator with its own focused dependencies and unit tests, removing the dead `IProcessRunner` fallback branch in accordance with the `AGENTS.md` fail-fast policy.
3. **Bring `AcpAgentHarness` Constructor to `<= 8` Parameters:** Remove pass-through dependencies (`IEditorConfigService`, `IEditorConfigFileApplier`, `IExecutionEnvironmentProvider`) from `AcpAgentHarness` and remove `AcpAgentHarness` from the `ConstructorParameterCeilingTests` allowlist.

---

## 3. Required Specification & Behavioral Contracts

### 3.1 `ExternalProcessAgentHarnessBase`

Create an abstract base class `ExternalProcessAgentHarnessBase` in `NightCodeForge.Agents` implementing `IAgentHarness`:

1. **Core Responsibilities:**
   - Implements `RunStageAsync(AgentSessionContext context, CancellationToken cancellationToken)`.
   - Provides `CustomProcessFactory` support for testing.
   - Parses `HarnessConfigurationJson` into `(string ExecutablePath, string Arguments)` (extracting string `executablePath` / `executable` / `command` and string `arguments` / `args`, and falling back to defaults when `HarnessConfigurationJson` is empty, malformed JSON, or contains a non-string `arguments` / `args` value), delegating fallback executable/arguments to abstract/virtual members.
   - Resolves `(string WorkspacePath, string LocalWorkspaceRoot)` across Windows, WSL, and container paths using `IWorkspacePathResolver`.
   - Provides a shared `BuildEnrichedPrompt` helper encapsulating `CodeStackPresets`, `AgentsMdService.FormatExecutionGuidelines`, `ConversationHistorySerializer.FormatForPrompt` (when `ConversationHistorySerializer.IsSerializedHistory(context.SerializedStateJson)` is `true`), `<environment>` block, and `[Human Answer]` prefixing.
   - Provides protected `BroadcastAsync(...)` and `SaveTranscriptAsync(...)` helpers.
   - Delegates the protocol-specific stdio loop to a `public abstract` `RunSessionStreamAsync(StreamWriter stdin, StreamReader stdout, AgentSessionContext context, string sessionId, Stopwatch sw, CancellationToken cancellationToken = default)` method so existing stream-level unit tests can continue invoking it directly.
2. **Constructor Dependencies Policy:**
   - Per `AGENTS.md`, all constructor dependencies on `ExternalProcessAgentHarnessBase`, `AcpAgentHarness`, `PiRpcAgentHarness`, and `AcpHostRpcHandler` must be required (no optional parameters or `= null` defaults in production constructors; test builders in `tests/` supply fakes/mocks).

### 3.2 `IAcpHostRpcHandler` (`AcpHostRpcHandler`)

Extract ACP's host-side JSON-RPC tool callbacks (`terminal/*` and `fs/*`) from `AcpAgentHarness` into `IAcpHostRpcHandler`:

- Handles `terminal/create`, `terminal/run`, `terminal/output`, `terminal/wait_for_exit`, `terminal/release`, and `terminal/kill` using `IExecutionEnvironmentProvider` (failing fast when the workspace or execution environment cannot be resolved, without a `cmd.exe`/`bash` `IProcessRunner` fallback). Terminal output/exit state must be scoped so concurrent ACP sessions do not leak or share terminal IDs.
- Handles `fs/read_text_file` (including 1-based `offset` and `limit`) and `fs/write_text_file` (applying `.editorconfig` rules via `IEditorConfigService` and `IEditorConfigFileApplier`) using `IWorkspacePathResolver`.
- Registered in DI in `NightCodeForge.Agents` service registration (`AddNightCodeForgeAgents` in `ServiceCollectionExtensions.cs`).
- Constructor parameter count must be `<= 6`.

### 3.3 Refactored `AcpAgentHarness` & `PiRpcAgentHarness`

- Both inherit from `ExternalProcessAgentHarnessBase`.
- **`HarnessActivityRecorder` (`IHarnessActivityRecorder`):** To keep constructors within the ceiling, activity broadcasting (`IAgentActivityStreamer`) and transcript persistence are extracted into a single cohesive collaborator, `HarnessActivityRecorder` implementing `IHarnessActivityRecorder` (in `NightCodeForge.Agents.ExternalProcess`, registered scoped in DI). `ExternalProcessAgentHarnessBase` receives `IHarnessActivityRecorder` instead of the streamer and transcript service separately. Transcript persistence failures remain logged warnings (never thrown) and must be covered by unit tests, as must event forwarding, entry composition (empty prompt/summary skipped) and transcript directory resolution (`<repo>/docs/transcripts`, falling back to the workspace path).
- **`ISessionTranscriptService`:** Introduce `ISessionTranscriptService` (`SaveTranscriptAsync(metadata, entries, outputDirectory, cancellationToken)`) in `NightCodeForge.Agents.Transcripts`, implemented by `SessionTranscriptService` and registered in DI (forwarding to the existing singleton). `HarnessActivityRecorder` depends on the interface so it can be unit tested with a mock. Migrating other consumers of the concrete `SessionTranscriptService` (e.g. `CrucibleAgentHarness`, `IdeationService`) is out of scope for this step.
- `AcpAgentHarness` constructor parameter count is reduced from **11 to `<= 8`**.
- `PiRpcAgentHarness` constructor parameter count remains **`<= 8`**.
- Update `TestAcpHarnessBuilder` so existing `AcpAgentHarnessTests` setups (mutating `EnvProvider`, `PathResolver`, `EditorConfigService`, `EditorConfigFileApplier`) wire into `AcpHostRpcHandler` seamlessly while also allowing an explicit `IAcpHostRpcHandler` override.
- Remove `AcpAgentHarness` from `LegacyConstructorDebtAllowlist` in `ConstructorParameterCeilingTests`.

---

## 4. Verification Criteria

- All characterization tests from Step 11.38A and all existing `AcpAgentHarnessTests`, `AcpProtocolNegotiationTests`, and `PiRpcAgentHarnessTests` pass without behavioral regression.
- Dedicated unit tests for `AcpHostRpcHandler` and `ExternalProcessAgentHarnessBase` pass.
- `AcpAgentHarness` is removed from `LegacyConstructorDebtAllowlist` in `ConstructorParameterCeilingTests`.
- Full test suite passes.

