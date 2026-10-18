using NightCodeForge.Core.Concurrency;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Tests.Fixtures;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Core.Workflow.Stages;

namespace NightCodeForge.Core.Tests;

public class ResumptionTests
{
    private readonly InMemoryTaskRepository _taskRepo;
    private readonly InMemoryWorkspaceRepository _workspaceRepo;
    private readonly InMemoryTaskDependencyStore _depStore;
    private readonly DependencyResolutionEngine _depEngine;
    private readonly WorkspaceLockProvider _lockProvider;
    private readonly FakeGitWorkspaceManager _gitManager;
    private readonly FakeTaskJobQueue _jobQueue;
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
            DependencyStore = _depStore
        }.Build();

    public ResumptionTests()
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
    public async Task SubmitHumanAnswerAsync_PersistsAnswerUpdatesStatusAndEnqueuesUrgent()
    {
        var task = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 100,
            WorkspaceId = 1,
            Status = TaskOverallStatus.Open,
            IsWaitingForHuman = true,
            Questions = new List<Question>
            {
                new()
                {
                    Id = 1,
                    TaskId = 100,
                    Prompt = "What port should the server listen on?",
                    Status = QuestionStatus.Pending
                }
            }
        });
        _depStore.AddTask(100, 1, TaskOverallStatus.Open);
        _depStore.SetWaitingForHuman(100, true);

        var orchestrator = CreateOrchestrator();

        await orchestrator.SubmitHumanAnswerAsync(1, "Port 8080");

        var question = await _taskRepo.GetQuestionByIdAsync(1);
        Assert.NotNull(question);
        Assert.Equal("Port 8080", question.Answer);
        Assert.Equal(QuestionStatus.Answered, question.Status);
        Assert.NotNull(question.AnsweredAt);

        var updatedTask = await _taskRepo.GetByIdAsync(100);
        Assert.NotNull(updatedTask);
        Assert.False(updatedTask.IsWaitingForHuman);
        Assert.Equal(TaskOverallStatus.Open, updatedTask.Status);

        var bag = PipelineDataBag.FromJson(updatedTask.PipelineDataJson);
        var lastAnswer = bag.Get<string>(PipelineDataBagKeys.LastHumanAnswer);
        Assert.NotNull(lastAnswer);
        Assert.Contains("What port should the server listen on?", lastAnswer);
        Assert.Contains("Port 8080", lastAnswer);

        // Enqueued to llm-urgent
        Assert.Contains(100, _jobQueue.UrgentQueue);
        Assert.DoesNotContain(100, _jobQueue.InferenceQueue);
    }

    [Fact]
    public async Task SubmitHumanAnswerAsync_PreservesImplementedStatusAndEnqueuesUrgent()
    {
        var task = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 105,
            WorkspaceId = 1,
            Status = TaskOverallStatus.Implemented,
            IsWaitingForHuman = true,
            Questions = new List<Question>
            {
                new()
                {
                    Id = 5,
                    TaskId = 105,
                    Prompt = "Should test assertions include mock verification?",
                    Status = QuestionStatus.Pending
                }
            }
        });
        _depStore.AddTask(105, 1, TaskOverallStatus.Implemented);
        _depStore.SetWaitingForHuman(105, true);

        var orchestrator = CreateOrchestrator();

        await orchestrator.SubmitHumanAnswerAsync(5, "Yes, verify mock calls strictly.");

        var question = await _taskRepo.GetQuestionByIdAsync(5);
        Assert.NotNull(question);
        Assert.Equal("Yes, verify mock calls strictly.", question.Answer);
        Assert.Equal(QuestionStatus.Answered, question.Status);

        var updatedTask = await _taskRepo.GetByIdAsync(105);
        Assert.NotNull(updatedTask);
        Assert.False(updatedTask.IsWaitingForHuman);
        Assert.Equal(TaskOverallStatus.Implemented, updatedTask.Status); // Preserved!

        Assert.Contains(105, _jobQueue.UrgentQueue);
    }

    [Fact]
    public async Task SubmitHumanAnswerAsync_ThenProcessTask_AllowsStageToConsumeAnswerAndSucceed()
    {
        var task = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 101,
            WorkspaceId = 1,
            Status = TaskOverallStatus.Reviewed,
            IsWaitingForHuman = true,
            CurrentStageId = "implementation",
            Questions = new List<Question>
            {
                new()
                {
                    Id = 2,
                    TaskId = 101,
                    Prompt = "Which framework?",
                    Status = QuestionStatus.Pending
                }
            }
        });
        _depStore.AddTask(101, 1, TaskOverallStatus.Reviewed);
        _depStore.SetWaitingForHuman(101, true);

        string? consumedAnswer = null;
        var stage = TestWorkflowPipelines.CreateImplementationStage((ctx, ct) =>
        {
            consumedAnswer = ctx.DataBag.Get<string>(PipelineDataBagKeys.LastHumanAnswer);
            return Task.FromResult(StageExecutionResult.Success());
        });
        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stage });

        var orchestrator = CreateOrchestrator(new DefaultWorkflowPipelineProvider(pipeline));

        // Human answers
        await orchestrator.SubmitHumanAnswerAsync(2, "ASP.NET Core");

        // Orchestrator processes resumed task
        await orchestrator.ProcessTaskAsync(101);

        Assert.NotNull(consumedAnswer);
        Assert.Contains("ASP.NET Core", consumedAnswer);
        var updated = await _taskRepo.GetByIdAsync(101);
        Assert.NotNull(updated);
        // Stage completed (single stage pipeline reached end -> Ready)
        Assert.Equal(TaskOverallStatus.Ready, updated.Status);
    }

    [Fact]
    public async Task RequestChangesAsync_ReRoutesToStage2AndEnqueuesUrgent()
    {
        var task = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 102,
            WorkspaceId = 1,
            Status = TaskOverallStatus.Ready,
            CurrentStageId = StandardStageIds.CodeReviewAndFix
        });
        _depStore.AddTask(102, 1, TaskOverallStatus.Ready);

        var orchestrator = CreateOrchestrator();

        await orchestrator.RequestChangesAsync(102, "Please add XML documentation to all public interfaces and fix indentation.");

        var updated = await _taskRepo.GetByIdAsync(102);
        Assert.NotNull(updated);
        Assert.Equal(TaskOverallStatus.Reviewed, updated.Status);
        Assert.Equal(StandardStageIds.Implementation, updated.CurrentStageId);

        var bag = PipelineDataBag.FromJson(updated.PipelineDataJson);
        Assert.Equal("Please add XML documentation to all public interfaces and fix indentation.", bag.Get<string>(PipelineDataBagKeys.ReviewerFeedback));

        // Enqueued to llm-urgent
        Assert.Contains(102, _jobQueue.UrgentQueue);
    }
}
