using NightCodeForge.Core.Concurrency;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Tests.Fixtures;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Core.Workflow.Configuration;
using NightCodeForge.Core.Workflow.Stages;
using Xunit;

namespace NightCodeForge.Core.Tests;

public class TaskOrchestratorPromptRenderingTests
{
    private class FakeAgentHarness : IAgentHarness
    {
        public string HarnessId => "crucible";
        public List<AgentSessionContext> ExecutedSessions { get; } = new();

        public Task<AgentRunResult> RunStageAsync(AgentSessionContext context, CancellationToken cancellationToken = default)
        {
            ExecutedSessions.Add(context);
            return Task.FromResult(AgentRunResult.Completed(
                new AgentRunMetrics(50, 100, 50, 150),
                summary: "Stage completed successfully"));
        }
    }

    private class SingleStagePipeline : IWorkflowPipeline
    {
        private readonly IWorkflowStage _stage;
        public IReadOnlyList<IWorkflowStage> Stages => new[] { _stage };

        public SingleStagePipeline(IWorkflowStage stage)
        {
            _stage = stage;
        }

        public IWorkflowStage? GetInitialStage() => _stage;
        public IWorkflowStage? GetStage(string stageId) => string.Equals(stageId, _stage.StageId, StringComparison.OrdinalIgnoreCase) ? _stage : null;
        public IWorkflowStage? GetNextStage(string currentStageId) => null;
    }

    private class SingleStagePipelineProvider : IWorkflowPipelineProvider
    {
        private readonly IWorkflowPipeline _pipeline;
        public SingleStagePipelineProvider(IWorkflowPipeline pipeline) => _pipeline = pipeline;
        public Task<IWorkflowPipeline> GetPipelineAsync(ForgeTask task, CancellationToken cancellationToken = default) => Task.FromResult(_pipeline);
    }

    private class SingleHarnessRegistry : IHarnessRegistry
    {
        private readonly IAgentHarness _harness;
        public SingleHarnessRegistry(IAgentHarness harness) => _harness = harness;
        public IAgentHarness GetHarness(string harnessId) => _harness;
        public void RegisterHarness(IAgentHarness harness) { }
        public bool TryGetHarness(string harnessId, out IAgentHarness? harness)
        {
            harness = _harness;
            return true;
        }
        public IEnumerable<IAgentHarness> GetAllHarnesses() => new[] { _harness };
    }

    [Fact]
    public async Task ProcessTaskAsync_WithGenericAgenticStage_RendersScribanPromptTemplate()
    {
        var taskRepo = new InMemoryTaskRepository();
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var depStore = new InMemoryTaskDependencyStore();
        var depEngine = new DependencyResolutionEngine(depStore);
        var lockProvider = new WorkspaceLockProvider();
        var gitManager = new FakeGitWorkspaceManager();
        var jobQueue = new FakeTaskJobQueue();
        var fakeHarness = new FakeAgentHarness();
        var harnessRegistry = new SingleHarnessRegistry(fakeHarness);

        var workspace = new Workspace
        {
            Id = 1,
            Name = "Test Workspace",
            RepositoryPath = "C:/test/repo",
            DefaultBaseBranch = "main",
            AgentProfiles = new List<AgentProfile>
            {
                new AgentProfile { Id = "default", HarnessId = "crucible", ModelName = "test-model" }
            }
        };
        await workspaceRepo.CreateAsync(workspace);

        var task = new ForgeTask
        {
            Id = 101,
            WorkspaceId = 1,
            Title = "Add User Authentication",
            Description = "Support OAuth2 login with GitHub",
            Status = TaskOverallStatus.Open,
            CurrentStageId = "custom-agentic",
            Questions = new List<Question>
            {
                new Question
                {
                    Prompt = "Which providers are needed?",
                    Answer = "GitHub only",
                    Status = QuestionStatus.Answered
                }
            }
        };
        await taskRepo.CreateAsync(task);
        depStore.AddTask(task.Id, workspace.Id, TaskOverallStatus.Open);

        var config = new AgenticStageConfig
        {
            StageDirective = "You are a secure authentication specialist.",
            PromptTemplate = """
            Execute stage '{{ stage.display_name }}' for task #{{ task.id }}: {{ task.title }}
            {{ task.description }}
            {{- if context.answered_questions }}

            [Human Clarifications & Answers]:
            {{ for qa in context.answered_questions }}
            Q: {{ qa.prompt }}
            A: {{ qa.answer }}
            {{ end }}
            {{- end }}
            """
        };

        var genericStage = new GenericAgenticStage("custom-agentic", "Authentication Designer", config);
        var pipeline = new SingleStagePipeline(genericStage);
        var pipelineProvider = new SingleStagePipelineProvider(pipeline);

        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = workspaceRepo,
            DependencyEngine = depEngine,
            LockProvider = lockProvider,
            GitWorkspaceManager = gitManager,
            PipelineProvider = pipelineProvider,
            JobQueue = jobQueue,
            DependencyStore = depStore,
            HarnessRegistry = harnessRegistry
        }.Build();

