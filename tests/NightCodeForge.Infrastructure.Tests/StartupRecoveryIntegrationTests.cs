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
    private readonly SystemProcessRunner _processRunner;
    private readonly GitCliRunner _runner;
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _context;

    private StartupRecoveryService CreateRecoveryService(
        IWorkspaceRepository workspaceRepo,
        ITaskRepository taskRepo,
        ITaskJobQueue jobQueue)
    {
        var mockOps = new Mock<IOperationsControlService>();
        mockOps.Setup(m => m.IsPaused).Returns(false);

        return new(
            workspaceRepo,
            taskRepo,
            _runner,
            new GitWorkspaceManager(_runner, _pathResolver, new Mock<IWslMetadataService>().Object, new Mock<IGitMirrorSyncService>().Object, new Mock<IWorkspaceLockProvider>().Object, _processRunner, NullLogger<GitWorkspaceManager>.Instance),
            CreatePipelineProvider(),
            jobQueue,
            _pathResolver,
            _processRunner,
            new NightCodeForge.Core.Execution.SingleExecutionEnvironmentProvider(new Mock<IExecutionEnvironment>().Object),
            mockOps.Object,
            NullLogger<StartupRecoveryService>.Instance);
    }

    private readonly IWorkspacePathResolver _pathResolver = new WorkspacePathResolver(
        new Mock<IWslMetadataService>().Object,
        Microsoft.Extensions.Options.Options.Create(new ExecutionEnvironmentProviderOptions()));

    public StartupRecoveryIntegrationTests()
    {
        _repo = new TestGitRepository("main");
        _processRunner = new SystemProcessRunner(NullLogger<SystemProcessRunner>.Instance);
        _runner = new GitCliRunner(
            _processRunner,
            _pathResolver,
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

        // Verify task status remains Open and re-enqueued to its stage tier (task-review)
        var updated = await taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(updated);
        Assert.Equal(TaskOverallStatus.Open, updated.Status);
        Assert.Contains(task.Id, jobQueue.TaskReviewQueue);
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
        Assert.DoesNotContain("Host restarted unexpectedly", updatedTask.PipelineDataJson ?? string.Empty);
        Assert.Contains(task.Id, jobQueue.TaskReviewQueue);
    }

    [Fact]
    public async Task ReconcileWorkspacesAsync_WithActiveConversationAndTurns_RehydratesMemoryFromSqlite()
    {
        var workspace = new Workspace
        {
            Name = "Active Turn Workspace",
            RepositoryPath = _repo.RepositoryPath,
            DefaultBaseBranch = "main"
        };
        _context.Workspaces.Add(workspace);
        await _context.SaveChangesAsync();

        var task = new ForgeTask
        {
            WorkspaceId = workspace.Id,
            Title = "Task with Active Turns",
            Status = TaskOverallStatus.Open,
            CurrentStageId = "task-review"
        };
        _context.Tasks.Add(task);
        await _context.SaveChangesAsync();

        var taskRepoInitial = new EfCoreTaskRepository(_context);
        var conv = await taskRepoInitial.StartConversationAsync(
            task.Id,
            "task-review",
            WorkflowStageKind.Agentic,
            attemptIndex: 1,
            title: "Task Review Session");

        await taskRepoInitial.AddConversationTurnAsync(conv.Id, StageTurnRole.User, content: "Implement feature Z");
        await taskRepoInitial.AddConversationTurnAsync(conv.Id, StageTurnRole.Thinking, content: "Analyzing codebase architecture", thinkingTrace: "Analyzing codebase architecture");

        // Simulate reboot: clear EF Core change tracker
        _context.ChangeTracker.Clear();

        var workspaceRepo = new EfCoreWorkspaceRepository(_context);
        var taskRepo = new EfCoreTaskRepository(_context);
        var jobQueue = new FakeTestJobQueue();
        var recoveryService = CreateRecoveryService(workspaceRepo, taskRepo, jobQueue);

        await recoveryService.ReconcileWorkspacesAsync();

        // Verify task state in SQLite
        var updatedTask = await taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(updatedTask);

        var bag = PipelineDataBag.FromJson(updatedTask.PipelineDataJson);
        var memoryJson = bag.Get<string>(PipelineDataBagKeys.RecoveredSerializedMemory);
        Assert.NotNull(memoryJson);

        var items = ConversationHistorySerializer.Deserialize(memoryJson);
        Assert.Equal(3, items.Count); // User prompt, Thinking, System notice replayed as user
        Assert.Equal("user", items[0].Role);
        Assert.Equal("Implement feature Z", items[0].Text);
        Assert.Equal("assistant", items[1].Role);
        Assert.Equal("user", items[2].Role);
        Assert.Contains("Host restarted unexpectedly", items[2].Text);

        // Verify interrupted conversation was marked Interrupted in SQLite for in-place resumption
        var convs = await taskRepo.GetConversationsAsync(task.Id);
        var recoveredConv = convs.FirstOrDefault(c => c.Id == conv.Id);
        Assert.NotNull(recoveredConv);
        Assert.Equal(StageExecutionStatus.Interrupted, recoveredConv.Status);

        Assert.Contains(task.Id, jobQueue.TaskReviewQueue);
        var recoveredTask = await taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(recoveredTask);
        Assert.False(string.IsNullOrWhiteSpace(recoveredTask.LastHangfireJobId));
    }

    [Fact]
    public async Task ReconcileWorkspacesAsync_WithWorkspaceIdGreaterThan1002000_MigratesAbsolutePointersToRelativeAndEnablesGitOperations()
    {
        // 1. Seed the workspace sequence in SQLite to a high ID > 1002000
        const int targetWorkspaceId = 1002042;
        await _context.SeedWorkspaceIdSequenceAsync(targetWorkspaceId);

        // 2. Setup a temporary repository folder with sibling layout (clone and task worktrees)
        var wksRoot = Path.Combine(Path.GetTempPath(), $"ncf_wks_{targetWorkspaceId}_{Guid.NewGuid():N}");
        var cloneDir = Path.Combine(wksRoot, "clone");
        Directory.CreateDirectory(cloneDir);

        try
        {
            using (var p1 = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", "init -b main")
            {
                WorkingDirectory = cloneDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }))
            {
                p1?.WaitForExit();
            }

            using (var p2 = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", "config user.name \"NCF Test\"")
            {
                WorkingDirectory = cloneDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }))
            {
                p2?.WaitForExit();
            }

            using (var p3 = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", "config user.email \"test@ncf.local\"")
            {
                WorkingDirectory = cloneDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }))
            {
                p3?.WaitForExit();
            }

            using (var p4 = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", "commit --allow-empty -m \"Initial root commit on main\" --no-verify")
            {
                WorkingDirectory = cloneDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }))
            {
                p4?.WaitForExit();
            }

            // 3. Register Workspace entity in SQLite
            var workspace = new Workspace
            {
                Name = "Container Portability Workspace",
                RepositoryPath = cloneDir,
                DefaultBaseBranch = "main"
            };
            _context.Workspaces.Add(workspace);
            await _context.SaveChangesAsync();

            Assert.True(workspace.Id >= targetWorkspaceId, $"Expected workspace.Id >= {targetWorkspaceId}, got {workspace.Id}");

            // 4. Register active ForgeTask in SQLite
            var task = new ForgeTask
            {
                WorkspaceId = workspace.Id,
                Title = "Container Git Worktree Portability Task",
                Status = TaskOverallStatus.Open,
                CurrentStageId = "task-review"
            };
            _context.Tasks.Add(task);
            await _context.SaveChangesAsync();

            // 5. Ensure worktree for this task
            var gitManager = new GitWorkspaceManager(_runner, _pathResolver, new Mock<IWslMetadataService>().Object, new Mock<IGitMirrorSyncService>().Object, new Mock<IWorkspaceLockProvider>().Object, _processRunner, NullLogger<GitWorkspaceManager>.Instance);
            var wtPath = await gitManager.EnsureWorktreeAsync(workspace, task.Id, "main");

            var forwardGitFile = Path.Combine(wtPath, ".git");
            var reverseGitDirFile = Path.Combine(cloneDir, ".git", "worktrees", $"task-{task.Id}", "gitdir");

            // 6. Simulate corrupt / pre-Step-11.22 absolute pointers crossing container namespace boundary
            File.SetAttributes(forwardGitFile, FileAttributes.Normal);
            File.SetAttributes(reverseGitDirFile, FileAttributes.Normal);
            await File.WriteAllTextAsync(forwardGitFile, $"gitdir: /home/gruearbre/.nightcodeforge/wks/{workspace.Id}/clone/.git/worktrees/task-{task.Id}\n");
            await File.WriteAllTextAsync(reverseGitDirFile, $"/home/gruearbre/.nightcodeforge/wks/{workspace.Id}/task-{task.Id}/.git\n");

            // Clear EF Core change tracker to simulate full application restart
            _context.ChangeTracker.Clear();

            // 7. Run StartupRecoveryService.ReconcileWorkspacesAsync
            var workspaceRepo = new EfCoreWorkspaceRepository(_context);
            var taskRepo = new EfCoreTaskRepository(_context);
            var jobQueue = new FakeTestJobQueue();
            var recoveryService = CreateRecoveryService(workspaceRepo, taskRepo, jobQueue);

            await recoveryService.ReconcileWorkspacesAsync();

            // 8. Assertions: Both forward and reverse pointers were migrated to relative paths
            var forwardContent = (await File.ReadAllTextAsync(forwardGitFile)).Trim();
            Assert.Equal($"gitdir: ../clone/.git/worktrees/task-{task.Id}", forwardContent);

            var reverseContent = (await File.ReadAllTextAsync(reverseGitDirFile)).Trim();
            Assert.Equal($"../../../../task-{task.Id}/.git", reverseContent);

            // 9. Assertions: Git commands inside the worktree execute cleanly with zero errors
            var statusResult = await _runner.ExecuteAsync(wtPath, ["status", "--porcelain"]);
            Assert.Equal(0, statusResult.ExitCode);
            Assert.DoesNotContain("fatal: not a git repository", statusResult.StandardError, StringComparison.OrdinalIgnoreCase);

            var branchResult = await _runner.ExecuteAsync(wtPath, ["branch", "--show-current"]);
            Assert.Equal(0, branchResult.ExitCode);
            Assert.Equal($"feature/task-{task.Id}", branchResult.StandardOutput.Trim());

            var logResult = await _runner.ExecuteAsync(wtPath, ["log", "-1", "--oneline"]);
            Assert.Equal(0, logResult.ExitCode);
            Assert.Contains("Initial root commit", logResult.StandardOutput);

            // 10. Verify task re-enqueued to task-review queue
            Assert.Contains(task.Id, jobQueue.TaskReviewQueue);

            // 11. Verify ExecutionEnvironmentProvider also normalizes paths
            var envProvider = new ExecutionEnvironmentProvider(
                new BareHostExecutionEnvironment(_processRunner, _pathResolver),
                _processRunner,
                Microsoft.Extensions.Options.Options.Create(new ExecutionEnvironmentProviderOptions()),
                _pathResolver,
                new Mock<IHostSdkDiscoveryService>().Object,
                gitManager,
                NullLogger<NightCodeForgeContainerExecutor>.Instance,
                new Mock<IHostUserIdentityResolver>().Object,
                new Mock<IOperationsTelemetryService>().Object,
                new Mock<ICompanionToolInstaller>().Object);

            var env = envProvider.GetEnvironment(workspace, taskId: task.Id);
            Assert.NotNull(env);
        }
        finally
        {
            TestGitRepository.DeleteDirectorySafe(wksRoot);
        }
    }

    [Fact]
    public async Task ReconcileWorkspacesAsync_WithInFlightTaskAtCodeReviewStage_PreservesStageAndDoesNotTriggerLegacyNormalization()
    {
        var workspace = new Workspace
        {
            Name = "InFlight Workspace",
            RepositoryPath = _repo.RepositoryPath,
            DefaultBaseBranch = "main"
        };
        _context.Workspaces.Add(workspace);
        await _context.SaveChangesAsync();

        var task = new ForgeTask
        {
            WorkspaceId = workspace.Id,
            Title = "In-flight Code Review Task",
            Status = TaskOverallStatus.Open,
            CurrentStageId = "code-review-fix",
            HasExecutionRun = true
        };
        _context.Tasks.Add(task);
        await _context.SaveChangesAsync();

        // Clear EF Core local tracking to simulate host restart
        _context.ChangeTracker.Clear();

        var workspaceRepo = new EfCoreWorkspaceRepository(_context);
        var taskRepo = new EfCoreTaskRepository(_context);
        var jobQueue = new FakeTestJobQueue();
        var recoveryService = CreateRecoveryService(workspaceRepo, taskRepo, jobQueue);

        await recoveryService.ReconcileWorkspacesAsync();

        // Verify task state in SQLite: must remain Open and CurrentStageId must NOT be changed
        var updatedTask = await taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(updatedTask);
        Assert.Equal(TaskOverallStatus.Open, updatedTask.Status);
        Assert.Equal("code-review-fix", updatedTask.CurrentStageId);

        // Verify re-enqueued to CodeReview queue tier
        Assert.Contains(task.Id, jobQueue.CodeReviewQueue);

        // Verify no legacy status migration history was recorded
        var history = await taskRepo.GetHistoryAsync(task.Id);
        Assert.DoesNotContain(history, h => h.ChangeReason != null && h.ChangeReason.Contains("Legacy status migration"));
    }

    private static IWorkflowPipelineProvider CreatePipelineProvider() =>
        new DefaultWorkflowPipelineProvider(new WorkflowPipeline(new IWorkflowStage[]
        {
            new GenericAgenticStage("task-review", "Task Review", new AgenticStageConfig { RequiredMode = AgentMode.Full }, new ScribanPromptTemplateRenderer(), TimeProvider.System) { QueueTier = StageQueueTier.TaskReview },
            new GenericAgenticStage("implementation", "Implementation", new AgenticStageConfig { RequiredMode = AgentMode.Full }, new ScribanPromptTemplateRenderer(), TimeProvider.System) { QueueTier = StageQueueTier.Implementation },
            new GenericAgenticStage("code-review-fix", "Code Review and Fix", new AgenticStageConfig { RequiredMode = AgentMode.Full }, new ScribanPromptTemplateRenderer(), TimeProvider.System) { QueueTier = StageQueueTier.CodeReview }
        }));
}

