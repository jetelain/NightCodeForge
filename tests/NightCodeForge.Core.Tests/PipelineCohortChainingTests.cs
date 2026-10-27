namespace NightCodeForge.Core.Tests;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using NightCodeForge.Core.Concurrency;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Tests.Fixtures;
using NightCodeForge.Core.Workflow;
using Xunit;

public class PipelineCohortChainingTests
{
    private readonly Workspace _workspace = new()
    {
        Id = 1,
        Name = "CohortWorkspace",
        RepositoryPath = "C:/test/repo",
        DefaultBaseBranch = "main"
    };

    [Fact]
    public async Task CohortChaining_Stages234_ExecuteWithinSingleOrchestratorInvocation()
    {
        var task = new ForgeTask
        {
            Id = 100,
            WorkspaceId = _workspace.Id,
            BaseBranch = "main",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.PreImplementationBaseline
        };

        var taskRepo = new InMemoryTaskRepository();
        await taskRepo.CreateAsync(task);
        var workspaceRepo = new InMemoryWorkspaceRepository();
        await workspaceRepo.CreateAsync(_workspace);
        var depStore = new InMemoryTaskDependencyStore();
        depStore.AddTask(task.Id, task.WorkspaceId, task.Status);

        var executedStages = new List<string>();

        var stage2 = new TestWorkflowStage(StandardStageIds.PreImplementationBaseline, WorkflowStageKind.Deterministic)
        {
            QueueTier = StageQueueTier.Implementation,
            Handler = (ctx, ct) => { executedStages.Add(StandardStageIds.PreImplementationBaseline); return Task.FromResult(StageExecutionResult.Success()); }
        };
        var stage3 = new TestWorkflowStage(StandardStageIds.Implementation, WorkflowStageKind.Deterministic)
        {
            QueueTier = StageQueueTier.Implementation,
            Handler = (ctx, ct) => { executedStages.Add(StandardStageIds.Implementation); return Task.FromResult(StageExecutionResult.Success()); }
        };
        var stage4 = new TestWorkflowStage(StandardStageIds.PostImplementationGates, WorkflowStageKind.Deterministic)
        {
            QueueTier = StageQueueTier.Implementation,
            Handler = (ctx, ct) => { executedStages.Add(StandardStageIds.PostImplementationGates); return Task.FromResult(StageExecutionResult.Success()); }
        };
        var stage5 = new TestWorkflowStage(StandardStageIds.CodeReviewAndFix, WorkflowStageKind.Deterministic)
        {
            QueueTier = StageQueueTier.CodeReview,
            Handler = (ctx, ct) => { executedStages.Add(StandardStageIds.CodeReviewAndFix); return Task.FromResult(StageExecutionResult.Success()); }
        };

        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stage2, stage3, stage4, stage5 });
        var jobQueue = new FakeTaskJobQueue();

        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = workspaceRepo,
            DependencyStore = depStore,
            PipelineProvider = new DefaultWorkflowPipelineProvider(pipeline),
            JobQueue = jobQueue
        }.Build();

        // Act: Execute single orchestrator invocation
        await orchestrator.ProcessTaskAsync(task.Id);

        // Assert: Stages 2, 3, 4 executed in that single invocation
        Assert.Equal(new[]
        {
            StandardStageIds.PreImplementationBaseline,
            StandardStageIds.Implementation,
            StandardStageIds.PostImplementationGates
        }, executedStages);

        var updated = await taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(updated);
        // Cohort boundary reached: stage 5 enqueued on CodeReview queue, task not yet executed Stage 5
        Assert.Equal(StandardStageIds.CodeReviewAndFix, updated.CurrentStageId);
        Assert.Single(jobQueue.CodeReviewQueue);
        Assert.Equal(task.Id, jobQueue.CodeReviewQueue[0]);
    }

    [Fact]
    public async Task CohortLoopback_Stage4Failure_ReExecutesStage3InProcessWithoutHangfireEnqueue()
    {
        var task = new ForgeTask
        {
            Id = 101,
            WorkspaceId = _workspace.Id,
            BaseBranch = "main",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.Implementation
        };

        var taskRepo = new InMemoryTaskRepository();
        await taskRepo.CreateAsync(task);
        var workspaceRepo = new InMemoryWorkspaceRepository();
        await workspaceRepo.CreateAsync(_workspace);
        var depStore = new InMemoryTaskDependencyStore();
        depStore.AddTask(task.Id, task.WorkspaceId, task.Status);

        int stage3Runs = 0;
        int stage4Runs = 0;

        var stage3 = new TestWorkflowStage(StandardStageIds.Implementation, WorkflowStageKind.Deterministic)
        {
            QueueTier = StageQueueTier.Implementation,
            Handler = (ctx, ct) =>
            {
                stage3Runs++;
                return Task.FromResult(StageExecutionResult.Success());
            }
        };

        var stage4 = new TestWorkflowStage(StandardStageIds.PostImplementationGates, WorkflowStageKind.Deterministic)
        {
            QueueTier = StageQueueTier.Implementation,
            Handler = (ctx, ct) =>
            {
                stage4Runs++;
                if (stage4Runs == 1)
                {
                    // Fail once with loopback to stage 3
                    return Task.FromResult(StageExecutionResult.Failure(
                        "Compiler errors",
                        loopBackStageId: StandardStageIds.Implementation));
                }
                return Task.FromResult(StageExecutionResult.Success());
            }
        };

        var stage5 = new TestWorkflowStage(StandardStageIds.CodeReviewAndFix, WorkflowStageKind.Deterministic)
        {
            QueueTier = StageQueueTier.CodeReview
        };

        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stage3, stage4, stage5 });
        var jobQueue = new FakeTaskJobQueue();

        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = workspaceRepo,
            DependencyStore = depStore,
            PipelineProvider = new DefaultWorkflowPipelineProvider(pipeline),
            JobQueue = jobQueue
        }.Build();

        await orchestrator.ProcessTaskAsync(task.Id);

        // Stage 3 ran twice (first pass + in-process loopback pass)
        Assert.Equal(2, stage3Runs);
        Assert.Equal(2, stage4Runs);

        // Job queue was NOT enqueued during the loopback between 4 and 3
        Assert.Empty(jobQueue.ImplementationQueue);

        // Cohort finished and reached Stage 5
        var updated = await taskRepo.GetByIdAsync(task.Id);
        Assert.Equal(StandardStageIds.CodeReviewAndFix, updated!.CurrentStageId);
        Assert.Single(jobQueue.CodeReviewQueue);
    }

    [Fact]
    public async Task CohortCrossCohortLoopback_Stage6Failure_ReEnqueuesCohort1OnTier2AndExits()
    {
        var task = new ForgeTask
        {
            Id = 102,
            WorkspaceId = _workspace.Id,
            BaseBranch = "main",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.PostReviewVerification
        };

        var taskRepo = new InMemoryTaskRepository();
        await taskRepo.CreateAsync(task);
        var workspaceRepo = new InMemoryWorkspaceRepository();
        await workspaceRepo.CreateAsync(_workspace);
        var depStore = new InMemoryTaskDependencyStore();
        depStore.AddTask(task.Id, task.WorkspaceId, task.Status);

        var stage3 = new TestWorkflowStage(StandardStageIds.Implementation, WorkflowStageKind.Deterministic)
        {
            QueueTier = StageQueueTier.Implementation
        };

        var stage6 = new TestWorkflowStage(StandardStageIds.PostReviewVerification, WorkflowStageKind.Deterministic)
        {
            QueueTier = StageQueueTier.CodeReview,
            Handler = (ctx, ct) => Task.FromResult(StageExecutionResult.Failure(
                "Review gate tests failed",
                loopBackStageId: StandardStageIds.Implementation))
        };

        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stage3, stage6 });
        var jobQueue = new FakeTaskJobQueue();

        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = workspaceRepo,
            DependencyStore = depStore,
            PipelineProvider = new DefaultWorkflowPipelineProvider(pipeline),
            JobQueue = jobQueue
        }.Build();

        await orchestrator.ProcessTaskAsync(task.Id);

        // Cross-cohort loopback re-enqueues Stage 3 on Implementation tier and exits
        Assert.Single(jobQueue.ImplementationQueue);
        Assert.Equal(task.Id, jobQueue.ImplementationQueue[0]);

        var updated = await taskRepo.GetByIdAsync(task.Id);
        Assert.Equal(StandardStageIds.Implementation, updated!.CurrentStageId);
    }

    [Fact]
    public async Task CohortUrgentPreemption_PendingUrgentJob_CausesCohortToYieldGracefullyBetweenStages()
    {
        var task = new ForgeTask
        {
            Id = 103,
            WorkspaceId = _workspace.Id,
            BaseBranch = "main",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.PreImplementationBaseline
        };

        var taskRepo = new InMemoryTaskRepository();
        await taskRepo.CreateAsync(task);
        var workspaceRepo = new InMemoryWorkspaceRepository();
        await workspaceRepo.CreateAsync(_workspace);
        var depStore = new InMemoryTaskDependencyStore();
        depStore.AddTask(task.Id, task.WorkspaceId, task.Status);

        var jobQueue = new FakeTaskJobQueue();

        var stage2 = new TestWorkflowStage(StandardStageIds.PreImplementationBaseline, WorkflowStageKind.Deterministic)
        {
            QueueTier = StageQueueTier.Implementation,
            Handler = (ctx, ct) =>
            {
                // Simulate an urgent P1 job arriving while Stage 2 executes
                jobQueue.HasUrgentJobs = true;
                return Task.FromResult(StageExecutionResult.Success());
            }
        };

        bool stage3Executed = false;
        var stage3 = new TestWorkflowStage(StandardStageIds.Implementation, WorkflowStageKind.Deterministic)
        {
            QueueTier = StageQueueTier.Implementation,
            Handler = (ctx, ct) =>
            {
                stage3Executed = true;
                return Task.FromResult(StageExecutionResult.Success());
            }
        };

        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stage2, stage3 });

        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = workspaceRepo,
            DependencyStore = depStore,
            PipelineProvider = new DefaultWorkflowPipelineProvider(pipeline),
            JobQueue = jobQueue
        }.Build();

        await orchestrator.ProcessTaskAsync(task.Id);

        // Stage 3 was NOT executed in this turn because orchestrator yielded
        Assert.False(stage3Executed);

        // Task was re-enqueued on its cohort tier (Implementation)
        Assert.Single(jobQueue.ImplementationQueue);
        Assert.Equal(task.Id, jobQueue.ImplementationQueue[0]);

        // Current stage is Stage 3 so resume continues at Stage 3
        var updated = await taskRepo.GetByIdAsync(task.Id);
        Assert.Equal(StandardStageIds.Implementation, updated!.CurrentStageId);
    }

    [Fact]
    public async Task CohortDelivery_Stages56AndAutoMerge_ExecuteInSingleTurn_TransitionsToClosed_PersistsSquashSha()
    {
        var task = new ForgeTask
        {
            Id = 104,
            WorkspaceId = _workspace.Id,
            BaseBranch = "main",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.CodeReviewAndFix,
            MergeMode = MergeMode.Automatic
        };

        var taskRepo = new InMemoryTaskRepository();
        await taskRepo.CreateAsync(task);
        var workspaceRepo = new InMemoryWorkspaceRepository();
        await workspaceRepo.CreateAsync(_workspace);
        var depStore = new InMemoryTaskDependencyStore();
        depStore.AddTask(task.Id, task.WorkspaceId, task.Status);

        var executedStages = new List<string>();

        var stage5 = new TestWorkflowStage(StandardStageIds.CodeReviewAndFix, WorkflowStageKind.Deterministic)
        {
            QueueTier = StageQueueTier.CodeReview,
            Handler = (ctx, ct) => { executedStages.Add(StandardStageIds.CodeReviewAndFix); return Task.FromResult(StageExecutionResult.Success()); }
        };
        var stage6 = new TestWorkflowStage(StandardStageIds.PostReviewVerification, WorkflowStageKind.Deterministic)
        {
            QueueTier = StageQueueTier.CodeReview,
            Handler = (ctx, ct) => { executedStages.Add(StandardStageIds.PostReviewVerification); return Task.FromResult(StageExecutionResult.Success()); }
        };

        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stage5, stage6 });

        var gitManagerMock = new Mock<IGitWorkspaceManager>();
        gitManagerMock.Setup(m => m.CheckMergeableAsync(It.IsAny<Workspace>(), task.Id, "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MergeabilityCheckResult(true, Array.Empty<string>(), "base_sha", "feat_sha"));
        gitManagerMock.Setup(m => m.SyncFromBaseBranchAsync(It.IsAny<Workspace>(), task.Id, "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MergeSyncResult(true, false, Array.Empty<string>(), "feat_sha"));
        gitManagerMock.Setup(m => m.SquashAndMergeAsync(It.IsAny<Workspace>(), task.Id, "main", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("squash-sha-abc-123");

        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = workspaceRepo,
            DependencyStore = depStore,
            GitWorkspaceManager = gitManagerMock.Object,
            PipelineProvider = new DefaultWorkflowPipelineProvider(pipeline)
        }.Build();

        await orchestrator.ProcessTaskAsync(task.Id);

        // Both stages executed in the single turn
        Assert.Equal(new[] { StandardStageIds.CodeReviewAndFix, StandardStageIds.PostReviewVerification }, executedStages);

        var updated = await taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(updated);
        // Automated delivery closed the task and persisted the commit SHA
        Assert.Equal(TaskOverallStatus.Closed, updated.Status);
        Assert.Equal("squash-sha-abc-123", updated.SquashCommitSha);
    }

    [Fact]
    public async Task CohortDelivery_ManualReviewRequired_TransitionsToReady()
    {
        var task = new ForgeTask
        {
            Id = 105,
            WorkspaceId = _workspace.Id,
            BaseBranch = "main",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.CodeReviewAndFix,
            MergeMode = MergeMode.ManualReviewRequired
        };

        var taskRepo = new InMemoryTaskRepository();
        await taskRepo.CreateAsync(task);
        var workspaceRepo = new InMemoryWorkspaceRepository();
        await workspaceRepo.CreateAsync(_workspace);
        var depStore = new InMemoryTaskDependencyStore();
        depStore.AddTask(task.Id, task.WorkspaceId, task.Status);

        var stage5 = new TestWorkflowStage(StandardStageIds.CodeReviewAndFix, WorkflowStageKind.Deterministic)
        {
            QueueTier = StageQueueTier.CodeReview,
            Handler = (ctx, ct) => Task.FromResult(StageExecutionResult.Success())
        };
        var stage6 = new TestWorkflowStage(StandardStageIds.PostReviewVerification, WorkflowStageKind.Deterministic)
        {
            QueueTier = StageQueueTier.CodeReview,
            Handler = (ctx, ct) => Task.FromResult(StageExecutionResult.Success())
        };

        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stage5, stage6 });

        var gitManagerMock = new Mock<IGitWorkspaceManager>();
        gitManagerMock.Setup(m => m.CheckMergeableAsync(It.IsAny<Workspace>(), task.Id, "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MergeabilityCheckResult(true, Array.Empty<string>(), "base_sha", "feat_sha"));

        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = workspaceRepo,
            DependencyStore = depStore,
            GitWorkspaceManager = gitManagerMock.Object,
            PipelineProvider = new DefaultWorkflowPipelineProvider(pipeline)
        }.Build();

        await orchestrator.ProcessTaskAsync(task.Id);

        var updated = await taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(updated);
        Assert.Equal(TaskOverallStatus.Ready, updated.Status);
    }

    [Fact]
    public async Task CohortResume_ResumesAtCurrentStageAndContinuesCohortInProcess()
    {
        // Task paused or resumed mid-cohort at Stage 3
        var task = new ForgeTask
        {
            Id = 106,
            WorkspaceId = _workspace.Id,
            BaseBranch = "main",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.Implementation
        };

        var taskRepo = new InMemoryTaskRepository();
        await taskRepo.CreateAsync(task);
        var workspaceRepo = new InMemoryWorkspaceRepository();
        await workspaceRepo.CreateAsync(_workspace);
        var depStore = new InMemoryTaskDependencyStore();
        depStore.AddTask(task.Id, task.WorkspaceId, task.Status);

        var executedStages = new List<string>();

        var stage2 = new TestWorkflowStage(StandardStageIds.PreImplementationBaseline, WorkflowStageKind.Deterministic)
        {
            QueueTier = StageQueueTier.Implementation,
            Handler = (ctx, ct) => { executedStages.Add(StandardStageIds.PreImplementationBaseline); return Task.FromResult(StageExecutionResult.Success()); }
        };
        var stage3 = new TestWorkflowStage(StandardStageIds.Implementation, WorkflowStageKind.Deterministic)
        {
            QueueTier = StageQueueTier.Implementation,
            Handler = (ctx, ct) => { executedStages.Add(StandardStageIds.Implementation); return Task.FromResult(StageExecutionResult.Success()); }
        };
        var stage4 = new TestWorkflowStage(StandardStageIds.PostImplementationGates, WorkflowStageKind.Deterministic)
        {
            QueueTier = StageQueueTier.Implementation,
            Handler = (ctx, ct) => { executedStages.Add(StandardStageIds.PostImplementationGates); return Task.FromResult(StageExecutionResult.Success()); }
        };
        var stage5 = new TestWorkflowStage(StandardStageIds.CodeReviewAndFix, WorkflowStageKind.Deterministic)
        {
            QueueTier = StageQueueTier.CodeReview
        };

        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stage2, stage3, stage4, stage5 });

        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = workspaceRepo,
            DependencyStore = depStore,
            PipelineProvider = new DefaultWorkflowPipelineProvider(pipeline)
        }.Build();

        await orchestrator.ProcessTaskAsync(task.Id);

        // Resumed starting at Stage 3, never re-ran Stage 2
        Assert.Equal(new[] { StandardStageIds.Implementation, StandardStageIds.PostImplementationGates }, executedStages);

        var updated = await taskRepo.GetByIdAsync(task.Id);
        Assert.Equal(StandardStageIds.CodeReviewAndFix, updated!.CurrentStageId);
    }

    [Fact]
    public async Task CohortAsk_AskToolInStage3_MarksWaitingForHumanAndExits()
    {
        var task = new ForgeTask
        {
            Id = 107,
            WorkspaceId = _workspace.Id,
            BaseBranch = "main",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.Implementation
        };

        var taskRepo = new InMemoryTaskRepository();
        await taskRepo.CreateAsync(task);
        var workspaceRepo = new InMemoryWorkspaceRepository();
        await workspaceRepo.CreateAsync(_workspace);
        var depStore = new InMemoryTaskDependencyStore();
        depStore.AddTask(task.Id, task.WorkspaceId, task.Status);

        var stage3 = new TestWorkflowStage(StandardStageIds.Implementation, WorkflowStageKind.Deterministic)
        {
            QueueTier = StageQueueTier.Implementation,
            Handler = (ctx, ct) => Task.FromResult(StageExecutionResult.WaitingForHuman(new[] { new Question { Prompt = "Need clarification on authentication provider" } }))
        };

        bool stage4Executed = false;
        var stage4 = new TestWorkflowStage(StandardStageIds.PostImplementationGates, WorkflowStageKind.Deterministic)
        {
            QueueTier = StageQueueTier.Implementation,
            Handler = (ctx, ct) => { stage4Executed = true; return Task.FromResult(StageExecutionResult.Success()); }
        };

        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stage3, stage4 });

        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = workspaceRepo,
            DependencyStore = depStore,
            PipelineProvider = new DefaultWorkflowPipelineProvider(pipeline)
        }.Build();

        await orchestrator.ProcessTaskAsync(task.Id);

        // Stage 4 did not execute
        Assert.False(stage4Executed);

        var updated = await taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(updated);
        Assert.True(updated.IsWaitingForHuman);
        Assert.Equal(StandardStageIds.Implementation, updated.InterruptedStageId);
    }

    [Fact]
    public async Task CohortGate2Conflict_ReEntersConflictResolutionOnImplementationTier()
    {
        var task = new ForgeTask
        {
            Id = 108,
            WorkspaceId = _workspace.Id,
            BaseBranch = "main",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.PostReviewVerification
        };

        var taskRepo = new InMemoryTaskRepository();
        await taskRepo.CreateAsync(task);
        var workspaceRepo = new InMemoryWorkspaceRepository();
        await workspaceRepo.CreateAsync(_workspace);
        var depStore = new InMemoryTaskDependencyStore();
        depStore.AddTask(task.Id, task.WorkspaceId, task.Status);

        var stage3 = new TestWorkflowStage(StandardStageIds.Implementation, WorkflowStageKind.Deterministic)
        {
            QueueTier = StageQueueTier.Implementation
        };
        var stage6 = new TestWorkflowStage(StandardStageIds.PostReviewVerification, WorkflowStageKind.Deterministic)
        {
            QueueTier = StageQueueTier.CodeReview,
            Handler = (ctx, ct) => Task.FromResult(StageExecutionResult.Success())
        };

        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stage3, stage6 });

        var gitManagerMock = new Mock<IGitWorkspaceManager>();
        gitManagerMock.Setup(m => m.CheckMergeableAsync(It.IsAny<Workspace>(), task.Id, "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MergeabilityCheckResult(false, new[] { "src/Conflict.cs" }, "base_sha", "feat_sha"));

        var jobQueue = new FakeTaskJobQueue();

        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = workspaceRepo,
            DependencyStore = depStore,
            GitWorkspaceManager = gitManagerMock.Object,
            JobQueue = jobQueue,
            PipelineProvider = new DefaultWorkflowPipelineProvider(pipeline)
        }.Build();

        await orchestrator.ProcessTaskAsync(task.Id);

        // Re-enqueues Implementation cohort on Tier 2
        Assert.Single(jobQueue.ImplementationQueue);
        Assert.Equal(task.Id, jobQueue.ImplementationQueue[0]);

        var updated = await taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(updated);
        Assert.Equal(StandardStageIds.Implementation, updated.CurrentStageId);

        var bag = PipelineDataBag.FromJson(updated.PipelineDataJson);
        Assert.Equal(new[] { "src/Conflict.cs" }, bag.Get<List<string>>(PipelineDataBagKeys.ConflictFiles));
    }

    [Fact]
    public async Task CohortGate2Reverify_AdvanceFailure_RoutesBackToImplementationTier()
    {
        var task = new ForgeTask
        {
            Id = 109,
            WorkspaceId = _workspace.Id,
            BaseBranch = "main",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.PostReviewVerification
        };

        var taskRepo = new InMemoryTaskRepository();
        await taskRepo.CreateAsync(task);
        var workspaceRepo = new InMemoryWorkspaceRepository();
        await workspaceRepo.CreateAsync(_workspace);
        var depStore = new InMemoryTaskDependencyStore();
        depStore.AddTask(task.Id, task.WorkspaceId, task.Status);

        var stage3 = new TestWorkflowStage(StandardStageIds.Implementation, WorkflowStageKind.Deterministic)
        {
            QueueTier = StageQueueTier.Implementation
        };

        int stage6Execution = 0;
        var stage6 = new TestWorkflowStage(StandardStageIds.PostReviewVerification, WorkflowStageKind.Deterministic)
        {
            QueueTier = StageQueueTier.CodeReview,
            Handler = (ctx, ct) =>
            {
                stage6Execution++;
                return Task.FromResult(stage6Execution == 1
                    ? StageExecutionResult.Success()
                    : StageExecutionResult.Failure("Semantic build breakage on merged tree"));
            }
        };

        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stage3, stage6 });

        var gitManagerMock = new Mock<IGitWorkspaceManager>();
        gitManagerMock.Setup(m => m.CheckMergeableAsync(It.IsAny<Workspace>(), task.Id, "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MergeabilityCheckResult(true, Array.Empty<string>(), "base_sha", "feat_sha"));
        // Simulate base branch advance
        gitManagerMock.Setup(m => m.SyncFromBaseBranchAsync(It.IsAny<Workspace>(), task.Id, "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MergeSyncResult(true, false, Array.Empty<string>(), "new_merge_commit_sha"));

        var jobQueue = new FakeTaskJobQueue();

        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = workspaceRepo,
            DependencyStore = depStore,
            GitWorkspaceManager = gitManagerMock.Object,
            JobQueue = jobQueue,
            PipelineProvider = new DefaultWorkflowPipelineProvider(pipeline)
        }.Build();

        await orchestrator.ProcessTaskAsync(task.Id);

        // Reverification failure routes backward to Implementation tier and exits
        Assert.Single(jobQueue.ImplementationQueue);
        Assert.Equal(task.Id, jobQueue.ImplementationQueue[0]);

        var updated = await taskRepo.GetByIdAsync(task.Id);
        Assert.Equal(StandardStageIds.Implementation, updated!.CurrentStageId);
    }

    [Fact]
    public void CohortValidation_NonContiguousQueueTiers_ThrowsValidationException()
    {
        var stages = new[]
        {
            new TestWorkflowStage("stage-1") { QueueTier = StageQueueTier.Implementation },
            new TestWorkflowStage("stage-2") { QueueTier = StageQueueTier.CodeReview },
            new TestWorkflowStage("stage-3") { QueueTier = StageQueueTier.Implementation } // Disconnected Implementation stage!
        };

        var ex = Assert.Throws<WorkflowValidationException>(() =>
            WorkflowDefinitionValidator.Validate(stages));

        Assert.Contains("non-contiguous", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CohortResume_UrgentYield_ThenResumesAndCompletesRemainderOfCohort()
    {
        var task = new ForgeTask
        {
            Id = 110,
            WorkspaceId = _workspace.Id,
            BaseBranch = "main",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.PreImplementationBaseline
        };

        var taskRepo = new InMemoryTaskRepository();
        await taskRepo.CreateAsync(task);
        var workspaceRepo = new InMemoryWorkspaceRepository();
        await workspaceRepo.CreateAsync(_workspace);
        var depStore = new InMemoryTaskDependencyStore();
        depStore.AddTask(task.Id, task.WorkspaceId, task.Status);

        var jobQueue = new FakeTaskJobQueue();
        var executedStages = new List<string>();

        var stage2 = new TestWorkflowStage(StandardStageIds.PreImplementationBaseline, WorkflowStageKind.Deterministic)
        {
            QueueTier = StageQueueTier.Implementation,
            Handler = (ctx, ct) =>
            {
                executedStages.Add(StandardStageIds.PreImplementationBaseline);
                jobQueue.HasUrgentJobs = true;
                return Task.FromResult(StageExecutionResult.Success());
            }
        };
        var stage3 = new TestWorkflowStage(StandardStageIds.Implementation, WorkflowStageKind.Deterministic)
        {
            QueueTier = StageQueueTier.Implementation,
            Handler = (ctx, ct) =>
            {
                executedStages.Add(StandardStageIds.Implementation);
                return Task.FromResult(StageExecutionResult.Success());
            }
        };
        var stage4 = new TestWorkflowStage(StandardStageIds.PostImplementationGates, WorkflowStageKind.Deterministic)
        {
            QueueTier = StageQueueTier.Implementation,
            Handler = (ctx, ct) =>
            {
                executedStages.Add(StandardStageIds.PostImplementationGates);
                return Task.FromResult(StageExecutionResult.Success());
            }
        };
        var stage5 = new TestWorkflowStage(StandardStageIds.CodeReviewAndFix, WorkflowStageKind.Deterministic)
        {
            QueueTier = StageQueueTier.CodeReview
        };

        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stage2, stage3, stage4, stage5 });
        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = workspaceRepo,
            DependencyStore = depStore,
            PipelineProvider = new DefaultWorkflowPipelineProvider(pipeline),
            JobQueue = jobQueue
        }.Build();

        // Turn 1: Stage 2 executes, urgent job waiting causes yield before Stage 3
        await orchestrator.ProcessTaskAsync(task.Id);

        Assert.Equal(new[] { StandardStageIds.PreImplementationBaseline }, executedStages);
        var afterTurn1 = await taskRepo.GetByIdAsync(task.Id);
        Assert.Equal(StandardStageIds.Implementation, afterTurn1!.CurrentStageId);
        Assert.Single(jobQueue.ImplementationQueue);

        // Urgent job completes and is no longer waiting
        jobQueue.HasUrgentJobs = false;
        jobQueue.ImplementationQueue.Clear();

        // Turn 2: Worker resumes on Implementation tier -> executes Stage 3 and Stage 4 in-process
        await orchestrator.ProcessTaskAsync(task.Id);

        Assert.Equal(new[]
        {
            StandardStageIds.PreImplementationBaseline,
            StandardStageIds.Implementation,
            StandardStageIds.PostImplementationGates
        }, executedStages);

        var afterTurn2 = await taskRepo.GetByIdAsync(task.Id);
        Assert.Equal(StandardStageIds.CodeReviewAndFix, afterTurn2!.CurrentStageId);
        Assert.Single(jobQueue.CodeReviewQueue);
    }

    [Fact]
    public async Task CohortResume_MidCohortPause_ThenResumesAndContinuesCohort()
    {
        var task = new ForgeTask
        {
            Id = 111,
            WorkspaceId = _workspace.Id,
            BaseBranch = "main",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.PreImplementationBaseline
        };

        var taskRepo = new InMemoryTaskRepository();
        await taskRepo.CreateAsync(task);
        var workspaceRepo = new InMemoryWorkspaceRepository();
        await workspaceRepo.CreateAsync(_workspace);
        var depStore = new InMemoryTaskDependencyStore();
        depStore.AddTask(task.Id, task.WorkspaceId, task.Status);

        var executedStages = new List<string>();
        var opsMock = new Mock<IOperationsControlService>();
        bool isPaused = false;
        opsMock.Setup(o => o.IsPaused).Returns(() => isPaused);

        var stage2 = new TestWorkflowStage(StandardStageIds.PreImplementationBaseline, WorkflowStageKind.Deterministic)
        {
            QueueTier = StageQueueTier.Implementation,
            Handler = (ctx, ct) =>
            {
                executedStages.Add(StandardStageIds.PreImplementationBaseline);
                // System paused right as Stage 2 completes
                isPaused = true;
                return Task.FromResult(StageExecutionResult.Success());
            }
        };
        var stage3 = new TestWorkflowStage(StandardStageIds.Implementation, WorkflowStageKind.Deterministic)
        {
            QueueTier = StageQueueTier.Implementation,
            Handler = (ctx, ct) =>
            {
                executedStages.Add(StandardStageIds.Implementation);
                return Task.FromResult(StageExecutionResult.Success());
            }
        };
        var stage4 = new TestWorkflowStage(StandardStageIds.PostImplementationGates, WorkflowStageKind.Deterministic)
        {
            QueueTier = StageQueueTier.Implementation,
            Handler = (ctx, ct) =>
            {
                executedStages.Add(StandardStageIds.PostImplementationGates);
                return Task.FromResult(StageExecutionResult.Success());
            }
        };
        var stage5 = new TestWorkflowStage(StandardStageIds.CodeReviewAndFix, WorkflowStageKind.Deterministic)
        {
            QueueTier = StageQueueTier.CodeReview
        };

        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stage2, stage3, stage4, stage5 });
        var jobQueue = new FakeTaskJobQueue();

        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = workspaceRepo,
            DependencyStore = depStore,
            PipelineProvider = new DefaultWorkflowPipelineProvider(pipeline),
            JobQueue = jobQueue,
            OperationsControlService = opsMock.Object
        }.Build();

        // Turn 1: Stage 2 completes, pause detected before Stage 3 starts
        await orchestrator.ProcessTaskAsync(task.Id);

        Assert.Equal(new[] { StandardStageIds.PreImplementationBaseline }, executedStages);
        var pausedTask = await taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(pausedTask);
        Assert.True(pausedTask.IsDeferredByPause);
        Assert.Equal(StandardStageIds.Implementation, pausedTask.CurrentStageId);

        // Resume operations
        isPaused = false;

        // Turn 2: Execution resumes at Stage 3 and completes Stage 3 + 4 in-process
        await orchestrator.ProcessTaskAsync(task.Id);

        Assert.Equal(new[]
        {
            StandardStageIds.PreImplementationBaseline,
            StandardStageIds.Implementation,
            StandardStageIds.PostImplementationGates
        }, executedStages);

        var resumedTask = await taskRepo.GetByIdAsync(task.Id);
        Assert.False(resumedTask!.IsDeferredByPause);
        Assert.Equal(StandardStageIds.CodeReviewAndFix, resumedTask.CurrentStageId);
        Assert.Single(jobQueue.CodeReviewQueue);
    }

    [Fact]
    public async Task CohortResume_MidCohortCancellation_AbortsCleanlyWithoutCorruptingCurrentStage()
    {
        var task = new ForgeTask
        {
            Id = 112,
            WorkspaceId = _workspace.Id,
            BaseBranch = "main",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.PreImplementationBaseline
        };

        var taskRepo = new InMemoryTaskRepository();
        await taskRepo.CreateAsync(task);
        var workspaceRepo = new InMemoryWorkspaceRepository();
        await workspaceRepo.CreateAsync(_workspace);
        var depStore = new InMemoryTaskDependencyStore();
        depStore.AddTask(task.Id, task.WorkspaceId, task.Status);

        var cancellationManager = new TaskCancellationManager(Microsoft.Extensions.Logging.Abstractions.NullLogger<TaskCancellationManager>.Instance);
        var executedStages = new List<string>();

        var stage2 = new TestWorkflowStage(StandardStageIds.PreImplementationBaseline, WorkflowStageKind.Deterministic)
        {
            QueueTier = StageQueueTier.Implementation,
            Handler = (ctx, ct) =>
            {
                executedStages.Add(StandardStageIds.PreImplementationBaseline);
                // User cancels while stage 2 finishes
                cancellationManager.Cancel(task.Id);
                return Task.FromResult(StageExecutionResult.Success());
            }
        };
        var stage3 = new TestWorkflowStage(StandardStageIds.Implementation, WorkflowStageKind.Deterministic)
        {
            QueueTier = StageQueueTier.Implementation,
            Handler = (ctx, ct) =>
            {
                executedStages.Add(StandardStageIds.Implementation);
                return Task.FromResult(StageExecutionResult.Success());
            }
        };

        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stage2, stage3 });
        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = workspaceRepo,
            DependencyStore = depStore,
            PipelineProvider = new DefaultWorkflowPipelineProvider(pipeline),
            CancellationManager = cancellationManager
        }.Build();

        await orchestrator.ProcessTaskAsync(task.Id);

        // Stage 3 was aborted due to cancellation
        Assert.Equal(new[] { StandardStageIds.PreImplementationBaseline }, executedStages);

        var cancelledTask = await taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(cancelledTask);
        Assert.Equal(TaskOverallStatus.Cancelled, cancelledTask.Status);
    }

    [Fact]
    public async Task CohortLoopback_RetryBudgetPersistedAcrossJobRestart()
    {
        var task = new ForgeTask
        {
            Id = 113,
            WorkspaceId = _workspace.Id,
            BaseBranch = "main",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.Implementation
        };

        var bag = new PipelineDataBag();
        bag.Set(PipelineDataBagKeys.MaxRetries, 2);
        // Previously used 1 retry before crash/restart
        bag.Set(PipelineDataBagKeys.StageRetries, new Dictionary<string, int> { [StandardStageIds.PostImplementationGates] = 1 });
        task.PipelineDataJson = bag.ToJson();

        var taskRepo = new InMemoryTaskRepository();
        await taskRepo.CreateAsync(task);
        var workspaceRepo = new InMemoryWorkspaceRepository();
        await workspaceRepo.CreateAsync(_workspace);
        var depStore = new InMemoryTaskDependencyStore();
        depStore.AddTask(task.Id, task.WorkspaceId, task.Status);

        var stage3 = new TestWorkflowStage(StandardStageIds.Implementation, WorkflowStageKind.Deterministic)
        {
            QueueTier = StageQueueTier.Implementation
        };

        int stage4Attempts = 0;
        var stage4 = new TestWorkflowStage(StandardStageIds.PostImplementationGates, WorkflowStageKind.Deterministic)
        {
            QueueTier = StageQueueTier.Implementation,
            Handler = (ctx, ct) =>
            {
                stage4Attempts++;
                // Always fail
                return Task.FromResult(StageExecutionResult.Failure(
                    "Compiler error",
                    loopBackStageId: StandardStageIds.Implementation));
            }
        };

        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stage3, stage4 });
        var jobQueue = new FakeTaskJobQueue();

        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = workspaceRepo,
            DependencyStore = depStore,
            PipelineProvider = new DefaultWorkflowPipelineProvider(pipeline),
            JobQueue = jobQueue
        }.Build();

        await orchestrator.ProcessTaskAsync(task.Id);

        // Since budget was max 2 and 1 was already used, Stage 4 can only retry once (attempt 2) before exhausting budget
        // On attempt 2 <= 2: in-process loopback to Stage 3
        // Next Stage 4 run: attempt 3 > 2 -> FATAL FAILURE
        var failedTask = await taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(failedTask);
        Assert.Equal(TaskOverallStatus.Failed, failedTask.Status);

        var persistedBag = PipelineDataBag.FromJson(failedTask.PipelineDataJson);
        var retries = persistedBag.Get<Dictionary<string, int>>(PipelineDataBagKeys.StageRetries);
        Assert.NotNull(retries);
        Assert.Equal(3, retries[StandardStageIds.PostImplementationGates]);
    }

    [Fact]
    public async Task CohortScenario_ZeroInterleaving_ExecutesEntireCohortWithoutLowerTierInterference()
    {
        // Setup InMemoryTaskJobQueue (deterministic in-memory scheduler)
        var inMemoryQueue = new InMemoryTaskJobQueue();

        var taskRepo = new InMemoryTaskRepository();
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var depStore = new InMemoryTaskDependencyStore();
        await workspaceRepo.CreateAsync(_workspace);

        // 1. Enqueue Task 999 (Lower Priority: Tier 3 TaskReview) FIRST
        var task999 = new ForgeTask
        {
            Id = 999,
            WorkspaceId = _workspace.Id,
            BaseBranch = "main",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.TaskReview,
            Priority = TaskPriority.Normal
        };
        await taskRepo.CreateAsync(task999);
        depStore.AddTask(task999.Id, _workspace.Id, task999.Status);
        inMemoryQueue.EnqueueStage(task999.Id, StageQueueTier.TaskReview, task999.Priority);

        // 2. Enqueue Task 100 (Implementation Cohort: Tier 2 Implementation)
        var task100 = new ForgeTask
        {
            Id = 100,
            WorkspaceId = _workspace.Id,
            BaseBranch = "main",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.PreImplementationBaseline,
            Priority = TaskPriority.Normal,
            MergeMode = MergeMode.Automatic
        };
        await taskRepo.CreateAsync(task100);
        depStore.AddTask(task100.Id, _workspace.Id, task100.Status);
        inMemoryQueue.EnqueueStage(task100.Id, StageQueueTier.Implementation, task100.Priority);

        var executedTurns = new List<string>();

        var stage2 = new TestWorkflowStage(StandardStageIds.PreImplementationBaseline, WorkflowStageKind.Deterministic)
        {
            QueueTier = StageQueueTier.Implementation,
            Handler = (ctx, ct) => { executedTurns.Add($"Task-{ctx.ForgeTask.Id}:{StandardStageIds.PreImplementationBaseline}"); return Task.FromResult(StageExecutionResult.Success()); }
        };
        var stage3 = new TestWorkflowStage(StandardStageIds.Implementation, WorkflowStageKind.Deterministic)
        {
            QueueTier = StageQueueTier.Implementation,
            Handler = (ctx, ct) => { executedTurns.Add($"Task-{ctx.ForgeTask.Id}:{StandardStageIds.Implementation}"); return Task.FromResult(StageExecutionResult.Success()); }
        };
        var stage4 = new TestWorkflowStage(StandardStageIds.PostImplementationGates, WorkflowStageKind.Deterministic)
        {
            QueueTier = StageQueueTier.Implementation,
            Handler = (ctx, ct) => { executedTurns.Add($"Task-{ctx.ForgeTask.Id}:{StandardStageIds.PostImplementationGates}"); return Task.FromResult(StageExecutionResult.Success()); }
        };
        var stage5 = new TestWorkflowStage(StandardStageIds.CodeReviewAndFix, WorkflowStageKind.Deterministic)
        {
            QueueTier = StageQueueTier.CodeReview,
            Handler = (ctx, ct) => { executedTurns.Add($"Task-{ctx.ForgeTask.Id}:{StandardStageIds.CodeReviewAndFix}"); return Task.FromResult(StageExecutionResult.Success()); }
        };
        var stage6 = new TestWorkflowStage(StandardStageIds.PostReviewVerification, WorkflowStageKind.Deterministic)
        {
            QueueTier = StageQueueTier.CodeReview,
            Handler = (ctx, ct) => { executedTurns.Add($"Task-{ctx.ForgeTask.Id}:{StandardStageIds.PostReviewVerification}"); return Task.FromResult(StageExecutionResult.Success()); }
        };

        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stage2, stage3, stage4, stage5, stage6 });

        var gitManagerMock = new Mock<IGitWorkspaceManager>();
        gitManagerMock.Setup(m => m.CheckMergeableAsync(It.IsAny<Workspace>(), task100.Id, "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MergeabilityCheckResult(true, Array.Empty<string>(), "base_sha", "feat_sha"));
        gitManagerMock.Setup(m => m.SyncFromBaseBranchAsync(It.IsAny<Workspace>(), task100.Id, "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MergeSyncResult(true, false, Array.Empty<string>(), "feat_sha"));
        gitManagerMock.Setup(m => m.SquashAndMergeAsync(It.IsAny<Workspace>(), task100.Id, "main", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("squash-sha-task-100");

        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = workspaceRepo,
            DependencyStore = depStore,
            GitWorkspaceManager = gitManagerMock.Object,
            JobQueue = inMemoryQueue,
            PipelineProvider = new DefaultWorkflowPipelineProvider(pipeline)
        }.Build();

        // Simulate single-worker scheduler loop using InMemoryTaskJobQueue priority
        while (inMemoryQueue.TryDequeue(out var job))
        {
            if (job!.TaskId == task999.Id)
            {
                executedTurns.Add($"Task-999:TaskReview");
                break; // Stop after lower-tier task runs
            }
            await orchestrator.ProcessTaskAsync(job.TaskId);
        }

        // Verify:
        // 1. Task 100 ran Turn 1: Cohort 1 (Stages 2 -> 3 -> 4) chained in-process with ZERO interleaving from Task 999.
        // 2. Task 100 then enqueued Cohort 2 (Tier 1 CodeReview) which strictly preempted Task 999 (Tier 3 TaskReview).
        // 3. Task 100 ran Turn 2: Cohort 2 (Stages 5 -> 6 + AutoMerge) chained in-process with ZERO interleaving from Task 999.
        // 4. Only after Task 100 closed did Task 999 finally execute!
        Assert.Equal(new[]
        {
            "Task-100:deterministic-pre-impl",
            "Task-100:implementation",
            "Task-100:deterministic-post-impl",
            "Task-100:code-review-fix",
            "Task-100:deterministic-post-review",
            "Task-999:TaskReview"
        }, executedTurns);

        var finalTask100 = await taskRepo.GetByIdAsync(task100.Id);
        Assert.Equal(TaskOverallStatus.Closed, finalTask100!.Status);
        Assert.Equal("squash-sha-task-100", finalTask100.SquashCommitSha);
    }

    [Fact]
    public async Task CohortFailureRouting_Stage2BaselineDeterministicFailure_FailsWithoutRetryLoop()
    {
        var task = new ForgeTask
        {
            Id = 114,
            WorkspaceId = _workspace.Id,
            BaseBranch = "main",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.PreImplementationBaseline
        };

        var taskRepo = new InMemoryTaskRepository();
        await taskRepo.CreateAsync(task);
        var workspaceRepo = new InMemoryWorkspaceRepository();
        await workspaceRepo.CreateAsync(_workspace);
        var depStore = new InMemoryTaskDependencyStore();
        depStore.AddTask(task.Id, task.WorkspaceId, task.Status);

        var stage2 = new TestWorkflowStage(StandardStageIds.PreImplementationBaseline, WorkflowStageKind.Deterministic)
        {
            QueueTier = StageQueueTier.Implementation,
            Handler = (ctx, ct) => Task.FromResult(StageExecutionResult.Failure("Baseline build/test failed"))
        };
        var stage3 = new TestWorkflowStage(StandardStageIds.Implementation, WorkflowStageKind.Deterministic)
        {
            QueueTier = StageQueueTier.Implementation
        };

        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stage2, stage3 });
        var jobQueue = new FakeTaskJobQueue();

        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = workspaceRepo,
            DependencyStore = depStore,
            PipelineProvider = new DefaultWorkflowPipelineProvider(pipeline),
            JobQueue = jobQueue
        }.Build();

        await orchestrator.ProcessTaskAsync(task.Id);

        var failedTask = await taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(failedTask);
        Assert.Equal(TaskOverallStatus.Failed, failedTask.Status);
        Assert.Empty(jobQueue.ImplementationQueue);
        Assert.Empty(jobQueue.CodeReviewQueue);
    }

    [Fact]
    public void CohortExitReason_ContainsCanonicalExpectedValues()
    {
        Assert.True(Enum.IsDefined(typeof(CohortExitReason), CohortExitReason.CohortCompleted));
        Assert.True(Enum.IsDefined(typeof(CohortExitReason), CohortExitReason.Paused));
        Assert.True(Enum.IsDefined(typeof(CohortExitReason), CohortExitReason.Yielded));
        Assert.True(Enum.IsDefined(typeof(CohortExitReason), CohortExitReason.WaitingForHuman));
        Assert.True(Enum.IsDefined(typeof(CohortExitReason), CohortExitReason.Failed));
        Assert.True(Enum.IsDefined(typeof(CohortExitReason), CohortExitReason.Cancelled));
        Assert.True(Enum.IsDefined(typeof(CohortExitReason), CohortExitReason.Aborted));
    }

    [Fact]
    public async Task CohortGate2Conflict_WhenLifetimeBudgetExhausted_SetsWaitingForHuman()
    {
        var task = new ForgeTask
        {
            Id = 115,
            WorkspaceId = _workspace.Id,
            BaseBranch = "main",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.PostReviewVerification
        };

        var bag = new PipelineDataBag();
        bag.Set(PipelineDataBagKeys.LifetimeConflictDetections, ConflictResolutionConstants.MaxLifetimeConflictDetections);
        task.PipelineDataJson = bag.ToJson();

        var taskRepo = new InMemoryTaskRepository();
        await taskRepo.CreateAsync(task);
        var workspaceRepo = new InMemoryWorkspaceRepository();
        await workspaceRepo.CreateAsync(_workspace);
        var depStore = new InMemoryTaskDependencyStore();
        depStore.AddTask(task.Id, task.WorkspaceId, task.Status);

        var stage6 = new TestWorkflowStage(StandardStageIds.PostReviewVerification, WorkflowStageKind.Deterministic)
        {
            QueueTier = StageQueueTier.CodeReview,
            Handler = (ctx, ct) => Task.FromResult(StageExecutionResult.Success())
        };

        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stage6 });

        var gitManagerMock = new Mock<IGitWorkspaceManager>();
        gitManagerMock.Setup(m => m.CheckMergeableAsync(It.IsAny<Workspace>(), task.Id, "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MergeabilityCheckResult(false, new[] { "src/Conflict.cs" }, "base_sha", "feat_sha"));

        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = workspaceRepo,
            DependencyStore = depStore,
            GitWorkspaceManager = gitManagerMock.Object,
            PipelineProvider = new DefaultWorkflowPipelineProvider(pipeline)
        }.Build();

        await orchestrator.ProcessTaskAsync(task.Id);

        var updated = await taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(updated);
        Assert.True(updated.IsWaitingForHuman);
        Assert.Equal(StandardStageIds.Implementation, updated.InterruptedStageId);
    }

    [Fact]
    public async Task CohortDynamicRouting_ToNonExistentStage_ThrowsWorkflowValidationException()
    {
        var task = new ForgeTask
        {
            Id = 116,
            WorkspaceId = _workspace.Id,
            BaseBranch = "main",
            Status = TaskOverallStatus.Open,
            CurrentStageId = "stage-1"
        };

        var taskRepo = new InMemoryTaskRepository();
        await taskRepo.CreateAsync(task);
        var workspaceRepo = new InMemoryWorkspaceRepository();
        await workspaceRepo.CreateAsync(_workspace);
        var depStore = new InMemoryTaskDependencyStore();
        depStore.AddTask(task.Id, task.WorkspaceId, task.Status);

        var stage1 = new TestWorkflowStage("stage-1", WorkflowStageKind.Deterministic)
        {
            QueueTier = StageQueueTier.Implementation,
            Handler = (ctx, ct) => Task.FromResult(new StageExecutionResult
            {
                IsSuccess = true,
                TargetRoutingStageId = "non-existent-stage"
            })
        };

        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stage1 });

        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = workspaceRepo,
            DependencyStore = depStore,
            PipelineProvider = new DefaultWorkflowPipelineProvider(pipeline)
        }.Build();

        await Assert.ThrowsAsync<WorkflowValidationException>(() => orchestrator.ProcessTaskAsync(task.Id));

        var updated = await taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(updated);
        Assert.Equal(TaskOverallStatus.Failed, updated.Status);
    }

    [Fact]
    public async Task CohortLoopback_WhenStage4PassesAfterLoopback_ClearsStaleDiagnostics()
    {
        var task = new ForgeTask
        {
            Id = 117,
            WorkspaceId = _workspace.Id,
            BaseBranch = "main",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.Implementation
        };

        var taskRepo = new InMemoryTaskRepository();
        await taskRepo.CreateAsync(task);
        var workspaceRepo = new InMemoryWorkspaceRepository();
        await workspaceRepo.CreateAsync(_workspace);
        var depStore = new InMemoryTaskDependencyStore();
        depStore.AddTask(task.Id, task.WorkspaceId, task.Status);

        int stage4Runs = 0;
        var stage3 = new TestWorkflowStage(StandardStageIds.Implementation, WorkflowStageKind.Deterministic)
        {
            QueueTier = StageQueueTier.Implementation,
            Handler = (ctx, ct) => Task.FromResult(StageExecutionResult.Success())
        };

        var stage4 = new TestWorkflowStage(StandardStageIds.PostImplementationGates, WorkflowStageKind.Deterministic)
        {
            QueueTier = StageQueueTier.Implementation,
            Handler = (ctx, ct) =>
            {
                stage4Runs++;
                if (stage4Runs == 1)
                {
                    return Task.FromResult(StageExecutionResult.Failure("Compiler error attempt 1", loopBackStageId: StandardStageIds.Implementation));
                }
                return Task.FromResult(StageExecutionResult.Success());
            }
        };

        var stage5 = new TestWorkflowStage(StandardStageIds.CodeReviewAndFix, WorkflowStageKind.Deterministic)
        {
            QueueTier = StageQueueTier.CodeReview
        };

        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stage3, stage4, stage5 });

        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = workspaceRepo,
            DependencyStore = depStore,
            PipelineProvider = new DefaultWorkflowPipelineProvider(pipeline)
        }.Build();

        await orchestrator.ProcessTaskAsync(task.Id);

        var updated = await taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(updated);
        var bag = PipelineDataBag.FromJson(updated.PipelineDataJson);
        Assert.Null(bag.Get<List<string>>(PipelineDataBagKeys.CompilerErrors));
        Assert.Null(bag.Get<List<string>>(PipelineDataBagKeys.FailedTests));
    }
}
