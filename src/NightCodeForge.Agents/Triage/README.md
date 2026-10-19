# NightCodeForge.Agents.Triage

> **Assembly:** `NightCodeForge.Agents` | **Layer:** Agents / Security & Triage  
> **Role:** Implements untrusted intake evaluation and security triage in restricted read-only environments.

---

## 1. Core Responsibility

`NightCodeForge.Agents.Triage` safely ingests external bug reports, customer issues, and untrusted specifications. It runs an agent session within a restricted read-only execution tier to explore the base branch, attempt reproduction, assess validity, and synthesize structured backlog tasks with `Suggested` status awaiting human triage approval.

## 2. Key Types & Contracts

| Type | Kind | Description |
| :--- | :--- | :--- |
| `TriageService` | Class | Implements `ITriageService`, orchestrating sandboxed triage sessions for external issue reports. |
| `TriageReportRequest` | Class | Request payload carrying external title, raw description, reporter metadata, and target branch. |

## 3. Dependencies & Interaction Graph

- **Inbound (Who uses this):** Web API endpoints receiving external webhooks/issues, `NightCodeForge.ScenarioRunner`.
- **Outbound (What this relies on):** `Core.Repositories.ITaskRepository`, `Core.Services.IHarnessRegistry`, `ReadOnlyCommandValidator`.

## 4. Invariants & Architectural Rules

- **Rule 1 (Restricted Sandbox):** Triage agents must run with strictly read-only tools and sandboxed command execution; filesystem mutations and network exfiltration are forbidden.
- **Rule 2 (Suggested State):** Created tasks must enter the backlog in `Suggested` status; they cannot execute automatically without human approval.
- **Rule 3 (Fail Fast Validation):** Invalid or missing workspace definitions must throw domain exceptions rather than proceeding with synthetic defaults.

## 5. Usage Example

```csharp
// Processing an external bug report through triage
var proposedTasks = await triageService.TriageExternalReportAsync(
    workspace,
    new TriageReportRequest { Title = "Memory leak in hub connections", Description = "..." },
    cancellationToken);
```
