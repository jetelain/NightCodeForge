using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Core.Concurrency;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Streaming;
using NightCodeForge.Core.Tests.Fixtures;
using NightCodeForge.Core.Workflow;
using System.Text.Json;
using Xunit;

namespace NightCodeForge.Core.Tests;

public class TaskOrchestratorConversationResumptionTests
{
    [Fact]
    public async Task SubmitAnswersAsync_WithMultipleBatchQuestions_MarksIsWaitingForHumanFalse_OnlyWhenWholeBatchIsAnswered()
    {
        var repo = new InMemoryTaskRepository();
        var jobQueue = new FakeTaskJobQueue();
        var builder = new TestOrchestratorBuilder
        {
            TaskRepository = repo,
            JobQueue = jobQueue
        };
        var orchestrator = builder.Build();

        var ws = new Workspace { Id = 1, Name = "Test WS", RepositoryPath = "C:\\test" };
        await builder.WorkspaceRepository.CreateAsync(ws);

        var task = new ForgeTask
        {
            Id = 50,
            WorkspaceId = 1,
            Title = "Task with batch questions",
            Status = TaskOverallStatus.Open,
            IsWaitingForHuman = true,
            CurrentStageId = "task-review"
        };
        await repo.CreateAsync(task);

        var batchId = "batch-xyz";
        var q1 = new Question
        {
            Id = 101,
            TaskId = task.Id,
            StageId = "task-review",
            Prompt = "Question 1",
            Status = QuestionStatus.Pending,
            BatchId = batchId
        };
        var q2 = new Question
        {
            Id = 102,
            TaskId = task.Id,
            StageId = "task-review",
            Prompt = "Question 2",
            Status = QuestionStatus.Pending,
            BatchId = batchId
        };
        task.Questions.Add(q1);
        task.Questions.Add(q2);
        repo.AddQuestion(q1);
        repo.AddQuestion(q2);
        await repo.UpdateAsync(task);

        // Step 1: Submit answer for Q1 only (partial submission)
        await orchestrator.SubmitAnswersAsync(task.Id, new Dictionary<int, string> { [q1.Id] = "Answer 1" });

        var reloadedTask = await repo.GetByIdAsync(task.Id);
        Assert.NotNull(reloadedTask);
        Assert.True(reloadedTask.IsWaitingForHuman, "Task must still be waiting for human after partial answer");
        Assert.Empty(jobQueue.UrgentQueue);

        // Step 2: Submit answer for Q2 (completes batch)
        await orchestrator.SubmitAnswersAsync(task.Id, new Dictionary<int, string> { [q2.Id] = "Answer 2" });

        reloadedTask = await repo.GetByIdAsync(task.Id);
        Assert.NotNull(reloadedTask);
        Assert.False(reloadedTask.IsWaitingForHuman, "Task must not be waiting for human after entire batch is answered");
        Assert.Single(jobQueue.TaskReviewQueue);
        Assert.Equal(task.Id, jobQueue.TaskReviewQueue[0]);
    }

