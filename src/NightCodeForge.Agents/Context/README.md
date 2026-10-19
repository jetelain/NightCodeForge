# NightCodeForge.Agents.Context

> **Assembly:** `NightCodeForge.Agents` | **Layer:** Agents / Context Management  
> **Role:** Ingests repository policy documents (`AGENTS.md`), manages system prompts, and bounds context windows.

---

## 1. Core Responsibility

`NightCodeForge.Agents.Context` ensures agents are grounded in repository guidelines and conventions. It discovers and parses `AGENTS.md` files from the workspace root or parent folders, compiles them into static system instructions, and manages context budget limits for agent reasoning sessions.

## 2. Key Types & Contracts

| Type | Kind | Description |
| :--- | :--- | :--- |
| `AgentsMdService` | Class | Locates and loads `AGENTS.md` content from workspace directories, caching policies for prompt assembly. |

## 3. Dependencies & Interaction Graph

- **Inbound (Who uses this):** `CrucibleAgentHarness`, `DefaultBootstrapPromptBuilder`, `IdeationService`.
- **Outbound (What this relies on):** `System.IO`, `NightCodeForge.Core.Domain.Workspace`.

## 4. Invariants & Architectural Rules

- **Rule 1 (Top-Level Precedence):** Repository-level `AGENTS.md` rules take strict precedence over default agent behaviors.
- **Rule 2 (Cache-Safe Formatting):** Loaded markdown rules must be appended deterministically to maintain prompt prefix caching.
- **Rule 3 (Graceful Omission):** If no `AGENTS.md` is found, the system proceeds with standard baseline conventions without error.

## 5. Usage Example

```csharp
// Loading repository instructions for a workspace
var agentsRules = await agentsMdService.GetAgentsRulesAsync(workspace.RootPath, cancellationToken);
if (!string.IsNullOrEmpty(agentsRules))
{
    systemPromptBuilder.AppendSection("Repository Guidelines", agentsRules);
}
```
