using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Execution;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Core.Workflow.Configuration;
using NightCodeForge.Core.Workflow.Stages;
using NightCodeForge.Infrastructure.Data;
using NightCodeForge.Infrastructure.Data.Repositories;
using NightCodeForge.Infrastructure.Execution;
using NightCodeForge.Infrastructure.Git;
using NightCodeForge.Infrastructure.Tests.Git;

namespace NightCodeForge.Infrastructure.Tests;

public class StartupRecoveryIntegrationTests : IDisposable
{
    private readonly TestGitRepository _repo;
    private readonly GitCliRunner _runner;
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _context;

    private StartupRecoveryService CreateRecoveryService(
        IWorkspaceRepository workspaceRepo,
        ITaskRepository taskRepo,
        ITaskJobQueue jobQueue) =>
        new(
            workspaceRepo,
            taskRepo,
            _runner,
            CreatePipelineProvider(),
            jobQueue,
            new WorkspacePathResolver(),
            NullLogger<StartupRecoveryService>.Instance);

    public StartupRecoveryIntegrationTests()
    {
        _repo = new TestGitRepository("main");
        _runner = new GitCliRunner(
            new SystemProcessRunner(NullLogger<SystemProcessRunner>.Instance),
            new WorkspacePathResolver(),
            new Mock<IWslMetadataService>().Object,
            NullLogger<GitCliRunner>.Instance);

        // Initial base commit
        _repo.CreateFile("README.md", "# Test Repository\nInitial commit.");
        _repo.CommitAll("Initial commit");

        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        _context = new AppDbContext(dbOptions);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
        _repo.Dispose();
    }

    [Fact]
    public async Task ReconcileWorkspacesAsync_WithRealGitRepo_PurgesLocksAndCreatesWipRecoveryCommit()
    {
        // 1. Create a dangling .git/index.lock
        var gitDir = Path.Combine(_repo.RepositoryPath, ".git");
        var lockFile = Path.Combine(gitDir, "index.lock");
        await File.WriteAllTextAsync(lockFile, "stale lock");
        Assert.True(File.Exists(lockFile));

        // 2. Create dirty uncommitted changes
        _repo.CreateFile("DirtyFile.cs", "public class DirtyFile {}");

        // 3. Register workspace and running task in DB
        var workspace = new Workspace
        {
            Name = "Integration Workspace",
            RepositoryPath = _repo.RepositoryPath,
            DefaultBaseBranch = "main"
        };
        _context.Workspaces.Add(workspace);
        await _context.SaveChangesAsync();

        var task = new ForgeTask
        {
            WorkspaceId = workspace.Id,
            Title = "Running ForgeTask Interrupted",
            Status = TaskOverallStatus.Open,
            CurrentStageId = "task-review"
        };
        _context.Tasks.Add(task);
        await _context.SaveChangesAsync();

        var workspaceRepo = new EfCoreWorkspaceRepository(_context);
        var taskRepo = new EfCoreTaskRepository(_context);
        var jobQueue = new FakeTestJobQueue();

        var recoveryService = CreateRecoveryService(workspaceRepo, taskRepo, jobQueue);

        // Run recovery
        await recoveryService.ReconcileWorkspacesAsync();

        // Verify lock is purged
        Assert.False(File.Exists(lockFile));

        // Verify git status is clean now (rescue commit created)
        var status = await _runner.ExecuteAsync(_repo.RepositoryPath, ["status", "--porcelain"]);
        Assert.True(string.IsNullOrWhiteSpace(status.StandardOutput));

        // Verify git log contains WIP-RECOVERY commit
        var log = await _runner.ExecuteAsync(_repo.RepositoryPath, ["log", "-n", "1", "--oneline"]);
        Assert.Contains("WIP-RECOVERY", log.StandardOutput);

        // Verify task status remains Open and re-enqueued to urgent
        var updated = await taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(updated);
        Assert.Equal(TaskOverallStatus.Open, updated.Status);
        Assert.Contains(task.Id, jobQueue.UrgentQueue);
    }

    [Fact]
    public async Task ReconcileWorkspacesAsync_OnHostRebootWithAgenticCheckpoint_RehydratesMemoryWithoutWorkspaceTrackingConflict()
    {
        // 1. Setup workspace and task in DB with agentic stage and checkpoint
        var workspace = new Workspace
        {
            Name = "Reboot Workspace",
            RepositoryPath = _repo.RepositoryPath,
            DefaultBaseBranch = "main"
        };
        _context.Workspaces.Add(workspace);
        await _context.SaveChangesAsync();

        var task = new ForgeTask
        {
            WorkspaceId = workspace.Id,
            Title = "Orphaned Agentic Task",
            Status = TaskOverallStatus.Open,
            CurrentStageId = "task-review"
        };
        _context.Tasks.Add(task);
        await _context.SaveChangesAsync();

        _context.StageCheckpoints.Add(new StageCheckpoint
        {
            TaskId = task.Id,
            StageId = "task-review",
            SerializedMemory = "{\"thoughts\":\"analyzing\"}",
            CreatedAt = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();

        // Simulate host restart: clear ChangeTracker so nothing is locally tracked
        _context.ChangeTracker.Clear();

        var workspaceRepo = new EfCoreWorkspaceRepository(_context);
        var taskRepo = new EfCoreTaskRepository(_context);
        var jobQueue = new FakeTestJobQueue();

        var recoveryService = CreateRecoveryService(workspaceRepo, taskRepo, jobQueue);

        // Run recovery - previously threw:
        // System.InvalidOperationException: The instance of entity type 'Workspace' cannot be tracked because another instance with the same key value for {'Id'} is already being tracked.
        var ex = await Record.ExceptionAsync(() => recoveryService.ReconcileWorkspacesAsync());
        Assert.Null(ex);

        // Verify rehydrated memory
        var updatedTask = await taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(updatedTask);
        Assert.Contains("[System Notice: Host restarted unexpectedly; workspace restored]", updatedTask.PipelineDataJson);
        Assert.Contains(task.Id, jobQueue.UrgentQueue);
    }

    private static IWorkflowPipelineProvider CreatePipelineProvider() =>
        new DefaultWorkflowPipelineProvider(new WorkflowPipeline(new IWorkflowStage[]
        {
            new GenericAgenticStage("task-review", "Task Review", new AgenticStageConfig { RequiredMode = AgentMode.Full }),
            new GenericAgenticStage("implementation", "Implementation", new AgenticStageConfig { RequiredMode = AgentMode.Full })
        }));
}

public class FakeTestJobQueue : ITaskJobQueue
{
    public List<int> UrgentQueue { get; } = new();
    public List<int> InferenceQueue { get; } = new();

    public string EnqueueUrgent(int taskId)
    {
        UrgentQueue.Add(taskId);
        return $"urgent-{taskId}";
    }

    public string EnqueueInference(int taskId)
    {
        InferenceQueue.Add(taskId);
        return $"inference-{taskId}";
    }
}
