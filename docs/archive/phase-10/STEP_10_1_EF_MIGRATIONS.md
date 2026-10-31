# 🗄️ Step 10.1: Production EF Core Migrations & Tooling

> **Phase 10 Implementation Step**  
> *Parent Plan:* [Phase 10 Master Plan](../10_MVP_FINAL_POLISH.md) | *Source Policy:* [AGENTS.md](../../../AGENTS.md)  
> *Status:* Completed | *Target Framework:* .NET 10 (C# 13)  

---

## 1. Objective

Transition NightCodeForge from initial development schema generation (`Database.EnsureCreatedAsync()`) to production-ready Entity Framework Core Migrations (`Database.MigrateAsync()`). Enable smooth CLI tooling via `dotnet ef` for both the startup project (`NightCodeForge.Web`) and migration assembly (`NightCodeForge.Infrastructure`), while guaranteeing automatic schema migration, SQLite WAL pragmas, and workspace sequence initialization at runtime.

---

## 2. Technical Scope & Architecture

### Current State
- `NightCodeForge.Infrastructure/Migrations/` currently contains `20261019025958_InitialCreate.cs` and `AppDbContextModelSnapshot.cs`.
- `AppDbContextFactory` is implemented in `NightCodeForge.Infrastructure.Data`.
- `NightCodeForge.Web.csproj` lacks `Microsoft.EntityFrameworkCore.Design`, causing `dotnet ef migrations ... --startup-project src/NightCodeForge.Web` to fail.
- `EnsureDatabaseCreatedAndWalAsync()` in `AppDbContext.cs` executes `await Database.EnsureCreatedAsync(cancellationToken);`, which bypasses the migration pipeline and fails to record migrations in `__EFMigrationsHistory`.

### Target Architecture
1. **Design-Time Tooling:**
   - `NightCodeForge.Web.csproj` references `Microsoft.EntityFrameworkCore.Design` with `PrivateAssets="all"`.
   - Running `dotnet ef migrations list`, `dotnet ef migrations add <Name>`, and `dotnet ef database update` works against `src/NightCodeForge.Web` without error.
2. **Runtime Migration & PRAGMA Lifecycle:**
   - In `AppDbContext.cs`, replace `Database.EnsureCreatedAsync(cancellationToken)` with `Database.MigrateAsync(cancellationToken)`.
   - SQLite WAL pragmas (`PRAGMA journal_mode = WAL;`, `PRAGMA synchronous = NORMAL;`, `PRAGMA foreign_keys = ON;`) and workspace seed sequence initialization execute immediately after `MigrateAsync()`.
   - In-memory SQLite connections (`DataSource=:memory:`) used in test suites cleanly execute `Database.MigrateAsync()` on open connections.
3. **Verification & Tests:**
   - Verify that all existing unit and integration tests pass without regression.
   - Introduce an automated test verifying migration application and `__EFMigrationsHistory` population.

---

## 3. Subagent Action Breakdown

### Subagent Action 1.1: Project Configuration & CLI Tooling Setup
- **Target Files:**
  - `src/NightCodeForge.Web/NightCodeForge.Web.csproj`
  - `src/NightCodeForge.Infrastructure/Data/AppDbContextFactory.cs`
- **Tasks:**
  1. Add `Microsoft.EntityFrameworkCore.Design` (matching EF Core version `10.0.12` or latest) to `NightCodeForge.Web.csproj`:
     ```xml
     <PackageReference Include="Microsoft.EntityFrameworkCore.Design" Version="10.0.12">
       <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
       <PrivateAssets>all</PrivateAssets>
     </PackageReference>
     ```
  2. Verify CLI execution succeeds:
     ```powershell
     dotnet ef migrations list --project src/NightCodeForge.Infrastructure --startup-project src/NightCodeForge.Web
     dotnet ef migrations has-pending-model-changes --project src/NightCodeForge.Infrastructure --startup-project src/NightCodeForge.Web
     ```
- **Acceptance Criteria:** Both CLI commands return exit code 0 with no build or design-time errors.

---

### Subagent Action 1.2: Runtime Migration Application (`Database.MigrateAsync`)
- **Target Files:**
  - `src/NightCodeForge.Infrastructure/Data/AppDbContext.cs`
  - `src/NightCodeForge.Web/Program.cs`
  - `src/NightCodeForge.ScenarioRunner/Environment/SyntheticEnvironment.cs`
- **Tasks:**
  1. Update `EnsureDatabaseCreatedAndWalAsync(CancellationToken cancellationToken = default)` in `AppDbContext.cs`:
     - Replace:
       ```csharp
       await Database.EnsureCreatedAsync(cancellationToken);
       ```
     - With:
       ```csharp
       await Database.MigrateAsync(cancellationToken);
       ```
  2. Preserve and verify SQLite pragmas:
     ```csharp
     if (Database.IsSqlite())
     {
         await Database.ExecuteSqlRawAsync("PRAGMA journal_mode = WAL;", cancellationToken);
         await Database.ExecuteSqlRawAsync("PRAGMA synchronous = NORMAL;", cancellationToken);
         await Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;", cancellationToken);

         await ApplyWorkspaceIdSeedIfConfiguredAsync(cancellationToken);
     }
     ```
  3. Validate that `Program.cs` and `SyntheticEnvironment.cs` cleanly apply migrations on startup.
- **Acceptance Criteria:** App starts up and applies migrations cleanly without schema mismatches or unhandled exceptions.

---

### Subagent Action 1.3: Migration Verification & Automated Integration Tests
- **Target Files:**
  - `tests/NightCodeForge.Infrastructure.Tests/AppDbContextTests.cs`
  - (New) `tests/NightCodeForge.Infrastructure.Tests/DatabaseMigrationTests.cs`
- **Tasks:**
  1. Create `DatabaseMigrationTests.cs` using in-memory or temporary disk SQLite database:
     - Verify `await context.EnsureDatabaseCreatedAndWalAsync()` applies `InitialCreate`.
     - Query `__EFMigrationsHistory` to verify migration ID exists and matches the snapshot.
     - Validate that all tables (`Workspaces`, `ForgeTasks`, `WorkflowDefinitions`, `StageDefinitions`, etc.) are queryable.
  2. Run the full test suite:
     ```powershell
     dotnet test --filter "Category!=Acceptance&Category!=E2E"
     ```
- **Acceptance Criteria:** All unit and integration tests pass (100% green).
