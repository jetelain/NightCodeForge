using Moq;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Tests.Fixtures;
using NightCodeForge.Core.Workflow;
using Xunit;

namespace NightCodeForge.Core.Tests;

public class TaskOrchestratorHumanAnswerHygieneTests
{
    [Fact]
    public async Task SubmitHumanAnswerAsync_SetsAnswerKeysInPipelineDataBag()
    {
        var repo = new InMemoryTaskRepository();
        var builder = new TestOrchestratorBuilder
        {
            TaskRepository = repo
        };
        var orchestrator = builder.Build();

        var ws = new Workspace { Id = 1, Name = "Test WS", RepositoryPath = "C:\\test" };
        await builder.WorkspaceRepository.CreateAsync(ws);

        var task = new ForgeTask
        {
            Id = 100,
            WorkspaceId = 1,
            Title = "Task for human answers",
            Status = TaskOverallStatus.Open,
            IsWaitingForHuman = true,
            CurrentStageId = StandardStageIds.TaskReview,
            InterruptedStageId = StandardStageIds.TaskReview
        };
        await repo.CreateAsync(task);

        var question = new Question
        {
            Id = 501,
            TaskId = task.Id,
            StageId = StandardStageIds.TaskReview,
            Prompt = "Which database?",
            Status = QuestionStatus.Pending,
            OptionsJson = "[\"PostgreSQL\",\"SQLite\"]",
            BatchId = "batch-1"
        };
        task.Questions.Add(question);
        repo.AddQuestion(question);
        await repo.UpdateAsync(task);

        // Submit answer
        await orchestrator.SubmitAnswersAsync(task.Id, new Dictionary<int, string> { [question.Id] = "PostgreSQL" });

        var reloaded = await repo.GetByIdAsync(task.Id);
        Assert.NotNull(reloaded);
        var bag = PipelineDataBag.FromJson(reloaded.PipelineDataJson);

        var lastAnswer = bag.Get<string>(PipelineDataBagKeys.LastHumanAnswer);
        var lastAnswerJson = bag.Get<string>(PipelineDataBagKeys.LastHumanAnswerJson);

        Assert.False(string.IsNullOrWhiteSpace(lastAnswer));
        Assert.Contains("PostgreSQL", lastAnswer);
        Assert.False(string.IsNullOrWhiteSpace(lastAnswerJson));
        Assert.Contains("PostgreSQL", lastAnswerJson);
    }

    [Fact]
    public async Task ResumeExecution_PassesPendingHumanAnswerOnlyToInterruptedStage_AndConsumesFromDataBag()
    {
        var repo = new InMemoryTaskRepository();
        var harnessMock = new Mock<IAgentHarness>();
        harnessMock.Setup(h => h.HarnessId).Returns(StandardHarnessIds.Crucible);

        var builder = new TestOrchestratorBuilder
        {
            TaskRepository = repo,
            PipelineProvider = new DefaultWorkflowPipelineProvider(new WorkflowPipeline(new[]
            {
                new TestWorkflowStage(StandardStageIds.Implementation, WorkflowStageKind.Agentic)
                {
                    QueueTier = StageQueueTier.Implementation
                }
            }))
        };
        builder.HarnessRegistry.RegisterHarness(harnessMock.Object);

        var orchestrator = builder.Build();

        var ws = new Workspace
        {
            Id = 1,
            Name = "Test WS",
            RepositoryPath = "C:\\test",
            AgentProfiles = new List<AgentProfile>
            {
                new()
                {
                    Id = StandardStageIds.Implementation,
                    HarnessId = StandardHarnessIds.Crucible,
                    Mode = AgentMode.Full
                }
            }
        };
        await builder.WorkspaceRepository.CreateAsync(ws);

        var initialBag = new PipelineDataBag();
        initialBag.Set(PipelineDataBagKeys.LastHumanAnswer, "Q: DB?\nA: PostgreSQL");
        initialBag.Set(PipelineDataBagKeys.LastHumanAnswerJson, "[{\"QuestionIndex\":0,\"AnswerText\":\"PostgreSQL\"}]");

        var task = new ForgeTask
        {
            Id = 101,
            WorkspaceId = 1,
            Title = "Task resuming",
            Status = TaskOverallStatus.Open,
            IsWaitingForHuman = false,
            CurrentStageId = StandardStageIds.Implementation,
            InterruptedStageId = StandardStageIds.Implementation,
            PipelineDataJson = initialBag.ToJson()
        };
        await repo.CreateAsync(task);
        ((InMemoryTaskDependencyStore)builder.DependencyStore).AddTask(task.Id, 1, task.Status, false);

        AgentSessionContext? capturedContext = null;
        harnessMock.Setup(h => h.RunStageAsync(It.IsAny<AgentSessionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AgentSessionContext ctx, CancellationToken ct) =>
            {
                capturedContext = ctx;
                return AgentRunResult.Completed(new AgentRunMetrics(), summary: "Implementation complete");
            });

        // Execute task
        await orchestrator.ProcessTaskAsync(task.Id);

        Assert.NotNull(capturedContext);
        Assert.Equal(StandardStageIds.Implementation, capturedContext.StageId);
        Assert.Equal("Q: DB?\nA: PostgreSQL", capturedContext.PendingHumanAnswer);
        Assert.Equal("[{\"QuestionIndex\":0,\"AnswerText\":\"PostgreSQL\"}]", capturedContext.PendingHumanAnswerJson);

        // DataBag in repo must have consumed the human answer
        var reloaded = await repo.GetByIdAsync(task.Id);
        Assert.NotNull(reloaded);
        var bagAfter = PipelineDataBag.FromJson(reloaded.PipelineDataJson);
        Assert.Null(bagAfter.Get<string>(PipelineDataBagKeys.LastHumanAnswer));
        Assert.Null(bagAfter.Get<string>(PipelineDataBagKeys.LastHumanAnswerJson));
    }

