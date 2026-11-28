# ðŸ›¡ï¸ Step 11.66: Tool Binding Diagnostics, Question Parser, Symmetric Container Drift & Lifecycle Hardening

> **Parent Plan:** [docs/archive/11_EXTEND_FEATURES.md](../11_EXTEND_FEATURES.md)  
> **Target Framework:** .NET 10 (C# 13)  
> **Status:** Completed  
> **Target Projects:** `NightCodeForge.Core`, `NightCodeForge.Agents`, `NightCodeForge.Infrastructure`, `NightCodeForge.Benchmark.Cli`, `NightCodeForge.Core.Tests`, `NightCodeForge.Agents.Tests`, `NightCodeForge.Infrastructure.Tests`, `NightCodeForge.Benchmark.Cli.Tests`  
> **Dependencies:** [Step 11.57](STEP_11_57_CRUCIBLE_ASK_TOOL_RESILIENCE_AND_TRANSCRIPT_TIMESTAMPS.md), [Step 11.60](STEP_11_60_BASE_IMAGE_TZDATA_AND_HOST_MOUNTED_RUST.md), [Step 11.62](STEP_11_62_BENCHMARK_CUSTOM_IMAGE_ABI_RUSTUP_MOCHA_AND_REVIEW_GRADING.md), [Step 11.64](STEP_11_64_CONTAINER_SHELL_FRAMING_ENV_PARITY_MSBUILD_RESILIENCE_AND_AGENT_HYGIENE.md), `DiagnosticBindingAIFunction`, `QuestionItemParser`, `WorkflowInteractionToolModule`, `ContainerMountAndDriftBuilder`, `ContainerEnvironmentFactory`, `PersistentContainerShellSession`, `CrucibleAgentHarness`, `InMemoryCrucibleConversationCoordinator`, `ExecutionEnvironmentProvider`, `RoslynSymbolIndexProvider`  
> **Aligns With:** `AGENTS.md`, `docs/PLAN.md`

> [!IMPORTANT]
> **Specification Disclaimer:** This document is a behavioral specification. It defines required behaviors, architectural invariants, and verification criteria. Specific internal helper names and implementation details remain at the discretion of the implementer within these constraints.

---

## 1. Context & Motivation

Pre-merge code review of the `feat/bench` branch against `main` across `NightCodeForge.Core`, `NightCodeForge.Agents`, and `NightCodeForge.Infrastructure` identified several correctness, symmetry, safety, and hygiene edge cases that should be resolved cleanly:

1. **Schema Recursion, Parameter Naming & Prefix Matching (`DiagnosticBindingAIFunction`):**
   - Formatting recursive `anyOf`/`oneOf` JSON schemas overflows the stack (`StackOverflowException`) because union branch recursion does not advance schema depth toward `MaxSchemaDepth`.
   - Parameter schema name resolution checks `[JsonPropertyNameAttribute]` (which is invalid on method parameters) instead of `[AIParameterNameAttribute]` used by `Microsoft.Extensions.AI` (`AIJsonUtilities`).
   - Negative-number JSON sniffing in `IsPotentiallyJson` matches any leading `'-'` rather than requiring a following ASCII digit (`-?[0-9]`), speculatively attempting JSON deserialization on non-JSON CLI/flag strings such as `"--flag"`.
2. **Root `"question"` Prose & `IEnumerable<QuestionItem>` Normalization (`QuestionItemParser`, `WorkflowInteractionToolModule`):**
   - Across `ParseFromObject`, `ParseFromJsonElement`, `TryBindAskJsonElement`, and `TryBindAskDictionary`, root-level `"question"` handling can drop sibling `options`/`rationale` when parsing bracketed/braced prose or single-item nested JSON strings, and `"question": null` can prematurely short-circuit a valid sibling `"prompt"` key.
   - In `WorkflowInteractionToolModule.LenientAskAIFunction`, case-insensitive scan does not prefer an exact `"questions"` key first, passing a non-`IReadOnlyList<QuestionItem>` `IEnumerable<QuestionItem>` under `"questions"` computes `QuestionItemParser.Normalize(typed)` without rewriting `arguments["questions"]`, and an already-normalized `IReadOnlyList<QuestionItem>` under exact `"questions"` is redundantly re-normalized.
3. **Asymmetric Container Drift Detection (`ContainerMountAndDriftBuilder`):**
   - `DetectContainerDriftCoreAsync` detects transitions into restricted/isolated container states (`DisableNetwork`, `IsRestrictedReadOnly`, `BlockedHosts`, `MountHostDotnetTools = false`, `ProtectCloneWorkingTree = true`), but misses reverse or symmetric states (`true` $\rightarrow$ `false` for `DisableNetwork` including stale `npm_config_offline`/`NPM_CONFIG_OFFLINE` and fallback `npm_config_cache`, `HostConfig.ReadonlyRootfs` mismatches, cleared or reduced `BlockedHosts` leaving stale `:127.0.0.1` entries while ignoring non-loopback entries, `MountHostDotnetTools = true` leaving a stale `:ro` `tmpfs` mask over `<dotnet-root>/tools`, and `ProtectCloneWorkingTree = false` leaving a stale `:ro` `<workspace>/clone` submount), and applies inconsistent null checks across `runInspect` results.
4. **Host Package Cache Pre-Creation Safety & Retry Loops (`ContainerEnvironmentFactory`):**
   - `EnsureHostPackageCacheDirectoriesOnceAsync` keys its verification cache only by `(UseWsl, Distro)` rather than per resolved target path (skipping distinct per-option cache directories), omits WSL symlink guards (`[ ! -L "$d" ]`), unconditionally applies world-writable (`0777` / `chmod 777`) permissions to pre-existing shared host caches (`~/.nuget/packages`, `~/.npm`, `~/.cache/uv`, `~/.m2/repository`, etc.), and fails to isolate per-directory permission errors while still needing to retry transient `wsl.exe` failures.
5. **Container Environment Parity & Constants Hygiene (`ContainerMountAndDriftBuilder`, `PersistentContainerShellSession`):**
   - `DOTNET_ROLL_FORWARD=LatestMajor` is exported in the persistent shell preamble but omitted from `BuildDockerRunArgs`, so direct `docker exec` calls outside the persistent shell miss it.
   - Tilde (`~`) expansion on `SdkMountPaths` is missing or inconsistent across `ResolveRustupHomePath`, `BuildVolumeMounts` (container mount target), and `ComputePathEnvironmentVariable` (`PATH`), risking literal `~/...` or non-POSIX paths in container arguments.
   - Literal `"none"`, `"RUSTUP_HOME"`, and duplicate `"/.dotnet"` / `"/.dotnet/tools"` strings appear alongside shared constants.
6. **Crucible Interruption Flush, In-Memory Coordinator Drain & Lock-Scope Parity:**
   - `CrucibleAgentHarness.CompleteWithRequestChangesAsync` omits `FlushTurnToHistory()` before serializing `chatHistory` and saving the transcript (unlike `CompleteWithInterruptionAsync`), dropping buffered assistant text or reasoning.
   - `InMemoryCrucibleConversationCoordinator.BeginAsync` returns without awaiting `state.DrainAsync()` (unlike `CrucibleConversationCoordinator.BeginAsync`), creating a race for callers reading `TurnSink.Turns` or tracked turns immediately.
   - `ExecutionEnvironmentProvider.CleanupWorkspaceContainersAsync`, `CleanupTaskContainerAsync`, and `EvictIdleWorkspaceContainersAsync` should restrict cached entry eviction to the exact cache keys covered by their held locks so concurrent entries are never disposed without holding their lock.
7. **Balanced Symbol Ranking, Constants & Minor Hygiene (`RoslynSymbolIndexProvider`, `AgentsMdService`):**
   - Strictly sorting by `PathCategory` before `Relevance` allows up to 15 weak substring hits (`Relevance = 5`) in unrelated `src/` files to crowd out exact or whole-qualified matches (`Relevance >= 180`) located in directories named `sandbox`, `benchmark`, or `tests`, and nested `sandbox` feature folders under explicit source roots (`src/`, `lib/`, etc.) are demoted as auxiliary, while relevance weights and path categories use magic numbers.
   - `AgentsMdService.StripNoIncrementalFlag` collapses internal whitespace even when `--no-incremental` is absent.

---

## 2. Required Behaviors & Constraints

### 2.1 Recursive Schema Safety, Attribute Resolution & JSON Prefix Sniffing in `DiagnosticBindingAIFunction`
- **Union schema depth progression:** Increment schema recursion depth when traversing `anyOf` and `oneOf` union branches in `FormatSchemaType` so recursive self-referencing union `$ref` schemas terminate at `MaxSchemaDepth` with `"any"` fallback rather than throwing `StackOverflowException`.
- **Parameter schema name resolution:** Resolve custom parameter schema names via `[AIParameterNameAttribute]` (`Microsoft.Extensions.AI`, checking inherited attributes) to match `AIJsonUtilities` schema naming, instead of `[JsonPropertyNameAttribute]`.
- **Negative number JSON prefix sniffing:** Require a leading `'-'` in `IsPotentiallyJson` to be followed by an ASCII digit (`-?[0-9]`), matching `AIFunctionFactory`'s JSON-sniffing semantics so non-JSON strings starting with `'-'` (e.g., `"--flag"`) are not speculatively deserialized as JSON.
- Add unit tests verifying recursive `anyOf`/`oneOf` schema termination, `[AIParameterName]` diagnostic reporting, and `"-"` prefix binding behavior (`"--flag"` vs numeric strings).

### 2.2 Root `"question"` Prose Preservation in `QuestionItemParser` & `LenientAskAIFunction` Normalization
- **All root entry points in `QuestionItemParser` (`ParseFromObject`, `ParseFromJsonElement`, `TryBindAskJsonElement`, `TryBindAskDictionary`):**
  - Delegate root-level string values under `"question"` through object-level prompt handling rather than wrapper-array extraction, so bracketed/braced prose (e.g., `"[Auth] Use JWT or cookies?"`, `"{env} default to prod?"`) preserves sibling `options` and `rationale`, and valid single-item nested JSON strings under `"question"` or prompt keys preserve/merge enclosing `options` and `rationale` when not overridden by the inner item.
  - Ensure `"question": null` (or undefined) on a root object does not shadow a valid sibling `"prompt"` property.
- **Exact key preference & enumerable rewriting in `WorkflowInteractionToolModule.LenientAskAIFunction`:**
  - Prefer an exact `"questions"` argument key before falling back to case-insensitive matching.
  - When `rawQuestions` is an `IEnumerable<QuestionItem>` that is not already an `IReadOnlyList<QuestionItem>` (such as `HashSet<QuestionItem>` or a deferred iterator), rewrite `arguments["questions"]` with the normalized `IReadOnlyList<QuestionItem>` even when the argument key already matches `"questions"`.
  - When `rawQuestions` is already an `IReadOnlyList<QuestionItem>` and the key matches `"questions"` canonically, avoid redundant normalization/rewriting.
- Add unit tests covering root `"question"` bracketed/braced prose, nested single-item JSON sibling merging, `"question": null` + `"prompt"`, non-list `IEnumerable<QuestionItem>` arguments, and exact vs. case-variant `"questions"` key resolution.

### 2.3 Symmetric Container Drift Detection in `ContainerMountAndDriftBuilder`
- Update `ContainerMountAndDriftBuilder.DetectContainerDriftCoreAsync` (and `EvaluateMountDrift`) so all isolation and mount transitions are checked symmetrically when inspecting an existing container:
  - **Online network & environment recovery:** Always inspect `NetworkMode` and `Env`; when `!options.IsRestrictedReadOnly && !options.DisableNetwork`, flag drift if `NetworkMode` is `"none"`, flag stale `npm_config_offline=true` / `NPM_CONFIG_OFFLINE=true`, and when `options.MountPackageCaches` is enabled require `npm_config_cache=/cache/npm` (catching stale fallback `/tmp/ncf-npm-cache`).
  - **Symmetric `BlockedHosts`:** When online, always inspect `ExtraHosts` (even when `BlockedHosts` is empty) and symmetrically compare expected vs. actual `<host>:127.0.0.1` entries so missing, cleared, or stale/reduced loopback blocks are flagged while non-loopback `--add-host` entries remain untouched.
  - **Symmetric `MountHostDotnetTools`:** When `.NET` roots are mounted and `options.MountHostDotnetTools == true`, inspect `Tmpfs` and flag drift if a stale `tmpfs` mask still hides `<dotnet-root>/tools`.
  - **Symmetric `HostConfig.ReadonlyRootfs`:** Always inspect `{{.HostConfig.ReadonlyRootfs}}` and verify it matches `options.IsRestrictedReadOnly`.
  - **Stale `ProtectCloneWorkingTree` submount:** When `HostWorkspacePath` is set, `CanonicalClonePath` is unset, and `!options.ProtectCloneWorkingTree && !options.IsRestrictedReadOnly`, flag a stale read-only `<containerWorkspace>/clone` submount if present.
- Ensure all `runInspect` calls inside `DetectContainerDriftCoreAsync` guard consistently against `null` results.
- Add unit tests verifying all reverse/symmetric drift transitions, non-loopback `ExtraHosts` preservation, and `null` `runInspect` safety across all inspect stages (and keep existing unit test mocks aligned with the full inspect contract).

### 2.4 Host Package Cache Pre-Creation Safety & Caching Resilience in `ContainerEnvironmentFactory`
- Update `ContainerEnvironmentFactory.EnsureHostPackageCacheDirectoriesOnceAsync`:
  - **Per-target caching:** Key verified package cache entries per `(UseWsl, Distro, TargetPath)` rather than `(UseWsl, Distro)` so custom or newly configured cache paths on subsequent options are verified instead of skipped.
  - **WSL symlink guard:** In the WSL setup script, guard each target directory individually against symbolic links (`[ ! -L "$d" ]` without trailing slashes) before creating or updating permissions, matching non-WSL `AgentMemoryReparseGuard.IsReparsePoint` protection.
  - **Scoped permission widening:** Restrict `0777` / `chmod 777` widening to newly created directories or NightCodeForge-owned cache directories (such as `~/.cache/ncf-cargo` / `ncf-*` Cargo cache directories) rather than widening pre-existing shared host user caches (`~/.nuget/packages`, `~/.npm`, `~/.cache/uv`, `~/.m2/repository`, etc.).
  - **Per-path isolation with transient WSL retry:** Isolate per-directory permission errors (`chmod ... 2>/dev/null || true` in WSL; log-and-continue on non-WSL permission errors for `SetUnixFileMode`) so one non-modifiable pre-existing directory does not block caching other verified targets, while preserving retry behavior when `wsl.exe` execution itself fails transiently or creation fails.
- Add unit tests covering per-target cache keying, WSL symlink guards, scoped permission widening, non-retrying permission error isolation, and transient `wsl.exe` retry behavior.

### 2.5 Container Environment Parity & Constants Hygiene (`ContainerMountAndDriftBuilder`, `PersistentContainerShellSession`, `NightCodeForgeContainerExecutor`)
- Export `DOTNET_ROLL_FORWARD=LatestMajor` (`ContainerExecutorOptions.DotNetRollForwardEnvVar` / `DotNetRollForwardLatestMajor`) in `ContainerMountAndDriftBuilder.BuildDockerRunArgs` (mirroring `PersistentContainerShellSession.WriteEnvironmentPreambleAsync`) so direct `docker exec` invocations inherit the same `.NET` roll-forward policy.
- Expand tilde (`~`) paths on `SdkMountPaths` into POSIX container paths (`/`-rooted) consistently across `ResolveRustupHomePath`, `BuildVolumeMounts` (container mount destination), and `ComputePathEnvironmentVariable` (`PATH`), never emitting literal `~/...` or non-POSIX Windows drive paths into container env/mount/`PATH` arguments.
- Promote `RustupHomeEnvVarName = "RUSTUP_HOME"` to `ContainerExecutorOptions` and replace remaining literal `"none"`, `"RUSTUP_HOME"`, and duplicate `"/.dotnet"` / `"/.dotnet/tools"` strings across `ContainerMountAndDriftBuilder` and `PersistentContainerShellSession` with shared constants per `AGENTS.md`.
- Add unit tests covering `DOTNET_ROLL_FORWARD` in `BuildDockerRunArgs` and tilde expansion across `ResolveRustupHomePath`, `BuildVolumeMounts`, and `ComputePathEnvironmentVariable`.

### 2.6 Crucible Interruption, In-Memory Coordinator & Lock-Scope Parity
- **Request-changes buffer flush (`CrucibleAgentHarness`):** Invoke `FlushTurnToHistory()` in `CompleteWithRequestChangesAsync` before serializing session state and saving the transcript (matching `CompleteWithInterruptionAsync`) so buffered assistant text/reasoning is preserved on `request_changes` interruptions.
- **In-memory coordinator drain (`InMemoryCrucibleConversationCoordinator`):** Await `state.DrainAsync()` before returning from `InMemoryCrucibleConversationCoordinator.BeginAsync` (matching `CrucibleConversationCoordinator.BeginAsync`) so initial prompt, synthetic priming, and pending human answer turns are guaranteed to be materialized immediately upon return.
- **Lock-scope alignment (`ExecutionEnvironmentProvider`):** Restrict cached environment eviction in `CleanupWorkspaceContainersAsync`, `CleanupTaskContainerAsync`, and `EvictIdleWorkspaceContainersAsync` to the matching scope (`TaskId == null` for workspace base cleanup/eviction) AND the exact cache keys covered by the currently held locks so concurrently added entries are never disposed without their lock.
- Add targeted unit tests for each of the three invariants.

### 2.7 Balanced `RoslynSymbolIndexProvider` Ranking, Named Constants & Minor Hygiene (`AgentsMdService`)
- Refine `RoslynSymbolIndexProvider.GetMatchingSymbolsPromptAsync` ranking using a balanced composite score (`Relevance + PathBonus`) so exact or whole-qualified symbol matches in `sandbox/`, `tests/`, or `benchmarks/` directories are not crowded out of the top 15 results by weak substring hits in unrelated production files, while production source symbols continue to rank ahead of `bench/`/`samples/`/`tests/` matches at equal relevance.
- Avoid demoting production feature folders under explicit source roots (`src/`, `lib/`, `source/`, `sources/`) solely because a nested folder is named `sandbox` (while keeping non-source-root `sandbox/` and all `bench`/`samples`/`examples`/`demos` folders auxiliary).
- Replace inline scoring weights (`100`, `120`, `80`, `20`, `10`, `5`) and integer path categories (`0`, `1`, `2`) in `RoslynSymbolIndexProvider` with named constants and an internal enum per `AGENTS.md`.
- In `AgentsMdService.StripNoIncrementalFlag`, return `command.Trim()` directly when `--no-incremental` is not present so unrelated internal whitespace/newlines are not altered.
- Add unit tests verifying balanced exact/qualified vs. weak-substring ranking, source-rooted `Sandbox/` classification, and `StripNoIncrementalFlag` preservation.

---

## 3. Definition of Done (DoD) Checklist

- [x] Fix union schema depth recursion, `[AIParameterNameAttribute]` lookup, and `-?[0-9]` JSON sniffing in `DiagnosticBindingAIFunction`, with unit tests.
- [x] Preserve sibling `options`/`rationale` for root `"question"` prose/nested JSON and handle `"question": null` in `QuestionItemParser`, and prefer exact `"questions"` / rewrite non-`IReadOnlyList<QuestionItem>` enumerables in `WorkflowInteractionToolModule.LenientAskAIFunction`, with unit tests.
- [x] Make container drift detection symmetric in `ContainerMountAndDriftBuilder` (`DisableNetwork`/online recovery, `HostConfig.ReadonlyRootfs`, cleared/reduced `BlockedHosts`, re-enabled `MountHostDotnetTools`, stale `ProtectCloneWorkingTree`, and consistent `null` `runInspect` guards), with unit tests.
- [x] Add per-target cache keying, WSL symlink guards, scoped `0777` widening, and per-path error isolation (with transient `wsl.exe` retry) in `ContainerEnvironmentFactory.EnsureHostPackageCacheDirectoriesOnceAsync`, with unit tests.
- [x] Export `DOTNET_ROLL_FORWARD=LatestMajor` in `ContainerMountAndDriftBuilder.BuildDockerRunArgs`, expand tilde paths consistently across `ResolveRustupHomePath`/`BuildVolumeMounts`/`ComputePathEnvironmentVariable`, and eliminate remaining `"none"`/`"RUSTUP_HOME"`/dotnet suffix literals, with unit tests.
- [x] Harden `BenchmarkTestCommandResolver.WrapPythonContainerCommand` so `pytest --version` probes executability and `pip_constraints` runs unconditionally enforce the isolated venv and prepend its `bin/` directory to `PATH`, with unit and live container acceptance tests.
- [x] Flush buffered turns in `CrucibleAgentHarness.CompleteWithRequestChangesAsync`, drain initial turns in `InMemoryCrucibleConversationCoordinator.BeginAsync`, and align lock vs eviction scope in `ExecutionEnvironmentProvider` (`CleanupWorkspaceContainersAsync`, `CleanupTaskContainerAsync`, `EvictIdleWorkspaceContainersAsync`), with unit tests.
- [x] Balance `RoslynSymbolIndexProvider` composite ranking, fix source-rooted `Sandbox/` classification, replace magic scoring/category numbers with constants/enum, and short-circuit `AgentsMdService.StripNoIncrementalFlag`, with unit tests.
- [x] Verify non-E2E/non-Acceptance tests (`dotnet test --filter "Category!=Acceptance&Category!=E2E"`) during development and run the full test suite (`dotnet test`) upon completion with all tests passing.
