namespace NightCodeForge.Core.Tests;

using Moq;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Concurrency;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Tests.Fixtures;
using NightCodeForge.Core.Workflow;
using Xunit;

public class ImplementationArtifactGateTests
{
    private const string ProductionStat = "src/Service.cs | 10 ++\n 1 file changed, 10 insertions(+)";
    private const string ScratchOnlyStat = " .scratch/probe.cs | 5 +++++\n docs/tasks/task-1.md | 2 +-\n 2 files changed, 6 insertions(+), 1 deletion(-)";

    private sealed class CountingHarness : IAgentHarness
    {
        public string HarnessId => "test-harness";
        public int Invocations { get; private set; }
        public List<string?> ReceivedSerializedStates { get; } = new();

        public Task<AgentRunResult> RunStageAsync(AgentSessionContext context, CancellationToken cancellationToken = default)
        {
            Invocations++;
            ReceivedSerializedStates.Add(context.SerializedStateJson);
            return Task.FromResult(AgentRunResult.Completed(new AgentRunMetrics(10, 10, 10, 20), summary: $"Turn {Invocations}"));
        }
    }

    private static async Task<(ITaskOrchestrator Orchestrator, InMemoryTaskRepository Repo, CountingHarness Harness, ForgeTask Task, Conversation Conversation)> ArrangeAsync(
        Mock<IGitWorkspaceManager> git, string tempDir, int workspaceId, int taskId)
    {
        var taskRepo = new InMemoryTaskRepository();
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var depStore = new InMemoryTaskDependencyStore();

        await workspaceRepo.CreateAsync(new Workspace
        {
            Id = workspaceId,
            Name = "Impl WS",
            RepositoryPath = tempDir,
            DefaultBaseBranch = "main",
            AgentProfiles = new List<AgentProfile>
            {
                new() { Id = "implementation", Name = "Impl", HarnessId = "test-harness", ModelName = "m", Mode = AgentMode.Full }
            }
        });

        var task = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = taskId,
            WorkspaceId = workspaceId,
            Title = "Implement thing",
            Description = "desc",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.Implementation,
            BaseBranch = "main",
            Version = 1
        });
        depStore.AddTask(taskId, workspaceId, TaskOverallStatus.Open);

        var conv = await taskRepo.StartConversationAsync(
            task.Id, StandardStageIds.Implementation, WorkflowStageKind.Agentic, attemptIndex: 1,
            agentProfileId: "implementation", harnessId: "test-harness", modelName: "m");
        await taskRepo.AddConversationTurnAsync(conv.Id, StageTurnRole.User, "Implement");
        await taskRepo.CompleteConversationAsync(conv.Id, StageExecutionStatus.Completed, summary: "first");

        var harness = new CountingHarness();
        var registry = new HarnessRegistry();
        registry.RegisterHarness(harness);

        var pipeline = new WorkflowPipeline(new IWorkflowStage[]
        {
            new TestWorkflowStage(StandardStageIds.Implementation, WorkflowStageKind.Agentic)
        });

        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = workspaceRepo,
            DependencyEngine = new DependencyResolutionEngine(depStore),
            LockProvider = new WorkspaceLockProvider(),
            PipelineProvider = new DefaultWorkflowPipelineProvider(pipeline),
            JobQueue = new FakeTaskJobQueue(),
            DependencyStore = depStore,
            HarnessRegistry = registry,
            GitWorkspaceManager = git.Object
        }.Build();

        return (orchestrator, taskRepo, harness, task, conv);
    }

    [Fact]
    public async Task Implementation_WhenDiffEmptyThenModified_InjectsNoticeAndResumesInSession()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var git = new Mock<IGitWorkspaceManager>();
            git.SetupSequence(m => m.GenerateLiveDiffStatAsync(It.IsAny<Workspace>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(ScratchOnlyStat)
                .ReturnsAsync(ProductionStat);

            var (orchestrator, repo, harness, task, conv) = await ArrangeAsync(git, tempDir, 11, 200);

            await orchestrator.ProcessTaskAsync(task.Id);

            Assert.Equal(2, harness.Invocations);
            var convWithTurns = await repo.GetConversationWithTurnsAsync(conv.Id);
            Assert.NotNull(convWithTurns);
            var injected = convWithTurns.Turns.FirstOrDefault(t => t.Content != null && t.Content.Contains("[System Notice: Mandatory Code Implementation Required]"));
            Assert.NotNull(injected);
            Assert.Equal(StageTurnRole.User, injected.Role);
            Assert.NotNull(harness.ReceivedSerializedStates[1]);
            Assert.Contains("Mandatory Code Implementation Required", harness.ReceivedSerializedStates[1]);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task Implementation_WhenDiffNeverModified_FailsAfterRetryCeiling()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var git = new Mock<IGitWorkspaceManager>();
            git.Setup(m => m.GenerateLiveDiffStatAsync(It.IsAny<Workspace>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(GitDiffConstants.NoChanges);

            var (orchestrator, repo, harness, task, conv) = await ArrangeAsync(git, tempDir, 12, 201);

            var exception = await Record.ExceptionAsync(() => orchestrator.ProcessTaskAsync(task.Id));
            Assert.Null(exception);

            Assert.Equal(4, harness.Invocations);
            var updated = await repo.GetByIdAsync(task.Id);
            Assert.NotNull(updated);
            Assert.Equal(TaskOverallStatus.Failed, updated.Status);

            var convWithTurns = await repo.GetConversationWithTurnsAsync(conv.Id);
            Assert.NotNull(convWithTurns);
            Assert.Equal(StageExecutionStatus.Failed, convWithTurns.Status);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("no changes", false)]
    [InlineData(" .scratch/a.cs | 3 +++\n 1 file changed", false)]
    [InlineData(" docs/transcripts/x.md | 3 +++\n docs/tasks/t.md | 1 +\n 2 files changed", false)]
    [InlineData(" src/Service.cs | 10 ++\n 1 file changed", true)]
    [InlineData(" .scratch/a.cs | 3 +++\n tests/FooTests.cs | 8 +++\n 2 files changed", true)]
    [InlineData(" src/.scratch/a.cs | 3 +++\n 1 file changed", false)]
    public void HasProductionChanges_ClassifiesDiffStat(string? stat, bool expected)
    {
        Assert.Equal(expected, ImplementationDiffEvaluator.HasProductionChanges(stat));
    }
}

