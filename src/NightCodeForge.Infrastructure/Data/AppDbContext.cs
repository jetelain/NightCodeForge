using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Workflow;

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
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ConversationTurn> ConversationTurns => Set<ConversationTurn>();
    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();
    public DbSet<WorkflowDefinition> WorkflowDefinitions => Set<WorkflowDefinition>();
    public DbSet<StageDefinition> StageDefinitions => Set<StageDefinition>();
    public DbSet<ForgeTaskHistory> TaskHistories => Set<ForgeTaskHistory>();

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
            b.Property(w => w.DefaultWorkflowId);

            b.HasMany(w => w.Tasks)
                .WithOne(i => i.Workspace)
                .HasForeignKey(i => i.WorkspaceId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasMany(w => w.AgentProfiles)
                .WithOne(p => p.Workspace)
                .HasForeignKey(p => p.WorkspaceId)
                .OnDelete(DeleteBehavior.SetNull);

            b.HasMany(w => w.Workflows)
                .WithOne(wf => wf.Workspace)
                .HasForeignKey(wf => wf.WorkspaceId)
                .OnDelete(DeleteBehavior.SetNull);

            b.HasMany(w => w.Conversations)
                .WithOne(c => c.Workspace)
                .HasForeignKey(c => c.WorkspaceId)
                .OnDelete(DeleteBehavior.Cascade);
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
            b.Property(i => i.WorkflowId);
            b.Property(i => i.Version).HasDefaultValue(1);

            b.HasOne(i => i.Workflow)
                .WithMany(w => w.Tasks)
                .HasForeignKey(i => i.WorkflowId)
                .OnDelete(DeleteBehavior.Restrict);

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

            b.HasMany(i => i.Conversations)
                .WithOne(c => c.ForgeTask)
                .HasForeignKey(c => c.TaskId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasMany(i => i.History)
                .WithOne(h => h.ForgeTask)
                .HasForeignKey(h => h.TaskId)
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
            b.Property(q => q.BatchId).HasMaxLength(64);
            b.HasIndex(q => q.BatchId);
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

        // 10. Conversation
        modelBuilder.Entity<Conversation>(b =>
        {
            b.HasKey(c => c.Id);
            b.Property(c => c.ConversationType).HasConversion<int>();
            b.Property(c => c.Title).HasMaxLength(256);
            b.Property(c => c.StageId).IsRequired().HasMaxLength(128);
            b.Property(c => c.StageKind).HasConversion<int>();
            b.Property(c => c.Status).HasConversion<int>();
            b.Property(c => c.AgentProfileId).HasMaxLength(128);
            b.Property(c => c.HarnessId).HasMaxLength(64);
            b.Property(c => c.ModelName).HasMaxLength(256);
            b.Property(c => c.TranscriptFileName).HasMaxLength(256);

            b.HasIndex(c => new { c.TaskId, c.StageId, c.AttemptIndex });
            b.HasIndex(c => c.WorkspaceId);

            b.HasOne(c => c.ForgeTask)
                .WithMany(t => t.Conversations)
                .HasForeignKey(c => c.TaskId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasOne(c => c.Workspace)
                .WithMany(w => w.Conversations)
                .HasForeignKey(c => c.WorkspaceId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasMany(c => c.Turns)
                .WithOne(t => t.Conversation)
                .HasForeignKey(t => t.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // 11. ConversationTurn
        modelBuilder.Entity<ConversationTurn>(b =>
        {
            b.HasKey(t => t.Id);
            b.Property(t => t.Role).HasConversion<int>();
            b.Property(t => t.ToolName).HasMaxLength(128);

            b.HasIndex(t => new { t.ConversationId, t.TurnIndex });
        });

        // 12. WorkflowDefinition
        modelBuilder.Entity<WorkflowDefinition>(b =>
        {
            b.HasKey(w => w.Id);
            b.Property(w => w.Name).IsRequired().HasMaxLength(256);
            b.Property(w => w.Code).IsRequired().HasMaxLength(128);
            b.Property(w => w.Icon).HasMaxLength(32).HasDefaultValue("⚡");
            b.Property(w => w.Description).HasMaxLength(2048);
            b.Property(w => w.IsDefault).HasDefaultValue(false);

            b.HasIndex(w => w.Code).IsUnique();

            b.HasOne(w => w.Workspace)
                .WithMany(ws => ws.Workflows)
                .HasForeignKey(w => w.WorkspaceId)
                .OnDelete(DeleteBehavior.SetNull);

            b.HasMany(w => w.Stages)
                .WithOne(s => s.Workflow)
                .HasForeignKey(s => s.WorkflowId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasMany(w => w.Tasks)
                .WithOne(t => t.Workflow)
                .HasForeignKey(t => t.WorkflowId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // 13. StageDefinition
        modelBuilder.Entity<StageDefinition>(b =>
        {
            b.HasKey(s => s.Id);
            b.Property(s => s.StageId).IsRequired().HasMaxLength(128);
            b.Property(s => s.DisplayName).IsRequired().HasMaxLength(256);
            b.Property(s => s.Icon).HasMaxLength(32).HasDefaultValue("⚙️");
            b.Property(s => s.SequenceOrder).IsRequired();
            b.Property(s => s.Kind).HasConversion<int>();
            b.Property(s => s.CsharpImplementationType).IsRequired().HasMaxLength(256);
            b.Property(s => s.ConfigurationJson).IsRequired();

            b.HasIndex(s => new { s.WorkflowId, s.StageId }).IsUnique();
            b.HasIndex(s => new { s.WorkflowId, s.SequenceOrder });

            b.HasOne(s => s.Workflow)
                .WithMany(w => w.Stages)
                .HasForeignKey(s => s.WorkflowId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // 14. ForgeTaskHistory
        modelBuilder.Entity<ForgeTaskHistory>(b =>
        {
            b.ToTable("TaskHistories");
            b.HasKey(h => h.Id);
            b.Property(h => h.Version).IsRequired();
            b.Property(h => h.Title).IsRequired().HasMaxLength(512);
            b.Property(h => h.Description).IsRequired();
            b.Property(h => h.Status).HasConversion<int>();
            b.Property(h => h.Priority).IsRequired();
            b.Property(h => h.MergeMode).HasConversion<int>();
            b.Property(h => h.BaseBranch).IsRequired().HasMaxLength(128);
            b.Property(h => h.FeatureBranch).HasMaxLength(256);
            b.Property(h => h.CurrentStageId).HasMaxLength(128);
            b.Property(h => h.SerializedDependencies).IsRequired();
            b.Property(h => h.ChangedAt).IsRequired();
            b.Property(h => h.ChangedBy).IsRequired().HasMaxLength(128);
            b.Property(h => h.ChangeReason).HasMaxLength(1024);

            b.HasIndex(h => new { h.TaskId, h.Version }).IsUnique();
            b.HasIndex(h => h.ChangedAt);

            b.HasOne(h => h.ForgeTask)
                .WithMany(t => t.History)
                .HasForeignKey(h => h.TaskId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    /// <summary>
    /// Environment variable used to seed the SQLite autoincrement sequence for Workspaces (e.g. in E2E tests).
    /// </summary>
    public const string WorkspaceIdSeedEnvironmentVariable = "NCF_WORKSPACE_ID_SEED";

    /// <summary>
    /// Default workspace ID seed used in end-to-end tests to avoid colliding with user/production workspaces.
    /// </summary>
    public const int DefaultE2EWorkspaceIdSeed = 1_001_000;

    /// <summary>
    /// Ensures the database schema is created and enables SQLite Write-Ahead Logging (WAL) and optimal concurrency pragmas.
    /// Also applies the workspace ID seed if configured via environment variables.
    /// </summary>
    public async Task EnsureDatabaseCreatedAndWalAsync(CancellationToken cancellationToken = default)
    {
        await Database.EnsureCreatedAsync(cancellationToken);
        if (Database.IsSqlite())
        {
            await Database.ExecuteSqlRawAsync("PRAGMA journal_mode = WAL;", cancellationToken);
            await Database.ExecuteSqlRawAsync("PRAGMA synchronous = NORMAL;", cancellationToken);
            await Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;", cancellationToken);

            await ApplyWorkspaceIdSeedIfConfiguredAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Reads the workspace ID seed from the environment variable (or fallback) if set, and seeds sqlite_sequence.
    /// </summary>
    public async Task ApplyWorkspaceIdSeedIfConfiguredAsync(CancellationToken cancellationToken = default)
    {
        var rawValue = Environment.GetEnvironmentVariable(WorkspaceIdSeedEnvironmentVariable);

        if (!string.IsNullOrWhiteSpace(rawValue) && int.TryParse(rawValue, out var nextId) && nextId > 1)
        {
            await SeedWorkspaceIdSequenceAsync(nextId, cancellationToken);
        }
    }

    /// <summary>
    /// Seeds the SQLite autoincrement sequence for Workspaces so that the next generated Workspace ID is at least <paramref name="nextId"/>.
    /// In SQLite, autoincrement sequence values are tracked in the internal table 'sqlite_sequence' as nextId - 1.
    /// Handles both unseeded/empty tables (where no row exists in sqlite_sequence) and existing tables.
    /// </summary>
    public async Task SeedWorkspaceIdSequenceAsync(int nextId, CancellationToken cancellationToken = default)
    {
        if (!Database.IsSqlite())
        {
            return;
        }

        if (nextId <= 1)
        {
            return;
        }

        const string tableName = "Workspaces";
        var sequenceValue = nextId - 1;

        // Try updating existing sequence row if it is smaller than sequenceValue
        var rowsUpdated = await Database.ExecuteSqlRawAsync(
            "UPDATE sqlite_sequence SET seq = MAX(seq, {0}) WHERE name = {1};",
            new object[] { sequenceValue, tableName },
            cancellationToken);

        if (rowsUpdated == 0)
        {
            // If the row does not exist yet (table is newly created or empty), insert it.
            // Using NOT EXISTS ensures we don't insert a duplicate row if seq was already >= sequenceValue.
            await Database.ExecuteSqlRawAsync(
                "INSERT INTO sqlite_sequence (name, seq) SELECT {0}, {1} WHERE NOT EXISTS (SELECT 1 FROM sqlite_sequence WHERE name = {0});",
                new object[] { tableName, sequenceValue },
                cancellationToken);
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

    /// <summary>
    /// Seeds the standard default workflow ("standard-3-agent") and its 6 stages if not already present in the database.
    /// Preserves existing workflows and stages without duplicating them.
    /// </summary>
    public async Task<WorkflowDefinition> EnsureDefaultWorkflowsAsync(CancellationToken cancellationToken = default)
    {
        var standardWorkflow = await WorkflowDefinitions
            .Include(w => w.Stages)
            .FirstOrDefaultAsync(w => w.Code == StandardWorkflowDefinitions.Standard3AgentCode, cancellationToken);

        if (standardWorkflow == null)
        {
            standardWorkflow = StandardWorkflowDefinitions.CreateStandard3AgentWorkflow();
            WorkflowDefinitions.Add(standardWorkflow);
            await SaveChangesAsync(cancellationToken);
        }

        return standardWorkflow;
    }
}
