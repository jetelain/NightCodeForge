using NightCodeForge.Core.Concurrency;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Tests.Fixtures;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Core.Workflow.Configuration;
using NightCodeForge.Core.Workflow.Stages;

namespace NightCodeForge.Core.Tests;

public class AgentSessionContextPromotionTests
{
    private class FakeAgentHarness : IAgentHarness
    {
        public string HarnessId => "crucible";
        public List<AgentSessionContext> ExecutedSessions { get; } = new();
        public AgentRunResult ResultToReturn { get; set; } = AgentRunResult.Completed(
            new AgentRunMetrics(DurationMs: 50, PromptTokens: 100, CompletionTokens: 50, TotalTokens: 150),
            summary: "Completed by Crucible harness");

        public Task<AgentRunResult> RunStageAsync(AgentSessionContext context, CancellationToken cancellationToken = default)
        {
            ExecutedSessions.Add(context);
            return Task.FromResult(ResultToReturn);
        }
    }

    private class FakeHarnessRegistry : IHarnessRegistry
    {
        private readonly Dictionary<string, IAgentHarness> _map = new(StringComparer.OrdinalIgnoreCase);

        public FakeHarnessRegistry(IAgentHarness harness)
        {
            _map[harness.HarnessId] = harness;
        }

        public IAgentHarness GetHarness(string harnessId) => _map[harnessId];
        public void RegisterHarness(IAgentHarness harness) => _map[harness.HarnessId] = harness;
        public bool TryGetHarness(string harnessId, out IAgentHarness? harness) => _map.TryGetValue(harnessId, out harness);
        public IEnumerable<IAgentHarness> GetAllHarnesses() => _map.Values;
    }

    [Fact]
    public void AgentSessionContext_PromotedProperties_DefaultToNull_WhenOmitted()
    {
        var task = new ForgeTask { Id = 1, Title = "Sample", Description = "Desc" };
        var profile = new AgentProfile { Id = "p1", Name = "Profile 1" };

        var context = new AgentSessionContext
        {
            ForgeTask = task,
            Profile = profile
        };

        Assert.Null(context.TaskMarkdownPath);
        Assert.Null(context.ReviewDiffStat);
        Assert.Null(context.ApprovedPlan);
        Assert.Null(context.ReviewerFeedback);
        Assert.Null(context.PriorDiagnostics);
    }

    [Fact]
    public void AgentSessionContext_PromotedProperties_HoldValues_WhenInitialized()
    {
        var task = new ForgeTask { Id = 1, Title = "Sample", Description = "Desc" };
        var profile = new AgentProfile { Id = "p1", Name = "Profile 1" };
        var diagnostics = new List<string> { "CS0001: Error" };

        var context = new AgentSessionContext
        {
            ForgeTask = task,
            Profile = profile,
            TaskMarkdownPath = "docs/tasks/task-1.md",
            ReviewDiffStat = "file.cs | 2 +-\n 1 file changed, 1 insertion(+), 1 deletion(-)",
            ApprovedPlan = "Step 1: Code",
            ReviewerFeedback = "Needs more tests",
            PriorDiagnostics = diagnostics
        };

        Assert.Equal("docs/tasks/task-1.md", context.TaskMarkdownPath);
        Assert.Equal("file.cs | 2 +-\n 1 file changed, 1 insertion(+), 1 deletion(-)", context.ReviewDiffStat);
        Assert.Equal("Step 1: Code", context.ApprovedPlan);
        Assert.Equal("Needs more tests", context.ReviewerFeedback);
        Assert.Same(diagnostics, context.PriorDiagnostics);
    }

