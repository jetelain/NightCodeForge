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
        var repo = new EfCoreTaskRepository(new AppDbContextFactory(_options));

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
        Assert.Equal($"feature/task-{created.Id}", created.FeatureBranch);

        // Verify history baseline
        var history = await repo.GetHistoryAsync(created.Id, CancellationToken.None);
        Assert.Single(history);
        var baseline = history[0];
        Assert.Equal(1, baseline.Version);
        Assert.Equal("Feature Implementation", baseline.Title);
        Assert.Equal("Initial requirement", baseline.Description);
        Assert.Equal($"feature/task-{created.Id}", baseline.FeatureBranch);
        Assert.Equal(AuditActors.User, baseline.ChangedBy);
        Assert.Equal("Initial task creation", baseline.ChangeReason);
        Assert.NotNull(baseline.SerializedDependencies);
        Assert.Contains(dep1.Id.ToString(), baseline.SerializedDependencies);
        Assert.Contains(dep2.Id.ToString(), baseline.SerializedDependencies);

        // Verify GetHistoryByTaskIdAsync alias returns identical result
        var aliasHistory = await repo.GetHistoryByTaskIdAsync(created.Id, CancellationToken.None);
        Assert.Single(aliasHistory);
        Assert.Equal(baseline.Id, aliasHistory[0].Id);

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
        var repo = new EfCoreTaskRepository(new AppDbContextFactory(_options));

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
        var repo = new EfCoreTaskRepository(new AppDbContextFactory(_options));

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

    [Fact]
    public async Task CreateWithBaselineAsync_PopulatesTaskDependenciesOnEntity()
    {
        using var context = new AppDbContext(_options);
        var repo = new EfCoreTaskRepository(new AppDbContextFactory(_options));

        var workspace = new Workspace
        {
            Name = "DepSyncRepo",
            RepositoryPath = "/test/repo-depsync",
            DefaultBaseBranch = "main"
        };
        context.Workspaces.Add(workspace);
        await context.SaveChangesAsync();

        var depTask = new ForgeTask { WorkspaceId = workspace.Id, Title = "Prereq", Status = TaskOverallStatus.Open };
        context.Tasks.Add(depTask);
        await context.SaveChangesAsync();

        var task = new ForgeTask
        {
            WorkspaceId = workspace.Id,
            Title = "Task with Deps",
            Status = TaskOverallStatus.Open
        };

        var created = await repo.CreateWithBaselineAsync(task, new[] { depTask.Id }, AuditActors.User, "Created with deps", CancellationToken.None);

        Assert.NotNull(created.Dependencies);
        Assert.Single(created.Dependencies);
        Assert.Equal(depTask.Id, created.Dependencies.First().DependsOnTaskId);
    }

    [Fact]
    public async Task UpdateWithHistoryAsync_WhenDependsOnNull_PreservesExistingDependenciesAndSetsOnEntity()
    {
        using var context = new AppDbContext(_options);
        var repo = new EfCoreTaskRepository(new AppDbContextFactory(_options));

        var workspace = new Workspace
        {
            Name = "PreserveDepRepo",
            RepositoryPath = "/test/repo-preserve",
            DefaultBaseBranch = "main"
        };
        context.Workspaces.Add(workspace);
        await context.SaveChangesAsync();

        var depTask = new ForgeTask { WorkspaceId = workspace.Id, Title = "Prereq 1", Status = TaskOverallStatus.Open };
        context.Tasks.Add(depTask);
        await context.SaveChangesAsync();

        var task = new ForgeTask
        {
            WorkspaceId = workspace.Id,
            Title = "Original",
            Status = TaskOverallStatus.Open
        };
        var created = await repo.CreateWithBaselineAsync(task, new[] { depTask.Id }, AuditActors.User, "Created", CancellationToken.None);

        // Update with dependsOn = null
        created.Title = "Updated Title";
        await repo.UpdateWithHistoryAsync(created, null, AuditActors.User, "Title update", CancellationToken.None);

        Assert.NotNull(created.Dependencies);
        Assert.Single(created.Dependencies);
        Assert.Equal(depTask.Id, created.Dependencies.First().DependsOnTaskId);

        var history = await repo.GetHistoryAsync(created.Id, CancellationToken.None);
        Assert.Equal(2, history.Count);
        Assert.Contains(depTask.Id.ToString(), history[1].SerializedDependencies ?? "");
    }

    [Fact]
    public async Task UpdateWithHistoryAsync_WhenDependsOnProvided_UpdatesDependenciesAndSetsOnEntity()
    {
        using var context = new AppDbContext(_options);
        var repo = new EfCoreTaskRepository(new AppDbContextFactory(_options));

        var workspace = new Workspace
        {
            Name = "UpdateDepRepo",
            RepositoryPath = "/test/repo-updatedep",
            DefaultBaseBranch = "main"
        };
        context.Workspaces.Add(workspace);
        await context.SaveChangesAsync();

        var dep1 = new ForgeTask { WorkspaceId = workspace.Id, Title = "D1", Status = TaskOverallStatus.Open };
        var dep2 = new ForgeTask { WorkspaceId = workspace.Id, Title = "D2", Status = TaskOverallStatus.Open };
        context.Tasks.AddRange(dep1, dep2);
        await context.SaveChangesAsync();

        var task = new ForgeTask
        {
            WorkspaceId = workspace.Id,
            Title = "Target Task",
            Status = TaskOverallStatus.Open
        };
        var created = await repo.CreateWithBaselineAsync(task, new[] { dep1.Id }, AuditActors.User, "Created", CancellationToken.None);

        // Now replace with dep2
        await repo.UpdateWithHistoryAsync(created, new[] { dep2.Id }, AuditActors.User, "Swapped dependency", CancellationToken.None);

        Assert.NotNull(created.Dependencies);
        Assert.Single(created.Dependencies);
        Assert.Equal(dep2.Id, created.Dependencies.First().DependsOnTaskId);

        var dbDeps = await repo.GetDependenciesAsync(created.Id, CancellationToken.None);
        Assert.Single(dbDeps);
        Assert.Equal(dep2.Id, dbDeps[0]);
    }

    [Fact]
    public async Task UpdateAsync_WhenTaskUpdatedConcurrentlyWithHistory_PreservesMaintainerFieldsAndUpdatesInMemoryTask()
    {
        using var context = new AppDbContext(_options);
        var repo = new EfCoreTaskRepository(new AppDbContextFactory(_options));

        var workspace = new Workspace
        {
            Name = "ConcurrencyTestRepo",
            RepositoryPath = "/test/repo-concurrency",
            DefaultBaseBranch = "main"
        };
        context.Workspaces.Add(workspace);
        await context.SaveChangesAsync();

        var task = new ForgeTask
        {
            WorkspaceId = workspace.Id,
            Title = "Original Title",
            Description = "Original Description",
            Priority = 2,
            MergeMode = MergeMode.ManualReviewRequired,
            Status = TaskOverallStatus.Open,
            CurrentStageId = "stage1"
        };
        var created = await repo.CreateWithBaselineAsync(task, null, AuditActors.User, "Created", CancellationToken.None);

        // Simulate background orchestrator holding a copy of 'created' (Version 1, ManualReviewRequired)
        var staleInMemoryTask = new ForgeTask
        {
            Id = created.Id,
            WorkspaceId = created.WorkspaceId,
            Title = created.Title,
            Description = created.Description,
            Priority = created.Priority,
            MergeMode = created.MergeMode,
            Status = created.Status,
            Version = created.Version, // 1
            CurrentStageId = "stage2" // orchestrator advancing stage
        };

        // User changes MergeMode to Automatic via UpdateWithHistoryAsync in UI
        created.MergeMode = MergeMode.Automatic;
        created.Priority = 1;
        await repo.UpdateWithHistoryAsync(created, null, AuditActors.User, "User enabled auto-merge", CancellationToken.None);

        // Orchestrator updates execution state via UpdateAsync with its stale task
        await repo.UpdateAsync(staleInMemoryTask, null, CancellationToken.None);

        // Authoritative task in database must have preserved Automatic merge mode and Priority 1
        var dbTask = await repo.GetByIdAsync(created.Id, CancellationToken.None);
        Assert.NotNull(dbTask);
        Assert.Equal(MergeMode.Automatic, dbTask.MergeMode);
        Assert.Equal(1, dbTask.Priority);
        Assert.Equal(2, dbTask.Version);
        Assert.Equal("stage2", dbTask.CurrentStageId);

        // Stale in-memory task must have been synced with latest maintainer fields
        Assert.Equal(MergeMode.Automatic, staleInMemoryTask.MergeMode);
        Assert.Equal(1, staleInMemoryTask.Priority);
        Assert.Equal(2, staleInMemoryTask.Version);
    }

    [Fact]
    public async Task UpdateWithHistoryAsync_PersistsCheckpointMutations_WhenLoadedAsNoTracking()
    {
        using var context = new AppDbContext(_options);
        var repo = new EfCoreTaskRepository(new AppDbContextFactory(_options));

        var workspace = new Workspace
        {
            Name = "CheckpointSyncRepo",
            RepositoryPath = "/test/repo-checkpoints",
            DefaultBaseBranch = "main"
        };
        context.Workspaces.Add(workspace);
        await context.SaveChangesAsync();

        var created = await repo.CreateWithBaselineAsync(new ForgeTask
        {
            WorkspaceId = workspace.Id,
            Title = "Task with Checkpoint",
            Status = TaskOverallStatus.Open,
            CurrentStageId = "Implementation"
        }, null, AuditActors.User, "Created", CancellationToken.None);

        await repo.AddCheckpointAsync(new StageCheckpoint
        {
            TaskId = created.Id,
            StageId = "Implementation",
            SerializedMemory = "[{\"Role\":\"user\",\"Text\":\"stale checkpoint memory\"}]",
            CreatedAt = DateTime.UtcNow
        });

        var loaded = await repo.GetByIdAsync(created.Id, CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Single(loaded.Checkpoints);
        Assert.NotEmpty(loaded.Checkpoints.First().SerializedMemory);

        foreach (var checkpoint in loaded.Checkpoints)
        {
            checkpoint.SerializedMemory = string.Empty;
        }

        await repo.UpdateWithHistoryAsync(loaded, null, AuditActors.User, "Stage retried", CancellationToken.None);

        using var verifyContext = new AppDbContext(_options);
        var verifyRepo = new EfCoreTaskRepository(new AppDbContextFactory(_options));
        var reloaded = await verifyRepo.GetByIdAsync(created.Id, CancellationToken.None);
        Assert.NotNull(reloaded);
        Assert.Single(reloaded.Checkpoints);
        Assert.Equal(string.Empty, reloaded.Checkpoints.First().SerializedMemory);
    }
}
