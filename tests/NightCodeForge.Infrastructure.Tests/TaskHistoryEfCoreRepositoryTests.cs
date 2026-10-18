using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NightCodeForge.Core.Domain;
using NightCodeForge.Infrastructure.Data;
using NightCodeForge.Infrastructure.Data.Repositories;

namespace NightCodeForge.Infrastructure.Tests;

public class TaskHistoryEfCoreRepositoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public TaskHistoryEfCoreRepositoryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var context = new AppDbContext(_options);
        context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    [Fact]
    public async Task CreateWithBaselineAsync_CreatesTaskAndBaselineHistoryVersionOne()
    {
        using var context = new AppDbContext(_options);
        var repo = new EfCoreTaskRepository(context);

        var workspace = new Workspace
        {
            Name = "TestRepo",
            RepositoryPath = "/test/repo",
            DefaultBaseBranch = "main"
        };
        context.Workspaces.Add(workspace);
        await context.SaveChangesAsync();

        var dep1 = new ForgeTask { WorkspaceId = workspace.Id, Title = "Dependency 1", Status = TaskOverallStatus.Open };
        var dep2 = new ForgeTask { WorkspaceId = workspace.Id, Title = "Dependency 2", Status = TaskOverallStatus.Open };
        context.Tasks.AddRange(dep1, dep2);
        await context.SaveChangesAsync();

        var newTask = new ForgeTask
        {
            WorkspaceId = workspace.Id,
            Title = "Feature Implementation",
            Description = "Initial requirement",
            Priority = 2,
            Status = TaskOverallStatus.Suggested,
            CurrentStageId = "task-review"
        };

        var created = await repo.CreateWithBaselineAsync(
            newTask,
            new[] { dep1.Id, dep2.Id },
            AuditActors.User,
            "Initial task creation",
            CancellationToken.None);

        Assert.True(created.Id > 0);
        Assert.Equal(1, created.Version);

        // Verify history baseline
        var history = await repo.GetHistoryAsync(created.Id, CancellationToken.None);
        Assert.Single(history);
        var baseline = history[0];
        Assert.Equal(1, baseline.Version);
        Assert.Equal("Feature Implementation", baseline.Title);
        Assert.Equal("Initial requirement", baseline.Description);
        Assert.Equal(AuditActors.User, baseline.ChangedBy);
        Assert.Equal("Initial task creation", baseline.ChangeReason);
        Assert.NotNull(baseline.SerializedDependencies);
        Assert.Contains(dep1.Id.ToString(), baseline.SerializedDependencies);
        Assert.Contains(dep2.Id.ToString(), baseline.SerializedDependencies);

        // Verify max version
        var maxVer = await repo.GetMaxHistoryVersionAsync(created.Id, CancellationToken.None);
        Assert.Equal(1, maxVer);

        // Verify dependencies
        var deps = await repo.GetDependenciesAsync(created.Id, CancellationToken.None);
        Assert.Equal(2, deps.Count);
        Assert.Contains(dep1.Id, deps);
        Assert.Contains(dep2.Id, deps);
    }

    [Fact]
    public async Task UpdateWithHistoryAsync_IncrementsVersion_AndAppendsHistorySnapshot()
    {
        using var context = new AppDbContext(_options);
        var repo = new EfCoreTaskRepository(context);

        var workspace = new Workspace
        {
            Name = "TestRepo2",
            RepositoryPath = "/test/repo2",
            DefaultBaseBranch = "main"
        };
        context.Workspaces.Add(workspace);
        await context.SaveChangesAsync();

        var depTask = new ForgeTask { WorkspaceId = workspace.Id, Title = "Dep A", Status = TaskOverallStatus.Open };
        context.Tasks.Add(depTask);
        await context.SaveChangesAsync();

        var task = new ForgeTask
        {
            WorkspaceId = workspace.Id,
            Title = "Original Title",
            Description = "Original Desc",
            Priority = 1,
            Status = TaskOverallStatus.Open
        };

        var created = await repo.CreateWithBaselineAsync(task, null, AuditActors.User, "Created", CancellationToken.None);
        Assert.Equal(1, created.Version);

        // Update task
        created.Title = "Mutated Title";
        created.Description = "Mutated Description";
        created.Priority = 3;

        await repo.UpdateWithHistoryAsync(
            created,
            new[] { depTask.Id },
            AuditActors.GetActorForStage("task-review"),
            "Agent enriched specification",
            CancellationToken.None);

        Assert.Equal(2, created.Version);

        var history = await repo.GetHistoryAsync(created.Id, CancellationToken.None);
        Assert.Equal(2, history.Count);

        var v1 = history[0];
        Assert.Equal(1, v1.Version);
        Assert.Equal("Original Title", v1.Title);

        var v2 = history[1];
        Assert.Equal(2, v2.Version);
        Assert.Equal("Mutated Title", v2.Title);
        Assert.Equal("Mutated Description", v2.Description);
        Assert.Equal(3, v2.Priority);
        Assert.Equal(AuditActors.GetActorForStage("task-review"), v2.ChangedBy);
        Assert.Equal("Agent enriched specification", v2.ChangeReason);
        Assert.Contains(depTask.Id.ToString(), v2.SerializedDependencies ?? "");

        var maxVer = await repo.GetMaxHistoryVersionAsync(created.Id, CancellationToken.None);
        Assert.Equal(2, maxVer);
    }

    [Fact]
    public async Task GetWorkspaceDependenciesAsync_ReturnsAllWorkspaceDependencies()
    {
        using var context = new AppDbContext(_options);
        var repo = new EfCoreTaskRepository(context);

        var workspace = new Workspace
        {
            Name = "DepWorkspace",
            RepositoryPath = "/test/dep-workspace",
            DefaultBaseBranch = "main"
        };
        context.Workspaces.Add(workspace);
        await context.SaveChangesAsync();

        var t1 = new ForgeTask { WorkspaceId = workspace.Id, Title = "T1", Status = TaskOverallStatus.Open };
        var t2 = new ForgeTask { WorkspaceId = workspace.Id, Title = "T2", Status = TaskOverallStatus.Open };
        var t3 = new ForgeTask { WorkspaceId = workspace.Id, Title = "T3", Status = TaskOverallStatus.Open };
        context.Tasks.AddRange(t1, t2, t3);
        await context.SaveChangesAsync();

        context.TaskDependencies.Add(new TaskDependency { TaskId = t2.Id, DependsOnTaskId = t1.Id });
        context.TaskDependencies.Add(new TaskDependency { TaskId = t3.Id, DependsOnTaskId = t1.Id });
        context.TaskDependencies.Add(new TaskDependency { TaskId = t3.Id, DependsOnTaskId = t2.Id });
        await context.SaveChangesAsync();

        var wksDeps = await repo.GetWorkspaceDependenciesAsync(workspace.Id, CancellationToken.None);

        Assert.Equal(3, wksDeps.Count);
        Assert.Empty(wksDeps[t1.Id]);
        Assert.Single(wksDeps[t2.Id]);
        Assert.Contains(t1.Id, wksDeps[t2.Id]);

        Assert.Equal(2, wksDeps[t3.Id].Count);
        Assert.Contains(t1.Id, wksDeps[t3.Id]);
        Assert.Contains(t2.Id, wksDeps[t3.Id]);
    }
}
