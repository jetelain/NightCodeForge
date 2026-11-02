namespace NightCodeForge.Core.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Core.Concurrency;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Streaming;
using NightCodeForge.Core.Tests.Fixtures;
using NightCodeForge.Core.Workflow;
using Xunit;

public class TaskReviewSpecificationEnforcementTests
{
    private class RemediationTestHarness : IAgentHarness
    {
        public string HarnessId => "test-harness";
        public int Invocations { get; private set; }
        public List<string?> ReceivedSerializedStates { get; } = new();
        public Action<AgentSessionContext, int>? OnInvocation { get; set; }

        public Task<AgentRunResult> RunStageAsync(AgentSessionContext context, CancellationToken cancellationToken = default)
        {
            Invocations++;
            ReceivedSerializedStates.Add(context.SerializedStateJson);
            OnInvocation?.Invoke(context, Invocations);
            return Task.FromResult(AgentRunResult.Completed(
                new AgentRunMetrics(50, 100, 50, 150),
                summary: $"Turn {Invocations} summary"));
        }
    }

    [Fact]
    public async Task TaskReview_WhenDocNotModified_InjectsRemediationTurn_ReopensConversationAsInterrupted_AndResumesInSession()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var taskRepo = new InMemoryTaskRepository();
            var workspaceRepo = new InMemoryWorkspaceRepository();
            var depStore = new InMemoryTaskDependencyStore();
            var depEngine = new DependencyResolutionEngine(depStore);
            var lockProvider = new WorkspaceLockProvider();
            var jobQueue = new FakeTaskJobQueue();

            var workspace = new Workspace
            {
                Id = 1,
                Name = "Remediation WS",
                RepositoryPath = tempDir,
                DefaultBaseBranch = "main",
                AgentProfiles = new List<AgentProfile>
                {
                    new()
                    {
                        Id = "task-review",
                        Name = "Task Review Agent",
                        HarnessId = "test-harness",
                        ModelName = "test-model",
                        Mode = AgentMode.Full
                    }
                }
            };
        await workspaceRepo.CreateAsync(workspace);

