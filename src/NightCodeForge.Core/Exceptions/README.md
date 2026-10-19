# NightCodeForge.Core.Exceptions

> **Assembly:** `NightCodeForge.Core` | **Layer:** Core / Domain Exceptions  
> **Role:** Defines strongly-typed domain and operational exceptions for explicit, fast-failing error handling.

---

## 1. Core Responsibility

`NightCodeForge.Core.Exceptions` defines the domain exception hierarchy used throughout the application. Adhering to the zero-silent-failures policy, these exceptions replace ambiguous generic exceptions (`Exception`, `InvalidOperationException`) and prevent synthetic fallbacks by conveying specific domain error conditions to higher layers and the user interface.

## 2. Key Types & Contracts

| Type | Kind | Description |
| :--- | :--- | :--- |
| `WorkflowNotFoundException` | Class | Thrown when a task references a workflow definition ID that does not exist in persistence. |
| `WorkflowExecutionException` | Class | Thrown when a pipeline stage or workflow execution encounters an unrecoverable failure. |
| `TaskConcurrencyException` | Class | Thrown when a lock acquisition times out or concurrent mutations conflict. |
| `BaseBranchNotFoundException` | Class | Thrown when a workspace base branch (e.g. `main`) cannot be located in the local or remote Git repository. |
| `FeatureBranchNotFoundException` | Class | Thrown when a task worktree branch cannot be resolved or checked out. |
| `AgentProfileNotFoundException` | Class | Thrown when a stage references an agent profile that is not configured. |
| `TaskMarkdownSyncException` | Class | Thrown when frontmatter or markdown structure in `tasks/TASK-*.md` fails parsing. |

## 3. Dependencies & Interaction Graph

- **Inbound (Who uses this):** All services, repositories, and orchestrators in `Core`, `Infrastructure`, `Agents`, and `Web`.
- **Outbound (What this relies on):** Standard `System.Exception`.

## 4. Invariants & Architectural Rules

- **Rule 1 (No Silent Failures):** Bare `catch { }` blocks that swallow these exceptions are strictly forbidden.
- **Rule 2 (Meaningful Messages):** All domain exceptions must include contextual identifiers (IDs, paths, branch names) to allow prompt root-cause diagnostics.
- **Rule 3 (HTTP / UI Mapping):** Web and API layers should catch these domain exceptions and translate them into appropriate HTTP status codes or UI toast notifications.

## 5. Usage Example

```csharp
// Guarding against missing workflow definition
var workflow = await workflowRepo.GetByIdAsync(workflowId, ct);
if (workflow == null)
{
    throw new WorkflowNotFoundException($"Workflow with ID '{workflowId}' does not exist.");
}
```
