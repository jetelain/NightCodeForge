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
            PermittedTools = new[] { "read", "grep", "propose_issues" },
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
        Assert.Contains("propose_issues", loaded.PermittedTools);
    }

    [Fact]
    public async Task EfCoreIssueRepository_And_DependencyStore_WorkCorrectly()
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

        var repo = new EfCoreIssueRepository(context);
        var depStore = new EfCoreIssueDependencyStore(context);

        var issue1 = new Issue
        {
            WorkspaceId = ws.Id,
            Title = "First Issue",
            Description = "Base issue",
            Status = IssueOverallStatus.Closed
        };
        await repo.CreateAsync(issue1);

        var issue2 = new Issue
        {
            WorkspaceId = ws.Id,
            Title = "Dependent Issue",
            Description = "Depends on First Issue",
            Status = IssueOverallStatus.Open
        };
        await repo.CreateAsync(issue2, dependsOn: new[] { issue1.Id });

        var prereqs = await depStore.GetPrerequisiteIssueIdsAsync(issue2.Id);
        Assert.Single(prereqs);
        Assert.Equal(issue1.Id, prereqs[0]);

        var dependents = await depStore.GetDependentIssueIdsAsync(issue1.Id);
        Assert.Single(dependents);
        Assert.Equal(issue2.Id, dependents[0]);

        var status1 = await depStore.GetIssueStatusAsync(issue1.Id);
        Assert.Equal(IssueOverallStatus.Closed, status1);
    }

    [Fact]
    public async Task CascadeDelete_RemovesChildEntitiesWhenIssueDeleted()
    {
        using var context = new AppDbContext(_options);
        await context.Database.EnsureCreatedAsync();

        var ws = new Workspace { Name = "CascadeWS", RepositoryPath = "/path", DefaultBaseBranch = "main" };
        context.Workspaces.Add(ws);
        await context.SaveChangesAsync();

        var issue = new Issue
        {
            WorkspaceId = ws.Id,
            Title = "Parent Issue"
        };
        context.Issues.Add(issue);
        await context.SaveChangesAsync();

        context.Questions.Add(new Question { IssueId = issue.Id, StageId = "s1", Prompt = "Q1?" });
        context.ExecutionLogs.Add(new ExecutionLog { IssueId = issue.Id, Source = "Test", Message = "Log 1" });
        context.StageCheckpoints.Add(new StageCheckpoint { IssueId = issue.Id, StageId = "s1", SerializedMemory = "{}", GitCommitHash = "abc" });
        await context.SaveChangesAsync();

        // Delete issue
        context.Issues.Remove(issue);
        await context.SaveChangesAsync();

        // Verify child records cascaded
        Assert.Empty(await context.Questions.Where(q => q.IssueId == issue.Id).ToListAsync());
        Assert.Empty(await context.ExecutionLogs.Where(l => l.IssueId == issue.Id).ToListAsync());
        Assert.Empty(await context.StageCheckpoints.Where(c => c.IssueId == issue.Id).ToListAsync());
    }
}