    [Fact]
    public void DefaultBootstrapPromptBuilder_IncludesReviewDiffStat_WhenTemplateHasReviewDiffStat()
    {
        var task = new ForgeTask { Id = 42, Title = "Review Feature", Description = "Implement feature" };
        var pipelineContext = new PipelineContext(task, new Workspace { Id = 1 });
        pipelineContext.DataBag.Set(PipelineDataBagKeys.ReviewDiffStat, "file.cs | 2 +-");

        var stage = new GenericAgenticStage(
            StandardStageIds.CodeReviewAndFix,
            "Code Review",
            new AgenticStageConfig
            {
                PromptTemplate = "Reviewing task #{{ task.id }}\n{{- if context.review_diff_stat }}\nDiff Stat:\n{{ context.review_diff_stat }}\n{{- end }}"
            },
            new ScribanPromptTemplateRenderer(),
            TimeProvider.System);

        var prompt = DefaultBootstrapPromptBuilder.Build(stage, task, pipelineContext);

        Assert.Contains("Diff Stat:\nfile.cs | 2 +-", prompt);
    }

    [Fact]
    public void DefaultBootstrapPromptBuilder_AppendsReviewDiffStat_WhenTemplateDoesNotContainIt()
    {
        var task = new ForgeTask { Id = 42, Title = "Review Feature", Description = "Implement feature" };
        var pipelineContext = new PipelineContext(task, new Workspace { Id = 1 });
        pipelineContext.DataBag.Set(PipelineDataBagKeys.ReviewDiffStat, "file.cs | 2 +-");

        var stage = new GenericAgenticStage(
            StandardStageIds.CodeReviewAndFix,
            "Code Review",
            new AgenticStageConfig
            {
                PromptTemplate = "Reviewing task #{{ task.id }}: {{ task.title }}"
            },
            new ScribanPromptTemplateRenderer(),
            TimeProvider.System);

        var prompt = DefaultBootstrapPromptBuilder.Build(stage, task, pipelineContext);

        Assert.Contains("[Git Diff Summary]:", prompt);
        Assert.Contains("file.cs | 2 +-", prompt);
        Assert.Contains("git diff main...feature/task-42 -- <file-path>", prompt);
    }

