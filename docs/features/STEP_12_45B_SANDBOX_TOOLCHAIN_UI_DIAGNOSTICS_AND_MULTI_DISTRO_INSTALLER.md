# 🛠️ Step 12.45B: Sandbox Toolchain Management UI, Environment Doctor & Multi-Distro/macOS Installer Alignment

> **Phase 12 Implementation Step (Extend Features — Toolchain UI, Diagnostics & Cross-Platform Installer)**  
> *Parent Plan:* [Features & Integrations Roadmap](README.md)  
> *Companion Step:* [Step 12.45A: Cross-Platform Layered Container Images, Named Toolchain Volumes & Runtime Hardening](STEP_12_45A_CROSS_PLATFORM_LAYERED_CONTAINER_IMAGES_AND_TOOLCHAIN_VOLUMES.md)  
> *Related Steps:* [Step 12.18A: Installer Core Services, NuGet Tooling, Air-Gapped Bundle & Uninstaller](STEP_12_18A_INSTALLER_CORE_HARDWARE_RELEASE_AND_PROVISIONER.md) | [Step 12.22A: Container Diagnostic Shell Service, Environment Doctor & SIGINT Cancellation Fix](STEP_12_22A_DIAGNOSTIC_SHELL_SERVICE_AND_CONTAINER_SIGINT_RECOVERY.md)  
> *Source Policy:* [AGENTS.md](../../AGENTS.md) | [docs/ARCHITECTURE.md](../ARCHITECTURE.md)  
> *Project Target:* `src/NightCodeForge.Web`, `src/NightCodeForge.Agents`, `src/NightCodeForge.Installer`, `tests/NightCodeForge.Web.Tests`, `tests/NightCodeForge.Agents.Tests`, `tests/NightCodeForge.Installer.Tests` | *Status:* Deferred (v0.2) | *Target Framework:* .NET 10 (C# 13)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

> [!IMPORTANT]
> **Revision Prerequisite (Pending Benchmark Stream Merge):** Revise and reconcile this specification once the ongoing Benchmark stream ([Step 12.23A](STEP_12_23A_CRUCIBLE_BENCHMARK_ENGINE_AND_CONTAINER_VERIFIER.md) / [Step 12.23B](STEP_12_23B_BENCHMARK_CLI_AND_LEADERBOARD_WEB_UI.md)) changes are merged into trunk. The benchmark work requires substantial ongoing container adaptations (especially for reliable **Node.js** and **Rust** execution, caches, mounts, and environment setup) that must be folded into this design (and [Step 12.45A](STEP_12_45A_CROSS_PLATFORM_LAYERED_CONTAINER_IMAGES_AND_TOOLCHAIN_VOLUMES.md)) before implementation begins.

---

## 1. Executive Objective & Motivation

Following the Clean Break in [Step 12.45A](STEP_12_45A_CROSS_PLATFORM_LAYERED_CONTAINER_IMAGES_AND_TOOLCHAIN_VOLUMES.md) (which eliminates host-ABI SDK bind-mounting in favor of layered `COPY --from=` stack images and architecture-qualified named volumes), the Web UI, container diagnostics, and Native AOT Installer must be updated to match:

1. **Replace Host-SDK-Coupled Settings UI:** [`HostSdkInspectorTab.razor`](../../src/NightCodeForge.Web/Components/Settings/HostSdkInspectorTab.razor) currently surfaces host SDK mount paths (`/usr/lib/x86_64-linux-gnu`, `/usr/share/dotnet`) and host `~/.dotnet/tools` installation buttons. It must be replaced by a cross-platform **Sandbox Toolchains** management panel inspecting container runtime capabilities, managed stack images, named toolchain volumes, and container-native companion tools (`csnav`, `mdfetch`, `codecondenser`, `ncf`, `agy`).
2. **Align Workspace Wizard & Environment Doctor:** [`WizardStep3Environment.razor`](../../src/NightCodeForge.Web/Components/Workspaces/Steps/WizardStep3Environment.razor), [`WorkspaceSettingsTab.razor`](../../src/NightCodeForge.Web/Components/Workspaces/WorkspaceSettingsTab.razor), and [`EnvironmentDoctorProbe`](../../src/NightCodeForge.Agents/Diagnostics/EnvironmentDoctorProbe.cs) must reflect layered stack images, named volume health, rootless/SELinux runtime diagnostics, and hide Windows-only `WslDirect` choices on Linux and macOS.
3. **Eliminate the `apt-get`-Only Barrier in `NightCodeForge.Installer`:** [`EnvironmentProvisioner`](../../src/NightCodeForge.Installer/Core/Services/EnvironmentProvisioner.cs) currently exits with an error whenever `apt-get` is absent on Linux and requires host `.NET 10 SDK` even when the user runs `ContainerSandbox`. It must support Fedora/RHEL (`dnf`), Arch/Manjaro (`pacman`), openSUSE (`zypper`), Alpine (`apk`), Ubuntu/Debian (`apt-get`), and macOS (`brew` + Docker Desktop / OrbStack / Colima), while treating container runtime + Git + `ripgrep` as the core prerequisites for `ContainerSandbox`.

---

## 2. Behavioral Specifications

### 2.1 Settings UI: `ContainerToolchainsTab.razor` (Replacing `HostSdkInspectorTab.razor`)

1. **Clean-Break Replacement & Presentation Separation:**
   - Remove [`HostSdkInspectorTab.razor`](../../src/NightCodeForge.Web/Components/Settings/HostSdkInspectorTab.razor) and `HostSdkInspectorTabTests.cs`.
   - Create `ContainerToolchainsTab.razor` in `src/NightCodeForge.Web/Components/Settings/` and update [`Settings.razor`](../../src/NightCodeForge.Web/Components/Pages/Settings.razor) (`"sdks"` / `"toolchains"` tab) to label it `"📦 Sandbox Toolchains"` and render `<ContainerToolchainsTab />`.
   - Adhering strictly to the **UI and Presentation separation** policy in [AGENTS.md](../../AGENTS.md), keep all image/volume inspection, formatting, and orchestration logic in a dedicated service (`ISandboxToolchainManagementService` / `SandboxToolchainViewService` in `NightCodeForge.Web/Services` / `NightCodeForge.Infrastructure`), leaving `@code` in `ContainerToolchainsTab.razor` strictly limited to UI state and event forwarding.
2. **Four Operational Cards in `ContainerToolchainsTab.razor`:**
   - **Card 1 — Host & Container Runtime Summary:** Displays host OS, container CPU architecture (`amd64` / `arm64`), detected runtime flavor (`DockerRootfulLinux`, `DockerRootlessLinux`, `PodmanRootless`, `DockerWsl2`, `MacOsDockerDesktop`, `MacOsOrbStack`, `MacOsColima`), active container CLI (`docker` / `podman`), SELinux enforcing badge, and active user UID mapping strategy (plus a **Re-probe Runtime** refresh button).
   - **Card 2 — Managed Base & Stack Images:** Lists `ncf-agent-base:v6`, `ncf-agent-{stack}:v6`, and any workspace-composed `ncf-stack-*` images with status (`Ready`, `Stale >30d`, `Not Built`), baked `.NET` workloads (e.g., `maui-android`), creation timestamp, image size, and explicit **Build / Rebuild** and **Prune Unused** actions.
   - **Card 3 — Named Toolchain & Cache Volumes:** Lists `ncf-ms-playwright-{arch}`, `ncf-android-sdk-{arch}`, `ncf-acp-tools-{arch}`, and (on macOS or when named caches are enabled) `ncf-cache-*` volumes with presence/usage status and **Clear / Re-provision** actions (with confirmation before clearing).
   - **Card 4 — Container Companion Tools & Antigravity ACP (`agy`):** Reports container-native availability of `csnav`, `mdfetch`, `codecondenser`, `ncf`, and `agy` inside the managed image/volume, plus a 1-click **Provision / Update `agy`** action (disabled with clear guidance when `AirGappedMode` is active).

### 2.2 Workspace Wizard & Settings Alignment (`WizardStep3Environment.razor`, `WorkspaceSettingsTab.razor`)

1. **Stack Image Preview & Capability Toggles:**
   - In [`WizardStep3Environment.razor`](../../src/NightCodeForge.Web/Components/Workspaces/Steps/WizardStep3Environment.razor) and [`WorkspaceSettingsTab.razor`](../../src/NightCodeForge.Web/Components/Workspaces/WorkspaceSettingsTab.razor), display the auto-resolved managed stack image preview (e.g., `ncf-agent-dotnet:v6` derived from `CodeStackPresets.Detect`) when `ContainerImage` is blank or set to auto-resolve, while preserving custom image / `.nightcodeforge/Dockerfile` overrides.
   - Expose clear toggles/indicators for optional container capabilities (`includePlaywrightDeps`, `includeAndroidSdk`, and `.NET` workloads such as `maui-android` via `dotnetWorkloads`, plus optional `linux/amd64` platform override on ARM64 hosts) persisted to `.nightcodeforge/config.json` (when configured by the user) without requiring manual Dockerfile editing.
2. **Platform-Aware Confinement Mode Visibility:**
   - On Linux and macOS hosts (`!OperatingSystem.IsWindows()`), hide the Windows-only `WslDirect` confinement card in [`WizardStep3Environment.razor`](../../src/NightCodeForge.Web/Components/Workspaces/Steps/WizardStep3Environment.razor) and [`WorkspaceSettingsTab.razor`](../../src/NightCodeForge.Web/Components/Workspaces/WorkspaceSettingsTab.razor) so users are presented cleanly with `ContainerSandbox` (Recommended) and `BareHost`.
   - On Windows hosts, retain `ContainerSandbox`, `WslDirect`, and `BareHost` unchanged.

### 2.3 Environment Doctor Upgrade (`EnvironmentDoctorProbe.cs`)

Extend [`EnvironmentDoctorProbe`](../../src/NightCodeForge.Agents/Diagnostics/EnvironmentDoctorProbe.cs) (and its report parser/models used by `EnvironmentDiagnosticShellService`) to validate the cross-platform container stack:

1. **Runtime & Permission Probes:**
   - Report detected `RuntimeFlavor`, `ContainerArchitecture`, `IsRootless`, `IsSelinuxEnforcing`, and container `UID`/`GID` alongside actual `/workspace` writability (catching rootless UID mismatch or SELinux denial immediately with actionable remediation hints).
2. **Stack Image & In-Container Toolchain Probes:**
   - Report the resolved container image tag (`ncf-agent-dotnet:v6`, `ncf-stack-...:v6`, etc.), image freshness, and probe the stack's primary runtime binaries (`dotnet --info` / `node -v` / `java -version` / `rustc --version` / `go version` / `uv --version`) plus `git`, `rg`, `csnav`, `mdfetch`, `codecondenser`, and `ncf`. When Android or MAUI Android is enabled for the workspace, also verify `dotnet workload list`, `JAVA_HOME` / `java -version`, and `ANDROID_HOME` (`/opt/android-sdk`, `sdkmanager`/`adb` availability).
3. **Named Volume & ACP Probes:**
   - Verify expected named volume mounts (`/cache/ms-playwright`, `/opt/android-sdk` when enabled, `/opt/ncf-acp/agy` and `/ncf-home/.gemini` when Antigravity ACP is configured) and report `MOUNT|...|MISSING|RO|RW` consistently.

### 2.4 Installer Multi-Distro Linux & macOS Alignment (`NightCodeForge.Installer`)

Update [`EnvironmentProvisioner`](../../src/NightCodeForge.Installer/Core/Services/EnvironmentProvisioner.cs) (aligning with [Step 12.18A](STEP_12_18A_INSTALLER_CORE_HARDWARE_RELEASE_AND_PROVISIONER.md)):

1. **Multi-Package-Manager Linux Support:**
   - Remove the hard `apt-get`-only exit in `EnvironmentProvisioner.BuildLinuxToolchainScript`.
   - Detect and support standard Linux package managers for installing host prerequisites (`git`, `ripgrep`, `curl`, `ca-certificates`, and Docker/Podman when missing):
     - `apt-get` (Ubuntu / Debian)
     - `dnf` (Fedora / RHEL / CentOS Stream / Rocky / Alma)
     - `pacman` (Arch Linux / Manjaro / EndeavourOS)
     - `zypper` (openSUSE Tumbleweed / Leap)
     - `apk` (Alpine Linux)
   - For unsupported distributions (e.g., NixOS or immutable OSes such as Fedora Silverblue / Bazzite), emit clear actionable manual instructions instead of failing with an `apt-get` error, and succeed cleanly when `docker`/`podman`, `git`, and `rg` are already present on `PATH`.
   - Accept either **Docker** or **Podman** (rootful or rootless) in Linux runtime probes (`ProbeDockerRuntimeAsync`) and guide daemon/socket startup (`systemctl --user enable --now podman.socket` or `docker.service`) without forcing rootful `docker.io` when Podman is already configured.
2. **macOS Container Runtime & Toolchain Alignment:**
   - Probe Docker Desktop, OrbStack, and Colima sockets/contexts cleanly on macOS; install missing `git` / `ripgrep` via Homebrew (`brew`) when available or surface concise Xcode CLT / Homebrew instructions.
3. **Container-First Host Prerequisite Policy (Linux & macOS):**
   - Because `ContainerSandbox` provides .NET, JDK, Node, Python, Go, Rust, and companion CLI tools inside self-contained stack images, **host .NET 10 SDK is optional on Linux and macOS** (required only if the user installs/runs `ncf` or `nightcodeassay` directly on the host or selects `BareHost` mode).
   - **Windows/WSL2 preservation:** Windows provisioning continues setting up WSL2, Ubuntu, Docker Engine, and .NET 10 SDK as specified in [Step 12.18A](STEP_12_18A_INSTALLER_CORE_HARDWARE_RELEASE_AND_PROVISIONER.md), while container sandboxes inside WSL2 use the new Step 12.45A stack images and named volumes.
4. **Optional Air-Gapped OCI Image Archive Loading:**
   - Extend `IReleaseManager` / air-gapped bundle detection to recognize optional pre-exported container image tarballs (`ncf-agent-base-{arch}.tar`, `ncf-agent-{stack}-{arch}.tar`) in `<bundleDir>` and load them automatically via `docker load -i` / `podman load -i` so fully offline Linux/macOS/WSL2 installations can run `ContainerSandbox` with zero registry access.

---

## 3. Definition of Done (DoD) Checklist

- [ ] **Settings UI Replacement:** Replace `HostSdkInspectorTab.razor` with `ContainerToolchainsTab.razor` (and `HostSdkInspectorTabTests.cs` with `ContainerToolchainsTabTests.cs`), delegating logic to a testable service in `NightCodeForge.Web/Services` / `NightCodeForge.Infrastructure`, and update `Settings.razor` and `SettingsComponentTests.cs`.
- [ ] **Workspace Wizard & Settings UI:** Display resolved stack image preview and container capability toggles (`includePlaywrightDeps`, `includeAndroidSdk`, `dotnetWorkloads` / MAUI Android, optional `platform`) in `WizardStep3Environment.razor` and `WorkspaceSettingsTab.razor`, and hide `WslDirect` on non-Windows hosts.
- [ ] **Environment Doctor:** Update `EnvironmentDoctorProbe` and tests to verify runtime flavor, SELinux/rootless writability, stack image tools (including `dotnet workload list`, `JAVA_HOME`, and `ANDROID_HOME` when Android/MAUI Android is enabled), and named volume mounts.
- [ ] **Multi-Distro & macOS Installer:** Update `EnvironmentProvisioner` (plus `NightCodeForge.Installer.Tests`) to support `apt-get`, `dnf`, `pacman`, `zypper`, `apk`, Podman/Docker, macOS runtimes, optional host .NET 10 SDK on Linux/macOS, and optional air-gapped OCI `.tar` loading while keeping Windows/WSL2 provisioning intact.
- [ ] **Verification:** Run `dotnet test --filter "Category!=Acceptance&Category!=E2E"` during implementation and execute the full test suite (`dotnet test`) upon completion with all tests passing.
