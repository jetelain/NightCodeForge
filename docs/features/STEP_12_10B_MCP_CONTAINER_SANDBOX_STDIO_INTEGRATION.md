# ⚡ Step 12.10B: Isolated Stdio MCP Servers inside `ContainerSandbox`

> **Feature Implementation Step (Part 2 of 2 — Containerized `StdioClientTransport` & Sandbox Dispatch)**  
> *Parent Plan:* [Features & Integrations Roadmap](README.md)  
> *Prerequisite Step:* [Step 12.10A: Remote HTTP/SSE MCP Server Integration](STEP_12_10A_MCP_HTTP_SERVER_INTEGRATION.md)  
> *Project Target:* `src/NightCodeForge.Agents/Mcp`, `src/NightCodeForge.Infrastructure/Execution`, `tests/NightCodeForge.Agents.Tests` | *Status:* Planned | *Target Framework:* .NET 10 (C# 13)  
> *Aligns With:* [AGENTS.md](../../AGENTS.md), [docs/PLAN.md](../PLAN.md)

---

## 1. Executive Objective

Building on the `IMcpSession` architecture from [Step 12.10A](STEP_12_10A_MCP_HTTP_SERVER_INTEGRATION.md), this step (Part 2 of Step 12.10) enables **stdio-based MCP servers** (e.g. `@modelcontextprotocol/server-github`, Python/Node/.NET local MCP tools) while strictly enforcing NightCodeForge's container isolation guarantees:
1. **Isolated Stdio Execution inside `ContainerSandbox`:** Run stdio MCP servers **strictly inside the task's Docker sandbox container** via interactive `docker exec -i -w /workspace/task-{id} <containerName> <command> <args>` piped directly to `StdioClientTransport`.
2. **Prevent Unverified Host Binary Execution:** Never spawn stdio MCP commands directly on the host OS when the workspace is configured for `ContainerSandbox`.
3. **Multi-Tier Dispatch Matrix (`ContainerSandbox`, `WslDirect`, `BareHost`):** Route `StdioClientTransportOptions` through `docker exec -i` (with WSL wrapper when Docker runs inside WSL2), `wsl.exe -d <distro> --cd ...` for `WslDirect`, or host execution for `BareHost`.

---

## 2. Technical Design Specifications

### 2.1 Container Command Builder (`McpClientManager`)
When `serverDef.Command` is populated and `executionEnv` is [`NightCodeForgeContainerExecutor`](../../src/NightCodeForge.Infrastructure/Execution/NightCodeForgeContainerExecutor.cs) (`ExecutionTier.ContainerSandbox`):

1. **Extract Container Runtime Parameters:**
   - Container Name/ID: `containerExecutor.Options.ContainerNameOrId`.
   - Container Working Directory: `/workspace/task-{taskId}` (or `/workspace/clone`).
   - WSL Distribution (if Docker is running in WSL): `containerExecutor.Options.UseWsl ? containerExecutor.Options.WslDistribution : null`.

2. **Construct Interactive Docker Execution Arguments:**
   ```csharp
   var dockerArgs = new List<string>
   {
       "exec",
       "-i",                         // Interactive stdio stream for JSON-RPC
       "-w", containerWorkingDir
   };

   // Inject environment variables into the container command
   foreach (var (key, value) in serverDef.Env)
   {
       dockerArgs.Add("-e");
       dockerArgs.Add($"{key}={value}");
   }

   dockerArgs.Add(containerName);
   dockerArgs.Add(serverDef.Command);
   dockerArgs.AddRange(serverDef.Args);
   ```

3. **Initialize `StdioClientTransport`:**
   ```csharp
   var transportOptions = new StdioClientTransportOptions
   {
       Name = serverName,
       Command = useWsl ? "wsl.exe" : "docker",
       Arguments = useWsl ? BuildWslDockerArgs(wslDistro, dockerArgs) : dockerArgs,
       WorkingDirectory = workspaceRoot
   };
   var transport = new StdioClientTransport(transportOptions);
   var client = await McpClient.CreateAsync(transport, cancellationToken: ct);
   ```

### 2.2 Execution Environment Dispatch Matrix
- **`ContainerSandbox`:** Stdio tools spawn strictly via `docker exec -i -w ...` (via `wsl.exe` if Docker runs in WSL) inside the task container. If no running container is available, stdio tools are skipped with an explicit audit log.
- **`WslDirect`:** Stdio tools execute directly inside the WSL Linux distribution using `wsl.exe -d <distro> --cd <linuxWorktreePath> <command> <args>`.
- **`BareHost`:** Spawns `serverDef.Command` directly on the host using `StdioClientTransportOptions`, subject to profile `SecurityTier` constraints.

---

## 3. Implementation Checklist & Deliverables

- [ ] Implement containerized stdio transport builder in `McpClientManager` extracting container metadata from `NightCodeForgeContainerExecutor`.
- [ ] Wire `StdioClientTransport` with interactive `docker exec -i` argument formatting (including `wsl.exe` wrapper when `UseWsl == true`) and `WslDirect` / `BareHost` dispatch.
- [ ] Enforce security gates: reject host stdio execution when `ContainerSandbox` is active and ensure `IMcpSession.DisposeAsync()` cleanly terminates stdio subprocesses.
- [ ] Add unit tests in `NightCodeForge.Agents.Tests` verifying `docker exec -i` argument generation, environment variable injection (`-e KEY=VALUE`), WSL wrapping, and sandbox isolation constraints.