        var task = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 100,
            WorkspaceId = 1,
            Title = "Task requiring spec remediation",
            Description = "Original description",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.TaskReview,
            BaseBranch = "main",
            Version = 1
        });
        depStore.AddTask(100, 1, TaskOverallStatus.Open);

        // Pre-create initial stage conversation
        var conv = await taskRepo.StartConversationAsync(
            task.Id,
            StandardStageIds.TaskReview,
            WorkflowStageKind.Agentic,
            attemptIndex: 1,
            agentProfileId: "task-review",
            harnessId: "test-harness",
            modelName: "test-model");
        await taskRepo.AddConversationTurnAsync(conv.Id, StageTurnRole.User, "Execute task review");
        await taskRepo.CompleteConversationAsync(conv.Id, StageExecutionStatus.Completed, summary: "Turn 1 finished");

        var mdSyncMock = new Mock<ITaskMarkdownSyncService>();
        var baseline = new Dictionary<int, TaskBaselineEntry>
        {
            [task.Id] = new(task.Id, 1, DateTime.UtcNow, "hash1", "/path/to/task-100.md", new TaskFrontmatter { Title = "Task requiring spec remediation" }, "Original description")
        };
        mdSyncMock.Setup(m => m.SyncAllTasksToDiskAsync(
            It.IsAny<Workspace>(),
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SyncAllToDiskResult(new Dictionary<int, string>(), baseline));

        var fakeHarness = new RemediationTestHarness();
        fakeHarness.OnInvocation = (ctx, attempt) =>
        {
            if (attempt == 2)
            {
                // On second invocation (remediation resumed), the agent edits the spec
                task.Description = "Enriched technical plan with architectural decisions";
                task.Version = 2;
            }
        };

        var harnessRegistry = new HarnessRegistry();
        harnessRegistry.RegisterHarness(fakeHarness);

        var stage1 = new TestWorkflowStage(StandardStageIds.TaskReview, WorkflowStageKind.Agentic);
        var stage2 = new TestWorkflowStage(StandardStageIds.PreImplementationBaseline, WorkflowStageKind.Deterministic);
        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stage1, stage2 });

        var builder = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = workspaceRepo,
            DependencyEngine = depEngine,
            LockProvider = lockProvider,
            PipelineProvider = new DefaultWorkflowPipelineProvider(pipeline),
            JobQueue = jobQueue,
            DependencyStore = depStore,
            HarnessRegistry = harnessRegistry,
            MarkdownSyncService = mdSyncMock.Object
        };
        var orchestrator = builder.Build();

        await orchestrator.ProcessTaskAsync(task.Id);

        // Assert that the harness was invoked twice:
        // Turn 1: unmodified -> triggers in-session remediation turn injection
        // Turn 2: modified -> completes task review and advances to stage 2
        Assert.Equal(2, fakeHarness.Invocations);

        // Verify the injected turn exists in the conversation turns
        var convWithTurns = await taskRepo.GetConversationWithTurnsAsync(conv.Id);
        Assert.NotNull(convWithTurns);
        var injectedTurn = convWithTurns.Turns.FirstOrDefault(t => t.Content != null && t.Content.Contains("[System Notice: Mandatory Task Specification Document Update Required]"));
        Assert.NotNull(injectedTurn);
        Assert.Equal(StageTurnRole.User, injectedTurn.Role);

        // Verify Turn 2 received the serialized memory with the injected notice
        Assert.NotNull(fakeHarness.ReceivedSerializedStates[1]);
        Assert.Contains("Mandatory Task Specification Document Update Required", fakeHarness.ReceivedSerializedStates[1]);

        // Verify task advanced past task-review to stage 2
        var updatedTask = await taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(updatedTask);
        Assert.Equal(StandardStageIds.PreImplementationBaseline, updatedTask.CurrentStageId);
        Assert.Null(updatedTask.InterruptedStageId);
    }
    finally
    {
        if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
    }
    }

    [Fact]
    public async Task TaskReview_WhenRemediationAttemptsExceedCeiling_FailsGracefullyWithoutUnhandledException_AndMarksConversationFailed()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var taskRepo = new InMemoryTaskRepository();
            var workspaceRepo = new InMemoryWorkspaceRepository();
            var depStore = new InMemoryTaskDependencyStore();
            var depEngine = new DependencyResolutionEngine(depStore);
            var lockProvider = new WorkspaceLockProvider();
            var jobQueue = new FakeTaskJobQueue();

            var workspace = new Workspace
            {
                Id = 2,
                Name = "Ceiling WS",
                RepositoryPath = tempDir,
                DefaultBaseBranch = "main",
                AgentProfiles = new List<AgentProfile>
                {
                    new()
                    {
                        Id = "task-review",
                        Name = "Task Review Agent",
                        HarnessId = "test-harness",
                        ModelName = "test-model",
                        Mode = AgentMode.Full
                    }
                }
            };
        await workspaceRepo.CreateAsync(workspace);

        var task = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 101,
            WorkspaceId = 2,
            Title = "Task with stubborn agent",
            Description = "Original description",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.TaskReview,
            BaseBranch = "main",
            Version = 1
        });
        depStore.AddTask(101, 2, TaskOverallStatus.Open);

        // Pre-create initial conversation
        var conv = await taskRepo.StartConversationAsync(
            task.Id,
            StandardStageIds.TaskReview,
            WorkflowStageKind.Agentic,
            attemptIndex: 1,
            agentProfileId: "task-review",
            harnessId: "test-harness",
            modelName: "test-model");
        await taskRepo.AddConversationTurnAsync(conv.Id, StageTurnRole.User, "Execute review");

        var mdSyncMock = new Mock<ITaskMarkdownSyncService>();
        var baseline = new Dictionary<int, TaskBaselineEntry>
        {
            [task.Id] = new(task.Id, 1, DateTime.UtcNow, "hash1", "/path/to/task-101.md", new TaskFrontmatter { Title = "Task with stubborn agent" }, "Original description")
        };
        mdSyncMock.Setup(m => m.SyncAllTasksToDiskAsync(
            It.IsAny<Workspace>(),
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SyncAllToDiskResult(new Dictionary<int, string>(), baseline));

        // Harness never modifies the document across all attempts
        var fakeHarness = new RemediationTestHarness();
        var harnessRegistry = new HarnessRegistry();
        harnessRegistry.RegisterHarness(fakeHarness);

        var stage1 = new TestWorkflowStage(StandardStageIds.TaskReview, WorkflowStageKind.Agentic);
        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stage1 });

        var builder = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = workspaceRepo,
            DependencyEngine = depEngine,
            LockProvider = lockProvider,
            PipelineProvider = new DefaultWorkflowPipelineProvider(pipeline),
            JobQueue = jobQueue,
            DependencyStore = depStore,
            HarnessRegistry = harnessRegistry,
            MarkdownSyncService = mdSyncMock.Object
        };
        var orchestrator = builder.Build();

        // Must complete gracefully without throwing WorkflowExecutionException
        var exception = await Record.ExceptionAsync(() => orchestrator.ProcessTaskAsync(task.Id));
        Assert.Null(exception);

        // With default MaxRetries = 3: initial turn + 3 retries = 4 invocations total
        Assert.Equal(4, fakeHarness.Invocations);

        // Verify task transitioned cleanly to Failed
        var updatedTask = await taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(updatedTask);
        Assert.Equal(TaskOverallStatus.Failed, updatedTask.Status);

        // Verify latest conversation is marked as Failed (not Completed)
        var convWithTurns = await taskRepo.GetConversationWithTurnsAsync(conv.Id);
        Assert.NotNull(convWithTurns);
        Assert.Equal(StageExecutionStatus.Failed, convWithTurns.Status);
        Assert.Contains("remediation attempts", convWithTurns.Summary ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        // Verify that premature ApprovedPlan was purged from data bag
        var dataBag = PipelineDataBag.FromJson(updatedTask.PipelineDataJson);
        Assert.Null(dataBag.Get<string>(PipelineDataBagKeys.ApprovedPlan));
    }
    finally
    {
        if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
    }
    }

    [Fact]
    public async Task TaskReview_WhenNoConversationExists_StillPopulatesRecoveredMemoryWithRemediationNotice()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ncf-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var taskRepo = new InMemoryTaskRepository();
            var workspaceRepo = new InMemoryWorkspaceRepository();
            var depStore = new InMemoryTaskDependencyStore();
            var depEngine = new DependencyResolutionEngine(depStore);
            var lockProvider = new WorkspaceLockProvider();
            var jobQueue = new FakeTaskJobQueue();

            var workspace = new Workspace
            {
                Id = 3,
                Name = "External Harness WS",
                RepositoryPath = tempDir,
                DefaultBaseBranch = "main",
                AgentProfiles = new List<AgentProfile>
                {
                    new()
                    {
                        Id = "task-review",
                        Name = "Task Review Agent",
                        HarnessId = "test-harness",
                        ModelName = "test-model",
                        Mode = AgentMode.Full
                    }
                }
            };
            await workspaceRepo.CreateAsync(workspace);

            var task = await taskRepo.CreateAsync(new ForgeTask
            {
                Id = 102,
                WorkspaceId = 3,
                Title = "Task on external harness without DB conversation",
                Description = "Original description",
                Status = TaskOverallStatus.Open,
                CurrentStageId = StandardStageIds.TaskReview,
                BaseBranch = "main",
                Version = 1
            });
            depStore.AddTask(102, 3, TaskOverallStatus.Open);

            var mdSyncMock = new Mock<ITaskMarkdownSyncService>();
            var baseline = new Dictionary<int, TaskBaselineEntry>
            {
                [task.Id] = new(task.Id, 1, DateTime.UtcNow, "hash1", "/path/to/task-102.md", new TaskFrontmatter { Title = task.Title }, "Original description")
            };
            mdSyncMock.Setup(m => m.SyncAllTasksToDiskAsync(
                It.IsAny<Workspace>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SyncAllToDiskResult(new Dictionary<int, string>(), baseline));

            var fakeHarness = new RemediationTestHarness
            {
                OnInvocation = (ctx, attempt) =>
                {
                    if (attempt == 2)
                    {
                        task.Description = "Updated specification on second attempt";
                        task.Version = 2;
                    }
                }
            };

            var harnessRegistry = new HarnessRegistry();
            harnessRegistry.RegisterHarness(fakeHarness);

            var stage1 = new TestWorkflowStage(StandardStageIds.TaskReview, WorkflowStageKind.Agentic);
            var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stage1 });

            var orchestrator = new TestOrchestratorBuilder
            {
                TaskRepository = taskRepo,
                WorkspaceRepository = workspaceRepo,
                DependencyEngine = depEngine,
                LockProvider = lockProvider,
                PipelineProvider = new DefaultWorkflowPipelineProvider(pipeline),
                JobQueue = jobQueue,
                DependencyStore = depStore,
                HarnessRegistry = harnessRegistry,
                MarkdownSyncService = mdSyncMock.Object
            }.Build();

            await orchestrator.ProcessTaskAsync(task.Id);

            Assert.Equal(2, fakeHarness.Invocations);
            Assert.NotNull(fakeHarness.ReceivedSerializedStates[1]);
            Assert.Contains("Mandatory Task Specification Document Update Required", fakeHarness.ReceivedSerializedStates[1]);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
