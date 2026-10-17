using NightCodeForge.Core.Concurrency;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Tests.Fixtures;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Core.Tests;

public class QualityGateLoopbackTests
{
    private class FakeAgentHarness : IAgentHarness
    {
        public string HarnessId { get; set; } = StandardHarnessIds.Crucible;
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
        var task = new ForgeTask
        {
            Id = 10,
            WorkspaceId = 1,
            Title = "Feature X",
            Description = "Implement feature X",
            CurrentStageId = "quality-gate",
            Status = TaskOverallStatus.Open
        };

        var workspace = new Workspace
        {
            Id = 1,
            Name = "TestRepo",
            RepositoryPath = Path.GetTempPath(),
            ExecutionMode = ExecutionMode.BareHost
        };

        var taskRepo = new InMemoryTaskRepository();
        await taskRepo.CreateAsync(task);
        var workspaceRepo = new InMemoryWorkspaceRepository();
        await workspaceRepo.CreateAsync(workspace);
        var depStore = new InMemoryTaskDependencyStore();
        depStore.AddTask(10, 1, TaskOverallStatus.Open);
        var depEngine = new DependencyResolutionEngine(depStore);
        var lockProvider = new WorkspaceLockProvider();
        var jobQueue = new FakeTaskJobQueue();
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

        // Act
        await orchestrator.ProcessTaskAsync(task.Id);

        // Assert
        Assert.Equal("implementation", task.CurrentStageId);
        Assert.Equal(TaskOverallStatus.Open, task.Status);
        Assert.Single(jobQueue.UrgentQueue);
        Assert.Equal(10, jobQueue.UrgentQueue[0]);

        var bag = PipelineDataBag.FromJson(task.PipelineDataJson);
        var stageRetries = bag.Get<Dictionary<string, int>>(PipelineDataBagKeys.StageRetries);
        Assert.NotNull(stageRetries);
        Assert.Equal(1, stageRetries["quality-gate"]);

        var diagnostics = bag.Get<List<string>>(PipelineDataBagKeys.CompilerErrors);
        Assert.NotNull(diagnostics);
        Assert.Single(diagnostics);
        Assert.Contains("Unit tests failed", diagnostics[0]);
    }

    [Fact]
    public async Task Orchestrator_FailsPermanently_WhenMaxRetriesExceeded()
    {
        // Arrange
        var task = new ForgeTask
        {
            Id = 11,
            WorkspaceId = 1,
            Title = "Feature Y",
            Description = "Implement feature Y",
            CurrentStageId = "quality-gate",
            Status = TaskOverallStatus.Open
        };

        var initialBag = new PipelineDataBag();
        initialBag.Set(PipelineDataBagKeys.MaxRetries, 2);
        initialBag.Set(PipelineDataBagKeys.StageRetries, new Dictionary<string, int> { ["quality-gate"] = 2 });
        task.PipelineDataJson = initialBag.ToJson();

        var workspace = new Workspace
        {
            Id = 1,
            Name = "TestRepo",
            RepositoryPath = Path.GetTempPath(),
            ExecutionMode = ExecutionMode.BareHost
        };

        var taskRepo = new InMemoryTaskRepository();
        await taskRepo.CreateAsync(task);
        var workspaceRepo = new InMemoryWorkspaceRepository();
        await workspaceRepo.CreateAsync(workspace);
        var depStore = new InMemoryTaskDependencyStore();
        depStore.AddTask(11, 1, TaskOverallStatus.Open);
        var depEngine = new DependencyResolutionEngine(depStore);
        var lockProvider = new WorkspaceLockProvider();
        var jobQueue = new FakeTaskJobQueue();
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

        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = workspaceRepo,
            DependencyEngine = depEngine,
            LockProvider = lockProvider,
            GitWorkspaceManager = gitManager,
            PipelineProvider = new DefaultWorkflowPipelineProvider(pipeline),
            JobQueue = jobQueue,
            DependencyStore = depStore
        }.Build();

        // Act
        await orchestrator.ProcessTaskAsync(task.Id);

