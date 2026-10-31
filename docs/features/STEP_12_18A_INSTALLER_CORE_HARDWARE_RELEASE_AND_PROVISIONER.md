# 🖥️ Step 12.18A: Installer Core Services — Hardware Inspector, Release Manager & Environment Provisioner

> **Feature Implementation Step (Part 1 of 3 — Installer Core & Host Provisioning Services)**  
> *Parent Plan:* [Features & Integrations Roadmap](README.md)  
> *Companion Steps:* [Step 12.18B: Ollama Manager & 128k Hardware Micro-Benchmark](STEP_12_18B_INSTALLER_OLLAMA_MANAGER_AND_128K_MICRO_BENCHMARK.md) | [Step 12.18C: Avalonia OOBE Setup Wizard & Maintenance Hub UI](STEP_12_18C_INSTALLER_AVALONIA_OOBE_WIZARD_AND_MAINTENANCE_HUB.md)  
> *Source Policy:* [AGENTS.md](../../AGENTS.md)  
> *Project Target:* `src/NightCodeForge.Installer`, `tests/NightCodeForge.Installer.Tests` | *Status:* Planned | *Target Framework:* .NET 10 (C# 13)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Objective

To eliminate the friction of script-based bootstrapping (`install.ps1`), this step (Part 1 of Step 12.18) scaffolds `NightCodeForge.Installer` and implements the three foundational cross-platform core services:
1. **Hardware Capabilities Detection (`IHardwareInspectorService`):** Inspect CPU architecture, logical cores, total system RAM, and GPU vendor/VRAM across Windows, Linux, and macOS.
2. **Subsystem & Environment Provisioning (`IEnvironmentProvisioner`):** Detect and provision WSL2 (on Windows with JIT UAC elevation and reboot detection `3010`), .NET 10 SDK, Docker Engine, and companion CLI tools (`ripgrep`, `git`, `libicu`, `ncf`, `devskim`).
3. **Binary Release Delivery & Atomic Updater (`IReleaseManager`):** Query GitHub Releases (`jetelain/NightCodeForge`), download platform-specific `NightCodeForge-Web-{rid}.zip` and `NightCodeForge-Cli-{rid}.zip` archives with resumable streaming and SHA-256 verification, and perform atomic binary swaps with `.backup` rollback while preserving `config/config.json` and `~/.nightcodeforge/`.

*(Ollama installation and the automated 128k-context hardware micro-benchmark are specified in [Step 12.18B](STEP_12_18B_INSTALLER_OLLAMA_MANAGER_AND_128K_MICRO_BENCHMARK.md); the Avalonia OOBE Wizard and Maintenance Hub GUI are specified in [Step 12.18C](STEP_12_18C_INSTALLER_AVALONIA_OOBE_WIZARD_AND_MAINTENANCE_HUB.md).)*

---

## 2. Technical Specifications

### 2.1 Project Layout & Domain Models (`NightCodeForge.Installer`)
- **Project File:** `src/NightCodeForge.Installer/NightCodeForge.Installer.csproj` (`net10.0`)
- **Test Project:** `tests/NightCodeForge.Installer.Tests/NightCodeForge.Installer.Tests.csproj`

```csharp
namespace NightCodeForge.Installer.Core.Models;

public enum HostPlatformType { Windows, Linux, MacOS }

public enum ProvisioningStatus
{
    NotStarted,
    InProgress,
    Completed,
    RebootRequired,
    Failed,
    Skipped
}

public sealed record HardwareProfile(
    HostPlatformType Platform,
    string CpuArchitecture,
    int LogicalCores,
    long TotalSystemMemoryBytes,
    string? GpuDescription,
    long? DedicatedGpuMemoryBytes,
    bool HasHardwareAcceleration);

public sealed record ComponentReleaseAsset(
    string ComponentName,
    string Version,
    string DownloadUrl,
    string Sha256Checksum,
    long FileSizeBytes);
```

### 2.2 Hardware Capabilities Detection (`IHardwareInspectorService`)

| Metric | Windows Detection | Linux Detection | macOS Detection |
| :--- | :--- | :--- | :--- |
| **CPU Architecture** | `RuntimeInformation.ProcessArchitecture` | `RuntimeInformation.ProcessArchitecture` | `RuntimeInformation.ProcessArchitecture` |
| **Logical Cores** | `Environment.ProcessorCount` | `Environment.ProcessorCount` | `Environment.ProcessorCount` |
| **System RAM** | `GlobalMemoryStatusEx` (Win32 P/Invoke) | `/proc/meminfo` (`MemTotal`) | `sysctl hw.memsize` |
| **GPU Vendor & VRAM** | DXGI / NVML P/Invoke or `nvidia-smi` | `/proc/driver/nvidia` or `rocm-smi` | Apple Silicon Unified Memory via `sysctl` |

### 2.3 Subsystem & Environment Provisioning (`IEnvironmentProvisioner`)
1. **Windows Subsystem for Linux (WSL2):**
   - Runs `wsl --status` and `wsl --list --verbose`.
   - If missing/disabled, triggers JIT elevation (`Start-Process wsl -ArgumentList '--install --no-launch' -Verb RunAs -Wait`), detects exit code `3010` (`ProvisioningStatus.RebootRequired`), and provisions default `Ubuntu` distribution.
2. **Unix Toolchain & Sandboxing (WSL / Native Linux):**
   - Verifies/installs .NET 10 SDK (`dotnet-install.sh --channel 10.0`), Docker Engine (`docker-ce`, `usermod -aG docker $USER`), and companion tools (`ripgrep`, `git`, `libicu-dev`, `curl`, `ca-certificates`).
3. **macOS Host Environment:**
   - Verifies Docker runtime (Docker Desktop / Colima / OrbStack) and .NET 10 SDK.

### 2.4 Binary Release Delivery & Atomic Swap (`IReleaseManager`)
1. **GitHub Releases Client:** Queries `https://api.github.com/repos/jetelain/NightCodeForge/releases/latest`, resolves `NightCodeForge-Web-{rid}.zip` and `NightCodeForge-Cli-{rid}.zip`, and downloads with resumable `.part` streaming and SHA-256 verification.
2. **Standard Installation Paths:**
   - Windows: `%LOCALAPPDATA%\Programs\NightCodeForge\`
   - Linux: `~/.local/share/nightcodeforge/`
   - macOS: `~/Applications/NightCodeForge/`
3. **Atomic Binary Swaps & Rollback:** Renames existing `bin/` to `.backup` before extracting updates, restoring `.backup` automatically if extraction or checksum validation fails, and never overwriting user `config/config.json` or `~/.nightcodeforge/`.

---

## 3. Implementation Checklist & Deliverables

- [ ] Create `src/NightCodeForge.Installer` and `tests/NightCodeForge.Installer.Tests` and add them to `NightCodeForge.slnx`.
- [ ] Implement `IHardwareInspectorService` with platform parsers for Windows, Linux (`/proc/meminfo`, `nvidia-smi`/`rocm-smi`), and macOS (`sysctl`).
- [ ] Implement `IEnvironmentProvisioner` with WSL2 detection, JIT privilege elevation, reboot `3010` detection, and Linux/macOS Docker/.NET/companion tool provisioning.
- [ ] Implement `IReleaseManager` with GitHub Releases discovery, SHA-256 checksum validation, atomic `.backup` swap/rollback, and `PATH` registration.
- [ ] Add unit tests in `NightCodeForge.Installer.Tests` with mocked process runners and HTTP clients covering hardware parsing, provisioning state transitions, checksum verification, and atomic rollback.

