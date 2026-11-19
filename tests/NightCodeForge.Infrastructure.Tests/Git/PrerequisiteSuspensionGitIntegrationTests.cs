using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NightCodeForge.Core.Concurrency;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Execution;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Streaming;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Core.Workflow.Configuration;
using NightCodeForge.Core.Workflow.Stages;
using NightCodeForge.Infrastructure.Execution;
using NightCodeForge.Infrastructure.Git;
using Xunit;

namespace NightCodeForge.Infrastructure.Tests.Git;

public class PrerequisiteSuspensionGitIntegrationTests : IDisposable
{
    private readonly TestGitRepository _repo;
    private readonly GitCliRunner _runner;
    private readonly GitWorkspaceManager _gitManager;

    public PrerequisiteSuspensionGitIntegrationTests()
    {
        _repo = new TestGitRepository("main");
        _repo.CreateFile("README.md", "# Initial repo\n");
        _repo.CommitAll("Initial commit on main");

        var wslMeta = new Mock<IWslMetadataService>().Object;
        var pathResolver = new WorkspacePathResolver(wslMeta, Options.Create(new ExecutionEnvironmentProviderOptions()));
        var processRunner = new SystemProcessRunner(NullLogger<SystemProcessRunner>.Instance);
        _runner = new GitCliRunner(processRunner, pathResolver, wslMeta, NullLogger<GitCliRunner>.Instance);
        _gitManager = new GitWorkspaceManager(
            _runner,
            pathResolver,
            wslMeta,
            new Mock<IGitMirrorSyncService>().Object,
            new WorkspaceLockProvider(),
            processRunner,
            NullLogger<GitWorkspaceManager>.Instance);
    }

    public void Dispose()
    {
        _repo.Dispose();
    }

    private sealed class RealGitStage1Harness : IAgentHarness
    {
        private readonly Func<AgentSessionContext, Task<AgentRunResult>> _handler;
        public string HarnessId => StandardHarnessIds.Crucible;

        public RealGitStage1Harness(Func<AgentSessionContext, Task<AgentRunResult>> handler)
        {
            _handler = handler;
        }

        public Task<AgentRunResult> RunStageAsync(AgentSessionContext context, CancellationToken cancellationToken = default)
            => _handler(context);
    }

    private sealed class SingleHarnessRegistry : IHarnessRegistry
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

    private static async Task AddDependencyToFrontmatterAsync(string fullPath, int dependsOnId)
    {
        var content = (await File.ReadAllTextAsync(fullPath)).Replace("\r\n", "\n");
        var closingIdx = content.IndexOf("\n---", startIndex: 3, StringComparison.Ordinal);
        if (closingIdx < 0)
        {
            throw new InvalidOperationException($"Missing YAML frontmatter delimiter in '{fullPath}'.");
        }

        var frontmatter = content[..closingIdx];
        var rest = content[closingIdx..];
        frontmatter = frontmatter.Contains("depends_on: []", StringComparison.Ordinal)
            ? frontmatter.Replace("depends_on: []", $"depends_on:\n  - {dependsOnId}", StringComparison.Ordinal)
            : $"{frontmatter}\ndepends_on:\n  - {dependsOnId}";

        await File.WriteAllTextAsync(fullPath, frontmatter + rest);
    }

