using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Infrastructure.Data;
using Xunit;

namespace NightCodeForge.Infrastructure.Tests;

public class DatabaseMigrationTests : IDisposable
{
    private readonly string _tempDbPath;
    private readonly SqliteConnection _inMemoryConnection;

    public DatabaseMigrationTests()
    {
        _tempDbPath = Path.Combine(Path.GetTempPath(), $"NCF_MigrationTest_{Guid.NewGuid():N}.db");
        _inMemoryConnection = new SqliteConnection("DataSource=:memory:");
        _inMemoryConnection.Open();
    }

    public void Dispose()
    {
        _inMemoryConnection.Dispose();
        SqliteConnection.ClearAllPools();

        try
        {
            if (File.Exists(_tempDbPath))
            {
                File.Delete(_tempDbPath);
            }

            var shm = $"{_tempDbPath}-shm";
            if (File.Exists(shm))
            {
                File.Delete(shm);
            }

            var wal = $"{_tempDbPath}-wal";
            if (File.Exists(wal))
            {
                File.Delete(wal);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    [Fact]
    public async Task EnsureDatabaseCreatedAndWalAsync_AppliesInitialCreateMigration_AndPopulatesHistoryTable()
    {
        // Arrange
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_inMemoryConnection)
            .Options;

        using var context = new AppDbContext(options);

        // Act
        await context.EnsureDatabaseCreatedAndWalAsync();

        // Assert - EF Core migration API checks
        var appliedMigrations = (await context.Database.GetAppliedMigrationsAsync()).ToList();
        var pendingMigrations = (await context.Database.GetPendingMigrationsAsync()).ToList();

        Assert.NotEmpty(appliedMigrations);
        Assert.Contains("20261019025958_InitialCreate", appliedMigrations);
        Assert.Contains("20261021065652_AddPortalScopingAndExecutionFlags", appliedMigrations);
        Assert.Empty(pendingMigrations);

        // Assert - Query physical __EFMigrationsHistory table directly
        using var command = _inMemoryConnection.CreateCommand();
        command.CommandText = "SELECT MigrationId, ProductVersion FROM __EFMigrationsHistory;";
        using var reader = await command.ExecuteReaderAsync();

        var historyRows = new List<(string MigrationId, string ProductVersion)>();
        while (await reader.ReadAsync())
        {
            historyRows.Add((reader.GetString(0), reader.GetString(1)));
        }

        Assert.Equal(2, historyRows.Count);
        Assert.Contains(historyRows, h => h.MigrationId == "20261019025958_InitialCreate");
        Assert.Contains(historyRows, h => h.MigrationId == "20261021065652_AddPortalScopingAndExecutionFlags");
    }

    [Fact]
    public async Task EnsureDatabaseCreatedAndWalAsync_EnablesWalAndOptimalPragmas_OnDiskDatabase()
    {
        // Arrange
        var connectionString = $"Data Source={_tempDbPath};";
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connectionString)
            .Options;

        using var context = new AppDbContext(options);

        // Act
        await context.EnsureDatabaseCreatedAndWalAsync();

        // Assert pragmas
        using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode;";
            var journalMode = (string?)await cmd.ExecuteScalarAsync();
            Assert.Equal("wal", journalMode?.ToLowerInvariant());
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA synchronous;";
            var synchronous = Convert.ToInt64(await cmd.ExecuteScalarAsync());
            // In SQLite: 0 = OFF, 1 = NORMAL, 2 = FULL, 3 = EXTRA
            Assert.Equal(1, synchronous);
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA foreign_keys;";
            var foreignKeys = Convert.ToInt64(await cmd.ExecuteScalarAsync());
            Assert.Equal(1, foreignKeys);
        }
    }

    [Fact]
    public async Task EnsureDatabaseCreatedAndWalAsync_CreatesAllSchemaTables_QueryableAndFunctional()
    {
        // Arrange
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_inMemoryConnection)
            .Options;

        using var context = new AppDbContext(options);
        await context.EnsureDatabaseCreatedAndWalAsync();

        // Act & Assert - verify all 14 DbSets are queryable and empty initially
        Assert.Equal(0, await context.Workspaces.CountAsync());
        Assert.Equal(0, await context.AgentProfiles.CountAsync());
        Assert.Equal(0, await context.Tasks.CountAsync());
        Assert.Equal(0, await context.TaskDependencies.CountAsync());
        Assert.Equal(0, await context.Questions.CountAsync());
        Assert.Equal(0, await context.StageCheckpoints.CountAsync());
        Assert.Equal(0, await context.StageExecutionRecords.CountAsync());
        Assert.Equal(0, await context.ExecutionLogs.CountAsync());
        Assert.Equal(0, await context.Conversations.CountAsync());
        Assert.Equal(0, await context.ConversationTurns.CountAsync());
        Assert.Equal(0, await context.SystemSettings.CountAsync());
        Assert.Equal(0, await context.WorkflowDefinitions.CountAsync());
        Assert.Equal(0, await context.StageDefinitions.CountAsync());
        Assert.Equal(0, await context.TaskHistories.CountAsync());

