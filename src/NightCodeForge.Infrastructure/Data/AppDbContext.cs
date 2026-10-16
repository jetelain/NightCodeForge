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
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ConversationTurn> ConversationTurns => Set<ConversationTurn>();
    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();
    public DbSet<WorkflowDefinition> WorkflowDefinitions => Set<WorkflowDefinition>();
    public DbSet<StageDefinition> StageDefinitions => Set<StageDefinition>();

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
    }

    /// <summary>
    /// Ensures the database schema is created and enables SQLite Write-Ahead Logging (WAL) and optimal concurrency pragmas.
    /// In early development, schema changes are handled via database recreation. EF Core migrations will be adopted for production releases.
    /// </summary>
    public async Task EnsureDatabaseCreatedAndWalAsync(CancellationToken cancellationToken = default)
    {
        await Database.EnsureCreatedAsync(cancellationToken);
        if (Database.IsSqlite())
        {
            await Database.ExecuteSqlRawAsync("PRAGMA journal_mode = WAL;", cancellationToken);
            await Database.ExecuteSqlRawAsync("PRAGMA synchronous = NORMAL;", cancellationToken);
            await Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;", cancellationToken);
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
            .FirstOrDefaultAsync(w => w.Code == "standard-3-agent", cancellationToken);

        if (standardWorkflow == null)
        {
            standardWorkflow = new WorkflowDefinition
            {
                Name = "Standard 3-Agent Pipeline",
                Code = "standard-3-agent",
                Icon = "⚡",
                Description = "Default autonomous pipeline: ForgeTask Review -> Pre-Impl Baseline -> Implementation -> Post-Impl Gates -> Code Review & Fix -> Post-Review Verification.",
                IsDefault = true,
                WorkspaceId = null,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                Stages = new List<StageDefinition>
                {
                    new StageDefinition
                    {
                        StageId = "task-review",
                        DisplayName = "ForgeTask Review",
                        Icon = "🔍",
                        SequenceOrder = 1,
                        Kind = WorkflowStageKind.Agentic,
                        CsharpImplementationType = "agentic",
                        ConfigurationJson = """
                        {
                          "AgentProfileId": null,
                          "RequiredMode": 1,
                          "MaxRetries": 3,
                          "TimeoutMinutes": 30,
                          "StageDirective": "You are a Staff Software Architect & Requirements Investigator.\n1. Thoroughly investigate the task requirements and inspect the codebase using 'read' and 'bash' (ls, rg, find) to locate affected files, dependencies, and interfaces.\n2. MANDATORY FILE EDIT: You must edit the task specification document on disk (specified in [ForgeTask Specification Document] or docs/tasks/) using 'edit' (or 'write'). Enrich it with your approved technical architecture, affected files/classes, step-by-step implementation plan, and testable acceptance criteria. When you finish, the system automatically reads back the file from disk to update the task specification and guide the implementation agent.\n3. Proactively uncover ambiguities, implicit assumptions, or architectural conflicts. If any requirement or design decision is ambiguous or has multiple valid alternatives, invoke 'ask' immediately. Do not guess.\n4. Do not write or modify production code in this stage.",
                          "PromptTemplate": "Execute stage '{{ stage.display_name }}' for task #{{ task.id }}: {{ task.title }}\n{{ task.description }}\n{{- if context.task_markdown_path }}\n\n[ForgeTask Specification Document]:\n{{ context.task_markdown_path }}\n(CRITICAL: You are required to edit this file directly on disk using 'edit' or 'write' to document your approved architecture, step-by-step implementation plan, and acceptance criteria.)\n{{- end }}\n{{- if context.answered_questions }}\n\n[Human Clarifications & Answers]:\n{{ for qa in context.answered_questions }}\nQ: {{ qa.prompt }}\nA: {{ qa.answer }}\n{{ end }}\n{{- end }}"
                        }
                        """
                    },
                    new StageDefinition
                    {
                        StageId = "deterministic-pre-impl",
                        DisplayName = "Pre-Implementation Baseline",
                        Icon = "⚙️",
                        SequenceOrder = 2,
                        Kind = WorkflowStageKind.Deterministic,
                        CsharpImplementationType = "deterministic-composite",
                        ConfigurationJson = """{"Action": "CompositeBaseline", "FailOnError": false, "TimeoutSeconds": 600, "ExtraParameters": {"Stages": "build,test"}}"""
                    },
                    new StageDefinition
                    {
                        StageId = "implementation",
                        DisplayName = "Implementation",
                        Icon = "💻",
                        SequenceOrder = 3,
                        Kind = WorkflowStageKind.Agentic,
                        CsharpImplementationType = "agentic",
                        ConfigurationJson = """
                        {
                          "AgentProfileId": null,
                          "RequiredMode": 1,
                          "MaxRetries": 3,
                          "TimeoutMinutes": 60,
                          "StageDirective": "You are a Senior Software Engineer & TDD Practitioner.\n1. Implement clean, robust, and maintainable code adhering to the task specification document in docs/tasks/, the approved plan, human answers, and prior reviewer feedback.\n2. Keep the blast radius minimal: avoid unnecessary refactoring or file churn outside the approved scope.\n3. Apply precision edits with 'edit' (or 'write' for new files). Follow repository .editorconfig formatting standards.\n4. Continuously verify your changes: execute builds and tests incrementally using 'bash'. Fix all compiler errors and test failures.\n5. Write comprehensive unit tests covering all new logic and edge cases. If unexpected breaking blockers arise, invoke 'ask'.",
                          "PromptTemplate": "Execute stage '{{ stage.display_name }}' for task #{{ task.id }}: {{ task.title }}\n{{ task.description }}\n{{- if context.task_markdown_path }}\n\n[ForgeTask Specification Document]:\n{{ context.task_markdown_path }} (You can read or update this document with read/edit tools.)\n{{- end }}\n{{- if context.approved_plan }}\n\n[Approved Implementation Plan]:\n{{ context.approved_plan }}\n{{- end }}\n{{- if context.reviewer_feedback }}\n\n[Reviewer Requested Changes / Feedback]:\n{{ context.reviewer_feedback }}\n{{- end }}\n{{- if context.compiler_errors }}\n\n[Prior Diagnostic Failures to Remediate]:\n{{ for err in context.compiler_errors }}\n{{ err }}\n{{ end }}\n{{- end }}\n{{- if context.answered_questions }}\n\n[Human Clarifications & Answers]:\n{{ for qa in context.answered_questions }}\nQ: {{ qa.prompt }}\nA: {{ qa.answer }}\n{{ end }}\n{{- end }}"
                        }
                        """
                    },
                    new StageDefinition
                    {
                        StageId = "deterministic-post-impl",
                        DisplayName = "Post-Implementation Gates",
                        Icon = "🧪",
                        SequenceOrder = 4,
                        Kind = WorkflowStageKind.Deterministic,
                        CsharpImplementationType = "deterministic-composite",
                        ConfigurationJson = """{"Action": "CompositeVerification", "LoopBackStageId": "implementation", "FailOnError": true, "TimeoutSeconds": 900, "ExtraParameters": {"Stages": "build,test,coverage,sast"}}"""
                    },
                    new StageDefinition
                    {
                        StageId = "code-review-fix",
                        DisplayName = "Code Review & Fix",
                        Icon = "🧐",
                        SequenceOrder = 5,
                        Kind = WorkflowStageKind.Agentic,
                        CsharpImplementationType = "agentic",
                        ConfigurationJson = """
                        {
                          "AgentProfileId": null,
                          "RequiredMode": 2,
                          "LoopBackStageId": "implementation",
                          "MaxRetries": 3,
                          "TimeoutMinutes": 30,
                          "StageDirective": "You are a Principal Code Reviewer and Quality Gatekeeper.\n1. Inspect the full git diff against the base branch using 'bash' (e.g. git diff, git status) and 'read'. Compare against the specification in docs/tasks/ and the implementation claims.\n2. TIER 1 - MINOR POLISH: If the implementation is fundamentally sound and only requires minor edge-case fixes, code style cleanup, variable renames, or documentation additions, apply precision fixes directly using 'edit' and verify with 'bash' (build and test).\n3. TIER 2 - REJECTION GATE: Rejection is strictly reserved for major tasks or missing features. If core acceptance criteria are unfulfilled, if nothing has been implemented, if tests fundamentally fail requiring architectural redesign, or if entire modules are missing, REJECT the implementation immediately:\n   - If 'request_changes' tool is available, invoke 'request_changes(feedback, reason)' immediately.\n   - Otherwise, conclude your final response with:\n     <request_changes>\n     [Detailed explanation of missing features, broken requirements, and instructions for rework]\n     </request_changes>\n   Do not attempt to write entire missing feature modules in the review stage.",
                          "PromptTemplate": "Execute stage '{{ stage.display_name }}' for task #{{ task.id }}: {{ task.title }}\n{{ task.description }}\n{{- if context.task_markdown_path }}\n\n[ForgeTask Specification Document]:\n{{ context.task_markdown_path }}\n{{- end }}\n{{- if context.approved_plan }}\n\n[Approved Implementation Plan]:\n{{ context.approved_plan }}\n{{- end }}\n{{- if context.implementation_summary }}\n\n[Implementation Agent Completion Summary]:\n{{ context.implementation_summary }}\n(Note: Treat these reported achievements as claims to verify against the git diff and test results. Do not assume claims are true without checking the code.)\n{{- end }}"
                        }
                        """
                    },
                    new StageDefinition
                    {
                        StageId = "deterministic-post-review",
                        DisplayName = "Post-Review Verification",
                        Icon = "🛡️",
                        SequenceOrder = 6,
                        Kind = WorkflowStageKind.Deterministic,
                        CsharpImplementationType = "deterministic-composite",
                        ConfigurationJson = """{"Action": "CompositeVerification", "LoopBackStageId": "implementation", "FailOnError": true, "TimeoutSeconds": 600, "ExtraParameters": {"Stages": "build,test"}}"""
                    }
                }
            };

            WorkflowDefinitions.Add(standardWorkflow);
            await SaveChangesAsync(cancellationToken);
        }

        return standardWorkflow;
    }
}
