# ⚡ Step 11.33: Unified LLM Provider Architecture (Standalone Ollama vs OllamaRouter), Auto Context Window Discovery, & Dynamic Fleet Telemetry

> **Phase 11 Implementation Step (Post-MVP - Real-Project Hardening, Token Efficiency & Agent Ergonomics)**  
> *Parent Plan:* [Phase 11 Master Plan](../11_EXTEND_FEATURES.md)  
> *Project Target:* [`src/NightCodeForge.Core`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core), [`src/NightCodeForge.Agents`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Agents), [`src/NightCodeForge.Infrastructure`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Infrastructure), [`src/NightCodeForge.Web`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web), [`tests/NightCodeForge.Agents.Tests`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/tests/NightCodeForge.Agents.Tests), [`tests/NightCodeForge.Infrastructure.Tests`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/tests/NightCodeForge.Infrastructure.Tests)  
> *Status:* Proposed | *Target Framework:* .NET 10 (C# 13)  
> *Dependencies / OllamaRouter Backend Tasks:* [OllamaRouter Task #17: Router Identity, Health & Fleet Hardware Topology API](file:///C:/Users/Julien/source/repos/jetelain/OllamaRouter/docs/tasks/2026-10-26-router-identity-health-fleet-hardware-topology-api.md), [OllamaRouter Task #16: Model Capabilities, Dynamic Context Sizing & Routing Preview API](file:///C:/Users/Julien/source/repos/jetelain/OllamaRouter/docs/tasks/2026-10-26-model-capabilities-dynamic-context-sizing-routing-preview-api.md), [OllamaRouter Task #18: Fleet VRAM Management & Reclaim API](file:///C:/Users/Julien/source/repos/jetelain/OllamaRouter/docs/tasks/2026-10-26-fleet-vram-management-reclaim-api.md), [OllamaRouter Task #21: Session Affinity Header & 32 GB Server-Class Hardware Ceiling](file:///C:/Users/Julien/source/repos/jetelain/OllamaRouter/docs/tasks/2026-10-26-session-affinity-header-and-32gb-server-class-hardware-ceiling.md)  
> *Aligns With:* [AGENTS.md](../../../AGENTS.md), [docs/PLAN.md](../../PLAN.md)

---

## 1. Executive Summary & Motivation

### 1.1 The Standalone Ollama Principle (Zero Router Burden for Single GPUs)
Currently, NightCodeForge assumes an OllamaRouter proxy topology with three hardcoded targets (`Local`, `Remote`, `Cloud`). Target pinning selectors (`X-OllamaRouter-Target`) are displayed across agent profiles, and the gateway service queries `/monitor/api` (an endpoint designed for the terminal/HTML monitor).

However:
1. **Single-GPU Developers:** Users operating on a single workstation or laptop have no need for a reverse proxy or multi-node routing daemon. Imposing OllamaRouter on single-GPU users creates unnecessary setup complexity, port forwarding conflicts, and maintenance friction. They must be able to use **standalone Ollama directly**.
2. **Simplified Global Configuration:** Instead of sprawling per-profile configurations, global configuration must be streamlined to:
   - **Provider Selection:** `Ollama` (Direct Standalone) or `OllamaRouter` (Extensible Multi-Node Fleet).
   - **Default Model:** Model Name + Thinking Effort (e.g. `qwen2.5-coder:32b`, `Medium`).
   - **Optional Role Specialization:** An optional toggle to differentiate:
     - **Coder / Implementation Role:** Fast coding model + thinking effort for Stage 3 (`implementation`).
     - **Architect / Reviewer Role:** Deep reasoning model + thinking effort for Stage 0 (`triage`), Stage 1 (`task-review`), Stage 5 (`code-review-fix`), and interactive `ideation`.
3. **Automatic Context Window Discovery Based on Fleet Capabilities:** Context window sizes should never be manually guessed or hardcoded to static 128k fallbacks. Context capacities must be automatically read from Ollama (via Modelfile `num_ctx` and GGUF metadata) or OllamaRouter (via `recommendedContextWindow`). In a multi-node fleet, compaction `ContextWindowTokens` must be sized to the **largest capability of the fleet** (the maximum context ceiling across capable targets or cloud overflow), ensuring conversations are not compacted prematurely while high-capacity nodes (e.g. dedicated GPU servers or cloud overflow) can service extended context.
4. **Clean API Integration:** Replace scraping of legacy `/monitor/api` with dedicated, high-performance REST APIs ([OllamaRouter Task #17](file:///C:/Users/Julien/source/repos/jetelain/OllamaRouter/docs/tasks/2026-10-26-router-identity-health-fleet-hardware-topology-api.md): `GET /api/router/info`, `GET /api/router/fleet`; [OllamaRouter Task #16](file:///C:/Users/Julien/source/repos/jetelain/OllamaRouter/docs/tasks/2026-10-26-model-capabilities-dynamic-context-sizing-routing-preview-api.md): `GET /api/router/models`; [OllamaRouter Task #18](file:///C:/Users/Julien/source/repos/jetelain/OllamaRouter/docs/tasks/2026-10-26-fleet-vram-management-reclaim-api.md): `POST /api/router/vram/reclaim`), dynamically rendering arbitrary fleet topologies (Desktop 16 GB, Laptop 8 GB, AI Server 1 96 GB, AI Server 2 64 GB, Cloud Overflow).
5. **Multi-Agent KV-Cache & Session Affinity in Fleet Mode ([OllamaRouter Task #21](file:///C:/Users/Julien/source/repos/jetelain/OllamaRouter/docs/tasks/2026-10-26-session-affinity-header-and-32gb-server-class-hardware-ceiling.md)):** When multiple agents (e.g. Architect and Developer, or concurrent background workers) execute against OllamaRouter, prompts must not bounce haphazardly across capable nodes (e.g., oscillating between Desktop, AI Server 1, and AI Server 2). Because `llama.cpp` prompt prefix evaluation is cached in host memory, bouncing between nodes destroys prompt KV-cache reuse and imposes severe 10–30s re-evaluation penalties on every turn. NightCodeForge must propagate a session identifier (`X-OllamaRouter-Session-Id` derived from the active `ConversationId` or `AgentId`/`TaskId`) with every inference call to ensure each agent sticks to its assigned node and reuses prompt prefix KV cache seamlessly.

---

## 2. Architectural Specifications

### 2.1 Provider Selection & Simplified Configuration Schema

In `LlmProviderOptions` and Global Settings:
```csharp
public enum LlmProviderType
{
    /// <summary>Direct connection to standard Ollama instance (single host GPU).</summary>
    Ollama,

    /// <summary>Connection to OllamaRouter reverse proxy (multi-node LAN fleet + cloud overflow).</summary>
    OllamaRouter
}

public class LlmProviderOptions
{
    public const string SectionName = "LlmProvider";

    public LlmProviderType ProviderType { get; set; } = LlmProviderType.OllamaRouter;
    public string Endpoint { get; set; } = "http://localhost:11434";
    public double RequestTimeoutMinutes { get; set; } = 15;

    // Unified baseline configuration
    public string DefaultModelName { get; set; } = "qwen2.5-coder:32b";
    public ThinkingEffort DefaultThinkingEffort { get; set; } = ThinkingEffort.Medium;

    // Optional role differentiation
    public bool DifferentiateRoles { get; set; } = false;

    // Coder role (Implementation stage)
    public string? CoderModelName { get; set; }
    public ThinkingEffort? CoderThinkingEffort { get; set; }

    // Architect role (Triage, Ideation, Task Review, Code Review & Fix)
    public string? ArchitectModelName { get; set; }
    public ThinkingEffort? ArchitectThinkingEffort { get; set; }
}
```

#### Provider Auto-Detection
On startup or when clicking **"Detect Provider"** in the Web UI:
1. Issue `GET /api/router/info`:
   - If HTTP 200 returned with `{ "isRouter": true }` $\rightarrow$ Provider is **OllamaRouter**.
2. If HTTP 404 or connection failed, issue `GET /api/version`:
   - If HTTP 200 returned with `{ "version": "..." }` $\rightarrow$ Provider is **Ollama**.
3. If both fail $\rightarrow$ Endpoint is offline.

---

### 2.2 Standalone Ollama Mode Operation

When `ProviderType == LlmProviderType.Ollama`:
1. **Target Pinning & Session Headers Suppressed:** The `X-OllamaRouter-Target` and `X-OllamaRouter-Session-Id` headers are never attached to outbound HTTP requests (`OllamaChatClient.cs`). The target pinning dropdowns in `AgentProfilesTab.razor` are completely hidden.
2. **Clean Single-Host Inspector:** The `/settings` and `/monitoring` tabs display a streamlined single-host GPU card:
   - Host Ollama version and latency from `GET /api/version`.
   - Resident GPU models in local memory from `GET /api/ps`.
3. **Direct VRAM Reclamation:** Invoking VRAM reclamation sends `keep_alive: 0` via `POST /api/generate` for all resident models discovered from `/api/ps`, instantly freeing local GPU memory without requiring router intervention.

---

### 2.3 Automatic Context Window Discovery & 32 GB Server-Class Tiering

Context window tokens are dynamically resolved to ensure the [CrucibleCompactionStrategy](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Agents/Inference/ModelCapabilityResolver.cs) and [SessionTranscriptService](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Agents/Transcripts/SessionTranscriptService.cs) compact conversations at the exact mathematical boundary:

```mermaid
flowchart TD
    ModelSelected["Model Selected in NCF<br/>(e.g. qwen2.5-coder:32b)"] --> ProviderCheck{"Provider Type?"}
    
    ProviderCheck -- "OllamaRouter" --> RouterQuery["GET /api/router/models/{model}"]
    RouterQuery --> RouterResp["Read recommendedContextWindow<br/>(Derived from Largest Capability of Fleet)"]
    
    ProviderCheck -- "Ollama (Standalone)" --> OllamaQuery["POST /api/show { model }"]
    OllamaQuery --> ParseShow["Parse parameters (num_ctx)<br/>or model_info (*.context_length)"]
    
    RouterResp --> ApplyContext["Set Effective ContextWindowTokens<br/>in ModelCapabilityResolver & Crucible Compaction"]
    ParseShow --> ApplyContext
```

#### Discovery Mechanics:
1. **OllamaRouter:** Calls `GET /api/router/models/{name}`. The router dynamically returns `recommendedContextWindow`, which represents the **largest capability across all capable nodes in the fleet** (`max(target.contextCeiling)` for capable targets, or cloud overflow ceiling).
   - **Crucible Compaction Fleet Rule:** Context compaction (`CrucibleCompactionOptions.ContextWindowTokens`) MUST be sized to the fleet's maximum context capability, not constrained to the smallest node. Because OllamaRouter dynamically routes requests exceeding smaller nodes' VRAM to higher-tier nodes (e.g. Server) or cloud overflow, sizing compaction to the largest capability ensures conversations are never compacted prematurely when the cluster has the hardware capacity to handle extended context.
   - **Server-Class 32 GB Threshold ([OllamaRouter Task #21](file:///C:/Users/Julien/source/repos/jetelain/OllamaRouter/docs/tasks/2026-10-26-session-affinity-header-and-32gb-server-class-hardware-ceiling.md)):** OllamaRouter partitions target classes as:
     - **Small (< 12 GB):** Laptop (8 GB) — MoE & SmallDense offload.
     - **Desktop (12 GB – 32 GB):** Desktop (16 GB) — capped at 48k context (`49,152` tokens).
     - **Server (≥ 32 GB):** AI Server 2 (64 GB) & AI Server 1 (96 GB) — full 260k context (`262,144` tokens) and Server LargeDense role matching.
     In a fleet containing AI Server 1 or 2, `recommendedContextWindow` for Qwen 3.8 27B will correctly resolve to **262,144 tokens**.
2. **Standalone Ollama:** Calls `POST /api/show`:
   - Reads `parameters`: searches for `num_ctx <int>` in Modelfile.
   - If absent, reads `model_info`: searches for keys ending with `.context_length` (e.g. `qwen2.context_length: 32768`).
   - If absent, safely falls back to standard Ollama default (4,096 tokens).
3. **Caching:** Discovered capabilities are cached in memory (`IOllamaModelCatalogService`) keyed by model name and digest to prevent repeated network requests.

---

### 2.4 Dynamic Fleet Topology & Clean Gateway Service

#### Refactored `IOllamaRouterGatewayService`
```csharp
public record FleetTargetHardware(
    string? HardwareHint,
    string GpuVendor,
    IReadOnlyList<string> Devices,
    int? MaxVramMB,
    int MaxParallel);

public record FleetTargetExecution(
    int InFlightRequests,
    string? ActiveModel,
    string ContextAllocation);

public record FleetTargetStatus(
    string Name,
    string? Url,
    bool Enabled,
    string Liveness,
    long? LatencyMs,
    FleetTargetHardware Hardware,
    FleetTargetExecution Execution);

public record GatewayStatusReport(
    bool IsConnected,
    LlmProviderType ProviderType,
    string BaseUrl,
    string Version,
    IReadOnlyList<FleetTargetStatus> FleetTargets);

public interface IOllamaRouterGatewayService
{
    Task<GatewayStatusReport> GetGatewayStatusAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ResidentModelInfo>> GetResidentModelsAsync(CancellationToken cancellationToken = default);
    Task<int> ResolveContextWindowAsync(string modelName, CancellationToken cancellationToken = default);
    Task<bool> ReclaimVramAsync(string? targetName = null, CancellationToken cancellationToken = default);
}
```

#### UI Presentation in Web Dashboard
1. **OllamaRouter Gateway Card (`OllamaRouterGatewayCard.razor`):**
   - Renders dynamic cards for every node in `FleetTargets` (e.g., `Desktop (16 GB)`, `Laptop (8 GB)`, `Server 1 (96 GB)`, `Server 2 (64 GB)`, `Cloud Overflow`).
   - Displays real-time latency, active model, hardware tier (Desktop vs Server), and in-flight load per node.
2. **Cluster Monitor (`OllamaRouterClusterMonitor.razor`):**
   - Shows active fleet execution state without requiring heavy monitoring telemetry overhead.

---

### 2.5 Multi-Agent Session Affinity & KV-Cache Stickiness

In multi-agent environments where NightCodeForge runs multiple concurrent or interleaved agent workflows:
1. **Outbound Header Injection (`OllamaChatClient.cs`):**
   When `ProviderType == LlmProviderType.OllamaRouter`, `OllamaChatClient` inspects the inference context for an active session key:
   - Primary: `context.SessionId` (e.g., persistent agent conversation UUID or workflow run ID).
   - Fallback: `context.ConversationId?.ToString()` or `context.TaskId?.ToString()`.
   - Header Attached: `X-OllamaRouter-Session-Id: {sessionId}` (and optional `X-OllamaRouter-Agent-Id: {role}`).
2. **Router-Side Target Binding:**
   - OllamaRouter awards a `SessionAffinityBonus (+150)` in Stage 2 candidate scoring, ensuring consecutive turns from the same agent stick to the same physical node.
   - **KV-Cache Preservation:** Consecutive turns hit the node holding the prompt's pre-computed KV cache, eliminating prompt evaluation latency spikes.
   - **Isolation:** Agent 1 (e.g. Architect) naturally binds to `aiserver1` (or Desktop), while Agent 2 (e.g. Developer) binds to `aiserver2` (or Laptop), avoiding eviction contention and queueing.
3. **Resilience & Failover:**
   - If the assigned node goes offline or exceeds failure streak limits, OllamaRouter breaks the lease and rebinds the session to the next best candidate automatically. NightCodeForge remains completely decoupled from physical node management.

---

## 3. Implementation Checklist & Deliverables

### Phase 1: Core Domain & Abstractions
- [ ] Add `LlmProviderType` enum (`Ollama`, `OllamaRouter`) in `NightCodeForge.Core.Domain`.
- [ ] Update `LlmProviderOptions` in `NightCodeForge.Agents.Inference` with unified baseline and role differentiation settings.
- [ ] Add `SessionId` support to `InferenceContext` / `ChatRequestOptions` in `NightCodeForge.Agents`.
- [ ] Enhance `IOllamaRouterGatewayService` with clean `GatewayStatusReport`, `FleetTargetStatus`, and `ResolveContextWindowAsync`.

### Phase 2: Gateway Service & Discovery Engine
- [ ] Refactor `OllamaRouterGatewayService` in `NightCodeForge.Infrastructure.Inference`:
  - Consume `GET /api/router/info` and `GET /api/router/fleet` when connected to OllamaRouter.
  - Implement fallback to `GET /api/version` and `POST /api/show` when connected to standalone Ollama.
  - Implement `ResolveContextWindowAsync` using clean router model capabilities or Ollama `show`.
  - Implement `ReclaimVramAsync` calling `POST /api/router/vram/reclaim` (Router) or `POST /api/generate` `keep_alive: 0` (Ollama).
- [ ] Update `OllamaChatClient.cs`:
  - Attach `X-OllamaRouter-Session-Id` header with current session ID when `ProviderType == LlmProviderType.OllamaRouter`.
  - Bypass `X-OllamaRouter-Target` and `X-OllamaRouter-Session-Id` injection completely when `ProviderType == LlmProviderType.Ollama`.

### Phase 3: Web UI & Settings Ergonomics
- [ ] Update `Settings.razor` / `AgentProfilesTab.razor`:
  - Provider selector pill toggle (`Ollama (Single Host)` vs `OllamaRouter (Fleet)`).
  - Default Model & Thinking Effort fields.
  - Optional "Differentiate Implementation & Review Roles" checkbox:
    - Exposes Coder Model + Thinking Effort.
    - Exposes Architect Model + Thinking Effort.
  - Hide OllamaRouter Target Pinning selectors when `Ollama` is selected.
  - Auto-populate discovered context window next to model dropdown with a badge (e.g. `⚡ 262,144 tokens discovered`).
- [ ] Update `OllamaRouterGatewayCard.razor` and `OllamaRouterClusterMonitor.razor` to dynamically render arbitrary fleet targets (Desktop, Laptop, Server 1, Server 2).

### Phase 4: Automated Testing
- [ ] Unit tests for `OllamaRouterGatewayService`:
  - Connect to mocked OllamaRouter (`/api/router/*` responses).
  - Connect to mocked standalone Ollama (`/api/version` and `/api/show` responses).
- [ ] Unit tests for `OllamaChatClient`:
  - Header suppression under `Ollama` mode.
  - Header propagation (`X-OllamaRouter-Session-Id`) under `OllamaRouter` mode.
- [ ] End-to-end component tests for Settings and Profile tabs.

---

## 4. Acceptance Criteria

1. **Standalone Ollama Verification:** When pointed to a vanilla Ollama instance without OllamaRouter, NightCodeForge connects cleanly, reports `Provider: Ollama (Single Host)`, lists resident models from `/api/ps`, hides target pinning, and automatically discovers context window size via `POST /api/show`.
2. **OllamaRouter Multi-Node Verification:** When pointed to OllamaRouter, NightCodeForge connects cleanly, reports `Provider: OllamaRouter`, queries `GET /api/router/fleet`, renders all configured fleet machines (Desktop 16 GB, Laptop 8 GB, Server 1 96 GB, Server 2 64 GB), and routes requests with dynamic context sizing.
3. **Context Window Accuracy & Fleet Compaction Sizing:** Selecting a model (e.g. `qwen2.5-coder:32b` or `qwen3.8:27b`) dynamically retrieves its safe context window from the provider and updates `ModelCapabilityResolver` without requiring manual user input. In a multi-node fleet with Server-class nodes (≥ 32 GB), `ContextWindowTokens` for compaction is sized according to the **largest capability of the fleet** (262,144 tokens for 27B on Server), ensuring conversations are not compacted prematurely while high-capacity nodes can handle extended context.
4. **Session Affinity & KV-Cache Stickiness:** When NightCodeForge runs multiple agents concurrently against OllamaRouter, each agent transmits its distinct `X-OllamaRouter-Session-Id`. Consecutive LLM turns for Agent A stick to Agent A's node (e.g. `aiserver1`), and turns for Agent B stick to Agent B's node (e.g. `aiserver2` or `desktop`), avoiding cross-node bouncing and preserving prompt prefix KV-cache evaluation speed.
5. **VRAM Reclaim:** Triggering VRAM reclamation cleanly evicts models in both standalone Ollama and OllamaRouter modes.