        // Insert and verify entities across key tables
        var ws = new Workspace
        {
            Name = "Migration Test Workspace",
            RepositoryPath = "/test/repo",
            DefaultBaseBranch = "main"
        };
        context.Workspaces.Add(ws);
        await context.SaveChangesAsync();

        var wf = new WorkflowDefinition
        {
            Name = "Migration Test Workflow",
            Code = "mig-test-wf",
            WorkspaceId = ws.Id
        };
        context.WorkflowDefinitions.Add(wf);

        var stage = new StageDefinition
        {
            Workflow = wf,
            StageId = "stage-test",
            DisplayName = "Test Stage",
            SequenceOrder = 1,
            Kind = WorkflowStageKind.Agentic,
            CsharpImplementationType = "test",
            ConfigurationJson = "{}"
        };
        context.StageDefinitions.Add(stage);

        var task = new ForgeTask
        {
            WorkspaceId = ws.Id,
            Workflow = wf,
            Title = "Migrated Task",
            BaseBranch = "main"
        };
        context.Tasks.Add(task);
        await context.SaveChangesAsync();

        var history = new ForgeTaskHistory
        {
            TaskId = task.Id,
            Version = 1,
            Title = task.Title,
            Description = "Initial",
            Status = task.Status,
            Priority = 1,
            MergeMode = task.MergeMode,
            BaseBranch = task.BaseBranch,
            SerializedDependencies = "[]",
            ChangedAt = DateTime.UtcNow,
            ChangedBy = "system"
        };
        context.TaskHistories.Add(history);

        var setting = new SystemSetting
        {
            Key = "TestSettingKey",
            Value = "Enabled",
            UpdatedAt = DateTimeOffset.UtcNow
        };
        context.SystemSettings.Add(setting);
        await context.SaveChangesAsync();

        Assert.True(ws.Id > 0);
        Assert.True(wf.Id > 0);
        Assert.True(stage.Id > 0);
        Assert.True(task.Id > 0);
        Assert.True(history.Id > 0);
        Assert.Equal("Enabled", (await context.SystemSettings.FindAsync("TestSettingKey"))?.Value);
    }

    [Fact]
    public async Task EnsureDatabaseCreatedAndWalAsync_IsIdempotent_WhenCalledMultipleTimes()
    {
        // Arrange
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_inMemoryConnection)
            .Options;

        using var context = new AppDbContext(options);

        // Act - call twice
        await context.EnsureDatabaseCreatedAndWalAsync();
        var ex = await Record.ExceptionAsync(() => context.EnsureDatabaseCreatedAndWalAsync());

        // Assert
        Assert.Null(ex);

        var appliedMigrations = (await context.Database.GetAppliedMigrationsAsync()).ToList();
        Assert.Equal(2, appliedMigrations.Count);
        Assert.Contains("20261019025958_InitialCreate", appliedMigrations);
        Assert.Contains("20261021065652_AddPortalScopingAndExecutionFlags", appliedMigrations);
    }

    [Fact]
    public async Task EnsureDatabaseCreatedAndWalAsync_BaselinesExistingPreMigrationDatabase_WithoutError()
    {
        // Arrange - Simulate a database previously initialized with EnsureCreatedAsync
        using var preMigrationConnection = new SqliteConnection("DataSource=:memory:");
        await preMigrationConnection.OpenAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(preMigrationConnection)
            .Options;

        // Create tables using EnsureCreatedAsync (bypassing migrations, like early dev)
        using (var initialContext = new AppDbContext(options))
        {
            await initialContext.Database.EnsureCreatedAsync();

            initialContext.Workspaces.Add(new Workspace
            {
                Name = "PreMigrationWorkspace",
                RepositoryPath = "/pre/migration",
                DefaultBaseBranch = "main"
            });
            await initialContext.SaveChangesAsync();
        }

        // Verify __EFMigrationsHistory does not exist yet
        using (var checkCmd = preMigrationConnection.CreateCommand())
        {
            checkCmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '__EFMigrationsHistory';";
            var count = Convert.ToInt64(await checkCmd.ExecuteScalarAsync());
            Assert.Equal(0, count);
        }

        // Act - Call EnsureDatabaseCreatedAndWalAsync()
        using (var migratedContext = new AppDbContext(options))
        {
            var ex = await Record.ExceptionAsync(() => migratedContext.EnsureDatabaseCreatedAndWalAsync());
            Assert.Null(ex);

            // Assert - migration recorded and existing data preserved
            var applied = (await migratedContext.Database.GetAppliedMigrationsAsync()).ToList();
            Assert.Contains(AppDbContext.InitialCreateMigrationId, applied);

            var loadedWs = await migratedContext.Workspaces.FirstOrDefaultAsync(w => w.Name == "PreMigrationWorkspace");
            Assert.NotNull(loadedWs);
        }
    }
}
