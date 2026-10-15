# 📚 NightCodeForge - Source Code Reference Guide

This document indexes key reference files from the local source checkouts of **Microsoft Agent Framework**, **ComicGen CLI**, **Pi Coding Agent**, **Agent Client Protocol (ACP)**, and **OllamaRouter**. Use these references to guide the implementation of NightCodeForge's harnesses, streaming reasoning telemetry, full-fidelity transcripts, sandboxed execution environments, context compaction, JSON-RPC daemon bridges, and GPU/LLM gateway integrations.

---

## 1. Microsoft Agent Framework (`dotnet`)
* **Local Root:** `C:/Users/Julien/source/repos/agent-framework-main/dotnet/src`
* **NuGet Policy:** NightCodeForge references official NuGet packages (`Microsoft.Agents.AI.*`, `Microsoft.Extensions.AI.*`). Use these source files as behavioral and architectural references.

### 1.1 Harness & Agent Orchestration (`Microsoft.Agents.AI.Harness`)
Implements the high-level orchestration pipeline, loop execution, tool approval, and telemetry wrapping.

| Component | Source File | Key Architectural Patterns to Replicate / Reference |
| :--- | :--- | :--- |
| **`HarnessAgent`** | [`HarnessAgent.cs`](file:///C:/Users/Julien/source/repos/agent-framework-main/dotnet/src/Microsoft.Agents.AI.Harness/HarnessAgent.cs) | High-level agent wrapping `LoopAgent`, `ToolApprovalAgent`, and `OpenTelemetryAgent`. Serves as the foundation for NightCodeForge's native `CrucibleAgent`. |
| **`HarnessAgentOptions`** | [`HarnessAgentOptions.cs`](file:///C:/Users/Julien/source/repos/agent-framework-main/dotnet/src/Microsoft.Agents.AI.Harness/HarnessAgentOptions.cs) | Configuration options for the harness pipeline, approval policies, max loop iterations, and telemetry sinks. |
| **`ChatClientHarnessExtensions`** | [`ChatClientHarnessExtensions.cs`](file:///C:/Users/Julien/source/repos/agent-framework-main/dotnet/src/Microsoft.Agents.AI.Harness/ChatClientHarnessExtensions.cs) | Extension methods for chaining harness middleware onto `IChatClient` builders. |

### 1.2 Core Abstractions & Checkpointing (`Microsoft.Agents.AI.Abstractions`)
Provides the contracts for agent lifecycle, session state serialization, and context injection.

| Component | Source File | Key Architectural Patterns to Replicate / Reference |
| :--- | :--- | :--- |
| **`AIAgent`** | [`AIAgent.cs`](file:///C:/Users/Julien/source/repos/agent-framework-main/dotnet/src/Microsoft.Agents.AI.Abstractions/AIAgent.cs) | Base class defining `RunAsync`, `RunStreamingAsync`, and session management contracts. |
| **`AgentSession`** | [`AgentSession.cs`](file:///C:/Users/Julien/source/repos/agent-framework-main/dotnet/src/Microsoft.Agents.AI.Abstractions/AgentSession.cs) | Encapsulates conversational state, message history, and state bags across agent turns. |
| **`AgentRunContext`** | [`AgentRunContext.cs`](file:///C:/Users/Julien/source/repos/agent-framework-main/dotnet/src/Microsoft.Agents.AI.Abstractions/AgentRunContext.cs) | Per-run execution context carrying cancellation tokens, tool approval delegates, and telemetry spans. |
| **`AgentSessionStateBag`** | [`AgentSessionStateBag.cs`](file:///C:/Users/Julien/source/repos/agent-framework-main/dotnet/src/Microsoft.Agents.AI.Abstractions/AgentSessionStateBag.cs) | Thread-safe key-value state bag serialized with JSON polymorphism for non-blocking worker pauses. |
| **`AgentSessionStore`** | [`AgentSessionStore.cs`](file:///C:/Users/Julien/source/repos/agent-framework-main/dotnet/src/Microsoft.Agents.AI.Abstractions/AgentSessionStore.cs) | Session persistence abstraction. NightCodeForge implements this over EF Core `AppDbContext`. |
| **`AIContextProvider`** | [`AIContextProvider.cs`](file:///C:/Users/Julien/source/repos/agent-framework-main/dotnet/src/Microsoft.Agents.AI.Abstractions/AIContextProvider.cs) | Pluggable context provider invoked before each turn (used for injecting dynamic stage personas, skills, and memory). |
| **`InMemoryChatHistoryProvider`** | [`InMemoryChatHistoryProvider.cs`](file:///C:/Users/Julien/source/repos/agent-framework-main/dotnet/src/Microsoft.Agents.AI.Abstractions/InMemoryChatHistoryProvider.cs) | Default in-memory chat history provider showing how message collections are isolated per session. |

### 1.3 Chat Client Middleware & Resiliency (`Microsoft.Agents.AI/ChatClient`)
Specialized `DelegatingChatClient` implementations that guarantee zero data loss and enable seamless human-in-the-loop resumption.

| Component | Source File | Key Architectural Patterns to Replicate / Reference |
| :--- | :--- | :--- |
| **`PerServiceCallChatHistoryPersistingChatClient`** | [`PerServiceCallChatHistoryPersistingChatClient.cs`](file:///C:/Users/Julien/source/repos/agent-framework-main/dotnet/src/Microsoft.Agents.AI/ChatClient/PerServiceCallChatHistoryPersistingChatClient.cs) | **Critical for zero data loss.** Persists chat history to storage after *every individual LLM service call* and tool call, preventing loss if the host crashes mid-turn. |
| **`MessageInjectingChatClient`** | [`MessageInjectingChatClient.cs`](file:///C:/Users/Julien/source/repos/agent-framework-main/dotnet/src/Microsoft.Agents.AI/ChatClient/MessageInjectingChatClient.cs) | Injects synthetic messages, developer answers from `ask`, or deterministic test feedback into the chat stream on resumption. |
| **`ApprovalResponseBindingChatClient`** | [`ApprovalResponseBindingChatClient.cs`](file:///C:/Users/Julien/source/repos/agent-framework-main/dotnet/src/Microsoft.Agents.AI/ChatClient/ApprovalResponseBindingChatClient.cs) | Binds user approval/rejection responses back to pending function calls. |
| **`ApprovalNotRequiredFunctionBypassingChatClient`** | [`ApprovalNotRequiredFunctionBypassingChatClient.cs`](file:///C:/Users/Julien/source/repos/agent-framework-main/dotnet/src/Microsoft.Agents.AI/ChatClient/ApprovalNotRequiredFunctionBypassingChatClient.cs) | Bypasses approval gates for pre-approved autonomous tools (`read`, read-only inspection commands). |

### 1.4 Context Window Compaction (`Microsoft.Agents.AI/Compaction`)
Manages bounded GPU VRAM context by pruning older tool outputs and summarizing conversation history.

| Component | Source File | Key Architectural Patterns to Replicate / Reference |
| :--- | :--- | :--- |
| **`ToolResultCompactionStrategy`** | [`ToolResultCompactionStrategy.cs`](file:///C:/Users/Julien/source/repos/agent-framework-main/dotnet/src/Microsoft.Agents.AI/Compaction/ToolResultCompactionStrategy.cs) | Collapses completed tool call/result pairs into brief summary stubs outside a protected window (`minimumPreservedGroups`). |
| **`ContextWindowCompactionStrategy`** | [`ContextWindowCompactionStrategy.cs`](file:///C:/Users/Julien/source/repos/agent-framework-main/dotnet/src/Microsoft.Agents.AI/Compaction/ContextWindowCompactionStrategy.cs) | Monitors token thresholds and triggers compaction before context limits are breached. |
| **`PipelineCompactionStrategy`** | [`PipelineCompactionStrategy.cs`](file:///C:/Users/Julien/source/repos/agent-framework-main/dotnet/src/Microsoft.Agents.AI/Compaction/PipelineCompactionStrategy.cs) | Chains multiple compaction strategies (e.g. `ToolResultCompactionStrategy` $\rightarrow$ `SlidingWindowCompactionStrategy`). |
| **`SlidingWindowCompactionStrategy`** | [`SlidingWindowCompactionStrategy.cs`](file:///C:/Users/Julien/source/repos/agent-framework-main/dotnet/src/Microsoft.Agents.AI/Compaction/SlidingWindowCompactionStrategy.cs) | Maintains a rolling window of recent turns while pruning older exchanges. |
| **`CompactionTriggers`** | [`CompactionTriggers.cs`](file:///C:/Users/Julien/source/repos/agent-framework-main/dotnet/src/Microsoft.Agents.AI/Compaction/CompactionTriggers.cs) | Factory methods for token threshold triggers, turn count triggers, and message size triggers. |

### 1.5 Shell Execution & Sandboxing Patterns (`Microsoft.Agents.AI.Tools.Shell`)
Implements command execution in containers and local processes.

| Component | Source File | Key Architectural Patterns to Replicate / Reference |
| :--- | :--- | :--- |
| **`DockerShellExecutor`** | [`DockerShellExecutor.cs`](file:///C:/Users/Julien/source/repos/agent-framework-main/dotnet/src/Microsoft.Agents.AI.Tools.Shell/DockerShellExecutor.cs) | **Sentinel REPL Pattern:** Spawns `docker exec -i bash --norc` and evaluates completion using cryptographic sentinels (`echo "__SENTINEL__ $?"`). NightCodeForge adopts this pattern for `NightCodeForgeContainerExecutor` while lifting restrictive `--network none` and `--read-only` flags. |
| **`HeadTailBuffer`** | [`HeadTailBuffer.cs`](file:///C:/Users/Julien/source/repos/agent-framework-main/dotnet/src/Microsoft.Agents.AI.Tools.Shell/HeadTailBuffer.cs) | **Rune-Safe Compaction:** Streams stdout/stderr through a bounded byte buffer, preserving head and tail without splitting multi-byte UTF-8 code points. |
| **`LocalShellExecutor`** | [`LocalShellExecutor.cs`](file:///C:/Users/Julien/source/repos/agent-framework-main/dotnet/src/Microsoft.Agents.AI.Tools.Shell/LocalShellExecutor.cs) | Spawns local host terminal sessions (PowerShell, Bash) with process isolation and timeout governance. |
| **`EnvironmentSanitizer`** | [`EnvironmentSanitizer.cs`](file:///C:/Users/Julien/source/repos/agent-framework-main/dotnet/src/Microsoft.Agents.AI.Tools.Shell/EnvironmentSanitizer.cs) | Filters sensitive host environment variables before passing them into the execution sandbox. |
| **`ShellResult`** | [`ShellResult.cs`](file:///C:/Users/Julien/source/repos/agent-framework-main/dotnet/src/Microsoft.Agents.AI.Tools.Shell/ShellResult.cs) | Standardized result model capturing exit code, stdout, stderr, execution duration, and truncation flags. |

### 1.6 Modular Skills System (`Microsoft.Agents.AI/Skills`)
Implements the [agentskills.io](https://agentskills.io/) progressive disclosure specification.

| Component | Source File | Key Architectural Patterns to Replicate / Reference |
| :--- | :--- | :--- |
| **`AgentSkillsProvider`** | [`AgentSkillsProvider.cs`](file:///C:/Users/Julien/source/repos/agent-framework-main/dotnet/src/Microsoft.Agents.AI/Skills/AgentSkillsProvider.cs) | Injects available skill names and descriptions into the agent's prompt (~25 tokens/skill) and registers `load_skill` / `run_skill_script`. |
| **`AggregatingAgentSkillsSource`** | [`AggregatingAgentSkillsSource.cs`](file:///C:/Users/Julien/source/repos/agent-framework-main/dotnet/src/Microsoft.Agents.AI/Skills/AggregatingAgentSkillsSource.cs) | Merges multiple skill directories (e.g. global `~/.nightcodeforge/skills/` and repo `.agents/skills/`). |
| **`FileAgentSkillsSource`** | [`FileAgentSkillsSource.cs`](file:///C:/Users/Julien/source/repos/agent-framework-main/dotnet/src/Microsoft.Agents.AI/Skills/FileAgentSkillsSource.cs) | Loads skills from filesystem directories containing `SKILL.md` with YAML frontmatter. |
| **`AgentSkillResource`** | [`AgentSkillResource.cs`](file:///C:/Users/Julien/source/repos/agent-framework-main/dotnet/src/Microsoft.Agents.AI/Skills/AgentSkillResource.cs) | Represents supplementary reference assets or templates inside a skill folder. |
| **`AgentSkillScript`** | [`AgentSkillScript.cs`](file:///C:/Users/Julien/source/repos/agent-framework-main/dotnet/src/Microsoft.Agents.AI/Skills/AgentSkillScript.cs) | Represents executable automation scripts (`scripts/`) associated with a skill. |

### 1.7 Model Context Protocol (`Microsoft.Agents.AI.Mcp`)
Connects external MCP servers and tools into the agent session.

| Component | Source File | Key Architectural Patterns to Replicate / Reference |
| :--- | :--- | :--- |
| **`TaskAwareMcpClientAIFunction`** | [`TaskAwareMcpClientAIFunction.cs`](file:///C:/Users/Julien/source/repos/agent-framework-main/dotnet/src/Microsoft.Agents.AI.Mcp/TaskAwareMcpClientAIFunction.cs) | Wraps an MCP tool as an `AIFunction` compatible with `IChatClient` and `CrucibleAgent`. |
| **`AgentMcpSkillsSource`** | [`AgentMcpSkillsSource.cs`](file:///C:/Users/Julien/source/repos/agent-framework-main/dotnet/src/Microsoft.Agents.AI.Mcp/Skills/AgentMcpSkillsSource.cs) | Dynamically imports skills exposed by external MCP servers. |

---

## 2. Pi Coding Agent (`coding-agent`)
* **Local Root:** `C:/Users/Julien/source/repos/pi-main/packages/coding-agent`
* **Architecture:** Node.js/Bun CLI and headless RPC daemon (`@earendil-works/coding-agent`).

### 2.1 Minimalist Canonical Tools (`src/core/tools/`)
Contains the reference implementations for the minimalist toolset that Crucible reuses.

| Tool | Source File | Implementation Details & Behavioral Contracts |
| :--- | :--- | :--- |
| **`read`** | [`read.ts`](file:///C:/Users/Julien/source/repos/pi-main/packages/coding-agent/src/core/tools/read.ts) | Reads file content with 1-indexed `offset` and `limit` line slicing. Detects binary files and applies line window truncation. Guideline: *"Use read to examine files instead of cat or sed."* |
| **`edit`** | [`edit.ts`](file:///C:/Users/Julien/source/repos/pi-main/packages/coding-agent/src/core/tools/edit.ts) | Precision search-and-replace chunk editing. Supports multi-disjoint edits via `edits: [{ oldText, newText }]` applied in sequence against original file content. Normalizes fuzzy indentation and line endings. |
| **`edit-diff`** | [`edit-diff.ts`](file:///C:/Users/Julien/source/repos/pi-main/packages/coding-agent/src/core/tools/edit-diff.ts) | Computes unified diffs of edits before applying mutations to disk. |
| **`write`** | [`write.ts`](file:///C:/Users/Julien/source/repos/pi-main/packages/coding-agent/src/core/tools/write.ts) | Brand-new file creation or full overwrite. Integrates with file mutation queue to prevent concurrent write corruption. |
| **`bash`** | [`bash.ts`](file:///C:/Users/Julien/source/repos/pi-main/packages/coding-agent/src/core/tools/bash.ts) | Executes commands with timeout handling, working directory resolution, and bounded output accumulation. Replaces standalone `grep` / `ls` / `find` tools. |
| **`file-mutation-queue`** | [`file-mutation-queue.ts`](file:///C:/Users/Julien/source/repos/pi-main/packages/coding-agent/src/core/tools/file-mutation-queue.ts) | Serializes file modifications across concurrent tool invocations. |
| **`output-accumulator`** | [`output-accumulator.ts`](file:///C:/Users/Julien/source/repos/pi-main/packages/coding-agent/src/core/tools/output-accumulator.ts) | Bounded buffer implementation in TypeScript matching the behavior of .NET's `HeadTailBuffer`. |
| **`truncate`** | [`truncate.ts`](file:///C:/Users/Julien/source/repos/pi-main/packages/coding-agent/src/core/tools/truncate.ts) | String truncation and byte formatting utilities. |

### 2.2 Headless JSONL RPC Protocol (`src/modes/rpc/`)
Defines the headless daemon protocol used by NightCodeForge's `PiRpcAgentHarness`.

| Component | Source File | Protocol Details & Message Schemas |
| :--- | :--- | :--- |
| **`rpc-mode.ts`** | [`rpc-mode.ts`](file:///C:/Users/Julien/source/repos/pi-main/packages/coding-agent/src/core/../src/modes/rpc/rpc-mode.ts) | Main daemon process loop. Reads JSON Lines from `stdin`, runs agent turns, and streams events (`message_update`, `tool_execution`, `agent_settled`) to `stdout`. |
| **`rpc-types.ts`** | [`rpc-types.ts`](file:///C:/Users/Julien/source/repos/pi-main/packages/coding-agent/src/core/../src/modes/rpc/rpc-types.ts) | TypeScript interfaces for all RPC commands, responses, and events. Includes `ExtensionUiRequest` schema (`input`, `select`, `confirm`) used for human-in-the-loop interruption. |
| **`rpc-client.ts`** | [`rpc-client.ts`](file:///C:/Users/Julien/source/repos/pi-main/packages/coding-agent/src/core/../src/modes/rpc/rpc-client.ts) | Reference client demonstrating how to launch the daemon, send `prompt` requests, and parse JSONL streams. |
| **`jsonl.ts`** | [`jsonl.ts`](file:///C:/Users/Julien/source/repos/pi-main/packages/coding-agent/src/core/../src/modes/rpc/jsonl.ts) | Safe JSONL serialization, newline buffering, and chunk reconstruction. |

### 2.3 Prompt Engineering & System Directives (`src/core/`)
Reference prompts and session management for local and frontier coding models.

| Component | Source File | Prompt Structure & Behavioral Logic |
| :--- | :--- | :--- |
| **`system-prompt.ts`** | [`system-prompt.ts`](file:///C:/Users/Julien/source/repos/pi-main/packages/coding-agent/src/core/system-prompt.ts) | **Reference System Prompt:** Pi's concise system prompt defining tool guidelines, minimal conversational chatter, and code modification rules. Crucible's stage personas adapt these rules. |
| **`prompt-templates.ts`** | [`prompt-templates.ts`](file:///C:/Users/Julien/source/repos/pi-main/packages/coding-agent/src/core/prompt-templates.ts) | Dynamic templates for injecting repository context, git status, and environment information. |
| **`session-manager.ts`** | [`session-manager.ts`](file:///C:/Users/Julien/source/repos/pi-main/packages/coding-agent/src/core/session-manager.ts) | Manages conversational branches, checkpoint saves to disk, and resumption snapshots. |
| **`skills.ts`** | [`skills.ts`](file:///C:/Users/Julien/source/repos/pi-main/packages/coding-agent/src/core/skills.ts) | Pi's skill discovery, parsing `SKILL.md` files, and prompt injection. |
| **`compaction/compaction.ts`** | [`compaction.ts`](file:///C:/Users/Julien/source/repos/pi-main/packages/coding-agent/src/core/compaction/compaction.ts) | Context window compaction in Pi: branch summarization and token trimming. |
| **`output-guard.ts`** | [`output-guard.ts`](file:///C:/Users/Julien/source/repos/pi-main/packages/coding-agent/src/core/output-guard.ts) | Prevents runaway output from saturating the terminal or RPC stdout pipe. |

---

## 3. Agent Client Protocol (ACP) Reference Source

ACP serves as NightCodeForge's open-standard extensible harness, connecting external coding agents (Zed AI, JetBrains, Gemini CLI, Claude ACP) into NightCodeForge's orchestration pipeline.

### 3.1 Official ACP Specification & Schemas (`agent-client-protocol-main`)
* **Local Root:** `C:/Users/Julien/source/repos/agent-client-protocol-main`
* **Dedicated Integration Guide:** See **[ACP.md](ACP.md)** for protocol differences, sequence diagrams, and modernization strategy.

| Specification Area | Source File | Protocol Details & Contract Definitions |
| :--- | :--- | :--- |
| **v2 Migration Guide** | [`migration.mdx`](file:///C:/Users/Julien/source/repos/agent-client-protocol-main/docs/protocol/v2/migration.mdx) | **Crucial reference for modernizing C# SDK:** Documents all breaking changes, method renames (`auth/login`, `session/resume`), replacement of `fs/*` & client `terminal/*` with MCP, and update upsert semantics. |
| **v2 Protocol Overview** | [`overview.mdx`](file:///C:/Users/Julien/source/repos/agent-client-protocol-main/docs/protocol/v2/overview.mdx) | High-level architecture, JSON-RPC framing, transport abstractions, and capability negotiation. |
| **Initialization & Capabilities** | [`initialization.mdx`](file:///C:/Users/Julien/source/repos/agent-client-protocol-main/docs/protocol/v2/initialization.mdx) | Handshake contract: version negotiation (`protocolVersion: 1` vs `2`), client capabilities (`fs`, `terminal`), and agent capabilities. |
| **Prompt Lifecycle** | [`prompt-lifecycle.mdx`](file:///C:/Users/Julien/source/repos/agent-client-protocol-main/docs/protocol/v2/prompt-lifecycle.mdx) | Prompt request payload structure, streaming update notifications, and stop reason taxonomy. |
| **Permission Elicitation** | [`elicitation.mdx`](file:///C:/Users/Julien/source/repos/agent-client-protocol-main/docs/protocol/v2/elicitation.mdx) | Human-in-the-loop permission flow (`session/request_permission`) mapped to NightCodeForge's `WaitingForHuman` queue state. |
| **Tool Call Updates** | [`tool-calls.mdx`](file:///C:/Users/Julien/source/repos/agent-client-protocol-main/docs/protocol/v2/tool-calls.mdx) | Upsert patch semantics for tool calls (`toolCallId`, status updates, and content chunks). |
| **MCP over ACP** | [`mcp-over-acp.mdx`](file:///C:/Users/Julien/source/repos/agent-client-protocol-main/docs/rfds/mcp-over-acp.mdx) | RFD defining how ACP clients provide MCP server configurations (`mcpServers`) to agents for tool execution. |
| **v1 Canonical Schema** | [`schema.json (v1)`](file:///C:/Users/Julien/source/repos/agent-client-protocol-main/schema/v1/schema.json) | Complete JSON Schema definition for ACP v1 baseline. |
| **v2 Canonical Schema** | [`schema.json (v2)`](file:///C:/Users/Julien/source/repos/agent-client-protocol-main/schema/v2/schema.json) | Complete JSON Schema definition for ACP v2 standard. |

### 3.2 C# SDK Foundation (`acp-csharp-main`)
* **Local Root:** `C:/Users/Julien/source/repos/acp-csharp-main`
* **Status:** Cleanly compiling on .NET 9 and 10; fork base for `NightCodeForge.Agents/Acp/`.

| Component | Source File | Implementation Role & Modernization Notes |
| :--- | :--- | :--- |
| **Project Definition** | [`AgentClientProtocol.csproj`](file:///C:/Users/Julien/source/repos/acp-csharp-main/src/AgentClientProtocol/AgentClientProtocol.csproj) | Targets `net10.0;net9.0;net8.0;netstandard2.1` with C# 13, nullable reference types, and AOT compatibility. |
| **Client Connection Engine** | [`ClientSideConnection.cs`](file:///C:/Users/Julien/source/repos/acp-csharp-main/src/AgentClientProtocol/ClientSideConnection.cs) | Wraps `IAcpAgent` and registers request handlers for `IAcpClient`. Reads/writes JSON-RPC lines over stdio streams (`TextReader`/`TextWriter`). |
| **Client Interface** | [`IAcpClient.cs`](file:///C:/Users/Julien/source/repos/acp-csharp-main/src/AgentClientProtocol/IAcpClient.cs) | Core client callback contracts implemented by NightCodeForge (`RequestPermissionAsync`, `WriteTextFileAsync`, `ReadTextFileAsync`, `CreateTerminalAsync`, `SessionNotificationAsync`). |
| **Agent Interface** | [`IAcpAgent.cs`](file:///C:/Users/Julien/source/repos/acp-csharp-main/src/AgentClientProtocol/IAcpAgent.cs) | High-level agent proxy used by `AcpAgentHarness` (`InitializeAsync`, `NewSessionAsync`, `PromptAsync`, `CancelAsync`, `LoadSessionAsync`). |
| **Method Name Constants** | [`Constants.cs`](file:///C:/Users/Julien/source/repos/acp-csharp-main/src/AgentClientProtocol/Constants.cs) | Static RPC method names for `AgentMethods` (`initialize`, `session/new`, `session/prompt`) and `ClientMethods` (`session/request_permission`, `terminal/create`). |
| **JSON-RPC Engine** | [`JsonRpcEndpoint.cs`](file:///C:/Users/Julien/source/repos/acp-csharp-main/src/AgentClientProtocol/JsonRpc/JsonRpcEndpoint.cs) & [`JsonRpcMessage.cs`](file:///C:/Users/Julien/source/repos/acp-csharp-main/src/AgentClientProtocol/JsonRpc/JsonRpcMessage.cs) | Low-level bidirectional JSON-RPC message framing, request/response tracking via `RequestId`, and cancellation. |
| **Source Generator Context** | [`AcpJsonSerializerContext.cs`](file:///C:/Users/Julien/source/repos/acp-csharp-main/src/AgentClientProtocol/AcpJsonSerializerContext.cs) | High-performance `System.Text.Json` source generator. Extend with polymorphic types for ACP v2 updates. |
| **Core Schemas** | [`InitializeRequest.cs`](file:///C:/Users/Julien/source/repos/acp-csharp-main/src/AgentClientProtocol/Schema/InitializeRequest.cs)<br>[`NewSessionRequest.cs`](file:///C:/Users/Julien/source/repos/acp-csharp-main/src/AgentClientProtocol/Schema/NewSessionRequest.cs)<br>[`PromptRequest.cs`](file:///C:/Users/Julien/source/repos/acp-csharp-main/src/AgentClientProtocol/Schema/PromptRequest.cs)<br>[`RequestPermissionRequest.cs`](file:///C:/Users/Julien/source/repos/acp-csharp-main/src/AgentClientProtocol/Schema/RequestPermissionRequest.cs)<br>[`SessionNotification.cs`](file:///C:/Users/Julien/source/repos/acp-csharp-main/src/AgentClientProtocol/Schema/SessionNotification.cs) | Data contracts for handshake, session management, prompting, human permissions, and streaming update events. |
| **Reference Sandbox Client** | [`Program.cs`](file:///C:/Users/Julien/source/repos/acp-csharp-main/sandbox/ConsoleApp1/Program.cs) | Working demonstration launching an external agent (`gemini --experimental-acp`), establishing connection, handling permissions, and streaming outputs. |

---

## 4. OllamaRouter Local/Remote LLM Gateway

* **Local Root:** `C:/Users/Julien/source/repos/jetelain/OllamaRouter`
* **Architecture:** ASP.NET Core 9/10 Reverse Proxy ([YARP](https://github.com/microsoft/reverse-proxy)) orchestrating dynamic model context routing, GPU VRAM management, cloud model overflow, consecutive error auto-failover, and protocol-level tool call auto-healing.
* **Specification & Plan:** [`OLLAMAROUTER_PLAN.md`](file:///C:/Users/Julien/source/repos/jetelain/OllamaRouter/docs/OLLAMAROUTER_PLAN.md)
* **Endpoints Handled:**
  * Completion & Chat: `POST /api/chat`, `POST /api/generate`, `POST /v1/chat/completions`, `POST /v1/completions`
  * Model Catalog & State: `GET /api/tags`, `GET /v1/models`, `GET /api/ps`, `POST /api/show`
  * VRAM Reclaim & Lifecycle: `POST /api/models/unload`, `POST /monitor/reclaim-vram`
  * Dashboard & Target Control: `GET /monitor`, `POST /monitor/toggle`

### 4.1 Routing Pipeline & Reverse Proxy Core (`OllamaRouter/Middleware`, `ReverseProxy`, `Services`)
Inspects incoming requests, calculates prompt token context (including structured schema formats), executes priority-ordered target matching, and handles consecutive failure failover.

| Component | Source File | Key Architectural Patterns to Replicate / Reference |
| :--- | :--- | :--- |
| **`OllamaRoutingMiddleware`** | [`OllamaRoutingMiddleware.cs`](file:///C:/Users/Julien/source/repos/jetelain/OllamaRouter/OllamaRouter/Middleware/OllamaRoutingMiddleware.cs) | Central routing middleware. Inspects requests, checks for client target override (`X-OllamaRouter-Target`), rewrites `model` for Cloud overflow, attaches `X-OllamaRouter-*` response headers via `OnStarting`, wraps streaming responses in `ToolCallHealingResponseStream`, and tracks stream cancellations (`HTTP 499`/`502`). |
| **`RoutingDecisionService`** | [`RoutingDecisionService.cs`](file:///C:/Users/Julien/source/repos/jetelain/OllamaRouter/OllamaRouter/Services/RoutingDecisionService.cs) | Orchestrates priority target dispatch (`Local` $\rightarrow$ `Remote` $\rightarrow$ `Cloud`). Tracks consecutive failure streaks across adjacent token counts (`ConsecutiveFailuresTokenTolerance = 4000`) and fails over after `MaxConsecutiveFailures` (default: 3). |
| **`LocalRoutingTargetHandler`** | [`LocalRoutingTargetHandler.cs`](file:///C:/Users/Julien/source/repos/jetelain/OllamaRouter/OllamaRouter/Services/LocalRoutingTargetHandler.cs) | Evaluates if request fits `MaxLocalTokens`, verifies local catalog presence, checks if model is currently resident via `/api/ps`, and queries `nvidia-smi` against `MinRequiredVramMB`. |
| **`RemoteRoutingTargetHandler`** | [`RemoteRoutingTargetHandler.cs`](file:///C:/Users/Julien/source/repos/jetelain/OllamaRouter/OllamaRouter/Services/RemoteRoutingTargetHandler.cs) | Routes models exceeding local tokens, unlisted models, or requests when local GPU VRAM is exhausted. |
| **`CloudRoutingTargetHandler`** | [`CloudRoutingTargetHandler.cs`](file:///C:/Users/Julien/source/repos/jetelain/OllamaRouter/OllamaRouter/Services/CloudRoutingTargetHandler.cs) | Handles overflow to `ollama.com` cloud models when Local and Remote are busy or failing. Relays via the local Ollama daemon's authenticated cloud credentials without requiring direct external API keys. |
| **`TargetAvailabilityService`** | [`TargetAvailabilityService.cs`](file:///C:/Users/Julien/source/repos/jetelain/OllamaRouter/OllamaRouter/Services/TargetAvailabilityService.cs) | Thread-safe in-memory state manager toggling `Local`, `Remote`, and `Cloud` targets at runtime without server restart. |
| **`OllamaReverseProxyConfig`** | [`OllamaReverseProxyConfig.cs`](file:///C:/Users/Julien/source/repos/jetelain/OllamaRouter/OllamaRouter/ReverseProxy/OllamaReverseProxyConfig.cs) | Defines dynamic YARP routes and cluster definitions for Local and Remote instances matched by `X-Ollama-Target`. |
| **`OllamaRouterOptions`** | [`OllamaRouterOptions.cs`](file:///C:/Users/Julien/source/repos/jetelain/OllamaRouter/OllamaRouter/Options/OllamaRouterOptions.cs) | Configuration schema for model thresholds (`MaxLocalTokens`, `MinRequiredVramMB`, `CloudModel`), URLs, token estimation overhead factors, and `AutoHealToolCalls`. |

### 4.2 Protocol-Level Tool Call Auto-Healing (`OllamaRouter/Transforms`)
Sanitizes malformed tool calling payloads emitted by local open-weights models directly on the HTTP wire before client SDK deserialization.

| Component | Source File | Key Architectural Patterns to Replicate / Reference |
| :--- | :--- | :--- |
| **`ToolCallHealingTransform`** | [`ToolCallHealingTransform.cs`](file:///C:/Users/Julien/source/repos/jetelain/OllamaRouter/OllamaRouter/Transforms/ToolCallHealingTransform.cs) | Wire-level JSON repair: strips markdown code fences (` ```json\n...\n``` `) from `arguments`, promotes `<tool_call>` pseudo-tags in `content` into standard OpenAI/Ollama `tool_calls`, synthesizes missing `id` fields (`call_xxxxxxxx`), and repairs trailing commas. |
| **`ToolCallHealingResponseStream`** | [`ToolCallHealingResponseStream.cs`](file:///C:/Users/Julien/source/repos/jetelain/OllamaRouter/OllamaRouter/Transforms/ToolCallHealingResponseStream.cs) | Streaming interceptor supporting non-streaming JSON, NDJSON (`application/x-ndjson`), and SSE (`text/event-stream`), buffing and sanitizing tool call delta chunks before downstream emission. |
| **`ResponseCapturingStream`** | [`ResponseCapturingStream.cs`](file:///C:/Users/Julien/source/repos/jetelain/OllamaRouter/OllamaRouter/Middleware/ResponseCapturingStream.cs) | Ring-buffered stream capturing the response tail to extract usage tokens (`prompt_eval_count`, `eval_count`) and verify stream completion sentinels (`data: [DONE]`, `"done": true`). |

### 4.3 Parsing, Token Estimation & System Hardware (`OllamaRouter/Parsing`, `Services`)
Accurately estimates prompt token counts and queries host GPU memory state.

| Component | Source File | Key Architectural Patterns to Replicate / Reference |
| :--- | :--- | :--- |
| **`OllamaRequestParser`** | [`OllamaRequestParser.cs`](file:///C:/Users/Julien/source/repos/jetelain/OllamaRouter/OllamaRouter/Parsing/OllamaRequestParser.cs) | High-performance JSON parser extracting prompt context parts (`messages`, `prompt`, `system`, `tools`, `reasoning`, and guided JSON schema `format`). Rewrites model names for cloud overflow requests. |
| **`TiktokenTokenEstimator`** | [`TiktokenTokenEstimator.cs`](file:///C:/Users/Julien/source/repos/jetelain/OllamaRouter/OllamaRouter/Services/TiktokenTokenEstimator.cs) | BCL token estimator utilizing `Microsoft.ML.Tokenizers` (cl100k_base / o200k_base) for precise pre-routing token budgeting. |
| **`HeuristicTokenEstimator`** | [`HeuristicTokenEstimator.cs`](file:///C:/Users/Julien/source/repos/jetelain/OllamaRouter/OllamaRouter/Services/HeuristicTokenEstimator.cs) | Lightweight fallback token estimator (~4 chars per token). |
| **`NvidiaSmiVramProvider`** | [`NvidiaSmiVramProvider.cs`](file:///C:/Users/Julien/source/repos/jetelain/OllamaRouter/OllamaRouter/Services/NvidiaSmiVramProvider.cs) | Executes `nvidia-smi --query-gpu=memory.free` with caching and process timeout controls to inspect host GPU memory. |

### 4.4 Catalog Discovery, VRAM Reclaim & Activity Telemetry (`OllamaRouter/Endpoints`, `Services`)
Exposes model catalogs, non-destructive VRAM release, and real-time monitoring.

| Component | Source File | Key Architectural Patterns to Replicate / Reference |
| :--- | :--- | :--- |
| **`OllamaEndpointsExtensions`** | [`OllamaEndpointsExtensions.cs`](file:///C:/Users/Julien/source/repos/jetelain/OllamaRouter/OllamaRouter/Endpoints/OllamaEndpointsExtensions.cs) | Implements `/api/tags` and `/v1/models` by merging local and remote catalogs and advertising configured `CloudModel` options. Exposes clean `/api/models/unload` for unloading models from memory. |
| **`MonitorEndpointsExtensions`** | [`MonitorEndpointsExtensions.cs`](file:///C:/Users/Julien/source/repos/jetelain/OllamaRouter/OllamaRouter/Endpoints/MonitorEndpointsExtensions.cs) | Serves the `/monitor` web UI and `/monitor/reclaim-vram?disableTarget=false` for non-destructive memory reclaim without disabling the Local target. |
| **`OllamaModelCatalogClient`** | [`OllamaModelCatalogClient.cs`](file:///C:/Users/Julien/source/repos/jetelain/OllamaRouter/OllamaRouter/Services/OllamaModelCatalogClient.cs) | HTTP client interacting with local/remote Ollama instances for model tags, running process inspection (`/api/ps`), and model unloading (`keep_alive: 0`). |
| **`ActivityMonitorService`** | [`ActivityMonitorService.cs`](file:///C:/Users/Julien/source/repos/jetelain/OllamaRouter/OllamaRouter/Services/ActivityMonitorService.cs) | Tracks in-flight request state, busy status per target, and request lifecycle. |
| **`ActivityStatisticsService`** | [`ActivityStatisticsService.cs`](file:///C:/Users/Julien/source/repos/jetelain/OllamaRouter/OllamaRouter/Services/ActivityStatisticsService.cs) | Collects aggregate inference statistics (tokens/sec, prompt eval latency, failure counts) displayed on the monitor and console dashboard. |

---

## 5. ComicGen Reference Source (`ComicGenCLI`)

* **Local Root:** `C:/Users/Julien/source/repos/ComicGenCLI`
* **Architecture:** C# / .NET 10 solution implementing production Microsoft Agent Framework (`HarnessAgent`), real `AIFunction` tools with parameter schemas and descriptions, live streaming reasoning (`TextReasoningContent`) extraction, and full-fidelity benchmark transcripts (`AgentTranscript`).
* **Key Subprojects:**
  * `ComicGen.Agent`: Core MAF agent implementation, options, session management, and tools suite.
  * `ComicGen.Agent.Benchmark`: Live-logging chat client middleware, full-fidelity markdown transcripts, and scenario runner.
  * `ComicGen`: Core LLM chat client factory, local model chat formatting, and reasoning effort configuration.

### 5.1 MAF HarnessAgent Integration & Streaming Event Projection (`ComicGen.Agent`)
Demonstrates how to cleanly host Microsoft Agent Framework's `HarnessAgent`, manage per-thread `AgentSession`s, disable contradictory MAF default providers, and project `RunStreamingAsync` into strongly-typed domain events.

| Component | Source File | Key Architectural Patterns to Replicate / Reference |
| :--- | :--- | :--- |
| **`ComicGenAgent` (Harness Setup)** | [`ComicGenAgent.cs`](file:///C:/Users/Julien/source/repos/ComicGenCLI/ComicGen.Agent/ComicGenAgent.cs#L60-L115) | **HarnessAgent Construction:** Configures `HarnessAgentOptions` (`HarnessInstructions`, `ChatOptions.Tools`, `MaximumIterationsPerRequest`, `CompactionStrategy`). **Crucial:** Sets `DisableTodoProvider = true`, `DisableAgentModeProvider = true`, `DisableFileMemory = true`, `DisableAgentSkillsProvider = true`, `DisableWebSearch = true` to prevent MAF from injecting unwanted plan/execute prompt boilerplate, dummy `mode_get`/`mode_set` calls, or spurious filesystem directories. |
| **`ComicGenAgent` (Session Management)** | [`ComicGenAgent.cs`](file:///C:/Users/Julien/source/repos/ComicGenCLI/ComicGen.Agent/ComicGenAgent.cs#L116-L135) | **AgentSession Management:** Provides `GetOrCreateSessionAsync` and `CreateSessionAsync` to isolate conversational state and history across threads without state leakage. |
| **`ComicGenAgent` (Streaming Lifecycle)** | [`ComicGenAgent.cs`](file:///C:/Users/Julien/source/repos/ComicGenCLI/ComicGen.Agent/ComicGenAgent.cs#L160-L245) | **Stream Resiliency:** Asynchronous enumerator wrapping `RunStreamingInSessionAsync`, catching `OperationCanceledException` and unhandled exceptions at the iteration level and yielding terminal `TurnErrorEvent`s without crashing the host. |
| **`ComicGenAgent` (Stream Projection)** | [`ComicGenAgent.cs`](file:///C:/Users/Julien/source/repos/ComicGenCLI/ComicGen.Agent/ComicGenAgent.cs#L246-L345) | **Update Stream Projection:** Walks `_agent.RunStreamingAsync(userMessage, session)` and maps `AgentResponseUpdate` items to domain events (`ToolCallStartedEvent`, `ToolCallFinishedEvent`, `TextChunkEvent`, `TurnCompletedEvent`). Measures tool invocation duration via `Stopwatch`. |
| **`AgentStreamEvent`** | [`AgentStreamEvent.cs`](file:///C:/Users/Julien/source/repos/ComicGenCLI/ComicGen.Agent/AgentStreamEvent.cs#L1-L45) | **Domain Stream Events:** Clean record hierarchy (`TextChunkEvent`, `ToolCallStartedEvent`, `ToolCallFinishedEvent`, `TurnCompletedEvent`, `TurnErrorEvent`). |
| **`ComicGenAgentOptions`** | [`ComicGenAgentOptions.cs`](file:///C:/Users/Julien/source/repos/ComicGenCLI/ComicGen.Agent/ComicGenAgentOptions.cs) | Configuration options carrying `AgentName`, `WorkspaceDirectory`, `MaxOutputTokens`, `MaximumIterationsPerRequest`, `Compaction`, and `ReasoningEffort`. |

### 5.2 Strongly-Typed Tool Definitions (`ComicGen.Agent/Tools`)
Illustrates how to create real `AIFunction` tools with rich parameter metadata and descriptions via `AIFunctionFactory.Create`, eliminating the need for prompt-injected XML tool descriptions or dummy `() => "ok"` stubs.

| Component | Source File | Key Architectural Patterns to Replicate / Reference |
| :--- | :--- | :--- |
| **`ComicGenToolsFactory`** | [`ComicGenToolsFactory.cs`](file:///C:/Users/Julien/source/repos/ComicGenCLI/ComicGen.Agent/Tools/ComicGenToolsFactory.cs#L40-L80) | **Bounded Tool Suite Catalog:** Assembles domain, filesystem, search, VCS, UI interaction, and skill tools into an `IReadOnlyList<AIFunction>` returned directly to MAF. |
| **`FileTools` (read_file)** | [`FileTools.cs`](file:///C:/Users/Julien/source/repos/ComicGenCLI/ComicGen.Agent/Tools/FileTools.cs#L12-L42) | Uses `AIFunctionFactory.Create` with `[Description]` attributes on parameters (`string path`, `int? offset`, `int? limit`) for line-sliced reading with 1-indexed line numbers (`startLine + index + 1`). |
| **`FileTools` (write/edit/move/delete)** | [`FileTools.cs`](file:///C:/Users/Julien/source/repos/ComicGenCLI/ComicGen.Agent/Tools/FileTools.cs#L44-L140) | Workspace-safe file mutations (`ResolveSafePath`) enforcing UTF-8 without BOM and LF line endings. `edit_file` performs exact snippet replacement with uniqueness verification. |
| **`SearchTools` (list_dir & grep_search)** | [`SearchTools.cs`](file:///C:/Users/Julien/source/repos/ComicGenCLI/ComicGen.Agent/Tools/SearchTools.cs#L24-L120) | Safe directory enumeration with glob filtering and regex grep searching, ignoring VCS and build artifact directories (`.git`, `bin`, `obj`). |
| **`InteractionTools` (ask_user & user_pick)** | [`InteractionTools.cs`](file:///C:/Users/Julien/source/repos/ComicGenCLI/ComicGen.Agent/Tools/InteractionTools.cs#L1-L60) | Turn-blocking human-in-the-loop interaction tools exposed as `AIFunction`s delegated to `IUserInteractionDriver`. |
| **`SkillTools` (read_skill)** | [`SkillTools.cs`](file:///C:/Users/Julien/source/repos/ComicGenCLI/ComicGen.Agent/Tools/SkillTools.cs#L1-L40) | Dynamic skill content loading tool enabling progressive disclosure of specialized workflow instructions. |

### 5.3 Live Streaming Reasoning & Intercepting Telemetry (`ComicGen.Agent.Benchmark`)
Demonstrates how to intercept streaming model calls at the `IChatClient` boundary to capture `TextReasoningContent` deltas and compile full-fidelity diagnostic transcripts.

| Component | Source File | Key Architectural Patterns to Replicate / Reference |
| :--- | :--- | :--- |
| **`LiveLogChatClient` (Streaming Reasoning)** | [`LiveLogChatClient.cs`](file:///C:/Users/Julien/source/repos/ComicGenCLI/ComicGen.Agent.Benchmark/Infrastructure/LiveLogChatClient.cs#L52-L105) | **Live `TextReasoningContent` Extraction:** In `GetStreamingResponseAsync`, iterates `update.Contents` matching `case TextReasoningContent reasoning:` to accumulate thinking text without relying on `update.Text` (which is empty during thinking). |
| **`LiveLogChatClient` (Tool Call Interception)** | [`LiveLogChatClient.cs`](file:///C:/Users/Julien/source/repos/ComicGenCLI/ComicGen.Agent.Benchmark/Infrastructure/LiveLogChatClient.cs#L94-L99) | Matches `case FunctionCallContent call:` to record tool calls and arguments to the transcript and console immediately. |
| **`LiveLogChatClient` (Stream Flush)** | [`LiveLogChatClient.cs`](file:///C:/Users/Julien/source/repos/ComicGenCLI/ComicGen.Agent.Benchmark/Infrastructure/LiveLogChatClient.cs#L106-L125) | Flushes consolidated 💭 `reasoning` and 🤖 `assistant` entries to transcripts and live console on stream close. |
| **`LiveLogChatClient` (Non-Streaming)** | [`LiveLogChatClient.cs`](file:///C:/Users/Julien/source/repos/ComicGenCLI/ComicGen.Agent.Benchmark/Infrastructure/LiveLogChatClient.cs#L170-L205) | Inspects `ChatMessage.Contents` for `TextReasoningContent` and `FunctionCallContent` in non-streaming responses. |
| **`AgentTranscript` (Environment Rendering)** | [`AgentTranscript.cs`](file:///C:/Users/Julien/source/repos/ComicGenCLI/ComicGen.Agent.Benchmark/Models/AgentTranscript.cs#L81-L125) | **`### Environment` Rendering:** Renders `**Available tools**` and `**System prompt**` inside safe 4-tick fences (` ```` `) before conversation turns. |
| **`AgentTranscript` (Ordered Step Taxonomy)** | [`AgentTranscript.cs`](file:///C:/Users/Julien/source/repos/ComicGenCLI/ComicGen.Agent.Benchmark/Models/AgentTranscript.cs#L126-L165) | **Ordered Step Taxonomy:** Emits chronological entries with wall-clock offsets (`[ mm:ss ]`) and icons (`🧑 User`, `💭 Reasoning`, `🤖 Assistant`, `🔧 Tool call`, `↳ Tool result`, `❌ Tool error`). |
| **`AgentTranscript` (Safety Capping)** | [`AgentTranscript.cs`](file:///C:/Users/Julien/source/repos/ComicGenCLI/ComicGen.Agent.Benchmark/Models/AgentTranscript.cs#L166-L175) | Enforces `MaxEntryChars = 32768` truncation per step to prevent unbounded transcript bloat. |
| **`BenchmarkRunner`** | [`BenchmarkRunner.cs`](file:///C:/Users/Julien/source/repos/ComicGenCLI/ComicGen.Agent.Benchmark/Runner/BenchmarkRunner.cs#L120-L165) | Wires `LiveLogChatClient`, `AgentTranscript`, and console live stream during scenario execution. |

### 5.4 LLM Runtimes, Reasoning Effort & Local Formatting (`ComicGen/Llm`, `docs/issues`)

| Component | Source File | Key Architectural Patterns to Replicate / Reference |
| :--- | :--- | :--- |
| **`ChatClientFactory`** | [`ChatClientFactory.cs`](file:///C:/Users/Julien/source/repos/ComicGenCLI/ComicGen/Llm/ChatClientFactory.cs#L220-L240) | Creation of `OllamaApiClient`, `OpenAIClient`, `GeminiChatClient`, and `LLamaSharpChatClient`. Shows HTTP client timeout configuration and model instantiation. |
| **`LocalModelChatFormatter`** | [`LocalModelChatFormatter.cs`](file:///C:/Users/Julien/source/repos/ComicGenCLI/ComicGen/Llm/LocalModelChatFormatter.cs#L270-L305) | Parses raw model responses containing `<think>...</think>` or `<thought>...</thought>` tags into structured `new TextReasoningContent(reasoning)`. |
| **`Reasoning Effort Architecture RFC`** | [`2026-10-11-reasoning-effort-configuration.md`](file:///C:/Users/Julien/source/repos/ComicGenCLI/docs/issues/2026-10-11-reasoning-effort-configuration.md) | Architectural decision record detailing configuration and propagation of `reasoning_effort` across providers (`openai`, `ollama`, `gemini`, `llama`), mapping Qwen's `xhigh` and Ollama's `think: true`. |

---

## 6. NightCodeForge Architectural Mapping

This table connects NightCodeForge's architectural components to the corresponding source files across all reference codebases:

| NightCodeForge Component | Microsoft Agent Framework | ComicGen Reference | Pi Coding Agent | Agent Client Protocol (ACP) | OllamaRouter Gateway | Target Implementation in NightCodeForge |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| **Native Crucible Harness** | [`HarnessAgent.cs`](file:///C:/Users/Julien/source/repos/agent-framework-main/dotnet/src/Microsoft.Agents.AI.Harness/HarnessAgent.cs) | [`ComicGenAgent.cs`](file:///C:/Users/Julien/source/repos/ComicGenCLI/ComicGen.Agent/ComicGenAgent.cs) | [`system-prompt.ts`](file:///C:/Users/Julien/source/repos/pi-main/packages/coding-agent/src/core/system-prompt.ts) | — | — | `NightCodeForge.Agents/Crucible/CrucibleAgent.cs` |
| **Pi JSON-RPC Harness** | — | — | [`rpc-mode.ts`](file:///C:/Users/Julien/source/repos/pi-main/packages/coding-agent/src/modes/rpc/rpc-mode.ts)<br>[`rpc-types.ts`](file:///C:/Users/Julien/source/repos/pi-main/packages/coding-agent/src/modes/rpc/rpc-types.ts) | — | — | `NightCodeForge.Agents/PiRpc/PiRpcAgentHarness.cs` |
| **Open ACP Harness Adapter** | — | — | — | [`ClientSideConnection.cs`](file:///C:/Users/Julien/source/repos/acp-csharp-main/src/AgentClientProtocol/ClientSideConnection.cs)<br>[`Program.cs`](file:///C:/Users/Julien/source/repos/acp-csharp-main/sandbox/ConsoleApp1/Program.cs) | — | `NightCodeForge.Agents/Acp/AcpAgentHarness.cs` |
| **Minimalist Canonical Tools** | — | [`FileTools.cs`](file:///C:/Users/Julien/source/repos/ComicGenCLI/ComicGen.Agent/Tools/FileTools.cs)<br>[`SearchTools.cs`](file:///C:/Users/Julien/source/repos/ComicGenCLI/ComicGen.Agent/Tools/SearchTools.cs) | [`read.ts`](file:///C:/Users/Julien/source/repos/pi-main/packages/coding-agent/src/core/tools/read.ts)<br>[`edit.ts`](file:///C:/Users/Julien/source/repos/pi-main/packages/coding-agent/src/core/tools/edit.ts)<br>[`write.ts`](file:///C:/Users/Julien/source/repos/pi-main/packages/coding-agent/src/core/tools/write.ts)<br>[`bash.ts`](file:///C:/Users/Julien/source/repos/pi-main/packages/coding-agent/src/core/tools/bash.ts) | [`IAcpClient.cs`](file:///C:/Users/Julien/source/repos/acp-csharp-main/src/AgentClientProtocol/IAcpClient.cs) (`ReadTextFile`, `WriteTextFile`) | — | `NightCodeForge.Agents/Tools/` (`ReadTool.cs`, `EditTool.cs`, `WriteTool.cs`, `BashTool.cs`, `CanonicalAIFunctionFactory.cs`) |
| **Streaming Reasoning & Live Tracing** | — | [`LiveLogChatClient.cs`](file:///C:/Users/Julien/source/repos/ComicGenCLI/ComicGen.Agent.Benchmark/Infrastructure/LiveLogChatClient.cs) | `message_update` in [`rpc-mode.ts`](file:///C:/Users/Julien/source/repos/pi-main/packages/coding-agent/src/modes/rpc/rpc-mode.ts) | `SessionNotificationAsync` in [`IAcpClient.cs`](file:///C:/Users/Julien/source/repos/acp-csharp-main/src/AgentClientProtocol/IAcpClient.cs) | — | `NightCodeForge.Agents/Ideation/IdeationService.cs`<br>`NightCodeForge.Agents/Crucible/CrucibleAgentHarness.cs` |
| **Full-Fidelity Transcripts** | — | [`AgentTranscript.cs`](file:///C:/Users/Julien/source/repos/ComicGenCLI/ComicGen.Agent.Benchmark/Models/AgentTranscript.cs) | — | — | — | `NightCodeForge.Core/Services/SessionTranscriptService.cs` |
| **Reasoning Effort & Thinking Options** | — | [`2026-10-11-reasoning-effort-configuration.md`](file:///C:/Users/Julien/source/repos/ComicGenCLI/docs/issues/2026-10-11-reasoning-effort-configuration.md) | — | — | `OllamaRequestParser.cs` | `NightCodeForge.Infrastructure/Inference/OllamaChatClientFactory.cs`<br>`NightCodeForge.Core/Models/LlmProviderOptions.cs` |
| **Forgiving Tool Dispatcher** | — | — | [`tools-manager.ts`](file:///C:/Users/Julien/source/repos/pi-main/packages/coding-agent/src/utils/tools-manager.ts) | — | [`ToolCallHealingTransform.cs`](file:///C:/Users/Julien/source/repos/jetelain/OllamaRouter/OllamaRouter/Transforms/ToolCallHealingTransform.cs) (wire-level tool repair) | `NightCodeForge.Agents/Dispatcher/LenientToolDispatcher.cs` |
| **EditorConfig Enforcement Engine** | — | — | — | — | — | `NightCodeForge.Agents/Tools/EditorConfig/` (`IEditorConfigService.cs`, `EditorConfigFileApplier.cs`) |
| **Container Sandbox REPL** | [`DockerShellExecutor.cs`](file:///C:/Users/Julien/source/repos/agent-framework-main/dotnet/src/Microsoft.Agents.AI.Tools.Shell/DockerShellExecutor.cs) | — | — | [`CreateTerminalRequest.cs`](file:///C:/Users/Julien/source/repos/acp-csharp-main/src/AgentClientProtocol/Schema/CreateTerminalRequest.cs) | — | `NightCodeForge.Infrastructure/Execution/NightCodeForgeContainerExecutor.cs` |
| **Restricted Read-Only Sandbox (Triage)** | Docker CLI hardening (`--network none`, `--read-only`, `:ro`, `--cap-drop=ALL`) | — | — | — | Target Pinning: `X-OllamaRouter-Target: Local` | `NightCodeForge.Infrastructure/Execution/ContainerSandboxEnvironment.cs` |
| **Rune-Safe Output Compaction** | [`HeadTailBuffer.cs`](file:///C:/Users/Julien/source/repos/agent-framework-main/dotnet/src/Microsoft.Agents.AI.Tools.Shell/HeadTailBuffer.cs) | — | [`output-accumulator.ts`](file:///C:/Users/Julien/source/repos/pi-main/packages/coding-agent/src/core/tools/output-accumulator.ts) | — | — | `NightCodeForge.Infrastructure/Execution/HeadTailBuffer.cs` |
| **In-Loop Context Compaction** | [`ToolResultCompactionStrategy.cs`](file:///C:/Users/Julien/source/repos/agent-framework-main/dotnet/src/Microsoft.Agents.AI/Compaction/ToolResultCompactionStrategy.cs) | `ComicGenAgentOptions.Compaction` | [`compaction.ts`](file:///C:/Users/Julien/source/repos/pi-main/packages/coding-agent/src/core/compaction/compaction.ts) | — | — | `NightCodeForge.Agents/Crucible/Compaction/`<br>`NightCodeForge.Agents/Compaction/ThinkingTraceCompactionChatClient.cs` |
| **Zero Data Loss Middleware** | [`PerServiceCallChatHistoryPersistingChatClient.cs`](file:///C:/Users/Julien/source/repos/agent-framework-main/dotnet/src/Microsoft.Agents.AI/ChatClient/PerServiceCallChatHistoryPersistingChatClient.cs) | — | — | — | — | `NightCodeForge.Agents/Crucible/ChatClients/` |
| **Session Resume & Interruption** | [`ApprovalResponseBindingChatClient.cs`](file:///C:/Users/Julien/source/repos/agent-framework-main/dotnet/src/Microsoft.Agents.AI/ChatClient/ApprovalResponseBindingChatClient.cs) | [`InteractionTools.cs`](file:///C:/Users/Julien/source/repos/ComicGenCLI/ComicGen.Agent/Tools/InteractionTools.cs) | `extension_ui_request` in [`rpc-types.ts`](file:///C:/Users/Julien/source/repos/pi-main/packages/coding-agent/src/modes/rpc/rpc-types.ts) | [`RequestPermissionRequest.cs`](file:///C:/Users/Julien/source/repos/acp-csharp-main/src/AgentClientProtocol/Schema/RequestPermissionRequest.cs) | — | `NightCodeForge.Agents/Common/HumanInterruptionHandler.cs` |
| **SignalR Telemetry Streaming** | `OpenTelemetryAgent` | `AgentStreamEvent.cs` | `message_update` in [`rpc-mode.ts`](file:///C:/Users/Julien/source/repos/pi-main/packages/coding-agent/src/modes/rpc/rpc-mode.ts) | `SessionNotificationAsync` in [`IAcpClient.cs`](file:///C:/Users/Julien/source/repos/acp-csharp-main/src/AgentClientProtocol/IAcpClient.cs) | `X-OllamaRouter-Target` headers & [`ActivityMonitorService.cs`](file:///C:/Users/Julien/source/repos/jetelain/OllamaRouter/OllamaRouter/Services/ActivityMonitorService.cs) | `NightCodeForge.Infrastructure/Hubs/OrchestratorHub.cs` |
| **Host MCP Tool Integration** | `Microsoft.Agents.AI.Mcp` | — | — | `McpServers` in [`NewSessionRequest.cs`](file:///C:/Users/Julien/source/repos/acp-csharp-main/src/AgentClientProtocol/Schema/NewSessionRequest.cs) | — | `NightCodeForge.Agents/Mcp/McpClientManager.cs` |
| **LLM Inference Gateway** | `IChatClient` | [`ChatClientFactory.cs`](file:///C:/Users/Julien/source/repos/ComicGenCLI/ComicGen/Llm/ChatClientFactory.cs) | [`rpc-client.ts`](file:///C:/Users/Julien/source/repos/pi-main/packages/coding-agent/src/core/../src/modes/rpc/rpc-client.ts) | — | [`OllamaRoutingMiddleware.cs`](file:///C:/Users/Julien/source/repos/jetelain/OllamaRouter/OllamaRouter/Middleware/OllamaRoutingMiddleware.cs)<br>[`OllamaReverseProxyConfig.cs`](file:///C:/Users/Julien/source/repos/jetelain/OllamaRouter/OllamaRouter/ReverseProxy/OllamaReverseProxyConfig.cs) | `NightCodeForge.Infrastructure/Inference/OllamaRouterClient.cs` (`Microsoft.Extensions.AI`) |
| **Dynamic Model Catalog Discovery** | — | — | [`skills.ts`](file:///C:/Users/Julien/source/repos/pi-main/packages/coding-agent/src/core/skills.ts) | — | [`OllamaEndpointsExtensions.cs`](file:///C:/Users/Julien/source/repos/jetelain/OllamaRouter/OllamaRouter/Endpoints/OllamaEndpointsExtensions.cs) (`/api/tags`, `/v1/models`) | `NightCodeForge.Infrastructure/Services/OllamaModelCatalogService.cs` |
| **Zero-VRAM Cloud AI Diagnostics** | — | — | — | — | [`OllamaRoutingMiddleware.cs`](file:///C:/Users/Julien/source/repos/jetelain/OllamaRouter/OllamaRouter/Middleware/OllamaRoutingMiddleware.cs) (`X-OllamaRouter-Target: Cloud`) | `NightCodeForge.Infrastructure/Diagnostics/TranscriptDiagnosticService.cs` |
| **JIT Model Preload & Warmup** | — | — | — | — | [`OllamaModelCatalogClient.cs`](file:///C:/Users/Julien/source/repos/jetelain/OllamaRouter/OllamaRouter/Services/OllamaModelCatalogClient.cs) (`POST /api/generate` with `keep_alive`) | `NightCodeForge.Core/Pipeline/PipelineEngine.cs` (Stage transitions) |
| **Non-Destructive VRAM Reclaim** | — | — | — | — | [`MonitorEndpointsExtensions.cs`](file:///C:/Users/Julien/source/repos/jetelain/OllamaRouter/OllamaRouter/Endpoints/MonitorEndpointsExtensions.cs) (`/monitor/reclaim-vram?disableTarget=false`, `/api/models/unload`) | `NightCodeForge.Core/Services/WorkspaceLock.cs` & stage teardown |


