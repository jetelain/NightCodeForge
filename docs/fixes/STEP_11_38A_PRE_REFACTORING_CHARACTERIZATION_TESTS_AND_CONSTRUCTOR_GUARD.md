# Step 11.38A — Pre-Refactoring Characterization Tests & Constructor Parameter Ceiling Guard

**Status:** Pending (Fix / Reliability & Test Coverage)

---

## 1. Problem Statement

Several core classes in NightCodeForge have grown into large, multi-responsibility units with excessive constructor dependencies:

| Class | Project | Lines | Constructor Parameters |
| --- | --- | --- | --- |
| `CrucibleAgentHarness` | `NightCodeForge.Agents` | 2,022 | **23** |
| `IdeationService` | `NightCodeForge.Agents` | 919 | **19** |
| `TaskOrchestrator` | `NightCodeForge.Core` | 3,807 | **19** |
| `CanonicalTools` | `NightCodeForge.Agents` | 931 | **13** |
| `AcpAgentHarness` | `NightCodeForge.Agents` | 816 | **11** |
| `StartupRecoveryService` | `NightCodeForge.Core` | 637 | **11** |

Factorizing shared logic across `AcpAgentHarness` / `PiRpcAgentHarness` and `CrucibleAgentHarness` / `IdeationService`, as well as decomposing `TaskOrchestrator` and `StartupRecoveryService`, carries regression risk unless the exact observable behaviors of the existing implementations are locked down by characterization tests first:

1. **`AcpAgentHarness` & `PiRpcAgentHarness` `RunStageAsync` Gap:** Existing unit tests (`AcpAgentHarnessTests` and `PiRpcAgentHarnessTests`) invoke `RunSessionStreamAsync(writer, reader, ...)` directly with in-memory streams, leaving `RunStageAsync`—including `IProcessRunner.StartSession(ProcessStartSpec)` invocation, `HarnessConfigurationJson` parsing (`executablePath`/`executable`/`command`, string `arguments`/`args`, and fallback on malformed/non-string JSON), Windows/Unix path resolution, `OperationCanceledException` graceful protocol shutdown (`session/cancel` / `abort`), and transcript persistence—untested.
2. **ACP Host RPC Method Edge Cases:** `terminal/output`, `terminal/wait_for_exit`, `terminal/release`, `terminal/kill`, line-sliced `fs/read_text_file` (`offset` / `limit`), and `fs/write_text_file` formatting application need explicit regression tests before extracting host RPC handling out of `AcpAgentHarness`.
3. **Shared `CrucibleAgentHarness` & `IdeationService` Context/Toolset Wiring:** Conditional injection of the skills catalog (`load_skill` in `permittedTools`), dynamic context window discovery via `ILlmGatewayService.ResolveContextWindowAsync`, and `CanonicalTools` read-only vs read-write scoping across both services need characterization tests that assert identical behavior before and after extracting shared factories.
4. **No Automated Guard Against Constructor Bloat:** Without an automated architecture test, new dependencies can silently push constructors past 10 parameters again.

---

## 2. Architectural Goals

1. **Characterization Coverage First:** Add fast, deterministic unit tests covering the untested outer lifecycle and edge cases of `AcpAgentHarness`, `PiRpcAgentHarness`, `CrucibleAgentHarness`, and `IdeationService` **without modifying production code**.
2. **Automated Constructor Parameter Ceiling Test (`<= 10` Parameters):** Add a reflection-based architecture test verifying that no concrete class in `NightCodeForge.Core`, `NightCodeForge.Infrastructure`, `NightCodeForge.Agents`, or `NightCodeForge.Web` has a constructor with more than **10 parameters**, tracking the 6 legacy classes above in an explicit, shrinking allowlist that Steps 11.38B–11.38D must empty.

---

## 3. Required Specification & Behavioral Contracts

### 3.1 `AcpAgentHarness` & `PiRpcAgentHarness` Outer Lifecycle Characterization Tests

Add unit tests in `tests/NightCodeForge.Agents.Tests/` verifying:

1. **`RunStageAsync` via `IProcessRunner.StartSession(ProcessStartSpec)`:**
   - Parses `context.Profile.HarnessConfigurationJson` for `executablePath` (or `executable` / `command`) and string `arguments` (or `args`).
   - Falls back to configured harness defaults (`AcpHarnessOptions`: `gemini` with `--experimental-acp`; `PiRpcHarnessOptions`: `node` with `./packages/coding-agent/dist/rpc-entry.js --mode rpc`) when `HarnessConfigurationJson` is empty, malformed JSON, or contains a non-string `arguments`/`args` value.
   - Resolves Unix-style working directories (`/...` or `~...`) on Windows via `IWorkspacePathResolver.ResolveCruciblePath` when `Workspace.Id` is present.
   - Broadcasts `AgentActivityKind.StageStarted` before starting the process and disposes the `IProcessStreamSession` when the stream completes or throws.