        await orchestrator.ProcessTaskAsync(task.Id);

        Assert.Single(fakeHarness.ExecutedSessions);
        var prompt = fakeHarness.ExecutedSessions[0].BootstrapPrompt;

        Assert.Contains("Execute stage 'Authentication Designer' for task #101: Add User Authentication", prompt);
        Assert.Contains("Support OAuth2 login with GitHub", prompt);
        Assert.Contains("[Human Clarifications & Answers]:", prompt);
        Assert.Contains("Q: Which providers are needed?\nA: GitHub only", prompt);
    }

    [Fact]
    public async Task ProcessTaskAsync_WithoutPromptTemplate_ThrowsInvalidOperationException()
    {
        var taskRepo = new InMemoryTaskRepository();
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var depStore = new InMemoryTaskDependencyStore();
        var depEngine = new DependencyResolutionEngine(depStore);
        var lockProvider = new WorkspaceLockProvider();
        var gitManager = new FakeGitWorkspaceManager();
        var jobQueue = new FakeTaskJobQueue();
        var fakeHarness = new FakeAgentHarness();
        var harnessRegistry = new SingleHarnessRegistry(fakeHarness);

        var workspace = new Workspace
        {
            Id = 1,
            Name = "Test Workspace",
            RepositoryPath = "C:/test/repo",
            DefaultBaseBranch = "main",
            AgentProfiles = new List<AgentProfile>
            {
                new AgentProfile { Id = "default", HarnessId = "crucible", ModelName = "test-model" }
            }
        };
        await workspaceRepo.CreateAsync(workspace);

        var task = new ForgeTask
        {
            Id = 202,
            WorkspaceId = 1,
            Title = "Legacy Task",
            Description = "Legacy description",
            Status = TaskOverallStatus.Open,
            CurrentStageId = "task-review"
        };
        await taskRepo.CreateAsync(task);
        depStore.AddTask(task.Id, workspace.Id, TaskOverallStatus.Open);

        var legacyStage = new GenericAgenticStage("task-review", "ForgeTask Review", new AgenticStageConfig { RequiredMode = AgentMode.Full });
        var pipeline = new SingleStagePipeline(legacyStage);
        var pipelineProvider = new SingleStagePipelineProvider(pipeline);

        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = workspaceRepo,
            DependencyEngine = depEngine,
            LockProvider = lockProvider,
            GitWorkspaceManager = gitManager,
            PipelineProvider = pipelineProvider,
            JobQueue = jobQueue,
            DependencyStore = depStore,
            HarnessRegistry = harnessRegistry
        }.Build();

        await orchestrator.ProcessTaskAsync(task.Id);

        Assert.Empty(fakeHarness.ExecutedSessions);
        var updatedTask = await taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(updatedTask);
        Assert.Equal(TaskOverallStatus.Failed, updatedTask.Status);
    }

    [Fact]
    public void BuildBootstrapPrompt_GenericAgenticStageDirectCall_RendersTemplate()
    {
        var config = new AgenticStageConfig
        {
            PromptTemplate = "Stage {{ stage.display_name }} on #{{ task.id }}"
        };
        var stage = new GenericAgenticStage("test-stage", "Tester", config);
        var task = new ForgeTask { Id = 7, Title = "Test Task" };
        var context = new PipelineContext(task, new Workspace());

        var prompt = stage.BuildBootstrapPrompt(task, context);

        Assert.Equal("Stage Tester on #7", prompt);
    }

    [Fact]
    public void BuildBootstrapPrompt_WithoutPromptTemplate_ThrowsInvalidOperationException()
    {
        IWorkflowStage stage = new GenericAgenticStage("task-review", "ForgeTask Review", new AgenticStageConfig { RequiredMode = AgentMode.Full });
        var task = new ForgeTask { Id = 8, Title = "Review Task", Description = "Review details" };
        var context = new PipelineContext(task, new Workspace());

        Assert.Throws<InvalidOperationException>(() => stage.BuildBootstrapPrompt(task, context));
    }
}
