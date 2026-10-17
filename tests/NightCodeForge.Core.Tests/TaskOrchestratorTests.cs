using NightCodeForge.Core.Concurrency;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Repositories;
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

    [Fact]
    public async Task ProcessTaskAsync_WhenPrerequisitesNotReady_RemainsOpenAndDoesNotEnqueue()
    {
        // ForgeTask 2 depends on ForgeTask 1 (which is still Open, not Closed)
        var prereq = await _taskRepo.CreateAsync(new ForgeTask { Id = 1, WorkspaceId = 1, Status = TaskOverallStatus.Open });
        var target = await _taskRepo.CreateAsync(new ForgeTask { Id = 2, WorkspaceId = 1, Status = TaskOverallStatus.Open });

        _depStore.AddTask(1, 1, TaskOverallStatus.Open);
        _depStore.AddTask(2, 1, TaskOverallStatus.Open);
        _depStore.AddDependency(1, 2, 1);

        var orchestrator = new TaskOrchestrator(
            _taskRepo, _workspaceRepo, _depEngine, _lockProvider,
            _gitManager, new DefaultWorkflowPipelineProvider(), _jobQueue, _depStore);

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

        var orchestrator = new TaskOrchestrator(
            _taskRepo, _workspaceRepo, _depEngine, _lockProvider,
            _gitManager, new DefaultWorkflowPipelineProvider(pipeline), _jobQueue, _depStore);

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

        var orchestrator = new TaskOrchestrator(
            _taskRepo, _workspaceRepo, _depEngine, _lockProvider,
            _gitManager, new DefaultWorkflowPipelineProvider(pipeline), _jobQueue, _depStore);

        await orchestrator.ProcessTaskAsync(task.Id);

        var updated = await _taskRepo.GetByIdAsync(20);
        Assert.NotNull(updated);
        Assert.Equal("stage-2", updated.CurrentStageId);
        Assert.Equal(TaskOverallStatus.Open, updated.Status);
        Assert.Contains(20, _jobQueue.InferenceQueue);
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

        var orchestrator = new TaskOrchestrator(
            _taskRepo, _workspaceRepo, _depEngine, _lockProvider,
            _gitManager, new DefaultWorkflowPipelineProvider(pipeline), _jobQueue, _depStore);

        await orchestrator.ProcessTaskAsync(task.Id);

        var updated = await _taskRepo.GetByIdAsync(30);
        Assert.NotNull(updated);
        Assert.Equal(TaskOverallStatus.Ready, updated.Status);

        var bag = PipelineDataBag.FromJson(updated.PipelineDataJson);
        Assert.Equal(_gitManager.GeneratedDiff, bag.Get<string>("ReviewDiff"));
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
                return Task.FromResult(StageExecutionResult.WaitingForHuman(question));
            }
        };
        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stage });

        var orchestrator = new TaskOrchestrator(
            _taskRepo, _workspaceRepo, _depEngine, _lockProvider,
            _gitManager, new DefaultWorkflowPipelineProvider(pipeline), _jobQueue, _depStore);

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
            Handler = (ctx, ct) => throw new HumanInterruptionException("Should we support PostgreSQL or SQLite?")
        };
        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stage });

        var orchestrator = new TaskOrchestrator(
            _taskRepo, _workspaceRepo, _depEngine, _lockProvider,
            _gitManager, new DefaultWorkflowPipelineProvider(pipeline), _jobQueue, _depStore);

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

        var orchestrator = new TaskOrchestrator(
            _taskRepo, _workspaceRepo, _depEngine, _lockProvider,
            _gitManager, new DefaultWorkflowPipelineProvider(pipeline), _jobQueue, _depStore);

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

        var orchestrator = new TaskOrchestrator(
            _taskRepo, _workspaceRepo, _depEngine, _lockProvider,
            _gitManager, new DefaultWorkflowPipelineProvider(pipeline), _jobQueue, _depStore);

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

        var orchestrator = new TaskOrchestrator(
            _taskRepo, _workspaceRepo, _depEngine, _lockProvider,
            _gitManager, new DefaultWorkflowPipelineProvider(pipeline), _jobQueue, _depStore);

        await orchestrator.ProcessTaskAsync(task.Id);

        var workspaceTasks = await _taskRepo.GetByWorkspaceIdAsync(_workspace.Id);
        var proposal = workspaceTasks.FirstOrDefault(i => i.Title == "Fix Memory Leak");
        Assert.NotNull(proposal);
        Assert.Equal(TaskOverallStatus.Suggested, proposal.Status);
        Assert.Equal("Found memory leak in parser", proposal.Description);
        Assert.Equal(1, proposal.Priority);
    }
}
