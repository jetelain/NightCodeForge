# 🖥️ Step 12.18B: Installer Ollama Manager & Automated 128k Context Hardware Micro-Benchmark

> **Feature Implementation Step (Part 2 of 3 — Local AI Provisioning & Curated `jetelain` Model Micro-Benchmark)**  
> *Parent Plan:* [Features & Integrations Roadmap](README.md)  
> *Prerequisite Step:* [Step 12.18A: Installer Core Services](STEP_12_18A_INSTALLER_CORE_HARDWARE_RELEASE_AND_PROVISIONER.md)  
> *Companion Step:* [Step 12.18C: Avalonia OOBE Setup Wizard & Maintenance Hub UI](STEP_12_18C_INSTALLER_AVALONIA_OOBE_WIZARD_AND_MAINTENANCE_HUB.md)  
> *Source Policy:* [AGENTS.md](../../AGENTS.md)  
> *Project Target:* `src/NightCodeForge.Installer`, `tests/NightCodeForge.Installer.Tests` | *Status:* Planned | *Target Framework:* .NET 10 (C# 13)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Objective

Building on `IHardwareInspectorService` from [Step 12.18A](STEP_12_18A_INSTALLER_CORE_HARDWARE_RELEASE_AND_PROVISIONER.md), this step (Part 2 of Step 12.18) implements **Ollama installation & model pulling (`IOllamaManager`)** and the **Automated 128k-Context Hardware Micro-Benchmark (`IHardwareMicroBenchmarkService`)**:
1. **Standardized Curated `jetelain` Qwen3 Model Suite (Unsloth Dynamics):**
   - **Coder / Implementation:** `jetelain/qwen3.6-35b-a3b` (MoE with Active 3B, ~16 GB in Dynamic Q4, targeting 32 GB RAM + $\ge 6$ GB VRAM).
   - **Architect / Reviewer:** `jetelain/qwen3.8-27b` (Dense 27B reasoning in Dynamic Q4 `~17 GB` or compact Dynamic Q3 `~12–13 GB` to fit $\le 16$ GB VRAM envelopes alongside 128k context).
   - **Auxiliary / Entry Tier:** `jetelain/qwen3.5-9b` (Dynamic Q4, ~6 GB footprint for $< 32$ GB RAM workstations and fast pre-processing).
2. **Mandatory 128k Context Rule (`num_ctx >= 131,072`):** Every agent role must operate with a minimum context window of 128k tokens. The micro-benchmark probes KV cache allocation (`FP16` vs `q8_0` vs `q4_0`), prompt evaluation speed, and token generation throughput on the user's hardware to select the optimal model mix (`ModelRoleAssignment`).
3. **Ollama & Optional OllamaRouter Provisioning (`IOllamaManager`):** Ensure Ollama is installed and running (`http://127.0.0.1:11434/api/tags`), stream `/api/pull` model downloads with progress and ETA, and optionally provision LAN OllamaRouter mesh endpoints.

---

## 2. Technical Specifications

### 2.1 Domain Contracts (`IHardwareMicroBenchmarkService` & `IOllamaManager`)

```csharp
namespace NightCodeForge.Installer.Core.Models;

public sealed record ModelBenchmarkMetrics(
    string ModelTag,
    int ContextWindowTokens,
    double PromptEvalTokensPerSecond,
    double GenerationTokensPerSecond,
    long PeakVramBytes,
    long SystemMemorySpillBytes,
    bool MeetsMinimumContextConstraint);

public sealed record ModelRoleAssignment(
    string ArchitectModelTag,
    int ArchitectContextTokens,
    string CoderModelTag,
    int CoderContextTokens,
    string AuxiliaryModelTag,
    int AuxiliaryContextTokens,
    bool RequiresDeterministicJitSwap,
    string Rationale);
```

```csharp
namespace NightCodeForge.Installer.Core.Services;

public interface IHardwareMicroBenchmarkService
{
    Task<ModelBenchmarkMetrics> RunModelMicroBenchmarkAsync(
        string modelTag,
        int targetContextTokens = 131072,
        IProgress<BenchmarkProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<ModelRoleAssignment> DetermineOptimalModelMixAsync(
        HardwareProfile hardware,
        IReadOnlyList<ModelBenchmarkMetrics> benchmarkResults,
        CancellationToken cancellationToken = default);
}

public interface IOllamaManager
{
    Task<bool> EnsureOllamaInstalledAsync(IProgress<ProvisioningProgress> progress, CancellationToken cancellationToken = default);
    Task PullModelAsync(string modelTag, IProgress<DownloadProgress> progress, CancellationToken cancellationToken = default);
}
```

### 2.2 Hardware-Tier Classification & Optimal Mix Matrix

| Hardware Tier | Memory & Acceleration Profile | Architect / Reviewer Role (Min 128k) | Coder / Implementation Role (Min 128k) | Execution Mode |
| :--- | :--- | :--- | :--- | :--- |
| **Enthusiast / Multi-GPU** | $\ge 64$ GB Unified Memory or $\ge 48$ GB VRAM | `jetelain/qwen3.8-27b` (Dynamic Q4, 128k FP16 KV) | `jetelain/qwen3.8-27b` or `jetelain/qwen3.6-35b-a3b` | Concurrent Full VRAM Offload |
| **High Performance (16–24 GB VRAM)** | 32–64 GB RAM + 16–24 GB VRAM | `jetelain/qwen3.8-27b` (Dynamic Q3 or Q4, 128k KV `q4_0`) | `jetelain/qwen3.6-35b-a3b` (Dynamic Q4, 128k KV `q4_0`) | Deterministic Stage JIT Swap or Dynamic Q3 Full Fit |
| **Mainstream MoE Target (6–12 GB VRAM)** | 32 GB System RAM + 6–12 GB VRAM | `jetelain/qwen3.6-35b-a3b` (Dynamic Q4, 128k KV `q4_0`) | `jetelain/qwen3.6-35b-a3b` (Dynamic Q4, 128k KV `q4_0`) | Unified MoE (Zero Swapping) |
| **Entry / Constrained** | $< 32$ GB RAM or VRAM $< 6$ GB | `jetelain/qwen3.5-9b` (Dynamic Q4, 128k context) | `jetelain/qwen3.5-9b` (Dynamic Q4, 128k context) | Unified Local Run (128k preserved) or Remote Mesh |
| **Cloud / Mesh Offload** | Any Hardware | Routed to LAN OllamaRouter Mesh Node | Routed to LAN OllamaRouter Mesh Node | Remote Execution |

---

## 3. Implementation Checklist & Deliverables

- [ ] Implement `IOllamaManager` supporting cross-platform Ollama installation (`OllamaSetup.exe /silent` on Windows, `install.sh` on Linux, macOS binary), heartbeat probe (`GET /api/tags`), and streaming model pull (`POST /api/pull`).
- [ ] Implement `IHardwareMicroBenchmarkService` executing `num_ctx >= 131,072` verification, prompt eval / generation throughput measurement, KV cache quantization selection (`FP16`, `q8_0`, `q4_0`), and `DetermineOptimalModelMixAsync`.
- [ ] Implement durable `config/config.json` overlay writer that serializes the calibrated `ModelRoleAssignment` into NightCodeForge's LLM provider settings without modifying factory `appsettings.json`.
- [ ] Add unit tests in `NightCodeForge.Installer.Tests` verifying model recommendation tiering across RAM/VRAM profiles, strict enforcement of the minimum 128k context constraint (`num_ctx >= 131,072`), and `/api/pull` streaming progress parsing.

