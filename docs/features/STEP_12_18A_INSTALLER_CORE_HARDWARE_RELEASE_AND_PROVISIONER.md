# 🖥️ Step 12.18A: Installer Core Services — Environment Provisioner & Binary Release Manager

> **Feature Implementation Step (Part 1 of 3 — Installer Core & Host Provisioning Services)**  
> *Parent Plan:* [Features & Integrations Roadmap](README.md)  
> *Companion Steps:* [Step 12.18B: OllamaRouter Setup Wizard Integration, Role Recommendation Sync & Protocol Shortcut](STEP_12_18B_INSTALLER_OLLAMA_MANAGER_AND_128K_MICRO_BENCHMARK.md) | [Step 12.18C: Avalonia OOBE Setup Wizard & Maintenance Hub UI](STEP_12_18C_INSTALLER_AVALONIA_OOBE_WIZARD_AND_MAINTENANCE_HUB.md)  
> *Source Policy:* [AGENTS.md](../../AGENTS.md)  
> *Project Target:* `src/NightCodeForge.Installer`, `tests/NightCodeForge.Installer.Tests` | *Status:* Planned | *Target Framework:* .NET 10 (C# 13)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Objective

To eliminate the friction of script-based bootstrapping (`install.ps1`), this step (Part 1 of Step 12.18) scaffolds `NightCodeForge.Installer` and implements the two foundational cross-platform core services:
1. **Subsystem & Environment Provisioning (`IEnvironmentProvisioner`):** Detect and provision WSL2 (on Windows with JIT UAC elevation and reboot detection `3010`), Ubuntu distribution, .NET 10 SDK, Docker Engine, and companion CLI tools (`ripgrep`, `git`, `libicu`, `ncf`, `devskim`).
2. **Binary Release Delivery & Atomic Updater (`IReleaseManager`):** Query GitHub Releases (`jetelain/NightCodeForge` and `jetelain/OllamaRouter`), download platform-specific `NightCodeForge-Web-{rid}.zip`, `NightCodeForge-Cli-{rid}.zip`, and `OllamaRouter-{rid}.zip` archives with resumable streaming and SHA-256 verification, and perform atomic binary swaps with `.backup` rollback while preserving user configuration (`config/config.json`, `~/.nightcodeforge/`, and OllamaRouter `config/` & `state/`).

*(Hardware GPU/VRAM discovery, local Ollama `:11435` port migration, candidate model micro-benchmarking, and role-to-model recommendations are delegated to **OllamaRouter's Setup Wizard** as specified in [Step 12.18B](STEP_12_18B_INSTALLER_OLLAMA_MANAGER_AND_128K_MICRO_BENCHMARK.md); the Avalonia OOBE Wizard and Maintenance Hub GUI are specified in [Step 12.18C](STEP_12_18C_INSTALLER_AVALONIA_OOBE_WIZARD_AND_MAINTENANCE_HUB.md).)*

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

public sealed record ComponentReleaseAsset(
    string ComponentName,
    string Version,
    string DownloadUrl,
    string Sha256Checksum,
    long FileSizeBytes);
```

### 2.2 Subsystem & Environment Provisioning (`IEnvironmentProvisioner`)
1. **Windows Subsystem for Linux (WSL2):**
   - Runs `wsl --status` and `wsl --list --verbose`.
   - If missing/disabled, triggers JIT elevation (`Start-Process wsl -ArgumentList '--install --no-launch' -Verb RunAs -Wait`), detects exit code `3010` (`ProvisioningStatus.RebootRequired`), and provisions the default `Ubuntu` distribution.
2. **Unix Toolchain & Sandboxing (WSL / Native Linux):**
   - Verifies/installs .NET 10 SDK (`dotnet-sdk-10.0` / `dotnet-install.sh --channel 10.0`), Docker Engine (`docker.io` / `docker-ce`, `systemd=true` in `/etc/wsl.conf`, `usermod -aG docker $USER`), and companion tools (`ripgrep`, `git`, `libicu-dev`, `curl`, `ca-certificates`, `locales`).
3. **macOS Host Environment:**
   - Verifies Docker runtime (Docker Desktop / Colima / OrbStack), Git, `ripgrep`, and .NET 10 SDK.

### 2.3 Binary Release Delivery & Atomic Swap (`IReleaseManager`)
1. **GitHub Releases Client:**
   - Queries `https://api.github.com/repos/jetelain/NightCodeForge/releases/latest` (`NightCodeForge-Web-{rid}.zip` and `NightCodeForge-Cli-{rid}.zip`) and `https://api.github.com/repos/jetelain/OllamaRouter/releases/latest` (`OllamaRouter-{rid}.zip` bundling `ollamarouter-server`, `ollamarouter` CLI, and `ollamarouter-app`).
   - Downloads assets with resumable `.part` streaming and SHA-256 verification.
2. **Standard Installation Paths:**
   - **NightCodeForge:**
     - Windows: `%LOCALAPPDATA%\Programs\NightCodeForge\`
     - Linux: `~/.local/share/nightcodeforge/`
     - macOS: `~/Applications/NightCodeForge/`
   - **OllamaRouter:**
     - Windows: `%LOCALAPPDATA%\OllamaRouter\` (with `%LOCALAPPDATA%\OllamaRouter\bin` added to User `PATH`)
     - Linux / macOS: `~/.ollamarouter/` (with `~/.ollamarouter/bin` added to `PATH`)
3. **Atomic Binary Swaps & Rollback:**
   - Gracefully stops running daemons before updating (`ollamarouter stop` for OllamaRouter; stopping `NightCodeForge.Web` when updating NCF).
   - Renames existing binaries to `.backup` before extracting updates, restoring `.backup` automatically if extraction or checksum validation fails, and never overwriting user `config/config.json`, `~/.nightcodeforge/`, or OllamaRouter `config/` and `state/` directories.

---

## 3. Implementation Checklist & Deliverables

- [ ] Create `src/NightCodeForge.Installer` and `tests/NightCodeForge.Installer.Tests` and add them to `NightCodeForge.slnx`.
- [ ] Implement `IEnvironmentProvisioner` with WSL2 detection, JIT privilege elevation, reboot `3010` detection, and Linux/macOS Docker/.NET/companion tool provisioning.
- [ ] Implement `IReleaseManager` with GitHub Releases discovery (`jetelain/NightCodeForge` and `jetelain/OllamaRouter`), SHA-256 checksum validation, atomic `.backup` swap/rollback, and user `PATH` registration.
- [ ] Add unit tests in `NightCodeForge.Installer.Tests` with mocked process runners and HTTP clients covering provisioning state transitions, checksum verification, and atomic rollback.
