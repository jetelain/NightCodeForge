# 📚 Step 10.2: Namespace & Module Documentation Architecture

> **Phase 10 Implementation Step**  
> *Parent Plan:* [Phase 10 Master Plan](../10_MVP_FINAL_POLISH.md) | *Source Policy:* [AGENTS.md](../../../AGENTS.md)  
> *Status:* Ready for Execution | *Target Framework:* .NET 10 (C# 13)  

---

## 1. Objective

Provide comprehensive, high-signal `README.md` files across all functional namespaces and projects in `src/` (and root `tests/`). This ensures both autonomous AI subagents and human software engineers can quickly understand the architectural responsibility, key types, dependencies, invariants, and usage patterns of any module without reading through dozens of C# source files or exhausting their LLM context windows.

---

## 2. Standardized Namespace README Template

Each namespace `README.md` must adhere to the following concise (~40–80 lines), structured template:

```markdown
# [Namespace Name]

> **Assembly:** `NightCodeForge.[Project]` | **Layer:** [Domain / Core / Infrastructure / Agents / UI]  
> **Role:** [One-sentence summary of the single responsibility of this namespace]

---

## 1. Core Responsibility
[2-3 sentences detailing what this module does, why it exists, and what problem it solves.]

## 2. Key Types & Contracts
| Type | Kind | Description |
| :--- | :--- | :--- |
| `IFooService` | Interface | Contract for executing foo operations. |
| `FooEntity` | Class / Record | Domain representation of foo. |
| `FooOptions` | Class | Strongly-typed configuration options. |

## 3. Dependencies & Interaction Graph
- **Inbound (Who uses this):** [Namespaces or services that consume this module]
- **Outbound (What this relies on):** [Namespaces, libraries, or external systems this calls]

## 4. Invariants & Architectural Rules
- **Rule 1:** [e.g. Fail fast: throw domain exception on missing records; zero synthetic fallbacks]
- **Rule 2:** [e.g. Concurrency: all methods must accept and propagate CancellationToken]
- **Rule 3:** [e.g. Thread safety: instances must be stateless or synchronized]

## 5. Usage Example
```csharp
// Idiomatic C# snippet showing standard usage (5-10 lines)
```
```

---

## 3. Subagent Action Breakdown

To prevent subagent context exhaustion, documentation generation is split across 5 bounded subagent actions:

### Subagent Action 2.1: `NightCodeForge.Core` Namespace Documentation
- **Scope:** Root project + 10 functional namespaces.
- **Target Files to Create/Update:**
  1. `src/NightCodeForge.Core/README.md` (Project overview, Clean Architecture layer role)
  2. `src/NightCodeForge.Core/Domain/README.md` (Domain entities: `ForgeTask`, `Workspace`, `WorkflowDefinition`, `StageDefinition`, `AgentProfile`, `Question`, etc.)
  3. `src/NightCodeForge.Core/Workflow/README.md` (Pipeline engine: `IWorkflowPipeline`, `IWorkflowStage`, `PipelineContext`, `PipelineDataBagKeys`, retry policies)
  4. `src/NightCodeForge.Core/Dependencies/README.md` (DAG engine: `TaskDependencyResolver`, cycle detection, topological sorting)
  5. `src/NightCodeForge.Core/Interfaces/README.md` (Primary service contracts: `IGitService`, `IAgentHarness`, `IOrchestratorEngine`, etc.)
  6. `src/NightCodeForge.Core/Repositories/README.md` (Data access abstractions: `IWorkspaceRepository`, `IForgeTaskRepository`, `IWorkflowRepository`)
  7. `src/NightCodeForge.Core/Services/README.md` (Core business logic: `ITaskOrchestrator`, `IStartupRecoveryService`, `ITaskMarkdownSyncService`)
  8. `src/NightCodeForge.Core/Concurrency/README.md` (Concurrency management: `WorkspaceLockManager`, task queue coordination)
  9. `src/NightCodeForge.Core/Execution/README.md` (Execution abstractions: `IExecutionEnvironment`, process runners, sandboxing models)
  10. `src/NightCodeForge.Core/Exceptions/README.md` (Domain exception hierarchy: `WorkflowNotFoundException`, `GitCommandException`, etc.)
  11. `src/NightCodeForge.Core/Streaming/README.md` (Real-time telemetry event streams, token delta delegates, stage lifecycle events)
- **Acceptance Criteria:** All 11 markdown files exist, follow the standard template, and pass markdown linting.

---

### Subagent Action 2.2: `NightCodeForge.Infrastructure` Namespace Documentation
- **Scope:** Root project + 7 functional namespaces.
- **Target Files to Create/Update:**
  1. `src/NightCodeForge.Infrastructure/README.md` (Infrastructure overview: EF Core, Git CLI, Hangfire, SignalR)
  2. `src/NightCodeForge.Infrastructure/Data/README.md` (`AppDbContext`, entity type configurations, SQLite pragmas, seeding)
  3. `src/NightCodeForge.Infrastructure/Git/README.md` (CliWrap Git execution: worktrees, WIP squash, mirror sync, diffs)
  4. `src/NightCodeForge.Infrastructure/Execution/README.md` (Concrete environments: `BareHostExecutionEnvironment`, `WslExecutionEnvironment`, `ContainerSandboxEnvironment`)
  5. `src/NightCodeForge.Infrastructure/BackgroundJobs/README.md` (Hangfire setup: single-worker queues `llm-urgent` & `llm-inference`)
  6. `src/NightCodeForge.Infrastructure/Hubs/README.md` (SignalR `OrchestratorHub`, `SignalRActivityBridgeService`)
  7. `src/NightCodeForge.Infrastructure/Inference/README.md` (Ollama router client, token estimation, endpoint routing)
  8. `src/NightCodeForge.Infrastructure/Migrations/README.md` (EF Core migrations history, snapshots, CLI migration commands)
- **Acceptance Criteria:** All 8 markdown files exist, follow the standard template, and correctly describe infrastructure mechanics.

---

### Subagent Action 2.3A: `NightCodeForge.Agents` (Core Harnesses, Inference & Tools)
- **Scope:** Root project + 7 agent execution namespaces.
- **Target Files to Create/Update:**
  1. `src/NightCodeForge.Agents/README.md` (Agents architecture: harness abstraction, multi-stage pipelines)
  2. `src/NightCodeForge.Agents/Crucible/README.md` (Microsoft Agent Framework harness implementation, session lifecycle)
  3. `src/NightCodeForge.Agents/Inference/README.md` (LLM client integration, model options, streaming response handling)
  4. `src/NightCodeForge.Agents/Tools/README.md` (Canonical minimalist tools: `read`, `edit`, `write`, `bash`, `web_search`, `ask`, `propose_tasks`)
  5. `src/NightCodeForge.Agents/Dispatcher/README.md` (Lenient tool dispatching: parameter casing normalization, auto-repair)
  6. `src/NightCodeForge.Agents/Context/README.md` (Agent context window budgeting, token estimators, history compaction)
  7. `src/NightCodeForge.Agents/Streaming/README.md` (Streaming token processors, SSE adapters)
  8. `src/NightCodeForge.Agents/Transcripts/README.md` (Disk-based execution transcripts, JSONL audit logs)
- **Acceptance Criteria:** All 8 markdown files exist and accurately capture the agent harness contracts.

---

### Subagent Action 2.3B: `NightCodeForge.Agents` (Protocols, Skills, Optimizations & Roles)
- **Scope:** 7 protocol and role-specialized namespaces.
- **Target Files to Create/Update:**
  1. `src/NightCodeForge.Agents/Acp/README.md` (Agent Client Protocol client & message exchange)
  2. `src/NightCodeForge.Agents/PiRpc/README.md` (Pi headless JSONL RPC daemon protocol & execution loop)
  3. `src/NightCodeForge.Agents/Mcp/README.md` (Model Context Protocol client integration)
  4. `src/NightCodeForge.Agents/Skills/README.md` (Autonomous skill loader and prompt enhancer)
  5. `src/NightCodeForge.Agents/Optimizations/README.md` (Local LLM speedups: `<think>` pruning, prompt cache stability, loop breakers)
  6. `src/NightCodeForge.Agents/Ideation/README.md` (Interactive codebase chat & task generation agent)
  7. `src/NightCodeForge.Agents/Triage/README.md` (Untrusted intake and security triage agent)
- **Acceptance Criteria:** All 7 markdown files exist, highlighting role-specific behaviors and constraints.

---

### Subagent Action 2.4: Web UI, Scenario Runner & Test Suite Documentation
- **Scope:** Presentation layer, scenario runner, and testing taxonomy.
- **Target Files to Create/Update:**
  1. `src/NightCodeForge.Web/README.md` (Blazor Web App overview: interactive server rendering, CSS design system, SignalR)
  2. `src/NightCodeForge.Web/Components/README.md` (Component architecture & domains: `Workspaces`, `DevQueue`, `Review`, `Monitoring`, `Settings`)
  3. `src/NightCodeForge.Web/Services/README.md` (Web scoped services: `WorkspaceState`, `ToastService`, `DevQueueService`, `TaskReviewService`)
  4. `src/NightCodeForge.ScenarioRunner/README.md` (Update/verify existing documentation for options, mock LLM, container sandbox)
  5. `tests/README.md` (Testing architecture: unit tests, Bunit component tests, integration tests, E2E acceptance tests, test categories & filters)
- **Acceptance Criteria:** All 5 documentation files accurately reflect the current frontend and testing architecture.
