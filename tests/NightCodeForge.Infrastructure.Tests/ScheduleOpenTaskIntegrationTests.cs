using Hangfire;
using Hangfire.MemoryStorage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Core.Concurrency;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Streaming;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Infrastructure.BackgroundJobs;
using NightCodeForge.Infrastructure.Data;
using NightCodeForge.Infrastructure.Data.Repositories;

namespace NightCodeForge.Infrastructure.Tests;

public class ScheduleOpenTaskIntegrationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _dbOptions;

    public ScheduleOpenTaskIntegrationTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var context = new AppDbContext(_dbOptions);
        context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    [Fact]
    public async Task EfCoreAndHangfire_RemovingLastBlockingDependencyOnOpenTask_EnqueuesOnceAndHandlesStaleJob()
    {
        var contextFactory = new AppDbContextFactory(_dbOptions);
        var taskRepo = new EfCoreTaskRepository(contextFactory);
        var depStore = new EfCoreTaskDependencyStore(contextFactory);
        var depEngine = new DependencyResolutionEngine(depStore);

        var storage = new MemoryStorage();
        var client = new BackgroundJobClient(storage);
        var jobQueue = new HangfireTaskJobQueue(client, storage, NullLogger<HangfireTaskJobQueue>.Instance);

        var stage = Mock.Of<IWorkflowStage>(s =>
            s.StageId == StandardStageIds.TaskReview &&
            s.DisplayName == "Task Review" &&
            s.QueueTier == StageQueueTier.TaskReview);
        var pipelineProvider = new DefaultWorkflowPipelineProvider(new WorkflowPipeline([stage]));

        var lockProvider = new WorkspaceLockProvider();
        var preparer = new TaskWorkspacePreparer(
            new EfCoreWorkspaceRepository(contextFactory),
            lockProvider,
            Mock.Of<IGitWorkspaceManager>(),
            Mock.Of<IGitMirrorSyncService>(),
            Mock.Of<ITaskMarkdownSyncService>(),
            Mock.Of<IWorkspacePathResolver>(),
            Mock.Of<IExecutionEnvironmentProvider>(),
            Mock.Of<IGitHubPullRequestDeliveryService>(),
            Mock.Of<IGitHubTaskMirrorService>(),
            NullLogger<TaskWorkspacePreparer>.Instance);

        var streamer = new InMemoryAgentActivityStreamer();
        var telemetry = Mock.Of<IOperationsTelemetryService>();
        var mergeCoordinator = new TaskMergeAndCascadeCoordinator(
            taskRepo,
            preparer,
            depEngine,
            depStore,
            jobQueue,
            pipelineProvider,
            Mock.Of<IConflictResolutionVerifier>(),
            streamer,
            telemetry,
            NullLogger<TaskMergeAndCascadeCoordinator>.Instance);

        var cancellationManager = new TaskCancellationManager(NullLogger<TaskCancellationManager>.Instance);
        var lifecycleCoordinator = new TaskLifecycleMutationCoordinator(
            taskRepo,
            preparer,
            mergeCoordinator,
            pipelineProvider,
            jobQueue,
            cancellationManager,
            streamer,
            telemetry,
            NullLogger<TaskLifecycleMutationCoordinator>.Instance);

        var orchestrator = new TaskOrchestrator(
            taskRepo,
            preparer,
            mergeCoordinator,
            lifecycleCoordinator,
            new HarnessRegistry(),
            streamer,
            Mock.Of<IOperationsControlService>(),
            telemetry,
            new EfCoreTaskAttachmentRepository(contextFactory),
            NullLogger<TaskOrchestrator>.Instance);

        int blocker1Id;
        int blocker2Id;
        int openTaskId;
        await using (var setupContext = new AppDbContext(_dbOptions))
        {
            var ws = new Workspace
            {
                Name = "Integration WS",
                RepositoryPath = Path.GetTempPath(),
                DefaultBaseBranch = "main"
            };
            setupContext.Workspaces.Add(ws);
            await setupContext.SaveChangesAsync();

            var blocker1 = new ForgeTask { WorkspaceId = ws.Id, Title = "Blocker 1", Status = TaskOverallStatus.Open };
            var blocker2 = new ForgeTask { WorkspaceId = ws.Id, Title = "Blocker 2", Status = TaskOverallStatus.Open };
            setupContext.Tasks.AddRange(blocker1, blocker2);
            await setupContext.SaveChangesAsync();
            blocker1Id = blocker1.Id;
            blocker2Id = blocker2.Id;

            var target = await taskRepo.CreateWithBaselineAsync(
                new ForgeTask
                {
                    WorkspaceId = ws.Id,
                    Title = "Blocked Open Task",
                    Status = TaskOverallStatus.Open,
                    Priority = TaskPriority.High
                },
                [blocker1Id, blocker2Id],
                AuditActors.User,
                "Initial setup");
            openTaskId = target.Id;
        }

        // Removing one blocker while blocker2 remains unclosed must NOT enqueue
        var loaded = await taskRepo.GetByIdAsync(openTaskId);
        Assert.NotNull(loaded);
        await taskRepo.UpdateWithHistoryAsync(loaded!, [blocker2Id], AuditActors.User, "Removed blocker 1");

        Assert.False(await orchestrator.TryScheduleOpenTaskIfReadyAsync(openTaskId));
        Assert.Null((await taskRepo.GetByIdAsync(openTaskId))!.LastHangfireJobId);

        // Removing the last blocker enqueues the task and persists LastHangfireJobId
        loaded = await taskRepo.GetByIdAsync(openTaskId);
        await taskRepo.UpdateWithHistoryAsync(loaded!, Array.Empty<int>(), AuditActors.User, "Removed blocker 2");

        Assert.True(await orchestrator.TryScheduleOpenTaskIfReadyAsync(openTaskId));
        var firstJobId = (await taskRepo.GetByIdAsync(openTaskId))!.LastHangfireJobId;
        Assert.False(string.IsNullOrWhiteSpace(firstJobId));
        Assert.True(jobQueue.IsJobActive(firstJobId));

        // Re-evaluating while the job is still Enqueued must NOT create a duplicate job
        Assert.False(await orchestrator.TryScheduleOpenTaskIfReadyAsync(openTaskId));
        Assert.Equal(firstJobId, (await taskRepo.GetByIdAsync(openTaskId))!.LastHangfireJobId);

        // After deleting the stale job, re-evaluating creates a fresh Hangfire job
        Assert.True(jobQueue.Delete(firstJobId!));
        Assert.False(jobQueue.IsJobActive(firstJobId));
        Assert.True(await orchestrator.TryScheduleOpenTaskIfReadyAsync(openTaskId));
        var secondJobId = (await taskRepo.GetByIdAsync(openTaskId))!.LastHangfireJobId;
        Assert.False(string.IsNullOrWhiteSpace(secondJobId));
        Assert.NotEqual(firstJobId, secondJobId);
    }
}
