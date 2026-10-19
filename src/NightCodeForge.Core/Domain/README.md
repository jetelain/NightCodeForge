# NightCodeForge.Core.Domain

> **Assembly:** `NightCodeForge.Core` | **Layer:** Domain  
> **Role:** Houses the enterprise entities, aggregates, enums, value objects, and domain constants.

---

## 1. Core Responsibility

`NightCodeForge.Core.Domain` defines the foundational business model of the application. It models tasks, workspaces, declarative workflows, execution checkpoints, human interaction questions, conversation records, and agent configuration profiles without coupling to persistence technologies or execution engines.

## 2. Key Types & Contracts

| Type | Kind | Description |
| :--- | :--- | :--- |
| `ForgeTask` | Class | Central aggregate root representing an autonomous engineering task with status, branch, dependencies, and iteration tracking. |
| `Workspace` | Class | Represents a Git-backed workspace with filesystem paths, default branch configurations, and execution environment specs. |
| `WorkflowDefinition` | Class | Declarative specification of an execution pipeline, listing ordered stage definitions. |
| `StageDefinition` | Class | Configuration for an individual workflow stage (harness ID, prompt template, quality gates, retry policies). |
| `AgentProfile` | Class | Configuration for an agent role (system prompt, harness, inference model name, tool whitelist). |
| `Question` | Class | Represents a human-in-the-loop interaction item paused awaiting developer input. |
| `StageExecutionRecord` | Class | Audit trail entry capturing execution status, duration, token usage, and diagnostic logs for a stage. |
| `TaskDependency` | Class | Directed edge in the task dependency DAG linking parent and dependent child tasks. |
| `CanonicalToolNames` | Static Class | Standard string constants for built-in tools (`read`, `edit`, `write`, `bash`, `ask`, etc.). |

## 3. Dependencies & Interaction Graph

- **Inbound (Who uses this):** Consumed across all layers: `Workflow`, `Repositories`, `Services`, `NightCodeForge.Infrastructure.Data` (EF Core mapping), `NightCodeForge.Agents`, and `NightCodeForge.Web`.
- **Outbound (What this relies on):** Pure .NET types and standard collection classes. No third-party or outer layer dependencies.

## 4. Invariants & Architectural Rules

- **Rule 1 (Rich Domain Validation):** Entity state transitions must be explicit; task status transitions (`Pending` -> `InProgress` -> `Completed` / `Failed`) must be validated.
- **Rule 2 (No Synthetic Fallbacks):** Entities must represent valid, existing system concepts. If an entity is absent, repositories or services throw domain exceptions rather than synthesizing stub records.
- **Rule 3 (Constant Integrity):** System-wide stage IDs, harness IDs, and tool names must reference domain constants (`CanonicalToolNames`, `StandardHarnessIds`, `StandardStageIds`).

## 5. Usage Example

```csharp
// Creating a new task aggregate linked to a workspace
var task = new ForgeTask
{
    WorkspaceId = workspace.Id,
    Title = "Implement OAuth2 PKCE Flow",
    Description = "Add PKCE support for desktop client authentication",
    Status = ForgeTaskStatus.Pending,
    BranchName = "feature/task-42-pkce",
    WorkflowDefinitionId = standardWorkflow.Id,
    CreatedAt = DateTimeOffset.UtcNow
};
```