    [Fact]
    public async Task SuspendCleansRealWorktreeAndBranch_CascadeUnblockCreatesFreshBranchOffUpdatedMain()
    {
        var workspaceRepo = new InMemoryWorkspaceRepository();
        workspaceRepo.SeedGlobalProfiles(AgentProfile.CreateStandardGlobalProfiles("test-model"));
        var workspace = await workspaceRepo.CreateAsync(new Workspace
        {
            Id = 1,
            Name = "Real Git Integration WS",
            RepositoryPath = _repo.RepositoryPath,
            DefaultBaseBranch = "main",
            ExecutionMode = ExecutionMode.ContainerSandbox
        });

        var taskRepo = new InMemoryTaskRepository();
        var depStore = new InMemoryTaskDependencyStore();
        var depEngine = new DependencyResolutionEngine(depStore);

        var prereq = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 1,
            WorkspaceId = 1,
            Title = "Add Prereq Contract",
            Status = TaskOverallStatus.Ready,
            BaseBranch = "main"
        });
        depStore.AddTask(1, 1, TaskOverallStatus.Ready);

        // Prepare Task #1 branch with PrereqContract.cs committed on feature/task-1
        var wt1 = await _gitManager.EnsureWorktreeAsync(workspace, 1, "main");
        _repo.CreateFile("PrereqContract.cs", "public interface IPrereqContract {}\n", wt1);
        await _gitManager.CreateWipCheckpointAsync(workspace, 1, StandardStageIds.Implementation, "Add contract");

        var dependent = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 2,
            WorkspaceId = 1,
            Title = "Consume Prereq Contract",
            Description = "Initial spec",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.TaskReview,
            BaseBranch = "main",
            Version = 1,
            CreatedAt = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc)
        });
        depStore.AddTask(2, 1, TaskOverallStatus.Open);

        var syncServices = new ServiceCollection();
        syncServices.AddSingleton<ITaskRepository>(taskRepo);
        syncServices.AddSingleton<ITaskDependencyStore>(depStore);
        syncServices.AddSingleton<IDependencyResolutionEngine>(depEngine);
        var pathResolverMock = new Mock<IWorkspacePathResolver>();
        pathResolverMock.Setup(r => r.ToHostPath(It.IsAny<string>())).Returns<string>(p => p);
        var syncService = new TaskMarkdownSyncService(
            syncServices.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            pathResolverMock.Object,
            NullLogger<TaskMarkdownSyncService>.Instance);

        var cleanupContainerCalls = 0;
        var envProviderMock = new Mock<IExecutionEnvironmentProvider>();
        envProviderMock
            .Setup(e => e.CleanupTaskContainerAsync(workspace, 2, It.IsAny<CancellationToken>()))
            .Callback(() => cleanupContainerCalls++)
            .Returns(Task.CompletedTask);

        var preparer = new TaskWorkspacePreparer(
            workspaceRepo,
            new WorkspaceLockProvider(),
            _gitManager,
            new Mock<IGitMirrorSyncService>().Object,
            syncService,
            pathResolverMock.Object,
            envProviderMock.Object,
            new Mock<IGitHubPullRequestDeliveryService>().Object,
            new Mock<IGitHubTaskMirrorService>().Object,
            NullLogger<TaskWorkspacePreparer>.Instance);

        var jobQueue = new FakeTestJobQueue();
        var renderer = new ScribanPromptTemplateRenderer();
        var reviewStage = new GenericAgenticStage(
            StandardStageIds.TaskReview,
            "ForgeTask Review",
            new AgenticStageConfig
            {
                RequiredMode = AgentMode.Full,
                StageDirective = "Review and edit markdown specification on disk.",
                PromptTemplate = "Task #{{ task.id }}: {{ context.task_markdown_path }}"
            },
            renderer,
            TimeProvider.System)
        {
            QueueTier = StageQueueTier.TaskReview
        };
        var implStage = new GenericAgenticStage(
            StandardStageIds.Implementation,
            "Implementation",
            new AgenticStageConfig { RequiredMode = AgentMode.Full },
            renderer,
            TimeProvider.System)
        {
            QueueTier = StageQueueTier.Implementation
        };
        var pipelineProvider = new DefaultWorkflowPipelineProvider(new WorkflowPipeline(new IWorkflowStage[] { reviewStage, implStage }));

        var streamer = new InMemoryAgentActivityStreamer();
        var telemetry = new Mock<IOperationsTelemetryService>().Object;
        var mergeCoordinator = new TaskMergeAndCascadeCoordinator(
            taskRepo,
            preparer,
            depEngine,
            depStore,
            jobQueue,
            pipelineProvider,
            new Mock<IConflictResolutionVerifier>().Object,
            streamer,
            telemetry,
            NullLogger<TaskMergeAndCascadeCoordinator>.Instance);

        var cancellationManager = new TaskCancellationManager(NullLogger<TaskCancellationManager>.Instance);
        var lifecycleCoordinator = new TaskLifecycleMutationCoordinator(
            taskRepo,
            preparer,
            mergeCoordinator,
            pipelineProvider,
            jobQueue,
            cancellationManager,
            streamer,
            telemetry,
            NullLogger<TaskLifecycleMutationCoordinator>.Instance);

        var harnessCalls = 0;
        var sawContractOnSecondRun = false;
        var harness = new RealGitStage1Harness(async ctx =>
        {
            harnessCalls++;
            var workDir = ctx.PipelineContext!.WorkingDirectory!;
            var relPath = ctx.PipelineContext.DataBag.Get<string>(PipelineDataBagKeys.TaskMarkdownPath)!;
            var fullPath = Path.Combine(workDir, relPath);

            if (harnessCalls == 1)
            {
                Assert.False(File.Exists(Path.Combine(workDir, "PrereqContract.cs")));
                await AddDependencyToFrontmatterAsync(fullPath, 1);
                return AgentRunResult.Completed(new AgentRunMetrics(20, 40, 20, 60), summary: "Blocked on #1");
            }

            sawContractOnSecondRun = File.Exists(Path.Combine(workDir, "PrereqContract.cs"));
            var currentText = await File.ReadAllTextAsync(fullPath);
            await File.WriteAllTextAsync(fullPath, currentText.TrimEnd() + "\n\n## Plan\n- Use `IPrereqContract`.\n");
            return AgentRunResult.Completed(new AgentRunMetrics(20, 40, 20, 60), summary: "Reviewed against updated main");
        });

        var opsMock = new Mock<IOperationsControlService>();
        opsMock.Setup(o => o.IsPaused).Returns(false);

        var orchestrator = new TaskOrchestrator(
            taskRepo,
            preparer,
            mergeCoordinator,
            lifecycleCoordinator,
            new SingleHarnessRegistry(harness),
            streamer,
            opsMock.Object,
            telemetry,
            new InMemoryTaskAttachmentRepository(taskRepo),
            NullLogger<TaskOrchestrator>.Instance);

        // Act 1: Stage 1 discovers dependency on unclosed #1 -> suspends & deletes worktree + branch feature/task-2
        await orchestrator.ProcessTaskAsync(2);

        Assert.Equal(1, harnessCalls);
        Assert.True(cleanupContainerCalls >= 1);
        var wt2Path = _gitManager.GetWorktreePath(workspace, 2);
        Assert.False(Directory.Exists(wt2Path));

        var branchListAfterSuspend = await _runner.ExecuteAsync(_repo.RepositoryPath, ["branch", "--list", "feature/task-2"]);
        Assert.True(string.IsNullOrWhiteSpace(branchListAfterSuspend.StandardOutput));

        var suspendedTask = await taskRepo.GetByIdAsync(2);
        Assert.NotNull(suspendedTask);
        Assert.Equal(TaskOverallStatus.Open, suspendedTask.Status);
        Assert.Equal(string.Empty, suspendedTask.CurrentStageId);

        // Act 2: Merge Task #1 to main -> cascade unblocks Task #2 and re-enqueues Stage 1
        await orchestrator.CompleteTaskAndCascadeAsync(1, "feat: add PrereqContract (#1)");

        var closedPrereq = await taskRepo.GetByIdAsync(1);
        Assert.NotNull(closedPrereq);
        Assert.Equal(TaskOverallStatus.Closed, closedPrereq.Status);
        Assert.True(File.Exists(Path.Combine(_repo.RepositoryPath, "PrereqContract.cs")));
        Assert.Contains(jobQueue.EnqueuedStages, s => s.TaskId == 2 && s.Tier == StageQueueTier.TaskReview);

        // Act 3: Execute unblocked Task #2 -> fresh branch off updated main contains PrereqContract.cs
        await orchestrator.ProcessTaskAsync(2);

        Assert.Equal(2, harnessCalls);
        Assert.True(sawContractOnSecondRun);

        var resumedTask = await taskRepo.GetByIdAsync(2);
        Assert.NotNull(resumedTask);
        Assert.Equal(StandardStageIds.Implementation, resumedTask.CurrentStageId);
        Assert.True(resumedTask.HasExecutionRun);
    }
}