    [Fact]
    public async Task TaskOrchestrator_PopulatesPromotedProperties_OnAgentSessionContext()
    {
        var taskRepo = new InMemoryTaskRepository();
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var depStore = new InMemoryTaskDependencyStore();
        var depEngine = new DependencyResolutionEngine(depStore);
        var lockProvider = new WorkspaceLockProvider();
        var gitManager = new FakeGitWorkspaceManager();
        var jobQueue = new FakeTaskJobQueue();

        var workspace = new Workspace
        {
            Id = 1,
            Name = "NCF Test Workspace",
            RepositoryPath = "C:/test/repo",
            Ext4MirrorPath = "/mnt/wsl/mirror",
            DefaultBaseBranch = "main",
            AgentProfiles = new List<AgentProfile>
            {
                new()
                {
                    Id = "task-review",
                    Name = "Task Review Agent",
                    HarnessId = "crucible",
                    ModelName = "test-model",
                    Mode = AgentMode.Full
                }
            }
        };
        await workspaceRepo.CreateAsync(workspace);

        var bag = new PipelineDataBag();
        bag.Set(PipelineDataBagKeys.TaskMarkdownPath, "docs/tasks/task-99.md");
        bag.Set(PipelineDataBagKeys.ReviewDiffStat, "diff-stat summary");
        bag.Set(PipelineDataBagKeys.ApprovedPlan, "Plan A");
        bag.Set(PipelineDataBagKeys.ReviewerFeedback, "Initial notes");
        bag.Set(PipelineDataBagKeys.CompilerErrors, new List<string> { "CS1002" });

        var task = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 99,
            WorkspaceId = 1,
            Status = TaskOverallStatus.Open,
            CurrentStageId = "task-review",
            BaseBranch = "main",
            PipelineDataJson = bag.ToJson()
        });
        depStore.AddTask(99, 1, TaskOverallStatus.Open);

        var fakeHarness = new FakeAgentHarness();
        var harnessRegistry = new FakeHarnessRegistry(fakeHarness);

        var stage1 = TestWorkflowPipelines.CreateReviewStage();
        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stage1 });

        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = workspaceRepo,
            DependencyEngine = depEngine,
            LockProvider = lockProvider,
            GitWorkspaceManager = gitManager,
            PipelineProvider = new DefaultWorkflowPipelineProvider(pipeline),
            JobQueue = jobQueue,
            DependencyStore = depStore,
            HarnessRegistry = harnessRegistry
        }.Build();

        await orchestrator.ProcessTaskAsync(task.Id);

        Assert.Single(fakeHarness.ExecutedSessions);
        var session = fakeHarness.ExecutedSessions[0];

        Assert.Equal("docs/tasks/task-99.md", session.TaskMarkdownPath);
        Assert.Equal("diff-stat summary", session.ReviewDiffStat);
        Assert.Equal("Plan A", session.ApprovedPlan);
        Assert.Equal("Initial notes", session.ReviewerFeedback);
        Assert.NotNull(session.PriorDiagnostics);
        Assert.Contains("CS1002", session.PriorDiagnostics);
    }

    [Fact]
    public async Task TaskOrchestrator_PropagatesReviewerFeedbackAndProposals_FromAgentRunResult_IntoDataBag()
    {
        var taskRepo = new InMemoryTaskRepository();
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var depStore = new InMemoryTaskDependencyStore();
        var depEngine = new DependencyResolutionEngine(depStore);
        var lockProvider = new WorkspaceLockProvider();
        var gitManager = new FakeGitWorkspaceManager();
        var jobQueue = new FakeTaskJobQueue();

        var workspace = new Workspace
        {
            Id = 1,
            Name = "NCF Test Workspace",
            RepositoryPath = "C:/test/repo",
            Ext4MirrorPath = "/mnt/wsl/mirror",
            DefaultBaseBranch = "main",
            AgentProfiles = new List<AgentProfile>
            {
                new()
                {
                    Id = "task-review",
                    Name = "Task Review Agent",
                    HarnessId = "crucible",
                    ModelName = "test-model",
                    Mode = AgentMode.Full
                }
            }
        };
        await workspaceRepo.CreateAsync(workspace);

        var task = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 101,
            WorkspaceId = 1,
            Status = TaskOverallStatus.Open,
            CurrentStageId = "task-review",
            BaseBranch = "main"
        });
        depStore.AddTask(101, 1, TaskOverallStatus.Open);

        var proposalList = new List<TaskProposal>
        {
            new("Discovered Bug", "Fix edge-case in parser", 2)
        };

        var fakeHarness = new FakeAgentHarness
        {
            ResultToReturn = AgentRunResult.Completed(
                new AgentRunMetrics(50, 10, 10, 20),
                summary: "Review finished with recommendations",
                reviewerFeedback: "Needs null check in line 42",
                proposals: proposalList)
        };
        var harnessRegistry = new FakeHarnessRegistry(fakeHarness);

        var stage1 = TestWorkflowPipelines.CreateReviewStage();
        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stage1 });

        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = workspaceRepo,
            DependencyEngine = depEngine,
            LockProvider = lockProvider,
            GitWorkspaceManager = gitManager,
            PipelineProvider = new DefaultWorkflowPipelineProvider(pipeline),
            JobQueue = jobQueue,
            DependencyStore = depStore,
            HarnessRegistry = harnessRegistry
        }.Build();

        await orchestrator.ProcessTaskAsync(task.Id);

        var updatedTask = await taskRepo.GetByIdAsync(101);
        Assert.NotNull(updatedTask);
        var bag = PipelineDataBag.FromJson(updatedTask.PipelineDataJson);

        // Feedback saved in DataBag
        Assert.Equal("Needs null check in line 42", bag.Get<string>(PipelineDataBagKeys.ReviewerFeedback));

        // Proposed tasks auto-created as Suggested tasks in repository
        var suggestedTasks = await taskRepo.GetByWorkspaceIdAsync(1);
        var createdSuggested = suggestedTasks.FirstOrDefault(t => t.Title == "Discovered Bug");
        Assert.NotNull(createdSuggested);
        Assert.Equal(TaskOverallStatus.Suggested, createdSuggested.Status);
        Assert.Equal("Fix edge-case in parser", createdSuggested.Description);
    }
}
