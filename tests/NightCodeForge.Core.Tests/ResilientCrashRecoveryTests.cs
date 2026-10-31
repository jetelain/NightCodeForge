using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Core.Concurrency;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Tests.Fixtures;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Core.Workflow.Stages;

namespace NightCodeForge.Core.Tests;

public class ResilientCrashRecoveryTests
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

    private readonly InMemoryTaskRepository _taskRepo = new();
    private readonly InMemoryWorkspaceRepository _workspaceRepo = new();
    private readonly FakeGitCliRunner _gitCliRunner = new();
    private readonly FakeTaskJobQueue _jobQueue = new();

    private StartupRecoveryService CreateRecoveryService(IWorkflowPipelineProvider pipelineProvider, IOperationsControlService? operationsControlService = null)
    {
        var ops = operationsControlService;
        if (ops == null)
        {
            var mockOps = new Mock<IOperationsControlService>();
            mockOps.Setup(m => m.IsPaused).Returns(false);
            ops = mockOps.Object;
        }

        return new StartupRecoveryService(
            _workspaceRepo,
            _taskRepo,
            _gitCliRunner,
            new FakeGitWorkspaceManager(),
            pipelineProvider,
            _jobQueue,
            new Mock<IWorkspacePathResolver>().Object,
            new Mock<IProcessRunner>().Object,
            new NightCodeForge.Core.Execution.SingleExecutionEnvironmentProvider(new Mock<IExecutionEnvironment>().Object),
            ops,
            NullLogger<StartupRecoveryService>.Instance);
    }

    [Fact]
    public async Task AgenticStage_MidSessionCrash_RehydratesConversationTurnsAndEnqueuesUrgent()
    {
        // 1. Create Workspace and Task
        var workspace = await _workspaceRepo.CreateAsync(new Workspace
        {
            Id = 1,
            Name = "CrashRecoveryWorkspace",
            RepositoryPath = "c:/repo"
        });

        var task = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 401,
            WorkspaceId = workspace.Id,
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.TaskReview
        });

        // 2. Active running conversation with mid-session turns in SQLite
        var conv = await _taskRepo.StartConversationAsync(
            task.Id,
            StandardStageIds.TaskReview,
            WorkflowStageKind.Agentic,
            attemptIndex: 1,
            title: "Task Review Session");

        await _taskRepo.AddConversationTurnAsync(
            conv.Id,
            StageTurnRole.User,
            content: "Review this task and produce a plan.");

        await _taskRepo.AddConversationTurnAsync(
            conv.Id,
            StageTurnRole.Thinking,
            content: "Analyzing files in repository.",
            thinkingTrace: "Analyzing files in repository.");

        await _taskRepo.AddConversationTurnAsync(
            conv.Id,
            StageTurnRole.Tool,
            toolName: "read_file",
            toolArguments: "{\"path\":\"README.md\"}",
            metadataJson: "{\"CallId\":\"call-1\"}");

        await _taskRepo.AddConversationTurnAsync(
            conv.Id,
            StageTurnRole.ToolResult,
            toolName: "read_file",
            toolResult: "# Test Project",
            metadataJson: "{\"CallId\":\"call-1\"}");

        // 3. Setup pipeline and recovery service
        var stage = TestWorkflowPipelines.CreateReviewStage();
        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stage });
        var recoveryService = CreateRecoveryService(new DefaultWorkflowPipelineProvider(pipeline));

        // 4. Execute crash recovery
        await recoveryService.ReconcileWorkspacesAsync();

        // 5. Assert: System notice turn added to DB conversation
        var fullConv = await _taskRepo.GetConversationWithTurnsAsync(conv.Id);
        Assert.NotNull(fullConv);
        Assert.Equal(5, fullConv.Turns.Count);

        var noticeTurn = fullConv.Turns.Last();
        Assert.Equal(StageTurnRole.System, noticeTurn.Role);
        Assert.Contains("Host restarted unexpectedly", noticeTurn.Content);

        // 6. Assert: DataBag has rehydrated serialized memory containing all 5 turns
        var updatedTask = await _taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(updatedTask);
        var bag = PipelineDataBag.FromJson(updatedTask.PipelineDataJson);
        var memoryJson = bag.Get<string>(PipelineDataBagKeys.RecoveredSerializedMemory);
        Assert.NotNull(memoryJson);

        var restoredItems = ConversationHistorySerializer.Deserialize(memoryJson);
        Assert.Equal(5, restoredItems.Count);
        Assert.Equal("user", restoredItems[0].Role);
        Assert.Equal("assistant", restoredItems[1].Role); // thinking
        Assert.Equal("assistant", restoredItems[2].Role); // tool call
        Assert.Equal("tool", restoredItems[3].Role);      // tool result
        Assert.Equal("user", restoredItems[4].Role);      // notice (never a mid-history system message)

        // 7. Assert: interrupted conversation is transitioned to Interrupted (the harness resumes it in-place)
        Assert.Equal(StageExecutionStatus.Interrupted, fullConv.Status);
        Assert.Equal("task-review", updatedTask.InterruptedStageId);

        // 8. Assert: Enqueued to stage queue
        Assert.Contains(task.Id, _jobQueue.TaskReviewQueue);
    }

    [Fact]
    public async Task AgenticStage_CrashBetweenToolCallAndResult_ClosesDanglingCallWithInterruptedResult()
    {
        var task = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 404,
            WorkspaceId = 1,
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.TaskReview
        });

        var conv = await _taskRepo.StartConversationAsync(
            task.Id, StandardStageIds.TaskReview, WorkflowStageKind.Agentic, attemptIndex: 1, title: "Dangling");
        await _taskRepo.AddConversationTurnAsync(conv.Id, StageTurnRole.User, content: "Go");
        await _taskRepo.AddConversationTurnAsync(
            conv.Id,
            StageTurnRole.Tool,
            toolName: "read_file",
            toolArguments: "{\"path\":\"a.cs\"}",
            metadataJson: "{\"CallId\":\"call-9\"}");

        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { TestWorkflowPipelines.CreateReviewStage() });
        await CreateRecoveryService(new DefaultWorkflowPipelineProvider(pipeline)).ReconcileWorkspacesAsync();

        var updated = await _taskRepo.GetByIdAsync(task.Id);
        var bag = PipelineDataBag.FromJson(updated!.PipelineDataJson);
        var items = ConversationHistorySerializer.Deserialize(bag.Get<string>(PipelineDataBagKeys.RecoveredSerializedMemory)!);

        // user, call, synthetic interrupted result, notice
        Assert.Equal(4, items.Count);
        Assert.Equal("tool", items[2].Role);
        Assert.Equal("call-9", items[2].FunctionResults![0].CallId);
        Assert.Equal(ConversationHistorySerializer.InterruptedToolResult, items[2].FunctionResults![0].Result);
        Assert.Equal("user", items[3].Role);
    }

    [Fact]
    public async Task DeterministicStage_MidExecutionCrash_PurgesStaleMetricsAndSupersedesRunningConversation()
    {
        // 1. Setup dirty DataBag metrics from crashed run
        var bag = new PipelineDataBag();
        bag.Set(PipelineDataBagKeys.TestsCurrentTotal, 100);
        bag.Set(PipelineDataBagKeys.TestsCurrentPassed, 95);
        bag.Set(PipelineDataBagKeys.TestsCurrentFailed, 5);
        bag.Set(PipelineDataBagKeys.TestsCurrentDurationMs, 2500L);
        bag.Set(PipelineDataBagKeys.CoverageCurrentLineRate, 0.65);
        bag.Set(PipelineDataBagKeys.WarningsCurrentTotal, 4);
        bag.Set(PipelineDataBagKeys.WarningsNew, new List<string> { "CS0219" });
        bag.Set(PipelineDataBagKeys.FailedTests, new List<string> { "TestFailure" });
        bag.Set(PipelineDataBagKeys.BuildFailureReason, "Compiler error");
        bag.Set(PipelineDataBagKeys.TestFailureReason, "5 failed tests");
        bag.Set(PipelineDataBagKeys.CoverageFailureReason, "Line rate below threshold");
        bag.Set(PipelineDataBagKeys.SastFailureReason, "Vulnerability detected");
        bag.Set(PipelineDataBagKeys.RatchetFailureReason, "Regression");
        bag.Set(PipelineDataBagKeys.StageCommand(DeterministicTestWithCoverageGate.DefaultStageId), "dotnet test --coverage");
        bag.Set(PipelineDataBagKeys.StageOutput(DeterministicTestWithCoverageGate.DefaultStageId), "Partial output before crash");

        var workspace = await _workspaceRepo.CreateAsync(new Workspace
        {
            Id = 2,
            Name = "DeterministicWorkspace",
            RepositoryPath = "c:/repo"
        });

        var task = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 402,
            WorkspaceId = workspace.Id,
            Status = TaskOverallStatus.Open,
            CurrentStageId = DeterministicTestWithCoverageGate.DefaultStageId,
            PipelineDataJson = bag.ToJson()
        });

        // Running conversation interrupted by crash
        var runningConv = await _taskRepo.StartConversationAsync(
            task.Id,
            DeterministicTestWithCoverageGate.DefaultStageId,
            WorkflowStageKind.Deterministic,
            attemptIndex: 1);

        var stage = new DeterministicTestWithCoverageGate(new NightCodeForge.Core.Execution.SingleExecutionEnvironmentProvider(new Mock<IExecutionEnvironment>().Object));
        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stage });
        var recoveryService = CreateRecoveryService(new DefaultWorkflowPipelineProvider(pipeline));

        // Execute recovery
        await recoveryService.ReconcileWorkspacesAsync();

        // Verify task state
        var updatedTask = await _taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(updatedTask);
        Assert.Equal(DeterministicTestWithCoverageGate.DefaultStageId, updatedTask.CurrentStageId);

        var updatedBag = PipelineDataBag.FromJson(updatedTask.PipelineDataJson);
        Assert.False(updatedBag.ContainsKey(PipelineDataBagKeys.TestsCurrentTotal));
        Assert.False(updatedBag.ContainsKey(PipelineDataBagKeys.TestsCurrentPassed));
        Assert.False(updatedBag.ContainsKey(PipelineDataBagKeys.TestsCurrentFailed));
        Assert.False(updatedBag.ContainsKey(PipelineDataBagKeys.TestsCurrentDurationMs));
        Assert.False(updatedBag.ContainsKey(PipelineDataBagKeys.CoverageCurrentLineRate));
        Assert.False(updatedBag.ContainsKey(PipelineDataBagKeys.WarningsCurrentTotal));
        Assert.False(updatedBag.ContainsKey(PipelineDataBagKeys.WarningsNew));
        Assert.False(updatedBag.ContainsKey(PipelineDataBagKeys.FailedTests));
        Assert.False(updatedBag.ContainsKey(PipelineDataBagKeys.BuildFailureReason));
        Assert.False(updatedBag.ContainsKey(PipelineDataBagKeys.TestFailureReason));
        Assert.False(updatedBag.ContainsKey(PipelineDataBagKeys.CoverageFailureReason));
        Assert.False(updatedBag.ContainsKey(PipelineDataBagKeys.SastFailureReason));
        Assert.False(updatedBag.ContainsKey(PipelineDataBagKeys.RatchetFailureReason));
        Assert.False(updatedBag.ContainsKey(PipelineDataBagKeys.StageCommand(DeterministicTestWithCoverageGate.DefaultStageId)));
        Assert.False(updatedBag.ContainsKey(PipelineDataBagKeys.StageOutput(DeterministicTestWithCoverageGate.DefaultStageId)));

        // Conversation marked as Failed/superseded
        var convs = await _taskRepo.GetConversationsAsync(task.Id);
        var supersededConv = convs.FirstOrDefault(c => c.Id == runningConv.Id);
        Assert.NotNull(supersededConv);
        Assert.Equal(StageExecutionStatus.Failed, supersededConv.Status);
        Assert.Contains("reset for clean re-run", supersededConv.Summary);

        // Re-enqueued to stage tier for fresh execution
        Assert.Contains(task.Id, _jobQueue.ImplementationQueue);
    }

    [Fact]
    public async Task Orchestrator_PassesRehydratedSerializedMemory_ToAgentSessionContext()
    {
        var bag = new PipelineDataBag();
        const string expectedHistory = "[{\"Role\":\"user\",\"Text\":\"prior prompt\"}]";
        bag.Set(PipelineDataBagKeys.RecoveredSerializedMemory, expectedHistory);
        bag.Set(PipelineDataBagKeys.DiscoveredSymbols, new List<string> { "SymbolA", "SymbolB" });

        var workspace = await _workspaceRepo.CreateAsync(new Workspace
        {
            Id = 3,
            RepositoryPath = "c:/repo",
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
        });
        var task = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 403,
            WorkspaceId = workspace.Id,
            Status = TaskOverallStatus.Open,
            CurrentStageId = "task-review",
            PipelineDataJson = bag.ToJson()
        });

        var depStore = new InMemoryTaskDependencyStore();
        depStore.AddTask(403, 3, TaskOverallStatus.Open);
        var depEngine = new DependencyResolutionEngine(depStore);
        var lockProvider = new WorkspaceLockProvider();
        var gitManager = new Mock<IGitWorkspaceManager>().Object;

        var fakeHarness = new FakeAgentHarness();
        var harnessRegistry = new FakeHarnessRegistry(fakeHarness);
        var stage = TestWorkflowPipelines.CreateReviewStage();
        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stage });

        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = _taskRepo,
            WorkspaceRepository = _workspaceRepo,
            DependencyEngine = depEngine,
            LockProvider = lockProvider,
            GitWorkspaceManager = gitManager,
            PipelineProvider = new DefaultWorkflowPipelineProvider(pipeline),
            JobQueue = _jobQueue,
            DependencyStore = depStore,
            HarnessRegistry = harnessRegistry
        }.Build();

        await orchestrator.ProcessTaskAsync(task.Id);

        Assert.Single(fakeHarness.ExecutedSessions);
        var session = fakeHarness.ExecutedSessions[0];
        Assert.Equal(expectedHistory, session.SerializedStateJson);
        Assert.NotNull(session.DiscoveredSymbols);
        Assert.Equal(2, session.DiscoveredSymbols.Count);
    }

    [Fact]
    public async Task AgenticStage_CrashWithZeroTurns_DoesNotSetRecoveredMemoryAndClosesConversation()
    {
        var task = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 405,
            WorkspaceId = 1,
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.TaskReview
        });

        // Started conversation before crash, but 0 turns were added
        var conv = await _taskRepo.StartConversationAsync(
            task.Id,
            StandardStageIds.TaskReview,
            WorkflowStageKind.Agentic,
            attemptIndex: 1,
            title: "Zero Turns Session");

        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { TestWorkflowPipelines.CreateReviewStage() });
        var recoveryService = CreateRecoveryService(new DefaultWorkflowPipelineProvider(pipeline));

        await recoveryService.ReconcileWorkspacesAsync();

        var updated = await _taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(updated);

        var bag = PipelineDataBag.FromJson(updated.PipelineDataJson);
        // With 0 turns, nothing should be rehydrated so orchestrator starts cleanly
        Assert.False(bag.ContainsKey(PipelineDataBagKeys.RecoveredSerializedMemory));

        // Conversation should be marked Failed
        var convs = await _taskRepo.GetConversationsAsync(task.Id);
        var closedConv = convs.FirstOrDefault(c => c.Id == conv.Id);
        Assert.NotNull(closedConv);
        Assert.Equal(StageExecutionStatus.Failed, closedConv.Status);

        Assert.Contains(task.Id, _jobQueue.TaskReviewQueue);
    }

    [Fact]
    public async Task AgenticStage_MultipleInterruptedRunningConversations_SupersedesAllRunningConversations()
    {
        var task = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 406,
            WorkspaceId = 1,
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.TaskReview
        });

        // Older interrupted conversation
        var olderConv = await _taskRepo.StartConversationAsync(
            task.Id,
            StandardStageIds.TaskReview,
            WorkflowStageKind.Agentic,
            attemptIndex: 1,
            title: "Attempt 1");
        await _taskRepo.AddConversationTurnAsync(olderConv.Id, StageTurnRole.User, content: "Initial try");

        // Latest interrupted conversation
        var latestConv = await _taskRepo.StartConversationAsync(
            task.Id,
            StandardStageIds.TaskReview,
            WorkflowStageKind.Agentic,
            attemptIndex: 2,
            title: "Attempt 2");
        await _taskRepo.AddConversationTurnAsync(latestConv.Id, StageTurnRole.User, content: "Retry with more details");
        await _taskRepo.AddConversationTurnAsync(latestConv.Id, StageTurnRole.Thinking, content: "Thinking deeply", thinkingTrace: "Thinking deeply");

        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { TestWorkflowPipelines.CreateReviewStage() });
        var recoveryService = CreateRecoveryService(new DefaultWorkflowPipelineProvider(pipeline));

        await recoveryService.ReconcileWorkspacesAsync();

        var convs = await _taskRepo.GetConversationsAsync(task.Id);
        // No conversation should remain in Running state
        Assert.DoesNotContain(convs, c => c.Status == StageExecutionStatus.Running);

        var rehydratedOlder = convs.First(c => c.Id == olderConv.Id);
        Assert.Equal(StageExecutionStatus.Superseded, rehydratedOlder.Status);
        Assert.Contains("Superseded by startup recovery", rehydratedOlder.Summary);

        var rehydratedLatest = convs.First(c => c.Id == latestConv.Id);
        Assert.Equal(StageExecutionStatus.Interrupted, rehydratedLatest.Status);

        // Memory should be rehydrated from the latest conversation (attempt 2)
        var updated = await _taskRepo.GetByIdAsync(task.Id);
        var bag = PipelineDataBag.FromJson(updated!.PipelineDataJson);
        var history = bag.Get<string>(PipelineDataBagKeys.RecoveredSerializedMemory);
        Assert.NotNull(history);
        Assert.Contains("Retry with more details", history);
        Assert.DoesNotContain("Initial try", history);
    }

    [Fact]
    public async Task AgenticStage_ChainedCrashAndPauseAcrossRestarts_PreservesAllTurnsInSingleConversation()
    {
        var task = await _taskRepo.CreateAsync(new ForgeTask
        {
            Id = 428,
            WorkspaceId = 1,
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.Implementation
        });

        var conv = await _taskRepo.StartConversationAsync(
            task.Id,
            StandardStageIds.Implementation,
            WorkflowStageKind.Agentic,
            attemptIndex: 1,
            title: "Task 28 Implementation");

        await _taskRepo.AddConversationTurnAsync(conv.Id, StageTurnRole.User, content: "Turn 0: Bootstrap");
        await _taskRepo.AddConversationTurnAsync(conv.Id, StageTurnRole.Assistant, content: "Turn 1: Initial progress");

        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { TestWorkflowPipelines.CreateImplementationStage() });
        var recoveryService1 = CreateRecoveryService(new DefaultWorkflowPipelineProvider(pipeline));

        // 1. First crash recovery transitions conv from Running -> Interrupted and appends system notice
        await recoveryService1.ReconcileWorkspacesAsync();

        var afterCrash1 = await _taskRepo.GetConversationWithTurnsAsync(conv.Id);
        Assert.NotNull(afterCrash1);
        Assert.Equal(StageExecutionStatus.Interrupted, afterCrash1.Status);
        Assert.Equal(3, afterCrash1.Turns.Count);

        // 2. Simulate second restart while still Interrupted (e.g. paused before next turn)
        var pausedOps = new Mock<IOperationsControlService>();
        pausedOps.Setup(m => m.IsPaused).Returns(true);
        var recoveryService2 = CreateRecoveryService(new DefaultWorkflowPipelineProvider(pipeline), pausedOps.Object);

        await recoveryService2.ReconcileWorkspacesAsync();

        var afterRestart2 = await _taskRepo.GetConversationWithTurnsAsync(conv.Id);
        Assert.NotNull(afterRestart2);
        Assert.Equal(StageExecutionStatus.Interrupted, afterRestart2.Status);
        // Must still have all 3 turns and must NOT duplicate the system recovery notice
        Assert.Equal(3, afterRestart2.Turns.Count);
        Assert.Equal(1, afterRestart2.Turns.Count(t => t.Role == StageTurnRole.System));

        var updatedTask = await _taskRepo.GetByIdAsync(task.Id);
        var bag = PipelineDataBag.FromJson(updatedTask!.PipelineDataJson);
        var memory = bag.Get<string>(PipelineDataBagKeys.RecoveredSerializedMemory);
        Assert.NotNull(memory);
        Assert.Contains("Turn 0: Bootstrap", memory);
        Assert.Contains("Turn 1: Initial progress", memory);
    }
}
