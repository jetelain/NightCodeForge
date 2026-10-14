using NightCodeForge.Core.Concurrency;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Core.Tests;

public class QualityGateLoopbackTests
{
    private class FakeAgentHarness : IAgentHarness
    {
        public string HarnessId { get; set; } = "crucible";
        public List<AgentSessionContext> ExecutedSessions { get; } = new();
        public AgentRunResult ResultToReturn { get; set; } = AgentRunResult.Completed(
            new AgentRunMetrics(50, 100, 50, 150),
            summary: "Stage 1 generated architecture plan");

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
    public async Task Orchestrator_RoutesBackwardToTargetStage_OnQualityGateFailure()
    {
        // Arrange
        var issue = new Issue
        {
            Id = 10,
            WorkspaceId = 1,
            Title = "Feature X",
            Description = "Implement feature X",
            CurrentStageId = "quality-gate",
            Status = IssueOverallStatus.Open
        };

        var workspace = new Workspace
        {
            Id = 1,
            Name = "TestRepo",
            RepositoryPath = Path.GetTempPath(),
            ExecutionMode = ExecutionMode.BareHost
        };

        var issueRepo = new InMemoryIssueRepository();
        await issueRepo.CreateAsync(issue);
        var workspaceRepo = new InMemoryWorkspaceRepository();
        await workspaceRepo.CreateAsync(workspace);
        var depStore = new InMemoryIssueDependencyStore();
        depStore.AddIssue(10, 1, IssueOverallStatus.Open);
        var depEngine = new DependencyResolutionEngine(depStore);
        var lockProvider = new WorkspaceLockProvider();
        var jobQueue = new FakeIssueJobQueue();
        var gitManager = new FakeGitWorkspaceManager();

        var harness = new FakeAgentHarness();
        var harnessRegistry = new FakeHarnessRegistry(harness);

        var pipeline = new WorkflowPipeline(new IWorkflowStage[]
        {
            new TestWorkflowStage("implementation", WorkflowStageKind.Agentic),
            new TestWorkflowStage("quality-gate", WorkflowStageKind.Deterministic)
            {
                Handler = (ctx, ct) => Task.FromResult(
                    StageExecutionResult.Failure("Unit tests failed: 2 broken", loopBackStageId: "implementation"))
            }
        });

        var orchestrator = new IssueOrchestrator(
            issueRepo,
            workspaceRepo,
            depEngine,
            lockProvider,
            gitManager,
            new DefaultWorkflowPipelineProvider(pipeline),
            jobQueue,
            depStore,
            harnessRegistry: harnessRegistry);

        // Act
        await orchestrator.ProcessIssueAsync(issue.Id);

        // Assert
        Assert.Equal("implementation", issue.CurrentStageId);
        Assert.Equal(IssueOverallStatus.Open, issue.Status);
        Assert.Single(jobQueue.UrgentQueue);
        Assert.Equal(10, jobQueue.UrgentQueue[0]);

        var bag = PipelineDataBag.FromJson(issue.PipelineDataJson);
        var stageRetries = bag.Get<Dictionary<string, int>>("Workflow.StageRetries");
        Assert.NotNull(stageRetries);
        Assert.Equal(1, stageRetries["quality-gate"]);

        var diagnostics = bag.Get<List<string>>("diagnostics.compiler_errors");
        Assert.NotNull(diagnostics);
        Assert.Single(diagnostics);
        Assert.Contains("Unit tests failed", diagnostics[0]);
    }

