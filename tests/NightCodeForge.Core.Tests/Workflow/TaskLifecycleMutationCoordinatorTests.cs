using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Core.Concurrency;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Streaming;
using NightCodeForge.Core.Tests.Fixtures;
using NightCodeForge.Core.Workflow;
using Xunit;

namespace NightCodeForge.Core.Tests.Workflow;

public class TaskLifecycleMutationCoordinatorTests
{
    private readonly InMemoryTaskRepository _taskRepo = new();
    private readonly Mock<ITaskWorkspacePreparer> _preparerMock = new();
    private readonly Mock<ITaskMergeAndCascadeCoordinator> _mergeCoordinatorMock = new();
    private readonly IWorkflowPipelineProvider _pipelineProvider;
    private readonly FakeTaskJobQueue _jobQueue = new();
    private readonly Mock<ITaskCancellationManager> _cancellationManagerMock = new();
    private readonly InMemoryAgentActivityStreamer _streamer = new();
    private readonly Mock<IOperationsTelemetryService> _telemetryMock = new();
    private readonly Workspace _workspace;

    public TaskLifecycleMutationCoordinatorTests()
    {
        var stages = new IWorkflowStage[]
        {
            new TestWorkflowStage(StandardStageIds.TaskReview, WorkflowStageKind.Agentic) { QueueTier = StageQueueTier.TaskReview },
            new TestWorkflowStage(StandardStageIds.Implementation, WorkflowStageKind.Agentic) { QueueTier = StageQueueTier.Implementation },
            new TestWorkflowStage(StandardStageIds.CodeReviewAndFix, WorkflowStageKind.Agentic) { QueueTier = StageQueueTier.CodeReview, LoopBackStageId = StandardStageIds.Implementation }
        };
        _pipelineProvider = new DefaultWorkflowPipelineProvider(new WorkflowPipeline(stages));

        _workspace = new Workspace
        {
            Id = 1,
            Name = "Lifecycle WS",
            RepositoryPath = "C:/repo",
            DefaultBaseBranch = "main"
        };

        _preparerMock.Setup(p => p.AcquireTaskLockAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());
        _preparerMock.Setup(p => p.GetWorkspaceAsync(It.IsAny<ForgeTask>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_workspace);
    }

    private TaskLifecycleMutationCoordinator CreateSut() => new(
        _taskRepo,
        _preparerMock.Object,
        _mergeCoordinatorMock.Object,
        _pipelineProvider,
        _jobQueue,
        _cancellationManagerMock.Object,
        _streamer,
        _telemetryMock.Object,
        NullLogger<TaskLifecycleMutationCoordinator>.Instance);

    [Fact]
    public void Constructor_WhenAnyDependencyIsNull_ThrowsArgumentNullException()
    {
        var logger = NullLogger<TaskLifecycleMutationCoordinator>.Instance;
        Assert.Throws<ArgumentNullException>(() => new TaskLifecycleMutationCoordinator(null!, _preparerMock.Object, _mergeCoordinatorMock.Object, _pipelineProvider, _jobQueue, _cancellationManagerMock.Object, _streamer, _telemetryMock.Object, logger));
        Assert.Throws<ArgumentNullException>(() => new TaskLifecycleMutationCoordinator(_taskRepo, null!, _mergeCoordinatorMock.Object, _pipelineProvider, _jobQueue, _cancellationManagerMock.Object, _streamer, _telemetryMock.Object, logger));
        Assert.Throws<ArgumentNullException>(() => new TaskLifecycleMutationCoordinator(_taskRepo, _preparerMock.Object, null!, _pipelineProvider, _jobQueue, _cancellationManagerMock.Object, _streamer, _telemetryMock.Object, logger));
        Assert.Throws<ArgumentNullException>(() => new TaskLifecycleMutationCoordinator(_taskRepo, _preparerMock.Object, _mergeCoordinatorMock.Object, null!, _jobQueue, _cancellationManagerMock.Object, _streamer, _telemetryMock.Object, logger));
        Assert.Throws<ArgumentNullException>(() => new TaskLifecycleMutationCoordinator(_taskRepo, _preparerMock.Object, _mergeCoordinatorMock.Object, _pipelineProvider, null!, _cancellationManagerMock.Object, _streamer, _telemetryMock.Object, logger));
        Assert.Throws<ArgumentNullException>(() => new TaskLifecycleMutationCoordinator(_taskRepo, _preparerMock.Object, _mergeCoordinatorMock.Object, _pipelineProvider, _jobQueue, null!, _streamer, _telemetryMock.Object, logger));
        Assert.Throws<ArgumentNullException>(() => new TaskLifecycleMutationCoordinator(_taskRepo, _preparerMock.Object, _mergeCoordinatorMock.Object, _pipelineProvider, _jobQueue, _cancellationManagerMock.Object, null!, _telemetryMock.Object, logger));
        Assert.Throws<ArgumentNullException>(() => new TaskLifecycleMutationCoordinator(_taskRepo, _preparerMock.Object, _mergeCoordinatorMock.Object, _pipelineProvider, _jobQueue, _cancellationManagerMock.Object, _streamer, null!, logger));
        Assert.Throws<ArgumentNullException>(() => new TaskLifecycleMutationCoordinator(_taskRepo, _preparerMock.Object, _mergeCoordinatorMock.Object, _pipelineProvider, _jobQueue, _cancellationManagerMock.Object, _streamer, _telemetryMock.Object, null!));
    }

    [Fact]
    public void CancellationTrackingMethods_DelegateToTaskCancellationManager()
    {
        var sut = CreateSut();
        using var expectedCts = new CancellationTokenSource();
        _cancellationManagerMock.Setup(c => c.Register(42, It.IsAny<CancellationToken>())).Returns(expectedCts);
        _cancellationManagerMock.Setup(c => c.IsCancelRequested(42)).Returns(true);
        _cancellationManagerMock.Setup(c => c.IsPauseRequested(42)).Returns(false);

        var actualCts = sut.RegisterExecutionCancellation(42);
        Assert.Same(expectedCts, actualCts);
        Assert.True(sut.IsCancelRequested(42));
        Assert.False(sut.IsPauseRequested(42));

        sut.UnregisterExecutionCancellation(42, actualCts);
        _cancellationManagerMock.Verify(c => c.Unregister(42, actualCts), Times.Once);
    }

    [Fact]
    public void CanRestoreCheckpointMemory_ValidatesStageAndConversationRecency()
    {
        var sut = CreateSut();
        Assert.False(sut.CanRestoreCheckpointMemory(null, null, StandardStageIds.Implementation));

        var checkpoint = new StageCheckpoint
        {
            TaskId = 1,
            StageId = StandardStageIds.Implementation,
            SerializedMemory = "{\"messages\":[]}",
            CreatedAt = DateTime.UtcNow.AddMinutes(-5)
        };

        // Mismatched stageId
        Assert.False(sut.CanRestoreCheckpointMemory(null, checkpoint, StandardStageIds.TaskReview));

        // Superseded conversation completed after checkpoint was created -> cannot restore
        var supersededConv = new Conversation
        {
            Id = 1,
            TaskId = 1,
            StageId = StandardStageIds.Implementation,
            Status = StageExecutionStatus.Superseded,
            StartedAt = DateTime.UtcNow.AddMinutes(-6),
            CompletedAt = DateTime.UtcNow.AddMinutes(-1)
        };
        Assert.False(sut.CanRestoreCheckpointMemory([supersededConv], checkpoint, StandardStageIds.Implementation));

        // Interrupted conversation -> can restore
        var interruptedConv = new Conversation
        {
            Id = 2,
            TaskId = 1,
            StageId = StandardStageIds.Implementation,
            Status = StageExecutionStatus.Interrupted,
            StartedAt = DateTime.UtcNow.AddMinutes(-6),
            CompletedAt = DateTime.UtcNow.AddMinutes(-4)
        };
        Assert.True(sut.CanRestoreCheckpointMemory([interruptedConv], checkpoint, StandardStageIds.Implementation));
    }

    [Fact]
    public async Task SubmitHumanAnswerAsync_AnswersQuestionClearsWaitingFlagAndEnqueuesTask()
    {
        var sut = CreateSut();
        var task = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 10,
            WorkspaceId = 1,
            Title = "Clarification Task",
            Status = TaskOverallStatus.Open,
            IsWaitingForHuman = true,
            CurrentStageId = StandardStageIds.TaskReview,
            Priority = TaskPriority.Normal
        });
        var question = new Question
        {
            Id = 100,
            TaskId = 10,
            StageId = StandardStageIds.TaskReview,
            Prompt = "Which database?",
            OptionsJson = "[\"SQLite\",\"PostgreSQL\"]",
            Status = QuestionStatus.Pending,
            BatchId = "batch-1"
        };
        task.Questions.Add(question);
        _taskRepo.AddQuestion(question);

