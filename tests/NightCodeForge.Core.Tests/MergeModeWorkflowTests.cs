using Microsoft.Extensions.Logging;
using Moq;
using NightCodeForge.Core.Concurrency;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Tests.Fixtures;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Core.Tests;

public class MergeModeWorkflowTests
{
    private readonly InMemoryTaskRepository _taskRepo;
    private readonly InMemoryWorkspaceRepository _workspaceRepo;
    private readonly InMemoryTaskDependencyStore _depStore;
    private readonly DependencyResolutionEngine _depEngine;
    private readonly WorkspaceLockProvider _lockProvider;
    private readonly FakeGitWorkspaceManager _gitManager;
    private readonly FakeTaskJobQueue _jobQueue;
    private readonly Mock<IGitHubPullRequestDeliveryService> _prDeliveryMock;
    private readonly Workspace _workspace;

    private TaskOrchestrator CreateOrchestrator(IWorkflowPipelineProvider? pipelineProvider = null) =>
        new TestOrchestratorBuilder
        {
            TaskRepository = _taskRepo,
            WorkspaceRepository = _workspaceRepo,
            DependencyEngine = _depEngine,
            LockProvider = _lockProvider,
            GitWorkspaceManager = _gitManager,
            PipelineProvider = pipelineProvider ?? new DefaultWorkflowPipelineProvider(),
            JobQueue = _jobQueue,
            DependencyStore = _depStore,
            GitHubPrDeliveryService = _prDeliveryMock.Object
        }.Build();

    public MergeModeWorkflowTests()
    {
        _taskRepo = new InMemoryTaskRepository();
        _workspaceRepo = new InMemoryWorkspaceRepository();
        _depStore = new InMemoryTaskDependencyStore();
        _depEngine = new DependencyResolutionEngine(_depStore);
        _lockProvider = new WorkspaceLockProvider();
        _gitManager = new FakeGitWorkspaceManager();
        _jobQueue = new FakeTaskJobQueue();
        _prDeliveryMock = new Mock<IGitHubPullRequestDeliveryService>();

        _workspace = new Workspace
        {
            Id = 1,
            Name = "MergeMode Test Workspace",
            RepositoryPath = "C:/test/repo",
            DefaultBaseBranch = "main"
        };
        _workspaceRepo.CreateAsync(_workspace).GetAwaiter().GetResult();
    }

