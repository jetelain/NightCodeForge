using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NightCodeForge.Agents.Crucible;
using NightCodeForge.Agents.Dispatcher;
using NightCodeForge.Agents.Inference;
using NightCodeForge.Agents.Tests.Fixtures;
using NightCodeForge.Agents.Tools;
using NightCodeForge.Core.Concurrency;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Execution;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Streaming;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Core.Workflow.Configuration;
using NightCodeForge.Core.Workflow.Stages;
using NightCodeForge.Infrastructure.Data;
using NightCodeForge.Infrastructure.Data.Repositories;
using NightCodeForge.Infrastructure.Execution;
using NightCodeForge.Infrastructure.Git;
using Xunit;

namespace NightCodeForge.Agents.Tests;

/// <summary>
/// Integration tests verifying all workflow execution situations based on the specification
/// (Step 9.5, Step 11.21, Step 11.27, Step 11.30, Step 11.31, Step 11.34) in ContainerSandbox mode
/// with a seeded workspace ID (e.g. > 1,000,000) and mocked LLM client.
/// </summary>
public class WorkflowSpecIntegrationTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public WorkflowSpecIntegrationTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        _output = output;
    }

    private class FakeJobQueue : ITaskJobQueue
    {
        public List<int> UrgentQueue { get; } = new();
        public List<int> CodeReviewQueue { get; } = new();
        public List<int> ImplementationQueue { get; } = new();
        public List<int> TaskReviewQueue { get; } = new();
        public List<(int TaskId, StageQueueTier Tier, int Priority)> EnqueuedStages { get; } = new();

        public string EnqueueUrgent(int taskId, DateTime? originalEnqueuedAt = null)
        {
            UrgentQueue.Add(taskId);
            return $"job-urgent-{taskId}-{Guid.NewGuid():N}";
        }

        public string EnqueueStage(int taskId, StageQueueTier tier, int priority, DateTime? originalEnqueuedAt = null)
        {
            EnqueuedStages.Add((taskId, tier, priority));
            switch (tier)
            {
                case StageQueueTier.CodeReview:
                    CodeReviewQueue.Add(taskId);
                    return $"job-cr-{taskId}-{Guid.NewGuid():N}";
                case StageQueueTier.Implementation:
                    ImplementationQueue.Add(taskId);
                    return $"job-impl-{taskId}-{Guid.NewGuid():N}";
                case StageQueueTier.TaskReview:
                    TaskReviewQueue.Add(taskId);
                    return $"job-tr-{taskId}-{Guid.NewGuid():N}";
                default:
                    throw new ArgumentOutOfRangeException(nameof(tier), tier, null);
            }
        }

        public bool Delete(string jobId) => true;

        public Task<bool> HasUrgentJobsWaitingAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(UrgentQueue.Count > 0);
    }

    private class WorkflowMockChatClient : IChatClient
    {
        public List<(AgentSessionContext Context, List<ChatMessage> Messages, ChatOptions? Options)> Invocations { get; } = new();
        public Func<AgentSessionContext, List<ChatMessage>, ChatOptions?, IAsyncEnumerable<ChatResponseUpdate>>? StreamHandler { get; set; }
        public AgentSessionContext? CurrentContext { get; set; }

        public ChatClientMetadata Metadata => new("workflow-mock", new Uri("http://localhost"));

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var msgs = chatMessages.ToList();
            if (CurrentContext != null)
            {
                Invocations.Add((CurrentContext, msgs, options));
            }

            if (StreamHandler != null && CurrentContext != null)
            {
                return StreamHandler(CurrentContext, msgs, options);
            }

            return DefaultStream();
        }

        private static async IAsyncEnumerable<ChatResponseUpdate> DefaultStream()
        {
            yield return new ChatResponseUpdate { Contents = [new TextContent("Default mock agent response: stage completed.")] };
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private class SingleClientFactory : ILlmChatClientFactory
    {
        private readonly WorkflowMockChatClient _client;
        public SingleClientFactory(WorkflowMockChatClient client) => _client = client;
        public IChatClient CreateClient(string? modelId = null) => _client;
        public IChatClient CreateClient(string? modelId, OllamaRoutingTarget routingTarget) => _client;
        public IChatClient CreateClient(AgentProfile? profile) => _client;
        public IChatClient CreateClient(AgentProfile? profile, string? sessionId) => _client;
        public IChatClient CreateClient(string? modelId, OllamaRoutingTarget routingTarget, string? sessionId) => _client;
    }

    private class InterceptingHarness : IAgentHarness
    {
        private readonly CrucibleAgentHarness _inner;
        private readonly WorkflowMockChatClient _client;

        public InterceptingHarness(CrucibleAgentHarness inner, WorkflowMockChatClient client)
        {
            _inner = inner;
            _client = client;
        }

        public string HarnessId => _inner.HarnessId;

        public Task<AgentRunResult> RunStageAsync(AgentSessionContext context, CancellationToken cancellationToken = default)
        {
            _client.CurrentContext = context;
            return _inner.RunStageAsync(context, cancellationToken);
        }
    }

    private class HarnessRegistryWrapper : IHarnessRegistry
    {
        private readonly Dictionary<string, IAgentHarness> _harnesses = new(StringComparer.OrdinalIgnoreCase);

        public void RegisterHarness(IAgentHarness harness) => _harnesses[harness.HarnessId] = harness;
        public IAgentHarness GetHarness(string harnessId) => _harnesses[harnessId];
        public bool TryGetHarness(string harnessId, out IAgentHarness? harness) => _harnesses.TryGetValue(harnessId, out harness);
        public IEnumerable<IAgentHarness> GetAllHarnesses() => _harnesses.Values;
    }

    private static readonly object SeedLock = new();

    private static (AppDbContext Context, SqliteConnection Connection, int SeededId) CreateSeededDatabase(int targetSeed = 1200050)
    {
        lock (SeedLock)
        {
            var prevEnv = Environment.GetEnvironmentVariable(AppDbContext.WorkspaceIdSeedEnvironmentVariable);
            try
            {
                Environment.SetEnvironmentVariable(AppDbContext.WorkspaceIdSeedEnvironmentVariable, targetSeed.ToString());

                var connection = new SqliteConnection("DataSource=:memory:");
                connection.Open();

                var options = new DbContextOptionsBuilder<AppDbContext>()
                    .UseSqlite(connection)
                    .Options;

                var dbContext = new AppDbContext(options);
                dbContext.EnsureDatabaseCreatedAndWalAsync().GetAwaiter().GetResult();

                return (dbContext, connection, targetSeed);
            }
            finally
            {
                Environment.SetEnvironmentVariable(AppDbContext.WorkspaceIdSeedEnvironmentVariable, prevEnv);
            }
        }
    }

    private sealed class TestWorkflowEnvironment : IAsyncDisposable, IDisposable
    {
        public TaskOrchestrator Orchestrator { get; init; } = null!;
        public AppDbContext DbContext { get; init; } = null!;
        public SqliteConnection Connection { get; init; } = null!;
        public Workspace Workspace { get; init; } = null!;
        public WorkflowMockChatClient MockLlm { get; init; } = null!;
        public FakeJobQueue JobQueue { get; init; } = null!;
        public string TempHostRepo { get; init; } = null!;
        public ServiceProvider ServiceProvider { get; init; } = null!;

        public AppDbContext Db => DbContext;
        public SqliteConnection Conn => Connection;

        public void Deconstruct(
            out TaskOrchestrator orchestrator,
            out AppDbContext dbContext,
            out SqliteConnection connection,
            out Workspace workspace,
            out WorkflowMockChatClient mockLlm,
            out FakeJobQueue jobQueue)
        {
            orchestrator = Orchestrator;
            dbContext = DbContext;
            connection = Connection;
            workspace = Workspace;
            mockLlm = MockLlm;
            jobQueue = JobQueue;
        }

        public void Dispose()
        {
            try { DbContext.Dispose(); } catch (Exception) { /* Best-effort cleanup of DbContext */ }
            try { Connection.Dispose(); } catch (Exception) { /* Best-effort cleanup of SQLite connection */ }
            try { ServiceProvider.Dispose(); } catch (Exception) { /* Best-effort cleanup of test service provider */ }
            try
            {
                if (Directory.Exists(TempHostRepo))
                {
                    Directory.Delete(TempHostRepo, true);
                }
            }
            catch (Exception) { /* Best-effort cleanup of temporary repository directory */ }
        }

        public async ValueTask DisposeAsync()
        {
            try { await DbContext.DisposeAsync(); } catch (Exception) { /* Best-effort cleanup of DbContext */ }
            try { await Connection.DisposeAsync(); } catch (Exception) { /* Best-effort cleanup of SQLite connection */ }
            try { await ServiceProvider.DisposeAsync(); } catch (Exception) { /* Best-effort cleanup of test service provider */ }
            try
            {
                if (Directory.Exists(TempHostRepo))
                {
                    Directory.Delete(TempHostRepo, true);
                }
            }
            catch (Exception) { /* Best-effort cleanup of temporary repository directory */ }
        }
    }

    private static TestWorkflowEnvironment CreateWorkflowEnvironment(
        int targetSeed = 1200050,
        Func<string, string?, CommandResult>? execHandler = null,
        IGitWorkspaceManager? gitManagerOverride = null,
        IOperationsControlService? opsControlOverride = null,
        ITaskCancellationManager? cancellationManagerOverride = null)
    {
        var (dbContext, connection, seededId) = CreateSeededDatabase(targetSeed);

        var tempHostRepo = Path.Combine(Path.GetTempPath(), $"ncf-test-repo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempHostRepo);

        dbContext.EnsureDefaultGlobalAgentProfilesAsync("mock-model").GetAwaiter().GetResult();
        dbContext.EnsureDefaultWorkflowsAsync().GetAwaiter().GetResult();

        var workspace = new Workspace
        {
            Name = "ContainerSandbox Integration Workspace",
            RepositoryPath = tempHostRepo,
            Ext4MirrorPath = $"/home/runner/.nightcodeforge/wks/{seededId}/clone",
            DefaultBaseBranch = "main",
            ExecutionMode = ExecutionMode.ContainerSandbox
        };

        dbContext.Workspaces.Add(workspace);
        dbContext.SaveChanges();

        Assert.Equal(seededId, workspace.Id);

        var taskRepo = new EfCoreTaskRepository(dbContext);
        var workspaceRepo = new EfCoreWorkspaceRepository(dbContext);
        var depStore = new EfCoreTaskDependencyStore(dbContext);
        var depEngine = new DependencyResolutionEngine(depStore);
        var lockProvider = new WorkspaceLockProvider();

        var jobQueue = new FakeJobQueue();

        // Execution Environment for ContainerSandbox
        var mockEnv = new Mock<IExecutionEnvironment>();
        mockEnv.Setup(e => e.Tier).Returns(ExecutionTier.ContainerSandbox);
        mockEnv.Setup(e => e.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<int?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>(),
                It.IsAny<CancellationToken>()))
            .Returns<string, string?, int?, IReadOnlyDictionary<string, string>?, CancellationToken>((cmd, wd, to, envs, ct) =>
            {
                if (execHandler != null)
                {
                    return Task.FromResult(execHandler(cmd, wd));
                }
                return Task.FromResult(new CommandResult(0, "Passed: 10, Failed: 0, Total: 10", "", TimeSpan.FromMilliseconds(50)));
            });

        var envProviderMock = new Mock<IExecutionEnvironmentProvider>();
        envProviderMock.Setup(p => p.GetEnvironment(It.IsAny<Workspace>(), It.IsAny<SandboxSecurityTier>(), It.IsAny<int?>()))
            .Returns(mockEnv.Object);

        // Git Workspace Manager
        var gitManager = gitManagerOverride;
        if (gitManager == null)
        {
            var mockGit = new Mock<IGitWorkspaceManager>();
            mockGit.Setup(m => m.CheckMergeableAsync(It.IsAny<Workspace>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MergeabilityCheckResult(true, Array.Empty<string>(), "base-sha", "feat-sha"));
            mockGit.Setup(m => m.SyncFromBaseBranchAsync(It.IsAny<Workspace>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MergeSyncResult(true, false, Array.Empty<string>(), "merge-sha"));
            mockGit.Setup(m => m.GetBranchShaAsync(It.IsAny<Workspace>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("commit-sha");
            mockGit.Setup(m => m.GenerateDiffStatAsync(It.IsAny<Workspace>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(" 2 files changed, 20 insertions(+), 2 deletions(-)");
            mockGit.Setup(m => m.GenerateLiveDiffStatAsync(It.IsAny<Workspace>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(" 2 files changed, 20 insertions(+), 2 deletions(-)");
            mockGit.Setup(m => m.GenerateDiffAsync(It.IsAny<Workspace>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("diff --git a/File.cs b/File.cs\n+new line");
            mockGit.Setup(m => m.GenerateLiveDiffAsync(It.IsAny<Workspace>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("diff --git a/File.cs b/File.cs\n+new line");
            mockGit.Setup(m => m.CreateWipCheckpointAsync(It.IsAny<Workspace>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("checkpoint-commit-sha");
            mockGit.Setup(m => m.SquashAndMergeAsync(It.IsAny<Workspace>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("squash-sha");
            mockGit.Setup(m => m.SquashAndMergeAsync(It.IsAny<Workspace>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("squash-sha");
            gitManager = mockGit.Object;
        }

        // Harness with Mock LLM
        var mockLlm = new WorkflowMockChatClient();
        var clientFactory = new SingleClientFactory(mockLlm);
        var harnessBuilder = new TestCrucibleHarnessBuilder
        {
            ChatClientFactory = clientFactory,
            TaskRepository = taskRepo,
            EnvProvider = envProviderMock.Object,
            PathResolver = TestPathResolver.Create()
        };
        var crucibleHarness = harnessBuilder.Build();
        var interceptingHarness = new InterceptingHarness(crucibleHarness, mockLlm);

        var registry = new HarnessRegistryWrapper();
        registry.RegisterHarness(interceptingHarness);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IPromptTemplateRenderer, ScribanPromptTemplateRenderer>();
        services.AddSingleton<IExecutionEnvironmentProvider>(envProviderMock.Object);
        services.AddSingleton<IExecutionEnvironment>(mockEnv.Object);
        services.AddSingleton<NightCodeForge.Core.Execution.IBackgroundTaskRunner>(
            new NightCodeForge.Core.Execution.BackgroundTaskRunner(NullLogger<NightCodeForge.Core.Execution.BackgroundTaskRunner>.Instance));
        services.AddSingleton<IOllamaModelPreloadService>(new Mock<IOllamaModelPreloadService>().Object);
        services.AddSingleton<IAgentActivityStreamer, InMemoryAgentActivityStreamer>();
        services.AddSingleton<ITaskRepository>(taskRepo);
        services.AddSingleton<IWorkflowRepository>(new EfCoreWorkflowRepository(dbContext));
        services.AddSingleton<IWorkflowStageRegistry, WorkflowStageRegistry>();
        services.AddSingleton<IWorkflowStageFactory, WorkflowStageFactory>();
        services.AddSingleton<IWorkflowPipelineProvider, DatabaseWorkflowPipelineProvider>();
        var sp = services.BuildServiceProvider();

        var pipelineProvider = sp.GetRequiredService<IWorkflowPipelineProvider>();

        var mockMirrorSync = new Mock<IGitMirrorSyncService>();
        mockMirrorSync.Setup(m => m.PushBaseBranchToHostAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitSyncResult(true, false, "main", null, false));
        var mockMdSync = new Mock<ITaskMarkdownSyncService>();
        mockMdSync.Setup(m => m.SyncTaskToDiskAsync(It.IsAny<ForgeTask>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<int>?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("docs/tasks/task-1.md");
        mockMdSync.Setup(m => m.SyncAllTasksToDiskAsync(It.IsAny<Workspace>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns<Workspace, string, string?, CancellationToken>((w, p, b, ct) =>
            {
                var tasks = dbContext.Tasks.Where(t => t.WorkspaceId == w.Id).ToList();
                var baselines = tasks.ToDictionary(
                    t => t.Id,
                    t => new TaskBaselineEntry(t.Id, t.Version, t.UpdatedAt, "hash", $"docs/tasks/task-{t.Id}.md", new TaskFrontmatter { Title = t.Title }, t.Description ?? ""));
                return Task.FromResult(new SyncAllToDiskResult(new Dictionary<int, string>(), baselines));
            });
        mockMdSync.Setup(m => m.SyncAllTasksFromDiskAsync(
                It.IsAny<Workspace>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<IReadOnlyDictionary<int, TaskBaselineEntry>?>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Returns<Workspace, string, string?, IReadOnlyDictionary<int, TaskBaselineEntry>?, string, string?, CancellationToken>(
                (w, sp, hp, b, a, r, ct) => Task.FromResult<IReadOnlyList<ForgeTask>>(dbContext.Tasks.Where(t => t.WorkspaceId == w.Id).ToList()));

        var opsService = opsControlOverride;
        if (opsService == null)
        {
            var mockOps = new Mock<IOperationsControlService>();
            mockOps.Setup(o => o.IsPaused).Returns(false);
            mockOps.Setup(o => o.ReclaimStatus).Returns(OperationsReclaimStatus.Idle);
            opsService = mockOps.Object;
        }

        var cancellationManager = cancellationManagerOverride ?? new TaskCancellationManager(NullLogger<TaskCancellationManager>.Instance);

        var mockVerifier = new Mock<IConflictResolutionVerifier>();
        mockVerifier.Setup(v => v.VerifyResolutionAsync(
            It.IsAny<Workspace>(),
            It.IsAny<int>(),
            It.IsAny<string>(),
            It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<PipelineDataBag?>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConflictResolutionVerificationResult(true));

        var orchestrator = new TaskOrchestrator(
            taskRepo,
            workspaceRepo,
            depEngine,
            lockProvider,
            gitManager,
            envProviderMock.Object,
            pipelineProvider,
            jobQueue,
            depStore,
            registry,
            mockMirrorSync.Object,
            mockMdSync.Object,
            TestPathResolver.Create(),
            new InMemoryAgentActivityStreamer(),
            cancellationManager,
            mockVerifier.Object,
            opsService,
            new Mock<IOperationsTelemetryService>().Object,
            NullLogger<TaskOrchestrator>.Instance);

        return new TestWorkflowEnvironment
        {
            Orchestrator = orchestrator,
            DbContext = dbContext,
            Connection = connection,
            Workspace = workspace,
            MockLlm = mockLlm,
            JobQueue = jobQueue,
            TempHostRepo = tempHostRepo,
            ServiceProvider = sp
        };
    }

    [Fact]
    public async Task PostImplementationGateFailure_LoopsBackInProcess_WithoutLeakingReviewDiff_AndIncludesDetailedTestDiagnostics()
    {
        // =========================================================================
        // REPRODUCING TASK 12 INCIDENT:
        // When Post-Implementation Gates fails on deterministic tests:
        // 1. Implementation prompt on retry MUST NOT leak [Git Diff Summary] or "no changes".
        // 2. Implementation prompt on retry MUST include the detailed assertion/exception failure
        //    (e.g. "Sequence contains more than one matching element"), NOT just the test method name.
        // =========================================================================

        int testRunCount = 0;
        CommandResult TestExecutionHandler(string command, string? workingDir)
        {
            if (command.Contains("dotnet test"))
            {
                testRunCount++;
                if (testRunCount == 1)
                {
                    // Attempt 1 fails with detailed failure output (Task 12 exact scenario)
                    var failureOutput = """
Passed!  - échec : 2, réussite : 644, ignorée(s) : 0, total : 646
  Failed OllamaRouter.Tests.Services.TerminalMonitorRendererTests.Render_FleetNamedTarget [9 ms]
  Error Message:
   System.InvalidOperationException : Sequence contains more than one matching element
  Stack Trace:
     at System.Linq.ThrowHelper.ThrowMoreThanOneElementException()
     at OllamaRouter.Tests.Services.TerminalMonitorRendererTests.Render_FleetNamedTarget() in /home/runner/tests/RenderTests.cs:line 529

  Failed OllamaRouter.Tests.Services.TerminalMonitorRendererTests.Render_FleetTargetGroup_WithHealthyNodes [12 ms]
  Error Message:
   Assert.Equal() Failure: Values differ
   Expected: 1
   Actual:   0
  Stack Trace:
     at OllamaRouter.Tests.Services.TerminalMonitorRendererTests.Render_FleetTargetGroup_WithHealthyNodes() in /home/runner/tests/RenderTests.cs:line 550
""";
                    return new CommandResult(1, failureOutput, "", TimeSpan.FromMilliseconds(100));
                }

                // Attempt 2 succeeds
                return new CommandResult(0, "Passed!  - échec : 0, réussite : 646, total : 646", "", TimeSpan.FromMilliseconds(80));
            }

            return new CommandResult(0, "Build succeeded", "", TimeSpan.FromMilliseconds(50));
        }

        await using var env = CreateWorkflowEnvironment(targetSeed: 1200050, execHandler: TestExecutionHandler);
        var (orchestrator, dbContext, connection, workspace, mockLlm, jobQueue) = env;
        {
            // Seed task starting at Implementation stage (Sequence 3)
            var task = new ForgeTask
            {
                WorkspaceId = workspace.Id,
                Title = "Implement Fleet Named Target Monitor",
                Description = "Render named target nodes in terminal monitor",
                Status = TaskOverallStatus.Open,
                CurrentStageId = StandardStageIds.Implementation,
                BaseBranch = "main",
                Priority = TaskPriority.Normal
            };
            dbContext.Tasks.Add(task);
            await dbContext.SaveChangesAsync();

            int agentTurn = 0;
            mockLlm.StreamHandler = (ctx, msgs, opts) =>
            {
                agentTurn++;
                if (agentTurn == 1)
                {
                    // First implementation attempt produces code edits
                    return ToAsyncEnumerable("Attempt 1: Implemented terminal monitor logic.");
                }

                // Second implementation attempt (after loopback): fixes the test failure
                return ToAsyncEnumerable("Attempt 2: Fixed duplicate element selection in fleet target group.");
            };

            // Process the Implementation cohort in a single worker turn
            await orchestrator.ProcessTaskAsync(task.Id);

            var logs = await dbContext.ExecutionLogs.Where(l => l.TaskId == task.Id).ToListAsync();
            foreach (var log in logs)
            {
                _output.WriteLine($"[{log.Level}] {log.Message}");
            }

            // Assertions on LLM Invocations
            Assert.True(mockLlm.Invocations.Count >= 2, $"Expected at least 2 LLM invocations for Implementation (initial + loopback), but got {mockLlm.Invocations.Count}.");

            var secondInvocation = mockLlm.Invocations[1];
            Assert.Equal(StandardStageIds.Implementation, secondInvocation.Context.StageId);

            var allMessagesText = string.Join("\n", secondInvocation.Messages.Select(m =>
            {
                var textContents = string.Join("\n", m.Contents.OfType<TextContent>().Select(t => t.Text));
                var toolResultContents = string.Join("\n", m.Contents.OfType<FunctionResultContent>().Select(f => f.Result?.ToString() ?? ""));
                return string.Join("\n", new[] { m.Text, textContents, toolResultContents }.Where(s => !string.IsNullOrWhiteSpace(s)));
            }));

            // FINDING 1: Implementation retry messages must NOT contain leaked git diff summary
            Assert.DoesNotContain("[Git Diff Summary]", allMessagesText);
            Assert.DoesNotContain("no changes", allMessagesText);

            // FINDING 2: Implementation retry messages must contain the detailed assertion/exception diagnostics
            Assert.Contains("Sequence contains more than one matching element", allMessagesText);
            Assert.Contains("Assert.Equal() Failure", allMessagesText);

            // Check that after attempt 2, the quality gate passed and task progressed to review queue
            Assert.True(jobQueue.CodeReviewQueue.Contains(task.Id), "Task should have advanced to CodeReviewQueue after fixing gate failure.");
        }
    }

    [Fact]
    public async Task CrossCohortLoopback_WhenReviewerRejects_EnqueuesImplementation_AndProvidesFeedback()
    {
        await using var env = CreateWorkflowEnvironment(targetSeed: 1200051);
        var (orchestrator, dbContext, connection, workspace, mockLlm, jobQueue) = env;
        {
            var task = new ForgeTask
            {
                WorkspaceId = workspace.Id,
                Title = "Feature requiring review",
                Description = "Review test",
                Status = TaskOverallStatus.Open,
                CurrentStageId = StandardStageIds.CodeReviewAndFix,
                BaseBranch = "main"
            };
            dbContext.Tasks.Add(task);
            await dbContext.SaveChangesAsync();

            // Reviewer LLM triggers <request_changes>
            mockLlm.StreamHandler = (ctx, msgs, opts) =>
            {
                var content = """
I reviewed the code.
<request_changes>
The validation for empty array input is missing and no unit tests cover this edge case.
</request_changes>
""";
                return ToAsyncEnumerable(content);
            };

            await orchestrator.ProcessTaskAsync(task.Id);

            // Verify task was routed back to Implementation cohort in Hangfire queue
            Assert.Contains(task.Id, jobQueue.ImplementationQueue);

            var updatedTask = await dbContext.Tasks.FindAsync(task.Id);
            Assert.NotNull(updatedTask);
            Assert.Equal(StandardStageIds.Implementation, updatedTask.CurrentStageId);

            // Execute Implementation cohort to verify prompt contains reviewer feedback
            mockLlm.Invocations.Clear();
            mockLlm.StreamHandler = (ctx, msgs, opts) =>
            {
                return ToAsyncEnumerable("Fixed empty array validation and added tests.");
            };

            await orchestrator.ProcessTaskAsync(task.Id);

            Assert.NotEmpty(mockLlm.Invocations);
            var implPrompt = string.Join("\n", mockLlm.Invocations[0].Messages.Select(m => m.Text));
            Assert.Contains("The validation for empty array input is missing", implPrompt);

            // Review diff must not leak into implementation prompt
            Assert.DoesNotContain("[Git Diff Summary]", implPrompt);
        }
    }

    [Fact]
    public async Task QualityGate_WhenExceedingMaxRetries_TransitionsTaskToFailed()
    {
        // Command always fails tests
        CommandResult FailingTestHandler(string command, string? workingDir)
        {
            if (command.Contains("dotnet test"))
            {
                return new CommandResult(1, "Failed TestX. Total tests: 1. Failed: 1.", "", TimeSpan.FromMilliseconds(50));
            }
            return new CommandResult(0, "Build OK", "", TimeSpan.FromMilliseconds(50));
        }

        await using var env = CreateWorkflowEnvironment(targetSeed: 1200052, execHandler: FailingTestHandler);
        var (orchestrator, dbContext, connection, workspace, mockLlm, jobQueue) = env;
        {
            var task = new ForgeTask
            {
                WorkspaceId = workspace.Id,
                Title = "Task doomed to fail quality gates",
                Description = "Max retries test",
                Status = TaskOverallStatus.Open,
                CurrentStageId = StandardStageIds.Implementation,
                BaseBranch = "main"
            };
            dbContext.Tasks.Add(task);
            await dbContext.SaveChangesAsync();

            mockLlm.StreamHandler = (ctx, msgs, opts) =>
            {
                return ToAsyncEnumerable("Attempted fix, but tests still fail.");
            };

            await orchestrator.ProcessTaskAsync(task.Id);

            var updatedTask = await dbContext.Tasks.FindAsync(task.Id);
            Assert.NotNull(updatedTask);
            Assert.Equal(TaskOverallStatus.Failed, updatedTask.Status);

            var logs = await dbContext.ExecutionLogs.Where(l => l.TaskId == task.Id).ToListAsync();
            Assert.Contains(logs, l => l.Level == LogLevel.Error && l.Message.Contains("exceeded max retry limit"));
        }
    }

    [Fact]
    public async Task IntakeStage_WhenAskToolInvoked_SafelyPausesTaskForHumanClarification()
    {
        await using var env = CreateWorkflowEnvironment(targetSeed: 1200053);
        var (orchestrator, dbContext, connection, workspace, mockLlm, jobQueue) = env;
        {
            var task = new ForgeTask
            {
                WorkspaceId = workspace.Id,
                Title = "Feature needing clarification",
                Description = "Clarification test",
                Status = TaskOverallStatus.Open,
                CurrentStageId = StandardStageIds.TaskReview,
                BaseBranch = "main"
            };
            dbContext.Tasks.Add(task);
            await dbContext.SaveChangesAsync();

            // Agent calls ask tool
            mockLlm.StreamHandler = (ctx, msgs, opts) =>
            {
                var update = new ChatResponseUpdate
                {
                    Contents = new List<AIContent>
                    {
                        new FunctionCallContent("call_ask", CanonicalToolNames.Ask, new Dictionary<string, object?>
                        {
                            ["questions"] = new[]
                            {
                                new Dictionary<string, object?>
                                {
                                    ["prompt"] = "Should we support XML or only JSON format?",
                                    ["rationale"] = "Clarify format requirement",
                                    ["options"] = new[] { "JSON", "XML" }
                                }
                            }
                        })
                    }
                };
                return ToAsyncEnumerable(update);
            };

            await orchestrator.ProcessTaskAsync(task.Id);

            // Task must be safely paused in intake waiting for human
            var pausedTask = await dbContext.Tasks.FindAsync(task.Id);
            Assert.NotNull(pausedTask);
            Assert.True(pausedTask.IsWaitingForHuman);
            Assert.Equal(StandardStageIds.TaskReview, pausedTask.InterruptedStageId);

            // Verify no downstream execution was enqueued
            Assert.Empty(jobQueue.ImplementationQueue);
            Assert.Empty(jobQueue.CodeReviewQueue);

            // Submit human answer and resume
            await orchestrator.SubmitHumanAnswerAsync(task.Id, "Support only JSON format.");

            var resumedTask = await dbContext.Tasks.FindAsync(task.Id);
            Assert.NotNull(resumedTask);
            Assert.False(resumedTask.IsWaitingForHuman);

            // Now next LLM turn completes intake by updating task description
            mockLlm.StreamHandler = (ctx, msgs, opts) =>
            {
                task.Description = "Updated spec: JSON format only.";
                task.Version = 2;
                dbContext.SaveChanges();
                return ToAsyncEnumerable("Intake complete: Updated task spec for JSON format only.");
            };

            await orchestrator.ProcessTaskAsync(task.Id);

            // Task must now have advanced to Implementation queue
            Assert.Contains(task.Id, jobQueue.ImplementationQueue);
        }
    }

    [Fact]
    public async Task HappyPath_AutonomousFeatureLifecycle_LikeE2ETest_EverythingWorksFine()
    {
        // =========================================================================
        // HAPPY PATH OF E2E TEST (AutonomousFeatureLifecycleTests):
        // 1. Task: "Add a Multiply(int a, int b) method to Calculator with full XML documentation and corresponding unit tests in CalculatorTests.cs."
        // 2. Cohort 1 (Intake - TaskReview):
        //    Staff Architect investigates codebase & enriches task spec with implementation plan.
        // 3. Cohort 2 (Implementation Phase: PreImplBaseline -> Implementation -> PostImplGates):
        //    - Pre-Implementation Baseline: passes (2 tests pass).
        //    - Implementation: Senior Engineer implements Multiply method and unit test.
        //      Implementation prompt MUST NOT leak review diff.
        //    - Post-Implementation Gates: passes (3 tests pass).
        // 4. Cohort 3 (Review & Delivery Phase: CodeReviewAndFix -> PostReviewVerification -> AutoMerge):
        //    - Code Review: Principal Reviewer receives diff showing Multiply & unit test, approves cleanly.
        //    - Post-Review Verification: passes (3 tests pass).
        //    - AutoMerge: feature branch is squash-merged into 'main'.
        // 5. Verification:
        //    - Task overall status transitions cleanly to Closed.
        //    - All 6 canonical stages of StandardWorkflowDefinitions execute with 0 failures, 0 retries, 0 loopbacks.
        // =========================================================================

        const string featureDiff = """
diff --git a/src/SyntheticLib/Calculator.cs b/src/SyntheticLib/Calculator.cs
--- a/src/SyntheticLib/Calculator.cs
+++ b/src/SyntheticLib/Calculator.cs
@@ -10,2 +10,8 @@
+    /// <summary>
+    /// Computes the product of two integers.
+    /// </summary>
+    public int Multiply(int a, int b) => a * b;
diff --git a/tests/SyntheticLib.Tests/CalculatorTests.cs b/tests/SyntheticLib.Tests/CalculatorTests.cs
--- a/tests/SyntheticLib.Tests/CalculatorTests.cs
+++ b/tests/SyntheticLib.Tests/CalculatorTests.cs
@@ -20,2 +20,7 @@
+    [Fact]
+    public void Multiply_ReturnsCorrectProduct()
+    {
+        Assert.Equal(6, _calc.Multiply(2, 3));
+    }
""";
        const string featureDiffStat = " 2 files changed, 15 insertions(+), 0 deletions(-)";

        var mockGit = new Mock<IGitWorkspaceManager>();
        mockGit.Setup(m => m.CheckMergeableAsync(It.IsAny<Workspace>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MergeabilityCheckResult(true, Array.Empty<string>(), "base-sha", "feat-sha"));
        mockGit.Setup(m => m.SyncFromBaseBranchAsync(It.IsAny<Workspace>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MergeSyncResult(true, false, Array.Empty<string>(), "merge-sha"));
        mockGit.Setup(m => m.GetBranchShaAsync(It.IsAny<Workspace>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("commit-sha");
        mockGit.Setup(m => m.GenerateDiffStatAsync(It.IsAny<Workspace>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(featureDiffStat);
        mockGit.Setup(m => m.GenerateLiveDiffStatAsync(It.IsAny<Workspace>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(featureDiffStat);
        mockGit.Setup(m => m.GenerateDiffAsync(It.IsAny<Workspace>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(featureDiff);
        mockGit.Setup(m => m.GenerateLiveDiffAsync(It.IsAny<Workspace>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(featureDiff);
        mockGit.Setup(m => m.CreateWipCheckpointAsync(It.IsAny<Workspace>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("checkpoint-commit-sha");
        mockGit.Setup(m => m.SquashAndMergeAsync(It.IsAny<Workspace>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("squash-sha-multiply");
        mockGit.Setup(m => m.SquashAndMergeAsync(It.IsAny<Workspace>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("squash-sha-multiply");

        int testInvocationCount = 0;
        CommandResult HappyPathExecHandler(string command, string? workingDir)
        {
            if (command.Contains("dotnet test"))
            {
                testInvocationCount++;
                if (testInvocationCount == 1)
                {
                    // Pre-Implementation Baseline: 2 baseline tests pass
                    return new CommandResult(0, "Passed!  - échec : 0, réussite : 2, ignorée(s) : 0, total : 2", "", TimeSpan.FromMilliseconds(50));
                }

                // Post-Implementation Gates and Post-Review Verification: 3 tests pass (baseline + Multiply test)
                return new CommandResult(0, "Passed!  - échec : 0, réussite : 3, ignorée(s) : 0, total : 3", "", TimeSpan.FromMilliseconds(50));
            }

            return new CommandResult(0, "Build succeeded.", "", TimeSpan.FromMilliseconds(50));
        }

        await using var env = CreateWorkflowEnvironment(targetSeed: 1200055, execHandler: HappyPathExecHandler, gitManagerOverride: mockGit.Object);
        var (orchestrator, dbContext, connection, workspace, mockLlm, jobQueue) = env;
        {
            var task = new ForgeTask
            {
                WorkspaceId = workspace.Id,
                Title = "Add a Multiply(int a, int b) method to Calculator with full XML documentation and corresponding unit tests in CalculatorTests.cs.",
                Description = "Add a Multiply(int a, int b) method to Calculator with full XML documentation and corresponding unit tests in CalculatorTests.cs.",
                Status = TaskOverallStatus.Open,
                CurrentStageId = StandardStageIds.TaskReview,
                BaseBranch = "main",
                Priority = TaskPriority.Normal,
                MergeMode = MergeMode.Automatic
            };
            dbContext.Tasks.Add(task);
            await dbContext.SaveChangesAsync();

            mockLlm.StreamHandler = (ctx, msgs, opts) =>
            {
                if (ctx.StageId == StandardStageIds.TaskReview)
                {
                    task.Description = """
# Task Specification: Add Multiply method to Calculator

## Approved Architecture & Implementation Plan
1. Add `public int Multiply(int a, int b) => a * b;` to `SyntheticLib.Calculator` with complete XML summary and param doc comments.
2. Add unit test `Multiply_ReturnsCorrectProduct()` to `SyntheticLib.Tests.CalculatorTests` asserting `Assert.Equal(6, _calc.Multiply(2, 3));`.
3. Verify 100% test pass rate with dotnet test.

## Acceptance Criteria
- [x] Calculator.Multiply(a, b) computes integer product correctly.
- [x] Full XML documentation comments on public method.
- [x] Unit test verifying arithmetic output is included and passes.
""";
                    task.Version = 2;
                    dbContext.SaveChanges();
                    return ToAsyncEnumerable("Task Review complete: Formulated and documented approved architecture and implementation plan.");
                }

                if (ctx.StageId == StandardStageIds.Implementation)
                {
                    return ToAsyncEnumerable("Implementation complete: Implemented Multiply(int a, int b) with XML doc comments in Calculator.cs and added Multiply_ReturnsCorrectProduct unit test in CalculatorTests.cs.");
                }

                if (ctx.StageId == StandardStageIds.CodeReviewAndFix)
                {
                    return ToAsyncEnumerable("""
I have thoroughly reviewed the changes against base branch main and verified the task specification.

<approved>
Code review approved:
- Multiply(int a, int b) implementation is concise and correct.
- XML documentation is complete and accurate.
- Unit test Multiply_ReturnsCorrectProduct covers the new functionality and passes.
No regressions detected.
</approved>
""");
                }

                return ToAsyncEnumerable($"Executed stage '{ctx.StageId}' successfully.");
            };

            // =============================================================
            // Cohort 1: Intake Phase (Stage 1: task-review)
            // =============================================================
            await orchestrator.ProcessTaskAsync(task.Id);

            Assert.Contains(task.Id, jobQueue.ImplementationQueue);
            Assert.Empty(jobQueue.CodeReviewQueue);

            // =============================================================
            // Cohort 2: Implementation Phase (Stages 2 -> 3 -> 4)
            // =============================================================
            await orchestrator.ProcessTaskAsync(task.Id);

            Assert.Contains(task.Id, jobQueue.CodeReviewQueue);

            // =============================================================
            // Cohort 3: Review & Delivery Phase (Stages 5 -> 6 -> Auto-Merge)
            // =============================================================
            await orchestrator.ProcessTaskAsync(task.Id);

            var finalTask = await dbContext.Tasks.FindAsync(task.Id);
            Assert.NotNull(finalTask);
            Assert.Equal(TaskOverallStatus.Closed, finalTask.Status);

            // =============================================================
            // Deep Workflow Invariants & Prompt Integrity Checks
            // =============================================================
            Assert.Equal(3, mockLlm.Invocations.Count);

            // 1. Task Review Invocation
            var trInvocation = mockLlm.Invocations[0];
            Assert.Equal(StandardStageIds.TaskReview, trInvocation.Context.StageId);
            var trPrompt = string.Join("\n", trInvocation.Messages.Select(m => m.Text));
            Assert.Contains("Multiply", trPrompt);
            Assert.Contains("[ForgeTask Specification Document]", trPrompt);

            // 2. Implementation Invocation
            var implInvocation = mockLlm.Invocations[1];
            Assert.Equal(StandardStageIds.Implementation, implInvocation.Context.StageId);
            var implPrompt = string.Join("\n", implInvocation.Messages.Select(m => m.Text));
            Assert.Contains("[Approved Implementation Plan]", implPrompt);
            Assert.Contains("Formulated and documented approved architecture", implPrompt);
            // CRITICAL INVARIANT: Implementation prompt MUST NOT leak Git Diff
            Assert.DoesNotContain("[Git Diff Summary]", implPrompt);
            Assert.DoesNotContain("no changes", implPrompt);

            // 3. Code Review Invocation
            var crInvocation = mockLlm.Invocations[2];
            Assert.Equal(StandardStageIds.CodeReviewAndFix, crInvocation.Context.StageId);
            var crPrompt = string.Join("\n", crInvocation.Messages.Select(m => m.Text));
            // CRITICAL INVARIANT: Code Review prompt MUST contain the Git Diff of the feature
            Assert.Contains("diff --git a/src/SyntheticLib/Calculator.cs", crPrompt);
            Assert.Contains("public int Multiply(int a, int b)", crPrompt);
            Assert.Contains("Multiply_ReturnsCorrectProduct", crPrompt);

            // 4. Git Squash & Merge Invariant
            mockGit.Verify(m => m.SquashAndMergeAsync(
                It.Is<Workspace>(w => w.Id == workspace.Id),
                task.Id,
                "main",
                It.Is<string>(msg => msg.Contains("Multiply", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<CancellationToken>()),
                Times.Once);

            // 5. Execution Logs Verification: All 6 stages completed successfully
            var logs = await dbContext.ExecutionLogs.Where(l => l.TaskId == task.Id).ToListAsync();
            Assert.DoesNotContain(logs, l => l.Level == LogLevel.Error);
            Assert.DoesNotContain(logs, l => l.Level == LogLevel.Warning);
        }
    }

    [Fact]
    public async Task PostImplementationBuildFailure_LoopsBackInProcess_ProvidesCompilerDiagnostics_AndRecovers()
    {
        int buildInvocationCount = 0;
        CommandResult BuildFailureExecutionHandler(string command, string? workingDir)
        {
            if (command.Contains("dotnet build"))
            {
                buildInvocationCount++;
                if (buildInvocationCount == 1)
                {
                    // Attempt 1 fails with compilation error CS1002
                    var buildErrorOutput = """
Microsoft (R) Build Engine version 17.14.1
Copyright (C) Microsoft Corporation. All rights reserved.

  Determining projects to restore...
  All projects are up-to-date for restore.
src/Calculator.cs(42,15): error CS1002: ; expected [/home/runner/SyntheticLib.csproj]
src/Calculator.cs(45,9): error CS0103: The name 'temp' does not exist in the current context [/home/runner/SyntheticLib.csproj]

Build FAILED.
    0 Warning(s)
    2 Error(s)
""";
                    return new CommandResult(1, buildErrorOutput, "", TimeSpan.FromMilliseconds(80));
                }

                // Attempt 2 succeeds
                return new CommandResult(0, "Build succeeded.", "", TimeSpan.FromMilliseconds(50));
            }

            return new CommandResult(0, "Passed: 10, Failed: 0, Total: 10", "", TimeSpan.FromMilliseconds(50));
        }

        await using var env = CreateWorkflowEnvironment(targetSeed: 1200056, execHandler: BuildFailureExecutionHandler);
        var (orchestrator, dbContext, connection, workspace, mockLlm, jobQueue) = env;
        {
            var task = new ForgeTask
            {
                WorkspaceId = workspace.Id,
                Title = "Implement feature with syntax mistake",
                Description = "Task requiring compiler fix",
                Status = TaskOverallStatus.Open,
                CurrentStageId = StandardStageIds.Implementation,
                BaseBranch = "main",
                Priority = TaskPriority.Normal
            };
            dbContext.Tasks.Add(task);
            await dbContext.SaveChangesAsync();

            int agentTurn = 0;
            mockLlm.StreamHandler = (ctx, msgs, opts) =>
            {
                agentTurn++;
                if (agentTurn == 1)
                {
                    return ToAsyncEnumerable("Attempt 1: Implemented logic but made syntax error.");
                }

                return ToAsyncEnumerable("Attempt 2: Fixed semicolon and variable name.");
            };

            await orchestrator.ProcessTaskAsync(task.Id);

            Assert.True(mockLlm.Invocations.Count >= 2, $"Expected at least 2 LLM invocations for Implementation, got {mockLlm.Invocations.Count}");

            var retryInvocation = mockLlm.Invocations[1];
            Assert.Equal(StandardStageIds.Implementation, retryInvocation.Context.StageId);

            var retryPrompt = string.Join("\n", retryInvocation.Messages.Select(m =>
            {
                var textContents = string.Join("\n", m.Contents.OfType<TextContent>().Select(t => t.Text));
                var toolResultContents = string.Join("\n", m.Contents.OfType<FunctionResultContent>().Select(f => f.Result?.ToString() ?? ""));
                return string.Join("\n", new[] { m.Text, textContents, toolResultContents }.Where(s => !string.IsNullOrWhiteSpace(s)));
            }));

            // Compiler diagnostics must be provided to the implementation agent
            Assert.Contains("error CS1002: ; expected", retryPrompt);

            // Review diff must not leak into implementation retry prompt
            Assert.DoesNotContain("[Git Diff Summary]", retryPrompt);
            Assert.DoesNotContain("no changes", retryPrompt);

            // Gate passed on attempt 2 and enqueued Code Review
            Assert.Contains(task.Id, jobQueue.CodeReviewQueue);
        }
    }

    [Fact]
    public async Task PostImplementationTestCountRegression_LoopsBackInProcess_WithAntiCheatingDiagnostic_AndRecovers()
    {
        int testRunCount = 0;
        CommandResult TestCountRegressionExecutionHandler(string command, string? workingDir)
        {
            if (command.Contains("dotnet test"))
            {
                testRunCount++;
                if (testRunCount == 1)
                {
                    // Pre-Implementation Baseline: 10 tests exist
                    return new CommandResult(0, "Passed!  - échec : 0, réussite : 10, total : 10", "", TimeSpan.FromMilliseconds(50));
                }

                if (testRunCount == 2)
                {
                    // Post-Implementation Gates: Only 7 tests run (anti-cheating regression: test count decreased!)
                    return new CommandResult(0, "Passed!  - échec : 0, réussite : 7, total : 7", "", TimeSpan.FromMilliseconds(50));
                }

                // Post-Implementation Gates retry: 12 tests run (regression resolved + new tests added)
                return new CommandResult(0, "Passed!  - échec : 0, réussite : 12, total : 12", "", TimeSpan.FromMilliseconds(50));
            }

            return new CommandResult(0, "Build succeeded.", "", TimeSpan.FromMilliseconds(50));
        }

        await using var env = CreateWorkflowEnvironment(targetSeed: 1200057, execHandler: TestCountRegressionExecutionHandler);
        var (orchestrator, dbContext, connection, workspace, mockLlm, jobQueue) = env;
        {
            // Start at Pre-Implementation Baseline to establish baseline total of 10 tests
            var task = new ForgeTask
            {
                WorkspaceId = workspace.Id,
                Title = "Feature with test deletion regression",
                Description = "Anti-cheating verification",
                Status = TaskOverallStatus.Open,
                CurrentStageId = StandardStageIds.PreImplementationBaseline,
                BaseBranch = "main",
                Priority = TaskPriority.Normal
            };
            dbContext.Tasks.Add(task);
            await dbContext.SaveChangesAsync();

            int agentTurn = 0;
            mockLlm.StreamHandler = (ctx, msgs, opts) =>
            {
                agentTurn++;
                if (agentTurn == 1)
                {
                    return ToAsyncEnumerable("Attempt 1: Implemented feature but accidentally commented out tests.");
                }

                return ToAsyncEnumerable("Attempt 2: Restored commented tests and added new unit tests.");
            };

            await orchestrator.ProcessTaskAsync(task.Id);

            Assert.True(mockLlm.Invocations.Count >= 2, $"Expected at least 2 LLM invocations for Implementation, got {mockLlm.Invocations.Count}");

            var retryInvocation = mockLlm.Invocations[1];
            Assert.Equal(StandardStageIds.Implementation, retryInvocation.Context.StageId);

            var retryPrompt = string.Join("\n", retryInvocation.Messages.Select(m =>
            {
                var textContents = string.Join("\n", m.Contents.OfType<TextContent>().Select(t => t.Text));
                var toolResultContents = string.Join("\n", m.Contents.OfType<FunctionResultContent>().Select(f => f.Result?.ToString() ?? ""));
                return string.Join("\n", new[] { m.Text, textContents, toolResultContents }.Where(s => !string.IsNullOrWhiteSpace(s)));
            }));

            // Anti-cheating violation must be present in the failure reason provided to the agent
            Assert.Contains("Anti-cheating violation: total test count decreased from 10 to 7", retryPrompt);

            // Review diff must not leak
            Assert.DoesNotContain("[Git Diff Summary]", retryPrompt);

            // On attempt 2, tests reached 12, gate passed, and task advanced to CodeReviewQueue
            Assert.Contains(task.Id, jobQueue.CodeReviewQueue);
        }
    }

    [Fact]
    public async Task SquashAndMergeConflict_RoutesToImplementation_WithConflictResolutionDirective_AndRecovers()
    {
        int mergeAttempt = 0;
        bool conflictActive = false;
        var mockGit = new Mock<IGitWorkspaceManager>();
        mockGit.Setup(m => m.CheckMergeableAsync(It.IsAny<Workspace>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MergeabilityCheckResult(true, Array.Empty<string>(), "base-sha", "feat-sha"));
        mockGit.Setup(m => m.SyncFromBaseBranchAsync(It.IsAny<Workspace>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Workspace, int, string, CancellationToken>((w, id, b, ct) =>
            {
                if (conflictActive)
                {
                    return Task.FromResult(new MergeSyncResult(false, true, new[] { "src/Calculator.cs", "tests/CalculatorTests.cs" }, null));
                }
                return Task.FromResult(new MergeSyncResult(true, false, Array.Empty<string>(), "merge-sha"));
            });
        mockGit.Setup(m => m.GetBranchShaAsync(It.IsAny<Workspace>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("commit-sha");
        mockGit.Setup(m => m.GenerateDiffStatAsync(It.IsAny<Workspace>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(" 1 file changed, 5 insertions(+)");
        mockGit.Setup(m => m.GenerateLiveDiffStatAsync(It.IsAny<Workspace>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(" 1 file changed, 5 insertions(+)");
        mockGit.Setup(m => m.GenerateDiffAsync(It.IsAny<Workspace>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("diff --git a/src/Calculator.cs b/src/Calculator.cs\n+new line");
        mockGit.Setup(m => m.GenerateLiveDiffAsync(It.IsAny<Workspace>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("diff --git a/src/Calculator.cs b/src/Calculator.cs\n+new line");
        mockGit.Setup(m => m.CreateWipCheckpointAsync(It.IsAny<Workspace>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("checkpoint-commit-sha");

        mockGit.Setup(m => m.SquashAndMergeAsync(It.IsAny<Workspace>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Workspace, int, string, string, CancellationToken>((w, id, b, msg, ct) =>
            {
                mergeAttempt++;
                if (mergeAttempt == 1)
                {
                    conflictActive = true;
                    // Attempt 1 fails with merge conflict
                    throw new MergeConflictException(id, b, new[] { "src/Calculator.cs", "tests/CalculatorTests.cs" }, "Merge conflict in src/Calculator.cs");
                }
                return Task.FromResult("squash-sha-resolved");
            });
        mockGit.Setup(m => m.SquashAndMergeAsync(It.IsAny<Workspace>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns<Workspace, int, string, string, bool, CancellationToken>((w, id, b, msg, a, ct) =>
            {
                mergeAttempt++;
                if (mergeAttempt == 1)
                {
                    conflictActive = true;
                    throw new MergeConflictException(id, b, new[] { "src/Calculator.cs", "tests/CalculatorTests.cs" }, "Merge conflict in src/Calculator.cs");
                }
                return Task.FromResult("squash-sha-resolved");
            });

        await using var env = CreateWorkflowEnvironment(targetSeed: 1200058, gitManagerOverride: mockGit.Object);
        var (orchestrator, dbContext, connection, workspace, mockLlm, jobQueue) = env;
        {
            var task = new ForgeTask
            {
                WorkspaceId = workspace.Id,
                Title = "Feature encountering merge conflict",
                Description = "Conflict resolution test",
                Status = TaskOverallStatus.Open,
                CurrentStageId = StandardStageIds.CodeReviewAndFix,
                BaseBranch = "main",
                MergeMode = MergeMode.Automatic
            };
            dbContext.Tasks.Add(task);
            await dbContext.SaveChangesAsync();

            mockLlm.StreamHandler = (ctx, msgs, opts) =>
            {
                return ToAsyncEnumerable("<approved>\nApproved for auto-merge.\n</approved>");
            };

            // 1. Process Review & Delivery Cohort: CodeReview approves, PostReviewVerification passes,
            //    SquashAndMerge throws MergeConflictException -> conflict resolution is initiated!
            await orchestrator.ProcessTaskAsync(task.Id);

            // Verify task was routed back to Implementation queue
            Assert.Contains(task.Id, jobQueue.ImplementationQueue);

            var conflictedTask = await dbContext.Tasks.FindAsync(task.Id);
            Assert.NotNull(conflictedTask);
            Assert.Equal(StandardStageIds.Implementation, conflictedTask.CurrentStageId);
            Assert.Equal(TaskOverallStatus.Open, conflictedTask.Status);

            // 2. Process Implementation Cohort to resolve conflict
            mockLlm.Invocations.Clear();
            mockLlm.StreamHandler = (ctx, msgs, opts) =>
            {
                conflictActive = false;
                return ToAsyncEnumerable("Resolved git merge conflict between main and feature branch.");
            };

            await orchestrator.ProcessTaskAsync(task.Id);

            // Verify Implementation prompt received the Conflict Resolution Directive
            Assert.NotEmpty(mockLlm.Invocations);
            var implPrompt = string.Join("\n", mockLlm.Invocations[0].Messages.Select(m => m.Text));
            Assert.Contains(ConflictResolutionConstants.ConflictDirectiveHeader, implPrompt);
            Assert.Contains("src/Calculator.cs", implPrompt);
            Assert.Contains("tests/CalculatorTests.cs", implPrompt);

            // Verified implementation prompt does not leak review diff
            Assert.DoesNotContain("[Git Diff Summary]", implPrompt);

            // Implementation finished and enqueued to CodeReviewQueue
            Assert.Contains(task.Id, jobQueue.CodeReviewQueue);

            // 3. Process Code Review & Delivery Cohort: SquashAndMerge succeeds on attempt 2
            mockLlm.StreamHandler = (ctx, msgs, opts) =>
            {
                return ToAsyncEnumerable("<approved>\nConflict resolution verified and approved.\n</approved>");
            };

            await orchestrator.ProcessTaskAsync(task.Id);

            var finalTask = await dbContext.Tasks.FindAsync(task.Id);
            Assert.NotNull(finalTask);
            Assert.Equal(TaskOverallStatus.Closed, finalTask.Status);
        }
    }

    [Fact]
    public async Task PostReviewVerificationFailure_LoopsBackCrossCohort_ToImplementation_AndRecovers()
    {
        int testCallCount = 0;
        CommandResult VerificationFailureExecutionHandler(string command, string? workingDir)
        {
            if (command.Contains("dotnet test"))
            {
                testCallCount++;
                if (testCallCount == 1)
                {
                    // Attempt 1 during PostReviewVerification fails
                    return new CommandResult(1, "Failed TestRegressed. Total: 10, Failed: 1.", "", TimeSpan.FromMilliseconds(50));
                }

                // Attempt 2 succeeds
                return new CommandResult(0, "Passed! Total: 10, Failed: 0.", "", TimeSpan.FromMilliseconds(50));
            }

            return new CommandResult(0, "Build succeeded.", "", TimeSpan.FromMilliseconds(50));
        }

        await using var env = CreateWorkflowEnvironment(targetSeed: 1200059, execHandler: VerificationFailureExecutionHandler);
        var (orchestrator, dbContext, connection, workspace, mockLlm, jobQueue) = env;
        {
            var task = new ForgeTask
            {
                WorkspaceId = workspace.Id,
                Title = "Feature failing post-review verification",
                Description = "Post-review verification failure test",
                Status = TaskOverallStatus.Open,
                CurrentStageId = StandardStageIds.CodeReviewAndFix,
                BaseBranch = "main",
                MergeMode = MergeMode.Automatic
            };
            dbContext.Tasks.Add(task);
            await dbContext.SaveChangesAsync();

            mockLlm.StreamHandler = (ctx, msgs, opts) =>
            {
                return ToAsyncEnumerable("<approved>\nReview approved, ready for verification.\n</approved>");
            };

            // 1. Process Review Cohort: CodeReview approves, PostReviewVerification fails tests!
            await orchestrator.ProcessTaskAsync(task.Id);

            // Cross-cohort loopback must enqueue task in ImplementationQueue
            Assert.Contains(task.Id, jobQueue.ImplementationQueue);

            var updatedTask = await dbContext.Tasks.FindAsync(task.Id);
            Assert.NotNull(updatedTask);
            Assert.Equal(StandardStageIds.Implementation, updatedTask.CurrentStageId);

            // 2. Process Implementation Cohort to fix the regressed test
            mockLlm.Invocations.Clear();
            mockLlm.StreamHandler = (ctx, msgs, opts) =>
            {
                return ToAsyncEnumerable("Fixed regressed test detected during post-review verification.");
            };

            await orchestrator.ProcessTaskAsync(task.Id);

            // Implementation prompt must not leak review diff
            var implPrompt = string.Join("\n", mockLlm.Invocations[0].Messages.Select(m => m.Text));
            Assert.DoesNotContain("[Git Diff Summary]", implPrompt);

            // Implementation passed and advanced to CodeReviewQueue
            Assert.Contains(task.Id, jobQueue.CodeReviewQueue);

            // 3. Process Review Cohort again: now verification passes and task is closed!
            mockLlm.StreamHandler = (ctx, msgs, opts) =>
            {
                return ToAsyncEnumerable("<approved>\nSecond review approved.\n</approved>");
            };

            await orchestrator.ProcessTaskAsync(task.Id);

            var closedTask = await dbContext.Tasks.FindAsync(task.Id);
            Assert.NotNull(closedTask);
            Assert.Equal(TaskOverallStatus.Closed, closedTask.Status);
        }
    }

    [Fact]
    public async Task MergeModeManualReviewRequired_WhenReviewApproved_TransitionsToReadyInsteadOfClosed()
    {
        await using var env = CreateWorkflowEnvironment(targetSeed: 1200060);
        var (orchestrator, dbContext, connection, workspace, mockLlm, jobQueue) = env;
        {
            var task = new ForgeTask
            {
                WorkspaceId = workspace.Id,
                Title = "Feature requiring maintainer manual review",
                Description = "Task configured with MergeMode.ManualReviewRequired",
                Status = TaskOverallStatus.Open,
                CurrentStageId = StandardStageIds.CodeReviewAndFix,
                BaseBranch = "main",
                MergeMode = MergeMode.ManualReviewRequired
            };
            dbContext.Tasks.Add(task);
            await dbContext.SaveChangesAsync();

            mockLlm.StreamHandler = (ctx, msgs, opts) =>
            {
                return ToAsyncEnumerable("<approved>\nApproved for merge review.\n</approved>");
            };

            await orchestrator.ProcessTaskAsync(task.Id);

            var updatedTask = await dbContext.Tasks.FindAsync(task.Id);
            Assert.NotNull(updatedTask);
            Assert.Equal(TaskOverallStatus.Ready, updatedTask.Status);
            Assert.Null(updatedTask.SquashCommitSha);
            Assert.Empty(jobQueue.CodeReviewQueue);
            Assert.Empty(jobQueue.ImplementationQueue);
        }
    }

    [Fact]
    public async Task PreImplementationBaselineFailure_TransitionsImmediatelyToFailed_WithoutLoopback()
    {
        CommandResult BaselineFailureHandler(string command, string? workingDir)
        {
            if (command.Contains("dotnet test") || command.Contains("dotnet build"))
            {
                return new CommandResult(1, "Build failed on main baseline.", "CSC : error CS0103: The name 'DoesNotExist' does not exist in the current context", TimeSpan.FromMilliseconds(50));
            }
            return new CommandResult(0, "Build succeeded.", "", TimeSpan.FromMilliseconds(50));
        }

        await using var env = CreateWorkflowEnvironment(targetSeed: 1200061, execHandler: BaselineFailureHandler);
        var (orchestrator, dbContext, connection, workspace, mockLlm, jobQueue) = env;
        {
            var task = new ForgeTask
            {
                WorkspaceId = workspace.Id,
                Title = "Feature failing baseline",
                Description = "Baseline failure test",
                Status = TaskOverallStatus.Open,
                CurrentStageId = StandardStageIds.PreImplementationBaseline,
                BaseBranch = "main",
                MergeMode = MergeMode.Automatic
            };
            dbContext.Tasks.Add(task);
            await dbContext.SaveChangesAsync();

            await orchestrator.ProcessTaskAsync(task.Id);

            var updatedTask = await dbContext.Tasks.FindAsync(task.Id);
            Assert.NotNull(updatedTask);
            Assert.Equal(TaskOverallStatus.Failed, updatedTask.Status);
            Assert.Empty(jobQueue.ImplementationQueue);
            Assert.Empty(jobQueue.CodeReviewQueue);
        }
    }

    [Fact]
    public async Task Gate2ReverificationFailure_BaseBranchAdvanced_LoopsBackCrossCohortToImplementation()
    {
        var mockGit = new Mock<IGitWorkspaceManager>();
        mockGit.Setup(m => m.CheckMergeableAsync(It.IsAny<Workspace>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MergeabilityCheckResult(true, Array.Empty<string>(), "base-sha-1", "feat-sha-1"));
        mockGit.Setup(m => m.SyncFromBaseBranchAsync(It.IsAny<Workspace>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MergeSyncResult(true, false, Array.Empty<string>(), "merge-commit-sha-2"));
        mockGit.Setup(m => m.GetBranchShaAsync(It.IsAny<Workspace>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("feat-sha-1");
        mockGit.Setup(m => m.GenerateDiffStatAsync(It.IsAny<Workspace>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(" 1 file changed, 1 insertion(+)");
        mockGit.Setup(m => m.GenerateLiveDiffStatAsync(It.IsAny<Workspace>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(" 1 file changed, 1 insertion(+)");
        mockGit.Setup(m => m.GenerateDiffAsync(It.IsAny<Workspace>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("diff --git a/File.cs b/File.cs\n+line");
        mockGit.Setup(m => m.GenerateLiveDiffAsync(It.IsAny<Workspace>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("diff --git a/File.cs b/File.cs\n+line");
        mockGit.Setup(m => m.CreateWipCheckpointAsync(It.IsAny<Workspace>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("checkpoint-sha");

        int testCalls = 0;
        CommandResult ReverificationExecHandler(string command, string? workingDir)
        {
            if (command.Contains("dotnet test"))
            {
                testCalls++;
                if (testCalls > 1)
                {
                    // Re-verification test failure!
                    return new CommandResult(1, "Failed ReverifiedRegression. Total: 10, Failed: 1.", "", TimeSpan.FromMilliseconds(50));
                }
            }
            return new CommandResult(0, "Passed: 10, Failed: 0, Total: 10", "", TimeSpan.FromMilliseconds(50));
        }

        await using var env = CreateWorkflowEnvironment(targetSeed: 1200062, execHandler: ReverificationExecHandler, gitManagerOverride: mockGit.Object);
        var (orchestrator, dbContext, connection, workspace, mockLlm, jobQueue) = env;
        {
            var task = new ForgeTask
            {
                WorkspaceId = workspace.Id,
                Title = "Feature experiencing base branch advance during Gate 2",
                Description = "Re-verification failure test",
                Status = TaskOverallStatus.Open,
                CurrentStageId = StandardStageIds.CodeReviewAndFix,
                BaseBranch = "main",
                MergeMode = MergeMode.Automatic
            };
            dbContext.Tasks.Add(task);
            await dbContext.SaveChangesAsync();

            mockLlm.StreamHandler = (ctx, msgs, opts) =>
            {
                return ToAsyncEnumerable("<approved>\nApproved for Gate 2.\n</approved>");
            };

            await orchestrator.ProcessTaskAsync(task.Id);

            var updatedTask = await dbContext.Tasks.FindAsync(task.Id);
            Assert.NotNull(updatedTask);
            Assert.Equal(StandardStageIds.Implementation, updatedTask.CurrentStageId);
            Assert.Contains(task.Id, jobQueue.ImplementationQueue);
        }
    }

    [Fact]
    public async Task ImplementationStage_WhenAskToolInvoked_SafelyPausesImplementationCohort_AndResumesOnAnswer()
    {
        await using var env = CreateWorkflowEnvironment(targetSeed: 1200063);
        var (orchestrator, dbContext, connection, workspace, mockLlm, jobQueue) = env;
        {
            var task = new ForgeTask
            {
                WorkspaceId = workspace.Id,
                Title = "Feature requiring human clarification during implementation",
                Description = "Implementation ask tool test",
                Status = TaskOverallStatus.Open,
                CurrentStageId = StandardStageIds.Implementation,
                BaseBranch = "main",
                MergeMode = MergeMode.Automatic
            };
            dbContext.Tasks.Add(task);
            await dbContext.SaveChangesAsync();

            // 1. Implementation agent invokes 'ask' tool
            mockLlm.StreamHandler = (ctx, msgs, opts) =>
            {
                var askCall = new FunctionCallContent(
                    "call_ask_impl",
                    CanonicalToolNames.Ask,
                    new Dictionary<string, object?>
                    {
                        ["questions"] = new[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["prompt"] = "Should caching be in-memory or Redis?",
                                ["rationale"] = "Clarify caching technology",
                                ["options"] = new[] { "In-Memory MemoryCache", "Distributed Redis" }
                            }
                        }
                    });

                return ToAsyncEnumerable(new ChatResponseUpdate { Contents = [askCall] });
            };

            await orchestrator.ProcessTaskAsync(task.Id);

            // Verify task paused safely
            var pausedTask = await dbContext.Tasks.FindAsync(task.Id);
            Assert.NotNull(pausedTask);
            Assert.True(pausedTask.IsWaitingForHuman);
            Assert.Equal(StandardStageIds.Implementation, pausedTask.CurrentStageId);
            Assert.Equal(StandardStageIds.Implementation, pausedTask.InterruptedStageId);

            // Free worker invariant: ImplementationQueue must NOT contain task while waiting for human
            Assert.DoesNotContain(task.Id, jobQueue.ImplementationQueue);

            // 2. Maintainer provides human answer
            var question = (await dbContext.Tasks.Include(t => t.Questions).FirstAsync(t => t.Id == task.Id)).Questions.First();
            await orchestrator.SubmitHumanAnswerAsync(question.Id, "In-Memory MemoryCache");

            // 3. Resume task execution in Implementation queue
            mockLlm.Invocations.Clear();
            mockLlm.StreamHandler = (ctx, msgs, opts) =>
            {
                return ToAsyncEnumerable("Completed caching implementation using In-Memory MemoryCache as answered.");
            };

            await orchestrator.ProcessTaskAsync(task.Id);

            // Verify Implementation received the answered question in chat context
            Assert.NotEmpty(mockLlm.Invocations);
            var hasAnswer = mockLlm.Invocations[0].Messages.Any(m =>
                (m.Text != null && m.Text.Contains("In-Memory MemoryCache")) ||
                m.Contents.OfType<FunctionResultContent>().Any(frc => frc.Result?.ToString()?.Contains("In-Memory MemoryCache") == true));
            Assert.True(hasAnswer);

            // Implementation finished and enqueued to CodeReviewQueue
            var resumedTask = await dbContext.Tasks.FindAsync(task.Id);
            Assert.NotNull(resumedTask);
            Assert.False(resumedTask.IsWaitingForHuman);
            Assert.Contains(task.Id, jobQueue.CodeReviewQueue);
        }
    }

    [Fact]
    public async Task CooperativeUrgentPreemption_BetweenStages_YieldsGracefullyAndPreservesUnexecutedStage()
    {
        await using var env = CreateWorkflowEnvironment(targetSeed: 1200064);
        var (orchestrator, dbContext, connection, workspace, mockLlm, jobQueue) = env;
        {
            var task = new ForgeTask
            {
                WorkspaceId = workspace.Id,
                Title = "Feature being preempted by urgent job",
                Description = "Urgent preemption test",
                Status = TaskOverallStatus.Open,
                CurrentStageId = StandardStageIds.PreImplementationBaseline,
                BaseBranch = "main",
                Priority = 2,
                MergeMode = MergeMode.Automatic
            };
            dbContext.Tasks.Add(task);
            await dbContext.SaveChangesAsync();

            // Urgent P1 job arrives
            jobQueue.EnqueueUrgent(999);

            await orchestrator.ProcessTaskAsync(task.Id);

            // Task must have completed Stage 2 (baseline), and yielded before Stage 3 (Implementation)
            var yieldedTask = await dbContext.Tasks.FindAsync(task.Id);
            Assert.NotNull(yieldedTask);
            Assert.Equal(StandardStageIds.Implementation, yieldedTask.CurrentStageId);

            // Preempted task re-enqueued to its tier queue so worker can execute urgent job first
            Assert.Contains(task.Id, jobQueue.ImplementationQueue);

            // Implementation stage (LLM) was not executed yet
            Assert.Empty(mockLlm.Invocations);

            // Clear urgent queue and resume: Stage 3 executes now
            jobQueue.UrgentQueue.Clear();
            mockLlm.StreamHandler = (ctx, msgs, opts) =>
            {
                return ToAsyncEnumerable("Implemented feature after urgent preemption yield.");
            };

            await orchestrator.ProcessTaskAsync(task.Id);

            Assert.NotEmpty(mockLlm.Invocations);
            Assert.Contains(task.Id, jobQueue.CodeReviewQueue);
        }
    }

    [Fact]
    public async Task GlobalOperationsPause_BetweenStages_DefersTaskAndExitsWithoutReEnqueuing()
    {
        bool isPaused = false;
        var mockOps = new Mock<IOperationsControlService>();
        mockOps.Setup(o => o.IsPaused).Returns(() => isPaused);
        mockOps.Setup(o => o.ReclaimStatus).Returns(OperationsReclaimStatus.Idle);

        CommandResult PauseTriggeringExecHandler(string command, string? workingDir)
        {
            if (command.Contains("dotnet test") || command.Contains("dotnet build"))
            {
                // Pause activated during baseline execution
                isPaused = true;
            }
            return new CommandResult(0, "Passed: 10, Failed: 0, Total: 10", "", TimeSpan.FromMilliseconds(50));
        }

        await using var env = CreateWorkflowEnvironment(targetSeed: 1200065, execHandler: PauseTriggeringExecHandler, opsControlOverride: mockOps.Object);
        var (orchestrator, dbContext, connection, workspace, mockLlm, jobQueue) = env;
        {
            var task = new ForgeTask
            {
                WorkspaceId = workspace.Id,
                Title = "Feature deferred by operations pause",
                Description = "Operations pause test",
                Status = TaskOverallStatus.Open,
                CurrentStageId = StandardStageIds.PreImplementationBaseline,
                BaseBranch = "main",
                MergeMode = MergeMode.Automatic
            };
            dbContext.Tasks.Add(task);
            await dbContext.SaveChangesAsync();

            await orchestrator.ProcessTaskAsync(task.Id);

            var pausedTask = await dbContext.Tasks.FindAsync(task.Id);
            Assert.NotNull(pausedTask);
            Assert.True(pausedTask.IsDeferredByPause);
            Assert.Equal(StandardStageIds.Implementation, pausedTask.CurrentStageId);

            // Paused tasks MUST NOT be re-enqueued to inference queues
            Assert.DoesNotContain(task.Id, jobQueue.ImplementationQueue);
            Assert.Empty(mockLlm.Invocations);
        }
    }

    [Fact]
    public async Task TaskCancellation_AbortsProcessing_SetsStatusCancelledAndClosesConversations()
    {
        var cancellationManager = new TaskCancellationManager(NullLogger<TaskCancellationManager>.Instance);

        await using var env = CreateWorkflowEnvironment(targetSeed: 1200066, cancellationManagerOverride: cancellationManager);
        var (orchestrator, dbContext, connection, workspace, mockLlm, jobQueue) = env;
        {
            var task = new ForgeTask
            {
                WorkspaceId = workspace.Id,
                Title = "Feature cancelled during implementation",
                Description = "Cancellation test",
                Status = TaskOverallStatus.Open,
                CurrentStageId = StandardStageIds.Implementation,
                BaseBranch = "main",
                MergeMode = MergeMode.Automatic
            };
            dbContext.Tasks.Add(task);
            await dbContext.SaveChangesAsync();

            mockLlm.StreamHandler = (ctx, msgs, opts) =>
            {
                // User clicks cancel in UI
                cancellationManager.Cancel(task.Id);
                throw new OperationCanceledException();
            };

            await orchestrator.ProcessTaskAsync(task.Id);

            var cancelledTask = await dbContext.Tasks.FindAsync(task.Id);
            Assert.NotNull(cancelledTask);
            Assert.Equal(TaskOverallStatus.Cancelled, cancelledTask.Status);
            Assert.False(cancelledTask.IsWaitingForHuman);
            Assert.DoesNotContain(task.Id, jobQueue.CodeReviewQueue);
        }
    }

    [Fact]
    public async Task RepeatedMergeConflicts_ExceedingLifetimeLimit_EscalatesToHumanMaintainer()
    {
        var mockGit = new Mock<IGitWorkspaceManager>();
        mockGit.Setup(m => m.CheckMergeableAsync(It.IsAny<Workspace>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MergeabilityCheckResult(false, new[] { "src/ConflictFile.cs" }, "base", "feat"));
        mockGit.Setup(m => m.GetBranchShaAsync(It.IsAny<Workspace>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("feat-sha");
        mockGit.Setup(m => m.GenerateDiffStatAsync(It.IsAny<Workspace>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(" 1 file changed, 1 insertion(+)");
        mockGit.Setup(m => m.CreateWipCheckpointAsync(It.IsAny<Workspace>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("checkpoint-sha");

        await using var env = CreateWorkflowEnvironment(targetSeed: 1200067, gitManagerOverride: mockGit.Object);
        var (orchestrator, dbContext, connection, workspace, mockLlm, jobQueue) = env;
        {
            var task = new ForgeTask
            {
                WorkspaceId = workspace.Id,
                Title = "Feature with repeated merge conflicts",
                Description = "Lifetime conflict limit test",
                Status = TaskOverallStatus.Open,
                CurrentStageId = StandardStageIds.CodeReviewAndFix,
                BaseBranch = "main",
                MergeMode = MergeMode.Automatic
            };

            // Lifetime conflict detections already at 2 (third conflict will hit MaxLifetimeConflictDetections = 3)
            var bag = new PipelineDataBag();
            bag.Set(PipelineDataBagKeys.LifetimeConflictDetections, 2);
            task.PipelineDataJson = bag.ToJson();

            dbContext.Tasks.Add(task);
            await dbContext.SaveChangesAsync();

            mockLlm.StreamHandler = (ctx, msgs, opts) =>
            {
                return ToAsyncEnumerable("<approved>\nApproved, proceed to merge.\n</approved>");
            };

            await orchestrator.ProcessTaskAsync(task.Id);

            var escalatedTask = await dbContext.Tasks.FindAsync(task.Id);
            Assert.NotNull(escalatedTask);
            Assert.True(escalatedTask.IsWaitingForHuman);
            Assert.Equal(StandardStageIds.Implementation, escalatedTask.InterruptedStageId);
            Assert.Empty(jobQueue.ImplementationQueue);
        }
    }

    [Fact]
    public async Task PostReviewVerificationFailure_ExceedingMaxRetries_TransitionsTaskToFailed()
    {
        int testCalls = 0;
        CommandResult FailingVerificationExecHandler(string command, string? workingDir)
        {
            if (command.Contains("dotnet test"))
            {
                testCalls++;
                // Attempt 1: PostReviewVerification (call 1) fails -> loops back to Implementation
                // Attempt 2: PostImplementationGates (call 2) succeeds -> advances to CodeReviewQueue
                // Attempt 3: PostReviewVerification (call 3) fails again -> exceeds MaxRetries 1 -> Failed
                if (testCalls != 2)
                {
                    return new CommandResult(1, "Failed TestUnrecoverable. Total: 10, Failed: 1.", "", TimeSpan.FromMilliseconds(50));
                }
                return new CommandResult(0, "Passed: 10, Failed: 0, Total: 10", "", TimeSpan.FromMilliseconds(50));
            }
            return new CommandResult(0, "Build succeeded.", "", TimeSpan.FromMilliseconds(50));
        }

        await using var env = CreateWorkflowEnvironment(targetSeed: 1200068, execHandler: FailingVerificationExecHandler);
        var (orchestrator, dbContext, connection, workspace, mockLlm, jobQueue) = env;
        {
            var task = new ForgeTask
            {
                WorkspaceId = workspace.Id,
                Title = "Feature failing verification repeatedly",
                Description = "Verification max retries test",
                Status = TaskOverallStatus.Open,
                CurrentStageId = StandardStageIds.CodeReviewAndFix,
                BaseBranch = "main",
                MergeMode = MergeMode.Automatic
            };

            // Configure MaxRetries = 1
            var bag = new PipelineDataBag();
            bag.Set(PipelineDataBagKeys.MaxRetries, 1);
            task.PipelineDataJson = bag.ToJson();

            dbContext.Tasks.Add(task);
            await dbContext.SaveChangesAsync();

            mockLlm.StreamHandler = (ctx, msgs, opts) =>
            {
                return ToAsyncEnumerable("<approved>\nFirst review approved.\n</approved>");
            };

            // Attempt 1: Review approves, PostReviewVerification fails -> loops back cross-cohort (retry 1/1)
            await orchestrator.ProcessTaskAsync(task.Id);

            var retryTask = await dbContext.Tasks.FindAsync(task.Id);
            Assert.NotNull(retryTask);
            Assert.Equal(StandardStageIds.Implementation, retryTask.CurrentStageId);
            Assert.Contains(task.Id, jobQueue.ImplementationQueue);

            // Attempt 2: Implementation completes and enqueues to CodeReview
            jobQueue.ImplementationQueue.Clear();
            mockLlm.StreamHandler = (ctx, msgs, opts) =>
            {
                return ToAsyncEnumerable("Attempted fix for failing verification test.");
            };
            await orchestrator.ProcessTaskAsync(task.Id);
            Assert.Contains(task.Id, jobQueue.CodeReviewQueue);

            // Attempt 3: Review approves, but verification fails second time (retry 2 > MaxRetries 1) -> Fails!
            mockLlm.StreamHandler = (ctx, msgs, opts) =>
            {
                return ToAsyncEnumerable("<approved>\nSecond review approved.\n</approved>");
            };
            await orchestrator.ProcessTaskAsync(task.Id);

            var failedTask = await dbContext.Tasks.FindAsync(task.Id);
            Assert.NotNull(failedTask);
            Assert.Equal(TaskOverallStatus.Failed, failedTask.Status);
        }
    }

    [Fact]
    public async Task CodeReviewRejections_ExceedingMaxRetries_TransitionsTaskToFailed()
    {
        await using var env = CreateWorkflowEnvironment(targetSeed: 1200069);
        var (orchestrator, dbContext, connection, workspace, mockLlm, jobQueue) = env;

        var task = new ForgeTask
        {
            WorkspaceId = workspace.Id,
            Title = "Feature failing code review repeatedly",
            Description = "Review retry exhaustion test",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.CodeReviewAndFix,
            BaseBranch = "main"
        };

        var bag = new PipelineDataBag();
        bag.Set(PipelineDataBagKeys.MaxRetries, 1);
        task.PipelineDataJson = bag.ToJson();

        dbContext.Tasks.Add(task);
        await dbContext.SaveChangesAsync();

        // Attempt 1: Reviewer requests changes (retry 1/1 -> routes to Implementation)
        mockLlm.StreamHandler = (ctx, msgs, opts) =>
        {
            return ToAsyncEnumerable("<request_changes>\nFirst review rejection.\n</request_changes>");
        };

        await orchestrator.ProcessTaskAsync(task.Id);

        var retryTask = await dbContext.Tasks.FindAsync(task.Id);
        Assert.NotNull(retryTask);
        Assert.Equal(StandardStageIds.Implementation, retryTask.CurrentStageId);
        Assert.Contains(task.Id, jobQueue.ImplementationQueue);

        // Attempt 2: Implementation completes and routes back to CodeReview
        jobQueue.ImplementationQueue.Clear();
        mockLlm.StreamHandler = (ctx, msgs, opts) =>
        {
            return ToAsyncEnumerable("Implemented requested changes.");
        };
        await orchestrator.ProcessTaskAsync(task.Id);
        Assert.Contains(task.Id, jobQueue.CodeReviewQueue);

        // Attempt 3: Reviewer rejects again (retry 2 > MaxRetries 1) -> Task fails!
        mockLlm.StreamHandler = (ctx, msgs, opts) =>
        {
            return ToAsyncEnumerable("<request_changes>\nSecond review rejection.\n</request_changes>");
        };
        await orchestrator.ProcessTaskAsync(task.Id);

        var failedTask = await dbContext.Tasks.FindAsync(task.Id);
        Assert.NotNull(failedTask);
        Assert.Equal(TaskOverallStatus.Failed, failedTask.Status);
    }

    [Fact]
    public async Task MergeModeManualReviewRequired_CanBeManuallyMerged_TransitionsToClosed()
    {
        await using var env = CreateWorkflowEnvironment(targetSeed: 1200070);
        var (orchestrator, dbContext, connection, workspace, mockLlm, jobQueue) = env;

        var task = new ForgeTask
        {
            WorkspaceId = workspace.Id,
            Title = "Feature awaiting manual merge approval",
            Description = "Manual merge lifecycle test",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.CodeReviewAndFix,
            BaseBranch = "main",
            MergeMode = MergeMode.ManualReviewRequired
        };
        dbContext.Tasks.Add(task);
        await dbContext.SaveChangesAsync();

        mockLlm.StreamHandler = (ctx, msgs, opts) =>
        {
            return ToAsyncEnumerable("<approved>\nLooks good for manual merge.\n</approved>");
        };

        // Execute pipeline - advances to Ready
        await orchestrator.ProcessTaskAsync(task.Id);

        var readyTask = await dbContext.Tasks.FindAsync(task.Id);
        Assert.NotNull(readyTask);
        Assert.Equal(TaskOverallStatus.Ready, readyTask.Status);

        // Now human maintainer triggers manual merge via CompleteTaskAndCascadeAsync
        var mergeResult = await orchestrator.CompleteTaskAndCascadeAsync(task.Id, "Manual merge commit for approved task");

        Assert.Equal(CompleteTaskStatus.Completed, mergeResult.Status);

        var closedTask = await dbContext.Tasks.FindAsync(task.Id);
        Assert.NotNull(closedTask);
        Assert.Equal(TaskOverallStatus.Closed, closedTask.Status);
    }

    [Fact]
    public async Task Gate2ReverificationFailure_ExceedingMaxRetries_TransitionsTaskToFailed()
    {
        bool baseBranchSynced = false;
        CommandResult FailingVerificationExecHandler(string command, string? workingDir)
        {
            if (command.Contains("dotnet test") && baseBranchSynced)
            {
                baseBranchSynced = false;
                return new CommandResult(1, "Failed: 1, Passed: 9, Total: 10\nFailed MyTest [10 ms]", "", TimeSpan.FromMilliseconds(50));
            }
            return new CommandResult(0, "Passed: 10, Failed: 0, Total: 10", "", TimeSpan.FromMilliseconds(50));
        }

        var mockGit = new Mock<IGitWorkspaceManager>();
        mockGit.Setup(m => m.CheckMergeableAsync(It.IsAny<Workspace>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MergeabilityCheckResult(true, Array.Empty<string>(), "base-sha", "feat-sha"));
        mockGit.Setup(m => m.SyncFromBaseBranchAsync(It.IsAny<Workspace>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Workspace, int, string, CancellationToken>((w, t, b, ct) =>
            {
                baseBranchSynced = true;
                return Task.FromResult(new MergeSyncResult(true, false, Array.Empty<string>(), "merge-sync-sha"));
            });
        mockGit.Setup(m => m.GetBranchShaAsync(It.IsAny<Workspace>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("commit-sha");
        mockGit.Setup(m => m.GenerateDiffStatAsync(It.IsAny<Workspace>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(" 1 file changed, 5 insertions(+)");
        mockGit.Setup(m => m.GenerateLiveDiffStatAsync(It.IsAny<Workspace>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(" 1 file changed, 5 insertions(+)");
        mockGit.Setup(m => m.CreateWipCheckpointAsync(It.IsAny<Workspace>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("checkpoint-sha");

        await using var env = CreateWorkflowEnvironment(
            targetSeed: 1200071,
            execHandler: FailingVerificationExecHandler,
            gitManagerOverride: mockGit.Object);
        var (orchestrator, dbContext, connection, workspace, mockLlm, jobQueue) = env;

        var task = new ForgeTask
        {
            WorkspaceId = workspace.Id,
            Title = "Feature failing Gate 2 reverification repeatedly",
            Description = "Gate 2 retry exhaustion test",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.CodeReviewAndFix,
            BaseBranch = "main",
            MergeMode = MergeMode.Automatic
        };

        var bag = new PipelineDataBag();
        bag.Set(PipelineDataBagKeys.MaxRetries, 1);
        task.PipelineDataJson = bag.ToJson();

        dbContext.Tasks.Add(task);
        await dbContext.SaveChangesAsync();

        mockLlm.StreamHandler = (ctx, msgs, opts) =>
        {
            return ToAsyncEnumerable("<approved>\nApproved for Gate 2.\n</approved>");
        };

        // Attempt 1: Review approves -> Gate 2 reverify fails -> loops back cross-cohort to Implementation (retry 1/1)
        await orchestrator.ProcessTaskAsync(task.Id);

        var retryTask = await dbContext.Tasks.FindAsync(task.Id);
        Assert.NotNull(retryTask);
        Assert.Equal(StandardStageIds.Implementation, retryTask.CurrentStageId);
        Assert.Contains(task.Id, jobQueue.ImplementationQueue);

        // Attempt 2: Implementation completes and enqueues to CodeReview
        jobQueue.ImplementationQueue.Clear();
        mockLlm.StreamHandler = (ctx, msgs, opts) =>
        {
            return ToAsyncEnumerable("Attempted fix for reverification failure.");
        };
        await orchestrator.ProcessTaskAsync(task.Id);
        Assert.Contains(task.Id, jobQueue.CodeReviewQueue);

        // Attempt 3: Review approves again -> Gate 2 reverify fails second time (retry 2 > MaxRetries 1) -> Task fails!
        mockLlm.StreamHandler = (ctx, msgs, opts) =>
        {
            return ToAsyncEnumerable("<approved>\nApproved again for Gate 2.\n</approved>");
        };
        await orchestrator.ProcessTaskAsync(task.Id);

        var failedTask = await dbContext.Tasks.FindAsync(task.Id);
        Assert.NotNull(failedTask);
        Assert.Equal(TaskOverallStatus.Failed, failedTask.Status);
    }

    [Fact]
    public async Task UnmetDependencies_TaskRemainsPending_DoesNotExecute()
    {
        await using var env = CreateWorkflowEnvironment(targetSeed: 1200072);
        var (orchestrator, dbContext, connection, workspace, mockLlm, jobQueue) = env;

        var taskA = new ForgeTask
        {
            WorkspaceId = workspace.Id,
            Title = "Prerequisite Task A",
            Description = "Parent task",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.Implementation,
            BaseBranch = "main"
        };
        dbContext.Tasks.Add(taskA);
        await dbContext.SaveChangesAsync();

        var taskB = new ForgeTask
        {
            WorkspaceId = workspace.Id,
            Title = "Dependent Task B",
            Description = "Child task",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.Implementation,
            BaseBranch = "main"
        };
        dbContext.Tasks.Add(taskB);
        await dbContext.SaveChangesAsync();

        // Record dependency: Task B depends on Task A
        dbContext.TaskDependencies.Add(new TaskDependency { TaskId = taskB.Id, DependsOnTaskId = taskA.Id });
        await dbContext.SaveChangesAsync();

        bool llmCalled = false;
        mockLlm.StreamHandler = (ctx, msgs, opts) =>
        {
            llmCalled = true;
            return ToAsyncEnumerable("Should not run");
        };

        await orchestrator.ProcessTaskAsync(taskB.Id);

        Assert.False(llmCalled);
        var taskBAfter = await dbContext.Tasks.FindAsync(taskB.Id);
        Assert.NotNull(taskBAfter);
        Assert.Equal(TaskOverallStatus.Open, taskBAfter.Status);
        Assert.Equal(StandardStageIds.Implementation, taskBAfter.CurrentStageId);

        var logs = await dbContext.ExecutionLogs.Where(l => l.TaskId == taskB.Id).ToListAsync();
        Assert.Contains(logs, l => l.Message.Contains("waiting on prerequisite tasks before execution"));
    }

    [Fact]
    public async Task StageThrowsFatalException_TransitionsTaskToFailed()
    {
        await using var env = CreateWorkflowEnvironment(targetSeed: 1200073);
        var (orchestrator, dbContext, connection, workspace, mockLlm, jobQueue) = env;

        var task = new ForgeTask
        {
            WorkspaceId = workspace.Id,
            Title = "Task with fatal harness exception",
            Description = "Exception handling test",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.Implementation,
            BaseBranch = "main"
        };
        dbContext.Tasks.Add(task);
        await dbContext.SaveChangesAsync();

        mockLlm.StreamHandler = (ctx, msgs, opts) =>
        {
            throw new HttpRequestException("Inference host connection terminated unexpectedly.");
        };

        await orchestrator.ProcessTaskAsync(task.Id);

        var failedTask = await dbContext.Tasks.FindAsync(task.Id);
        Assert.NotNull(failedTask);
        Assert.Equal(TaskOverallStatus.Failed, failedTask.Status);
    }

    [Fact]
    public async Task PostImplementationBuildFailure_ExceedingMaxRetries_TransitionsTaskToFailed()
    {
        CommandResult BuildFailingExecutionHandler(string command, string? workingDir)
        {
            if (command.Contains("dotnet build"))
            {
                return new CommandResult(1, "Build FAILED: CS0103 The name 'nonExistentVar' does not exist in the current context", "", TimeSpan.FromMilliseconds(50));
            }
            return new CommandResult(0, "Passed: 10, Failed: 0, Total: 10", "", TimeSpan.FromMilliseconds(50));
        }

        await using var env = CreateWorkflowEnvironment(
            targetSeed: 1200074,
            execHandler: BuildFailingExecutionHandler);
        var (orchestrator, dbContext, connection, workspace, mockLlm, jobQueue) = env;

        var task = new ForgeTask
        {
            WorkspaceId = workspace.Id,
            Title = "Feature with persistent build error",
            Description = "Build failure retry exhaustion test",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.Implementation,
            BaseBranch = "main"
        };

        var bag = new PipelineDataBag();
        bag.Set(PipelineDataBagKeys.MaxRetries, 1);
        task.PipelineDataJson = bag.ToJson();

        dbContext.Tasks.Add(task);
        await dbContext.SaveChangesAsync();

        mockLlm.StreamHandler = (ctx, msgs, opts) =>
        {
            return ToAsyncEnumerable("Implementation agent attempting code change.");
        };

        await orchestrator.ProcessTaskAsync(task.Id);

        var failedTask = await dbContext.Tasks.FindAsync(task.Id);
        Assert.NotNull(failedTask);
        Assert.Equal(TaskOverallStatus.Failed, failedTask.Status);
    }

    [Fact]
    public async Task TaskReviewStage_WhenSpecificationUnchanged_FailsTaskFast()
    {
        await using var env = CreateWorkflowEnvironment(targetSeed: 1200075);
        var (orchestrator, dbContext, connection, workspace, mockLlm, jobQueue) = env;

        var task = new ForgeTask
        {
            WorkspaceId = workspace.Id,
            Title = "Task with untouched spec in review",
            Description = "Initial description",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.TaskReview,
            BaseBranch = "main",
            Version = 1
        };
        dbContext.Tasks.Add(task);
        await dbContext.SaveChangesAsync();

        mockLlm.StreamHandler = (ctx, msgs, opts) =>
        {
            return ToAsyncEnumerable("Reviewed task specification, looks complete as-is.");
        };

        await Assert.ThrowsAsync<WorkflowExecutionException>(() => orchestrator.ProcessTaskAsync(task.Id));

        var failedTask = await dbContext.Tasks.FindAsync(task.Id);
        Assert.NotNull(failedTask);
        Assert.Equal(TaskOverallStatus.Failed, failedTask.Status);
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> ToAsyncEnumerable(string text)
    {
        yield return new ChatResponseUpdate { Contents = [new TextContent(text)] };
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> ToAsyncEnumerable(ChatResponseUpdate update)
    {
        yield return update;
        await Task.CompletedTask;
    }
}
