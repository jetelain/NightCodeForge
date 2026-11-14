using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Tests.Fixtures;
using NightCodeForge.Core.Workflow;
using Xunit;

namespace NightCodeForge.Core.Tests.Workflow;

public class GitHubTaskMirrorIntegrationTests : IDisposable
{
    private readonly string _tempDir;

    public GitHubTaskMirrorIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ncf_mirror_int_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore temp directory cleanup failures in tests
        }
    }

    private Workspace CreateGitHubWorkspace(int id = 1, bool autoCreateLocalIssues = false) => new()
    {
        Id = id,
        Name = $"GitHub Workspace {id}",
        RepositoryPath = _tempDir,
        DefaultBaseBranch = "main",
        GitHubSettings = new GitHubIntegrationSettings
        {
            Enabled = true,
            RepositoryOwner = "acme",
            RepositoryName = "nightcodeforge",
            TaskMirroringEnabled = true,
            PullRequestDeliveryEnabled = true,
            CloseTaskOnPrMerge = true,
            AutoAssignIssueOnStart = true,
            SyncStatusLabels = true,
            PostMilestoneComments = true,
            CreateGitHubIssuesForLocalTasks = autoCreateLocalIssues
        }
    };

    [Fact]
    public async Task PreFlightClaimConflict_SuspendsTask_CancelAndOverrideAnswersDriveWorkflow()
    {
        var taskRepo = new InMemoryTaskRepository();
        var wsRepo = new InMemoryWorkspaceRepository();
        var depStore = new InMemoryTaskDependencyStore();
        var jobQueue = new FakeTaskJobQueue();

        var workspace = await wsRepo.CreateAsync(CreateGitHubWorkspace());
        var stage1ExecutedFor = new List<int>();

        var pipeline = new WorkflowPipeline(
        [
            new TestWorkflowStage(StandardStageIds.TaskReview, WorkflowStageKind.Agentic)
            {
                QueueTier = StageQueueTier.TaskReview,
                Handler = (ctx, _) =>
                {
                    stage1ExecutedFor.Add(ctx.ForgeTask.Id);
                    ctx.DataBag.Set(PipelineDataBagKeys.ApprovedPlan, "Approved architecture plan");
                    return Task.FromResult(StageExecutionResult.Success());
                }
            }
        ]);

        var builder = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = wsRepo,
            DependencyStore = depStore,
            JobQueue = jobQueue,
            PipelineProvider = new DefaultWorkflowPipelineProvider(pipeline)
        };

        var cancelTask = await taskRepo.CreateAsync(new ForgeTask
        {
            WorkspaceId = workspace.Id,
            Title = "Task cancelled after conflict",
            Description = "Req",
            Status = TaskOverallStatus.Open,
            BaseBranch = "main",
            ExternalSource = GitHubSyncConstants.ExternalSourceGitHub,
            ExternalReferenceId = "42"
        });
        depStore.AddTask(cancelTask.Id, workspace.Id, TaskOverallStatus.Open);

        var overrideTask = await taskRepo.CreateAsync(new ForgeTask
        {
            WorkspaceId = workspace.Id,
            Title = "Task overridden after conflict",
            Description = "Req",
            Status = TaskOverallStatus.Open,
            BaseBranch = "main",
            ExternalSource = GitHubSyncConstants.ExternalSourceGitHub,
            ExternalReferenceId = "43"
        });
        depStore.AddTask(overrideTask.Id, workspace.Id, TaskOverallStatus.Open);

        var nextQuestionId = 500;
        builder.GitHubTaskMirrorMock
            .Setup(m => m.TryClaimIssueForExecutionAsync(It.IsAny<ForgeTask>(), It.IsAny<Workspace>(), It.IsAny<CancellationToken>()))
            .Returns<ForgeTask, Workspace, CancellationToken>(async (t, _, ct) =>
            {
                var hasOverride = t.Questions.Any(q =>
                    q.Status == QuestionStatus.Answered &&
                    q.Answer == GitHubClaimConflictConstants.OverrideClaimOption);
                if (hasOverride)
                {
                    return true;
                }

                var qId = ++nextQuestionId;
                var q = new Question
                {
                    Id = qId,
                    TaskId = t.Id,
                    StageId = StandardStageIds.TaskReview,
                    BatchId = GitHubClaimConflictConstants.ClaimConflictBatchPrefix + t.Id,
                    QuestionType = QuestionType.SingleChoice,
                    Prompt = "Issue already claimed",
                    OptionsJson = $"[\"{GitHubClaimConflictConstants.OverrideClaimOption}\",\"{GitHubClaimConflictConstants.CancelTaskOption}\"]",
                    Status = QuestionStatus.Pending,
                    AskedAt = DateTime.UtcNow
                };
                taskRepo.AddQuestion(q);
                t.Questions.Add(q);
                t.IsWaitingForHuman = true;
                t.InterruptedStageId = StandardStageIds.TaskReview;
                t.CurrentStageId = StandardStageIds.TaskReview;
                await taskRepo.UpdateAsync(t, cancellationToken: ct);
                await depStore.SetWaitingForHumanAsync(t.Id, true, ct);
                return false;
            });

        builder.GitHubTaskMirrorMock
            .Setup(m => m.TryHandleClaimConflictAnswerAsync(
                It.IsAny<ForgeTask>(),
                It.IsAny<Workspace>(),
                It.IsAny<IReadOnlyList<Question>>(),
                It.IsAny<CancellationToken>()))
            .Returns<ForgeTask, Workspace, IReadOnlyList<Question>, CancellationToken>(async (t, _, qs, ct) =>
            {
                if (qs.Any(q => q.Answer == GitHubClaimConflictConstants.CancelTaskOption))
                {
                    t.Status = TaskOverallStatus.Cancelled;
                    t.IsWaitingForHuman = false;
                    await taskRepo.UpdateAsync(t, cancellationToken: ct);
                    await depStore.UpdateTaskStatusAsync(t.Id, TaskOverallStatus.Cancelled, ct);
                    return true;
                }
                return false;
            });

        var orchestrator = builder.Build();

        // 1. Both tasks suspend before Stage 1 due to claim conflicts
        await orchestrator.ProcessTaskAsync(cancelTask.Id);
        await orchestrator.ProcessTaskAsync(overrideTask.Id);

        Assert.Empty(stage1ExecutedFor);
        var suspendedCancel = await taskRepo.GetByIdAsync(cancelTask.Id);
        Assert.NotNull(suspendedCancel);
        Assert.True(suspendedCancel.IsWaitingForHuman);
        Assert.Equal(StandardStageIds.TaskReview, suspendedCancel.InterruptedStageId);

        // 2. Answering Cancel transitions task to Cancelled without enqueuing Stage 1
        var cancelQuestionId = suspendedCancel.Questions.Single().Id;
        await orchestrator.SubmitAnswersAsync(cancelTask.Id, new Dictionary<int, string>
        {
            [cancelQuestionId] = GitHubClaimConflictConstants.CancelTaskOption
        });

        var cancelledAfterAnswer = await taskRepo.GetByIdAsync(cancelTask.Id);
        Assert.NotNull(cancelledAfterAnswer);
        Assert.Equal(TaskOverallStatus.Cancelled, cancelledAfterAnswer.Status);
        Assert.False(cancelledAfterAnswer.IsWaitingForHuman);
        Assert.Null(cancelledAfterAnswer.InterruptedStageId);
        Assert.DoesNotContain(jobQueue.EnqueuedStages, j => j.TaskId == cancelTask.Id);

        // 3. Answering Override re-enqueues Stage 1 without polluting PipelineDataBag or leaving InterruptedStageId set
        var suspendedOverride = await taskRepo.GetByIdAsync(overrideTask.Id);
        var overrideQuestionId = suspendedOverride!.Questions.Single().Id;
        await orchestrator.SubmitAnswersAsync(overrideTask.Id, new Dictionary<int, string>
        {
            [overrideQuestionId] = GitHubClaimConflictConstants.OverrideClaimOption
        });

        var resumedOverride = await taskRepo.GetByIdAsync(overrideTask.Id);
        Assert.NotNull(resumedOverride);
        Assert.False(resumedOverride.IsWaitingForHuman);
        Assert.Null(resumedOverride.InterruptedStageId);
        var overrideBag = PipelineDataBag.FromJson(resumedOverride.PipelineDataJson);
        Assert.False(overrideBag.ContainsKey(PipelineDataBagKeys.LastHumanAnswer));
        Assert.False(overrideBag.ContainsKey(PipelineDataBagKeys.LastHumanAnswerJson));
        Assert.Contains(jobQueue.EnqueuedStages, j => j.TaskId == overrideTask.Id && j.Tier == StageQueueTier.TaskReview);

        await orchestrator.ProcessTaskAsync(overrideTask.Id);
        Assert.Contains(overrideTask.Id, stage1ExecutedFor);
    }

    [Fact]
    public async Task PreFlightExternallyClosedIssue_CancelsTaskBeforeStage1()
    {
        var taskRepo = new InMemoryTaskRepository();
        var wsRepo = new InMemoryWorkspaceRepository();
        var depStore = new InMemoryTaskDependencyStore();
        var workspace = await wsRepo.CreateAsync(CreateGitHubWorkspace());
        var stage1Ran = false;

        var pipeline = new WorkflowPipeline(
        [
            new TestWorkflowStage(StandardStageIds.TaskReview, WorkflowStageKind.Agentic)
            {
                Handler = (_, _) =>
                {
                    stage1Ran = true;
                    return Task.FromResult(StageExecutionResult.Success());
                }
            }
        ]);

        var builder = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = wsRepo,
            DependencyStore = depStore,
            PipelineProvider = new DefaultWorkflowPipelineProvider(pipeline)
        };

        builder.GitHubTaskMirrorMock
            .Setup(m => m.TryClaimIssueForExecutionAsync(It.IsAny<ForgeTask>(), It.IsAny<Workspace>(), It.IsAny<CancellationToken>()))
            .Returns<ForgeTask, Workspace, CancellationToken>(async (t, _, ct) =>
            {
                t.Status = TaskOverallStatus.Cancelled;
                await taskRepo.UpdateAsync(t, cancellationToken: ct);
                await depStore.UpdateTaskStatusAsync(t.Id, TaskOverallStatus.Cancelled, ct);
                return false;
            });

        var task = await taskRepo.CreateAsync(new ForgeTask
        {
            WorkspaceId = workspace.Id,
            Title = "Closed externally",
            Status = TaskOverallStatus.Open,
            BaseBranch = "main",
            ExternalSource = GitHubSyncConstants.ExternalSourceGitHub,
            ExternalReferenceId = "88"
        });
        depStore.AddTask(task.Id, workspace.Id, TaskOverallStatus.Open);

        var orchestrator = builder.Build();
        await orchestrator.ProcessTaskAsync(task.Id);

        var reloaded = await taskRepo.GetByIdAsync(task.Id);
        Assert.Equal(TaskOverallStatus.Cancelled, reloaded!.Status);
        Assert.False(stage1Ran);
    }

    [Fact]
    public async Task PipelineAndPromotion_InvokeOpenedReviewGatesAndReadyMirrors()
    {
        var taskRepo = new InMemoryTaskRepository();
        var wsRepo = new InMemoryWorkspaceRepository();
        var depStore = new InMemoryTaskDependencyStore();
        var workspace = await wsRepo.CreateAsync(CreateGitHubWorkspace());

        var pipeline = new WorkflowPipeline(
        [
            new TestWorkflowStage(StandardStageIds.TaskReview, WorkflowStageKind.Agentic)
            {
                QueueTier = StageQueueTier.TaskReview,
                Handler = (ctx, _) =>
                {
                    ctx.DataBag.Set(PipelineDataBagKeys.ApprovedPlan, "Approved Plan Summary");
                    return Task.FromResult(StageExecutionResult.Success());
                }
            },
            new TestWorkflowStage(StandardStageIds.PostImplementationGates, WorkflowStageKind.Deterministic)
            {
                QueueTier = StageQueueTier.Implementation,
                Handler = (ctx, _) =>
                {
                    ctx.DataBag.Set(PipelineDataBagKeys.TestsCurrentTotal, 12);
                    ctx.DataBag.Set(PipelineDataBagKeys.TestsCurrentPassed, 12);
                    return Task.FromResult(StageExecutionResult.Success());
                }
            }
        ]);

        var mdSyncMock = new Mock<ITaskMarkdownSyncService>();
        var suggestedTask = await taskRepo.CreateAsync(new ForgeTask
        {
            WorkspaceId = workspace.Id,
            Title = "Promoted task",
            Description = "Initial draft description",
            Status = TaskOverallStatus.Suggested,
            BaseBranch = "main",
            ExternalSource = GitHubSyncConstants.ExternalSourceGitHub,
            ExternalReferenceId = "90"
        });
        depStore.AddTask(suggestedTask.Id, workspace.Id, TaskOverallStatus.Suggested);

        var baseline = new Dictionary<int, TaskBaselineEntry>
        {
            [suggestedTask.Id] = new TaskBaselineEntry(
                suggestedTask.Id,
                suggestedTask.Version,
                suggestedTask.UpdatedAt,
                "hash-1",
                "docs/tasks/task.md",
                new TaskFrontmatter { Id = suggestedTask.Id, Title = suggestedTask.Title },
                suggestedTask.Description ?? string.Empty)
        };
        mdSyncMock
            .Setup(m => m.SyncAllTasksToDiskAsync(
                It.IsAny<Workspace>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SyncAllToDiskResult(new Dictionary<int, string>(), baseline));
        mdSyncMock
            .Setup(m => m.SyncAllTasksFromDiskAsync(
                It.IsAny<Workspace>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<IReadOnlyDictionary<int, TaskBaselineEntry>?>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new ForgeTask
                {
                    Id = suggestedTask.Id,
                    WorkspaceId = workspace.Id,
                    Title = suggestedTask.Title,
                    Description = "Refined task specification produced in Stage 1",
                    Version = suggestedTask.Version + 1,
                    Status = TaskOverallStatus.Open,
                    BaseBranch = "main",
                    ExternalSource = GitHubSyncConstants.ExternalSourceGitHub,
                    ExternalReferenceId = "90"
                }
            ]);

        var builder = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = wsRepo,
            DependencyStore = depStore,
            MarkdownSyncService = mdSyncMock.Object,
            PipelineProvider = new DefaultWorkflowPipelineProvider(pipeline)
        };

        var orchestrator = builder.Build();

        await orchestrator.PromoteSuggestedTaskAsync(
            suggestedTask.Id,
            TaskOverallStatus.Open,
            TaskPriority.Normal,
            MergeMode.ManualReviewRequired);

        builder.GitHubTaskMirrorMock.Verify(
            m => m.MirrorTaskOpenedAsync(
                It.Is<ForgeTask>(t => t.Id == suggestedTask.Id && t.Status == TaskOverallStatus.Open),
                It.Is<Workspace>(w => w.Id == workspace.Id),
                It.IsAny<CancellationToken>()),
            Times.Once);

        // Cohort 1: executes TaskReview and yields at cohort boundary to Implementation queue
        await orchestrator.ProcessTaskAsync(suggestedTask.Id);
        // Cohort 2: executes PostImplementationGates and transitions task to Ready
        await orchestrator.ProcessTaskAsync(suggestedTask.Id);

        builder.GitHubTaskMirrorMock.Verify(
            m => m.TryClaimIssueForExecutionAsync(
                It.Is<ForgeTask>(t => t.Id == suggestedTask.Id),
                It.Is<Workspace>(w => w.Id == workspace.Id),
                It.IsAny<CancellationToken>()),
            Times.Once);

        var readyTask = await taskRepo.GetByIdAsync(suggestedTask.Id);
        var logs = string.Join(" | ", taskRepo.GetAllLogs().Select(l => $"{l.StageId}:{l.Level}:{l.Message}"));
        Assert.True(readyTask!.Status == TaskOverallStatus.Ready, $"Expected Ready but got {readyTask.Status}. Logs: {logs}");

        builder.GitHubTaskMirrorMock.Verify(
            m => m.MirrorTaskReviewCompletedAsync(
                It.Is<ForgeTask>(t => t.Id == suggestedTask.Id),
                It.Is<Workspace>(w => w.Id == workspace.Id),
                "Approved Plan Summary",
                It.IsAny<CancellationToken>()),
            Times.Once);
        builder.GitHubTaskMirrorMock.Verify(
            m => m.MirrorPostImplementationGatesCompletedAsync(
                It.Is<ForgeTask>(t => t.Id == suggestedTask.Id),
                It.Is<Workspace>(w => w.Id == workspace.Id),
                It.IsAny<CancellationToken>()),
            Times.Once);
        builder.GitHubTaskMirrorMock.Verify(
            m => m.MirrorTaskReadyAsync(
                It.Is<ForgeTask>(t => t.Id == suggestedTask.Id && t.Status == TaskOverallStatus.Ready),
                It.Is<Workspace>(w => w.Id == workspace.Id),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SyncMergedPullRequestsAsync_ClosesPrTaskAndCascadesDownstreamDependent()
    {
        var taskRepo = new InMemoryTaskRepository();
        var wsRepo = new InMemoryWorkspaceRepository();
        var depStore = new InMemoryTaskDependencyStore();
        var jobQueue = new FakeTaskJobQueue();

        var workspace = await wsRepo.CreateAsync(CreateGitHubWorkspace());

        var builder = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = wsRepo,
            DependencyStore = depStore,
            JobQueue = jobQueue
        };

        var taskA = await taskRepo.CreateAsync(new ForgeTask
        {
            WorkspaceId = workspace.Id,
            Title = "Upstream PR task",
            Status = TaskOverallStatus.Ready,
            BaseBranch = "main",
            MergeMode = MergeMode.ExternalPullRequest,
            ExternalSource = GitHubSyncConstants.ExternalSourceGitHub,
            ExternalReferenceId = "42",
            PullRequestNumber = 77,
            PullRequestUrl = "https://github.com/acme/nightcodeforge/pull/77"
        });
        var taskB = await taskRepo.CreateAsync(new ForgeTask
        {
            WorkspaceId = workspace.Id,
            Title = "Downstream dependent task",
            Status = TaskOverallStatus.Open,
            BaseBranch = "main"
        });
        depStore.AddTask(taskA.Id, workspace.Id, TaskOverallStatus.Ready);
        depStore.AddTask(taskB.Id, workspace.Id, TaskOverallStatus.Open);
        depStore.AddDependency(workspace.Id, taskB.Id, taskA.Id);

        builder.GitHubPrDeliveryMock
            .Setup(d => d.VerifyAndSyncExternallyMergedPullRequestAsync(
                It.Is<ForgeTask>(t => t.Id == taskA.Id),
                It.IsAny<Workspace>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubPullRequest
            {
                Number = 77,
                State = "closed",
                Merged = true,
                MergeCommitSha = "merged-sha-77",
                HtmlUrl = "https://github.com/acme/nightcodeforge/pull/77"
            });

        builder.GitHubTaskMirrorMock
            .Setup(m => m.SyncMergedPullRequestsAsync(
                It.Is<Workspace>(w => w.Id == workspace.Id),
                It.IsAny<Func<int, CancellationToken, Task>>(),
                It.IsAny<CancellationToken>()))
            .Returns<Workspace, Func<int, CancellationToken, Task>, CancellationToken>(async (_, closeCallback, ct) =>
            {
                taskA.SquashCommitSha = "merged-sha-77";
                await closeCallback(taskA.Id, ct);
                return new[] { taskA.Id };
            });

        var orchestrator = builder.Build();
        var closedIds = await orchestrator.SyncMergedPullRequestsAsync(workspace.Id);

        Assert.Equal(new[] { taskA.Id }, closedIds);
        var closedTaskA = await taskRepo.GetByIdAsync(taskA.Id);
        Assert.NotNull(closedTaskA);
        Assert.Equal(TaskOverallStatus.Closed, closedTaskA.Status);
        Assert.Equal("merged-sha-77", closedTaskA.SquashCommitSha);

        builder.GitHubTaskMirrorMock.Verify(
            m => m.MirrorTaskClosedAsync(
                It.Is<ForgeTask>(t => t.Id == taskA.Id && t.Status == TaskOverallStatus.Closed),
                It.IsAny<Workspace>(),
                It.IsAny<CancellationToken>()),
            Times.Once);

        Assert.Contains(jobQueue.EnqueuedStages, j => j.TaskId == taskB.Id && j.Tier == StageQueueTier.TaskReview);
    }

    [Fact]
    public async Task PromoteSuggestedTaskAsync_MirrorsOpenedStateBeforeEnqueuingInitialStage()
    {
        var taskRepo = new InMemoryTaskRepository();
        var wsRepo = new InMemoryWorkspaceRepository();
        var depStore = new InMemoryTaskDependencyStore();
        var workspace = await wsRepo.CreateAsync(CreateGitHubWorkspace(autoCreateLocalIssues: true));

        var callOrder = new List<string>();
        var jobQueueMock = new Mock<ITaskJobQueue>();
        jobQueueMock
            .Setup(q => q.EnqueueStage(It.IsAny<int>(), It.IsAny<StageQueueTier>(), It.IsAny<int>(), It.IsAny<DateTime?>()))
            .Callback(() => callOrder.Add("enqueue-stage"))
            .Returns("job-1");

        var mdSyncMock = new Mock<ITaskMarkdownSyncService>();
        mdSyncMock
            .Setup(m => m.SyncTaskToDiskAsync(
                It.IsAny<ForgeTask>(),
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<int>?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("sync-markdown"))
            .ReturnsAsync("docs/tasks/task.md");

        var builder = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = wsRepo,
            DependencyStore = depStore,
            JobQueue = jobQueueMock.Object,
            MarkdownSyncService = mdSyncMock.Object
        };
        builder.GitHubTaskMirrorMock
            .Setup(m => m.MirrorTaskOpenedAsync(It.IsAny<ForgeTask>(), It.IsAny<Workspace>(), It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("mirror-opened"))
            .Returns(Task.CompletedTask);

        var suggestedTask = await taskRepo.CreateAsync(new ForgeTask
        {
            WorkspaceId = workspace.Id,
            Title = "Local task promoted",
            Status = TaskOverallStatus.Suggested,
            BaseBranch = "main"
        });
        depStore.AddTask(suggestedTask.Id, workspace.Id, TaskOverallStatus.Suggested);

        var orchestrator = builder.Build();
        await orchestrator.PromoteSuggestedTaskAsync(
            suggestedTask.Id,
            TaskOverallStatus.Open,
            TaskPriority.Normal,
            MergeMode.ManualReviewRequired);

        Assert.Equal(["mirror-opened", "sync-markdown", "enqueue-stage"], callOrder);
    }

    [Fact]
    public async Task SyncMergedPullRequestsAsync_WhenOperationsPaused_ReturnsEmptyWithoutInvokingMergeCoordinator()
    {
        var opsControlMock = new Mock<IOperationsControlService>();
        opsControlMock.Setup(o => o.IsPaused).Returns(true);
        var mergeCoordinatorMock = new Mock<ITaskMergeAndCascadeCoordinator>();

        var builder = new TestOrchestratorBuilder
        {
            OperationsControlService = opsControlMock.Object
        };
        var orchestrator = new TaskOrchestrator(
            builder.TaskRepository,
            builder.BuildWorkspacePreparer(),
            mergeCoordinatorMock.Object,
            builder.BuildLifecycleMutationCoordinator(),
            builder.HarnessRegistry,
            builder.Streamer,
            opsControlMock.Object,
            builder.TelemetryService,
            builder.Logger);

        var closedIds = await orchestrator.SyncMergedPullRequestsAsync(1);

        Assert.Empty(closedIds);
        mergeCoordinatorMock.Verify(
            m => m.SyncMergedPullRequestsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
