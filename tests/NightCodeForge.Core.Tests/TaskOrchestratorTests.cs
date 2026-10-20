using Moq;
using NightCodeForge.Core.Concurrency;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Streaming;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Core.Workflow.Stages;

namespace NightCodeForge.Core.Tests;

public class TaskOrchestratorTests
{
    private readonly InMemoryTaskRepository _taskRepo;
    private readonly InMemoryWorkspaceRepository _workspaceRepo;
    private readonly InMemoryTaskDependencyStore _depStore;
    private readonly DependencyResolutionEngine _depEngine;
    private readonly WorkspaceLockProvider _lockProvider;
    private readonly FakeGitWorkspaceManager _gitManager;
    private readonly FakeTaskJobQueue _jobQueue;
    private readonly Workspace _workspace;

    public TaskOrchestratorTests()
    {
        _taskRepo = new InMemoryTaskRepository();
        _workspaceRepo = new InMemoryWorkspaceRepository();
        _depStore = new InMemoryTaskDependencyStore();
        _depEngine = new DependencyResolutionEngine(_depStore);
        _lockProvider = new WorkspaceLockProvider();
        _gitManager = new FakeGitWorkspaceManager();
        _jobQueue = new FakeTaskJobQueue();

        _workspace = new Workspace
        {
            Id = 1,
            Name = "NCF Test Workspace",
            RepositoryPath = "C:/test/repo",
            DefaultBaseBranch = "main"
        };
        _workspaceRepo.CreateAsync(_workspace).GetAwaiter().GetResult();
    }

    private TaskOrchestrator CreateOrchestrator(IWorkflowPipelineProvider? pipelineProvider = null)
    {
        return new Fixtures.TestOrchestratorBuilder
        {
            TaskRepository = _taskRepo,
            WorkspaceRepository = _workspaceRepo,
            DependencyEngine = _depEngine,
            LockProvider = _lockProvider,
            GitWorkspaceManager = _gitManager,
            JobQueue = _jobQueue,
            DependencyStore = _depStore,
            PipelineProvider = pipelineProvider ?? new DefaultWorkflowPipelineProvider()
        }.Build();
    }

    [Fact]
    public async Task ProcessTaskAsync_WhenPrerequisitesNotReady_RemainsOpenAndDoesNotEnqueue()
    {
        // ForgeTask 2 depends on ForgeTask 1 (which is still Open, not Closed)
        var prereq = await _taskRepo.CreateAsync(new ForgeTask { Id = 1, WorkspaceId = 1, Status = TaskOverallStatus.Open });
        var target = await _taskRepo.CreateAsync(new ForgeTask { Id = 2, WorkspaceId = 1, Status = TaskOverallStatus.Open });

        _depStore.AddTask(1, 1, TaskOverallStatus.Open);
        _depStore.AddTask(2, 1, TaskOverallStatus.Open);
        _depStore.AddDependency(1, 2, 1);

        var orchestrator = CreateOrchestrator();

        await orchestrator.ProcessTaskAsync(target.Id);

        var updated = await _taskRepo.GetByIdAsync(target.Id);
        Assert.NotNull(updated);
        Assert.Equal(TaskOverallStatus.Open, updated.Status);
        Assert.Empty(_jobQueue.InferenceQueue);
    }

