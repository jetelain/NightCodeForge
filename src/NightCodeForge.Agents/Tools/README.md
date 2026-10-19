# NightCodeForge.Agents.Tools

> **Assembly:** `NightCodeForge.Agents` | **Layer:** Agents / Tool Execution  
> **Role:** Implements the canonical minimalist tool set (`read`, `edit`, `write`, `bash`, `ask`, `propose_tasks`) and command safety validators.

---

## 1. Core Responsibility

`NightCodeForge.Agents.Tools` provides the standard tool execution library available to AI agents. It follows the minimalist tool philosophy, equipping agents with atomic primitives: reading files with line offsets, surgical text replacement with uniqueness checks (`edit`), file creation (`write`), isolated shell execution (`bash`), web searches, and human interruption prompts (`ask`).

## 2. Key Types & Contracts

| Type | Kind | Description |
| :--- | :--- | :--- |
| `CanonicalTools` | Class | Contains the core implementation of canonical agent tools (`read`, `edit`, `write`, `bash`, `ask`). |
| `CanonicalAIFunctionFactory` | Class | Converts tool methods into `AIFunction` objects for `Microsoft.Extensions.AI`. |
| `ReadOnlyCommandValidator` | Class | Validates shell commands against a whitelist in read-only stages (e.g. Planning, Triage). |
| `IWebSearchService` | Interface | Contract for executing external documentation and web searches. |
| `OllamaWebSearchService` | Class | Implementation communicating with configured search providers. |

## 3. Dependencies & Interaction Graph

- **Inbound (Who uses this):** `CrucibleAgentHarness`, `Dispatcher`, `IdeationService`, `TriageService`.
- **Outbound (What this relies on):** `NightCodeForge.Core.Execution.IExecutionEnvironment`, `NightCodeForge.Core.Workflow.HumanInterruptionException`.

## 4. Invariants & Architectural Rules

- **Rule 1 (Surgical Edit Uniqueness):** The `edit` tool requires the target string to match uniquely within the file; ambiguous matches must return an error instructing the agent to provide more surrounding context.
- **Rule 2 (Read-Only Enforcement):** In read-only stages, any mutating tool call or blacklisted shell command must be intercepted and rejected immediately.
- **Rule 3 (Human Question Pausing):** Invocations of the `ask` tool must pause execution and transition the task to `Paused` awaiting human input.

## 5. Usage Example

```csharp
// Exposing canonical tools as AIFunction instances to an agent
var tools = toolFactory.CreateTools(sessionContext, allowedToolNames: ["read", "edit", "bash"]);
var chatOptions = new ChatOptions { Tools = tools };
```