        await sut.SubmitHumanAnswerAsync(100, "SQLite");

        var updatedTask = await _taskRepo.GetByIdAsync(10);
        Assert.NotNull(updatedTask);
        Assert.False(updatedTask.IsWaitingForHuman);
        Assert.Contains(10, _jobQueue.TaskReviewQueue);
        _mergeCoordinatorMock.Verify(m => m.SetWaitingForHumanInStoreAsync(10, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SubmitAnswersAsync_ValidatesInputAndRejectsCrossBatchOrWrongTaskQuestions()
    {
        var sut = CreateSut();
        await Assert.ThrowsAsync<ArgumentException>(() => sut.SubmitHumanAnswerAsync(1, "   "));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => sut.SubmitHumanAnswerAsync(999, "Valid"));
        await Assert.ThrowsAsync<ArgumentException>(() => sut.SubmitAnswersAsync(1, new Dictionary<int, string>()));

        var task = await _taskRepo.CreateAsync(new ForgeTask { Id = 11, WorkspaceId = 1, Status = TaskOverallStatus.Open, IsWaitingForHuman = true });
        var q1 = new Question { Id = 101, TaskId = 11, BatchId = "b1", Status = QuestionStatus.Pending };
        var q2 = new Question { Id = 102, TaskId = 11, BatchId = "b2", Status = QuestionStatus.Pending };
        var qOtherTask = new Question { Id = 103, TaskId = 99, BatchId = "b1", Status = QuestionStatus.Pending };
        task.Questions.Add(q1);
        task.Questions.Add(q2);
        _taskRepo.AddQuestion(q1);
        _taskRepo.AddQuestion(q2);
        _taskRepo.AddQuestion(qOtherTask);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.SubmitAnswersAsync(11, new Dictionary<int, string> { [103] = "Answer" }));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            sut.SubmitAnswersAsync(11, new Dictionary<int, string> { [101] = "A1", [102] = "A2" }));
    }

    [Fact]
    public async Task RequestChangesAsync_RoutesBackToLoopBackStageAndStoresReviewerFeedback()
    {
        var sut = CreateSut();
        var task = await _taskRepo.CreateWithBaselineAsync(new ForgeTask
        {
            Id = 20,
            WorkspaceId = 1,
            Title = "Review Task",
            Status = TaskOverallStatus.Ready,
            CurrentStageId = StandardStageIds.CodeReviewAndFix,
            IsWaitingForHuman = true,
            InterruptedStageId = StandardStageIds.Implementation,
            InterruptedAgentProfileId = "profile-1",
            Priority = TaskPriority.High
        }, null, AuditActors.User, "init");

        await sut.RequestChangesAsync(20, "Add missing null check in Foo.cs");

        var updatedTask = await _taskRepo.GetByIdAsync(20);
        Assert.NotNull(updatedTask);
        Assert.Equal(TaskOverallStatus.Open, updatedTask.Status);
        Assert.Equal(StandardStageIds.Implementation, updatedTask.CurrentStageId);
        Assert.False(updatedTask.IsWaitingForHuman);
        Assert.Null(updatedTask.InterruptedStageId);
        Assert.Null(updatedTask.InterruptedAgentProfileId);

        var bag = PipelineDataBag.FromJson(updatedTask.PipelineDataJson);
        Assert.Equal("Add missing null check in Foo.cs", bag.Get<string>(PipelineDataBagKeys.ReviewerFeedback));
        Assert.Contains(20, _jobQueue.ImplementationQueue);
        _mergeCoordinatorMock.Verify(m => m.SetWaitingForHumanInStoreAsync(20, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PromoteSuggestedTaskAsync_HandlesOpenRejectedAndInvalidTargetStatus()
    {
        var sut = CreateSut();
        var task = await _taskRepo.CreateWithBaselineAsync(new ForgeTask
        {
            Id = 30,
            WorkspaceId = 1,
            Title = "Suggested Task",
            Status = TaskOverallStatus.Suggested,
            BaseBranch = "main"
        }, null, AuditActors.User, "init");

        _mergeCoordinatorMock.Setup(m => m.IsReadyForExecutionAsync(30, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await sut.PromoteSuggestedTaskAsync(
            30,
            TaskOverallStatus.Open,
            TaskPriority.Critical,
            MergeMode.Automatic,
            baseBranch: "develop");

        var updatedTask = await _taskRepo.GetByIdAsync(30);
        Assert.NotNull(updatedTask);
        Assert.Equal(TaskOverallStatus.Open, updatedTask.Status);
        Assert.Equal(TaskPriority.Critical, updatedTask.Priority);
        Assert.Equal(MergeMode.Automatic, updatedTask.MergeMode);
        Assert.Equal("develop", updatedTask.BaseBranch);
        Assert.Contains(30, _jobQueue.UrgentQueue);
        _preparerMock.Verify(p => p.SyncTaskMarkdownAndMirrorAsync(It.Is<ForgeTask>(t => t.Id == 30), _workspace, It.IsAny<CancellationToken>()), Times.Once);

        // Promoting to Rejected delegates to RejectTaskAsync
        var suggestedToReject = await _taskRepo.CreateWithBaselineAsync(new ForgeTask
        {
            Id = 31,
            WorkspaceId = 1,
            Title = "Suggested To Reject",
            Status = TaskOverallStatus.Suggested
        }, null, AuditActors.User, "init");

        await sut.PromoteSuggestedTaskAsync(31, TaskOverallStatus.Rejected, TaskPriority.Low, MergeMode.Automatic);
        Assert.Equal(TaskOverallStatus.Rejected, (await _taskRepo.GetByIdAsync(31))!.Status);

        // Invalid status throws ArgumentException
        await Assert.ThrowsAsync<ArgumentException>(() =>
            sut.PromoteSuggestedTaskAsync(30, TaskOverallStatus.Closed, TaskPriority.Normal, MergeMode.Automatic));
    }

    [Fact]
    public async Task RejectTaskAsync_CancelsExecutionDeletesJobMarksConversationsCancelledAndCleansUpResources()
    {
        var sut = CreateSut();
        var task = await _taskRepo.CreateWithBaselineAsync(new ForgeTask
        {
            Id = 40,
            WorkspaceId = 1,
            Title = "Task to Reject",
            Status = TaskOverallStatus.Open,
            IsWaitingForHuman = true,
            InterruptedStageId = StandardStageIds.TaskReview,
            InterruptedAgentProfileId = "profile-1",
            LastHangfireJobId = "job-40"
        }, null, AuditActors.User, "init");

        var runningConv = await _taskRepo.StartConversationAsync(40, StandardStageIds.Implementation, WorkflowStageKind.Agentic, 1);

        await sut.RejectTaskAsync(40, "Out of scope");

        var updatedTask = await _taskRepo.GetByIdAsync(40);
        Assert.NotNull(updatedTask);
        Assert.Equal(TaskOverallStatus.Rejected, updatedTask.Status);
        Assert.False(updatedTask.IsWaitingForHuman);
        Assert.Null(updatedTask.InterruptedStageId);
        Assert.Null(updatedTask.InterruptedAgentProfileId);
        Assert.Null(updatedTask.LastHangfireJobId);
        Assert.Contains("job-40", _jobQueue.DeletedJobs);

        var convs = await _taskRepo.GetConversationsAsync(40);
        Assert.Equal(StageExecutionStatus.Cancelled, Assert.Single(convs).Status);

        _cancellationManagerMock.Verify(c => c.Cancel(40), Times.Once);
        _mergeCoordinatorMock.Verify(m => m.SetWaitingForHumanInStoreAsync(40, false, It.IsAny<CancellationToken>()), Times.Once);
        _preparerMock.Verify(p => p.RemoveWorktreeSafeAsync(_workspace, 40, "rejected", It.IsAny<CancellationToken>()), Times.Once);
        _preparerMock.Verify(p => p.CleanupTaskContainerSafeAsync(_workspace, 40, "rejected", It.IsAny<CancellationToken>()), Times.Once);

        // Rejecting an already terminal task throws InvalidOperationException
        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.RejectTaskAsync(40, "Again"));
    }

    [Fact]
    public async Task RejectTaskAsync_MarksPendingQuestionsAsSkipped_AndBroadcastsCounts()
    {
        var sut = CreateSut();
        var task = await _taskRepo.CreateWithBaselineAsync(new ForgeTask
        {
            Id = 41,
            WorkspaceId = 1,
            Title = "Task with pending question",
            Status = TaskOverallStatus.Open,
            Questions = new List<Question>
            {
                new() { Id = 100, TaskId = 41, Prompt = "Clarify?", Status = QuestionStatus.Pending },
                new() { Id = 101, TaskId = 41, Prompt = "Already answered?", Status = QuestionStatus.Answered }
            }
        }, null, AuditActors.User, "init");

        await sut.RejectTaskAsync(41, "Not doing this");

        var updatedTask = await _taskRepo.GetByIdAsync(41);
        Assert.NotNull(updatedTask);
        Assert.Equal(TaskOverallStatus.Rejected, updatedTask.Status);
        var q100 = Assert.Single(updatedTask.Questions, q => q.Id == 100);
        var q101 = Assert.Single(updatedTask.Questions, q => q.Id == 101);
        Assert.Equal(QuestionStatus.Skipped, q100.Status);
        Assert.Equal(QuestionStatus.Answered, q101.Status);

        _telemetryMock.Verify(t => t.RecordPendingCounts(It.IsAny<int>(), It.IsAny<int>()), Times.Once);
    }

    [Fact]
    public async Task RestartTaskPipelineAsync_HardReset_ResetsWorkspaceSupersedesConversationsAndReenqueuesInitialStage()
    {
        var sut = CreateSut();
        var task = await _taskRepo.CreateWithBaselineAsync(new ForgeTask
        {
            Id = 50,
            WorkspaceId = 1,
            Title = "Task to Restart",
            Status = TaskOverallStatus.Failed,
            CurrentStageId = StandardStageIds.Implementation,
            HasExecutionRun = true,
            PipelineDataJson = "{\"ApprovedPlan\":\"old\"}",
            BaseBranch = "main"
        }, null, AuditActors.User, "init");

        var conv = await _taskRepo.StartConversationAsync(50, StandardStageIds.Implementation, WorkflowStageKind.Agentic, 1);

        var result = await sut.RestartTaskPipelineAsync(50, TaskRestartGitMode.HardResetToBaseBranch);

        Assert.True(result);
        _preparerMock.Verify(p => p.ResetTaskWorkspaceToBaseAsync(_workspace, 50, "main", It.IsAny<CancellationToken>()), Times.Once);

        var updatedTask = await _taskRepo.GetByIdAsync(50);
        Assert.NotNull(updatedTask);
        Assert.Equal(TaskOverallStatus.Open, updatedTask.Status);
        Assert.Equal(StandardStageIds.TaskReview, updatedTask.CurrentStageId);
        Assert.Equal("{}", updatedTask.PipelineDataJson);
        Assert.False(updatedTask.HasExecutionRun);

        var convs = await _taskRepo.GetConversationsAsync(50);
        Assert.Equal(StageExecutionStatus.Superseded, Assert.Single(convs).Status);
    }
}