    [Fact]
    public async Task ResumeExecution_DownstreamStagesDoNotReceivePendingHumanAnswer()
    {
        var repo = new InMemoryTaskRepository();
        var harnessMock = new Mock<IAgentHarness>();
        harnessMock.Setup(h => h.HarnessId).Returns(StandardHarnessIds.Crucible);

        var builder = new TestOrchestratorBuilder
        {
            TaskRepository = repo
        };
        builder.HarnessRegistry.RegisterHarness(harnessMock.Object);

        var orchestrator = builder.Build();

        var ws = new Workspace
        {
            Id = 1,
            Name = "Test WS",
            RepositoryPath = "C:\\test",
            AgentProfiles = new List<AgentProfile>
            {
                new()
                {
                    Id = StandardStageIds.Implementation,
                    HarnessId = StandardHarnessIds.Crucible,
                    Mode = AgentMode.Full
                }
            }
        };
        await builder.WorkspaceRepository.CreateAsync(ws);

        // Suppose task was interrupted at TaskReview, but now Implementation is running
        var initialBag = new PipelineDataBag();
        initialBag.Set(PipelineDataBagKeys.LastHumanAnswer, "Old answer from Stage 1");
        initialBag.Set(PipelineDataBagKeys.LastHumanAnswerJson, "[{}]");

        var task = new ForgeTask
        {
            Id = 102,
            WorkspaceId = 1,
            Title = "Downstream stage task",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.Implementation,
            InterruptedStageId = StandardStageIds.TaskReview, // Mismatch with Implementation
            PipelineDataJson = initialBag.ToJson()
        };
        await repo.CreateAsync(task);
        ((InMemoryTaskDependencyStore)builder.DependencyStore).AddTask(task.Id, 1, task.Status, task.IsWaitingForHuman);

        AgentSessionContext? capturedContext = null;
        harnessMock.Setup(h => h.RunStageAsync(It.IsAny<AgentSessionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AgentSessionContext ctx, CancellationToken ct) =>
            {
                capturedContext = ctx;
                return AgentRunResult.Completed(new AgentRunMetrics(), summary: "Implementation complete");
            });

        await orchestrator.ProcessTaskAsync(task.Id);

        Assert.NotNull(capturedContext);
        Assert.Equal(StandardStageIds.Implementation, capturedContext.StageId);
        Assert.Null(capturedContext.PendingHumanAnswer);
        Assert.Null(capturedContext.PendingHumanAnswerJson);
    }

