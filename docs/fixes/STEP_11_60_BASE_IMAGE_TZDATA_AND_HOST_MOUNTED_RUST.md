# 🐳 Step 11.60: Base Image Timezone Data (`tzdata`/`tzdata-legacy`), Host-Mounted Rust Cache & Python CodeReview Image Parity

> **Parent Plan:** [docs/fixes/README.md](README.md)  
> **Target Framework:** .NET 10 (C# 13)  
> **Status:** Completed  
> **Target Projects:** `NightCodeForge.Infrastructure`, `NightCodeForge.Infrastructure.Tests`, `NightCodeForge.Benchmark.Cli`, `NightCodeForge.Benchmark.Cli.Tests`, `benchmarks/datasets`  
> **Dependencies:** `NightCodeForgeContainerExecutor`, `ContainerMountAndDriftBuilder`, `ContainerEnvironmentFactory`, `HostSdkDiscoveryService`, `CrucibleBenchmarkRunner`, `BenchmarkVerifier`  
> **Aligns With:** `AGENTS.md`, `docs/PLAN.md`

> [!IMPORTANT]
> **Specification Disclaimer:** This document is a behavioral specification. It defines required behaviors, architectural invariants, and verification criteria. Specific internal helper names and implementation details remain at the discretion of the implementer within these constraints.

---

## 1. Context & Motivation

Analysis of the November 24 `v1-smoke` benchmark runs (`Flint:latest` and `Qwen3.8-27B:latest`) exposed three container-runtime, cache-mounting, and benchmark-image parity gaps:

1. **Missing `tzdata` and `tzdata-legacy` in `ncf-agent-base:v5` (`messagepack-csharp__messagepack-csharp-1798`):**
   - `MessagePack.Tests.FormatterTest.DateTimeOffsetTest` fails on both `net6.0` and `net8.0` with `System.TimeZoneNotFoundException: The time zone ID 'Cuba' was not found on the local computer` (`Could not find file '/usr/share/zoneinfo/Cuba'`).
   - Bind-mounting `/usr/share/zoneinfo` from the host WSL Ubuntu distribution is not a viable fix: Ubuntu 24.04 splits legacy IANA links (`Cuba`, `US/Eastern`, etc.) out of `tzdata` into `tzdata-legacy`, which is not installed by default on the host WSL Ubuntu distro (`/usr/share/zoneinfo/Cuba` does not exist on the host), and host-level zoneinfo mounts reduce cross-platform portability.
   - Baking `tzdata` (~283 KB download / ~1,391 KB installed) and `tzdata-legacy` (~95 KB download / ~1,154 KB installed) directly into the managed base image adds only ~2.5 MB installed total.
2. **Host-Mounted Rust Cache & Pre-Flight Validation Gaps (`burntsushi__ripgrep-2576`, `ncf-cr-rust-001`):**
   - Consistent with `.NET` and `JDK`, `HostSdkDiscoveryService` and `ContainerMountAndDriftBuilder` already support host-mounting Rust (`~/.cargo`, `~/.rustup`, `RUSTUP_HOME`, and `$sdk/bin` on `PATH`). The November 24 Rust runs failed with `bash: cargo: command not found` because `rustup` was not installed in the host WSL distribution at the time—forcing agents to waste turns downloading `rustup` and leaving fresh verification containers without `cargo`.
   - However, unlike `.NET` and `JDK` (which separate read-only SDK mounts from writable `/cache/*` package caches), Rust stores both binaries (`~/.cargo/bin`) and writable crate registries/downloads (`~/.cargo/registry`, `~/.cargo/git`) under `~/.cargo`. Currently, `ContainerExecutorOptions.PackageCacheEnvironment` has no `/cache/cargo` mount, so mounting host `~/.cargo:ro` forces Cargo to fall back to container-local `/ncf-home/.cargo` (re-downloading all crates on every run) or fail if `CARGO_HOME` points at a read-only mount.
   - Additionally, there is no fast pre-flight diagnostic when a Rust benchmark instance runs without a `custom_container_image` and host SDK discovery finds no `cargo`/`rustc` toolchain.
3. **Python Code Review Custom Image Gaps (`ncf-cr-py-001`, `ncf-cr-py-002`):**
   - While `v1-smoke.manifest.json` and `v1-full.manifest.json` configure `custom_container_image` (`python:3.9` for `psf/requests`, `python:3.11` for `pallets/flask`) on Implementation entries, crafted Python `CodeReview` entries targeting those same legacy repositories in `v1-code-review.jsonl` and the manifests omit `custom_container_image`.
   - Whenever a model produces a `MinorFix` patch on `ncf-cr-py-001` (observed with `Flint:latest`), verification runs inside `ncf-agent-base:v5` (Ubuntu 24.04 / Python 3.12), failing pytest collection with `ImportError: cannot import name 'match_hostname' from 'ssl'` and `ModuleNotFoundError: No module named 'setuptools'`.

---

## 2. Required Behaviors & Constraints

### 2.1 Base Image `ncf-agent-base:v6` with Full Timezone Data & `/cache/cargo`
- Bump the default managed base image version from `v5` (`ncf-agent-base:v5` / `nightcodeforge-agent:v5`) to `v6` (`ncf-agent-base:v6` / `nightcodeforge-agent:v6`).
- Recognize `ncf-agent-base:v5` and `nightcodeforge-agent:v5` as legacy aliases resolving to the active default base image (`v6`), alongside existing `v1`–`v4` aliases.
- Install both `tzdata` and `tzdata-legacy` in `DefaultAgentBaseDockerfile` under `DEBIAN_FRONTEND=noninteractive` (cleaning `/var/lib/apt/lists/*` afterward) so standard and legacy IANA time zones (including `/usr/share/zoneinfo/Cuba`) resolve inside containers without host `/usr/share/zoneinfo` mounts.
- Pre-create `/cache/cargo` with container-writable permissions alongside `/cache/nuget`, `/cache/npm`, `/cache/pnpm`, `/cache/uv`, `/cache/m2`, and `/cache/ms-playwright`, and set `ENV CARGO_HOME=/cache/cargo`.

### 2.2 Host-Mounted Rust Toolchain, Writable `/cache/cargo` & Pre-Flight Validation
- Add a persistent Cargo package cache mount (`CARGO_HOME=/cache/cargo`) to `ContainerExecutorOptions.PackageCacheEnvironment` and `ContainerMountAndDriftBuilder.BuildVolumeMounts`, backed by a dedicated host cache directory separate from host `~/.cargo/bin` (e.g., `~/.cache/ncf-cargo` or equivalent). Mount this cache `:rw` in standard scopes (and `:ro` in `RestrictedReadOnly` scopes) so Cargo crate indexes and downloads persist across runs without allowing containers to mutate host `~/.cargo/bin`.
- Pre-create the dedicated host Cargo cache directory as the host/WSL user with container-writable permissions prior to container spawn so Docker never auto-creates a `root:root`-owned directory that causes `Permission denied` under non-root `--user uid:gid`.
- Include the `/cache/cargo` mount mode (`:rw` in standard scopes, `:ro` in `RestrictedReadOnly`) and `CARGO_HOME=/cache/cargo` in container drift detection and `PersistentContainerShellSession` environment preamble setup, while respecting any explicit caller-provided `CARGO_HOME` environment variable override.
- Preserve read-only host SDK mounting of `~/.cargo` (with `~/.cargo/bin` on `PATH`) and `~/.rustup` (`RUSTUP_HOME`) so host-installed Rust toolchains (`rustc`, `cargo`, `rustfmt`, `clippy`) execute cleanly under arbitrary non-root container UID/GIDs with `CARGO_HOME=/cache/cargo` in both interactive agent shells and non-interactive verifier commands.
- When preparing or executing a Rust benchmark instance in `ContainerSandbox` mode that requires toolchain execution (`Implementation` and `CodeReview`, excluding offline `TaskReview` instances) without an explicit `custom_container_image` override:
  - Pre-flight validation MUST verify not only that `cargo` and `rustc` exist on host `PATH`, but that they are backed by container-mountable host SDK directories (e.g., `~/.cargo` and `~/.rustup`, not solely excluded system directories such as `/usr/bin` which `HostSdkDiscoveryService` excludes from container bind mounts).
  - If no mountable host `cargo`/`rustc` toolchain is detected, fail fast on the affected Rust instance(s) with an actionable diagnostic error (instructing the operator to install `rustup` on the host/WSL distribution or configure `custom_container_image`) rather than letting the agent waste its timeout budget attempting to install `rustup` inside the container, and without aborting unrelated non-Rust instances in a multi-stack suite. Surface the same diagnostic check during `ncf-bench dataset validate`.

### 2.3 Python Code Review Custom Image Parity & Non-NCF Image Compatibility
- Set compatible `custom_container_image` values (`python:3.9` for `ncf-cr-py-001` on `psf/requests` across `v1-code-review.jsonl`, `v1-smoke.manifest.json`, and `v1-full.manifest.json`; `python:3.11` for `ncf-cr-py-002` on `pallets/flask` across `v1-code-review.jsonl` and `v1-full.manifest.json`) so `MinorFix` verification uses the same compatible Python runtime as Implementation instances without altering `v1-smoke`'s suite size or stack-mix invariants. Add dataset validation tests asserting `custom_container_image` parity between `v1-code-review.jsonl` and the manifests.
- Ensure non-managed custom container images (images other than `ncf-agent-base*` / `nightcodeforge-agent*`, such as `python:3.9` and `python:3.11`, assuming an ABI-compatible Ubuntu/Debian host for host-mounted SDKs and shared libraries) operate reliably across container provisioning, drift validation, interactive shell execution, and benchmark test verification:
  - **Locale compatibility:** Must avoid forcing unavailable `en_US.UTF-8` locale exports on non-managed custom images (e.g., use `C.UTF-8` or guard `en_US.UTF-8` availability on non-managed images while preserving `en_US.UTF-8` on managed `ncf-agent-base` images) so `setlocale` warnings or errors do not pollute stderr or break tools.
  - **Non-root writable home & cache resilience:** Non-managed images lack pre-created `/ncf-home` and `/cache/*` image directories; container startup and shell execution must work cleanly under non-root `--user uid:gid` via writable `/tmp` home fallback and user-owned pre-created host cache mounts.

---

## 3. Definition of Done (DoD) Checklist

- [x] Bump default base image to `ncf-agent-base:v6` / `nightcodeforge-agent:v6`, install `tzdata` and `tzdata-legacy`, pre-create `/cache/cargo` with `ENV CARGO_HOME=/cache/cargo`, and add `v5` legacy alias resolution, with unit tests verifying Dockerfile content and image alias/drift rules.
- [x] Wire persistent writable `/cache/cargo` (`CARGO_HOME=/cache/cargo`, with pre-created non-root-writable host directory, drift detection, and preamble override respect) alongside read-only host `~/.cargo` / `~/.rustup` SDK mounts, and add a fast pre-flight diagnostic for `Implementation`/`CodeReview` Rust instances in `ContainerSandbox` lacking a mountable host `cargo`/`rustc` toolchain (in both `run` and `dataset validate`), with unit tests.
- [x] Add compatible `custom_container_image` values (`python:3.9` for `ncf-cr-py-001`, `python:3.11` for `ncf-cr-py-002`) across `v1-code-review.jsonl` and the corresponding manifests, and harden non-managed custom images against unavailable `en_US.UTF-8` locale exports and missing `/ncf-home`/`/cache/*` directories, with unit and dataset parity tests.
- [x] Verify all non-E2E tests pass via `dotnet test --filter "Category!=Acceptance&Category!=E2E"`.
