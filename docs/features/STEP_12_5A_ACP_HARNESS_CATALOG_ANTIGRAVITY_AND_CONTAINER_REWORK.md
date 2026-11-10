# 🔌 Step 12.5A: ACP Harness Catalog, Antigravity (`agy acp`) Authentication & Container-Ready Rework (with `PiRpc` Removal)

> **Phase 12 Implementation Step (Extend Features - Harnesses & External Agent Integrations)**  
> *Parent Plan:* [Features & Integrations Roadmap](README.md)  
> *Companion Step:* [Step 12.5B: Task Difficulty Classification & Role-Based Harness/Model Escalation Matrix](STEP_12_5B_TASK_DIFFICULTY_AND_ROLE_ESCALATION_MATRIX.md)  
> *Source Policy:* [AGENTS.md](../../AGENTS.md)  
> *Project Target:* `src/NightCodeForge.Core`, `src/NightCodeForge.Agents`, `src/NightCodeForge.Infrastructure`, `src/NightCodeForge.Web`, `tests/NightCodeForge.Agents.Tests`, `tests/NightCodeForge.Infrastructure.Tests`  
> *Status:* Planned | *Target Framework:* .NET 10 (C# 13)

> This document is a **specification**: it states required behaviors and constraints, not implementations. Implementation choices (signatures, code structure) are left to the implementer, within the constraints of the documents.

---

## 1. Executive Summary & Architectural Motivation

### 1.1 Why Consolidate on `Crucible` + `ACP` (and Drop `PiRpc`)
1. **`Crucible` Covers All Local / Open-Weight & OpenAI-Compatible Inference:**
   - NightCodeForge's built-in [`CrucibleAgentHarness`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Agents/Crucible/CrucibleAgentHarness.cs) already provides native C# tool dispatch, guided grammar decoding, Roslyn symbol mapping, context compaction, and dynamic thinking effort mapping across Ollama, OllamaRouter, and OpenAI-compatible gateways.
   - Maintaining `PiRpcAgentHarness` (`StandardHarnessIds.PiRpc`) duplicates local LLM capabilities with an external Node.js daemon. Under the project's clean-break policy ([AGENTS.md](../../AGENTS.md)), `PiRpc` is removed.
2. **First-Class Google Antigravity CLI (`agy acp`) Integration via ACP:**
   - For `Expert` tasks (or teams leveraging cloud frontier models via their Google AI Pro/Ultra or Gemini Enterprise subscription), **Google Antigravity CLI (`agy acp`)** exposes a built-in **Agent Client Protocol (ACP)** stdio server (`https://antigravity.google/docs/cli/acp/`).
   - Instead of requiring users to manually type executable paths, CLI flags, and JSON blobs across multiple agent profiles, NightCodeForge introduces a centralized **ACP Harness Catalog** (`IAcpHarnessCatalogService`) with built-in support for **Google Antigravity (`agy acp`)**:
     - Auto-discovers the `agy` installation, authentication status (`antigravity-oauth-token`), subscription tier (e.g., `Google AI Ultra`), and available models (`gemini-4-argon`, `gemini-3.8-flash`, `claude-opus-5-5`, `claude-sonnet-5-5`, `gpt-oss-120b-medium`) + thinking levels (`low`, `medium`, `high`).
     - Seamlessly runs `agy acp` across **`BareHost`**, **`WslDirect`**, and **`ContainerSandbox`** without requiring the user to re-authenticate inside ephemeral Docker containers.

---

## 2. Empirical Protocol & Authentication Findings for `agy acp`

Live verification against `agy acp` (`v1.5.3` in WSL2 and inside `ncf-agent-base:v4` Docker containers) established the exact wire behavior and authentication mechanics that [`AcpAgentHarness`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Agents/Acp/AcpAgentHarness.cs) must implement:

### 2.1 Authentication Storage & Zero-Friction Container Mounting
1. **Where `agy` Stores Credentials in WSL2 / Linux:**
   - Binary location: `~/.local/bin/agy` (self-contained 64-bit Linux ELF binary).
   - Configuration & OAuth state directory: `~/.gemini/antigravity-cli/`, containing:
     - `antigravity-oauth-token` (`0600` permissions — OAuth token & refresh credentials created by `agy login` or `agy acp --login`).
     - `settings.json`, `installation_id`, and `jetski_state.pbtxt`.
2. **How `agy acp` Executes Tools (`run_command`, `read_file`, `write_file`, `edit_file`):**
   - Unlike thin ACP clients that delegate every command over `terminal/create`, `agy acp` includes its own built-in agent toolset (`run_command`, `read_file`, `write_file`, `edit_file`) that executes **directly inside the OS/container environment where the `agy acp` process is running**, rooted at the `cwd` passed to `session/new`.
3. **Executing `agy acp` Directly Inside `ContainerSandbox` (`ncf-agent-base:v4`):**
   - Because `NightCodeForgeContainerExecutor` runs task containers with the host/WSL user's exact `uid:gid` (e.g. `1000:1000`) and sets `HOME=/ncf-home`:
     - Mounting the WSL2/Linux host's `agy` binary to `/opt/ncf-acp/agy:ro` (and adding `/opt/ncf-acp` to `ExtraPathDirectories`) and `~/.gemini` to `/ncf-home/.gemini` (read-write so OAuth token refresh writes persist back to the host) allows `AcpAgentHarness` to spawn `agy acp` **directly inside the task container** via:
       `docker exec -i -u <uid>:<gid> -e HOME=/ncf-home -e PATH=<computedPath> -w <containerTaskWorkDir> <containerName> /opt/ncf-acp/agy acp --dangerously-skip-permissions`
     - **Important Working Directory Resolution:** In `ContainerEnvironmentFactory`, when executing a task (`effectiveTaskId.HasValue`), the host workspace root (`parentDir`) is mounted at `/workspace` (`clone/` is mounted read-only at `/workspace/clone`) and the task worktree is at **`/workspace/task-{id}`** (`options.DefaultWorkingDirectory`). Therefore, `<containerTaskWorkDir>` and `session/new` `cwd` (as well as the `<environment>` prompt block) MUST resolve to `NightCodeForgeContainerExecutor.ResolveContainerWorkingDirectory(workspacePath, options)` (i.e., `/workspace/task-{id}` for task runs, not `/workspace`).
     - Inside the container, `agy acp` is immediately authenticated, sees `/workspace/task-{id}` as its native working directory, and runs all `dotnet build`, `dotnet test`, `git`, and `csnav` commands inside the isolated container sandbox.

### 2.2 Verified `agy acp` JSON-RPC Wire Protocol Requirements
Current bugs in [`AcpAgentHarness.cs`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Agents/Acp/AcpAgentHarness.cs) that prevent it from working with `agy acp` and how they must be fixed:

1. **`initialize` Request (`clientCapabilities.terminal` Type):**
   - *Current bug:* `AcpAgentHarness` sends `"protocolVersion": 2` and `"terminal": { "create": true, "output": true }`. `agy acp`'s Go JSON unmarshaler expects `"terminal"` in `clientCapabilities` to be a `bool` (`true`), causing an unmarshal failure when an object is sent.
   - *Required payload:*
     ```json
     {
       "jsonrpc": "2.0",
       "id": "init-1",
       "method": "initialize",
       "params": {
         "protocolVersion": 1,
         "clientCapabilities": {
           "fs": { "readTextFile": true, "writeTextFile": true },
           "terminal": true
         }
       }
     }
     ```
   - *`initialize` Response from `agy acp`:*
     Returns `agentInfo` (`{ "name": "Antigravity ACP Server", "version": "1.5.3" }`), `authMethods` (`oauth-personal`, `oauth-business`, `gemini-api-key`, `agent-platform`), and `agentCapabilities._meta.antigravity.systemPrompt.append = true`.

2. **`session/new` Request, System Prompt Injection & Server-Assigned `sessionId`:**
   - *Current bug:* `AcpAgentHarness` generates a local C# GUID (`sessionId`), sends `session/new`, **never reads the `session/new` response**, and then sends that local C# GUID to `session/prompt` (which `agy acp` rejects as an unknown session).
   - *Required behavior:*
     - Pass `_meta.antigravity.systemPrompt.append` in `session/new` `@params` when `context.Profile.SystemPrompt` is non-empty:
       ```json
       {
         "jsonrpc": "2.0",
         "id": "session-1",
         "method": "session/new",
         "params": {
           "cwd": "/workspace",
           "mcpServers": [],
           "_meta": {
             "antigravity": {
               "systemPrompt": { "append": "<profile.SystemPrompt>" }
             }
           }
         }
       }
       ```
     - Read lines from `stdout` until the response with `"id": "session-1"` arrives (ignoring or processing interleaved `session/update` notifications such as `available_commands_update`).
     - If the response contains `"error"` (e.g., authentication required), fail fast with a clear `AcpAuthenticationException` / error message guiding the user to authenticate in Settings or run `agy login`.
     - Extract `result.sessionId` (e.g., `"2dc54083-334b-45c5-9841-cc5dfb88eedb"`) and use it as `activeSessionId` for all subsequent requests!

3. **Configuring Model, Thinking Effort & Agent Mode on the Session:**
   - `agy acp`'s `session/new` response returns both:
     - `models.availableModels`: combined model+effort IDs such as `"gemini-4-argon-high"`, `"gemini-4-argon-medium"`, `"gemini-4-argon-low"`, `"gemini-3.8-flash-high"`, `"claude-opus-5-5-high"`, `"claude-sonnet-5-5-high"`, `"gpt-oss-120b-medium"`.
     - `configOptions`:
       - `id: "model"` (`"gemini-4-argon"`, `"gemini-3.8-flash"`, `"claude-opus-5-5"`, `"claude-sonnet-5-5"`, `"gpt-oss-120b-medium"`)
       - `id: "thought_level"` (`"low"`, `"medium"`, `"high"`)
       - `id: "mode"` (`"default"`, `"accept-edits"`, `"plan"`)
   - *Required behavior after `session/new`:*
     - If `context.Profile.ModelName` matches a full `modelId` in `models.availableModels` (e.g., `"gemini-4-argon-high"`), invoke `session/set_model` (`{ "sessionId": activeSessionId, "modelId": modelName }`) — or if it matches a base model in `configOptions["model"]` (e.g., `"gemini-4-argon"`), invoke `session/set_config_option` (`configId = "model"`, `value = "gemini-4-argon"`) followed by `session/set_config_option` (`configId = "thought_level"`, `value = "low" | "medium" | "high"` mapped from `context.Profile.ThinkingEffort`).
     - Set `configId = "mode"` based on `context.Profile.Mode`:
       - `AgentMode.ReadOnly` $\rightarrow$ `"plan"`
       - `AgentMode.Full` / `AgentMode.Review` $\rightarrow$ `"accept-edits"`

4. **`session/prompt` Request, Streaming Events & Token Usage Extraction:**
   - *Current bug:* `AcpAgentHarness` sends `prompt = new[] { new { text = promptText } }` without `"type": "text"`.
   - *Required payload:*
     ```json
     {
       "jsonrpc": "2.0",
       "id": "prompt-1",
       "method": "session/prompt",
       "params": {
         "sessionId": "<activeSessionId>",
         "prompt": [
           { "type": "text", "text": "<promptText>" }
         ]
       }
     }
     ```
   - *Streaming `tool_call` & `tool_call_update` parsing:*
     - `agy acp` emits:
       `{"sessionUpdate":"tool_call","toolCallId":"...","name":"run_command","args":{"CommandLine":"...","toolSummary":"..."}}`
     - `AcpAgentHarness` must extract tool name from `update["name"] ?? update["title"] ?? update["toolCallId"]` and arguments from `update["args"] ?? update["rawInput"]` so the Task Execution Inspector and live activity stream display clean tool names and summaries.
   - *Token Usage Extraction on Completion:*
     - When the `"id": "prompt-1"` response arrives, extract `result.usage`:
       - `inputTokens` $\rightarrow$ `promptTokens`
       - `outputTokens` (+ `thoughtTokens` if reported separately) $\rightarrow$ `completionTokens`
       - `totalTokens` $\rightarrow$ `totalTokens`
       and populate `AgentRunMetrics` so stage execution records and telemetry track Antigravity ACP token consumption accurately.

---

## 3. Detailed Architectural Specifications

### 3.1 Removal of `PiRpc` Harness
1. Remove `StandardHarnessIds.PiRpc` from [`StandardHarnessIds.cs`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Domain/StandardHarnessIds.cs).
2. Delete `src/NightCodeForge.Agents/PiRpc/PiRpcAgentHarness.cs` and `tests/NightCodeForge.Agents.Tests/PiRpc/`.
3. Remove `PiRpcHarnessOptions` and `PiRpcAgentHarness` registrations from [`ServiceCollectionExtensions.cs`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Agents/ServiceCollectionExtensions.cs).
4. Normalize any legacy `HarnessId == "pirpc"` profile rows in SQLite to `StandardHarnessIds.Crucible`.

---

### 3.2 ACP Harness Catalog & Antigravity Discovery (`IAcpHarnessCatalogService`)

To avoid configuring executable paths, arguments, and container mounts repeatedly across profiles, introduce a centralized **ACP Harness Catalog**:

1. **Core Contracts (`NightCodeForge.Core.Interfaces` / `NightCodeForge.Core.Domain`):**
   ```csharp
   public static class KnownAcpCatalogIds
   {
       public const string Antigravity = "antigravity";
       public const string Custom = "custom";
   }

   public record AcpModelOption(
       string ModelId,
       string DisplayName,
       IReadOnlyList<string> SupportedThoughtLevels);

   public record AcpCatalogProviderStatus(
       string CatalogId,
       string DisplayName,
       bool IsInstalled,
       bool IsAuthenticated,
       string? DetectedExecutablePath,
       string? ExecutionTargetDescription, // e.g., "WSL2 (Ubuntu) + Container Bind-Mount" or "Host OS"
       string? ServerVersion,
       string? AccountTierName,            // e.g., "Google AI Ultra"
       string? DiagnosticMessage,
       IReadOnlyList<AcpModelOption> AvailableModels);

   public interface IAcpHarnessCatalogService
   {
       Task<IReadOnlyList<AcpCatalogProviderStatus>> GetCatalogStatusAsync(
           CancellationToken cancellationToken = default);

       Task<AcpCatalogProviderStatus> ProbeProviderAsync(
           string catalogId,
           bool forceRefresh = false,
           CancellationToken cancellationToken = default);
   }
   ```

2. **Antigravity (`agy acp`) Auto-Discovery & Probe Implementation:**
   - **Binary Discovery Order:**
     1. When running on Windows with WSL2 available (the standard NightCodeForge setup for `WslDirect` and `ContainerSandbox`), check inside the configured WSL2 distribution for `~/.local/bin/agy` or `which agy`.
     2. Also check the host OS (`~/.gemini/bin/agy.exe` on Windows, `~/.local/bin/agy` on Linux/macOS, or `agy` on `PATH`).
   - **Fast Authentication & Metadata Probe:**
     - Check if `~/.gemini/antigravity-cli/antigravity-oauth-token` exists (or `GEMINI_API_KEY` is configured).
     - Spawn `agy acp --dangerously-skip-permissions` (in WSL2 or Host), send `initialize` + `session/new` (with a 10-second timeout), and parse:
       - `agentInfo.version` (e.g. `"1.5.3"`),
       - `_meta.antigravity.tier.name` (e.g. `"Google AI Ultra"`),
       - `configOptions` (`model` options + `thought_level` options) and `models.availableModels`.
     - Cache the discovered `AcpCatalogProviderStatus` in memory (with a manual **"🔄 Refresh ACP Status & Models"** button and **"🔑 Login Hints / Test Connection"** modal in the UI).
     - If `agy` is installed but unauthenticated, return `IsInstalled = true, IsAuthenticated = false` with an actionable command hint (`wsl -e ~/.local/bin/agy login` or `agy login`).

---

### 3.3 Multi-Environment Execution Dispatch, Conversation Persistence & Constructor Limits for `AcpAgentHarness`

1. **Container Bind-Mounts for Antigravity ACP (`NightCodeForgeContainerExecutor` / `ContainerEnvironmentFactory`):**
   - **Existence Guard Before Mounting (Critical Docker Caveat):** Docker automatically creates an empty directory on the host if a `-v hostPath:containerPath` source path does not exist. Therefore, `ContainerEnvironmentFactory` MUST verify that the `agy` binary file (`~/.local/bin/agy` in WSL2/Linux) and the `~/.gemini` directory actually exist on the target host/WSL filesystem before adding their `ContainerVolumeMount` entries.
   - When verified present on the WSL2/Linux host:
     - Bind-mount `~/.local/bin/agy` to `/opt/ncf-acp/agy:ro` and include `/opt/ncf-acp` in `ContainerExecutorOptions.ExtraPathDirectories` (with `ExpandTildeInMounts = true` so `ResolveHostPath` and `DetectContainerDrift` expand `~/` consistently).
     - Bind-mount `~/.gemini` to `/ncf-home/.gemini` (read-write for `SandboxSecurityTier.Standard` so OAuth token refreshes and `agy` session state persist; omitted or read-only for `RestrictedReadOnly`).
2. **Environment-Aware Process Launch & Collaborator Extraction (`IAcpProcessLauncher`):**
   - **Constructor Parameter Limit ([AGENTS.md](../../AGENTS.md)):** `AcpAgentHarness` currently has 8 constructor parameters. To stay strictly below the 10-parameter limit without optional parameters, encapsulate multi-environment ACP process launch resolution (`BareHost`, `WslDirect`, `ContainerSandbox`) into a dedicated collaborator service (e.g., `IAcpProcessLauncher`) or extend `IHarnessActivityRecorder`.
   - When `context.Workspace.ExecutionMode == ExecutionMode.ContainerSandbox`:
     - Ensure the task container is running via `IExecutionEnvironmentProvider.GetEnvironment(workspace, profile.SecurityTier, task.Id)` (calling `EnsureContainerRunning()`).
     - Resolve the container working directory via `NightCodeForgeContainerExecutor.ResolveContainerWorkingDirectory(workspacePath, containerOptions)` (which maps task worktrees to `/workspace/task-{id}`).
     - Launch `agy acp --dangerously-skip-permissions` **inside the task container** via `docker exec -i -u <uid>:<gid> -e HOME=/ncf-home -e PATH=<computedPath> -w <containerTaskWorkDir> <containerName> /opt/ncf-acp/agy acp --dangerously-skip-permissions` (routed through `wsl.exe` when `UseWsl` is true).
     - Pass `cwd = <containerTaskWorkDir>` (e.g. `"/workspace/task-42"`) in `session/new` and in the `<environment>` prompt block.
   - When `context.Workspace.ExecutionMode == ExecutionMode.WslDirect`:
     - Launch `agy acp --dangerously-skip-permissions` inside WSL2 (`wsl.exe -d <distro> -- ~/.local/bin/agy acp --dangerously-skip-permissions`) with `cwd = workspacePath` (the WSL2 ext4 worktree path, e.g., `~/.nightcodeforge/wks/{wsId}/task-{id}`).
   - When `context.Workspace.ExecutionMode == ExecutionMode.BareHost`:
     - Launch the host `agy` binary (`~/.gemini/bin/agy.exe` on Windows or `~/.local/bin/agy` on Linux) with `cwd = localWorkspaceRoot`.
3. **Database `Conversation` & `ConversationTurn` Persistence (`IHarnessActivityRecorder`):**
   - Currently, `HarnessActivityRecorder` only broadcasts SignalR events and writes a `.md` transcript file without persisting `Conversation` or `ConversationTurn` rows in `ITaskRepository`. As a result, the Task Execution Inspector shows no conversation cards for ACP runs and `TaskOrchestrator` cannot inspect stage conversation history.
   - Update `IHarnessActivityRecorder` / `HarnessActivityRecorder` (injecting `ITaskRepository`) so that ACP stage executions:
     - Start or resume an `Agentic` `Conversation` in SQLite at stage start (`StartConversationAsync` with `context.Profile.Id`, `StandardHarnessIds.Acp`, `context.Profile.ModelName`, and computed `attemptIndex`).
     - Persist `StageTurnRole.User` (bootstrap prompt), `StageTurnRole.Assistant` (flushed assistant text and accumulated `thinkingTrace` from `agent_thought_chunk`), `StageTurnRole.Tool` (`tool_call`), and `StageTurnRole.ToolResult` (`tool_call_update`).
     - Complete the `Conversation` (`CompleteConversationAsync`) with the saved `transcriptFileName`, final `StageExecutionStatus` (`Completed`, `WaitingForHuman`, `Cancelled`, or `Failed`), and token usage delta (`InputTokens`, `OutputTokens`, `CachedInputTokens` from `result.usage`, keeping `Cost = null` for subscription-backed Antigravity ACP sessions so Step 12.33B displays token counts without a synthetic `$0.00` badge).
4. **Read-Only Enforcement, Stage Routing & Post-Run Validation:**
   - When `profile.Mode == AgentMode.ReadOnly` (e.g., `triage`, `ideation`, or plan-only stages):
     - Set `agy acp` session config option `mode = "plan"`.
     - Omit `--dangerously-skip-permissions` (or reject `session/request_permission` for write/command tools) and reject `fs/write_text_file` in `AcpHostRpcHandler`.
   - For `code-review-and-fix` (`AgentMode.Review`) and `task-review`:
     - Ensure the enriched prompt includes instructions compatible with `StageSummaryExtractor.Extract(lastTurnConcludingText)` so that if an ACP reviewer requests changes (or needs human clarification without triggering `session/request_permission`), `StageSummaryExtractor` extracts `TargetRouting` and `ReviewerFeedback` accurately.

---

### 3.4 Web UI: ACP Catalog Strip & Harness/Model Selector in `AgentProfilesTab.razor`

1. **Provider Status Strip Enhancement ([`AgentProfilesTab.razor`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Web/Components/Settings/AgentProfilesTab.razor)):**
   - Alongside the Local LLM / OllamaRouter connection status badge, display the **Antigravity ACP Status Badge**:
     - Ready state: `🟢 Antigravity ACP v1.5.3 Ready (WSL2 + Container | Google AI Ultra | 5 model families)`
     - Unauthenticated state: `🟡 Antigravity ACP Installed — Authentication Required (Run 'wsl -e agy login')`
     - Not installed state: `⚪ Antigravity ACP Not Detected`
2. **Profile Harness & Model Selection & Persistence Fix:**
   - Replace the static `Harness: @profile.HarnessId` badge on profile cards (and in the Step 12.5B Role-Based Difficulty Matrix) with a **Harness Selector** dropdown:
     - `Crucible (Local / Open-Weight & API)` (`crucible`)
     - `Antigravity ACP (Google AI / Gemini CLI)` (`acp`)
   - When `HarnessId == StandardHarnessIds.Acp`:
     - Populate the **Inference Model** dropdown from `IAcpHarnessCatalogService`'s discovered models (`Gemini 4 Argon`, `Gemini 3.8 Flash`, `Claude Opus 5.5`, `Claude Sonnet 5.5`, `GPT-OSS 120B`) with a custom model input toggle.
     - Map the profile's **Thinking Effort** selector (`Low`, `Medium`, `High`) directly to `agy acp`'s `thought_level` (`low`, `medium`, `high`).
     - Hide Crucible-only settings (`OllamaRouter Target`, `LocalOptimizationFlags`).
   - **Persistence Fix in `AgentProfilesTab.SaveProfilesAsync` & `CreateDedicatedProfilesAsync`:**
     - Currently, `SaveProfilesAsync` in `AgentProfilesTab.razor` updates `existing.ModelName`, `existing.RoutingTarget`, etc., but **omits** `existing.HarnessId = p.HarnessId` and `existing.HarnessConfigurationJson = p.HarnessConfigurationJson`! Update `SaveProfilesAsync` and `CreateDedicatedProfilesAsync` so `HarnessId` and `HarnessConfigurationJson` are persisted when saving or cloning profiles.

---

## 4. Implementation Checklist & Deliverables

- [ ] **1. Remove `PiRpc` Harness (Clean Break):**
  - [ ] Remove `StandardHarnessIds.PiRpc`, `PiRpcAgentHarness`, `PiRpcHarnessOptions`, DI registrations, and `PiRpc` tests.
  - [ ] Normalize any stored `HarnessId == "pirpc"` profile rows to `StandardHarnessIds.Crucible`.
- [ ] **2. ACP Harness Catalog & Antigravity Discovery (`IAcpHarnessCatalogService`):**
  - [ ] Implement `AcpHarnessCatalogService` discovering `agy` across WSL2 (`~/.local/bin/agy`) and Host (`~/.gemini/bin/agy.exe`), checking `~/.gemini/antigravity-cli/antigravity-oauth-token`, and probing `initialize` + `session/new` for server version, account tier (`_meta.antigravity.tier`), and available models (`configOptions` & `models.availableModels`).
- [ ] **3. `AcpAgentHarness` Protocol & Container Execution Rework:**
  - [ ] Update `NegotiateProtocolAsync` to send `"protocolVersion": 1` with `"terminal": true` (boolean) and parse `agentInfo`, `authMethods`, and `agentCapabilities`.
  - [ ] Update `session/new` to inject `_meta.antigravity.systemPrompt.append`, read the `session/new` response (skipping interleaved `session/update` notifications), and capture the server-assigned `result.sessionId`.
  - [ ] Apply `context.Profile.ModelName`, `ThinkingEffort`, and `AgentMode` via `session/set_model` and `session/set_config_option` (`model`, `thought_level`, `mode`).
  - [ ] Update `session/prompt` to send `{ "type": "text", "text": promptText }`, parse `agy acp` `tool_call` / `tool_call_update` (`name` & `args`), and extract `result.usage` token metrics into `AgentRunMetrics`.
  - [ ] Guard file/directory existence on the host/WSL2 before mounting `~/.local/bin/agy` (`/opt/ncf-acp/agy:ro`, with `/opt/ncf-acp` in `ExtraPathDirectories`) and `~/.gemini` (`/ncf-home/.gemini`) into task containers; launch `agy acp` inside the container at `/workspace/task-{id}` (`ResolveContainerWorkingDirectory`) when `ExecutionMode == ContainerSandbox` (and via `wsl.exe` when `ExecutionMode == WslDirect`), keeping constructor parameter counts $< 10$ via collaborator extraction (`IAcpProcessLauncher`).
  - [ ] Persist `Conversation` and `ConversationTurn` records in `ITaskRepository` via `HarnessActivityRecorder` during ACP sessions so the Task Execution Inspector displays ACP stage conversations and turns.
- [ ] **4. Web UI (`AgentProfilesTab.razor`):**
  - [ ] Display the Antigravity ACP status badge (version, tier, auth status) in the provider strip.
  - [ ] Add the Harness selector (`Crucible` vs `Antigravity ACP`), bind the ACP model dropdown & thinking level selector on profile cards, and ensure `SaveProfilesAsync` / `CreateDedicatedProfilesAsync` persist `HarnessId` and `HarnessConfigurationJson`.
- [ ] **5. Automated Testing:**
  - [ ] Unit tests in `AcpAgentHarnessTests` covering the verified `agy acp` JSON-RPC sequence (`initialize` $\rightarrow$ `session/new` with interleaved `available_commands_update` $\rightarrow$ `session/set_config_option` $\rightarrow$ `session/prompt` with `tool_call` and `result.usage`).
  - [ ] Unit tests for `AcpHarnessCatalogService` model/tier parsing and unauthenticated diagnostics.
  - [ ] Run test suite per AGENTS.md: `dotnet test --filter "Category!=Acceptance&Category!=E2E"`.