2. **Cancellation & Error Handling in `RunSessionStreamAsync`:**
   - When the `CancellationToken` is cancelled mid-stream, `AcpAgentHarness` writes `{"jsonrpc":"2.0","method":"session/cancel"}` to `stdin` and rethrows `OperationCanceledException`; `PiRpcAgentHarness` writes `{"id":"abort-...","type":"abort"}` to `stdin` and rethrows `OperationCanceledException`.
   - When an unexpected exception occurs or `PiRpcAgentHarness` receives `{"type":"response","command":"prompt","success":false,"error":"..."}`, the harness broadcasts `AgentActivityKind.Error`, saves a `Failed` transcript via `SessionTranscriptService`, and propagates the exception.
3. **ACP Host RPC Operations:**
   - `terminal/output`, `terminal/wait_for_exit`, and `terminal/release` / `terminal/kill` return the cached exit code and output from `terminal/create` / `terminal/run` and remove released terminals.
   - `fs/read_text_file` with 1-based `offset` and `limit` returns the exact line slice; missing files return a JSON-RPC `-32602` error response without crashing the session loop.
   - `fs/write_text_file` invokes `IEditorConfigService` and `IEditorConfigFileApplier` and writes the formatted bytes to the resolved workspace path.

### 3.2 `CrucibleAgentHarness` & `IdeationService` Shared Behavior Characterization Tests

Add unit tests in `tests/NightCodeForge.Agents.Tests/` verifying:

1. **Conditional Skills Catalog Injection:**
   - In both `CrucibleAgentHarness` and `IdeationService`, `IWorkspaceSkillsService.GetSkillsCatalogPromptAsync` is called and injected into the system prompt **if and only if** `load_skill` is present in the resolved `permittedTools`.
2. **Dynamic Context Window Discovery & Compaction Wiring:**
   - When `profile.ContextWindowTokens` is `null`, both `CrucibleAgentHarness` and `IdeationService` query `ILlmGatewayService.ResolveContextWindowAsync(effectiveModelId)` and pass the discovered token count to `ModelCapabilityResolver.BuildCompactionStrategy`; if the gateway throws a non-cancellation exception, execution logs a warning and falls back gracefully.
3. **Read-Only Tool Scoping:**
   - When `profile.Mode == AgentMode.ReadOnly` or `profile.SecurityTier == SandboxSecurityTier.RestrictedReadOnly` (or `context.RequiredMode == AgentMode.ReadOnly` in Crucible), mutating canonical tools (`write`, `edit`) are excluded or reject writes.

### 3.3 Constructor Parameter Ceiling Architecture Test

Add `ConstructorParameterCeilingTests` in `tests/NightCodeForge.Web.Tests/Architecture/ConstructorParameterCeilingTests.cs` (since `NightCodeForge.Web.Tests` references `Core`, `Infrastructure`, `Agents`, and `Web`):

- Reflect over all non-abstract, non-compiler-generated types in `NightCodeForge.Core`, `NightCodeForge.Infrastructure`, `NightCodeForge.Agents`, and `NightCodeForge.Web` (excluding EF Core migrations/snapshots and C# positional `record` data types identifiable via `<Clone>$`).
- Assert that every public constructor has **at most 10 parameters** (`const int MaxConstructorParameters = 10`).
- Maintain a `LegacyConstructorDebtAllowlist` containing only the 6 existing classes above with their current parameter counts as upper bounds (`CrucibleAgentHarness`: 23, `IdeationService`: 19, `TaskOrchestrator`: 19, `CanonicalTools`: 13, `AcpAgentHarness`: 11, `StartupRecoveryService`: 11).
- Also assert that **no class in the allowlist has already been reduced to `<= 10` parameters without being removed from the allowlist**, ensuring the allowlist strictly shrinks as Steps 11.38B–11.38D are implemented.

---

## 4. Verification Criteria

- All existing and new unit tests pass (`dotnet test --filter "Category!=Acceptance&Category!=E2E"`).
- No production code in `src/` is modified in this step.
- `ConstructorParameterCeilingTests` fails immediately if any new constructor with `> 10` parameters is introduced or if any existing class increases its constructor parameter count.