    [Fact]
    public async Task ProcessTaskAsync_WhenValid_AcquiresLockAndChecksOutFeatureBranch()
    {
        var task = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 10,
            WorkspaceId = 1,
            Status = TaskOverallStatus.Open,
            BaseBranch = "main"
        });
        _depStore.AddTask(10, 1, TaskOverallStatus.Open);

        var reviewStage = TestWorkflowPipelines.CreateReviewStage(
            (ctx, ct) => Task.FromResult(StageExecutionResult.Success()));
        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { reviewStage });

        var orchestrator = CreateOrchestrator(new DefaultWorkflowPipelineProvider(pipeline));

        await orchestrator.ProcessTaskAsync(task.Id);

        Assert.Contains(_gitManager.CheckedOutBranches, b => b.TaskId == 10 && b.BaseBranch == "main");
    }

    [Fact]
    public async Task ProcessTaskAsync_WhenIntermediateStageCompletes_AdvancesToNextStageAndEnqueuesInference()
    {
        var task = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 20,
            WorkspaceId = 1,
            Status = TaskOverallStatus.Open,
            CurrentStageId = "stage-1"
        });
        _depStore.AddTask(20, 1, TaskOverallStatus.Open);

        var stage1 = new TestWorkflowStage("stage-1") { Handler = (ctx, ct) => Task.FromResult(StageExecutionResult.Success()) };
        var stage2 = new TestWorkflowStage("stage-2") { Handler = (ctx, ct) => Task.FromResult(StageExecutionResult.Success()) };
        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stage1, stage2 });

        var orchestrator = CreateOrchestrator(new DefaultWorkflowPipelineProvider(pipeline));

        await orchestrator.ProcessTaskAsync(task.Id);

        var updated = await _taskRepo.GetByIdAsync(20);
        Assert.NotNull(updated);
        Assert.Equal("stage-2", updated.CurrentStageId);
        Assert.Equal(TaskOverallStatus.Open, updated.Status);
        Assert.Contains(20, _jobQueue.InferenceQueue);
    }

    [Fact]
    public async Task ProcessTaskAsync_StageAdvanceTransition_LogsHistoryWithTaskOrchestratorAttribution()
    {
        var task = await _taskRepo.CreateWithBaselineAsync(new ForgeTask
        {
            Id = 25,
            WorkspaceId = 1,
            Status = TaskOverallStatus.Open,
            CurrentStageId = "stage-1"
        }, null, AuditActors.User, "Created baseline");
        _depStore.AddTask(25, 1, TaskOverallStatus.Open);

        var stage1 = new TestWorkflowStage("stage-1") { Handler = (ctx, ct) => Task.FromResult(StageExecutionResult.Success()) };
        var stage2 = new TestWorkflowStage("stage-2") { Handler = (ctx, ct) => Task.FromResult(StageExecutionResult.Success()) };
        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stage1, stage2 });

        var orchestrator = CreateOrchestrator(new DefaultWorkflowPipelineProvider(pipeline));

        await orchestrator.ProcessTaskAsync(task.Id);

        var history = await _taskRepo.GetHistoryAsync(25);
        Assert.True(history.Count >= 2);

        var last = history.Last();
        Assert.Equal(AuditActors.TaskOrchestrator, last.ChangedBy);
        Assert.Contains("Advanced to 'stage-2'", last.ChangeReason);
    }

    [Fact]
    public async Task RequestChangesAsync_AppendsHistorySnapshotWithUserAttributionAndTargetStage()
    {
        var task = await _taskRepo.CreateWithBaselineAsync(new ForgeTask
        {
            Id = 35,
            WorkspaceId = 1,
            Status = TaskOverallStatus.Ready,
            CurrentStageId = "stage-review"
        }, null, AuditActors.User, "Created baseline");

        var stageImpl = new TestWorkflowStage("implementation");
        var stageReview = new TestWorkflowStage("stage-review") { LoopBackStageId = "implementation" };
        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stageImpl, stageReview });

        var orchestrator = CreateOrchestrator(new DefaultWorkflowPipelineProvider(pipeline));

        await orchestrator.RequestChangesAsync(task.Id, "Need unit tests for edge cases");

        var updated = await _taskRepo.GetByIdAsync(35);
        Assert.NotNull(updated);
        Assert.Equal(TaskOverallStatus.Reviewed, updated.Status);
        Assert.Equal("implementation", updated.CurrentStageId);

        var history = await _taskRepo.GetHistoryAsync(35);
        Assert.Equal(2, history.Count);

        var latest = history.Last();
        Assert.Equal(AuditActors.User, latest.ChangedBy);
        Assert.Contains("Reviewer requested changes", latest.ChangeReason);
        Assert.Contains("Need unit tests for edge cases", latest.ChangeReason);
        Assert.Equal(TaskOverallStatus.Reviewed, latest.Status);
    }

    [Fact]
    public async Task ProcessTaskAsync_WhenStageFailsPermanently_RecordsHistorySnapshotWithOrchestratorAttribution()
    {
        var task = await _taskRepo.CreateWithBaselineAsync(new ForgeTask
        {
            Id = 36,
            WorkspaceId = 1,
            Status = TaskOverallStatus.Open,
            CurrentStageId = "failing-stage"
        }, null, AuditActors.User, "Created baseline");
        _depStore.AddTask(36, 1, TaskOverallStatus.Open);

        var failingStage = new TestWorkflowStage("failing-stage")
        {
            Handler = (ctx, ct) => Task.FromResult(StageExecutionResult.Failure("Compilation broken"))
        };
        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { failingStage });

        var orchestrator = CreateOrchestrator(new DefaultWorkflowPipelineProvider(pipeline));

        await orchestrator.ProcessTaskAsync(task.Id);

        var updated = await _taskRepo.GetByIdAsync(36);
        Assert.NotNull(updated);
        Assert.Equal(TaskOverallStatus.Failed, updated.Status);

        var history = await _taskRepo.GetHistoryAsync(36);
        Assert.True(history.Count >= 2);

        var latest = history.Last();
        Assert.Equal(AuditActors.TaskOrchestrator, latest.ChangedBy);
        Assert.Contains("Pipeline failed at stage 'failing-stage'", latest.ChangeReason);
        Assert.Equal(TaskOverallStatus.Failed, latest.Status);
    }

    [Fact]
    public async Task ProcessTaskAsync_WhenFinalStageCompletes_TransitionsToReadyAndGeneratesDiff()
    {
        var task = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 30,
            WorkspaceId = 1,
            Status = TaskOverallStatus.Open,
            CurrentStageId = "final-stage"
        });
        _depStore.AddTask(30, 1, TaskOverallStatus.Open);

        var finalStage = new TestWorkflowStage("final-stage") { Handler = (ctx, ct) => Task.FromResult(StageExecutionResult.Success()) };
        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { finalStage });

        _gitManager.GeneratedDiff = "diff --git a/Program.cs b/Program.cs\n+Console.WriteLine(\"Done\");";

        var orchestrator = CreateOrchestrator(new DefaultWorkflowPipelineProvider(pipeline));

        await orchestrator.ProcessTaskAsync(task.Id);

        var updated = await _taskRepo.GetByIdAsync(30);
        Assert.NotNull(updated);
        Assert.Equal(TaskOverallStatus.Ready, updated.Status);

        var bag = PipelineDataBag.FromJson(updated.PipelineDataJson);
        Assert.Equal(_gitManager.GeneratedDiffStat, bag.Get<string>(PipelineDataBagKeys.ReviewDiffStat));
    }

    [Fact]
    public async Task ProcessTaskAsync_WhenWaitingForHuman_CreatesWipCheckpointSavesQuestionAndCleanReturn()
    {
        var task = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 40,
            WorkspaceId = 1,
            Status = TaskOverallStatus.Open,
            CurrentStageId = "review-stage"
        });
        _depStore.AddTask(40, 1, TaskOverallStatus.Open);

        var question = new Question
        {
            Prompt = "Which database provider should we use?",
            Rationale = "Architectural decision needed"
        };

        var stage = new TestWorkflowStage("review-stage")
        {
            Handler = (ctx, ct) =>
            {
                ctx.DataBag.Set("SerializedMemory", "MemorySnapshot123");
                return Task.FromResult(StageExecutionResult.WaitingForHuman(new[] { question }));
            }
        };
        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stage });

        var orchestrator = CreateOrchestrator(new DefaultWorkflowPipelineProvider(pipeline));

        // Process should return cleanly without throwing (freeing the GPU worker!)
        await orchestrator.ProcessTaskAsync(task.Id);

        var updated = await _taskRepo.GetByIdAsync(40);
        Assert.NotNull(updated);
        Assert.True(updated.IsWaitingForHuman);
        Assert.Equal(TaskOverallStatus.Open, updated.Status);

        // Checkpoint created
        Assert.Single(_gitManager.WipCheckpoints);
        Assert.Equal("review-stage", _gitManager.WipCheckpoints[0].StageName);

        var checkpoints = _taskRepo.GetAllCheckpoints();
        Assert.Single(checkpoints);
        Assert.Equal("MemorySnapshot123", checkpoints[0].SerializedMemory);

        // Question persisted
        Assert.Single(updated.Questions);
        Assert.Equal("Which database provider should we use?", updated.Questions.First().Prompt);
        Assert.Equal(QuestionStatus.Pending, updated.Questions.First().Status);

        // No inference jobs enqueued while blocked
        Assert.Empty(_jobQueue.InferenceQueue);
        Assert.Empty(_jobQueue.UrgentQueue);
    }

    [Fact]
    public async Task ProcessTaskAsync_WhenHumanInterruptionExceptionThrown_InterceptsCleanly()
    {
        var task = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 50,
            WorkspaceId = 1,
            Status = TaskOverallStatus.Open,
            CurrentStageId = "impl-stage"
        });
        _depStore.AddTask(50, 1, TaskOverallStatus.Open);

        var stage = new TestWorkflowStage("impl-stage")
        {
            Handler = (ctx, ct) => throw new HumanInterruptionException(new[] { new QuestionItem("Should we support PostgreSQL or SQLite?") })
        };
        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stage });

        var orchestrator = CreateOrchestrator(new DefaultWorkflowPipelineProvider(pipeline));

        // Clean completion of job - no exception thrown to Hangfire
        await orchestrator.ProcessTaskAsync(task.Id);

        var updated = await _taskRepo.GetByIdAsync(50);
        Assert.NotNull(updated);
        Assert.True(updated.IsWaitingForHuman);
        Assert.Equal(TaskOverallStatus.Open, updated.Status);
        Assert.Contains(updated.Questions, q => q.Prompt.Contains("PostgreSQL"));
    }

    [Fact]
    public async Task ProcessTaskAsync_WhenStageFails_TransitionsToFailedAndLogsError()
    {
        var task = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 60,
            WorkspaceId = 1,
            Status = TaskOverallStatus.Open,
            CurrentStageId = "impl-stage"
        });
        _depStore.AddTask(60, 1, TaskOverallStatus.Open);

        var stage = new TestWorkflowStage("impl-stage")
        {
            Handler = (ctx, ct) => Task.FromResult(StageExecutionResult.Failure("Compilation failed: error CS1002"))
        };
        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stage });

        var orchestrator = CreateOrchestrator(new DefaultWorkflowPipelineProvider(pipeline));

        await orchestrator.ProcessTaskAsync(task.Id);

        var updated = await _taskRepo.GetByIdAsync(60);
        Assert.NotNull(updated);
        Assert.Equal(TaskOverallStatus.Failed, updated.Status);

        var logs = _taskRepo.GetAllLogs();
        Assert.Contains(logs, l => l.Message.Contains("error CS1002"));
    }

    [Fact]
    public async Task ProcessTaskAsync_CapturesExecutionMetricsAndTokens()
    {
        var task = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 70,
            WorkspaceId = 1,
            Status = TaskOverallStatus.Open,
            CurrentStageId = "impl-stage"
        });
        _depStore.AddTask(70, 1, TaskOverallStatus.Open);

        var stage = new TestWorkflowStage("impl-stage")
        {
            Handler = (ctx, ct) =>
            {
                var record = new StageExecutionRecord
                {
                    PromptTokens = 1200,
                    CompletionTokens = 350,
                    TotalTokens = 1550
                };
                return Task.FromResult(StageExecutionResult.Success(record: record));
            }
        };
        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stage });

        var orchestrator = CreateOrchestrator(new DefaultWorkflowPipelineProvider(pipeline));

        await orchestrator.ProcessTaskAsync(task.Id);

        var records = _taskRepo.GetAllExecutionRecords();
        Assert.Single(records);
        Assert.Equal(70, records[0].TaskId);
        Assert.Equal(1200, records[0].PromptTokens);
        Assert.Equal(350, records[0].CompletionTokens);
        Assert.Equal(1550, records[0].TotalTokens);
        Assert.Equal(StageExecutionStatus.Completed, records[0].Status);
    }

    [Fact]
    public async Task ProcessTaskAsync_WhenPendingProposalsPresent_PersistsSuggestedTasks()
    {
        var task = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 80,
            WorkspaceId = 1,
            Status = TaskOverallStatus.Open,
            CurrentStageId = "triage-stage"
        });
        _depStore.AddTask(80, 1, TaskOverallStatus.Open);

        var stage = new TestWorkflowStage("triage-stage")
        {
            Handler = (ctx, ct) =>
            {
                ctx.DataBag.Set(PipelineDataBagKeys.PendingProposals, new List<TaskProposal>
                {
                    new TaskProposal("Fix Memory Leak", "Found memory leak in parser", "bug")
                });
                return Task.FromResult(StageExecutionResult.Success());
            }
        };
        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stage });

        var orchestrator = CreateOrchestrator(new DefaultWorkflowPipelineProvider(pipeline));

        await orchestrator.ProcessTaskAsync(task.Id);

        var workspaceTasks = await _taskRepo.GetByWorkspaceIdAsync(_workspace.Id);
        var proposal = workspaceTasks.FirstOrDefault(i => i.Title == "Fix Memory Leak");
        Assert.NotNull(proposal);
        Assert.Equal(TaskOverallStatus.Suggested, proposal.Status);
        Assert.Equal("Found memory leak in parser", proposal.Description);
        Assert.Equal(TaskPriority.Normal, proposal.Priority);

        var history = await _taskRepo.GetHistoryAsync(proposal.Id);
        Assert.Single(history);
        Assert.Equal(1, history[0].Version);
        Assert.Equal(AuditActors.GetActorForStage("triage-stage"), history[0].ChangedBy);
        Assert.Contains("triage-stage", history[0].ChangeReason);
    }

    [Fact]
    public async Task ProcessTaskAsync_WhenDeterministicStageSucceeds_BroadcastsStageCompleted()
    {
        var task = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 91,
            WorkspaceId = 1,
            Status = TaskOverallStatus.Open,
            CurrentStageId = "deterministic-gate"
        });
        _depStore.AddTask(91, 1, TaskOverallStatus.Open);

        var deterministicStage = new TestWorkflowStage("deterministic-gate", WorkflowStageKind.Deterministic)
        {
            Handler = (ctx, ct) => Task.FromResult(StageExecutionResult.Success())
        };
        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { deterministicStage });

        var broadcastEvents = new List<AgentActivityEvent>();
        var capturingStreamer = new Mock<IAgentActivityStreamer>();
        capturingStreamer
            .Setup(s => s.BroadcastAsync(It.IsAny<AgentActivityEvent>(), It.IsAny<CancellationToken>()))
            .Callback<AgentActivityEvent, CancellationToken>((evt, _) => broadcastEvents.Add(evt))
            .Returns(Task.CompletedTask);

        var orchestrator = new Fixtures.TestOrchestratorBuilder
        {
            TaskRepository = _taskRepo,
            WorkspaceRepository = _workspaceRepo,
            DependencyEngine = _depEngine,
            LockProvider = _lockProvider,
            GitWorkspaceManager = _gitManager,
            JobQueue = _jobQueue,
            DependencyStore = _depStore,
            PipelineProvider = new DefaultWorkflowPipelineProvider(pipeline),
            Streamer = capturingStreamer.Object
        }.Build();

        await orchestrator.ProcessTaskAsync(task.Id);

        var completedEvt = broadcastEvents.FirstOrDefault(e => e.Kind == AgentActivityKind.StageCompleted && e.StageId == "deterministic-gate");
        Assert.NotNull(completedEvt);
        Assert.Equal(91, completedEvt.TaskId);
        Assert.Equal("Deterministic", completedEvt.Metadata?["StageKind"]);
    }

    [Fact]
    public async Task ProcessTaskAsync_WhenWorkflowStageSucceeds_BroadcastsStageCompletedBeforeNextStage()
    {
        var task = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 92,
            WorkspaceId = 1,
            Status = TaskOverallStatus.Open,
            CurrentStageId = "stage-1"
        });
        _depStore.AddTask(92, 1, TaskOverallStatus.Open);

        var stage1 = new TestWorkflowStage("stage-1", WorkflowStageKind.Agentic)
        {
            Handler = (ctx, ct) => Task.FromResult(StageExecutionResult.Success())
        };
        var stage2 = new TestWorkflowStage("stage-2", WorkflowStageKind.Agentic)
        {
            Handler = (ctx, ct) => Task.FromResult(StageExecutionResult.Success())
        };
        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stage1, stage2 });

        var broadcastEvents = new List<AgentActivityEvent>();
        var capturingStreamer = new Mock<IAgentActivityStreamer>();
        capturingStreamer
            .Setup(s => s.BroadcastAsync(It.IsAny<AgentActivityEvent>(), It.IsAny<CancellationToken>()))
            .Callback<AgentActivityEvent, CancellationToken>((evt, _) => broadcastEvents.Add(evt))
            .Returns(Task.CompletedTask);

        var orchestrator = new Fixtures.TestOrchestratorBuilder
        {
            TaskRepository = _taskRepo,
            WorkspaceRepository = _workspaceRepo,
            DependencyEngine = _depEngine,
            LockProvider = _lockProvider,
            GitWorkspaceManager = _gitManager,
            JobQueue = _jobQueue,
            DependencyStore = _depStore,
            PipelineProvider = new DefaultWorkflowPipelineProvider(pipeline),
            Streamer = capturingStreamer.Object
        }.Build();

        await orchestrator.ProcessTaskAsync(task.Id);

        var completedEvt = broadcastEvents.FirstOrDefault(e => e.Kind == AgentActivityKind.StageCompleted && e.StageId == "stage-1");
        Assert.NotNull(completedEvt);
        Assert.Equal(92, completedEvt.TaskId);
    }
}
