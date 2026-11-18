# 👁️ Step 12.2D: Multimodal Vision Support, In-Loop Image Inspection & Context Compaction

> **Feature Implementation Step (Part 4 of 4 — Native Multimodal Vision, Zero-KV-Loss In-Loop Injection & Compaction Hygiene)**  
> *Parent Plan:* [Features & Integrations Roadmap](README.md)  
> *Prerequisite Steps:* [Step 12.2A: Attachments Store, Intake Service & MarkItDown Conversion](../archive/phase-12/STEP_12_2A_ATTACHMENTS_STORE_INTAKE_AND_MARKITDOWN.md) | [Step 12.2B: `/workspace/attachments` Read/Bash Access, Citation Links & Prompt Digest Injection](../archive/phase-12/STEP_12_2B_AGENT_ATTACHMENT_TOOLS_AND_PROMPT_DIGEST.md) | [Step 12.2C: Attachments Web UI & Task Picker](../archive/phase-12/STEP_12_2C_ATTACHMENTS_WEB_UI_AND_TASK_PICKER.md)  
> *Related Architecture:* [Step 12.5A: Antigravity ACP Harness](../archive/phase-12/STEP_12_5A_ACP_HARNESS_CATALOG_ANTIGRAVITY_AND_CONTAINER_REWORK.md) | [Step 12.36B: CanonicalTools Modular Decomposition](../archive/phase-12/STEP_12_36B_CANONICAL_TOOLS_MODULAR_DECOMPOSITION_AND_INTERRUPTION_PIPELINE.md)  
> *Target Components:* `src/NightCodeForge.Core`, `src/NightCodeForge.Infrastructure`, `src/NightCodeForge.Agents`, `src/NightCodeForge.Web`, `tests/NightCodeForge.Core.Tests`, `tests/NightCodeForge.Infrastructure.Tests`, `tests/NightCodeForge.Agents.Tests`, `tests/NightCodeForge.Web.Tests` | *Status:* Planned | *Target Framework:* .NET 10 (C# 13)  
> *Aligns With:* [AGENTS.md](../../AGENTS.md), [docs/PLAN.md](../PLAN.md)
>
> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Objective & Zero-KV-Loss Architecture

Software engineering tasks frequently depend on visual artifacts: UI bug screenshots, mockups, architecture diagrams, Playwright failure captures, or annotated error states.

A naive **"vision description tool"** that spawns an out-of-band LLM sub-conversation (`[System: Describe this image, User: <image>]`) breaks two critical invariants of NightCodeForge's local and sticky-routed inference architecture:
1. **KV-Cache Thrashing:** On single-slot local servers (Ollama, llama.cpp, vLLM) or sticky `OllamaRouter` sessions, dispatching a side-conversation with an unrelated prompt prefix evicts or flushes the active agent's KV cache, forcing a full re-prefill (30k–100k+ tokens) on the next turn.
2. **Loss of Task Context:** An isolated vision call does not know *what* bug, CSS layout constraint, or architectural detail the agent is investigating.

Meanwhile, `Microsoft.Extensions.AI` providers impose strict protocol constraints on `ChatRole.Tool` messages:
- `OpenAIChatClient.ToOpenAIChatMessages` only serializes `FunctionResultContent` text for `ChatRole.Tool` (because OpenAI-compatible `/v1/chat/completions` endpoints only accept `image_url` content parts on `role: "user"` messages).
- `OllamaSharp.MicrosoftAi.AbstractionMapper.ToOllamaSharpMessages` emits `FunctionResultContent` as a text-only `role: "tool"` message and extracts `DataContent("image/*")` into `Message.Images` on `ChatRole.User` messages.
- Returning Base64 text inside `FunctionResultContent` causes the text tokenizer to expand an image into tens of thousands of meaningless text tokens instead of routing it through the vision projector (`mmproj` / ViT).

This step (Part 4 of Step 12.2) solves all three constraints by introducing **native multimodal vision** via **In-Loop Paired `Tool` Result + `ChatRole.User(DataContent)` Append**, using the existing canonical `read` tool with zero extra tools added to the agent schema:

```mermaid
sequenceDiagram
    participant Model as Multimodal LLM (Same SessionId & KV Cache)
    participant FICC as FunctionInvokingChatClient
    participant Inject as MultimodalToolInjectionChatClient
    participant Tool as WorkspaceFileToolModule (read)

    Model->>FICC: Turn k: Assistant calls read("/workspace/attachments/001-ui-bug.png") [call_1]
    FICC->>Tool: Execute ReadAsync("/workspace/attachments/001-ui-bug.png")
    Tool->>Tool: Check SupportsVision, validate symlink & magic bytes, downscale if > MaxVisionImageDimensionPx, stage DataContent
    Tool-->>FICC: Return FunctionResultContent("Loaded image '/workspace/attachments/001-ui-bug.png' (1280x720, image/png, ~768 vision tokens).")
    FICC->>Inject: Turn k+1: [..., Assistant(call_1), Tool(result_1)]
    Inject->>Inject: Append User([TextContent("[Visual Attachment: ... (call_id: call_1)]"), DataContent(bytes, mediaType)])
    Inject->>Model: [..., Assistant(call_1), Tool(result_1), User(DataContent)]
    Note over Model: Prefix [M_0 .. Assistant(call_1)] is 100% KV-cached!<br/>Only Tool(result_1) + Vision tokens are prefilled; cached across Turn k+2..N.
```

### Two Complementary Entry Points
1. **Upfront User Turn Attachments (Ideation Chat, Triage & ACP):** When a developer attaches or cites an image in an Ideation Chat turn (or Triage when vision is permitted), normalized `DataContent` is attached directly to that `ChatMessage(ChatRole.User)` turn (or sent as `{ type: "image", data, mimeType }` blocks in [`AcpAgentHarness`](../../src/NightCodeForge.Agents/Acp/AcpAgentHarness.cs) `session/prompt`) — zero extra tool round-trips, prefilled once and KV-cached for the remainder of the conversation.
2. **On-Demand Agent Inspection (Canonical `read` Tool):** Layer 1–3 prompt prefixes ([`PromptPrefixStabilizer`](../../src/NightCodeForge.Agents/Optimizations/PromptPrefixStabilizer.cs)) remain 100% text-only and byte-identical across tasks (`<workspace_attachments>` / `<task_attachments>` list image dimensions, media types, and `/workspace/attachments/...` paths as compact text digests). When an agent inspects an image attachment (`/workspace/attachments/...`) or any workspace image file (e.g., screenshot generated inside `/workspace/task-{id}/...`), [`WorkspaceFileToolModule.ReadAsync`](../../src/NightCodeForge.Agents/Tools/WorkspaceFileToolModule.cs) natively stages the normalized `DataContent` and `MultimodalToolInjectionChatClient` appends the paired `ChatRole.User` message right after the `ChatRole.Tool` result.

---

## 2. Model Vision Capability Discovery & Fail-Fast Guardrails

> **Service Design & Constraint Alignment ([AGENTS.md](../../AGENTS.md)):** All new and modified services must require all collaborators (no optional constructor parameters, no silent fallbacks or bare `catch { }` blocks), never exceed **10 constructor parameters**, and define named constants/enums instead of magic numbers or strings.

### 2.1 Dynamic Discovery & Static Overrides
1. **`OllamaShowParser` ([`OllamaShowParser.cs`](../../src/NightCodeForge.Infrastructure/Inference/OllamaShowParser.cs)):**
   - Add `SupportsVision(JsonElement root)` (and corresponding capability constant `VisionCapability = "vision"` and `"projector_info"` key constant) inspecting the `POST /api/show` payload for `"vision"` in `capabilities` or presence of `"projector_info"` metadata.
2. **Domain & Catalog Models:**
   - Add `public bool? SupportsVision { get; set; }` to [`ModelCapabilityProfile`](../../src/NightCodeForge.Core/Inference/ModelCapabilityProfile.cs) so OpenAI-compatible or custom models can explicitly declare or override vision support in configuration.
   - Add `bool SupportsVision = false` to [`AvailableModelInfo`](../../src/NightCodeForge.Core/Interfaces/ILlmModelCatalogService.cs) and populate it across model catalog services and UI dropdown badges (e.g., `👁️ Vision`).
   - Extend [`ILlmContextWindowCache`](../../src/NightCodeForge.Core/Interfaces/ILlmContextWindowCache.cs) (and `EmptyLlmContextWindowCache`), [`ILlmGatewayService`](../../src/NightCodeForge.Core/Interfaces/IOllamaRouterGatewayService.cs) (`ResolveVisionCapabilityAsync` or combined capability resolution in [`OllamaGatewayServiceBase`](../../src/NightCodeForge.Infrastructure/Inference/OllamaGatewayServiceBase.cs) / [`OpenAiCompatibleGatewayService`](../../src/NightCodeForge.Infrastructure/Inference/OpenAiCompatibleGatewayService.cs)), and [`ILlmCapabilityResolver`](../../src/NightCodeForge.Agents/Inference/ILlmCapabilityResolver.cs) (`SupportsVision(string? modelId)` implemented in [`OllamaCapabilityResolver`](../../src/NightCodeForge.Agents/Inference/OllamaCapabilityResolver.cs) and [`OpenAiCapabilityResolver`](../../src/NightCodeForge.Agents/Inference/OpenAiCapabilityResolver.cs)).

### 2.2 Fail-Fast Domain Exception (`ModelVisionNotSupportedException`)
- Define `ModelVisionNotSupportedException` in `NightCodeForge.Core.Exceptions` (naming the model identifier and the rejected visual input).
- **Prompt Guidance Awareness:** Indicate in system prompt context whether multimodal image `read` is supported by the active model so text-only models do not waste turns attempting to inspect binary images.
- **No Silent Fallback on Text-Only Models:**
  - If [`WorkspaceFileToolModule.ReadAsync`](../../src/NightCodeForge.Agents/Tools/WorkspaceFileToolModule.cs) (`read`) targets an image file when the active model does not support vision, or if a user attempts to dispatch upfront image attachments to a text-only model, fail fast with `ModelVisionNotSupportedException` (surfaced cleanly as an actionable error in the UI or as a deterministic tool error to the agent — never silently dropping images or fabricating descriptions).

---

## 3. Canonical `read` Image Handling, Normalization & `MultimodalToolInjectionChatClient`

### 3.1 Zero New Tool Additions & Constructor Ceiling
- **Minimalist Tool Approach ([AGENTS.md](../../AGENTS.md)) — No New Tools in `CanonicalToolNames` or `DefaultAgentTools`:**
  - Following the minimalist tool policy in [AGENTS.md](../../AGENTS.md), re-use existing canonical tools with dedicated mount points to keep context size minimal: do **not** add any dedicated image or attachment tool to [`CanonicalToolNames`](../../src/NightCodeForge.Core/Domain/CanonicalToolNames.cs) or [`DefaultAgentTools`](../../src/NightCodeForge.Core/Domain/DefaultAgentTools.cs), and no agent profile migration/reconciliation is required.
  - Agents use the existing canonical `read` tool ([`WorkspaceFileToolModule.ReadAsync`](../../src/NightCodeForge.Agents/Tools/WorkspaceFileToolModule.cs)) across any readable sandbox mount (`/workspace/attachments/...`, `/workspace/task-{id}/...`, `/workspace/clone/...`).
  - Update the `read` tool description in [`WorkspaceFileToolModule`](../../src/NightCodeForge.Agents/Tools/WorkspaceFileToolModule.cs) (when vision is supported) to state that it also inspects supported image files (`.png`, `.jpg`, `.jpeg`, `.webp`, `.gif`).
- **Constructor Parameter Ceiling ([AGENTS.md](../../AGENTS.md)):**
  - [`CrucibleAgentHarness`](../../src/NightCodeForge.Agents/Crucible/CrucibleAgentHarness.cs) (10 parameters) and [`IdeationService`](../../src/NightCodeForge.Agents/Ideation/IdeationService.cs) (10 parameters) are already at the **10-constructor-parameter ceiling** and **must not** add constructor parameters.
  - Wire multimodal staging and vision-capability flags through [`CanonicalToolScope`](../../src/NightCodeForge.Agents/Tools/CanonicalToolsetFactory.cs), [`CanonicalTools`](../../src/NightCodeForge.Agents/Tools/CanonicalTools.cs), [`CanonicalToolsetFactory`](../../src/NightCodeForge.Agents/Tools/CanonicalToolsetFactory.cs) (currently 8 parameters), and [`HarnessAgentFactory`](../../src/NightCodeForge.Agents/Inference/HarnessAgentFactory.cs) (currently 6 parameters).

### 3.2 `WorkspaceFileToolModule.ReadAsync` Image Contracts & Normalization
- **Image File Dispatch in `ReadAsync`:**
  - Resolve `path` via [`ISandboxPathResolver.ResolveHostPath(path, forWrite: false)`](../../src/NightCodeForge.Core/Interfaces/SandboxPathResolver.cs) (preventing path traversal and honoring sandbox mount exclusions such as Stage 0 Triage excluding `/workspace/attachments`).
  - When the resolved file has a supported image extension (`.png`, `.jpg`, `.jpeg`, `.webp`, `.gif`) or matches an image magic-byte signature, branch into the multimodal image loading path (treating `offset`/`limit` as unused for binary images) instead of reading UTF-8 text lines.
- **Vision Capability, Symlink & Magic-Byte Validation:**
  - Check `SupportsVision` for the session's active model first; if `false`, fail fast with `ModelVisionNotSupportedException` (never attempting to pass binary bytes as text lines).
  - Verify file existence and, for symlinked host attachments inside `attachments/` ([Step 12.2B](../archive/phase-12/STEP_12_2B_AGENT_ATTACHMENT_TOOLS_AND_PROMPT_DIGEST.md)), verify the symlink resolves to its registered `SourceRef`, throwing `AttachmentTargetNotFoundException` if moved or unreadable.
  - Inspect file magic bytes to validate supported image media types (`image/png`, `image/jpeg`, `image/webp`, `image/gif`). Reject mismatched extensions, zero-byte files, or corrupt headers with `AttachmentConversionException` (never silent fallbacks).
- **Named Size & Dimension Caps:**
  - Define named constants (e.g., in `VisionConstants` / `WorkflowDefaults`):
    - `MaxVisionImageBytes = 10 * 1024 * 1024` (10 MB raw input cap before normalization; reject oversized raw files cleanly).
    - `MaxVisionImageDimensionPx = 1568` (downscale oversized images proportionally preserving aspect ratio and encode to compact PNG/JPEG before building `DataContent` so each image uses predictable vision tokens).
    - `MaxImagesInActiveWindow = 5` (maximum live `DataContent` image payloads retained simultaneously in uncompacted chat history).

### 3.3 `MultimodalToolInjectionChatClient` In-Loop Middleware
- Place `MultimodalToolInjectionChatClient` (a `DelegatingChatClient`) outermost in [`HarnessAgentFactory.CreateAsync`](../../src/NightCodeForge.Agents/Inference/HarnessAgentFactory.cs) wrapping `activeClient` (above `DecorateTracingClient` / [`PerServiceCallChatHistoryPersistingChatClient`](../../src/NightCodeForge.Agents/Crucible/ChatClients/PerServiceCallChatHistoryPersistingChatClient.cs) and [`ThinkingAndActivityTracingChatClient`](../../src/NightCodeForge.Agents/Inference/ThinkingAndActivityTracingChatClient.cs)) between `FunctionInvokingChatClient` (inside `HarnessAgent`) and the inner middleware stack, sharing a session-scoped `IMultimodalAttachmentQueue` with `CanonicalToolsetSession`.
- **Execution Flow:**
  1. When `WorkspaceFileToolModule.ReadAsync` (`read`) loads a valid image during the tool loop, it correlates the call via `FunctionInvokingChatClient.CurrentContext?.CallContent?.CallId` (falling back to a correlation marker/path match when invoked directly outside `FunctionInvokingChatClient`, e.g., in unit tests) and stages a `StagedVisualContent(CallId, Path, MediaType, Bytes, WidthPx, HeightPx, Sha256, EstimatedTokens)` into the session's `IMultimodalAttachmentQueue`.
  2. `ReadAsync` returns a concise text `FunctionResultContent`:  
     `"Loaded image '<path>' (<width>x<height>, <mediaType>, ~<tokens> vision tokens). Visual content attached in the following user message."`
  3. On the next `GetResponseAsync` / `GetStreamingResponseAsync` call inside `FunctionInvokingChatClient`'s loop, `MultimodalToolInjectionChatClient` inspects the `messages` list: for each `ChatMessage(ChatRole.Tool)` containing a `FunctionResultContent` whose `CallId` matches a staged visual item (retained in `IMultimodalAttachmentQueue` for the session) that does not yet have its paired user message immediately following it in `messages`, it inserts immediately after that `ChatRole.Tool` message:
     ```text
     ChatMessage(
       role: ChatRole.User,
       contents: [
         TextContent("[Visual Attachment: <path> (call_id: <callId>, dimensions: <width>x<height>)]"),
         DataContent(bytes, mediaType)
       ])
     ```
  4. Because `MultimodalToolInjectionChatClient` wraps `activeClient` outermost above `PerServiceCallChatHistoryPersistingChatClient` and `ThinkingAndActivityTracingChatClient` and retains staged items for the session, both `PerServiceCallChatHistoryPersistingChatClient` (for `"image_ref"` SQLite persistence) and `ThinkingAndActivityTracingChatClient` (for `UnchangedPrefixCount` across turns $k+1 \dots N$) observe the injected `ChatRole.User(DataContent)` message. Every prior message `[M_0 .. Assistant(tool_call)]` remains byte-identical across Turn $k+1$ and all subsequent turns $k+2 \dots N$, preserving 100% KV-cache prefix hits for both text and vision tokens while staying strictly compliant with OpenAI and Ollama message ordering rules.

---

## 4. Upfront User Turn Attachments & `AcpAgentHarness` Integration

### 4.1 Ideation Chat & Triage User Turns
- **Direct `ChatRole.User` `DataContent`:** When a developer uploads, pastes, or explicitly attaches an image attachment in an Ideation Chat turn ([`IdeationService`](../../src/NightCodeForge.Agents/Ideation/IdeationService.cs) / [`Ideation.razor`](../../src/NightCodeForge.Web/Components/Pages/Ideation.razor)) or Triage turn ([`TriageService`](../../src/NightCodeForge.Agents/Triage/TriageService.cs) / [`Triage.razor`](../../src/NightCodeForge.Web/Components/Pages/Triage.razor)) — and the profile's model supports vision — normalize the image and attach `DataContent` directly to that turn's `ChatMessage(ChatRole.User)` alongside the user text prompt.
- **No Redundant Tool Round-Trip:** Upfront user images are evaluated in the primary turn and immediately enter the sticky session KV cache.
- **UI Presentation Separation ([AGENTS.md](../../AGENTS.md)):** Image upload validation, thumbnail preview metadata, vision-capability gating warnings (disabling or warning clearly when the active profile model lacks `SupportsVision`), and attachment-to-turn binding live in `NightCodeForge.Web.Services` (`AttachmentPresentationHelper` / `IAttachmentUiService`), never inside `.razor` `@code` blocks.

### 4.2 `AcpAgentHarness` (`agy acp`) Native Multimodal Prompt Blocks
- Extend `AgentSessionContext` and [`AcpAgentHarness`](../../src/NightCodeForge.Agents/Acp/AcpAgentHarness.cs) (`DispatchPromptAsync` `session/prompt` payload) so when image attachments are linked to the prompt/task (and normalized within size caps), `session/prompt` emits `{ type = "image", data = "<base64>", mimeType = "<mimeType>" }` content blocks alongside `{ type = "text", text = promptText }`.
- Additionally, because `/workspace/attachments` (`:ro`) and `/workspace/task-{id}` (`:rw`) are bind-mounted into `agy` sessions ([Step 12.2A](../archive/phase-12/STEP_12_2A_ATTACHMENTS_STORE_INTAKE_AND_MARKITDOWN.md) & [Step 12.5A](../archive/phase-12/STEP_12_5A_ACP_HARNESS_CATALOG_ANTIGRAVITY_AND_CONTAINER_REWORK.md)), `agy` can inspect workspace screenshots and attachment images directly on disk using its native multimodal file tools.

---

## 5. Token Estimation, Reference-Only SQLite Persistence & Context Compaction

### 5.1 Vision Token Estimation (`ModelCalibratedTokenEstimator`)
- Extend [`ModelCalibratedTokenEstimator.EstimateMessageTokens`](../../src/NightCodeForge.Agents/Compaction/ModelCalibratedTokenEstimator.cs) with a `case DataContent dc when dc.HasTopLevelMediaType("image"):` branch (and update `ComputeActualMessageBytes` accordingly so `EstimateGroupTokens` accurately tracks image-bearing groups).
- Define named constants for deterministic tile-based vision token estimation:
  - `VisionTileSizePx = 512`
  - `VisionBaseTokens = 85`
  - `VisionTokensPerTile = 170`
  - `DefaultFallbackImageTokens = 768`
- Parse width and height from PNG, JPEG, WebP, or GIF binary headers; compute `tilesX * tilesY * VisionTokensPerTile + VisionBaseTokens` (or `DefaultFallbackImageTokens` if dimensions are unavailable from headers).

### 5.2 Reference-Only SQLite Serialization (`ConversationHistorySerializer` & `CrucibleAgentHarness`)
- **Zero Base64 Blobs in SQLite:** Never persist raw Base64 image strings inside SQLite `ConversationTurns` or `SerializedStateJson`.
- Extend [`ConversationHistorySerializer`](../../src/NightCodeForge.Core/Workflow/ConversationHistorySerializer.cs) and [`CrucibleAgentHarness`](../../src/NightCodeForge.Agents/Crucible/CrucibleAgentHarness.cs) (`ToSerializedContent` / `FromSerializedContent`, `RecordTracedEntry`, and [`CrucibleConversationCoordinator`](../../src/NightCodeForge.Agents/Crucible/CrucibleConversationCoordinator.cs)):
  - Add `ContentTypeImageRef = "image_ref"` and structured metadata fields (`SourcePath`, `MediaType`, `Sha256`, `WidthPx`, `HeightPx`) to `SerializedContent`.
  - When persisting history, serialize each image `DataContent` as an `"image_ref"` entry referencing the immutable file on disk (workspace attachment or sandbox path + SHA-256).
  - When restoring an uncompacted turn via `DeserializeSession`, rehydrate `DataContent` from disk (verifying SHA-256 and applying normalization caps); if the underlying disk file is missing or deleted during resumption, record an explicit missing-target placeholder / fail fast according to attachment policy without crashing history deserialization.
- Extend [`ThinkingAndActivityTracingChatClient.DescribeMessage`](../../src/NightCodeForge.Agents/Inference/ThinkingAndActivityTracingChatClient.cs) and `Transcript` rendering so traced model call snapshots display `[image: <mediaType>, <width>x<height>, <byteCount> bytes]` rather than opaque `[DataContent]`.

### 5.3 Context Compaction & Sliding Image Window (`PiStyleCrucibleCompactionStrategy`)
- **Summarizer Transcript Serialization ([`ConversationSerializer`](../../src/NightCodeForge.Agents/Compaction/ConversationSerializer.cs)):**
  - In `ConversationSerializer.SerializeParts`, render `DataContent("image/*")` parts as compact text placeholders (`[Visual Attachment: <path> (<width>x<height>, <mediaType>)]`) so the text summarizer LLM never receives raw image bytes or fails with unsupported-modality errors.
- **Compaction Eviction & Active Image Cap (`MaxImagesInActiveWindow = 5`):**
  - When older message groups are compacted by [`PiStyleCrucibleCompactionStrategy`](../../src/NightCodeForge.Agents/Compaction/PiStyleCrucibleCompactionStrategy.cs), their `DataContent` byte payloads are evicted alongside the compacted messages while the structured checkpoint summary preserves relevant visual findings.
  - Additionally, enforce a sliding-window eviction guard in `PiStyleCrucibleCompactionStrategy` / `MultimodalToolInjectionChatClient`: when more than `MaxImagesInActiveWindow` (`5`) live `DataContent` images exist in the uncompacted window, strip the `DataContent` bytes from the oldest image messages beyond the cap and replace them with a text marker (`[Earlier visual attachment '<path>' evicted from active image window to preserve context headroom; re-invoke read("<path>") if needed]`), protecting VRAM and inference stability.

---

## 6. Implementation Checklist & Deliverables

- [ ] **Core Exceptions, Capabilities & History Contracts (`NightCodeForge.Core`):**
  - [ ] Add `ModelVisionNotSupportedException` in `NightCodeForge.Core.Exceptions`.
  - [ ] Add `SupportsVision` to [`ModelCapabilityProfile`](../../src/NightCodeForge.Core/Inference/ModelCapabilityProfile.cs), [`AvailableModelInfo`](../../src/NightCodeForge.Core/Interfaces/ILlmModelCatalogService.cs), [`ILlmContextWindowCache`](../../src/NightCodeForge.Core/Interfaces/ILlmContextWindowCache.cs), and [`ILlmGatewayService`](../../src/NightCodeForge.Core/Interfaces/IOllamaRouterGatewayService.cs).
  - [ ] Add `"image_ref"` serialization contract to [`ConversationHistorySerializer`](../../src/NightCodeForge.Core/Workflow/ConversationHistorySerializer.cs) with zero Base64 persistence in SQLite.
- [ ] **Capability Discovery (`NightCodeForge.Infrastructure`):**
  - [ ] Extend [`OllamaShowParser`](../../src/NightCodeForge.Infrastructure/Inference/OllamaShowParser.cs), [`OllamaGatewayServiceBase`](../../src/NightCodeForge.Infrastructure/Inference/OllamaGatewayServiceBase.cs), [`OpenAiCompatibleGatewayService`](../../src/NightCodeForge.Infrastructure/Inference/OpenAiCompatibleGatewayService.cs), and model catalog services to probe and cache `SupportsVision`.
- [ ] **Canonical `read` Image Loading, Middleware, Token Estimator & Harnesses (`NightCodeForge.Agents`):**
  - [ ] Implement image header inspection, magic-byte validation, and aspect-preserving downscaling (`MaxVisionImageDimensionPx = 1568`, `MaxVisionImageBytes = 10 MB`).
  - [ ] Extend [`WorkspaceFileToolModule.ReadAsync`](../../src/NightCodeForge.Agents/Tools/WorkspaceFileToolModule.cs) (`read`) to detect image files across readable sandbox mounts (`/workspace/attachments/...`, `/workspace/task-{id}/...`, `/workspace/clone/...`), enforce `SupportsVision` (`ModelVisionNotSupportedException`) and symlink integrity (`AttachmentTargetNotFoundException`), and stage normalized `DataContent` into `IMultimodalAttachmentQueue` correlated via `FunctionInvokingChatClient.CurrentContext?.CallContent?.CallId` (with a fallback correlation marker/path match when invoked directly in unit tests), without adding new tools to `CanonicalToolNames` or `DefaultAgentTools`.
  - [ ] Implement `MultimodalToolInjectionChatClient` + session-retained `IMultimodalAttachmentQueue` wired outermost around `activeClient` in [`HarnessAgentFactory`](../../src/NightCodeForge.Agents/Inference/HarnessAgentFactory.cs) (above `DecorateTracingClient` / [`PerServiceCallChatHistoryPersistingChatClient`](../../src/NightCodeForge.Agents/Crucible/ChatClients/PerServiceCallChatHistoryPersistingChatClient.cs) and [`ThinkingAndActivityTracingChatClient`](../../src/NightCodeForge.Agents/Inference/ThinkingAndActivityTracingChatClient.cs)) and [`CanonicalToolsetFactory`](../../src/NightCodeForge.Agents/Tools/CanonicalToolsetFactory.cs) (preserving $\le 10$ constructor parameters on `CrucibleAgentHarness` and `IdeationService`) so both history persistence and tracing observe the appended `ChatRole.User([TextContent, DataContent])` messages immediately after `ChatRole.Tool` results without mutating prior message prefixes.
  - [ ] Extend [`ILlmCapabilityResolver`](../../src/NightCodeForge.Agents/Inference/ILlmCapabilityResolver.cs) ([`OllamaCapabilityResolver`](../../src/NightCodeForge.Agents/Inference/OllamaCapabilityResolver.cs) & [`OpenAiCapabilityResolver`](../../src/NightCodeForge.Agents/Inference/OpenAiCapabilityResolver.cs)) with `SupportsVision`.
  - [ ] Extend [`IdeationService`](../../src/NightCodeForge.Agents/Ideation/IdeationService.cs), [`TriageService`](../../src/NightCodeForge.Agents/Triage/TriageService.cs), and [`AcpAgentHarness`](../../src/NightCodeForge.Agents/Acp/AcpAgentHarness.cs) (`session/prompt` `{ type = "image" }` blocks) for upfront user image attachments.
  - [ ] Extend [`ModelCalibratedTokenEstimator`](../../src/NightCodeForge.Agents/Compaction/ModelCalibratedTokenEstimator.cs) (tile-based vision token calculation), [`ThinkingAndActivityTracingChatClient`](../../src/NightCodeForge.Agents/Inference/ThinkingAndActivityTracingChatClient.cs) (`DescribeMessage`), [`CrucibleAgentHarness`](../../src/NightCodeForge.Agents/Crucible/CrucibleAgentHarness.cs) (`image_ref` round-tripping), [`ConversationSerializer`](../../src/NightCodeForge.Agents/Compaction/ConversationSerializer.cs), and [`PiStyleCrucibleCompactionStrategy`](../../src/NightCodeForge.Agents/Compaction/PiStyleCrucibleCompactionStrategy.cs) (`MaxImagesInActiveWindow = 5` sliding eviction).
- [ ] **Web UI Vision Gating & Composer Support (`NightCodeForge.Web`):**
  - [ ] Surface `👁️ Vision` model badges in settings/profile pickers and disable or clearly warn in [`Ideation.razor`](../../src/NightCodeForge.Web/Components/Pages/Ideation.razor) / [`Triage.razor`](../../src/NightCodeForge.Web/Components/Pages/Triage.razor) (via `NightCodeForge.Web.Services` helpers, no logic in `.razor` `@code`) when attempting to attach images to a text-only model.
  - [ ] Render image thumbnails/badges in chat transcripts and inspector turns for `image_ref` / visual attachments.
- [ ] **Tests:**
  - [ ] Unit tests in `NightCodeForge.Core.Tests` and `NightCodeForge.Infrastructure.Tests`: `OllamaShowParser.SupportsVision`, `ModelVisionNotSupportedException`, and `ConversationHistorySerializer` `"image_ref"` round-trip without Base64 persistence.
  - [ ] Unit tests in `NightCodeForge.Agents.Tests`: `WorkspaceFileToolModule.ReadAsync` image loading (`read("/workspace/attachments/...")` and `read("/workspace/task-{id}/...")`), path-traversal/magic-byte validation/downscaling, `MultimodalToolInjectionChatClient` KV-prefix preservation (asserting `UnchangedPrefixCount` stays intact across `Tool` $\rightarrow$ `User(DataContent)` $\rightarrow$ subsequent turns), `ModelVisionNotSupportedException` on text-only models, `ModelCalibratedTokenEstimator` image tile estimation, `ConversationSerializer` text-marker rendering, `PiStyleCrucibleCompactionStrategy` `MaxImagesInActiveWindow` eviction, and `AcpAgentHarness` `session/prompt` image blocks.
  - [ ] Unit/bUnit tests in `NightCodeForge.Web.Tests`: vision capability badge/gating warnings in composer services and chat image rendering, followed by full test suite execution (including Acceptance & E2E suites) per [AGENTS.md](../../AGENTS.md).
