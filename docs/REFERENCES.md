# 📚 NightCodeForge - Source Code Reference Guide

This document indexes key reference files from the local source checkouts of **Microsoft Agent Framework** and **Pi Coding Agent**. Use these references to guide the implementation of NightCodeForge's harnesses, sandboxed execution environments, context compaction, and JSON-RPC daemon bridges.

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

## 4. NightCodeForge Architectural Mapping

This table connects NightCodeForge's architectural components to the corresponding source files across all three reference codebases:

| NightCodeForge Component | Microsoft Agent Framework | Pi Coding Agent | Agent Client Protocol (ACP) | Target Implementation in NightCodeForge |
| :--- | :--- | :--- | :--- | :--- |
| **Native Crucible Harness** | [`HarnessAgent.cs`](file:///C:/Users/Julien/source/repos/agent-framework-main/dotnet/src/Microsoft.Agents.AI.Harness/HarnessAgent.cs) | [`system-prompt.ts`](file:///C:/Users/Julien/source/repos/pi-main/packages/coding-agent/src/core/system-prompt.ts) | — | `NightCodeForge.Agents/Crucible/CrucibleAgent.cs` |
| **Pi JSON-RPC Harness** | — | [`rpc-mode.ts`](file:///C:/Users/Julien/source/repos/pi-main/packages/coding-agent/src/modes/rpc/rpc-mode.ts)<br>[`rpc-types.ts`](file:///C:/Users/Julien/source/repos/pi-main/packages/coding-agent/src/modes/rpc/rpc-types.ts) | — | `NightCodeForge.Agents/PiRpc/PiRpcAgentHarness.cs` |
| **Open ACP Harness Adapter** | — | — | [`ClientSideConnection.cs`](file:///C:/Users/Julien/source/repos/acp-csharp-main/src/AgentClientProtocol/ClientSideConnection.cs)<br>[`Program.cs`](file:///C:/Users/Julien/source/repos/acp-csharp-main/sandbox/ConsoleApp1/Program.cs) | `NightCodeForge.Agents/Acp/AcpAgentHarness.cs` |
| **Minimalist Canonical Tools** | — | [`read.ts`](file:///C:/Users/Julien/source/repos/pi-main/packages/coding-agent/src/core/tools/read.ts)<br>[`edit.ts`](file:///C:/Users/Julien/source/repos/pi-main/packages/coding-agent/src/core/tools/edit.ts)<br>[`write.ts`](file:///C:/Users/Julien/source/repos/pi-main/packages/coding-agent/src/core/tools/write.ts)<br>[`bash.ts`](file:///C:/Users/Julien/source/repos/pi-main/packages/coding-agent/src/core/tools/bash.ts) | [`IAcpClient.cs`](file:///C:/Users/Julien/source/repos/acp-csharp-main/src/AgentClientProtocol/IAcpClient.cs) (`ReadTextFile`, `WriteTextFile`) | `NightCodeForge.Agents/Tools/` (`ReadTool.cs`, `EditTool.cs`, `WriteTool.cs`, `BashTool.cs`) |
| **Forgiving Tool Dispatcher** | — | [`tools-manager.ts`](file:///C:/Users/Julien/source/repos/pi-main/packages/coding-agent/src/utils/tools-manager.ts) | — | `NightCodeForge.Agents/Dispatcher/LenientToolDispatcher.cs` |
| **EditorConfig Enforcement Engine** | — | — | — | `NightCodeForge.Agents/Tools/EditorConfig/` (`IEditorConfigService.cs`, `EditorConfigFileApplier.cs`, consuming official `editorconfig` .NET parser) |
| **Container Sandbox REPL** | [`DockerShellExecutor.cs`](file:///C:/Users/Julien/source/repos/agent-framework-main/dotnet/src/Microsoft.Agents.AI.Tools.Shell/DockerShellExecutor.cs) | — | [`CreateTerminalRequest.cs`](file:///C:/Users/Julien/source/repos/acp-csharp-main/src/AgentClientProtocol/Schema/CreateTerminalRequest.cs) (forwarded to REPL) | `NightCodeForge.Infrastructure/Execution/NightCodeForgeContainerExecutor.cs` |
| **Restricted Read-Only Sandbox (Triage)** | Docker CLI hardening (`--network none`, `--read-only`, `:ro` mount, `--cap-drop=ALL`) | — | — | `NightCodeForge.Infrastructure/Execution/ContainerSandboxEnvironment.cs` (`SandboxSecurityTier.RestrictedReadOnly`) |
| **Rune-Safe Output Compaction** | [`HeadTailBuffer.cs`](file:///C:/Users/Julien/source/repos/agent-framework-main/dotnet/src/Microsoft.Agents.AI.Tools.Shell/HeadTailBuffer.cs) | [`output-accumulator.ts`](file:///C:/Users/Julien/source/repos/pi-main/packages/coding-agent/src/core/tools/output-accumulator.ts) | — | `NightCodeForge.Infrastructure/Execution/HeadTailBuffer.cs` |
| **In-Loop Context Compaction** | [`ToolResultCompactionStrategy.cs`](file:///C:/Users/Julien/source/repos/agent-framework-main/dotnet/src/Microsoft.Agents.AI/Compaction/ToolResultCompactionStrategy.cs) | [`compaction.ts`](file:///C:/Users/Julien/source/repos/pi-main/packages/coding-agent/src/core/compaction/compaction.ts) | — | `NightCodeForge.Agents/Crucible/Compaction/` |
| **Zero Data Loss Middleware** | [`PerServiceCallChatHistoryPersistingChatClient.cs`](file:///C:/Users/Julien/source/repos/agent-framework-main/dotnet/src/Microsoft.Agents.AI/ChatClient/PerServiceCallChatHistoryPersistingChatClient.cs) | — | — | `NightCodeForge.Agents/Crucible/ChatClients/` |
| **Session Resume & Interruption** | [`ApprovalResponseBindingChatClient.cs`](file:///C:/Users/Julien/source/repos/agent-framework-main/dotnet/src/Microsoft.Agents.AI/ChatClient/ApprovalResponseBindingChatClient.cs) | `extension_ui_request` in [`rpc-types.ts`](file:///C:/Users/Julien/source/repos/pi-main/packages/coding-agent/src/modes/rpc/rpc-types.ts) | [`RequestPermissionRequest.cs`](file:///C:/Users/Julien/source/repos/acp-csharp-main/src/AgentClientProtocol/Schema/RequestPermissionRequest.cs) | `NightCodeForge.Agents/Common/HumanInterruptionHandler.cs` |
| **SignalR Telemetry Streaming** | `OpenTelemetryAgent` | `message_update` in [`rpc-mode.ts`](file:///C:/Users/Julien/source/repos/pi-main/packages/coding-agent/src/modes/rpc/rpc-mode.ts) | `SessionNotificationAsync` in [`IAcpClient.cs`](file:///C:/Users/Julien/source/repos/acp-csharp-main/src/AgentClientProtocol/IAcpClient.cs) | `NightCodeForge.Infrastructure/Hubs/OrchestratorHub.cs` |
| **Host MCP Tool Integration** | `Microsoft.Agents.AI.Mcp` | — | `McpServers` in [`NewSessionRequest.cs`](file:///C:/Users/Julien/source/repos/acp-csharp-main/src/AgentClientProtocol/Schema/NewSessionRequest.cs) | `NightCodeForge.Agents/Mcp/McpClientManager.cs` |

