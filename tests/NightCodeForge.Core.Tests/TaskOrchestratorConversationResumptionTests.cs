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
        Assert.Single(jobQueue.UrgentQueue);
        Assert.Equal(task.Id, jobQueue.UrgentQueue[0]);
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

        var recoveryService = new StartupRecoveryService(
            wsRepo,
            taskRepo,
            gitRunner.Object,
            new Mock<IGitWorkspaceManager>().Object,
            new DefaultWorkflowPipelineProvider(),
            jobQueue,
            pathResolver.Object,
            procRunner.Object,
            NullLogger<StartupRecoveryService>.Instance);

        await recoveryService.ReconcileWorkspacesAsync();

        var convs = await taskRepo.GetConversationsAsync(task.Id);
        var updatedConv = Assert.Single(convs);
        Assert.Equal(StageExecutionStatus.Failed, updatedConv.Status);
        Assert.Contains("Process terminated unexpectedly mid-execution", updatedConv.Summary);
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
}