    [Fact]
    public async Task CancelTask_WhenWaitingForHumanConversationExists_ClosesItAsCancelled()
    {
        var repo = new InMemoryTaskRepository();
        var cancellationManager = new TaskCancellationManager(NullLogger<TaskCancellationManager>.Instance);
        var depStore = new InMemoryTaskDependencyStore();
        var pipelineProvider = new Mock<IWorkflowPipelineProvider>();
        var stageMock = new Mock<IWorkflowStage>();
        stageMock.Setup(s => s.StageId).Returns("task-review");
        stageMock.Setup(s => s.Kind).Returns(WorkflowStageKind.Agentic);
        stageMock.Setup(s => s.ExecuteAsync(It.IsAny<PipelineContext>(), It.IsAny<CancellationToken>()))
            .Callback<PipelineContext, CancellationToken>((ctx, ct) =>
            {
                cancellationManager.Cancel(51);
                ct.ThrowIfCancellationRequested();
            })
            .ThrowsAsync(new OperationCanceledException());

        var pipeline = new WorkflowPipeline(new[] { stageMock.Object });
        pipelineProvider.Setup(p => p.GetPipelineAsync(It.IsAny<ForgeTask>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(pipeline);

        var builder = new TestOrchestratorBuilder
        {
            TaskRepository = repo,
            CancellationManager = cancellationManager,
            DependencyStore = depStore,
            PipelineProvider = pipelineProvider.Object
        };
        var orchestrator = builder.Build();

        var ws = new Workspace { Id = 1, Name = "Test WS", RepositoryPath = "C:\\test" };
        ws.AgentProfiles.Add(new AgentProfile { Id = "task-review", WorkspaceId = 1, Name = "Task Reviewer" });
        await builder.WorkspaceRepository.CreateAsync(ws);

        var task = new ForgeTask
        {
            Id = 51,
            WorkspaceId = 1,
            Title = "Task to cancel",
            Status = TaskOverallStatus.Open,
            IsWaitingForHuman = true,
            CurrentStageId = "task-review"
        };
        await repo.CreateAsync(task);
        depStore.AddTask(task.Id, 1, TaskOverallStatus.Open);

        var conv = await repo.StartConversationAsync(task.Id, "task-review", WorkflowStageKind.Agentic, 1);
        await repo.CompleteConversationAsync(conv.Id, StageExecutionStatus.WaitingForHuman, summary: "Waiting for human input");

        // Act: Process task triggers cancellation
        await orchestrator.ProcessTaskAsync(task.Id, CancellationToken.None);

        var convs = await repo.GetConversationsAsync(task.Id);
        var updatedConv = Assert.Single(convs);
        Assert.Equal(StageExecutionStatus.Cancelled, updatedConv.Status);

        var reloaded = await repo.GetByIdAsync(task.Id);
        Assert.NotNull(reloaded);
        Assert.False(reloaded.IsWaitingForHuman);
    }

    [Fact]
    public async Task StartupRecoveryService_WhenWaitingForHumanConversationLeftDangling_ClosesItAsFailed()
    {
        var taskRepo = new InMemoryTaskRepository();
        var wsRepo = new InMemoryWorkspaceRepository();
        var jobQueue = new FakeTaskJobQueue();
        var gitRunner = new Mock<IGitCliRunner>();
        var procRunner = new Mock<IProcessRunner>();
        var pathResolver = new Mock<IWorkspacePathResolver>();

        var ws = new Workspace { Id = 1, Name = "Recovery WS", RepositoryPath = "C:\\workspace" };
        await wsRepo.CreateAsync(ws);

        var task = new ForgeTask
        {
            Id = 52,
            WorkspaceId = 1,
            Title = "Task interrupted during recovery",
            Status = TaskOverallStatus.Open,
            IsWaitingForHuman = false, // Crashed host restarted, task was marked Open
            CurrentStageId = "task-review"
        };
        await taskRepo.CreateAsync(task);

        var conv = await taskRepo.StartConversationAsync(task.Id, "task-review", WorkflowStageKind.Agentic, 1);
        await taskRepo.AddConversationTurnAsync(conv.Id, StageTurnRole.User, content: "Initial prompt");
        await taskRepo.CompleteConversationAsync(conv.Id, StageExecutionStatus.WaitingForHuman, summary: "Waiting for human");

        var mockOps = new Mock<IOperationsControlService>();
        mockOps.Setup(m => m.IsPaused).Returns(false);

        var gitRecovery = new WorkspaceGitRecoveryService(
            gitRunner.Object,
            new Mock<IGitWorkspaceManager>().Object,
            pathResolver.Object,
            procRunner.Object,
            taskRepo,
            NullLogger<WorkspaceGitRecoveryService>.Instance);

        var recoveryService = new StartupRecoveryService(
            wsRepo,
            taskRepo,
            gitRecovery,
            new DefaultWorkflowPipelineProvider(),
            jobQueue,
            new NightCodeForge.Core.Execution.SingleExecutionEnvironmentProvider(new Mock<IExecutionEnvironment>().Object),
            mockOps.Object,
            NullLogger<StartupRecoveryService>.Instance);

        await recoveryService.ReconcileWorkspacesAsync();

        var convs = await taskRepo.GetConversationsAsync(task.Id);
        var updatedConv = Assert.Single(convs);
        Assert.Equal(StageExecutionStatus.Interrupted, updatedConv.Status);
        Assert.Contains("Interrupted by host restart; session will resume in this conversation", updatedConv.Summary);
    }

    [Fact]
    public async Task ProcessTaskAsync_WhenCancelledViaCancellationToken_ClosesWaitingForHumanConversation()
    {
        var repo = new InMemoryTaskRepository();
        var cancellationManager = new TaskCancellationManager(NullLogger<TaskCancellationManager>.Instance);
        var depStore = new InMemoryTaskDependencyStore();
        var pipelineProvider = new Mock<IWorkflowPipelineProvider>();
        var stageMock = new Mock<IWorkflowStage>();
        stageMock.Setup(s => s.StageId).Returns("task-review");
        stageMock.Setup(s => s.Kind).Returns(WorkflowStageKind.Agentic);
        stageMock.Setup(s => s.ExecuteAsync(It.IsAny<PipelineContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var pipeline = new WorkflowPipeline(new[] { stageMock.Object });
        pipelineProvider.Setup(p => p.GetPipelineAsync(It.IsAny<ForgeTask>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(pipeline);

        var builder = new TestOrchestratorBuilder
        {
            TaskRepository = repo,
            CancellationManager = cancellationManager,
            DependencyStore = depStore,
            PipelineProvider = pipelineProvider.Object
        };
        var orchestrator = builder.Build();

        var ws = new Workspace { Id = 1, Name = "Test WS", RepositoryPath = "C:\\test" };
        ws.AgentProfiles.Add(new AgentProfile { Id = "task-review", WorkspaceId = 1, Name = "Task Reviewer" });
        await builder.WorkspaceRepository.CreateAsync(ws);

        var task = new ForgeTask
        {
            Id = 60,
            WorkspaceId = 1,
            Title = "Task to cancel directly",
            Status = TaskOverallStatus.Open,
            IsWaitingForHuman = true,
            CurrentStageId = "task-review"
        };
        await repo.CreateAsync(task);
        depStore.AddTask(task.Id, 1, TaskOverallStatus.Open);

        var conv = await repo.StartConversationAsync(task.Id, "task-review", WorkflowStageKind.Agentic, 1);
        await repo.CompleteConversationAsync(conv.Id, StageExecutionStatus.WaitingForHuman, summary: "Waiting for human");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await orchestrator.ProcessTaskAsync(task.Id, cts.Token);

        var convs = await repo.GetConversationsAsync(task.Id);
        var updatedConv = Assert.Single(convs);
        Assert.Equal(StageExecutionStatus.Cancelled, updatedConv.Status);

        var reloaded = await repo.GetByIdAsync(task.Id);
        Assert.NotNull(reloaded);
        Assert.True(reloaded.IsWaitingForHuman, "Parent cancellation/shutdown must keep task recoverable with IsWaitingForHuman = true");
    }

    [Fact]
    public async Task SubmitAnswersAsync_WhenQuestionAlreadyAnswered_ThrowsInvalidOperationException()
    {
        var repo = new InMemoryTaskRepository();
        var builder = new TestOrchestratorBuilder { TaskRepository = repo };
        var orchestrator = builder.Build();

        var ws = new Workspace { Id = 1, Name = "WS", RepositoryPath = "C:\\test" };
        await builder.WorkspaceRepository.CreateAsync(ws);

        var task = new ForgeTask { Id = 61, WorkspaceId = 1, Title = "Task", Status = TaskOverallStatus.Open, IsWaitingForHuman = true };
        var q = new Question { Id = 301, TaskId = task.Id, Status = QuestionStatus.Answered, Answer = "Already answered", BatchId = "b1" };
        task.Questions.Add(q);
        repo.AddQuestion(q);
        await repo.CreateAsync(task);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            orchestrator.SubmitAnswersAsync(task.Id, new Dictionary<int, string> { [301] = "New Answer" }));
    }

    [Fact]
    public async Task SubmitAnswersAsync_WhenAnswerIsBlank_ThrowsArgumentException()
    {
        var repo = new InMemoryTaskRepository();
        var builder = new TestOrchestratorBuilder { TaskRepository = repo };
        var orchestrator = builder.Build();

        var ws = new Workspace { Id = 1, Name = "WS", RepositoryPath = "C:\\test" };
        await builder.WorkspaceRepository.CreateAsync(ws);

        var task = new ForgeTask { Id = 62, WorkspaceId = 1, Title = "Task", Status = TaskOverallStatus.Open, IsWaitingForHuman = true };
        var q = new Question { Id = 302, TaskId = task.Id, Status = QuestionStatus.Pending, BatchId = "b2" };
        task.Questions.Add(q);
        repo.AddQuestion(q);
        await repo.CreateAsync(task);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            orchestrator.SubmitAnswersAsync(task.Id, new Dictionary<int, string> { [302] = "   " }));
    }

    [Fact]
    public async Task SubmitAnswersAsync_WhenBatchCompleted_PopulatesStructuredAnswersInPipelineDataBag()
    {
        var repo = new InMemoryTaskRepository();
        var builder = new TestOrchestratorBuilder { TaskRepository = repo };
        var orchestrator = builder.Build();

        var ws = new Workspace { Id = 1, Name = "WS", RepositoryPath = "C:\\test" };
        await builder.WorkspaceRepository.CreateAsync(ws);

        var task = new ForgeTask { Id = 63, WorkspaceId = 1, Title = "Task", Status = TaskOverallStatus.Open, IsWaitingForHuman = true };
        var q1 = new Question
        {
            Id = 303,
            TaskId = task.Id,
            Prompt = "Choose environment",
            OptionsJson = "[\"Staging\",\"Production\"]",
            Status = QuestionStatus.Pending,
            BatchId = "b3"
        };
        var q2 = new Question
        {
            Id = 304,
            TaskId = task.Id,
            Prompt = "Describe custom config",
            Status = QuestionStatus.Pending,
            BatchId = "b3"
        };
        task.Questions.Add(q1);
        task.Questions.Add(q2);
        repo.AddQuestion(q1);
        repo.AddQuestion(q2);
        await repo.CreateAsync(task);

        await orchestrator.SubmitAnswersAsync(task.Id, new Dictionary<int, string>
        {
            [303] = "Production",
            [304] = "custom-secret-key"
        });

        var updatedTask = await repo.GetByIdAsync(task.Id);
        Assert.NotNull(updatedTask);
        Assert.False(updatedTask.IsWaitingForHuman);

        var bag = PipelineDataBag.FromJson(updatedTask.PipelineDataJson);
        var lastAnswerText = bag.Get<string>(PipelineDataBagKeys.LastHumanAnswer);
        var lastAnswerJson = bag.Get<string>(PipelineDataBagKeys.LastHumanAnswerJson);

        Assert.NotNull(lastAnswerText);
        Assert.Contains("1. Question: Choose environment", lastAnswerText);
        Assert.Contains("Answer: Production", lastAnswerText);

        Assert.NotNull(lastAnswerJson);
        var entries = JsonSerializer.Deserialize<List<QuestionAnswerEntry>>(lastAnswerJson);
        Assert.NotNull(entries);
        Assert.Equal(2, entries.Count);

        // First question matched "Production" (index 1 of ["Staging", "Production"])
        Assert.Equal(0, entries[0].QuestionIndex);
        Assert.Equal(303, entries[0].QuestionId);
        Assert.Equal(1, entries[0].SelectedOptionIndex);
        Assert.Equal("Production", entries[0].SelectedOption);
        Assert.Equal("Production", entries[0].AnswerText);

        // Second question has no options -> free text
        Assert.Equal(1, entries[1].QuestionIndex);
        Assert.Equal(304, entries[1].QuestionId);
        Assert.Null(entries[1].SelectedOptionIndex);
        Assert.Null(entries[1].SelectedOption);
        Assert.Equal("custom-secret-key", entries[1].AnswerText);
    }

    [Fact]
    public async Task SubmitAnswersAsync_ImmediatelyPersistsToolResultTurnInWaitingConversationAndBroadcastsToolCompleted()
    {
        var repo = new InMemoryTaskRepository();
        var jobQueue = new FakeTaskJobQueue();
        var streamer = new InMemoryAgentActivityStreamer();
        var builder = new TestOrchestratorBuilder
        {
            TaskRepository = repo,
            JobQueue = jobQueue,
            Streamer = streamer
        };
        var orchestrator = builder.Build();

        var ws = new Workspace { Id = 1, Name = "Test WS", RepositoryPath = "C:\\test" };
        await builder.WorkspaceRepository.CreateAsync(ws);

        var task = new ForgeTask
        {
            Id = 60,
            WorkspaceId = 1,
            Title = "Task with waiting conversation",
            Status = TaskOverallStatus.Open,
            IsWaitingForHuman = true,
            CurrentStageId = "task-review"
        };
        await repo.CreateAsync(task);

        var q = new Question
        {
            Id = 601,
            TaskId = task.Id,
            StageId = "task-review",
            Prompt = "Choose architecture:",
            OptionsJson = "[\"Monolith\",\"Microservices\"]",
            Status = QuestionStatus.Pending
        };
        task.Questions.Add(q);
        repo.AddQuestion(q);

        var conv = await repo.StartConversationAsync(
            task.Id,
            "task-review",
            WorkflowStageKind.Agentic,
            1,
            "agent-profile-1",
            "crucible",
            "deepseek-r1");
        conv.Status = StageExecutionStatus.WaitingForHuman;

        await repo.AddConversationTurnAsync(
            conv.Id,
            StageTurnRole.Tool,
            toolName: CanonicalToolNames.Ask,
            toolArguments: "{\"questions\":[{\"prompt\":\"Choose architecture:\",\"options\":[\"Monolith\",\"Microservices\"]}]}",
            metadataJson: "{\"CallId\":\"call-arch-1\"}");

        // Subscribe to activity streamer before submission to catch broadcasts
        var capturedEvents = new List<AgentActivityEvent>();
        using var cts = new CancellationTokenSource();
        var eventReceivedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var streamTask = Task.Run(async () =>
        {
            await foreach (var evt in streamer.SubscribeAsync(cancellationToken: cts.Token))
            {
                capturedEvents.Add(evt);
                if (evt.Kind == AgentActivityKind.ToolCompleted)
                {
                    eventReceivedTcs.TrySetResult();
                }
            }
        });

        // Ensure background stream subscription is registered
        while (streamer.ActiveSubscriptionsCount == 0)
        {
            await Task.Yield();
        }

        // Submit answer from Question Inbox
        await orchestrator.SubmitAnswersAsync(task.Id, new Dictionary<int, string>
        {
            [601] = "Monolith"
        });

        // Await event broadcast
        await Task.WhenAny(eventReceivedTcs.Task, Task.Delay(2000));

        // 1. ToolResult turn must be persisted in database immediately (no need to wait for worker)
        var updatedConv = await repo.GetConversationWithTurnsAsync(conv.Id);
        Assert.NotNull(updatedConv);
        var toolResultTurn = updatedConv.Turns.FirstOrDefault(t => t.Role == StageTurnRole.ToolResult);
        Assert.NotNull(toolResultTurn);
        Assert.Equal(CanonicalToolNames.Ask, toolResultTurn.ToolName);
        Assert.Contains("Monolith", toolResultTurn.ToolResult);
        Assert.NotNull(toolResultTurn.MetadataJson);
        Assert.Contains("call-arch-1", toolResultTurn.MetadataJson);
        Assert.Contains("Monolith", toolResultTurn.MetadataJson);

        // 2. Broadcast must have been dispatched for ToolCompleted
        cts.Cancel();
        try { await streamTask; } catch (OperationCanceledException) { }

        Assert.Contains(capturedEvents, e =>
            e.Kind == AgentActivityKind.ToolCompleted &&
            e.TaskId == task.Id &&
            e.Metadata?.GetValueOrDefault(AgentActivityMetadataKeys.ToolName)?.ToString() == CanonicalToolNames.Ask);
    }

    [Fact]
    public async Task ProcessTaskAsync_WhenResumedStageSyncsDiskEditsAndThenFails_PreservesPreResumeSyncedTaskMetadataInPromptAndDatabase()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf_pre_resume_sync_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var repo = new InMemoryTaskRepository();
            var wsRepo = new InMemoryWorkspaceRepository();
            var depStore = new InMemoryTaskDependencyStore();
            var depEngine = new DependencyResolutionEngine(depStore);

            string? capturedBootstrapPrompt = null;
            string? capturedAgentTaskTitle = null;
            string? capturedAgentTaskDescription = null;

            var harnessMock = new Mock<IAgentHarness>();
            harnessMock.SetupGet(h => h.HarnessId).Returns(StandardHarnessIds.NativeCli);
            harnessMock
                .Setup(h => h.RunStageAsync(It.IsAny<AgentSessionContext>(), It.IsAny<CancellationToken>()))
                .Callback<AgentSessionContext, CancellationToken>((ctx, _) =>
                {
                    capturedBootstrapPrompt = ctx.BootstrapPrompt;
                    capturedAgentTaskTitle = ctx.ForgeTask.Title;
                    capturedAgentTaskDescription = ctx.ForgeTask.Description;
                })
                .ReturnsAsync(new AgentRunResult(AgentRunStatus.Failed, new AgentRunMetrics(10), Error: "Simulated agent failure after resume"));

            var registry = new HarnessRegistry(new[] { harnessMock.Object });

            var mdSyncMock = new Mock<ITaskMarkdownSyncService>();
            mdSyncMock
                .Setup(m => m.SyncAllTasksFromDiskAsync(
                    It.IsAny<Workspace>(),
                    It.IsAny<string>(),
                    It.IsAny<string?>(),
                    null,
                    It.IsAny<string>(),
                    TaskSyncConstants.PreserveEditsBeforeResumeReason,
                    It.IsAny<int?>(),
                    It.IsAny<CancellationToken>()))
                .Returns<Workspace, string, string?, IReadOnlyDictionary<int, TaskBaselineEntry>?, string, string?, int?, CancellationToken>(
                    async (_, _, _, _, _, _, taskId, ct) =>
                    {
                        var dbTask = await repo.GetByIdAsync(taskId!.Value, ct);
                        dbTask!.Title = "Disk-Edited Title Before Pause";
                        dbTask.Description = "Disk-Edited Specification Body Before Pause";
                        dbTask.Version += 1;
                        await repo.UpdateAsync(dbTask, cancellationToken: ct);
                        return new[] { dbTask };
                    });

            mdSyncMock
                .Setup(m => m.SyncAllTasksToDiskAsync(
                    It.IsAny<Workspace>(),
                    It.IsAny<string>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SyncAllToDiskResult(new Dictionary<int, string>(), new Dictionary<int, TaskBaselineEntry>()));

            var stage = new TestWorkflowStage(StandardStageIds.TaskReview, WorkflowStageKind.Agentic)
            {
                QueueTier = StageQueueTier.TaskReview
            };
            var pipelineProvider = new DefaultWorkflowPipelineProvider(new WorkflowPipeline(new[] { stage }));

            var builder = new TestOrchestratorBuilder
            {
                TaskRepository = repo,
                WorkspaceRepository = wsRepo,
                DependencyStore = depStore,
                DependencyEngine = depEngine,
                HarnessRegistry = registry,
                MarkdownSyncService = mdSyncMock.Object,
                PipelineProvider = pipelineProvider
            };

            var ws = new Workspace
            {
                Id = 1,
                Name = "PreResumeSyncWS",
                RepositoryPath = tempDir,
                AgentProfiles = new List<AgentProfile>
                {
                    new()
                    {
                        Id = StandardStageIds.TaskReview,
                        WorkspaceId = 1,
                        Name = "Reviewer",
                        HarnessId = StandardHarnessIds.NativeCli,
                        ModelName = "test-model",
                        Mode = AgentMode.Full
                    }
                }
            };
            await wsRepo.CreateAsync(ws);

            var bag = new PipelineDataBag();
            bag.Set(PipelineDataBagKeys.MaxRetries, 1);

            var task = new ForgeTask
            {
                Id = 77,
                WorkspaceId = 1,
                Title = "Stale Original Title",
                Description = "Stale Original Description",
                Status = TaskOverallStatus.Open,
                CurrentStageId = StandardStageIds.TaskReview,
                InterruptedStageId = StandardStageIds.TaskReview,
                PipelineDataJson = bag.ToJson(),
                Version = 1
            };
            await repo.CreateAsync(task);
            depStore.AddTask(task.Id, ws.Id, TaskOverallStatus.Open);

            var orchestrator = builder.Build();
            await orchestrator.ProcessTaskAsync(task.Id, CancellationToken.None);

            // 1. Agent session and bootstrap prompt must receive the disk-synced metadata, not stale pre-resume metadata
            Assert.Equal("Disk-Edited Title Before Pause", capturedAgentTaskTitle);
            Assert.Equal("Disk-Edited Specification Body Before Pause", capturedAgentTaskDescription);
            Assert.Contains("Disk-Edited Title Before Pause", capturedBootstrapPrompt);
            Assert.Contains("Disk-Edited Specification Body Before Pause", capturedBootstrapPrompt);

            // 2. When stage failure persists the task to the repository, it must NOT overwrite DB with stale pre-resume Title/Description
            var persistedTask = await repo.GetByIdAsync(task.Id);
            Assert.NotNull(persistedTask);
            Assert.Equal(TaskOverallStatus.Failed, persistedTask.Status);
            Assert.Equal("Disk-Edited Title Before Pause", persistedTask.Title);
            Assert.Equal("Disk-Edited Specification Body Before Pause", persistedTask.Description);
            Assert.True(persistedTask.Version >= 2);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ProcessTaskAsync_WhenResumedStageHasNonExistentSyncPath_ClearsIsResumedRunBeforeSubsequentCohortStage()
    {
        var repo = new InMemoryTaskRepository();
        var wsRepo = new InMemoryWorkspaceRepository();
        var depStore = new InMemoryTaskDependencyStore();
        var depEngine = new DependencyResolutionEngine(depStore);

        var preparerMock = new Mock<ITaskWorkspacePreparer>();
        var isResumedRunValuesByStage = new List<(string StageId, bool IsResumedRun)>();

        var ws = new Workspace
        {
            Id = 1,
            Name = "CohortResumptionWS",
            RepositoryPath = "C:/non_existent_path_" + Guid.NewGuid().ToString("N")
        };
        await wsRepo.CreateAsync(ws);

        preparerMock.Setup(p => p.GetWorkspaceAsync(It.IsAny<ForgeTask>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ws);
        preparerMock.Setup(p => p.AcquireTaskLockAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());
        preparerMock.Setup(p => p.ResolveWorkspacePaths(It.IsAny<Workspace>(), It.IsAny<int>()))
            .Returns(new ResolvedTaskWorkspacePaths(ws.RepositoryPath, ws.RepositoryPath, ws.RepositoryPath, ws.RepositoryPath));
        preparerMock.Setup(p => p.SyncFromBaseBranchAsync(It.IsAny<Workspace>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MergeSyncResult(true, false, [], "merge-sha"));
        preparerMock.Setup(p => p.HasImplementationChangesAsync(It.IsAny<Workspace>(), It.IsAny<ForgeTask>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        preparerMock.Setup(p => p.SyncPreStageMarkdownAsync(
                It.IsAny<Workspace>(),
                It.IsAny<ForgeTask>(),
                It.IsAny<PipelineContext>(),
                It.IsAny<IWorkflowStage>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .Callback<Workspace, ForgeTask, PipelineContext, IWorkflowStage, string?, string?, bool, CancellationToken>(
                (_, _, _, stg, _, _, isResumed, _) => isResumedRunValuesByStage.Add((stg.StageId, isResumed)))
            .ReturnsAsync(new PreStageMarkdownSyncOutcome(null, false));

        var stage1 = new TestWorkflowStage(StandardStageIds.PreImplementationBaseline, WorkflowStageKind.Deterministic)
        {
            QueueTier = StageQueueTier.Implementation
        };
        var stage2 = new TestWorkflowStage(StandardStageIds.PostImplementationGates, WorkflowStageKind.Deterministic)
        {
            QueueTier = StageQueueTier.Implementation
        };
        var pipelineProvider = new DefaultWorkflowPipelineProvider(new WorkflowPipeline(new IWorkflowStage[] { stage1, stage2 }));

        var builder = new TestOrchestratorBuilder
        {
            TaskRepository = repo,
            WorkspaceRepository = wsRepo,
            DependencyStore = depStore,
            DependencyEngine = depEngine,
            PipelineProvider = pipelineProvider
        };

        var mergeCoordinator = builder.BuildMergeAndCascadeCoordinator(preparerMock.Object);
        var lifecycleCoordinator = builder.BuildLifecycleMutationCoordinator(preparerMock.Object, mergeCoordinator);
        var orchestrator = new TaskOrchestrator(
            repo,
            preparerMock.Object,
            mergeCoordinator,
            lifecycleCoordinator,
            builder.HarnessRegistry,
            builder.Streamer,
            builder.OperationsControlService,
            builder.TelemetryService,
            builder.Logger);

        var task = new ForgeTask
        {
            Id = 88,
            WorkspaceId = 1,
            Title = "Resumed Stage Cohort Task",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.PreImplementationBaseline,
            InterruptedStageId = StandardStageIds.PreImplementationBaseline
        };
        await repo.CreateAsync(task);
        depStore.AddTask(task.Id, ws.Id, TaskOverallStatus.Open);

        await orchestrator.ProcessTaskAsync(task.Id, CancellationToken.None);

        Assert.Equal(2, isResumedRunValuesByStage.Count);
        Assert.Equal((StandardStageIds.PreImplementationBaseline, true), isResumedRunValuesByStage[0]);
        Assert.Equal((StandardStageIds.PostImplementationGates, false), isResumedRunValuesByStage[1]);
    }
}
