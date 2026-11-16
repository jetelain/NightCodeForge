using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Infrastructure.Data;
using NightCodeForge.Infrastructure.Data.Repositories;

namespace NightCodeForge.Infrastructure.Tests;

public class Step125BDifficultyPersistenceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public Step125BDifficultyPersistenceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var context = new AppDbContext(_options);
        context.EnsureDatabaseCreatedAndWalAsync().GetAwaiter().GetResult();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task EfCoreTaskRepository_PersistsDifficultyAcrossCreateUpdateAndConcurrency()
    {
        using var context = new AppDbContext(_options);
        var repo = new EfCoreTaskRepository(new AppDbContextFactory(_options));

        var ws = new Workspace { Name = "InfDiffWS", RepositoryPath = "/inf/diff", DefaultBaseBranch = "main" };
        context.Workspaces.Add(ws);
        await context.SaveChangesAsync();

        var created = await repo.CreateWithBaselineAsync(
            new ForgeTask
            {
                WorkspaceId = ws.Id,
                Title = "Task with Difficulty",
                Difficulty = TaskDifficulty.Difficult,
                Status = TaskOverallStatus.Open
            },
            null,
            AuditActors.User,
            "Initial",
            CancellationToken.None);

        Assert.Equal(TaskDifficulty.Difficult, created.Difficulty);
        var history1 = await repo.GetHistoryAsync(created.Id);
        Assert.Single(history1);
        Assert.Equal(TaskDifficulty.Difficult, history1[0].Difficulty);

        // Concurrent stale handle still holding Difficult/v1 while authoritative handle upgrades to Expert/v2
        var staleCopy = await repo.GetByIdAsync(created.Id);
        Assert.NotNull(staleCopy);

        created.Difficulty = TaskDifficulty.Expert;
        await repo.UpdateWithHistoryAsync(created, null, AuditActors.User, "Upgrade to Expert", CancellationToken.None);

        // Simulate runtime field update from staleCopy (older version=1 < db version=2):
        // Authoritative Difficulty (Expert) in DB must NOT be clobbered back to Difficult!
        staleCopy!.CurrentStageId = StandardStageIds.Implementation;
        await repo.UpdateAsync(staleCopy, null, CancellationToken.None);

        var reloaded = await repo.GetByIdAsync(created.Id);
        Assert.NotNull(reloaded);
        Assert.Equal(TaskDifficulty.Expert, reloaded!.Difficulty);
        Assert.Equal(StandardStageIds.Implementation, reloaded.CurrentStageId);

        var history2 = await repo.GetHistoryAsync(created.Id);
        Assert.Equal(2, history2.Count);
        Assert.Contains(history2, h => h.Difficulty == TaskDifficulty.Expert);
    }

    [Fact]
    public async Task AgentProfileDifficultyOverrides_PersistAsJson_AndResolveForWorkspaceDifficulty()
    {
        var factory = new AppDbContextFactory(_options);
        var wsRepo = new EfCoreWorkspaceRepository(factory);

        await using (var db = new AppDbContext(_options))
        {
            await db.EnsureDefaultGlobalAgentProfilesAsync(new DefaultLlmProviderOptions(
                DefaultModelId: "qwen3.5:35b",
                ArchitectModelId: "qwen3.5:122b",
                ExpertHarnessId: StandardHarnessIds.Acp,
                ExpertModelId: "gemini-4-argon",
                ExpertThinkingEffort: ThinkingEffort.High));
        }

        var ws = await wsRepo.CreateAsync(new Workspace { Name = "MatrixWS", RepositoryPath = "/matrix" });

        var stdProfile = await wsRepo.GetEffectiveAgentProfileAsync(ws.Id, StandardStageIds.Implementation, TaskDifficulty.Standard);
        var diffProfile = await wsRepo.GetEffectiveAgentProfileAsync(ws.Id, StandardStageIds.Implementation, TaskDifficulty.Difficult);
        var expProfile = await wsRepo.GetEffectiveAgentProfileAsync(ws.Id, StandardStageIds.Implementation, TaskDifficulty.Expert);

        Assert.Equal("qwen3.5:35b", stdProfile!.ModelName);
        Assert.Equal(StandardHarnessIds.Crucible, stdProfile.HarnessId);

        Assert.Equal("qwen3.5:122b", diffProfile!.ModelName);
        Assert.Equal(StandardHarnessIds.Crucible, diffProfile.HarnessId);
        Assert.Equal(ThinkingEffort.High, diffProfile.ThinkingEffort);

        Assert.Equal(StandardHarnessIds.Acp, expProfile!.HarnessId);
        Assert.Equal("gemini-4-argon", expProfile.ModelName);
        Assert.Equal(ThinkingEffort.High, expProfile.ThinkingEffort);
    }

    [Fact]
    public async Task EnsureDefaultGlobalAgentProfilesAsync_BackfillsNullDifficultyOverridesOnExistingProfiles()
    {
        var triageTemplate = AgentProfile.CreateStandardGlobalProfiles("coder-32b").Single(p => p.Id == StandardStageIds.Triage);
        var ideationTemplate = AgentProfile.CreateDefaultIdeationProfile(null, "coder-32b");

        await using (var db = new AppDbContext(_options))
        {
            // Seed pre-12.5B profiles with DifficultyOverrides == null
            db.AgentProfiles.AddRange(
                triageTemplate,
                ideationTemplate,
                new AgentProfile { Id = StandardStageIds.TaskReview, WorkspaceId = null, Name = "Arch", ModelName = "arch-122b", ThinkingEffort = ThinkingEffort.High },
                new AgentProfile { Id = StandardStageIds.Implementation, WorkspaceId = null, Name = "Coder", ModelName = "coder-32b", ThinkingEffort = ThinkingEffort.Medium },
                new AgentProfile { Id = StandardStageIds.CodeReviewAndFix, WorkspaceId = null, Name = "Reviewer", ModelName = "arch-122b", ThinkingEffort = ThinkingEffort.High });
            await db.SaveChangesAsync();
        }

        await using (var db2 = new AppDbContext(_options))
        {
            await db2.EnsureDefaultGlobalAgentProfilesAsync(new DefaultLlmProviderOptions(
                DefaultModelId: "coder-32b",
                ArchitectModelId: "arch-122b",
                ExpertHarnessId: StandardHarnessIds.Acp,
                ExpertModelId: "claude-opus-5-5"));

            var impl = await db2.AgentProfiles.FindAsync(StandardStageIds.Implementation);
            var triage = await db2.AgentProfiles.FindAsync(StandardStageIds.Triage);

            Assert.NotNull(impl?.DifficultyOverrides);
            Assert.Equal("arch-122b", impl!.DifficultyOverrides![TaskDifficulty.Difficult].ModelName);
            Assert.Equal(StandardHarnessIds.Acp, impl.DifficultyOverrides[TaskDifficulty.Expert].HarnessId);
            Assert.Equal("claude-opus-5-5", impl.DifficultyOverrides[TaskDifficulty.Expert].ModelName);
            Assert.Null(triage?.DifficultyOverrides);
        }
    }

    [Fact]
    public async Task EnsureDefaultGlobalAgentProfilesAsync_BackfillsFromCustomizedTaskReviewModel_AndNormalizesLegacyPiRpcOverrides()
    {
        var triageTemplate = AgentProfile.CreateStandardGlobalProfiles("coder-32b").Single(p => p.Id == StandardStageIds.Triage);
        var ideationTemplate = AgentProfile.CreateDefaultIdeationProfile(null, "coder-32b");

        await using (var db = new AppDbContext(_options))
        {
            db.AgentProfiles.AddRange(
                triageTemplate,
                ideationTemplate,
                new AgentProfile
                {
                    Id = StandardStageIds.TaskReview,
                    WorkspaceId = null,
                    Name = "Arch",
                    HarnessId = StandardHarnessIds.Crucible,
                    ModelName = "deepseek-r1:70b",
                    ThinkingEffort = ThinkingEffort.High,
                    DifficultyOverrides = null
                },
                new AgentProfile
                {
                    Id = StandardStageIds.Implementation,
                    WorkspaceId = null,
                    Name = "Coder",
                    HarnessId = StandardHarnessIds.Crucible,
                    ModelName = "coder-32b",
                    ThinkingEffort = ThinkingEffort.Medium,
                    DifficultyOverrides = null
                },
                new AgentProfile
                {
                    Id = StandardStageIds.CodeReviewAndFix,
                    WorkspaceId = null,
                    Name = "Reviewer",
                    HarnessId = "pirpc",
                    ModelName = "deepseek-r1:70b",
                    ThinkingEffort = ThinkingEffort.High,
                    DifficultyOverrides = new Dictionary<TaskDifficulty, DifficultyStageOverride>
                    {
                        [TaskDifficulty.Difficult] = new()
                        {
                            HarnessId = "pirpc",
                            ModelName = "deepseek-r1:70b",
                            ThinkingEffort = ThinkingEffort.High
                        }
                    }
                });
            await db.SaveChangesAsync();
        }

        await using (var db2 = new AppDbContext(_options))
        {
            // ArchitectModelId is unset -> must preserve stored TaskReview model ("deepseek-r1:70b") instead of downgrading to DefaultModelId ("coder-32b")
            await db2.EnsureDefaultGlobalAgentProfilesAsync(new DefaultLlmProviderOptions(
                DefaultModelId: "coder-32b",
                ArchitectModelId: null));
        }

        await using (var db3 = new AppDbContext(_options))
        {
            var impl = await db3.AgentProfiles.FindAsync(StandardStageIds.Implementation);
            var review = await db3.AgentProfiles.FindAsync(StandardStageIds.CodeReviewAndFix);

            Assert.NotNull(impl?.DifficultyOverrides);
            Assert.Equal("deepseek-r1:70b", impl!.DifficultyOverrides![TaskDifficulty.Difficult].ModelName);
            Assert.Equal("deepseek-r1:70b", impl.DifficultyOverrides[TaskDifficulty.Expert].ModelName);

            Assert.NotNull(review);
            Assert.Equal(StandardHarnessIds.Crucible, review!.HarnessId);
            Assert.Equal(StandardHarnessIds.Crucible, review.DifficultyOverrides![TaskDifficulty.Difficult].HarnessId);
        }
    }
}