    [Fact]
    public async Task FinalStage_WithAutomaticMergeMode_AutoSquashesMergesToClosedAndCascades()
    {
        // ForgeTask 501 (Automatic merge mode)
        // ForgeTask 502 depends on 501
        var task501 = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 501,
            WorkspaceId = 1,
            Title = "Auto-merge Feature",
            Status = TaskOverallStatus.Implemented,
            CurrentStageId = "final-stage",
            BaseBranch = "main",
            MergeMode = MergeMode.Automatic
        });

        var task502 = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 502,
            WorkspaceId = 1,
            Title = "Dependent on 501",
            Status = TaskOverallStatus.Open
        });

        _depStore.AddTask(501, 1, TaskOverallStatus.Implemented);
        _depStore.AddTask(502, 1, TaskOverallStatus.Open);
        _depStore.AddDependency(1, 502, 501);

        var finalStage = new TestWorkflowStage("final-stage")
        {
            Handler = (ctx, ct) => Task.FromResult(StageExecutionResult.Success())
        };
        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { finalStage });

        var orchestrator = CreateOrchestrator(new DefaultWorkflowPipelineProvider(pipeline));

        // Act: Process final stage
        await orchestrator.ProcessTaskAsync(501);

        // 1. ForgeTask 501 automatically merged and marked Closed
        var updated501 = await _taskRepo.GetByIdAsync(501);
        Assert.NotNull(updated501);
        Assert.Equal(TaskOverallStatus.Closed, updated501.Status);

        // 2. Git squash merge performed
        Assert.Single(_gitManager.SquashMerges);
        Assert.Equal(501, _gitManager.SquashMerges[0].TaskId);

        // 3. Dependent task 502 unblocked and enqueued to inference
        Assert.Contains(502, _jobQueue.InferenceQueue);
    }

    [Fact]
    public async Task FinalStage_WithManualReviewRequired_PausesAtReadyAwaitingMaintainerApproval()
    {
        // ForgeTask 601 (ManualReviewRequired merge mode)
        // ForgeTask 602 depends on 601
        var task601 = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 601,
            WorkspaceId = 1,
            Title = "Manual Review Feature",
            Status = TaskOverallStatus.Implemented,
            CurrentStageId = "final-stage",
            BaseBranch = "main",
            MergeMode = MergeMode.ManualReviewRequired
        });

        var task602 = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 602,
            WorkspaceId = 1,
            Title = "Dependent on 601",
            Status = TaskOverallStatus.Open
        });

        _depStore.AddTask(601, 1, TaskOverallStatus.Implemented);
        _depStore.AddTask(602, 1, TaskOverallStatus.Open);
        _depStore.AddDependency(1, 602, 601);

        _gitManager.GeneratedDiff = "diff --git a/Feature.cs b/Feature.cs\n+new line";

        var finalStage = new TestWorkflowStage("final-stage")
        {
            Handler = (ctx, ct) => Task.FromResult(StageExecutionResult.Success())
        };
        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { finalStage });

        var orchestrator = CreateOrchestrator(new DefaultWorkflowPipelineProvider(pipeline));

        // Act: Process final stage
        await orchestrator.ProcessTaskAsync(601);

        // 1. ForgeTask 601 transitions to Ready, NOT Closed
        var updated601 = await _taskRepo.GetByIdAsync(601);
        Assert.NotNull(updated601);
        Assert.Equal(TaskOverallStatus.Ready, updated601.Status);

        // 2. Review diff stat saved
        var bag = PipelineDataBag.FromJson(updated601.PipelineDataJson);
        Assert.Equal(_gitManager.GeneratedDiffStat, bag.Get<string>(PipelineDataBagKeys.ReviewDiffStat));

        // 3. No git squash merge executed yet
        Assert.Empty(_gitManager.SquashMerges);

        // 4. Dependent task 602 NOT yet unblocked
        Assert.DoesNotContain(602, _jobQueue.InferenceQueue);

        // Act: Maintainer manually approves in Review Room
        await orchestrator.CompleteTaskAndCascadeAsync(601, "Merge branch 'feat/task-601' into main");

        // 5. Now marked Closed and squash merged
        var closed601 = await _taskRepo.GetByIdAsync(601);
        Assert.NotNull(closed601);
        Assert.Equal(TaskOverallStatus.Closed, closed601.Status);
        Assert.Single(_gitManager.SquashMerges);

        // 6. Dependent task 602 unblocked now
        Assert.Contains(602, _jobQueue.InferenceQueue);
    }

    [Fact]
    public async Task PromoteSuggestedTaskAsync_WhenPromotedToOpenWithDependenciesClosed_EnqueuesToInference()
    {
        var prereq = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 701,
            WorkspaceId = 1,
            Status = TaskOverallStatus.Closed
        });
        _depStore.AddTask(701, 1, TaskOverallStatus.Closed);

        var suggested = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 702,
            WorkspaceId = 1,
            Title = "Suggested Feature",
            Status = TaskOverallStatus.Suggested
        }, dependsOn: new[] { 701 });

        _depStore.AddTask(702, 1, TaskOverallStatus.Suggested);
        _depStore.AddDependency(1, 702, 701);

        var orchestrator = CreateOrchestrator();

        // Act: Maintainer promotes Suggested -> Open
        await orchestrator.PromoteSuggestedTaskAsync(
            taskId: 702,
            targetStatus: TaskOverallStatus.Open,
            priority: 2,
            mergeMode: MergeMode.Automatic);

        var updated = await _taskRepo.GetByIdAsync(702);
        Assert.NotNull(updated);
        Assert.Equal(TaskOverallStatus.Open, updated.Status);
        Assert.Equal(2, updated.Priority);
        Assert.Equal(MergeMode.Automatic, updated.MergeMode);

        // Because prerequisite 701 is Closed, 702 is ready and enqueued!
        Assert.Contains(702, _jobQueue.InferenceQueue);
    }

    [Fact]
    public async Task PromoteSuggestedTaskAsync_WhenPromotedToOpenWithUnfinishedPrerequisites_DoesNotEnqueue()
    {
        var prereq = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 801,
            WorkspaceId = 1,
            Status = TaskOverallStatus.Open // Not yet closed!
        });
        _depStore.AddTask(801, 1, TaskOverallStatus.Open);

        var suggested = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 802,
            WorkspaceId = 1,
            Title = "Suggested Feature 2",
            Status = TaskOverallStatus.Suggested
        }, dependsOn: new[] { 801 });

        _depStore.AddTask(802, 1, TaskOverallStatus.Suggested);
        _depStore.AddDependency(1, 802, 801);

        var orchestrator = CreateOrchestrator();

        // Act: Maintainer promotes Suggested -> Open
        await orchestrator.PromoteSuggestedTaskAsync(
            taskId: 802,
            targetStatus: TaskOverallStatus.Open,
            priority: 1,
            mergeMode: MergeMode.ManualReviewRequired);

        var updated = await _taskRepo.GetByIdAsync(802);
        Assert.NotNull(updated);
        Assert.Equal(TaskOverallStatus.Open, updated.Status);

        // Prerequisite 801 is not closed yet, so 802 must NOT be enqueued
        Assert.DoesNotContain(802, _jobQueue.InferenceQueue);
    }

    [Fact]
    public async Task PromoteSuggestedTaskAsync_WhenRejected_SetsStatusRejectedAndDoesNotEnqueue()
    {
        var suggested = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 901,
            WorkspaceId = 1,
            Title = "Out of scope feature",
            Status = TaskOverallStatus.Suggested
        });
        _depStore.AddTask(901, 1, TaskOverallStatus.Suggested);

        var orchestrator = CreateOrchestrator();

        // Act: Maintainer rejects task
        await orchestrator.PromoteSuggestedTaskAsync(
            taskId: 901,
            targetStatus: TaskOverallStatus.Rejected,
            priority: 5,
            mergeMode: MergeMode.ManualReviewRequired);

        var updated = await _taskRepo.GetByIdAsync(901);
        Assert.NotNull(updated);
        Assert.Equal(TaskOverallStatus.Rejected, updated.Status);
        Assert.DoesNotContain(901, _jobQueue.InferenceQueue);
    }

    [Fact]
    public async Task FinalStage_WithExternalPullRequest_DeliversPullRequest_PersistsMetadata_AndPausesAtReady()
    {
        _workspace.GitHubSettings = new GitHubIntegrationSettings
        {
            Enabled = true,
            PullRequestDeliveryEnabled = true,
            RepositoryOwner = "acme",
            RepositoryName = "demo",
            CreateAsDraft = true
        };

        _prDeliveryMock
            .Setup(s => s.DeliverPullRequestAsync(It.IsAny<ForgeTask>(), It.IsAny<Workspace>(), It.IsAny<CancellationToken>()))
            .Callback<ForgeTask, Workspace, CancellationToken>((t, _, _) =>
            {
                t.PullRequestNumber = 45;
                t.PullRequestUrl = "https://github.com/acme/demo/pull/45";
            })
            .ReturnsAsync(new GitHubPullRequest
            {
                Number = 45,
                HtmlUrl = "https://github.com/acme/demo/pull/45",
                Draft = true,
                State = "open"
            });

        await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 1001,
            WorkspaceId = 1,
            Title = "Outbound PR Feature",
            Status = TaskOverallStatus.Implemented,
            CurrentStageId = "final-stage",
            BaseBranch = "main",
            MergeMode = MergeMode.ExternalPullRequest
        });

        await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 1002,
            WorkspaceId = 1,
            Title = "Dependent on 1001",
            Status = TaskOverallStatus.Open
        });

        _depStore.AddTask(1001, 1, TaskOverallStatus.Implemented);
        _depStore.AddTask(1002, 1, TaskOverallStatus.Open);
        _depStore.AddDependency(1, 1002, 1001);

        var finalStage = new TestWorkflowStage("final-stage")
        {
            Handler = (_, _) => Task.FromResult(StageExecutionResult.Success())
        };
        var orchestrator = CreateOrchestrator(new DefaultWorkflowPipelineProvider(new WorkflowPipeline(new IWorkflowStage[] { finalStage })));

        // Act
        await orchestrator.ProcessTaskAsync(1001);

        // Assert
        var updated = await _taskRepo.GetByIdAsync(1001);
        Assert.NotNull(updated);
        Assert.Equal(TaskOverallStatus.Ready, updated.Status);
        Assert.True(updated.HasExecutionRun);
        Assert.Equal(45, updated.PullRequestNumber);
        Assert.Equal("https://github.com/acme/demo/pull/45", updated.PullRequestUrl);
        Assert.Empty(_gitManager.SquashMerges);
        Assert.DoesNotContain(1002, _jobQueue.InferenceQueue);

        _prDeliveryMock.Verify(
            s => s.DeliverPullRequestAsync(It.Is<ForgeTask>(t => t.Id == 1001), It.IsAny<Workspace>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task FinalStage_WithExternalPullRequest_WhenDeliveryFails_StillTransitionsToReadyWithRedactedWarningLog()
    {
        _prDeliveryMock
            .Setup(s => s.DeliverPullRequestAsync(It.IsAny<ForgeTask>(), It.IsAny<Workspace>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("GitHub push failed with Authorization: Bearer ghp_secret999"));

        await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 1003,
            WorkspaceId = 1,
            Title = "Resilient PR Task",
            Status = TaskOverallStatus.Implemented,
            CurrentStageId = "final-stage",
            BaseBranch = "main",
            MergeMode = MergeMode.ExternalPullRequest
        });
        _depStore.AddTask(1003, 1, TaskOverallStatus.Implemented);

        var finalStage = new TestWorkflowStage("final-stage")
        {
            Handler = (_, _) => Task.FromResult(StageExecutionResult.Success())
        };
        var orchestrator = CreateOrchestrator(new DefaultWorkflowPipelineProvider(new WorkflowPipeline(new IWorkflowStage[] { finalStage })));

        // Act: must not fail the task even though GitHub delivery threw
        await orchestrator.ProcessTaskAsync(1003);

        // Assert: task transitions to Ready (not Failed) and logs a redacted warning
        var updated = await _taskRepo.GetByIdAsync(1003);
        Assert.NotNull(updated);
        Assert.Equal(TaskOverallStatus.Ready, updated.Status);
        Assert.True(updated.HasExecutionRun);

        var warningLog = Assert.Single(updated.ExecutionLogs, l => l.Level == LogLevel.Warning);
        Assert.Contains("Automatic GitHub Pull Request delivery failed", warningLog.Message);
        Assert.DoesNotContain("ghp_secret999", warningLog.Message);
    }

    [Fact]
    public async Task RequestChangesAsync_OnExternalPullRequestTask_DiscardsApprovalAndUpdatesExistingPrOnReRun()
    {
        int? observedPrNumberOnSecondDelivery = null;
        _prDeliveryMock
            .Setup(s => s.DeliverPullRequestAsync(It.IsAny<ForgeTask>(), It.IsAny<Workspace>(), It.IsAny<CancellationToken>()))
            .Callback<ForgeTask, Workspace, CancellationToken>((t, _, _) =>
            {
                observedPrNumberOnSecondDelivery = t.PullRequestNumber;
                t.PullRequestNumber = 45;
                t.PullRequestUrl = "https://github.com/acme/demo/pull/45";
            })
            .ReturnsAsync(new GitHubPullRequest
            {
                Number = 45,
                HtmlUrl = "https://github.com/acme/demo/pull/45",
                State = "open"
            });

        await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 1004,
            WorkspaceId = 1,
            Title = "PR Re-Review Task",
            Status = TaskOverallStatus.Ready,
            CurrentStageId = StandardStageIds.PostReviewVerification,
            BaseBranch = "main",
            MergeMode = MergeMode.ExternalPullRequest,
            PullRequestNumber = 45,
            PullRequestUrl = "https://github.com/acme/demo/pull/45",
            HasExecutionRun = true
        });
        _depStore.AddTask(1004, 1, TaskOverallStatus.Ready);

        var implStage = new TestWorkflowStage(StandardStageIds.Implementation, WorkflowStageKind.Agentic)
        {
            Handler = (_, _) => Task.FromResult(StageExecutionResult.Success())
        };
        var verifyStage = new TestWorkflowStage(StandardStageIds.PostReviewVerification, WorkflowStageKind.Deterministic)
        {
            Handler = (_, _) => Task.FromResult(StageExecutionResult.Success())
        };
        var orchestrator = CreateOrchestrator(new DefaultWorkflowPipelineProvider(new WorkflowPipeline(new IWorkflowStage[] { implStage, verifyStage })));

        // Act 1: Request changes from Ready
        await orchestrator.RequestChangesAsync(1004, "Please handle null input");

        var afterRequestChanges = await _taskRepo.GetByIdAsync(1004);
        Assert.NotNull(afterRequestChanges);
        Assert.Equal(TaskOverallStatus.Open, afterRequestChanges.Status);
        Assert.Equal(StandardStageIds.Implementation, afterRequestChanges.CurrentStageId);
        Assert.Equal(45, afterRequestChanges.PullRequestNumber);

        // Act 2: Run Implementation -> PostReviewVerification
        await orchestrator.ProcessTaskAsync(1004); // runs Implementation -> transitions to PostReviewVerification
        await orchestrator.ProcessTaskAsync(1004); // runs PostReviewVerification -> updates PR #45 -> Ready

        var afterReRun = await _taskRepo.GetByIdAsync(1004);
        Assert.NotNull(afterReRun);
        Assert.Equal(TaskOverallStatus.Ready, afterReRun.Status);
        Assert.Equal(45, observedPrNumberOnSecondDelivery);
        Assert.Equal(45, afterReRun.PullRequestNumber);
    }

    [Fact]
    public async Task CloseExternallyMergedTaskAsync_WhenPrNotMerged_ThrowsAndLeavesTaskReadyAndDependentsBlocked()
    {
        _prDeliveryMock
            .Setup(s => s.VerifyAndSyncExternallyMergedPullRequestAsync(It.IsAny<ForgeTask>(), It.IsAny<Workspace>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("GitHub Pull Request #45 is not merged yet (state: 'open')."));

        await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 10,
            WorkspaceId = 1,
            Title = "Unmerged PR Task",
            Status = TaskOverallStatus.Ready,
            MergeMode = MergeMode.ExternalPullRequest,
            PullRequestNumber = 45,
            PullRequestUrl = "https://github.com/acme/demo/pull/45"
        });
        await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 11,
            WorkspaceId = 1,
            Title = "Downstream Dependent",
            Status = TaskOverallStatus.Open
        }, dependsOn: new[] { 10 });

        _depStore.AddTask(10, 1, TaskOverallStatus.Ready);
        _depStore.AddTask(11, 1, TaskOverallStatus.Open);
        _depStore.AddDependency(1, 11, 10);

        var orchestrator = CreateOrchestrator();

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => orchestrator.CloseExternallyMergedTaskAsync(10));

        var task10 = await _taskRepo.GetByIdAsync(10);
        Assert.NotNull(task10);
        Assert.Equal(TaskOverallStatus.Ready, task10.Status);
        Assert.DoesNotContain(11, _jobQueue.InferenceQueue);
    }

    [Fact]
    public async Task CloseExternallyMergedTaskAsync_WhenPrMerged_RecordsMergeSha_CleansResources_ClosesTask_AndCascadesDag()
    {
        _prDeliveryMock
            .Setup(s => s.VerifyAndSyncExternallyMergedPullRequestAsync(It.IsAny<ForgeTask>(), It.IsAny<Workspace>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubPullRequest
            {
                Number = 45,
                HtmlUrl = "https://github.com/acme/demo/pull/45",
                State = "closed",
                Merged = true,
                MergeCommitSha = "abc1234"
            });

        await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 10,
            WorkspaceId = 1,
            Title = "Merged PR Task",
            Status = TaskOverallStatus.Ready,
            BaseBranch = "main",
            FeatureBranch = "feature/task-10",
            MergeMode = MergeMode.ExternalPullRequest,
            PullRequestNumber = 45,
            PullRequestUrl = "https://github.com/acme/demo/pull/45"
        });
        await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 11,
            WorkspaceId = 1,
            Title = "Downstream Dependent",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.TaskReview
        }, dependsOn: new[] { 10 });

        _depStore.AddTask(10, 1, TaskOverallStatus.Ready);
        _depStore.AddTask(11, 1, TaskOverallStatus.Open);
        _depStore.AddDependency(1, 11, 10);

        var orchestrator = CreateOrchestrator();

        // Act
        var result = await orchestrator.CloseExternallyMergedTaskAsync(10);

        // Assert
        Assert.Equal(CompleteTaskStatus.Completed, result.Status);

        var closed10 = await _taskRepo.GetByIdAsync(10);
        Assert.NotNull(closed10);
        Assert.Equal(TaskOverallStatus.Closed, closed10.Status);
        Assert.Equal("abc1234", closed10.SquashCommitSha);
        Assert.Equal(string.Empty, closed10.CurrentStageId);

        // Local SquashAndMergeAsync must NOT be called
        Assert.Empty(_gitManager.SquashMerges);

        // Local task branch deleted
        Assert.Contains(10, _gitManager.DeletedTaskBranches);

        // Dependent task #11 unblocked and enqueued!
        Assert.Contains(11, _jobQueue.TaskReviewQueue);
    }

    [Fact]
    public async Task MixedModeWorkspace_AutomaticAndManualTasks_NeverTriggerPullRequestDelivery()
    {
        _workspace.GitHubSettings = new GitHubIntegrationSettings
        {
            Enabled = true,
            PullRequestDeliveryEnabled = true,
            RepositoryOwner = "acme",
            RepositoryName = "demo"
        };

        await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 2001,
            WorkspaceId = 1,
            Title = "Auto Task in PR-Enabled Workspace",
            Status = TaskOverallStatus.Implemented,
            CurrentStageId = "final-stage",
            BaseBranch = "main",
            MergeMode = MergeMode.Automatic
        });

        await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 2002,
            WorkspaceId = 1,
            Title = "Manual Task in PR-Enabled Workspace",
            Status = TaskOverallStatus.Implemented,
            CurrentStageId = "final-stage",
            BaseBranch = "main",
            MergeMode = MergeMode.ManualReviewRequired
        });

        _depStore.AddTask(2001, 1, TaskOverallStatus.Implemented);
        _depStore.AddTask(2002, 1, TaskOverallStatus.Implemented);

        var finalStage = new TestWorkflowStage("final-stage")
        {
            Handler = (_, _) => Task.FromResult(StageExecutionResult.Success())
        };
        var orchestrator = CreateOrchestrator(new DefaultWorkflowPipelineProvider(new WorkflowPipeline(new IWorkflowStage[] { finalStage })));

        await orchestrator.ProcessTaskAsync(2001);
        await orchestrator.ProcessTaskAsync(2002);

        Assert.Equal(TaskOverallStatus.Closed, (await _taskRepo.GetByIdAsync(2001))!.Status);
        Assert.Equal(TaskOverallStatus.Ready, (await _taskRepo.GetByIdAsync(2002))!.Status);

        _prDeliveryMock.Verify(
            s => s.DeliverPullRequestAsync(It.IsAny<ForgeTask>(), It.IsAny<Workspace>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
