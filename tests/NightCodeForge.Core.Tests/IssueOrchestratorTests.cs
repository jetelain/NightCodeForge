using NightCodeForge.Core.Concurrency;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Core.Workflow.Stages;

namespace NightCodeForge.Core.Tests;

public class IssueOrchestratorTests
{
    private readonly InMemoryIssueRepository _issueRepo;
    private readonly InMemoryWorkspaceRepository _workspaceRepo;
    private readonly InMemoryIssueDependencyStore _depStore;
    private readonly DependencyResolutionEngine _depEngine;
    private readonly WorkspaceLockProvider _lockProvider;
    private readonly FakeGitWorkspaceManager _gitManager;
    private readonly FakeIssueJobQueue _jobQueue;
    private readonly Workspace _workspace;

    public IssueOrchestratorTests()
    {
        _issueRepo = new InMemoryIssueRepository();
        _workspaceRepo = new InMemoryWorkspaceRepository();
        _depStore = new InMemoryIssueDependencyStore();
        _depEngine = new DependencyResolutionEngine(_depStore);
        _lockProvider = new WorkspaceLockProvider();
        _gitManager = new FakeGitWorkspaceManager();
        _jobQueue = new FakeIssueJobQueue();

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
    public async Task ProcessIssueAsync_WhenPrerequisitesNotReady_RemainsOpenAndDoesNotEnqueue()
    {
        // Issue 2 depends on Issue 1 (which is still Open, not Closed)
        var prereq = await _issueRepo.CreateAsync(new Issue { Id = 1, WorkspaceId = 1, Status = IssueOverallStatus.Open });
        var target = await _issueRepo.CreateAsync(new Issue { Id = 2, WorkspaceId = 1, Status = IssueOverallStatus.Open });

        _depStore.AddIssue(1, 1, IssueOverallStatus.Open);
        _depStore.AddIssue(2, 1, IssueOverallStatus.Open);
        _depStore.AddDependency(1, 2, 1);

        var orchestrator = new IssueOrchestrator(
            _issueRepo, _workspaceRepo, _depEngine, _lockProvider,
            _gitManager, new DefaultWorkflowPipelineProvider(), _jobQueue, _depStore);

        await orchestrator.ProcessIssueAsync(target.Id);

        var updated = await _issueRepo.GetByIdAsync(target.Id);
        Assert.NotNull(updated);
        Assert.Equal(IssueOverallStatus.Open, updated.Status);
        Assert.Empty(_jobQueue.InferenceQueue);
    }

    [Fact]
    public async Task ProcessIssueAsync_WhenValid_AcquiresLockAndChecksOutFeatureBranch()
    {
        var issue = await _issueRepo.CreateAsync(new Issue
        {
            Id = 10,
            WorkspaceId = 1,
            Status = IssueOverallStatus.Open,
            BaseBranch = "main"
        });
        _depStore.AddIssue(10, 1, IssueOverallStatus.Open);

        var reviewStage = new IssueReviewStage
        {
            Handler = (ctx, ct) => Task.FromResult(StageExecutionResult.Success())
        };
        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { reviewStage });

        var orchestrator = new IssueOrchestrator(
            _issueRepo, _workspaceRepo, _depEngine, _lockProvider,
            _gitManager, new DefaultWorkflowPipelineProvider(pipeline), _jobQueue, _depStore);

        await orchestrator.ProcessIssueAsync(issue.Id);

        Assert.Contains(_gitManager.CheckedOutBranches, b => b.IssueId == 10 && b.BaseBranch == "main");
    }

