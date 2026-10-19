# NightCodeForge Test Architecture & Test Suite

> **Assembly:** Test Projects (`tests/*`) | **Layer:** Testing & Quality Assurance  
> **Role:** Provides unit, integration, Bunit UI component, and end-to-end acceptance test coverage.

---

## 1. Core Responsibility

The NightCodeForge test suite validates correctness across all architectural layers. It is divided into fast in-memory unit tests, integration tests against native Git CLI and SQLite WAL databases, Bunit Razor component tests, and full end-to-end acceptance tests utilizing synthetic repositories and headless Playwright browser automation.

## 2. Test Project Taxonomy

| Project | Kind | Focus Areas |
| :--- | :--- | :--- |
| `NightCodeForge.Core.Tests` | Unit Tests | Dependency resolution DAGs, cycle detection, workflow state machine, prompt rendering, cancellation coordination. |
| `NightCodeForge.Infrastructure.Tests` | Integration Tests | EF Core schema migrations, SQLite WAL concurrency, native Git CLI worktrees, process runner buffering, Hangfire queues. |
| `NightCodeForge.Agents.Tests` | Unit & Integration | Crucible agent loops, tool dispatch repair, circuit breaking, `<think>` compaction, ACP/PiRpc protocol serialization. |
| `NightCodeForge.Web.Tests` | Component & E2E | Bunit Razor component rendering, SignalR hub client synchronization, Playwright browser E2E acceptance tests. |

## 3. Test Categories & Execution Filters

Per repository policy in [AGENTS.md](../AGENTS.md), tests are tagged with traits to enable fast feedback during active development:

- `Category=Integration`: Validates concrete infrastructure (Git, SQLite, Process execution).
- `Category=Acceptance`: End-to-end acceptance tests validating full user and feature workflows.
- `Category=E2E`: Long-running Playwright browser tests and multi-stage lifecycle simulations.

### Fast Local Development (Excluding E2E)
Unless explicitly instructed to run E2E, execute fast unit and integration tests:
```powershell
dotnet test --filter "Category!=Acceptance&Category!=E2E"
```

### Full Regression & Hardening (Including E2E)
When concluding a milestone or verifying release readiness:
```powershell
dotnet test
```

## 4. Invariants & Testing Rules

- **Rule 1 (Synthetic Workspaces):** E2E and integration tests must create ephemeral synthetic workspaces with IDs > 1,000,000 or clean temporary folders to avoid polluting developer workspaces.
- **Rule 2 (No Silent Test Failures):** Tests must assert explicit outcomes; catching exceptions and asserting true without verifying error types is strictly forbidden.
- **Rule 3 (Strict Test Isolation):** Tests must not rely on shared static state or execution order; each test creates its own scoped DbContext and temporary Git worktree.

## 5. Usage Example

```powershell
# Run only Core unit tests
dotnet test tests/NightCodeForge.Core.Tests

# Run only Infrastructure integration tests
dotnet test tests/NightCodeForge.Infrastructure.Tests --filter "Category=Integration"

# Run all tests with detailed verbosity
dotnet test --logger "console;verbosity=normal"
```
