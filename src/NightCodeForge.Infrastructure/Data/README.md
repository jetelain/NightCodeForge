# NightCodeForge.Infrastructure.Data

> **Assembly:** `NightCodeForge.Infrastructure` | **Layer:** Infrastructure / Persistence  
> **Role:** Implements Entity Framework Core context, entity type configurations, SQLite pragmas, and settings persistence.

---

## 1. Core Responsibility

`NightCodeForge.Infrastructure.Data` manages data persistence via Entity Framework Core targeting SQLite. It maps domain aggregates to database tables, configures relationships and indexes, enforces Write-Ahead Logging (`WAL`), and provides design-time factory support for automated `dotnet ef` migrations.

## 2. Key Types & Contracts

| Type | Kind | Description |
| :--- | :--- | :--- |
| `AppDbContext` | Class | Main EF Core `DbContext` with DbSets for tasks, workspaces, workflows, checkpoints, and questions. |
| `AppDbContextFactory` | Class | `IDesignTimeDbContextFactory<AppDbContext>` enabling `dotnet ef migrations` CLI tooling. |
| `GlobalOptimizationSettingsService` | Class | Reads and updates system optimization settings stored in `SystemSettings`. |
| `WebSearchSettingsService` | Class | Persists and retrieves web search engine configuration keys. |

## 3. Dependencies & Interaction Graph

- **Inbound (Who uses this):** Repositories, `StartupRecoveryService`, `TaskOrchestrator`, `NightCodeForge.Web`.
- **Outbound (What this relies on):** `NightCodeForge.Core.Domain`, `Microsoft.EntityFrameworkCore.Sqlite`.

## 4. Invariants & Architectural Rules

- **Rule 1 (WAL Mode Enforcement):** `EnsureDatabaseCreatedAndWalAsync` applies migrations via `Database.MigrateAsync()` and executes `PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;`.
- **Rule 2 (No Untracked Concurrency):** Multiple threads must not reuse the same `AppDbContext` instance; all calls must use scoped or factory-created contexts.
- **Rule 3 (Strict Relational Integrity):** Foreign keys are enabled (`PRAGMA foreign_keys=ON;`), cascade deletes are explicitly defined, and orphan records are prevented.

## 5. Usage Example

```csharp
// Applying migrations and setting WAL pragma at startup
await using var context = dbContextFactory.CreateDbContext();
await context.Database.MigrateAsync(cancellationToken);
await context.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;", cancellationToken);
```