    [Fact]
    public async Task ResumeExecution_DoesNotPurgeHumanAnswer_WhenAnotherStageRunsBeforeInterruptedStage()
    {
        var repo = new InMemoryTaskRepository();
        var harnessMock = new Mock<IAgentHarness>();
        harnessMock.Setup(h => h.HarnessId).Returns(StandardHarnessIds.Crucible);

        var intermediateStageMock = new Mock<IWorkflowStage>();
        intermediateStageMock.Setup(s => s.StageId).Returns(StandardStageIds.PreImplementationBaseline);
        intermediateStageMock.Setup(s => s.Kind).Returns(WorkflowStageKind.Deterministic);
        intermediateStageMock.Setup(s => s.QueueTier).Returns(StageQueueTier.Implementation);
        intermediateStageMock.Setup(s => s.ExecuteAsync(It.IsAny<PipelineContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(StageExecutionResult.Success());

        var interruptedStageMock = new Mock<IWorkflowStage>();
        interruptedStageMock.Setup(s => s.StageId).Returns(StandardStageIds.Implementation);
        interruptedStageMock.Setup(s => s.Kind).Returns(WorkflowStageKind.Agentic);
        interruptedStageMock.Setup(s => s.QueueTier).Returns(StageQueueTier.Implementation);
        interruptedStageMock.Setup(s => s.ExecuteAsync(It.IsAny<PipelineContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(StageExecutionResult.Success());

        var builder = new TestOrchestratorBuilder
        {
            TaskRepository = repo,
            PipelineProvider = new DefaultWorkflowPipelineProvider(new WorkflowPipeline(new[]
            {
                intermediateStageMock.Object,
                interruptedStageMock.Object
            }))
        };
        builder.HarnessRegistry.RegisterHarness(harnessMock.Object);

        var orchestrator = builder.Build();

        var ws = new Workspace
        {
            Id = 1,
            Name = "Test WS",
            RepositoryPath = "C:\\test",
            AgentProfiles = new List<AgentProfile>
            {
                new()
                {
                    Id = StandardStageIds.Implementation,
                    HarnessId = StandardHarnessIds.Crucible,
                    Mode = AgentMode.Full
                }
            }
        };
        await builder.WorkspaceRepository.CreateAsync(ws);

        var initialBag = new PipelineDataBag();
        initialBag.Set(PipelineDataBagKeys.LastHumanAnswer, "Answer for implementation");
        initialBag.Set(PipelineDataBagKeys.LastHumanAnswerJson, "[{\"QuestionIndex\":0,\"AnswerText\":\"Answer for implementation\"}]");

        var task = new ForgeTask
        {
            Id = 103,
            WorkspaceId = 1,
            Title = "Task resumed before interrupted stage",
            Status = TaskOverallStatus.Open,
            IsWaitingForHuman = false,
            CurrentStageId = StandardStageIds.PreImplementationBaseline,
            InterruptedStageId = StandardStageIds.Implementation,
            PipelineDataJson = initialBag.ToJson()
        };
        await repo.CreateAsync(task);
        ((InMemoryTaskDependencyStore)builder.DependencyStore).AddTask(task.Id, 1, task.Status, false);

        AgentSessionContext? capturedContext = null;
        harnessMock.Setup(h => h.RunStageAsync(It.IsAny<AgentSessionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AgentSessionContext ctx, CancellationToken ct) =>
            {
                capturedContext = ctx;
                return AgentRunResult.Completed(new AgentRunMetrics(), summary: "Implementation done");
            });

        await orchestrator.ProcessTaskAsync(task.Id);

        // PreImplementationBaseline ran first without purging LastHumanAnswer, so Implementation received it!
        Assert.NotNull(capturedContext);
        Assert.Equal(StandardStageIds.Implementation, capturedContext.StageId);
        Assert.Equal("Answer for implementation", capturedContext.PendingHumanAnswer);

        // And after Implementation consumed it, the data bag in repo has it purged
        var reloaded = await repo.GetByIdAsync(task.Id);
        Assert.NotNull(reloaded);
        var bagAfter = PipelineDataBag.FromJson(reloaded.PipelineDataJson);
        Assert.Null(bagAfter.Get<string>(PipelineDataBagKeys.LastHumanAnswer));
    }

    [Fact]
    public async Task ResumeExecution_WhenInterruptedStageIdIsNull_FallsBackToCurrentStageId_WhenWaitingForHuman()
    {
        var repo = new InMemoryTaskRepository();
        var harnessMock = new Mock<IAgentHarness>();
        harnessMock.Setup(h => h.HarnessId).Returns(StandardHarnessIds.Crucible);

        var builder = new TestOrchestratorBuilder
        {
            TaskRepository = repo,
            PipelineProvider = new DefaultWorkflowPipelineProvider(new WorkflowPipeline(new[]
            {
                new TestWorkflowStage(StandardStageIds.Implementation, WorkflowStageKind.Agentic)
                {
                    QueueTier = StageQueueTier.Implementation
                }
            }))
        };
        builder.HarnessRegistry.RegisterHarness(harnessMock.Object);

        var orchestrator = builder.Build();

        var ws = new Workspace
        {
            Id = 1,
            Name = "Test WS",
            RepositoryPath = "C:\\test",
            AgentProfiles = new List<AgentProfile>
            {
                new()
                {
                    Id = StandardStageIds.Implementation,
                    HarnessId = StandardHarnessIds.Crucible,
                    Mode = AgentMode.Full
                }
            }
        };
        await builder.WorkspaceRepository.CreateAsync(ws);

        var initialBag = new PipelineDataBag();
        initialBag.Set(PipelineDataBagKeys.LastHumanAnswer, "Fallback answer");
        initialBag.Set(PipelineDataBagKeys.LastHumanAnswerJson, "[{}]");

        var task = new ForgeTask
        {
            Id = 104,
            WorkspaceId = 1,
            Title = "Task with null InterruptedStageId",
            Status = TaskOverallStatus.Open,
            IsWaitingForHuman = true,
            CurrentStageId = StandardStageIds.Implementation,
            InterruptedStageId = null, // Simulated edge-case: null on entity but IsWaitingForHuman == true
            PipelineDataJson = initialBag.ToJson()
        };
        await repo.CreateAsync(task);
        ((InMemoryTaskDependencyStore)builder.DependencyStore).AddTask(task.Id, 1, task.Status, false);

        AgentSessionContext? capturedContext = null;
        harnessMock.Setup(h => h.RunStageAsync(It.IsAny<AgentSessionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AgentSessionContext ctx, CancellationToken ct) =>
            {
                capturedContext = ctx;
                return AgentRunResult.Completed(new AgentRunMetrics(), summary: "Implementation complete");
            });

        await orchestrator.ProcessTaskAsync(task.Id);

        Assert.NotNull(capturedContext);
        Assert.Equal(StandardStageIds.Implementation, capturedContext.StageId);
        Assert.Equal("Fallback answer", capturedContext.PendingHumanAnswer);
    }

    [Fact]
    public async Task ResumeExecution_PreservesInterruptedStageId_WhenDeferredByPause()
    {
        var repo = new InMemoryTaskRepository();
        var opsControlMock = new Mock<IOperationsControlService>();
        opsControlMock.Setup(o => o.IsPaused).Returns(true);

        var builder = new TestOrchestratorBuilder
        {
            TaskRepository = repo,
            OperationsControlService = opsControlMock.Object,
            PipelineProvider = new DefaultWorkflowPipelineProvider(new WorkflowPipeline(new[]
            {
                new TestWorkflowStage(StandardStageIds.TaskReview, WorkflowStageKind.Agentic)
            }))
        };

        var orchestrator = builder.Build();

        var ws = new Workspace { Id = 1, Name = "Test WS", RepositoryPath = "C:\\test" };
        await builder.WorkspaceRepository.CreateAsync(ws);

        var task = new ForgeTask
        {
            Id = 105,
            WorkspaceId = 1,
            Title = "Task deferred by pause",
            Status = TaskOverallStatus.Open,
            IsWaitingForHuman = true,
            CurrentStageId = StandardStageIds.TaskReview,
            InterruptedStageId = StandardStageIds.TaskReview
        };
        await repo.CreateAsync(task);
        ((InMemoryTaskDependencyStore)builder.DependencyStore).AddTask(task.Id, 1, task.Status, false);

        await orchestrator.ProcessTaskAsync(task.Id);

        var reloaded = await repo.GetByIdAsync(task.Id);
        Assert.NotNull(reloaded);
        Assert.True(reloaded.IsDeferredByPause);
        Assert.Equal(StandardStageIds.TaskReview, reloaded.InterruptedStageId);
    }
}

