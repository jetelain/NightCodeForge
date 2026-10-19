# NightCodeForge.Agents.Mcp

> **Assembly:** `NightCodeForge.Agents` | **Layer:** Agents / Protocol Integration  
> **Role:** Discovers and integrates external Model Context Protocol (MCP) servers and tools.

---

## 1. Core Responsibility

`NightCodeForge.Agents.Mcp` discovers, configures, and manages external Model Context Protocol (MCP) servers. It parses workspace configuration files (`mcp_config.json`, `.nightcodeforge/mcp_config.json`, `.agents/mcp_config.json`), providing agent harnesses with dynamic access to external MCP tools and context providers.

## 2. Key Types & Contracts

| Type | Kind | Description |
| :--- | :--- | :--- |
| `IMcpClientManager` | Interface | Contract for discovering configured MCP servers and exporting definitions to harnesses. |
| `McpClientManager` | Class | Discovers and deserializes MCP server configuration files in workspaces. |
| `McpServerDefinition` | Class | Configuration record defining server command, arguments, environment variables, or remote URL. |
| `McpConfigurationFile` | Class | Data structure representing the root `mcpServers` JSON object. |

## 3. Dependencies & Interaction Graph

- **Inbound (Who uses this):** `AcpAgentHarness`, `CrucibleAgentHarness`.
- **Outbound (What this relies on):** `System.Text.Json`, `NightCodeForge.Core.Domain.Workspace`.

## 4. Invariants & Architectural Rules

- **Rule 1 (Config Discovery Precedence):** Search for MCP configurations in `mcp_config.json`, then `.nightcodeforge/mcp_config.json`, then `.agents/mcp_config.json`.
- **Rule 2 (Fail Safe on Malformed JSON):** Corrupted MCP configuration files must log a warning and return empty configurations rather than crashing agent startup.
- **Rule 3 (Environment Variable Isolation):** Secrets declared in `env` blocks must only be injected into the specific child process running that MCP server.

## 5. Usage Example

```csharp
// Discovering configured MCP servers in a workspace
var servers = await mcpManager.GetConfiguredServersAsync(workspace.RootPath, cancellationToken);
foreach (var (name, def) in servers)
{
    logger.LogInformation("Loaded MCP server '{Name}' with command '{Command}'", name, def.Command);
}
```