public class FakeTestJobQueue : ITaskJobQueue
{
    public List<int> UrgentQueue { get; } = new();
    public List<int> CodeReviewQueue { get; } = new();
    public List<int> ImplementationQueue { get; } = new();
    public List<int> TaskReviewQueue { get; } = new();
    public List<(int TaskId, StageQueueTier Tier, int Priority)> EnqueuedStages { get; } = new();

    public string EnqueueUrgent(int taskId, DateTime? originalEnqueuedAt = null)
    {
        UrgentQueue.Add(taskId);
        return $"urgent-{taskId}";
    }

    public string EnqueueStage(int taskId, StageQueueTier tier, int priority, DateTime? originalEnqueuedAt = null)
    {
        EnqueuedStages.Add((taskId, tier, priority));
        if (priority <= TaskPriority.Critical)
        {
            UrgentQueue.Add(taskId);
            return $"urgent-{taskId}";
        }

        switch (tier)
        {
            case StageQueueTier.CodeReview:
                CodeReviewQueue.Add(taskId);
                return $"cr-{taskId}";
            case StageQueueTier.Implementation:
                ImplementationQueue.Add(taskId);
                return $"impl-{taskId}";
            case StageQueueTier.TaskReview:
                TaskReviewQueue.Add(taskId);
                return $"tr-{taskId}";
            default:
                throw new ArgumentOutOfRangeException(nameof(tier), tier, "Unknown queue tier");
        }
    }

    public bool Delete(string jobId) => true;

    public Task<bool> HasUrgentJobsWaitingAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(UrgentQueue.Count > 0);
    }
}