        // Assert
        Assert.Equal(TaskOverallStatus.Failed, task.Status);
        Assert.Empty(jobQueue.UrgentQueue);
    }

    [Fact]
    public async Task Orchestrator_InjectsStage1ApprovedPlanAndPriorQa_IntoBootstrapPrompt()
    {
        // Arrange
        var task = new ForgeTask
        {
            Id = 12,
            WorkspaceId = 1,
            Title = "Feature Z",
            Description = "Implement feature Z",
            CurrentStageId = "implementation",
            Status = TaskOverallStatus.Open
        };

        task.Questions.Add(new Question
        {
            Id = 1,
            TaskId = 12,
            Prompt = "Which database driver should we use?",
            Answer = "Use Microsoft.Data.Sqlite.",
            Status = QuestionStatus.Answered
        });

        var bag = new PipelineDataBag();
        bag.Set(PipelineDataBagKeys.ApprovedPlan, "1. Create models\n2. Add repository\n3. Add tests");
        task.PipelineDataJson = bag.ToJson();

        var workspace = new Workspace
        {
            Id = 1,
            Name = "TestRepo",
            RepositoryPath = Path.GetTempPath(),
            ExecutionMode = ExecutionMode.BareHost
        };

        var taskRepo = new InMemoryTaskRepository();
        await taskRepo.CreateAsync(task);
        var workspaceRepo = new InMemoryWorkspaceRepository();
        workspaceRepo.SeedGlobalProfiles(AgentProfile.CreateStandardGlobalProfiles("test-model"));
        await workspaceRepo.CreateAsync(workspace);
        var depStore = new InMemoryTaskDependencyStore();
        depStore.AddTask(12, 1, TaskOverallStatus.Open);
        var depEngine = new DependencyResolutionEngine(depStore);
        var lockProvider = new WorkspaceLockProvider();
        var jobQueue = new FakeTaskJobQueue();
        var gitManager = new FakeGitWorkspaceManager();

        var harness = new FakeAgentHarness();
        var harnessRegistry = new FakeHarnessRegistry(harness);

        var pipeline = new WorkflowPipeline(new IWorkflowStage[]
        {
            new TestWorkflowStage("implementation", WorkflowStageKind.Agentic)
        });

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

        // Act
        await orchestrator.ProcessTaskAsync(task.Id);

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
        var task = new ForgeTask
        {
            Id = 13,
            WorkspaceId = 1,
            Title = "Feature Checkpoint",
            Description = "Testing checkpoints",
            CurrentStageId = "stage1",
            Status = TaskOverallStatus.Open
        };

        var workspace = new Workspace
        {
            Id = 1,
            Name = "TestRepo",
            RepositoryPath = Path.GetTempPath(),
            ExecutionMode = ExecutionMode.BareHost
        };

        var taskRepo = new InMemoryTaskRepository();
        await taskRepo.CreateAsync(task);
        var workspaceRepo = new InMemoryWorkspaceRepository();
        await workspaceRepo.CreateAsync(workspace);
        var depStore = new InMemoryTaskDependencyStore();
        depStore.AddTask(13, 1, TaskOverallStatus.Open);
        var depEngine = new DependencyResolutionEngine(depStore);
        var lockProvider = new WorkspaceLockProvider();
        var jobQueue = new FakeTaskJobQueue();
        var gitManager = new FakeGitWorkspaceManager();

        var pipeline = new WorkflowPipeline(new IWorkflowStage[]
        {
            new TestWorkflowStage("stage1", WorkflowStageKind.Deterministic)
            {
                Handler = (ctx, ct) => Task.FromResult(StageExecutionResult.Success())
            },
            new TestWorkflowStage("stage2", WorkflowStageKind.Deterministic)
        });

        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = workspaceRepo,
            DependencyEngine = depEngine,
            LockProvider = lockProvider,
            GitWorkspaceManager = gitManager,
            PipelineProvider = new DefaultWorkflowPipelineProvider(pipeline),
            JobQueue = jobQueue,
            DependencyStore = depStore
        }.Build();

        // Act
        await orchestrator.ProcessTaskAsync(task.Id);

        // Assert
        var checkpoints = task.Checkpoints.ToList();
        Assert.Single(checkpoints);
        Assert.Equal("stage1", checkpoints[0].StageId);
        Assert.Equal("stage2", task.CurrentStageId);
    }

    [Fact]
    public async Task Orchestrator_RoutesBackwardToImplementation_WhenReviewAgentReturnsTargetRouting()
    {
        // Arrange
        var task = new ForgeTask
        {
            Id = 99,
            WorkspaceId = 1,
            Title = "Feature Z",
            Description = "Implement feature Z",
            CurrentStageId = "code-review-fix",
            Status = TaskOverallStatus.Implemented
        };

        var workspace = new Workspace
        {
            Id = 1,
            Name = "TestRepo",
            RepositoryPath = Path.GetTempPath(),
            ExecutionMode = ExecutionMode.BareHost
        };

        var taskRepo = new InMemoryTaskRepository();
        await taskRepo.CreateAsync(task);
        var workspaceRepo = new InMemoryWorkspaceRepository();
        workspaceRepo.SeedGlobalProfiles(AgentProfile.CreateStandardGlobalProfiles("test-model"));
        await workspaceRepo.CreateAsync(workspace);
        var depStore = new InMemoryTaskDependencyStore();
        depStore.AddTask(99, 1, TaskOverallStatus.Implemented);
        var depEngine = new DependencyResolutionEngine(depStore);
        var lockProvider = new WorkspaceLockProvider();
        var jobQueue = new FakeTaskJobQueue();
        var gitManager = new FakeGitWorkspaceManager();

        var fakeHarness = new FakeAgentHarness
        {
            ResultToReturn = AgentRunResult.Completed(
                new AgentRunMetrics(100),
                summary: "Reviewer requested changes: AuthService incomplete",
                targetRouting: StandardStageIds.Implementation)
        };
        var harnessRegistry = new FakeHarnessRegistry(fakeHarness);

        var pipeline = new WorkflowPipeline(new IWorkflowStage[]
        {
            new TestWorkflowStage(StandardStageIds.Implementation, WorkflowStageKind.Agentic),
            new TestWorkflowStage(StandardStageIds.CodeReviewAndFix, WorkflowStageKind.Agentic, AgentMode.Review)
        });

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

        // Act
        await orchestrator.ProcessTaskAsync(task.Id);

        // Assert
        Assert.Equal(StandardStageIds.Implementation, task.CurrentStageId);
        Assert.Single(jobQueue.UrgentQueue);
        Assert.Equal(99, jobQueue.UrgentQueue[0]);

        var bag = PipelineDataBag.FromJson(task.PipelineDataJson);
        var stageRetries = bag.Get<Dictionary<string, int>>(PipelineDataBagKeys.StageRetries);
        Assert.NotNull(stageRetries);
        Assert.Equal(1, stageRetries[StandardStageIds.CodeReviewAndFix]);
    }
}
