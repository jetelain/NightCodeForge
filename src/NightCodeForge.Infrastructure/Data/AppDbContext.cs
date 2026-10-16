using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NightCodeForge.Core.Domain;

namespace NightCodeForge.Infrastructure.Data;

/// <summary>
/// Entity Framework Core database context for NightCodeForge targeting SQLite with WAL mode.
/// </summary>
public class AppDbContext : DbContext
{
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<AgentProfile> AgentProfiles => Set<AgentProfile>();
    public DbSet<ForgeTask> Tasks => Set<ForgeTask>();
    public DbSet<TaskDependency> TaskDependencies => Set<TaskDependency>();
    public DbSet<Question> Questions => Set<Question>();
    public DbSet<StageCheckpoint> StageCheckpoints => Set<StageCheckpoint>();
    public DbSet<StageExecutionRecord> StageExecutionRecords => Set<StageExecutionRecord>();
    public DbSet<ExecutionLog> ExecutionLogs => Set<ExecutionLog>();
    public DbSet<StageConversation> StageConversations => Set<StageConversation>();
    public DbSet<StageConversationTurn> StageConversationTurns => Set<StageConversationTurn>();
    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // 1. Workspace
        modelBuilder.Entity<Workspace>(b =>
        {
            b.HasKey(w => w.Id);
            b.Property(w => w.Name).IsRequired().HasMaxLength(256);
            b.Property(w => w.RepositoryPath).IsRequired().HasMaxLength(1024);
            b.Property(w => w.DefaultBaseBranch).IsRequired().HasMaxLength(128);
            b.Property(w => w.ExecutionMode).HasConversion<int>();
            b.Property(w => w.ContainerImage).HasMaxLength(512);
            b.Property(w => w.ContainerName).HasMaxLength(256);
            b.Property(w => w.Ext4MirrorPath).HasMaxLength(1024);
            b.Property(w => w.UseWslForContainers).HasDefaultValue(false);
            b.Property(w => w.WslDistribution).HasMaxLength(128);
            b.Property(w => w.BuildTimeoutSeconds);
            b.Property(w => w.TestTimeoutSeconds);

            b.HasMany(w => w.Tasks)
                .WithOne(i => i.Workspace)
                .HasForeignKey(i => i.WorkspaceId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasMany(w => w.AgentProfiles)
                .WithOne(p => p.Workspace)
                .HasForeignKey(p => p.WorkspaceId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // 2. AgentProfile
        var optFlagsConverter = new ValueConverter<LocalOptimizationFlags?, string?>(
            v => v == null ? null : JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
            v => string.IsNullOrWhiteSpace(v) ? null : JsonSerializer.Deserialize<LocalOptimizationFlags>(v, (JsonSerializerOptions?)null));

        var optFlagsComparer = new ValueComparer<LocalOptimizationFlags?>(
            (c1, c2) => JsonSerializer.Serialize(c1, (JsonSerializerOptions?)null) == JsonSerializer.Serialize(c2, (JsonSerializerOptions?)null),
            c => c == null ? 0 : JsonSerializer.Serialize(c, (JsonSerializerOptions?)null).GetHashCode(),
            c => c == null ? null : JsonSerializer.Deserialize<LocalOptimizationFlags>(JsonSerializer.Serialize(c, (JsonSerializerOptions?)null), (JsonSerializerOptions?)null));

        var permittedToolsConverter = new ValueConverter<IReadOnlyList<string>?, string?>(
            v => v == null ? null : JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
            v => string.IsNullOrWhiteSpace(v) ? null : JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null));

        var permittedToolsComparer = new ValueComparer<IReadOnlyList<string>?>(
            (c1, c2) => (c1 == null && c2 == null) || (c1 != null && c2 != null && c1.SequenceEqual(c2)),
            c => c == null ? 0 : c.Aggregate(0, (a, v) => HashCode.Combine(a, v.GetHashCode())),
            c => c == null ? null : c.ToList().AsReadOnly());

        var stageDirectivesConverter = new ValueConverter<Dictionary<string, string>?, string?>(
            v => v == null ? null : JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
            v => string.IsNullOrWhiteSpace(v) ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(v, (JsonSerializerOptions?)null));

        var stageDirectivesComparer = new ValueComparer<Dictionary<string, string>?>(
            (c1, c2) => JsonSerializer.Serialize(c1, (JsonSerializerOptions?)null) == JsonSerializer.Serialize(c2, (JsonSerializerOptions?)null),
            c => c == null ? 0 : JsonSerializer.Serialize(c, (JsonSerializerOptions?)null).GetHashCode(),
            c => c == null ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(JsonSerializer.Serialize(c, (JsonSerializerOptions?)null), (JsonSerializerOptions?)null));

        modelBuilder.Entity<AgentProfile>(b =>
        {
            b.HasKey(p => p.Id);
            b.Property(p => p.Id).HasMaxLength(128);
            b.Property(p => p.Name).IsRequired().HasMaxLength(256);
            b.Property(p => p.HarnessId).IsRequired().HasMaxLength(64);
            b.Property(p => p.ModelName).IsRequired().HasMaxLength(256);
            b.Property(p => p.ThinkingEffort).HasConversion<int>();
            b.Property(p => p.Mode).HasConversion<int>();
            b.Property(p => p.SecurityTier).HasConversion<int>();
            b.Property(p => p.RoutingTarget).HasConversion<int>().HasDefaultValue(OllamaRoutingTarget.Auto);

            b.Property(p => p.OptimizationFlags)
                .HasConversion(optFlagsConverter)
                .Metadata.SetValueComparer(optFlagsComparer);

            b.Property(p => p.PermittedTools)
                .HasConversion(permittedToolsConverter)
                .Metadata.SetValueComparer(permittedToolsComparer);

            b.Property(p => p.StageDirectives)
                .HasConversion(stageDirectivesConverter)
                .Metadata.SetValueComparer(stageDirectivesComparer);
        });

        // 3. ForgeTask
        modelBuilder.Entity<ForgeTask>(b =>
        {
            b.HasKey(i => i.Id);
            b.Property(i => i.Title).IsRequired().HasMaxLength(512);
            b.Property(i => i.Status).HasConversion<int>();
            b.Property(i => i.MergeMode).HasConversion<int>();
            b.Property(i => i.IsWaitingForHuman).HasDefaultValue(false);
            b.Property(i => i.InterruptedAgentProfileId).HasMaxLength(128);
            b.Property(i => i.InterruptedStageId).HasMaxLength(128);
            b.Property(i => i.CurrentStageId).HasMaxLength(128);
            b.Property(i => i.BaseBranch).IsRequired().HasMaxLength(128);
            b.Property(i => i.FeatureBranch).HasMaxLength(256);
            b.Property(i => i.LastHangfireJobId).HasMaxLength(128);
            b.Property(i => i.SquashCommitSha).HasMaxLength(64);

            b.HasMany(i => i.Dependencies)
                .WithOne(d => d.ForgeTask)
                .HasForeignKey(d => d.TaskId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasMany(i => i.DependentTasks)
                .WithOne(d => d.DependsOnTask)
                .HasForeignKey(d => d.DependsOnTaskId)
                .OnDelete(DeleteBehavior.Restrict);

            b.HasMany(i => i.Questions)
                .WithOne(q => q.ForgeTask)
                .HasForeignKey(q => q.TaskId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasMany(i => i.Checkpoints)
                .WithOne(c => c.ForgeTask)
                .HasForeignKey(c => c.TaskId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasMany(i => i.ExecutionRecords)
                .WithOne(r => r.ForgeTask)
                .HasForeignKey(r => r.TaskId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasMany(i => i.ExecutionLogs)
                .WithOne(l => l.ForgeTask)
                .HasForeignKey(l => l.TaskId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasMany(i => i.StageConversations)
                .WithOne(c => c.ForgeTask)
                .HasForeignKey(c => c.TaskId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // 4. TaskDependency
        modelBuilder.Entity<TaskDependency>(b =>
        {
            b.HasKey(d => d.Id);
            b.HasIndex(d => new { d.TaskId, d.DependsOnTaskId }).IsUnique();
        });

        // 5. Question
        modelBuilder.Entity<Question>(b =>
        {
            b.HasKey(q => q.Id);
            b.Property(q => q.StageId).IsRequired().HasMaxLength(128);
            b.Property(q => q.Prompt).IsRequired();
            b.Property(q => q.QuestionType).HasConversion<int>();
            b.Property(q => q.Status).HasConversion<int>();
            b.Property(q => q.AgentProfileId).HasMaxLength(128);
        });

        // 6. StageCheckpoint
        modelBuilder.Entity<StageCheckpoint>(b =>
        {
            b.HasKey(c => c.Id);
            b.Property(c => c.StageId).IsRequired().HasMaxLength(128);
            b.Property(c => c.GitCommitHash).IsRequired().HasMaxLength(64);
        });

        // 7. StageExecutionRecord
        modelBuilder.Entity<StageExecutionRecord>(b =>
        {
            b.HasKey(r => r.Id);
            b.Property(r => r.StageId).IsRequired().HasMaxLength(128);
            b.Property(r => r.StageKind).IsRequired().HasMaxLength(64);
            b.Property(r => r.AgentProfileId).IsRequired().HasMaxLength(128);
            b.Property(r => r.HarnessId).IsRequired().HasMaxLength(64);
            b.Property(r => r.ModelName).IsRequired().HasMaxLength(256);
            b.Property(r => r.Status).HasConversion<int>();
        });

        // 8. ExecutionLog
        modelBuilder.Entity<ExecutionLog>(b =>
        {
            b.HasKey(l => l.Id);
            b.Property(l => l.StageId).HasMaxLength(128);
            b.Property(l => l.Level).HasConversion<int>();
            b.Property(l => l.Source).IsRequired().HasMaxLength(256);
            b.Property(l => l.Message).IsRequired();
        });

        // 9. SystemSetting
        modelBuilder.Entity<SystemSetting>(b =>
        {
            b.HasKey(s => s.Key);
            b.Property(s => s.Key).HasMaxLength(128);
            b.Property(s => s.Value).IsRequired();
            b.Property(s => s.UpdatedAt).IsRequired();
        });

        // 10. StageConversation
        modelBuilder.Entity<StageConversation>(b =>
        {
            b.HasKey(c => c.Id);
            b.Property(c => c.StageId).IsRequired().HasMaxLength(128);
            b.Property(c => c.StageKind).HasConversion<int>();
            b.Property(c => c.Status).HasConversion<int>();
            b.Property(c => c.AgentProfileId).HasMaxLength(128);
            b.Property(c => c.HarnessId).HasMaxLength(64);
            b.Property(c => c.ModelName).HasMaxLength(256);
            b.Property(c => c.TranscriptFileName).HasMaxLength(256);

            b.HasIndex(c => new { c.TaskId, c.StageId, c.AttemptIndex });

            b.HasMany(c => c.Turns)
                .WithOne(t => t.Conversation)
                .HasForeignKey(t => t.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // 11. StageConversationTurn
        modelBuilder.Entity<StageConversationTurn>(b =>
        {
            b.HasKey(t => t.Id);
            b.Property(t => t.Role).HasConversion<int>();
            b.Property(t => t.ToolName).HasMaxLength(128);

            b.HasIndex(t => new { t.ConversationId, t.TurnIndex });
        });
    }

    /// <summary>
    /// Applies migrations and enables SQLite Write-Ahead Logging (WAL) and optimal concurrency pragmas.
    /// </summary>
    public async Task EnsureDatabaseCreatedAndWalAsync(CancellationToken cancellationToken = default)
    {
        await Database.EnsureCreatedAsync(cancellationToken);
        if (Database.IsSqlite())
        {
            await Database.ExecuteSqlRawAsync("PRAGMA journal_mode = WAL;", cancellationToken);
            await Database.ExecuteSqlRawAsync("PRAGMA synchronous = NORMAL;", cancellationToken);
            await Database.ExecuteSqlRawAsync(@"CREATE TABLE IF NOT EXISTS ""SystemSettings"" (
                ""Key"" TEXT NOT NULL PRIMARY KEY,
                ""Value"" TEXT NOT NULL,
                ""UpdatedAt"" TEXT NOT NULL
            );", cancellationToken);

            try
            {
                await Database.ExecuteSqlRawAsync(@"ALTER TABLE ""AgentProfiles"" ADD COLUMN ""RoutingTarget"" INTEGER NOT NULL DEFAULT 0;", cancellationToken);
            }
            catch
            {
                // Column already exists or table was just created
            }

            try
            {
                await Database.ExecuteSqlRawAsync(@"ALTER TABLE ""Tasks"" ADD COLUMN ""SquashCommitSha"" TEXT NULL;", cancellationToken);
            }
            catch
            {
                // Column already exists or table was just created
            }

            try
            {
                await Database.ExecuteSqlRawAsync(@"CREATE TABLE IF NOT EXISTS ""StageConversations"" (
                    ""Id"" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    ""TaskId"" INTEGER NOT NULL,
                    ""StageId"" TEXT NOT NULL,
                    ""StageKind"" INTEGER NOT NULL,
                    ""AttemptIndex"" INTEGER NOT NULL,
                    ""AgentProfileId"" TEXT NULL,
                    ""HarnessId"" TEXT NULL,
                    ""ModelName"" TEXT NULL,
                    ""StartedAt"" TEXT NOT NULL,
                    ""CompletedAt"" TEXT NULL,
                    ""Status"" INTEGER NOT NULL,
                    ""Summary"" TEXT NULL,
                    ""TranscriptFileName"" TEXT NULL,
                    CONSTRAINT ""FK_StageConversations_Tasks_TaskId"" FOREIGN KEY (""TaskId"") REFERENCES ""Tasks"" (""Id"") ON DELETE CASCADE
                );", cancellationToken);

                await Database.ExecuteSqlRawAsync(@"CREATE TABLE IF NOT EXISTS ""StageConversationTurns"" (
                    ""Id"" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    ""ConversationId"" INTEGER NOT NULL,
                    ""TurnIndex"" INTEGER NOT NULL,
                    ""Role"" INTEGER NOT NULL,
                    ""Content"" TEXT NULL,
                    ""ThinkingTrace"" TEXT NULL,
                    ""ToolName"" TEXT NULL,
                    ""ToolArguments"" TEXT NULL,
                    ""ToolResult"" TEXT NULL,
                    ""DurationMs"" INTEGER NULL,
                    ""IsError"" INTEGER NOT NULL,
                    ""MetadataJson"" TEXT NULL,
                    ""Timestamp"" TEXT NOT NULL,
                    CONSTRAINT ""FK_StageConversationTurns_StageConversations_ConversationId"" FOREIGN KEY (""ConversationId"") REFERENCES ""StageConversations"" (""Id"") ON DELETE CASCADE
                );", cancellationToken);

                await Database.ExecuteSqlRawAsync(@"CREATE INDEX IF NOT EXISTS ""IX_StageConversations_TaskId_StageId_AttemptIndex"" ON ""StageConversations"" (""TaskId"", ""StageId"", ""AttemptIndex"");", cancellationToken);
                await Database.ExecuteSqlRawAsync(@"CREATE INDEX IF NOT EXISTS ""IX_StageConversationTurns_ConversationId_TurnIndex"" ON ""StageConversationTurns"" (""ConversationId"", ""TurnIndex"");", cancellationToken);
            }
            catch
            {
                // Tables already created by EnsureCreatedAsync
            }
        }
    }

    /// <summary>
    /// Seeds the standard global agent profiles (WorkspaceId == null) if not already present in the database.
    /// Preserves existing profiles and user configurations without overwriting them.
    /// </summary>
    public async Task EnsureDefaultGlobalAgentProfilesAsync(string defaultModelName, CancellationToken cancellationToken = default)
    {
        var standardProfiles = AgentProfile.CreateStandardGlobalProfiles(defaultModelName);
        var existingGlobalIds = await AgentProfiles
            .Where(p => p.WorkspaceId == null)
            .Select(p => p.Id)
            .ToListAsync(cancellationToken);

        var missingProfiles = standardProfiles
            .Where(p => !existingGlobalIds.Contains(p.Id, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (missingProfiles.Count > 0)
        {
            AgentProfiles.AddRange(missingProfiles);
            await SaveChangesAsync(cancellationToken);
        }
    }
}
