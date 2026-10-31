# 🖥️ Step 12.18C: Avalonia OOBE Setup Wizard, Post-Install Maintenance Hub & Cross-Platform Packaging

> **Feature Implementation Step (Part 3 of 3 — Avalonia GUI Views, MVVM ViewModels & Single-File Publish)**  
> *Parent Plan:* [Features & Integrations Roadmap](README.md)  
> *Prerequisite Steps:* [Step 12.18A: Installer Core Services](STEP_12_18A_INSTALLER_CORE_HARDWARE_RELEASE_AND_PROVISIONER.md) | [Step 12.18B: Ollama Manager & 128k Micro-Benchmark](STEP_12_18B_INSTALLER_OLLAMA_MANAGER_AND_128K_MICRO_BENCHMARK.md)  
> *Source Policy:* [AGENTS.md](../../AGENTS.md)  
> *Project Target:* `src/NightCodeForge.Installer`, `tests/NightCodeForge.Installer.Tests` | *Status:* Planned | *Target Framework:* .NET 10 (C# 13, Avalonia UI 11+)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Objective

With the core installer services ([Step 12.18A](STEP_12_18A_INSTALLER_CORE_HARDWARE_RELEASE_AND_PROVISIONER.md)) and AI micro-benchmark engine ([Step 12.18B](STEP_12_18B_INSTALLER_OLLAMA_MANAGER_AND_128K_MICRO_BENCHMARK.md)) in place, this step (Part 3 of Step 12.18) builds the **Avalonia UI 11+ (`CommunityToolkit.Mvvm`) desktop interface** and cross-platform single-file packaging:
1. **Dual-Mode Application Startup:** Automatically detect whether NightCodeForge is already installed on the host; launch the **6-Step OOBE Setup Wizard** on first run, or open the **Post-Install Maintenance & 1-Click Updater Hub** on subsequent runs.
2. **Forge Doctor Health Check UI:** Provide a 1-click self-test verifying WSL responsiveness, Docker daemon access, Ollama heartbeat, Web UI port (`5270`) availability, and SQLite database connectivity.
3. **Self-Contained Single-File Packaging:** Configure publish profiles (`PublishSingleFile=true`, `SelfContained=true`, `IncludeNativeLibrariesForSelfExtract=true`) for `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, and `osx-arm64`.

---

## 2. Dual-Mode UX & Screen Flow (Avalonia UI)

### 2.1 Mode 1: Setup Wizard Mode (OOBE)
- **Step 1 — Welcome & Hardware Scan Overview:** Product introduction and live hardware summary (`HardwareProfile`).
- **Step 2 — Environment Check & Provisioning Plan:** Displays OS capabilities and planned provisioning steps (`WSL2`, `Docker`, `.NET 10`, `Companion Tools`) with individual toggle options.
- **Step 3 — Host & Subsystem Provisioning:** Visual stepper with real-time log terminal and progress bar (`IEnvironmentProvisioner`), including reboot prompt if WSL2 installation returns `3010`.
- **Step 4 — AI & Micro-Benchmark Optimization:** Runs `IHardwareMicroBenchmarkService`, displays real-time graphical meters (tokens/sec, VRAM utilization, $\ge 128\text{k}$ context safety badge), allows reviewing/overriding the recommended `ModelRoleAssignment`, and streams model pulls (`IOllamaManager`) with "Download Now" or "Defer to First Run".
- **Step 5 — Software Download & Configuration Layout:** Downloads and extracts `NightCodeForge.Web` and `ncf` CLI binaries via `IReleaseManager` and writes the hardware-calibrated `config/config.json` user overlay.
- **Step 6 — Ready to Forge:** Summary screen with desktop/menu shortcut creation and **"Launch NightCodeForge Web"** button.

### 2.2 Mode 2: Maintenance & Updater Hub Mode (Post-Install)
- **Application Status Card:** Current installed version vs. latest GitHub Release, server running status (Running on port 5270 / Stopped), and quick actions (**Start Server**, **Stop Server**, **Open Web UI in Browser**, **Open CLI Terminal**).
- **1-Click Update:** **"Update to vX.Y.Z"** button executing atomic binary update via `IReleaseManager` while preserving `config/config.json` and `~/.nightcodeforge/`.
- **Component Manager:** Download additional Ollama models, re-run the 128k hardware micro-benchmark, or toggle Tailscale remote access.
- **Forge Doctor (System Diagnostics):** 1-click self-test card displaying pass/fail badges for WSL2, Docker daemon, Ollama endpoint, port 5270, and database health.

---

## 3. Implementation Checklist & Deliverables

- [ ] Configure Avalonia UI 11+ and `CommunityToolkit.Mvvm` in `NightCodeForge.Installer.csproj` with NightCodeForge dark/glassmorphism theme styling.
- [ ] Implement OOBE Wizard ViewModels and Views (Steps 1–6) backed by `IHardwareInspectorService`, `IEnvironmentProvisioner`, `IHardwareMicroBenchmarkService`, `IOllamaManager`, and `IReleaseManager`.
- [ ] Implement Maintenance Hub ViewModel and View (Status Card, 1-Click Updater, Component Manager, and Forge Doctor diagnostics).
- [ ] Configure self-contained single-file publish profiles for Windows, Linux, and macOS RIDs.
- [ ] Add ViewModel unit tests in `NightCodeForge.Installer.Tests` verifying wizard step navigation, OOBE vs. Maintenance Hub startup routing, 1-click update workflow, and Forge Doctor health check aggregation.