    [Fact]
    public async Task Orchestrator_FailsPermanently_WhenMaxRetriesExceeded()
    {
        // Arrange
        var issue = new Issue
        {
            Id = 11,
            WorkspaceId = 1,
            Title = "Feature Y",
            Description = "Implement feature Y",
            CurrentStageId = "quality-gate",
            Status = IssueOverallStatus.Open
        };

        var initialBag = new PipelineDataBag();
        initialBag.Set("Workflow.MaxRetries", 2);
        initialBag.Set("Workflow.StageRetries", new Dictionary<string, int> { ["quality-gate"] = 2 });
        issue.PipelineDataJson = initialBag.ToJson();

        var workspace = new Workspace
        {
            Id = 1,
            Name = "TestRepo",
            RepositoryPath = Path.GetTempPath(),
            ExecutionMode = ExecutionMode.BareHost
        };

        var issueRepo = new InMemoryIssueRepository();
        await issueRepo.CreateAsync(issue);
        var workspaceRepo = new InMemoryWorkspaceRepository();
        await workspaceRepo.CreateAsync(workspace);
        var depStore = new InMemoryIssueDependencyStore();
        depStore.AddIssue(11, 1, IssueOverallStatus.Open);
        var depEngine = new DependencyResolutionEngine(depStore);
        var lockProvider = new WorkspaceLockProvider();
        var jobQueue = new FakeIssueJobQueue();
        var gitManager = new FakeGitWorkspaceManager();

        var pipeline = new WorkflowPipeline(new IWorkflowStage[]
        {
            new TestWorkflowStage("implementation", WorkflowStageKind.Agentic),
            new TestWorkflowStage("quality-gate", WorkflowStageKind.Deterministic)
            {
                Handler = (ctx, ct) => Task.FromResult(
                    StageExecutionResult.Failure("Build broken", loopBackStageId: "implementation"))
            }
        });

        var orchestrator = new IssueOrchestrator(
            issueRepo,
            workspaceRepo,
            depEngine,
            lockProvider,
            gitManager,
            new DefaultWorkflowPipelineProvider(pipeline),
            jobQueue,
            depStore);

        // Act
        await orchestrator.ProcessIssueAsync(issue.Id);

        // Assert
        Assert.Equal(IssueOverallStatus.Failed, issue.Status);
        Assert.Empty(jobQueue.UrgentQueue);
    }

    [Fact]
    public async Task Orchestrator_InjectsStage1ApprovedPlanAndPriorQa_IntoBootstrapPrompt()
    {
        // Arrange
        var issue = new Issue
        {
            Id = 12,
            WorkspaceId = 1,
            Title = "Feature Z",
            Description = "Implement feature Z",
            CurrentStageId = "implementation",
            Status = IssueOverallStatus.Open
        };

        issue.Questions.Add(new Question
        {
            Id = 1,
            IssueId = 12,
            Prompt = "Which database driver should we use?",
            Answer = "Use Microsoft.Data.Sqlite.",
            Status = QuestionStatus.Answered
        });

        var bag = new PipelineDataBag();
        bag.Set("Stage1.ApprovedPlan", "1. Create models\n2. Add repository\n3. Add tests");
        issue.PipelineDataJson = bag.ToJson();

        var workspace = new Workspace
        {
            Id = 1,
            Name = "TestRepo",
            RepositoryPath = Path.GetTempPath(),
            ExecutionMode = ExecutionMode.BareHost
        };

        var issueRepo = new InMemoryIssueRepository();
        await issueRepo.CreateAsync(issue);
        var workspaceRepo = new InMemoryWorkspaceRepository();
        await workspaceRepo.CreateAsync(workspace);
        var depStore = new InMemoryIssueDependencyStore();
        depStore.AddIssue(12, 1, IssueOverallStatus.Open);
        var depEngine = new DependencyResolutionEngine(depStore);
        var lockProvider = new WorkspaceLockProvider();
        var jobQueue = new FakeIssueJobQueue();
        var gitManager = new FakeGitWorkspaceManager();

        var harness = new FakeAgentHarness();
        var harnessRegistry = new FakeHarnessRegistry(harness);

        var pipeline = new WorkflowPipeline(new IWorkflowStage[]
        {
            new TestWorkflowStage("implementation", WorkflowStageKind.Agentic)
        });

        var orchestrator = new IssueOrchestrator(
            issueRepo,
            workspaceRepo,
            depEngine,
            lockProvider,
            gitManager,
            new DefaultWorkflowPipelineProvider(pipeline),
            jobQueue,
            depStore,
            harnessRegistry: harnessRegistry);

        // Act
        await orchestrator.ProcessIssueAsync(issue.Id);

        // Assert
        Assert.Single(harness.ExecutedSessions);
        var prompt = harness.ExecutedSessions[0].BootstrapPrompt;
        Assert.Contains("[Approved Implementation Plan]:", prompt);
        Assert.Contains("1. Create models", prompt);
        Assert.Contains("[Human Clarifications & Answers]:", prompt);
        Assert.Contains("Which database driver should we use?", prompt);
        Assert.Contains("Use Microsoft.Data.Sqlite.", prompt);
    }

