using Microsoft.Extensions.Logging;
using Moq;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Tests.Fixtures;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Core.Tests.Workflow;

public class ScheduleOpenTaskOnDependencyRemovalTests
{
    private static (TestOrchestratorBuilder Builder, TaskOrchestrator Orchestrator) CreateOrchestrator(
        InMemoryTaskRepository? taskRepo = null,
        InMemoryTaskDependencyStore? depStore = null,
        FakeTaskJobQueue? jobQueue = null,
        IWorkflowPipelineProvider? pipelineProvider = null)
    {
        var builder = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo ?? new InMemoryTaskRepository(),
            DependencyStore = depStore ?? new InMemoryTaskDependencyStore(),
            JobQueue = jobQueue ?? new FakeTaskJobQueue()
        };

        if (pipelineProvider != null)
        {
            builder.PipelineProvider = pipelineProvider;
        }

        builder.WorkspaceRepository.CreateAsync(new Workspace
        {
            Id = 1,
            Name = "Test Workspace",
            RepositoryPath = "C:/repo",
            DefaultBaseBranch = "main"
        }).GetAwaiter().GetResult();

        return (builder, builder.Build());
    }

    [Fact]
    public async Task TryScheduleOpenTaskIfReadyAsync_OpenTaskWithNoDependencies_EnqueuesToInitialStageUpdatesJobIdAndLogs()
    {
        var taskRepo = new InMemoryTaskRepository();
        var depStore = new InMemoryTaskDependencyStore();
        var jobQueue = new FakeTaskJobQueue();
        var (_, orchestrator) = CreateOrchestrator(taskRepo, depStore, jobQueue);

        var task = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 10,
            WorkspaceId = 1,
            Title = "Unblocked Open Task",
            Status = TaskOverallStatus.Open,
            Priority = TaskPriority.High,
            IsDeferredByPause = true
        });
        depStore.AddTask(task.Id, 1, TaskOverallStatus.Open);

        var scheduled = await orchestrator.TryScheduleOpenTaskIfReadyAsync(task.Id);

        Assert.True(scheduled);
        var enqueued = Assert.Single(jobQueue.EnqueuedStages);
        Assert.Equal(task.Id, enqueued.TaskId);
        Assert.Equal(StageQueueTier.TaskReview, enqueued.Tier);
        Assert.Equal(TaskPriority.High, enqueued.Priority);

        var reloaded = await taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(reloaded);
        Assert.False(reloaded!.IsDeferredByPause);
        Assert.False(string.IsNullOrWhiteSpace(reloaded.LastHangfireJobId));
        Assert.Contains(reloaded.ExecutionLogs, l =>
            l.Level == LogLevel.Information &&
            l.Message.Contains("unblocked and scheduled", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TryScheduleOpenTaskIfReadyAsync_OpenTaskWithValidCurrentStageId_UsesCurrentStageQueueTier()
    {
        var taskRepo = new InMemoryTaskRepository();
        var jobQueue = new FakeTaskJobQueue();
        var (_, orchestrator) = CreateOrchestrator(taskRepo, jobQueue: jobQueue);

        var task = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 11,
            WorkspaceId = 1,
            Title = "Open Task at Implementation Stage",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.Implementation,
            Priority = TaskPriority.Normal
        });

        var scheduled = await orchestrator.TryScheduleOpenTaskIfReadyAsync(task.Id);

        Assert.True(scheduled);
        var enqueued = Assert.Single(jobQueue.EnqueuedStages);
        Assert.Equal(StageQueueTier.Implementation, enqueued.Tier);
        Assert.Contains(task.Id, jobQueue.ImplementationQueue);
    }

    [Fact]
    public async Task TryScheduleOpenTaskIfReadyAsync_OpenTaskWithClosedPrerequisites_EnqueuesCleanly()
    {
        var taskRepo = new InMemoryTaskRepository();
        var depStore = new InMemoryTaskDependencyStore();
        var jobQueue = new FakeTaskJobQueue();
        var (_, orchestrator) = CreateOrchestrator(taskRepo, depStore, jobQueue);

        var closedRepoPrereq = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 20,
            WorkspaceId = 1,
            Title = "Closed Repo Prereq",
            Status = TaskOverallStatus.Closed
        });
        depStore.AddTask(21, 1, TaskOverallStatus.Closed);

        var openTask = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 22,
            WorkspaceId = 1,
            Title = "Ready Open Task",
            Status = TaskOverallStatus.Open
        }, dependsOn: [closedRepoPrereq.Id]);
        depStore.AddTask(openTask.Id, 1, TaskOverallStatus.Open);
        depStore.AddDependency(1, openTask.Id, 21);

        var scheduled = await orchestrator.TryScheduleOpenTaskIfReadyAsync(openTask.Id);

        Assert.True(scheduled);
        Assert.Single(jobQueue.EnqueuedStages);
    }

    [Fact]
    public async Task TryScheduleOpenTaskIfReadyAsync_OpenTaskWithUnclosedPrerequisiteInRepository_DoesNotEnqueue()
    {
        var taskRepo = new InMemoryTaskRepository();
        var depStore = new InMemoryTaskDependencyStore();
        var jobQueue = new FakeTaskJobQueue();
        var (_, orchestrator) = CreateOrchestrator(taskRepo, depStore, jobQueue);

        var closedPrereq = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 30,
            WorkspaceId = 1,
            Title = "Closed Prereq",
            Status = TaskOverallStatus.Closed
        });
        var openPrereq = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 31,
            WorkspaceId = 1,
            Title = "Still Open Prereq",
            Status = TaskOverallStatus.Open
        });
        var target = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 32,
            WorkspaceId = 1,
            Title = "Target Task",
            Status = TaskOverallStatus.Open
        }, dependsOn: [closedPrereq.Id, openPrereq.Id]);

        var scheduled = await orchestrator.TryScheduleOpenTaskIfReadyAsync(target.Id);

        Assert.False(scheduled);
        Assert.Empty(jobQueue.EnqueuedStages);
        Assert.Null((await taskRepo.GetByIdAsync(target.Id))!.LastHangfireJobId);
    }

    [Fact]
    public async Task TryScheduleOpenTaskIfReadyAsync_OpenTaskWithUnclosedPrerequisiteInDependencyStore_DoesNotEnqueue()
    {
        var taskRepo = new InMemoryTaskRepository();
        var depStore = new InMemoryTaskDependencyStore();
        var jobQueue = new FakeTaskJobQueue();
        var (_, orchestrator) = CreateOrchestrator(taskRepo, depStore, jobQueue);

        var target = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 40,
            WorkspaceId = 1,
            Title = "Store Blocked Task",
            Status = TaskOverallStatus.Open
        });
        depStore.AddTask(41, 1, TaskOverallStatus.Reviewed);
        depStore.AddTask(target.Id, 1, TaskOverallStatus.Open);
        depStore.AddDependency(1, target.Id, 41);

        var scheduled = await orchestrator.TryScheduleOpenTaskIfReadyAsync(target.Id);

        Assert.False(scheduled);
        Assert.Empty(jobQueue.EnqueuedStages);
    }

    [Theory]
    [InlineData(TaskOverallStatus.Draft)]
    [InlineData(TaskOverallStatus.Suggested)]
    [InlineData(TaskOverallStatus.Reviewed)]
    [InlineData(TaskOverallStatus.Implemented)]
    [InlineData(TaskOverallStatus.Ready)]
    [InlineData(TaskOverallStatus.Closed)]
    [InlineData(TaskOverallStatus.Rejected)]
    [InlineData(TaskOverallStatus.Cancelled)]
    [InlineData(TaskOverallStatus.Failed)]
    public async Task TryScheduleOpenTaskIfReadyAsync_NonOpenStatus_DoesNotEnqueue(TaskOverallStatus status)
    {
        var taskRepo = new InMemoryTaskRepository();
        var jobQueue = new FakeTaskJobQueue();
        var (_, orchestrator) = CreateOrchestrator(taskRepo, jobQueue: jobQueue);

        var task = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 50,
            WorkspaceId = 1,
            Title = $"Status {status}",
            Status = status
        });

        var scheduled = await orchestrator.TryScheduleOpenTaskIfReadyAsync(task.Id);

        Assert.False(scheduled);
        Assert.Empty(jobQueue.EnqueuedStages);
    }

    [Fact]
    public async Task TryScheduleOpenTaskIfReadyAsync_WhenHasExecutionRunOrWaitingForHuman_DoesNotEnqueue()
    {
        var taskRepo = new InMemoryTaskRepository();
        var depStore = new InMemoryTaskDependencyStore();
        var jobQueue = new FakeTaskJobQueue();
        var (_, orchestrator) = CreateOrchestrator(taskRepo, depStore, jobQueue);

        var executedTask = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 60,
            WorkspaceId = 1,
            Title = "Executed Task",
            Status = TaskOverallStatus.Open,
            HasExecutionRun = true
        });
        var waitingEntityTask = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 61,
            WorkspaceId = 1,
            Title = "Waiting Entity Task",
            Status = TaskOverallStatus.Open,
            IsWaitingForHuman = true
        });
        var waitingStoreTask = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 62,
            WorkspaceId = 1,
            Title = "Waiting Store Task",
            Status = TaskOverallStatus.Open,
            IsWaitingForHuman = false
        });
        depStore.AddTask(waitingStoreTask.Id, 1, TaskOverallStatus.Open, isWaitingForHuman: true);

        Assert.False(await orchestrator.TryScheduleOpenTaskIfReadyAsync(executedTask.Id));
        Assert.False(await orchestrator.TryScheduleOpenTaskIfReadyAsync(waitingEntityTask.Id));
        Assert.False(await orchestrator.TryScheduleOpenTaskIfReadyAsync(waitingStoreTask.Id));
        Assert.Empty(jobQueue.EnqueuedStages);
    }

    [Fact]
    public async Task TryScheduleOpenTaskIfReadyAsync_ActiveJobVsStaleJob_SkipsDuplicateAndReEnqueuesStale()
    {
        var taskRepo = new InMemoryTaskRepository();
        var jobQueue = new FakeTaskJobQueue();
        var (_, orchestrator) = CreateOrchestrator(taskRepo, jobQueue: jobQueue);

        var activeJobTask = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 70,
            WorkspaceId = 1,
            Title = "Active Job Task",
            Status = TaskOverallStatus.Open,
            LastHangfireJobId = "active-job-70"
        });
        var deletedJobTask = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 71,
            WorkspaceId = 1,
            Title = "Deleted Job Task",
            Status = TaskOverallStatus.Open,
            LastHangfireJobId = "deleted-job-71"
        });
        jobQueue.Delete("deleted-job-71");

        var completedJobTask = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 72,
            WorkspaceId = 1,
            Title = "Completed Job Task",
            Status = TaskOverallStatus.Open,
            LastHangfireJobId = "completed-job-72"
        });
        jobQueue.CompletedJobs.Add("completed-job-72");

        Assert.False(await orchestrator.TryScheduleOpenTaskIfReadyAsync(activeJobTask.Id));
        Assert.Equal("active-job-70", (await taskRepo.GetByIdAsync(activeJobTask.Id))!.LastHangfireJobId);

        Assert.True(await orchestrator.TryScheduleOpenTaskIfReadyAsync(deletedJobTask.Id));
        Assert.True(await orchestrator.TryScheduleOpenTaskIfReadyAsync(completedJobTask.Id));

        Assert.Equal(2, jobQueue.EnqueuedStages.Count);
        Assert.NotEqual("deleted-job-71", (await taskRepo.GetByIdAsync(deletedJobTask.Id))!.LastHangfireJobId);
        Assert.NotEqual("completed-job-72", (await taskRepo.GetByIdAsync(completedJobTask.Id))!.LastHangfireJobId);
    }

    [Fact]
    public async Task TryScheduleOpenTaskIfReadyAsync_MissingTaskOrMissingStage_FailsFast()
    {
        var taskRepo = new InMemoryTaskRepository();
        var emptyPipelineProvider = new DefaultWorkflowPipelineProvider(new WorkflowPipeline(Array.Empty<IWorkflowStage>()));
        var (_, orchestrator) = CreateOrchestrator(taskRepo, pipelineProvider: emptyPipelineProvider);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => orchestrator.TryScheduleOpenTaskIfReadyAsync(9999));

        var task = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 80,
            WorkspaceId = 1,
            Title = "No Stages Task",
            Status = TaskOverallStatus.Open
        });

        await Assert.ThrowsAsync<WorkflowValidationException>(() => orchestrator.TryScheduleOpenTaskIfReadyAsync(task.Id));
    }

    [Fact]
    public async Task ProcessTaskAsync_WhenEnsureReadyForExecutionExitsEarly_ClearsStaleLastHangfireJobId()
    {
        var taskRepo = new InMemoryTaskRepository();
        var depStore = new InMemoryTaskDependencyStore();
        var jobQueue = new FakeTaskJobQueue();
        var (_, orchestrator) = CreateOrchestrator(taskRepo, depStore, jobQueue);

        var prereq = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 90,
            WorkspaceId = 1,
            Title = "Unclosed Prerequisite",
            Status = TaskOverallStatus.Open
        });
        var blockedTask = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 91,
            WorkspaceId = 1,
            Title = "Blocked Task With Stale Job",
            Status = TaskOverallStatus.Open,
            LastHangfireJobId = "stale-blocked-job-91"
        }, dependsOn: [prereq.Id]);

        depStore.AddTask(prereq.Id, 1, TaskOverallStatus.Open);
        depStore.AddTask(blockedTask.Id, 1, TaskOverallStatus.Open);
        depStore.AddDependency(1, blockedTask.Id, prereq.Id);

        await orchestrator.ProcessTaskAsync(blockedTask.Id);

        var reloaded = await taskRepo.GetByIdAsync(blockedTask.Id);
        Assert.NotNull(reloaded);
        Assert.Null(reloaded!.LastHangfireJobId);
        Assert.Null(blockedTask.LastHangfireJobId);
    }
}