    [Fact]
    public async Task ProcessIssueAsync_WhenIntermediateStageCompletes_AdvancesToNextStageAndEnqueuesInference()
    {
        var issue = await _issueRepo.CreateAsync(new Issue
        {
            Id = 20,
            WorkspaceId = 1,
            Status = IssueOverallStatus.Open,
            CurrentStageId = "stage-1"
        });
        _depStore.AddIssue(20, 1, IssueOverallStatus.Open);

        var stage1 = new TestWorkflowStage("stage-1") { Handler = (ctx, ct) => Task.FromResult(StageExecutionResult.Success()) };
        var stage2 = new TestWorkflowStage("stage-2") { Handler = (ctx, ct) => Task.FromResult(StageExecutionResult.Success()) };
        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stage1, stage2 });

        var orchestrator = new IssueOrchestrator(
            _issueRepo, _workspaceRepo, _depEngine, _lockProvider,
            _gitManager, new DefaultWorkflowPipelineProvider(pipeline), _jobQueue, _depStore);

        await orchestrator.ProcessIssueAsync(issue.Id);

        var updated = await _issueRepo.GetByIdAsync(20);
        Assert.NotNull(updated);
        Assert.Equal("stage-2", updated.CurrentStageId);
        Assert.Equal(IssueOverallStatus.Open, updated.Status);
        Assert.Contains(20, _jobQueue.InferenceQueue);
    }

    [Fact]
    public async Task ProcessIssueAsync_WhenFinalStageCompletes_TransitionsToReadyAndGeneratesDiff()
    {
        var issue = await _issueRepo.CreateAsync(new Issue
        {
            Id = 30,
            WorkspaceId = 1,
            Status = IssueOverallStatus.Open,
            CurrentStageId = "final-stage"
        });
        _depStore.AddIssue(30, 1, IssueOverallStatus.Open);

        var finalStage = new TestWorkflowStage("final-stage") { Handler = (ctx, ct) => Task.FromResult(StageExecutionResult.Success()) };
        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { finalStage });

        _gitManager.GeneratedDiff = "diff --git a/Program.cs b/Program.cs\n+Console.WriteLine(\"Done\");";

        var orchestrator = new IssueOrchestrator(
            _issueRepo, _workspaceRepo, _depEngine, _lockProvider,
            _gitManager, new DefaultWorkflowPipelineProvider(pipeline), _jobQueue, _depStore);

        await orchestrator.ProcessIssueAsync(issue.Id);

        var updated = await _issueRepo.GetByIdAsync(30);
        Assert.NotNull(updated);
        Assert.Equal(IssueOverallStatus.Ready, updated.Status);

        var bag = PipelineDataBag.FromJson(updated.PipelineDataJson);
        Assert.Equal(_gitManager.GeneratedDiff, bag.Get<string>("ReviewDiff"));
    }

    [Fact]
    public async Task ProcessIssueAsync_WhenWaitingForHuman_CreatesWipCheckpointSavesQuestionAndCleanReturn()
    {
        var issue = await _issueRepo.CreateAsync(new Issue
        {
            Id = 40,
            WorkspaceId = 1,
            Status = IssueOverallStatus.Open,
            CurrentStageId = "review-stage"
        });
        _depStore.AddIssue(40, 1, IssueOverallStatus.Open);

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

        var orchestrator = new IssueOrchestrator(
            _issueRepo, _workspaceRepo, _depEngine, _lockProvider,
            _gitManager, new DefaultWorkflowPipelineProvider(pipeline), _jobQueue, _depStore);

        // Process should return cleanly without throwing (freeing the GPU worker!)
        await orchestrator.ProcessIssueAsync(issue.Id);

        var updated = await _issueRepo.GetByIdAsync(40);
        Assert.NotNull(updated);
        Assert.True(updated.IsWaitingForHuman);
        Assert.Equal(IssueOverallStatus.Open, updated.Status);

        // Checkpoint created
        Assert.Single(_gitManager.WipCheckpoints);
        Assert.Equal("review-stage", _gitManager.WipCheckpoints[0].StageName);

        var checkpoints = _issueRepo.GetAllCheckpoints();
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
    public async Task ProcessIssueAsync_WhenHumanInterruptionExceptionThrown_InterceptsCleanly()
    {
        var issue = await _issueRepo.CreateAsync(new Issue
        {
            Id = 50,
            WorkspaceId = 1,
            Status = IssueOverallStatus.Open,
            CurrentStageId = "impl-stage"
        });
        _depStore.AddIssue(50, 1, IssueOverallStatus.Open);

        var stage = new TestWorkflowStage("impl-stage")
        {
            Handler = (ctx, ct) => throw new HumanInterruptionException("Should we support PostgreSQL or SQLite?")
        };
        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stage });

        var orchestrator = new IssueOrchestrator(
            _issueRepo, _workspaceRepo, _depEngine, _lockProvider,
            _gitManager, new DefaultWorkflowPipelineProvider(pipeline), _jobQueue, _depStore);

        // Clean completion of job - no exception thrown to Hangfire
        await orchestrator.ProcessIssueAsync(issue.Id);

        var updated = await _issueRepo.GetByIdAsync(50);
        Assert.NotNull(updated);
        Assert.True(updated.IsWaitingForHuman);
        Assert.Equal(IssueOverallStatus.Open, updated.Status);
        Assert.Contains(updated.Questions, q => q.Prompt.Contains("PostgreSQL"));
    }

    [Fact]
    public async Task ProcessIssueAsync_WhenStageFails_TransitionsToFailedAndLogsError()
    {
        var issue = await _issueRepo.CreateAsync(new Issue
        {
            Id = 60,
            WorkspaceId = 1,
            Status = IssueOverallStatus.Open,
            CurrentStageId = "impl-stage"
        });
        _depStore.AddIssue(60, 1, IssueOverallStatus.Open);

        var stage = new TestWorkflowStage("impl-stage")
        {
            Handler = (ctx, ct) => Task.FromResult(StageExecutionResult.Failure("Compilation failed: error CS1002"))
        };
        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stage });

        var orchestrator = new IssueOrchestrator(
            _issueRepo, _workspaceRepo, _depEngine, _lockProvider,
            _gitManager, new DefaultWorkflowPipelineProvider(pipeline), _jobQueue, _depStore);

        await orchestrator.ProcessIssueAsync(issue.Id);

        var updated = await _issueRepo.GetByIdAsync(60);
        Assert.NotNull(updated);
        Assert.Equal(IssueOverallStatus.Failed, updated.Status);

        var logs = _issueRepo.GetAllLogs();
        Assert.Contains(logs, l => l.Message.Contains("error CS1002"));
    }

    [Fact]
    public async Task ProcessIssueAsync_CapturesExecutionMetricsAndTokens()
    {
        var issue = await _issueRepo.CreateAsync(new Issue
        {
            Id = 70,
            WorkspaceId = 1,
            Status = IssueOverallStatus.Open,
            CurrentStageId = "impl-stage"
        });
        _depStore.AddIssue(70, 1, IssueOverallStatus.Open);

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

        var orchestrator = new IssueOrchestrator(
            _issueRepo, _workspaceRepo, _depEngine, _lockProvider,
            _gitManager, new DefaultWorkflowPipelineProvider(pipeline), _jobQueue, _depStore);

        await orchestrator.ProcessIssueAsync(issue.Id);

        var records = _issueRepo.GetAllExecutionRecords();
        Assert.Single(records);
        Assert.Equal(70, records[0].IssueId);
        Assert.Equal(1200, records[0].PromptTokens);
        Assert.Equal(350, records[0].CompletionTokens);
        Assert.Equal(1550, records[0].TotalTokens);
        Assert.Equal(StageExecutionStatus.Completed, records[0].Status);
    }

    [Fact]
    public async Task ProcessIssueAsync_WhenPendingProposalsPresent_PersistsSuggestedIssues()
    {
        var issue = await _issueRepo.CreateAsync(new Issue
        {
            Id = 80,
            WorkspaceId = 1,
            Status = IssueOverallStatus.Open,
            CurrentStageId = "triage-stage"
        });
        _depStore.AddIssue(80, 1, IssueOverallStatus.Open);

        var stage = new TestWorkflowStage("triage-stage")
        {
            Handler = (ctx, ct) =>
            {
                ctx.DataBag.Set("PendingProposals", new List<IssueProposal>
                {
                    new IssueProposal("Fix Memory Leak", "Found memory leak in parser", "bug")
                });
                return Task.FromResult(StageExecutionResult.Success());
            }
        };
        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stage });

        var orchestrator = new IssueOrchestrator(
            _issueRepo, _workspaceRepo, _depEngine, _lockProvider,
            _gitManager, new DefaultWorkflowPipelineProvider(pipeline), _jobQueue, _depStore);

        await orchestrator.ProcessIssueAsync(issue.Id);

        var workspaceIssues = await _issueRepo.GetByWorkspaceIdAsync(_workspace.Id);
        var proposal = workspaceIssues.FirstOrDefault(i => i.Title == "Fix Memory Leak");
        Assert.NotNull(proposal);
        Assert.Equal(IssueOverallStatus.Suggested, proposal.Status);
        Assert.Equal("Found memory leak in parser", proposal.Description);
        Assert.Equal(1, proposal.Priority);
    }
}