    [Fact]
    public async Task Orchestrator_SavesCheckpoint_OnStageSuccess()
    {
        // Arrange
        var issue = new Issue
        {
            Id = 13,
            WorkspaceId = 1,
            Title = "Feature Checkpoint",
            Description = "Testing checkpoints",
            CurrentStageId = "stage1",
            Status = IssueOverallStatus.Open
        };

        var workspace = new Workspace
        {
            Id = 1,
            Name = "TestRepo",
            RepositoryPath = Path.GetTempPath(),
            ExecutionMode = ExecutionMode.BareHost
        };

        var issueRepo = new InMemoryIssueRepository();
        await issueRepo.CreateAsync(issue);
        var workspaceRepo = new InMemoryWorkspaceRepository();
        await workspaceRepo.CreateAsync(workspace);
        var depStore = new InMemoryIssueDependencyStore();
        depStore.AddIssue(13, 1, IssueOverallStatus.Open);
        var depEngine = new DependencyResolutionEngine(depStore);
        var lockProvider = new WorkspaceLockProvider();
        var jobQueue = new FakeIssueJobQueue();
        var gitManager = new FakeGitWorkspaceManager();

        var pipeline = new WorkflowPipeline(new IWorkflowStage[]
        {
            new TestWorkflowStage("stage1", WorkflowStageKind.Deterministic)
            {
                Handler = (ctx, ct) => Task.FromResult(StageExecutionResult.Success())
            },
            new TestWorkflowStage("stage2", WorkflowStageKind.Deterministic)
        });

        var orchestrator = new IssueOrchestrator(
            issueRepo,
            workspaceRepo,
            depEngine,
            lockProvider,
            gitManager,
            new DefaultWorkflowPipelineProvider(pipeline),
            jobQueue,
            depStore);

        // Act
        await orchestrator.ProcessIssueAsync(issue.Id);

        // Assert
        var checkpoints = issue.Checkpoints.ToList();
        Assert.Single(checkpoints);
        Assert.Equal("stage1", checkpoints[0].StageId);
        Assert.Equal("stage2", issue.CurrentStageId);
    }

    [Fact]
    public async Task Orchestrator_RoutesBackwardToImplementation_WhenReviewAgentReturnsTargetRouting()
    {
        // Arrange
        var issue = new Issue
        {
            Id = 99,
            WorkspaceId = 1,
            Title = "Feature Z",
            Description = "Implement feature Z",
            CurrentStageId = "code-review-fix",
            Status = IssueOverallStatus.Implemented
        };

        var workspace = new Workspace
        {
            Id = 1,
            Name = "TestRepo",
            RepositoryPath = Path.GetTempPath(),
            ExecutionMode = ExecutionMode.BareHost
        };

        var issueRepo = new InMemoryIssueRepository();
        await issueRepo.CreateAsync(issue);
        var workspaceRepo = new InMemoryWorkspaceRepository();
        await workspaceRepo.CreateAsync(workspace);
        var depStore = new InMemoryIssueDependencyStore();
        depStore.AddIssue(99, 1, IssueOverallStatus.Implemented);
        var depEngine = new DependencyResolutionEngine(depStore);
        var lockProvider = new WorkspaceLockProvider();
        var jobQueue = new FakeIssueJobQueue();
        var gitManager = new FakeGitWorkspaceManager();

        var fakeHarness = new FakeAgentHarness
        {
            ResultToReturn = AgentRunResult.Completed(
                new AgentRunMetrics(100),
                summary: "Reviewer requested changes: AuthService incomplete",
                targetRouting: "implementation")
        };
        var harnessRegistry = new FakeHarnessRegistry(fakeHarness);

        var pipeline = new WorkflowPipeline(new IWorkflowStage[]
        {
            new TestWorkflowStage("implementation", WorkflowStageKind.Agentic),
            new TestWorkflowStage("code-review-fix", WorkflowStageKind.Agentic, AgentMode.Review)
        });

        var orchestrator = new IssueOrchestrator(
            issueRepo,
            workspaceRepo,
            depEngine,
            lockProvider,
            gitManager,
            new DefaultWorkflowPipelineProvider(pipeline),
            jobQueue,
            depStore,
            harnessRegistry: harnessRegistry);

        // Act
        await orchestrator.ProcessIssueAsync(issue.Id);

        // Assert
        Assert.Equal("implementation", issue.CurrentStageId);
        Assert.Single(jobQueue.UrgentQueue);
        Assert.Equal(99, jobQueue.UrgentQueue[0]);

        var bag = PipelineDataBag.FromJson(issue.PipelineDataJson);
        var stageRetries = bag.Get<Dictionary<string, int>>("Workflow.StageRetries");
        Assert.NotNull(stageRetries);
        Assert.Equal(1, stageRetries["code-review-fix"]);
    }
}
