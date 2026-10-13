using NightCodeForge.Core.Concurrency;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Core.Workflow.Stages;

namespace NightCodeForge.Core.Tests;

public class ResumptionTests
{
    private readonly InMemoryIssueRepository _issueRepo;
    private readonly InMemoryWorkspaceRepository _workspaceRepo;
    private readonly InMemoryIssueDependencyStore _depStore;
    private readonly DependencyResolutionEngine _depEngine;
    private readonly WorkspaceLockProvider _lockProvider;
    private readonly FakeGitWorkspaceManager _gitManager;
    private readonly FakeIssueJobQueue _jobQueue;
    private readonly Workspace _workspace;

    public ResumptionTests()
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
    public async Task SubmitHumanAnswerAsync_PersistsAnswerUpdatesStatusAndEnqueuesUrgent()
    {
        var issue = await _issueRepo.CreateAsync(new Issue
        {
            Id = 100,
            WorkspaceId = 1,
            Status = IssueOverallStatus.BlockedQuestion,
            Questions = new List<Question>
            {
                new()
                {
                    Id = 1,
                    IssueId = 100,
                    Prompt = "What port should the server listen on?",
                    Status = QuestionStatus.Pending
                }
            }
        });
        _depStore.AddIssue(100, 1, IssueOverallStatus.BlockedQuestion);

        var orchestrator = new IssueOrchestrator(
            _issueRepo, _workspaceRepo, _depEngine, _lockProvider,
            _gitManager, new DefaultWorkflowPipelineProvider(), _jobQueue, _depStore);

        await orchestrator.SubmitHumanAnswerAsync(1, "Port 8080");

        var question = await _issueRepo.GetQuestionByIdAsync(1);
        Assert.NotNull(question);
        Assert.Equal("Port 8080", question.Answer);
        Assert.Equal(QuestionStatus.Answered, question.Status);
        Assert.NotNull(question.AnsweredAt);

        var updatedIssue = await _issueRepo.GetByIdAsync(100);
        Assert.NotNull(updatedIssue);
        Assert.Equal(IssueOverallStatus.Pending, updatedIssue.Status);

        var bag = PipelineDataBag.FromJson(updatedIssue.PipelineDataJson);
        Assert.Equal("Port 8080", bag.Get<string>("LastHumanAnswer"));

        // Enqueued to llm-urgent
        Assert.Contains(100, _jobQueue.UrgentQueue);
        Assert.DoesNotContain(100, _jobQueue.InferenceQueue);
    }

    [Fact]
    public async Task SubmitHumanAnswerAsync_ThenProcessIssue_AllowsStageToConsumeAnswerAndSucceed()
    {
        var issue = await _issueRepo.CreateAsync(new Issue
        {
            Id = 101,
            WorkspaceId = 1,
            Status = IssueOverallStatus.BlockedQuestion,
            CurrentStageId = ImplementationStage.DefaultStageId,
            Questions = new List<Question>
            {
                new()
                {
                    Id = 2,
                    IssueId = 101,
                    Prompt = "Which framework?",
                    Status = QuestionStatus.Pending
                }
            }
        });
        _depStore.AddIssue(101, 1, IssueOverallStatus.Pending);

        string? consumedAnswer = null;
        var stage = new ImplementationStage
        {
            Handler = (ctx, ct) =>
            {
                consumedAnswer = ctx.DataBag.Get<string>("LastHumanAnswer");
                return Task.FromResult(StageExecutionResult.Success());
            }
        };
        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stage });

        var orchestrator = new IssueOrchestrator(
            _issueRepo, _workspaceRepo, _depEngine, _lockProvider,
            _gitManager, new DefaultWorkflowPipelineProvider(pipeline), _jobQueue, _depStore);

        // Human answers
        await orchestrator.SubmitHumanAnswerAsync(2, "ASP.NET Core");

        // Orchestrator processes resumed issue
        await orchestrator.ProcessIssueAsync(101);

        Assert.Equal("ASP.NET Core", consumedAnswer);
        var updated = await _issueRepo.GetByIdAsync(101);
        Assert.NotNull(updated);
        // Stage completed (single stage pipeline reached end -> Review)
        Assert.Equal(IssueOverallStatus.Review, updated.Status);
    }

    [Fact]
    public async Task RequestChangesAsync_ReRoutesToStage2AndEnqueuesUrgent()
    {
        var issue = await _issueRepo.CreateAsync(new Issue
        {
            Id = 102,
            WorkspaceId = 1,
            Status = IssueOverallStatus.Review,
            CurrentStageId = "code-review-fix"
        });
        _depStore.AddIssue(102, 1, IssueOverallStatus.Review);

        var orchestrator = new IssueOrchestrator(
            _issueRepo, _workspaceRepo, _depEngine, _lockProvider,
            _gitManager, new DefaultWorkflowPipelineProvider(), _jobQueue, _depStore);

        await orchestrator.RequestChangesAsync(102, "Please add XML documentation to all public interfaces and fix indentation.");

        var updated = await _issueRepo.GetByIdAsync(102);
        Assert.NotNull(updated);
        Assert.Equal(IssueOverallStatus.Pending, updated.Status);
        Assert.Equal(ImplementationStage.DefaultStageId, updated.CurrentStageId);

        var bag = PipelineDataBag.FromJson(updated.PipelineDataJson);
        Assert.Equal("Please add XML documentation to all public interfaces and fix indentation.", bag.Get<string>("ReviewerFeedback"));

        // Enqueued to llm-urgent
        Assert.Contains(102, _jobQueue.UrgentQueue);
    }
}
