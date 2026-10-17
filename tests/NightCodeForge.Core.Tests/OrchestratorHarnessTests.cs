using NightCodeForge.Core.Concurrency;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Tests.Fixtures;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Core.Workflow.Stages;

namespace NightCodeForge.Core.Tests;

public class OrchestratorHarnessTests
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

    private class FakeMirrorSyncService : IGitMirrorSyncService
    {
        public int PushCount { get; private set; }
        public int PushBaseBranchCount { get; private set; }

        public Task ConfigureHostRepositoryAsync(string hostRepoPath, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string> InitializeMirrorAsync(Workspace workspace, string mirrorDirectory, CancellationToken cancellationToken = default) => Task.FromResult(mirrorDirectory);
        public Task PullFromHostAsync(string mirrorRepoPath, string baseBranch, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<GitSyncResult> PushToHostAsync(string mirrorRepoPath, int taskId, CancellationToken cancellationToken = default)
        {
            PushCount++;
            return Task.FromResult(new GitSyncResult(true, false, $"feature/task-{taskId}", null));
        }

        public Task<GitSyncResult> PushBaseBranchToHostAsync(string mirrorRepoPath, string baseBranch, CancellationToken cancellationToken = default)
        {
            PushBaseBranchCount++;
            return Task.FromResult(new GitSyncResult(true, false, baseBranch, null));
        }
    }

    private class FakeMarkdownSyncService : ITaskMarkdownSyncService
    {
        public int SyncCount { get; private set; }
        public int ArchiveCount { get; private set; }

        public string GenerateMarkdown(ForgeTask task, IReadOnlyList<int>? dependencies = null, string? agentProfileId = null) => string.Empty;
        public (TaskFrontmatter Frontmatter, string Body) ParseMarkdown(string markdownContent) => (new TaskFrontmatter(), string.Empty);
        public string ComputeContentHash(string content) => string.Empty;

        public Task<string> SyncTaskToDiskAsync(ForgeTask task, string workspacePath, IReadOnlyList<int>? dependencies = null, string? agentProfileId = null, CancellationToken cancellationToken = default)
        {
            SyncCount++;
            return Task.FromResult(Path.Combine(workspacePath, $"task-{task.Id}.md"));
        }

        public Task ProcessFileContentAsync(string filePath, string content, int workspaceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ProcessFileChangedAsync(string filePath, int workspaceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ArchiveOrCleanupTaskFileAsync(ForgeTask task, string workspacePath, bool archive = true, CancellationToken cancellationToken = default)
        {
            ArchiveCount++;
            return Task.CompletedTask;
        }
        public void StartWatching(int workspaceId, string workspacePath) { }
        public void StopWatching(int workspaceId) { }
        public void Dispose() { }
    }

    private class FakePathResolver : IWorkspacePathResolver
    {
        public string GetAgentWorkingClonePath(int workspaceId) => $"/home/user/.nightcodeforge/wks/{workspaceId}";
        public string GetHostRemoteUrl(string repositoryPath) => $"/mnt/c/{repositoryPath}";
        public string ResolveCruciblePath(int workspaceId, string relativeOrContainerPath) => $@"\\wsl.localhost\Ubuntu\home\user\.nightcodeforge\wks\{workspaceId}\{relativeOrContainerPath}";
        public string GetWslDistributionName() => "Ubuntu";
        public string GetWslUserName() => "user";
    }

    [Fact]
    public async Task ProcessTaskAsync_DelegatesToHarness_ForAgenticStage()
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
            Id = 80,
            WorkspaceId = 1,
            Status = TaskOverallStatus.Open,
            CurrentStageId = "task-review",
            BaseBranch = "main"
        });
        depStore.AddTask(80, 1, TaskOverallStatus.Open);

        var fakeHarness = new FakeAgentHarness();
        var harnessRegistry = new FakeHarnessRegistry(fakeHarness);
        var mirrorSync = new FakeMirrorSyncService();
        var mdSync = new FakeMarkdownSyncService();

        // Default review stage without custom handler
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
            HarnessRegistry = harnessRegistry,
            GitMirrorSyncService = mirrorSync,
            MarkdownSyncService = mdSync
        }.Build();

        await orchestrator.ProcessTaskAsync(task.Id);

        // Harness should have been invoked
        Assert.Single(fakeHarness.ExecutedSessions);
        var session = fakeHarness.ExecutedSessions[0];
        Assert.Equal(80, session.ForgeTask.Id);
        Assert.Equal("task-review", session.StageId);
        Assert.Equal(stage1.StageDirective, session.StageDirective);
        Assert.Contains(stage1.StageDirective, session.BootstrapPrompt);

        // Mirror sync and MD sync should have been triggered
        Assert.True(mirrorSync.PushCount > 0);
        Assert.True(mdSync.SyncCount > 0);

        // Token tracking should be recorded
        var records = taskRepo.GetAllExecutionRecords();
        Assert.Single(records);
        Assert.Equal(100, records[0].PromptTokens);
        Assert.Equal(50, records[0].CompletionTokens);
        Assert.Equal(150, records[0].TotalTokens);
    }

    [Fact]
    public async Task ProcessTaskAsync_UsesProfileStageDirectiveOverride_WhenConfigured()
    {
        var taskRepo = new InMemoryTaskRepository();
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var depStore = new InMemoryTaskDependencyStore();
        var depEngine = new DependencyResolutionEngine(depStore);
        var lockProvider = new WorkspaceLockProvider();
        var gitManager = new FakeGitWorkspaceManager();
        var jobQueue = new FakeTaskJobQueue();

        const string customDirective = "Custom profile directive: Always generate unit tests with FluentAssertions.";

        var workspace = new Workspace
        {
            Id = 2,
            Name = "Profile Directive Test Workspace",
            RepositoryPath = "C:/test/repo2",
            DefaultBaseBranch = "main",
            AgentProfiles = new List<AgentProfile>
            {
                new()
                {
                    Id = "custom-profile",
                    HarnessId = "crucible",
                    ModelName = "qwen2.5-coder:7b",
                    StageDirectives = new Dictionary<string, string>
                    {
                        ["implementation"] = customDirective
                    }
                }
            }
        };
        await workspaceRepo.CreateAsync(workspace);

        var task = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 90,
            WorkspaceId = 2,
            Status = TaskOverallStatus.Open,
            CurrentStageId = "implementation",
            BaseBranch = "main"
        });
        depStore.AddTask(90, 2, TaskOverallStatus.Open);

        var fakeHarness = new FakeAgentHarness();
        var harnessRegistry = new FakeHarnessRegistry(fakeHarness);

        var stage = TestWorkflowPipelines.CreateImplementationStage();
        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stage });

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
        Assert.Equal("implementation", session.StageId);
        // Tier 2 override should take precedence over the canonical Tier 1 stage default
        Assert.Equal(customDirective, session.StageDirective);
        Assert.Contains(customDirective, session.BootstrapPrompt);
    }

    [Fact]
    public async Task ProcessTaskAsync_WhenAgentModeMismatch_FailsStageWithValidationMessage()
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
            Id = 3,
            Name = "Mode Test Workspace",
            RepositoryPath = "C:/test/repo",
            DefaultBaseBranch = "main",
            AgentProfiles = new List<AgentProfile>
            {
                new()
                {
                    Id = "implementation",
                    Name = "Audit Agent",
                    Mode = AgentMode.ReadOnly,
                    HarnessId = "crucible"
                }
            }
        };
        await workspaceRepo.CreateAsync(workspace);

        var task = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 80,
            WorkspaceId = 3,
            Status = TaskOverallStatus.Open,
            CurrentStageId = "implementation",
            BaseBranch = "main"
        });
        depStore.AddTask(80, 3, TaskOverallStatus.Open);

        var fakeHarness = new FakeAgentHarness();
        var harnessRegistry = new FakeHarnessRegistry(fakeHarness);

        // Implementation stage requires AgentMode.Full by default
        var stage = TestWorkflowPipelines.CreateImplementationStage();
        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stage });

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

        // The harness must NOT have been executed
        Assert.Empty(fakeHarness.ExecutedSessions);

        // ForgeTask must have failed
        var updated = await taskRepo.GetByIdAsync(80);
        Assert.NotNull(updated);
        Assert.Equal(TaskOverallStatus.Failed, updated.Status);

        var logs = taskRepo.GetAllLogs();
        Assert.Contains(logs, l => l.Message.Contains("requires AgentMode.Full capabilities"));
    }

    [Fact]
    public async Task ProcessTaskAsync_EnforcesZeroMemoryAcrossStageBoundaries()
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
            Id = 10,
            Name = "Zero Memory Test",
            RepositoryPath = "C:/test/repo",
            DefaultBaseBranch = "main",
            AgentProfiles = new List<AgentProfile>
            {
                new()
                {
                    Id = "implementation",
                    Name = "Implementation Agent",
                    HarnessId = "crucible",
                    ModelName = "test-model",
                    Mode = AgentMode.Full
                }
            }
        };
        await workspaceRepo.CreateAsync(workspace);

        var task = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 100,
            WorkspaceId = 10,
            Status = TaskOverallStatus.Reviewed,
            CurrentStageId = "implementation",
            BaseBranch = "main"
        });
        depStore.AddTask(100, 10, TaskOverallStatus.Reviewed);

        // Previous stage (task-review) created a checkpoint with serialized memory
        await taskRepo.AddCheckpointAsync(new StageCheckpoint
        {
            TaskId = 100,
            StageId = "task-review",
            SerializedMemory = "{\"messages\": [\"Stage 1 chat history that must NOT leak to Stage 3\"]}",
            CreatedAt = DateTime.UtcNow.AddMinutes(-5)
        });

        var fakeHarness = new FakeAgentHarness();
        var harnessRegistry = new FakeHarnessRegistry(fakeHarness);

        var stage = TestWorkflowPipelines.CreateImplementationStage();
        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stage });

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
        Assert.Equal("implementation", session.StageId);
        // Stage boundary context reset: chat history from stage 1 must be null
        Assert.Null(session.SerializedStateJson);
    }

    [Fact]
    public async Task ProcessTaskAsync_RestoresMemory_WhenResumingSameStage()
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
            Id = 11,
            Name = "Resumption Test",
            RepositoryPath = "C:/test/repo",
            DefaultBaseBranch = "main",
            AgentProfiles = new List<AgentProfile>
            {
                new()
                {
                    Id = "implementation",
                    Name = "Implementation Agent",
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
            WorkspaceId = 11,
            Status = TaskOverallStatus.Reviewed,
            CurrentStageId = "implementation",
            BaseBranch = "main"
        });
        depStore.AddTask(101, 11, TaskOverallStatus.Reviewed);

        // Checkpoint from the SAME stage (e.g. human interruption or retry)
        const string expectedMemory = "{\"messages\": [\"Implementation partial progress\"]}";
        await taskRepo.AddCheckpointAsync(new StageCheckpoint
        {
            TaskId = 101,
            StageId = "implementation",
            SerializedMemory = expectedMemory,
            CreatedAt = DateTime.UtcNow.AddMinutes(-1)
        });

        var fakeHarness = new FakeAgentHarness();
        var harnessRegistry = new FakeHarnessRegistry(fakeHarness);

        var stage = TestWorkflowPipelines.CreateImplementationStage();
        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stage });

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
        Assert.Equal("implementation", session.StageId);
        // Resuming the same stage must restore the checkpoint memory
        Assert.Equal(expectedMemory, session.SerializedStateJson);
    }

    [Fact]
    public async Task ProcessTaskAsync_CreatesWipCheckpointOnStageSuccess()
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
            Id = 12,
            Name = "Wip Commit Test",
            RepositoryPath = "C:/test/repo",
            DefaultBaseBranch = "main",
            AgentProfiles = new List<AgentProfile>
            {
                new()
                {
                    Id = "implementation",
                    Name = "Implementation Agent",
                    HarnessId = "crucible",
                    ModelName = "test-model",
                    Mode = AgentMode.Full
                }
            }
        };
        await workspaceRepo.CreateAsync(workspace);

        var task = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 102,
            WorkspaceId = 12,
            Status = TaskOverallStatus.Open,
            CurrentStageId = "implementation",
            BaseBranch = "main"
        });
        depStore.AddTask(102, 12, TaskOverallStatus.Open);

        var fakeHarness = new FakeAgentHarness();
        var harnessRegistry = new FakeHarnessRegistry(fakeHarness);

        var stage = TestWorkflowPipelines.CreateImplementationStage();
        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stage });

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

        // Verify gitManager was invoked to create a WIP checkpoint for the completed stage
        Assert.Contains(gitManager.WipCheckpoints, cp => cp.TaskId == 102 && cp.StageName == "implementation");

        // Verify checkpoint saved in repository has the git commit SHA
        var updatedTask = await taskRepo.GetByIdAsync(102);
        Assert.NotNull(updatedTask);
        var latestCheckpoint = updatedTask.Checkpoints.LastOrDefault();
        Assert.NotNull(latestCheckpoint);
        Assert.StartsWith("commit-sha-", latestCheckpoint.GitCommitHash);
        // Serialized memory is wiped clean at stage completion
        Assert.Equal(string.Empty, latestCheckpoint.SerializedMemory);
    }

    [Fact]
    public async Task ProcessTaskAsync_AutoInitializesMirror_WhenExt4MirrorPathIsEmpty()
    {
        var taskRepo = new InMemoryTaskRepository();
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var depStore = new InMemoryTaskDependencyStore();
        var depEngine = new DependencyResolutionEngine(depStore);
        var lockProvider = new WorkspaceLockProvider();
        var gitManager = new FakeGitWorkspaceManager();
        var jobQueue = new FakeTaskJobQueue();
        var mirrorService = new FakeMirrorSyncService();
        var pathResolver = new FakePathResolver();

        var workspace = new Workspace
        {
            Id = 13,
            Name = "Mirror Init Test",
            RepositoryPath = "C:/test/repo",
            Ext4MirrorPath = null, // Not yet initialized
            DefaultBaseBranch = "main",
            AgentProfiles = new List<AgentProfile>
            {
                new()
                {
                    Id = "implementation",
                    Name = "Implementation Agent",
                    HarnessId = "crucible",
                    ModelName = "test-model",
                    Mode = AgentMode.Full
                }
            }
        };
        await workspaceRepo.CreateAsync(workspace);

        var task = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 103,
            WorkspaceId = 13,
            Status = TaskOverallStatus.Open,
            CurrentStageId = "implementation",
            BaseBranch = "main"
        });
        depStore.AddTask(103, 13, TaskOverallStatus.Open);

        var fakeHarness = new FakeAgentHarness();
        var harnessRegistry = new FakeHarnessRegistry(fakeHarness);

        var stage = TestWorkflowPipelines.CreateImplementationStage();
        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stage });

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
            GitMirrorSyncService = mirrorService,
            PathResolver = pathResolver
        }.Build();

        await orchestrator.ProcessTaskAsync(task.Id);

        // Verify workspace was updated with initialized mirror path
        var updatedWorkspace = await workspaceRepo.GetByIdAsync(13);
        Assert.NotNull(updatedWorkspace);
        Assert.Equal("/home/user/.nightcodeforge/wks/13", updatedWorkspace.Ext4MirrorPath);
    }

    [Fact]
    public async Task CompleteTaskAndCascadeAsync_CallsArchiveOrCleanupTaskFileAsync()
    {
        var taskRepo = new InMemoryTaskRepository();
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var depStore = new InMemoryTaskDependencyStore();
        var depEngine = new DependencyResolutionEngine(depStore);
        var lockProvider = new WorkspaceLockProvider();
        var gitManager = new FakeGitWorkspaceManager();
        var jobQueue = new FakeTaskJobQueue();
        var markdownSync = new FakeMarkdownSyncService();

        var workspace = new Workspace
        {
            Id = 14,
            Name = "Archive Test",
            RepositoryPath = "C:/test/repo",
            DefaultBaseBranch = "main"
        };
        await workspaceRepo.CreateAsync(workspace);

        var task = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 104,
            WorkspaceId = 14,
            Status = TaskOverallStatus.Ready,
            CurrentStageId = "deterministic-post-review",
            BaseBranch = "main"
        });
        depStore.AddTask(104, 14, TaskOverallStatus.Ready);

        var pipeline = new WorkflowPipeline(Array.Empty<IWorkflowStage>());

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
            MarkdownSyncService = markdownSync
        }.Build();

        await orchestrator.CompleteTaskAndCascadeAsync(104, "Final squash commit");

        var updated = await taskRepo.GetByIdAsync(104);
        Assert.NotNull(updated);
        Assert.Equal(TaskOverallStatus.Closed, updated.Status);

        // Verify markdown sync service archived the task file
        Assert.Equal(1, markdownSync.ArchiveCount);
    }

    [Fact]
    public async Task CompleteTaskAndCascadeAsync_WithExt4Mirror_CallsPushBaseBranchToHostAsync()
    {
        var taskRepo = new InMemoryTaskRepository();
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var depStore = new InMemoryTaskDependencyStore();
        var depEngine = new DependencyResolutionEngine(depStore);
        var lockProvider = new WorkspaceLockProvider();
        var gitManager = new FakeGitWorkspaceManager();
        var jobQueue = new FakeTaskJobQueue();
        var mirrorService = new FakeMirrorSyncService();

        var workspace = new Workspace
        {
            Id = 15,
            Name = "Mirror Complete Test",
            RepositoryPath = "C:/test/repo",
            Ext4MirrorPath = "/home/user/.nightcodeforge/wks/15",
            DefaultBaseBranch = "main"
        };
        await workspaceRepo.CreateAsync(workspace);

        var task = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 105,
            WorkspaceId = 15,
            Status = TaskOverallStatus.Ready,
            CurrentStageId = "deterministic-post-review",
            BaseBranch = "main"
        });
        depStore.AddTask(105, 15, TaskOverallStatus.Ready);

        var pipeline = new WorkflowPipeline(Array.Empty<IWorkflowStage>());

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
            GitMirrorSyncService = mirrorService
        }.Build();

        await orchestrator.CompleteTaskAndCascadeAsync(105, "Squash and merge to main");

        var updated = await taskRepo.GetByIdAsync(105);
        Assert.NotNull(updated);
        Assert.Equal(TaskOverallStatus.Closed, updated.Status);

        // Verify base branch was pushed to host-origin
        Assert.Equal(1, mirrorService.PushBaseBranchCount);
    }

    [Fact]
    public void ProcessTaskAsync_HasAutomaticRetryAttribute_ConfiguredToZeroAttempts()
    {
        var method = typeof(TaskOrchestrator).GetMethod(nameof(TaskOrchestrator.ProcessTaskAsync));
        Assert.NotNull(method);

        var attr = method.GetCustomAttributes(typeof(Hangfire.AutomaticRetryAttribute), false)
            .FirstOrDefault() as Hangfire.AutomaticRetryAttribute;

        Assert.NotNull(attr);
        Assert.Equal(0, attr.Attempts);
        Assert.Equal(Hangfire.AttemptsExceededAction.Fail, attr.OnAttemptsExceeded);
    }

    [Fact]
    public async Task ProcessTaskAsync_WhenNoAgentProfileConfigured_ThrowsAgentProfileNotFoundExceptionAndFailsFast()
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
            Id = 20,
            Name = "Missing Profile Workspace",
            RepositoryPath = "C:/test/repo",
            DefaultBaseBranch = "main",
            AgentProfiles = new List<AgentProfile>()
        };
        await workspaceRepo.CreateAsync(workspace);

        var task = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 200,
            WorkspaceId = 20,
            Status = TaskOverallStatus.Open,
            CurrentStageId = "implementation",
            BaseBranch = "main"
        });
        depStore.AddTask(200, 20, TaskOverallStatus.Open);

        var fakeHarness = new FakeAgentHarness();
        var harnessRegistry = new FakeHarnessRegistry(fakeHarness);

        var stage = TestWorkflowPipelines.CreateImplementationStage();
        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stage });

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

        var ex = await Assert.ThrowsAsync<AgentProfileNotFoundException>(() => orchestrator.ProcessTaskAsync(task.Id));
        Assert.Equal("implementation", ex.ProfileId);
        Assert.Equal(20, ex.WorkspaceId);

        var updatedTask = await taskRepo.GetByIdAsync(200);
        Assert.NotNull(updatedTask);
        Assert.Equal(TaskOverallStatus.Failed, updatedTask.Status);
    }

    [Fact]
    public async Task ProcessTaskAsync_WhenStageAgentProfileMismatch_DoesNotFallbackToArbitraryProfile_ThrowsAgentProfileNotFoundException()
    {
        var taskRepo = new InMemoryTaskRepository();
        var workspaceRepo = new InMemoryWorkspaceRepository();
        var depStore = new InMemoryTaskDependencyStore();
        var depEngine = new DependencyResolutionEngine(depStore);
        var lockProvider = new WorkspaceLockProvider();
        var gitManager = new FakeGitWorkspaceManager();
        var jobQueue = new FakeTaskJobQueue();

        // Workspace has an agent profile, but its ID does NOT match the stage's required profile ("implementation")
        var workspace = new Workspace
        {
            Id = 21,
            Name = "Unmatched Profile Workspace",
            RepositoryPath = "C:/test/repo",
            DefaultBaseBranch = "main",
            AgentProfiles = new List<AgentProfile>
            {
                new AgentProfile
                {
                    Id = "unrelated_profile",
                    Name = "Unrelated Agent",
                    HarnessId = StandardHarnessIds.Crucible,
                    ModelName = "test-model"
                }
            }
        };
        await workspaceRepo.CreateAsync(workspace);

        var task = await taskRepo.CreateAsync(new ForgeTask
        {
            Id = 201,
            WorkspaceId = 21,
            Status = TaskOverallStatus.Open,
            CurrentStageId = "implementation",
            BaseBranch = "main"
        });
        depStore.AddTask(201, 21, TaskOverallStatus.Open);

        var fakeHarness = new FakeAgentHarness();
        var harnessRegistry = new FakeHarnessRegistry(fakeHarness);

        var stage = TestWorkflowPipelines.CreateImplementationStage();
        var pipeline = new WorkflowPipeline(new IWorkflowStage[] { stage });

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

        var ex = await Assert.ThrowsAsync<AgentProfileNotFoundException>(() => orchestrator.ProcessTaskAsync(task.Id));
        Assert.Equal("implementation", ex.ProfileId);
        Assert.Equal(21, ex.WorkspaceId);

        var updatedTask = await taskRepo.GetByIdAsync(201);
        Assert.NotNull(updatedTask);
        Assert.Equal(TaskOverallStatus.Failed, updatedTask.Status);
    }
}
