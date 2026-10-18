using Microsoft.Extensions.DependencyInjection;
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

public class TaskReviewDiskReadBackTests : IDisposable
{
    private readonly string _tempWorkspaceDir;
    private readonly string _tempWorktreeDir;

    public TaskReviewDiskReadBackTests()
    {
        _tempWorkspaceDir = Path.Combine(Path.GetTempPath(), "NightCodeForge_Wks_" + Guid.NewGuid().ToString("N"));
        _tempWorktreeDir = Path.Combine(Path.GetTempPath(), "NightCodeForge_Wkt_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempWorkspaceDir);
        Directory.CreateDirectory(_tempWorktreeDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempWorkspaceDir)) Directory.Delete(_tempWorkspaceDir, true);
            if (Directory.Exists(_tempWorktreeDir)) Directory.Delete(_tempWorktreeDir, true);
        }
        catch { }
    }

    private class TestHarness : IAgentHarness
    {
        public string HarnessId => StandardHarnessIds.Crucible;
        public string? CapturedBootstrapPrompt { get; private set; }
        public string? CapturedStageDirective { get; private set; }

        public async Task<AgentRunResult> RunStageAsync(AgentSessionContext context, CancellationToken cancellationToken = default)
        {
            CapturedBootstrapPrompt = context.BootstrapPrompt;
            CapturedStageDirective = context.StageDirective;

            var taskMarkdownRelPath = context.PipelineContext?.DataBag.Get<string>(PipelineDataBagKeys.TaskMarkdownPath);
            if (!string.IsNullOrWhiteSpace(taskMarkdownRelPath) && !string.IsNullOrWhiteSpace(context.PipelineContext?.WorkingDirectory))
            {
                var fullPath = Path.Combine(context.PipelineContext.WorkingDirectory, taskMarkdownRelPath);
                if (File.Exists(fullPath))
                {
                    var existingContent = await File.ReadAllTextAsync(fullPath, cancellationToken);
                    var enrichedContent = existingContent.TrimEnd() + @"

## 🏛️ Architecture & Implementation Plan
1. Implement `RedisDistributedCache` implementing `IDistributedCache`.
2. Add fallback in-memory cache on Redis connection failure.

## 🧪 Acceptance Criteria
- [ ] Redis timeout triggers circuit breaker.
- [ ] Cache miss loads from database.
";
                    await File.WriteAllTextAsync(fullPath, enrichedContent, cancellationToken);
                }
            }

            return AgentRunResult.Completed(
                new AgentRunMetrics(100, 200, 100, 300),
                summary: "ForgeTask review complete; updated specification on disk.");
        }
    }

    private class TestHarnessRegistry : IHarnessRegistry
    {
        private readonly IAgentHarness _harness;
        public TestHarnessRegistry(IAgentHarness harness) => _harness = harness;
        public IAgentHarness GetHarness(string harnessId) => _harness;
        public void RegisterHarness(IAgentHarness harness) { }
        public bool TryGetHarness(string harnessId, out IAgentHarness? harness)
        {
            harness = _harness;
            return true;
        }
        public IEnumerable<IAgentHarness> GetAllHarnesses() => new[] { _harness };
    }

    private class TestGitWorkspaceManager : FakeGitWorkspaceManager, IGitWorkspaceManager
    {
        private readonly string _worktreePath;
        public TestGitWorkspaceManager(string worktreePath) => _worktreePath = worktreePath;

        string IGitWorkspaceManager.GetWorktreePath(Workspace workspace, int taskId) => _worktreePath;
    }

    [Fact]
    public async Task ProcessTaskAsync_TaskReviewAgent_DirectsAgentToEditDisk_AndReadsBackDiskChangesIntoRepositoryAndPlan()
    {
        // Arrange
        var task = new ForgeTask
        {
            Id = 42,
            WorkspaceId = 1,
            Title = "Implement Distributed Cache",
            Description = "Basic cache requirements for session state.",
            Status = TaskOverallStatus.Open,
            CurrentStageId = "task-review",
            CreatedAt = new DateTime(2026, 10, 14, 0, 0, 0, DateTimeKind.Utc)
        };

        var workspace = new Workspace
        {
            Id = 1,
            Name = "CacheWorkspace",
            RepositoryPath = _tempWorkspaceDir,
            ExecutionMode = ExecutionMode.BareHost
        };

        var taskRepo = new InMemoryTaskRepository();
        await taskRepo.CreateAsync(task);

        var workspaceRepo = new InMemoryWorkspaceRepository();
        workspaceRepo.SeedGlobalProfiles(AgentProfile.CreateStandardGlobalProfiles("test-model"));
        await workspaceRepo.CreateAsync(workspace);

        var depStore = new InMemoryTaskDependencyStore();
        depStore.AddTask(42, 1, TaskOverallStatus.Open);
        var depEngine = new DependencyResolutionEngine(depStore);
        var lockProvider = new WorkspaceLockProvider();
        var jobQueue = new FakeTaskJobQueue();
        var gitManager = new TestGitWorkspaceManager(_tempWorktreeDir);
        var syncServices = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        syncServices.AddSingleton<ITaskRepository>(taskRepo);
        syncServices.AddSingleton<IDependencyResolutionEngine>(depEngine);
        var syncSp = syncServices.BuildServiceProvider();
        var syncService = new TaskMarkdownSyncService(syncSp.GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>());

        var harness = new TestHarness();
        var harnessRegistry = new TestHarnessRegistry(harness);

        var pipeline = new WorkflowPipeline(new IWorkflowStage[]
        {
            TestWorkflowPipelines.CreateReviewStage(),
            TestWorkflowPipelines.CreateImplementationStage()
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
            HarnessRegistry = harnessRegistry,
            MarkdownSyncService = syncService
        }.Build();

        // Act: Process task-review stage
        await orchestrator.ProcessTaskAsync(task.Id);

        // Assert 1: Agent directive and bootstrap prompt explicitly instruct editing disk
        Assert.NotNull(harness.CapturedStageDirective);
        Assert.Contains("MANDATORY FILE EDIT", harness.CapturedStageDirective);
        Assert.Contains("You must edit the task specification document on disk", harness.CapturedStageDirective);
        Assert.Contains("reads back the file from disk", harness.CapturedStageDirective);

        Assert.NotNull(harness.CapturedBootstrapPrompt);
        Assert.Contains("[ForgeTask Specification Document]:", harness.CapturedBootstrapPrompt);
        Assert.Contains("CRITICAL: You are required to edit this file directly on disk using 'edit' or 'write'", harness.CapturedBootstrapPrompt);
        Assert.Contains("Your disk changes will be read back automatically upon stage completion", harness.CapturedBootstrapPrompt);

        // Assert 2: The orchestrator read back the disk modifications into the task entity in database
        var updatedInDb = await taskRepo.GetByIdAsync(42);
        Assert.NotNull(updatedInDb);
        Assert.Equal(TaskOverallStatus.Reviewed, updatedInDb.Status);
        Assert.Equal(StandardStageIds.Implementation, updatedInDb.CurrentStageId);
        Assert.Contains("RedisDistributedCache", updatedInDb.Description);
        Assert.Contains("## 🏛️ Architecture & Implementation Plan", updatedInDb.Description);
        Assert.Contains("## 🧪 Acceptance Criteria", updatedInDb.Description);

        // Assert 3: Stage1.ApprovedPlan in DataBag was populated with the enriched specification from disk
        var bag = PipelineDataBag.FromJson(updatedInDb.PipelineDataJson);
        var approvedPlan = bag.Get<string>(PipelineDataBagKeys.ApprovedPlan);
        Assert.NotNull(approvedPlan);
        Assert.Contains("RedisDistributedCache", approvedPlan);
        Assert.Contains("## 🏛️ Architecture & Implementation Plan", approvedPlan);

        // Assert 4: Git checkpoint commit was created capturing the completed stage
        Assert.Contains(gitManager.WipCheckpoints, c => c.StageName == StandardStageIds.TaskReview);

        // Assert 5: The changes were mirrored to host repository path
        var mirroredFiles = Directory.GetFiles(_tempWorkspaceDir, "*.md", SearchOption.AllDirectories);
        Assert.NotEmpty(mirroredFiles);
        var mirroredContent = await File.ReadAllTextAsync(mirroredFiles[0]);
        Assert.Contains("RedisDistributedCache", mirroredContent);
    }
}
