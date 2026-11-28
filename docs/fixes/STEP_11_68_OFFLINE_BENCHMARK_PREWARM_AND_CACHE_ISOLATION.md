# 🔒 Step 11.68: Clean Offline Benchmark Pre-Warm, Isolated Caches & Host Tool/Mount Hardening

> **Parent Plan:** [docs/fixes/README.md](README.md)  
> **Target Framework:** .NET 10 (C# 13)  
> **Status:** Proposed  
> **Target Projects:** `NightCodeForge.Core`, `NightCodeForge.Infrastructure`, `NightCodeForge.Benchmark.Cli`, `NightCodeForge.Infrastructure.Tests`, `NightCodeForge.Benchmark.Cli.Tests`  
> **Dependencies:** [Step 11.66](STEP_11_66_TOOL_BINDING_PARSER_DRIFT_AND_CONTAINER_HARDENING.md), [Step 11.67](STEP_11_67_BENCHMARK_ANTICHEAT_PYTHON_IMAGE_AND_TASKREVIEW_PARITY.md), [Step 12.45A](../features/STEP_12_45A_CROSS_PLATFORM_LAYERED_CONTAINER_IMAGES_AND_TOOLCHAIN_VOLUMES.md), `SandboxScopeDescriptor`, `ContainerMountAndDriftBuilder`, `ContainerEnvironmentFactory`, `PersistentContainerShellSession`, `CrucibleBenchmarkRunner`, `BenchmarkVerifier`, `BenchmarkTestCommandResolver`, `BenchmarkWorkspaceManager`  
> **Aligns With:** `AGENTS.md`, `docs/PLAN.md`

> [!IMPORTANT]
> **Specification Disclaimer:** This document is a behavioral specification. It defines required behaviors, architectural invariants, and verification criteria. Specific internal helper names and implementation details remain at the discretion of the implementer within these constraints.

> [!WARNING]
> **Branch Workflow Split (`main` First, Then `feat/bench`):**
> Per repository branch rules, `feat/bench` MUST NOT modify `NightCodeForge.Core` or `NightCodeForge.Infrastructure` directly.
> - **Phase A (Section 2.1)** MUST be implemented on `main` first (and merged into `feat/bench`).
> - **Phase B (Sections 2.2–2.4)** is implemented in `NightCodeForge.Benchmark.Cli` on `feat/bench` once Phase A is merged.
> - Once [Step 12.45A](../features/STEP_12_45A_CROSS_PLATFORM_LAYERED_CONTAINER_IMAGES_AND_TOOLCHAIN_VOLUMES.md) lands, its container-native stack images and named volumes supersede the interim host-`.dotnet` `:ro` and allowlist mount hardening in Section 2.1.

---

## 1. Context & Motivation

While [Step 11.67](STEP_11_67_BENCHMARK_ANTICHEAT_PYTHON_IMAGE_AND_TASKREVIEW_PARITY.md) blocks package-browsing CDNs and adds post-run cheat audit telemetry without breaking online dependency downloads, three deeper structural gaps remain in benchmark isolation:

1. **Primary Package Registries Remain Reachable During Agent Execution:**
   - Because agents currently install dependencies inside the agent container, primary registries (`pypi.org`, `files.pythonhosted.org`, `registry.npmjs.org`, `api.nuget.org`, `crates.io`) must stay online. As demonstrated in `v1-smoke` (`Flint:latest` on `pytest-dev__pytest-10356` running `pip download pytest==7.2.0 --no-binary :all:` to copy the gold patch verbatim), a model can fetch published releases of the benchmarked repository directly from the registry.
   - Blocking registries while the agent is expected to install dependencies creates false negatives; the clean solution is a **harness-controlled online pre-warm phase** before the agent starts, followed by **offline (`--network none`) agent execution**.
2. **Host `~/.dotnet` Writable Mount & Full `~/.dotnet/tools` Exposure (Step 11.66 Backlog Caveat):**
   - `ContainerMountAndDriftBuilder` still mounts host `~/.dotnet` read-write (`isReadOnly: false`) when mapped as a user `.dotnet` root, allowing any container to persist executable scripts into host `~/.dotnet` across runs (as occurred on `pytest-dev__pytest-10356`).
   - `ContainerEnvironmentFactory` also mounts all of host `~/.dotnet/tools:ro` when `MountHostDotnetTools == true`, exposing non-NCF developer tools such as `ilspycmd` (which `Flint:latest` attempted to use to decompile cached `Spectre.Console.dll` assemblies on `spectreconsole__spectre-console-1458`).
3. **Shared Developer Host Package Caches Leak Newer Versions:**
   - Mounting developer host caches (`~/.nuget/packages`, `~/.cache/ncf-cargo`, `~/.npm`, `~/.cache/uv`) into benchmark agent containers exposes previously restored/newer versions of benchmarked repositories (e.g., `spectre.console.0.43.0.nupkg`, `grep-regex` crate sources).

---

## 2. Required Behaviors & Constraints

### 2.1 Main-Branch Infrastructure Prerequisites (`NightCodeForge.Core`, `NightCodeForge.Infrastructure` — Land on `main` First)
- **Read-Only Host `~/.dotnet` Mount & Container-Local `DOTNET_CLI_HOME`:**
  - Until [Step 12.45A](../features/STEP_12_45A_CROSS_PLATFORM_LAYERED_CONTAINER_IMAGES_AND_TOOLCHAIN_VOLUMES.md) eliminates host SDK bind-mounts, mount user `.dotnet` roots read-only (`isReadOnly: true`) in `ContainerMountAndDriftBuilder.BuildVolumeMounts` instead of `isReadOnly: false`.
  - Export `DOTNET_CLI_HOME=/tmp/ncf-dotnet-home` (and `DOTNET_CLI_TELEMETRY_OPTOUT=1`) in `BuildDockerRunArgs` and `PersistentContainerShellSession.WriteEnvironmentPreambleAsync` (ensuring `/tmp/ncf-dotnet-home` is created writable in `/tmp`) so `dotnet` CLI operations do not require write access to `/ncf-home/.dotnet`.
- **Allowlisted Companion Tool Exposure (No `ilspycmd` / Arbitrary Global Tool Leakage):**
  - Add an optional allowlist (e.g., `AllowedHostDotnetTools` on `SandboxScopeDescriptor` / `ContainerExecutorOptions`) or a scoped staging directory for `MountHostDotnetTools == true`: when specified (used by benchmark scopes), expose **only** the explicitly allowlisted NCF companion binaries/shims (`csnav`, `codecondenser`, `mdfetch` when enabled) inside `/opt/ncf-dotnet-tools` rather than bind-mounting the user's entire `~/.dotnet/tools` directory.
  - Ensure `MountHostDotnetTools == false` continues to mask and omit all companion tools symmetrically.
- **Per-Scope Isolated Package Cache Root Override:**
  - Extend `SandboxScopeDescriptor` / `ContainerExecutorOptions` so callers (such as `CrucibleBenchmarkRunner` and `BenchmarkVerifier`) can override the host package cache directories (`NuGet`, `npm`, `pnpm`, `uv`, `cargo`) and optionally mount them `:ro` during agent execution, instead of sharing the user's `~/.nuget/packages` or `~/.cache/ncf-cargo`.
- **Symmetric Drift Detection & Unit Tests:**
  - Update `ContainerMountAndDriftBuilder.DetectContainerDriftCoreAsync` / `EvaluateMountDrift` to verify `:ro` `.dotnet` mounts, `DOTNET_CLI_HOME`, allowlisted tool mount sources, and custom cache paths symmetrically, with unit tests covering all new options and drift transitions.

### 2.2 Harness-Controlled Online Dependency Pre-Warm Phase (`NightCodeForge.Benchmark.Cli`)
- Before starting the agent session in `CrucibleBenchmarkRunner.RunSingleEvaluationAsync` (for `Implementation`, `CodeReview`, and `TaskReview` when a workspace requires dependencies), execute a deterministic, harness-controlled **online pre-warm phase** against the provisioned workspace (using a short-lived container with `disableNetwork: false` and `BlockedAntiCheatHosts` active):
  - **Python:** Provision `/workspace/.venv` inside the workspace (already excluded from `ExtractPatchAsync` and preserved across `ResetToBaseAsync` via `-e .venv`) using `pip_constraints` (`PIP_CONSTRAINT`), repository `requirements/*.txt`, `-e .` (with `--no-build-isolation` fallback), and `pytest`. Prepend `/workspace/.venv/bin` to the agent container's `PATH` so `python`, `python -m pytest`, and `pytest` resolve cleanly without network access.
  - **Node (JavaScript / TypeScript):** Run lockfile-aware dependency installation (`pnpm` / `yarn` / `npm ci` / `npm install`, with `--ignore-scripts` fallback and ready marker) into `/workspace/node_modules` (already excluded from `ExtractPatchAsync` and preserved across `ResetToBaseAsync` via `-e node_modules`), plus any required `npm`/`pnpm` package-manager shim or isolated reporter prefix.
  - **C# (`.NET`):** Run `dotnet restore` against discovered solution/project targets (under `DotNetGlobalJsonResilienceScope`) into the isolated benchmark NuGet cache.
  - **Rust:** Run `cargo fetch` against the workspace `Cargo.toml`/`Cargo.lock` into the isolated benchmark Cargo cache so offline `cargo check` / `cargo test --offline` builds without network access.
- **Fail-Fast Infrastructure Error Handling:**
  - If dependency pre-warm fails (non-zero exit or timeout), abort the instance evaluation immediately and classify it as `BenchmarkInstanceOutcome.Error` (`IsInfrastructureError` equivalent) with the pre-warm diagnostic output — never launch an agent into a broken offline environment.
- **Agent Guidance Alignment:**
  - Update benchmark bootstrap / sandbox setup guidance (`BenchmarkStagePolicyResolver`) to inform agents that dependencies are pre-warmed and the sandbox is offline (e.g., use `--no-restore` / `NuGetAudit=false` for `.NET`, `--offline` for Cargo, and `/workspace/.venv` for Python rather than running `pip install` or `npm install`).

### 2.3 Offline Agent Execution (`--network none`) Across All Benchmark Categories (`NightCodeForge.Benchmark.Cli`)
- Launch the agent container (`CrucibleBenchmarkRunner.BuildExecutionScope`) with `disableNetwork: true` (`--network none`) across **all** benchmark categories (`Implementation`, `CodeReview`, and `TaskReview`) once pre-warm succeeds.
- Export offline environment guards in the agent container (`NuGetAudit=false` for `.NET`, `PIP_NO_INDEX=1` for Python, `CARGO_NET_OFFLINE=true` for Rust, alongside existing `npm_config_offline=true` / `NPM_CONFIG_OFFLINE=true` from `ContainerMountAndDriftBuilder`) so toolchain commands fail fast locally instead of hanging on DNS/TCP retries.
- **Verification Compatibility:**
  - Keep `BenchmarkVerifier` using the pre-warmed workspace dependencies (`node_modules`, `.venv`) and isolated benchmark caches; verification containers may run offline when dependencies are already satisfied (with an explicit opt-in/fallback only if a `TestPatch` introduces a previously unrestored test-only dependency).
  - Ensure `BenchmarkWorkspaceManager.ResetToBaseAsync` preserves pre-warmed `.venv` and `node_modules` while resetting tracked source files and removing agent build/temp artifacts (`target/`, `bin/`, `obj/`).

### 2.4 Isolated Benchmark Cache Hygiene & Target-Repo Scrubbing (`NightCodeForge.Benchmark.Cli`)
- Configure benchmark runs to use a dedicated isolated cache root under `~/.nightcodeforge/benchmarks/cache/{nuget,npm,pnpm,uv,cargo}` instead of the developer's `~/.nuget/packages`, `~/.npm`, `~/.cache/uv`, or `~/.cache/ncf-cargo`.
- Mount NuGet/Cargo caches read-only (`:ro`) during the offline agent execution phase (after online pre-warm populates them `:rw`) so an agent cannot poison caches for subsequent instances.
- Before launching the agent (or via per-instance cache scoping/filtering), ensure target-repository package directories matching the benchmarked repository's own package IDs (e.g., `spectre.console` when benchmarking `spectreconsole/spectre.console`, `serilog` when benchmarking `serilog/serilog`) are excluded or scrubbed from agent-visible caches when not required as a transitive dependency at `BaseCommit`, eliminating local cache source/assembly leaks.
- **Unit & Acceptance Tests:**
  - Add unit tests in `NightCodeForge.Benchmark.Cli.Tests` covering pre-warm command generation per stack, fail-fast `Error` classification on pre-warm failure, `disableNetwork: true` across all three categories, offline env flags, isolated benchmark cache paths, and target-package scrubbing.
  - Add non-CI `[Trait("Category", "Acceptance")]` live-container tests verifying offline agent execution after pre-warm across Python, Node, C#, and Rust.

---

## 3. Definition of Done (DoD) Checklist

- [ ] **Phase A (`main`, merged into `feat/bench`):**
  - [ ] Mount user `.dotnet` root `:ro` with container-local `DOTNET_CLI_HOME=/tmp/ncf-dotnet-home` in `ContainerMountAndDriftBuilder` / `PersistentContainerShellSession`.
  - [ ] Support allowlisted host `.dotnet/tools` exposure (exposing only `csnav`, `codecondenser`, `mdfetch` for benchmark scopes, excluding `ilspycmd` and unrelated host tools).
  - [ ] Support per-scope isolated package cache root overrides and `:ro` cache mounting options in `SandboxScopeDescriptor` / `ContainerExecutorOptions` / `ContainerMountAndDriftBuilder`, with symmetric drift detection and unit tests.
- [ ] **Phase B (`feat/bench` — `NightCodeForge.Benchmark.Cli`):**
  - [ ] Implement deterministic online dependency pre-warm across Python (`/workspace/.venv`), Node (`node_modules`), C# (`dotnet restore`), and Rust (`cargo fetch`) before agent launch, failing fast as `Error` on pre-warm failure.
  - [ ] Run agent containers offline (`disableNetwork: true` / `--network none`) across `Implementation`, `CodeReview`, and `TaskReview`, with offline toolchain env vars (`NuGetAudit=false`, `PIP_NO_INDEX=1`, `CARGO_NET_OFFLINE=true`, `npm_config_offline=true`) and updated sandbox guidance.
  - [ ] Wire isolated `~/.nightcodeforge/benchmarks/cache/*` directories (mounted `:ro` during agent execution) and target-repository package cache scrubbing.
  - [ ] Add unit tests and live `Category=Acceptance` verification tests across all stacks, and pass live `ncf-bench dataset validate --verify` on `v1-smoke`.
- [ ] **Full Verification:** Run `dotnet test --filter "Category!=Acceptance&Category!=E2E"` during development and execute the full test suite (`dotnet test`) upon completion with all tests passing.
