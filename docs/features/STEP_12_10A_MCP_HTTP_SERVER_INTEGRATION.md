# ⚡ Step 12.10A: Remote HTTP/SSE Model Context Protocol (MCP) Server Integration

> **Feature Implementation Step (Part 1 of 2 — `ModelContextProtocol` Client & HTTP Transport)** *(formerly Step 11.9)*  
> *Parent Plan:* [Features & Integrations Roadmap](README.md)  
> *Companion Step:* [Step 12.10B: Isolated Stdio MCP Servers inside `ContainerSandbox`](STEP_12_10B_MCP_CONTAINER_SANDBOX_STDIO_INTEGRATION.md)  
> *Project Target:* `src/NightCodeForge.Agents/Mcp`, `src/NightCodeForge.Agents/Crucible`, `tests/NightCodeForge.Agents.Tests` | *Status:* Planned | *Target Framework:* .NET 10 (C# 13)  
> *Aligns With:* [AGENTS.md](../../AGENTS.md), [docs/PLAN.md](../PLAN.md)

---

## 1. Executive Objective

Provide first-class **Model Context Protocol (MCP)** tool discovery, invocation, and session lifecycle management for remote/hosted HTTP MCP servers, fully aligned with the **Microsoft Agent Framework (MAF)** and .NET MCP ecosystem:

1. **Native Microsoft Agent Framework (MAF) Integration:**
   - Consume MCP servers via the official Microsoft .NET package `ModelContextProtocol` (`ModelContextProtocol.Client` v2.2.0).
   - Leverage `McpClient.ListToolsAsync()` which produces `McpClientTool` instances that natively derive from `AITool` / `AIFunction`, integrating directly into `HarnessAgent` (`ChatOptions.Tools`) without synthetic wrappers.
2. **Remote / Hosted HTTP MCP Servers (Streamable HTTP / SSE):**
   - Connect to remote MCP servers specified with `"url"` in `mcp_config.json`.
   - Manage active HTTP sessions via an `IMcpSession : IAsyncDisposable` lifecycle scope tied to stage execution.
   - Namespace tools cleanly (`mcp_{serverName}_{toolName}`) to eliminate collisions with canonical tools (`read`, `edit`, `bash`).
   - Provide graceful fault tolerance: an offline or misconfigured HTTP server logs a warning and is skipped without aborting stage startup.
3. **Security, Permission Gating & Telemetry:**
   - Enforce `AgentProfile.PermittedTools` whitelists (including wildcards like `mcp_github_*`).
   - Stream MCP tool calls, arguments, latency, and outputs through [`ThinkingAndActivityTracingChatClient`](../../src/NightCodeForge.Agents/Inference/ThinkingAndActivityTracingChatClient.cs) to the Task Inspector and markdown transcripts.

*(Isolated stdio MCP servers running inside `ContainerSandbox` via `docker exec -i` are specified in [Step 12.10B](STEP_12_10B_MCP_CONTAINER_SANDBOX_STDIO_INTEGRATION.md).)*

---

## 2. Technical Design Specifications

### 2.1 NuGet Dependency
`NightCodeForge.Agents.csproj` references:
```xml
<PackageReference Include="ModelContextProtocol" Version="2.2.0" />
```

### 2.2 Managed Session Architecture (`IMcpSession` & `IMcpClientManager`)

```csharp
namespace NightCodeForge.Agents.Mcp;

public interface IMcpSession : IAsyncDisposable
{
    IReadOnlyList<AITool> Tools { get; }
}

public interface IMcpClientManager
{
    Task<IReadOnlyDictionary<string, McpServerDefinition>> GetConfiguredServersAsync(
        string workspaceRoot,
        CancellationToken cancellationToken = default);

    Task<IMcpSession> StartSessionAsync(
        string workspaceRoot,
        IExecutionEnvironment? executionEnv,
        IReadOnlyList<string> permittedTools,
        IReadOnlyDictionary<string, string>? toolDirectives = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<object>> GetAcpMcpServersAsync(
        int? workspaceId,
        string? workspaceRoot = null,
        CancellationToken cancellationToken = default);
}
```

### 2.3 HTTP MCP Server Connection & Tool Discovery
For each server in `McpConfigurationFile.McpServers` where `!string.IsNullOrWhiteSpace(serverDef.Url)`:
1. Validate URL scheme (`http` or `https`).
2. Build `HttpClientTransportOptions` with `TransportMode = HttpTransportMode.StreamableHttp`, create `HttpClientTransport`, and connect via `McpClient.CreateAsync(transport, cancellationToken: ct)`.
3. Retrieve tools via `await client.ListToolsAsync(cancellationToken: ct)`.
4. Namespace and wrap tools:
   - Tool Name: `mcp_{serverName}_{tool.Name}`.
   - Description: Apply database-configured directive from `toolDirectives` if present; otherwise preserve server description.
   - Filter against `permittedTools`: Only add tools matching `mcp_{serverName}_{tool.Name}` or `mcp_{serverName}_*`.
5. Error Resilience: If a remote server is down, unreachable, or returns a 5xx error on connect, log a warning with the server name and URL, and continue discovering other servers.

### 2.4 Integration with [`CrucibleAgentHarness.cs`](../../src/NightCodeForge.Agents/Crucible/CrucibleAgentHarness.cs)
In `RunStageAsync`:
```csharp
await using var mcpSession = _mcpManager != null
    ? await _mcpManager.StartSessionAsync(
        workspacePath,
        effectiveEnv,
        permittedTools,
        harnessConfig.ToolDirectives,
        cancellationToken)
    : null;

var aiTools = BuildAIFunctionList(
    canonicalTools,
    permittedTools,
    dispatcher,
    harnessConfig.CustomTools,
    harnessConfig.ToolDirectives,
    mcpSession?.Tools);
```

---

## 3. Implementation Checklist & Deliverables

- [ ] Add `<PackageReference Include="ModelContextProtocol" Version="2.2.0" />` to `NightCodeForge.Agents.csproj`.
- [ ] Implement `IMcpSession` and update `IMcpClientManager.StartSessionAsync(...)`.
- [ ] Implement `McpSession` with `HttpClientTransport` connection, tool discovery, namespacing (`mcp_{server}_{tool}`), wildcard permission filtering (`mcp_{server}_*`), and async disposal.
- [ ] Update [`CrucibleAgentHarness.cs`](../../src/NightCodeForge.Agents/Crucible/CrucibleAgentHarness.cs) to acquire `IMcpSession`, remove the legacy MCP error stub, and include `mcpSession.Tools` in `ChatOptions.Tools`.
- [ ] Verify `ThinkingAndActivityTracingChatClient` and `BuildTranscriptEntries` capture and render `mcp_{server}_{tool}` calls cleanly.
- [ ] Add unit tests in `NightCodeForge.Agents.Tests` with mock HTTP MCP servers verifying tool discovery, invocation, permission filtering, and graceful offline degradation.

