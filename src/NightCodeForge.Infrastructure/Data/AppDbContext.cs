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
    public DbSet<Issue> Issues => Set<Issue>();
    public DbSet<IssueDependency> IssueDependencies => Set<IssueDependency>();
    public DbSet<Question> Questions => Set<Question>();
    public DbSet<StageCheckpoint> StageCheckpoints => Set<StageCheckpoint>();
    public DbSet<StageExecutionRecord> StageExecutionRecords => Set<StageExecutionRecord>();
    public DbSet<ExecutionLog> ExecutionLogs => Set<ExecutionLog>();

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

            b.HasMany(w => w.Issues)
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

        // 3. Issue
        modelBuilder.Entity<Issue>(b =>
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

            b.HasMany(i => i.Dependencies)
                .WithOne(d => d.Issue)
                .HasForeignKey(d => d.IssueId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasMany(i => i.DependentIssues)
                .WithOne(d => d.DependsOnIssue)
                .HasForeignKey(d => d.DependsOnIssueId)
                .OnDelete(DeleteBehavior.Restrict);

            b.HasMany(i => i.Questions)
                .WithOne(q => q.Issue)
                .HasForeignKey(q => q.IssueId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasMany(i => i.Checkpoints)
                .WithOne(c => c.Issue)
                .HasForeignKey(c => c.IssueId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasMany(i => i.ExecutionRecords)
                .WithOne(r => r.Issue)
                .HasForeignKey(r => r.IssueId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasMany(i => i.ExecutionLogs)
                .WithOne(l => l.Issue)
                .HasForeignKey(l => l.IssueId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // 4. IssueDependency
        modelBuilder.Entity<IssueDependency>(b =>
        {
            b.HasKey(d => d.Id);
            b.HasIndex(d => new { d.IssueId, d.DependsOnIssueId }).IsUnique();
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
        }
    }
}
