using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NightCodeForge.Core.Domain;
using NightCodeForge.Infrastructure.Data;
using NightCodeForge.Infrastructure.Data.Repositories;

namespace NightCodeForge.Infrastructure.Tests;

public class AppDbContextTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public AppDbContextTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    [Fact]
    public async Task EnsureDatabaseCreatedAndWalAsync_CreatesSchemaSuccessfully()
    {
        using var context = new AppDbContext(_options);
        await context.EnsureDatabaseCreatedAndWalAsync();

        // Verify tables exist by inserting a workspace
        var workspace = new Workspace
        {
            Name = "TestRepo",
            RepositoryPath = "/repos/test",
            DefaultBaseBranch = "main",
            ExecutionMode = ExecutionMode.ContainerSandbox
        };

        context.Workspaces.Add(workspace);
        await context.SaveChangesAsync();

        Assert.True(workspace.Id > 0);
    }

    [Fact]
    public async Task AgentProfile_SerializesComplexJsonColumns()
    {
        using var context = new AppDbContext(_options);
        await context.Database.EnsureCreatedAsync();

        var profile = new AgentProfile
        {
            Id = "test-agent",
            Name = "Test Agent",
            HarnessId = HarnessType.Crucible,
            ModelName = "qwen2.5-coder:32b",
            ThinkingEffort = ThinkingEffort.Medium,
            OptimizationFlags = new LocalOptimizationFlags
            {
                LoopCircuitBreaker = true,
                ThinkingTraceCompaction = false
            },
            PermittedTools = new[] { "read", "grep", "propose_tasks" },
            SecurityTier = SandboxSecurityTier.Standard
        };

        context.AgentProfiles.Add(profile);
        await context.SaveChangesAsync();

        // Reload fresh from DB
        using var context2 = new AppDbContext(_options);
        var loaded = await context2.AgentProfiles.FindAsync("test-agent");

        Assert.NotNull(loaded);
        Assert.NotNull(loaded.OptimizationFlags);
        Assert.True(loaded.OptimizationFlags.LoopCircuitBreaker);
        Assert.False(loaded.OptimizationFlags.ThinkingTraceCompaction);

        Assert.NotNull(loaded.PermittedTools);
        Assert.Equal(3, loaded.PermittedTools.Count);
        Assert.Contains("propose_tasks", loaded.PermittedTools);
    }

    [Fact]
    public async Task EfCoreTaskRepository_And_DependencyStore_WorkCorrectly()
    {
        using var context = new AppDbContext(_options);
        await context.Database.EnsureCreatedAsync();

        var ws = new Workspace
        {
            Name = "Repo1",
            RepositoryPath = "/path/1",
            DefaultBaseBranch = "main"
        };
        context.Workspaces.Add(ws);
        await context.SaveChangesAsync();

        var repo = new EfCoreTaskRepository(context);
        var depStore = new EfCoreTaskDependencyStore(context);

        var task1 = new ForgeTask
        {
            WorkspaceId = ws.Id,
            Title = "First ForgeTask",
            Description = "Base task",
            Status = TaskOverallStatus.Closed
        };
        await repo.CreateAsync(task1);

        var task2 = new ForgeTask
        {
            WorkspaceId = ws.Id,
            Title = "Dependent ForgeTask",
            Description = "Depends on First ForgeTask",
            Status = TaskOverallStatus.Open
        };
        await repo.CreateAsync(task2, dependsOn: new[] { task1.Id });

        var prereqs = await depStore.GetPrerequisiteTaskIdsAsync(task2.Id);
        Assert.Single(prereqs);
        Assert.Equal(task1.Id, prereqs[0]);

        var dependents = await depStore.GetDependentTaskIdsAsync(task1.Id);
        Assert.Single(dependents);
        Assert.Equal(task2.Id, dependents[0]);

        var status1 = await depStore.GetTaskStatusAsync(task1.Id);
        Assert.Equal(TaskOverallStatus.Closed, status1);
    }

    [Fact]
    public async Task CascadeDelete_RemovesChildEntitiesWhenTaskDeleted()
    {
        using var context = new AppDbContext(_options);
        await context.Database.EnsureCreatedAsync();

        var ws = new Workspace { Name = "CascadeWS", RepositoryPath = "/path", DefaultBaseBranch = "main" };
        context.Workspaces.Add(ws);
        await context.SaveChangesAsync();

        var task = new ForgeTask
        {
            WorkspaceId = ws.Id,
            Title = "Parent ForgeTask"
        };
        context.Tasks.Add(task);
        await context.SaveChangesAsync();

        context.Questions.Add(new Question { TaskId = task.Id, StageId = "s1", Prompt = "Q1?" });
        context.ExecutionLogs.Add(new ExecutionLog { TaskId = task.Id, Source = "Test", Message = "Log 1" });
        context.StageCheckpoints.Add(new StageCheckpoint { TaskId = task.Id, StageId = "s1", SerializedMemory = "{}", GitCommitHash = "abc" });
        await context.SaveChangesAsync();

        // Delete task
        context.Tasks.Remove(task);
        await context.SaveChangesAsync();

        // Verify child records cascaded
        Assert.Empty(await context.Questions.Where(q => q.TaskId == task.Id).ToListAsync());
        Assert.Empty(await context.ExecutionLogs.Where(l => l.TaskId == task.Id).ToListAsync());
        Assert.Empty(await context.StageCheckpoints.Where(c => c.TaskId == task.Id).ToListAsync());
    }

    [Fact]
    public async Task EnsureDefaultGlobalAgentProfilesAsync_SeedsAll5StandardProfiles_WhenEmpty()
    {
        using var context = new AppDbContext(_options);
        await context.Database.EnsureCreatedAsync();

        await context.EnsureDefaultGlobalAgentProfilesAsync("Qwen3.8-27B:latest");

        var profiles = await context.AgentProfiles.Where(p => p.WorkspaceId == null).ToListAsync();
        Assert.Equal(5, profiles.Count);

        var ids = profiles.Select(p => p.Id).OrderBy(x => x).ToList();
        Assert.Equal(new[] { "code-review-fix", "ideation", "implementation", "task-review", "triage" }, ids);

        foreach (var p in profiles)
        {
            Assert.Null(p.WorkspaceId);
            Assert.Equal("Qwen3.8-27B:latest", p.ModelName);
        }

        // Verify standard profiles have null OptimizationFlags by default to inherit global optimizations
        var impl = profiles.First(p => p.Id == "implementation");
        Assert.Null(impl.OptimizationFlags);

        // Verify triage has restricted sandbox tier
        var triage = profiles.First(p => p.Id == "triage");
        Assert.Equal(SandboxSecurityTier.RestrictedReadOnly, triage.SecurityTier);
        Assert.Equal(AgentMode.ReadOnly, triage.Mode);
    }

    [Fact]
    public async Task EnsureDefaultGlobalAgentProfilesAsync_IsIdempotent_AndPreservesCustomizations()
    {
        using var context = new AppDbContext(_options);
        await context.Database.EnsureCreatedAsync();

        await context.EnsureDefaultGlobalAgentProfilesAsync("Qwen3.8-27B:latest");

        // User customizes implementation temperature
        var impl = await context.AgentProfiles.FirstAsync(p => p.Id == "implementation");
        impl.Temperature = 0.77;
        impl.ModelName = "custom-coder:32b";
        await context.SaveChangesAsync();

        // Run seeder again (e.g. app restart)
        await context.EnsureDefaultGlobalAgentProfilesAsync("Qwen3.8-27B:latest");

        var profiles = await context.AgentProfiles.Where(p => p.WorkspaceId == null).ToListAsync();
        Assert.Equal(5, profiles.Count);

        var updatedImpl = await context.AgentProfiles.FirstAsync(p => p.Id == "implementation");
        Assert.Equal(0.77, updatedImpl.Temperature);
        Assert.Equal("custom-coder:32b", updatedImpl.ModelName);
    }

    [Fact]
    public async Task EfCoreWorkspaceRepository_GetEffectiveAgentProfileAsync_ResolvesDedicatedAndFallsBackToGlobal()
    {
        using var context = new AppDbContext(_options);
        await context.Database.EnsureCreatedAsync();

        await context.EnsureDefaultGlobalAgentProfilesAsync("Qwen3.8-27B:latest");

        var repo = new EfCoreWorkspaceRepository(context);
        var ws = await repo.CreateAsync(new Workspace { Name = "RepoTest", RepositoryPath = "/repo", DefaultBaseBranch = "main" });

        // 1. When no dedicated profile exists, falls back to global profile
        var globalImpl = await repo.GetEffectiveAgentProfileAsync(ws.Id, "implementation");
        Assert.NotNull(globalImpl);
        Assert.Null(globalImpl.WorkspaceId);
        Assert.Equal("Qwen3.8-27B:latest", globalImpl.ModelName);

        // 2. Add dedicated workspace override for implementation
        var dedicatedImpl = new AgentProfile
        {
            Id = $"ws-{ws.Id}:implementation",
            WorkspaceId = ws.Id,
            Name = "Dedicated Implementation Agent",
            HarnessId = "crucible",
            ModelName = "dedicated-model:70b",
            StageDirectives = new Dictionary<string, string> { ["implementation"] = "Custom directive" }
        };
        context.AgentProfiles.Add(dedicatedImpl);
        await context.SaveChangesAsync();

        // 3. Now resolves the dedicated workspace profile
        var resolved = await repo.GetEffectiveAgentProfileAsync(ws.Id, "implementation");
        Assert.NotNull(resolved);
        Assert.Equal(ws.Id, resolved.WorkspaceId);
        Assert.Equal("dedicated-model:70b", resolved.ModelName);

        // 4. Other stages still fall back to global
        var triage = await repo.GetEffectiveAgentProfileAsync(ws.Id, "triage");
        Assert.NotNull(triage);
        Assert.Null(triage.WorkspaceId);
        Assert.Equal("Qwen3.8-27B:latest", triage.ModelName);
    }
}
