using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Workflow;
using NightCodeForge.Core.Execution;
using NightCodeForge.Core.Tests.Fixtures;
using Xunit;

namespace NightCodeForge.Core.Tests;

public class ConflictResolutionLifecycleTests
{
    private readonly Workspace _workspace;

    public ConflictResolutionLifecycleTests()
    {
        _workspace = new Workspace
        {
            Id = 1,
            Name = "Test Workspace",
            RepositoryPath = "/test/repo",
            DefaultBaseBranch = "main"
        };
    }

    private static IWorkspacePathResolver CreateMockPathResolver()
    {
        var mock = new Mock<IWorkspacePathResolver>();
        mock.Setup(p => p.ToHostPath(It.IsAny<string>())).Returns<string>(path => path);
        return mock.Object;
    }

    private static IExecutionEnvironmentProvider CreateMockEnvironmentProvider()
    {
        var env = new Mock<IExecutionEnvironment>();
        env.Setup(e => e.ExecuteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommandResult(0, "Build succeeded", "", TimeSpan.Zero));
        var envProvider = new Mock<IExecutionEnvironmentProvider>();
        envProvider.Setup(p => p.GetEnvironment(It.IsAny<Workspace>(), It.IsAny<SandboxSecurityTier>(), It.IsAny<int?>()))
            .Returns(env.Object);
        return envProvider.Object;
    }

    [Fact]
    public async Task Verifier_DetectsLeftoverConflictMarkers()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var conflictedFile = Path.Combine(tempDir, "file.cs");
            await File.WriteAllTextAsync(conflictedFile, @"
public class Test
{
<<<<<<< HEAD
    public int X = 1;
||||||| common ancestor
    public int X = 0;
=======
    public int X = 2;
>>>>>>> main
}
");

            var gitRunner = new Mock<IGitCliRunner>();
            var gitWorkspaceManager = new Mock<IGitWorkspaceManager>();
            gitWorkspaceManager.Setup(m => m.GetWorktreePath(It.IsAny<Workspace>(), It.IsAny<int>())).Returns(tempDir);

            var verifier = new ConflictResolutionVerifier(gitRunner.Object, gitWorkspaceManager.Object, CreateMockEnvironmentProvider(), CreateMockPathResolver(), NullLogger<ConflictResolutionVerifier>.Instance);

            var customWorkspace = new Workspace
            {
                Id = 1,
                RepositoryPath = tempDir,
                DefaultBaseBranch = "main"
            };

            // Act
            var result = await verifier.VerifyResolutionAsync(customWorkspace, 10, "main", new[] { "file.cs" });

            // Assert
            Assert.False(result.IsSuccess);
            Assert.Contains("conflict markers", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task Verifier_BareMarkdownEquals_DoesNotTriggerFalsePositive()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var markdownFile = Path.Combine(tempDir, "README.md");
            await File.WriteAllTextAsync(markdownFile, @"
# Header Title
==============
Some resolved documentation.
");

            var gitRunner = new Mock<IGitCliRunner>();
            gitRunner.Setup(r => r.ExecuteAsync(It.IsAny<string>(), It.Is<IEnumerable<string>>(args => args.Contains("add")), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GitCommandResult(0, "", "", TimeSpan.Zero));
            gitRunner.Setup(r => r.ExecuteAsync(It.IsAny<string>(), It.Is<IEnumerable<string>>(args => args.Contains("ls-files")), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GitCommandResult(0, "", "", TimeSpan.Zero));
            gitRunner.Setup(r => r.ExecuteAsync(It.IsAny<string>(), It.Is<IEnumerable<string>>(args => args.Contains("commit")), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GitCommandResult(0, "", "", TimeSpan.Zero));
            gitRunner.Setup(r => r.ExecuteAsync(It.IsAny<string>(), It.Is<IEnumerable<string>>(args => args.Contains("rev-parse")), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GitCommandResult(0, "mock-sha", "", TimeSpan.Zero));

            var gitWorkspaceManager = new Mock<IGitWorkspaceManager>();
            gitWorkspaceManager.Setup(m => m.GetWorktreePath(It.IsAny<Workspace>(), It.IsAny<int>())).Returns(tempDir);

            var verifier = new ConflictResolutionVerifier(gitRunner.Object, gitWorkspaceManager.Object, CreateMockEnvironmentProvider(), CreateMockPathResolver(), NullLogger<ConflictResolutionVerifier>.Instance);

            var customWorkspace = new Workspace
            {
                Id = 1,
                RepositoryPath = tempDir,
                DefaultBaseBranch = "main"
            };

            // Act
            var result = await verifier.VerifyResolutionAsync(customWorkspace, 10, "main", new[] { "README.md" });

            // Assert
            Assert.True(result.IsSuccess);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task Gate1_SyncConflict_AbortsMergeAndThrowsMergeConflictException()
    {
        // Arrange
        var task = new ForgeTask
        {
            Id = 201,
            WorkspaceId = _workspace.Id,
            BaseBranch = "main",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.PreImplementationBaseline
        };

        var gitManagerMock = new Mock<IGitWorkspaceManager>();
        gitManagerMock.Setup(m => m.SyncFromBaseBranchAsync(It.IsAny<Workspace>(), task.Id, "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MergeSyncResult(Success: false, HasConflict: true, ConflictedFiles: new[] { "docs/tasks/task-201.md" }, MergeCommitSha: null));
        gitManagerMock.Setup(m => m.AbortMergeAsync(It.IsAny<Workspace>(), task.Id, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var taskRepo = new InMemoryTaskRepository();
        await taskRepo.CreateAsync(task);
        var workspaceRepo = new InMemoryWorkspaceRepository();
        await workspaceRepo.CreateAsync(_workspace);
        var depStore = new InMemoryTaskDependencyStore();
        depStore.AddTask(task.Id, task.WorkspaceId, task.Status);

        var pipeline = new WorkflowPipeline(new IWorkflowStage[]
        {
            new TestWorkflowStage(StandardStageIds.PreImplementationBaseline, WorkflowStageKind.Deterministic)
        });

        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = workspaceRepo,
            DependencyStore = depStore,
            GitWorkspaceManager = gitManagerMock.Object,
            PipelineProvider = new DefaultWorkflowPipelineProvider(pipeline)
        }.Build();

        // Act & Assert
        var ex = await Assert.ThrowsAsync<MergeConflictException>(() => orchestrator.ProcessTaskAsync(task.Id));
        Assert.Equal(task.Id, ex.TaskId);
        Assert.Equal("main", ex.BaseBranch);
        Assert.Contains("docs/tasks/task-201.md", ex.ConflictedFiles);

        // Verify AbortMergeAsync was invoked to leave clean tree
        gitManagerMock.Verify(m => m.AbortMergeAsync(It.IsAny<Workspace>(), task.Id, It.IsAny<CancellationToken>()), Times.Once);

        // Verify conversation was completed as Failed (not left stuck in Running)
        var convs = await taskRepo.GetConversationsAsync(task.Id);
        var conv = Assert.Single(convs);
        Assert.Equal(StageExecutionStatus.Failed, conv.Status);
        Assert.Contains("Merge conflict", conv.Summary);
    }

    [Fact]
    public async Task Gate2_DetectedConflict_InitiatesConflictResolution()
    {
        // Arrange
        var task = new ForgeTask
        {
            Id = 202,
            WorkspaceId = _workspace.Id,
            BaseBranch = "main",
            Status = TaskOverallStatus.Reviewed,
            CurrentStageId = StandardStageIds.PostReviewVerification
        };

        var gitManagerMock = new Mock<IGitWorkspaceManager>();
        gitManagerMock.Setup(m => m.CheckMergeableAsync(It.IsAny<Workspace>(), task.Id, "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MergeabilityCheckResult(IsMergeable: false, ConflictedFiles: new[] { "src/Core.cs" }, BaseBranchSha: "b1", FeatureBranchSha: "f1"));

        var taskRepo = new InMemoryTaskRepository();
        await taskRepo.CreateAsync(task);
        var workspaceRepo = new InMemoryWorkspaceRepository();
        await workspaceRepo.CreateAsync(_workspace);
        var depStore = new InMemoryTaskDependencyStore();
        depStore.AddTask(task.Id, task.WorkspaceId, task.Status);

        var jobQueue = new FakeTaskJobQueue();

        var pipeline = new WorkflowPipeline(new IWorkflowStage[]
        {
            new TestWorkflowStage(StandardStageIds.Implementation, WorkflowStageKind.Agentic),
            new TestWorkflowStage(StandardStageIds.PostReviewVerification, WorkflowStageKind.Deterministic)
            {
                Handler = (ctx, ct) => Task.FromResult(StageExecutionResult.Success())
            }
        });

        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = workspaceRepo,
            DependencyStore = depStore,
            GitWorkspaceManager = gitManagerMock.Object,
            JobQueue = jobQueue,
            PipelineProvider = new DefaultWorkflowPipelineProvider(pipeline)
        }.Build();

        // Act
        await orchestrator.ProcessTaskAsync(task.Id);

        // Assert
        var updatedTask = await taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(updatedTask);
        Assert.Equal(TaskOverallStatus.Open, updatedTask.Status);
        Assert.Equal(StandardStageIds.Implementation, updatedTask.CurrentStageId);

        var bag = PipelineDataBag.FromJson(updatedTask.PipelineDataJson);
        var conflictFiles = bag.Get<List<string>>(PipelineDataBagKeys.ConflictFiles);
        Assert.NotNull(conflictFiles);
        Assert.Contains("src/Core.cs", conflictFiles);

        // Should be enqueued
        Assert.True(jobQueue.InferenceQueue.Contains(task.Id) || jobQueue.UrgentQueue.Contains(task.Id));
    }

    [Fact]
    public async Task Gate3_SquashMergeConflict_DiscardsApprovalAndDispatchesConflictResolution()
    {
        // Arrange
        var task = new ForgeTask
        {
            Id = 203,
            WorkspaceId = _workspace.Id,
            BaseBranch = "main",
            Status = TaskOverallStatus.Ready,
            MergeMode = MergeMode.ManualReviewRequired
        };

        var gitManagerMock = new Mock<IGitWorkspaceManager>();
        gitManagerMock.Setup(m => m.SquashAndMergeAsync(It.IsAny<Workspace>(), task.Id, "main", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new MergeConflictException(task.Id, "main", new[] { "SharedService.cs" }, "Automatic merge failed"));

        var taskRepo = new InMemoryTaskRepository();
        await taskRepo.CreateAsync(task);
        var workspaceRepo = new InMemoryWorkspaceRepository();
        await workspaceRepo.CreateAsync(_workspace);

        var jobQueue = new FakeTaskJobQueue();

        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = workspaceRepo,
            GitWorkspaceManager = gitManagerMock.Object,
            JobQueue = jobQueue
        }.Build();

        // Act
        var result = await orchestrator.CompleteTaskAndCascadeAsync(task.Id, "Merge final");

        // Assert
        Assert.Equal(CompleteTaskStatus.ConflictResolutionDispatched, result.Status);
        Assert.NotNull(result.ConflictedFiles);
        Assert.Contains("SharedService.cs", result.ConflictedFiles);

        var updatedTask = await taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(updatedTask);
        Assert.Equal(TaskOverallStatus.Open, updatedTask.Status);
        Assert.Equal(StandardStageIds.Implementation, updatedTask.CurrentStageId);

        // Verify history contains approval discarded reason
        var history = await taskRepo.GetHistoryAsync(task.Id);
        Assert.Contains(history, h => h.ChangeReason?.Contains("approval discarded", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public async Task ConflictResolution_ExhaustsBudget_EscalatesToHumanMaintainer()
    {
        // Arrange
        var task = new ForgeTask
        {
            Id = 204,
            WorkspaceId = _workspace.Id,
            BaseBranch = "main",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.Implementation
        };

        var bag = new PipelineDataBag();
        bag.Set(PipelineDataBagKeys.ConflictFiles, new List<string> { "Conflicted.cs" });
        bag.Set(PipelineDataBagKeys.ConflictResolutionAttempts, ConflictResolutionConstants.MaxConflictResolutionAttempts - 1); // 2 previous attempts
        task.PipelineDataJson = bag.ToJson();

        var taskRepo = new InMemoryTaskRepository();
        await taskRepo.CreateAsync(task);
        var workspaceRepo = new InMemoryWorkspaceRepository();
        await workspaceRepo.CreateAsync(_workspace);
        var depStore = new InMemoryTaskDependencyStore();
        depStore.AddTask(task.Id, task.WorkspaceId, task.Status);

        var gitManagerMock = new Mock<IGitWorkspaceManager>();
        gitManagerMock.Setup(m => m.SyncFromBaseBranchAsync(It.IsAny<Workspace>(), task.Id, "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MergeSyncResult(Success: false, HasConflict: true, ConflictedFiles: new[] { "Conflicted.cs" }, MergeCommitSha: null));

        var verifierMock = new Mock<IConflictResolutionVerifier>();
        verifierMock.Setup(v => v.VerifyResolutionAsync(It.IsAny<Workspace>(), task.Id, "main", It.IsAny<IReadOnlyList<string>>(), It.IsAny<PipelineDataBag?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConflictResolutionVerificationResult(false, "Conflict markers remain in Conflicted.cs", new[] { "Conflicted.cs" }));

        var pipeline = new WorkflowPipeline(new IWorkflowStage[]
        {
            new TestWorkflowStage(StandardStageIds.Implementation, WorkflowStageKind.Agentic)
            {
                Handler = (ctx, ct) => Task.FromResult(StageExecutionResult.Success())
            }
        });

        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = workspaceRepo,
            DependencyStore = depStore,
            GitWorkspaceManager = gitManagerMock.Object,
            ConflictResolutionVerifier = verifierMock.Object,
            PipelineProvider = new DefaultWorkflowPipelineProvider(pipeline)
        }.Build();

        // Act
        await orchestrator.ProcessTaskAsync(task.Id);

        // Assert
        var updatedTask = await taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(updatedTask);
        Assert.True(updatedTask.IsWaitingForHuman);
        Assert.Equal(StandardStageIds.Implementation, updatedTask.InterruptedStageId);

        var history = await taskRepo.GetHistoryAsync(task.Id);
        Assert.Contains(history, h => h.ChangeReason?.Contains(ConflictResolutionConstants.AuditConflictResolutionBudgetExhausted, StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public async Task RejectTask_ClearsHasResolvedConflict()
    {
        // Arrange
        var task = new ForgeTask
        {
            Id = 205,
            WorkspaceId = _workspace.Id,
            BaseBranch = "main",
            Status = TaskOverallStatus.Ready
        };

        var bag = new PipelineDataBag();
        bag.Set(PipelineDataBagKeys.HasResolvedConflict, true);
        task.PipelineDataJson = bag.ToJson();

        var taskRepo = new InMemoryTaskRepository();
        await taskRepo.CreateAsync(task);
        var workspaceRepo = new InMemoryWorkspaceRepository();
        await workspaceRepo.CreateAsync(_workspace);

        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = workspaceRepo
        }.Build();

        // Act
        await orchestrator.RejectTaskAsync(task.Id, "Rejection for rework");

        // Assert
        var updatedTask = await taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(updatedTask);
        Assert.Equal(TaskOverallStatus.Rejected, updatedTask.Status);

        var updatedBag = PipelineDataBag.FromJson(updatedTask.PipelineDataJson);
        Assert.Null(updatedBag.Get<bool?>(PipelineDataBagKeys.HasResolvedConflict));
    }

    private static GitCommandResult Ok(string stdout = "") => new(0, stdout, "", TimeSpan.Zero);

    [Fact]
    public async Task Verifier_UndecidedDeleteModifyConflict_IsNotAutoStagedAndFails()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempDir, "kept.cs"), "class Kept {}");

            // Delete/modify conflict: ancestor (stage 1) and 'theirs' (stage 3) only, no 'ours'
            var unmerged = "100644 aaa 1\tkept.cs\n100644 bbb 3\tkept.cs\n";
            var gitRunner = new Mock<IGitCliRunner>();
            gitRunner.Setup(r => r.ExecuteAsync(It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Ok());
            gitRunner.Setup(r => r.ExecuteAsync(It.IsAny<string>(), It.Is<IEnumerable<string>>(args => args.Contains("ls-files")), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Ok(unmerged));

            var gitWorkspaceManager = new Mock<IGitWorkspaceManager>();
            gitWorkspaceManager.Setup(m => m.GetWorktreePath(It.IsAny<Workspace>(), It.IsAny<int>())).Returns(tempDir);
            var verifier = new ConflictResolutionVerifier(gitRunner.Object, gitWorkspaceManager.Object, CreateMockEnvironmentProvider(), CreateMockPathResolver(), NullLogger<ConflictResolutionVerifier>.Instance);

            var result = await verifier.VerifyResolutionAsync(new Workspace { Id = 1, RepositoryPath = tempDir }, 10, "main", new[] { "kept.cs" });

            Assert.False(result.IsSuccess);
            Assert.Contains("Unmerged index entries", result.ErrorMessage);
            gitRunner.Verify(r => r.ExecuteAsync(It.IsAny<string>(), It.Is<IEnumerable<string>>(args => args.Contains("add")), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task Gate1_SyncConflict_MarksTaskFailedWithAudit()
    {
        var task = new ForgeTask
        {
            Id = 206,
            WorkspaceId = _workspace.Id,
            BaseBranch = "main",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.PreImplementationBaseline
        };

        var gitManagerMock = new Mock<IGitWorkspaceManager>();
        gitManagerMock.Setup(m => m.SyncFromBaseBranchAsync(It.IsAny<Workspace>(), task.Id, "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MergeSyncResult(false, true, new[] { "docs/tasks/task-206.md" }, null));

        var taskRepo = new InMemoryTaskRepository();
        await taskRepo.CreateAsync(task);
        var workspaceRepo = new InMemoryWorkspaceRepository();
        await workspaceRepo.CreateAsync(_workspace);
        var depStore = new InMemoryTaskDependencyStore();
        depStore.AddTask(task.Id, task.WorkspaceId, task.Status);

        var pipeline = new WorkflowPipeline(new IWorkflowStage[]
        {
            new TestWorkflowStage(StandardStageIds.PreImplementationBaseline, WorkflowStageKind.Deterministic)
        });

        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = workspaceRepo,
            DependencyStore = depStore,
            GitWorkspaceManager = gitManagerMock.Object,
            PipelineProvider = new DefaultWorkflowPipelineProvider(pipeline)
        }.Build();

        await Assert.ThrowsAsync<MergeConflictException>(() => orchestrator.ProcessTaskAsync(task.Id));

        var updated = await taskRepo.GetByIdAsync(task.Id);
        Assert.Equal(TaskOverallStatus.Failed, updated!.Status);
        var history = await taskRepo.GetHistoryAsync(task.Id);
        Assert.Contains(history, h => h.ChangeReason?.Contains("Pre-Implementation Baseline", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public async Task ConflictMaterialization_EmptyUnmergedList_KeepsOriginalConflictFilesForVerification()
    {
        var task = new ForgeTask
        {
            Id = 207,
            WorkspaceId = _workspace.Id,
            BaseBranch = "main",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.Implementation
        };
        var bag = new PipelineDataBag();
        bag.Set(PipelineDataBagKeys.ConflictFiles, new List<string> { "Staged.cs" });
        task.PipelineDataJson = bag.ToJson();

        var taskRepo = new InMemoryTaskRepository();
        await taskRepo.CreateAsync(task);
        var workspaceRepo = new InMemoryWorkspaceRepository();
        await workspaceRepo.CreateAsync(_workspace);
        var depStore = new InMemoryTaskDependencyStore();
        depStore.AddTask(task.Id, task.WorkspaceId, task.Status);

        // Merge still in progress, but the agent already staged everything: unmerged list is empty
        var gitManagerMock = new Mock<IGitWorkspaceManager>();
        gitManagerMock.Setup(m => m.SyncFromBaseBranchAsync(It.IsAny<Workspace>(), task.Id, "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MergeSyncResult(false, true, Array.Empty<string>(), null));

        IReadOnlyList<string>? verifiedFiles = null;
        var verifierMock = new Mock<IConflictResolutionVerifier>();
        verifierMock.Setup(v => v.VerifyResolutionAsync(It.IsAny<Workspace>(), task.Id, "main", It.IsAny<IReadOnlyList<string>>(), It.IsAny<PipelineDataBag?>(), It.IsAny<CancellationToken>()))
            .Callback<Workspace, int, string, IReadOnlyList<string>, PipelineDataBag?, CancellationToken>((_, _, _, files, _, _) => verifiedFiles = files)
            .ReturnsAsync(new ConflictResolutionVerificationResult(true));

        var pipeline = new WorkflowPipeline(new IWorkflowStage[]
        {
            new TestWorkflowStage(StandardStageIds.Implementation, WorkflowStageKind.Agentic)
            {
                Handler = (ctx, ct) => Task.FromResult(StageExecutionResult.Success())
            }
        });

        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = workspaceRepo,
            DependencyStore = depStore,
            GitWorkspaceManager = gitManagerMock.Object,
            ConflictResolutionVerifier = verifierMock.Object,
            PipelineProvider = new DefaultWorkflowPipelineProvider(pipeline)
        }.Build();

        await orchestrator.ProcessTaskAsync(task.Id);

        Assert.NotNull(verifiedFiles);
        Assert.Contains("Staged.cs", verifiedFiles!);
    }

    [Fact]
    public async Task ResumeAfterEscalation_ResetsAttemptsCounter()
    {
        var task = new ForgeTask
        {
            Id = 208,
            WorkspaceId = _workspace.Id,
            BaseBranch = "main",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.Implementation,
            IsWaitingForHuman = true,
            InterruptedStageId = StandardStageIds.Implementation
        };
        var bag = new PipelineDataBag();
        bag.Set(PipelineDataBagKeys.ConflictFiles, new List<string> { "Conflicted.cs" });
        bag.Set(PipelineDataBagKeys.ConflictResolutionAttempts, ConflictResolutionConstants.MaxConflictResolutionAttempts);
        task.PipelineDataJson = bag.ToJson();

        var taskRepo = new InMemoryTaskRepository();
        await taskRepo.CreateAsync(task);
        var workspaceRepo = new InMemoryWorkspaceRepository();
        await workspaceRepo.CreateAsync(_workspace);
        var depStore = new InMemoryTaskDependencyStore();
        depStore.AddTask(task.Id, task.WorkspaceId, task.Status);

        var gitManagerMock = new Mock<IGitWorkspaceManager>();
        gitManagerMock.Setup(m => m.SyncFromBaseBranchAsync(It.IsAny<Workspace>(), task.Id, "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MergeSyncResult(false, true, new[] { "Conflicted.cs" }, null));

        var verifierMock = new Mock<IConflictResolutionVerifier>();
        verifierMock.Setup(v => v.VerifyResolutionAsync(It.IsAny<Workspace>(), task.Id, "main", It.IsAny<IReadOnlyList<string>>(), It.IsAny<PipelineDataBag?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConflictResolutionVerificationResult(false, "markers remain", new[] { "Conflicted.cs" }));

        var pipeline = new WorkflowPipeline(new IWorkflowStage[]
        {
            new TestWorkflowStage(StandardStageIds.Implementation, WorkflowStageKind.Agentic)
            {
                Handler = (ctx, ct) => Task.FromResult(StageExecutionResult.Success())
            }
        });

        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = workspaceRepo,
            DependencyStore = depStore,
            GitWorkspaceManager = gitManagerMock.Object,
            ConflictResolutionVerifier = verifierMock.Object,
            PipelineProvider = new DefaultWorkflowPipelineProvider(pipeline)
        }.Build();

        await orchestrator.ProcessTaskAsync(task.Id);

        var updated = await taskRepo.GetByIdAsync(task.Id);
        Assert.False(updated!.IsWaitingForHuman);
        var updatedBag = PipelineDataBag.FromJson(updated.PipelineDataJson);
        Assert.Equal(1, updatedBag.Get<int?>(PipelineDataBagKeys.ConflictResolutionAttempts));
    }

    [Fact]
    public async Task Gate3_PreSquashShaFailure_PropagatesAndDoesNotSquash()
    {
        var task = new ForgeTask
        {
            Id = 209,
            WorkspaceId = _workspace.Id,
            BaseBranch = "main",
            Status = TaskOverallStatus.Ready
        };

        var gitManagerMock = new Mock<IGitWorkspaceManager>();
        gitManagerMock.Setup(m => m.GetBranchShaAsync(It.IsAny<Workspace>(), "main", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitCommandException("/repo", new[] { "rev-parse", "main" }, 128, "", "fatal"));

        var taskRepo = new InMemoryTaskRepository();
        await taskRepo.CreateAsync(task);
        var workspaceRepo = new InMemoryWorkspaceRepository();
        await workspaceRepo.CreateAsync(_workspace);

        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = workspaceRepo,
            GitWorkspaceManager = gitManagerMock.Object
        }.Build();

        await Assert.ThrowsAsync<GitCommandException>(() => orchestrator.CompleteTaskAndCascadeAsync(task.Id, "msg"));

        gitManagerMock.Verify(m => m.SquashAndMergeAsync(It.IsAny<Workspace>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Gate3_SuccessfulMerge_ClearsResolvedConflictFlagAndAttempts()
    {
        var task = new ForgeTask
        {
            Id = 210,
            WorkspaceId = _workspace.Id,
            BaseBranch = "main",
            Status = TaskOverallStatus.Ready
        };
        var bag = new PipelineDataBag();
        bag.Set(PipelineDataBagKeys.HasResolvedConflict, true);
        bag.Set(PipelineDataBagKeys.ConflictResolutionAttempts, 2);
        task.PipelineDataJson = bag.ToJson();

        var gitManagerMock = new Mock<IGitWorkspaceManager>();
        gitManagerMock.Setup(m => m.GetBranchShaAsync(It.IsAny<Workspace>(), "main", It.IsAny<CancellationToken>())).ReturnsAsync("pre-sha");
        gitManagerMock.Setup(m => m.SquashAndMergeAsync(It.IsAny<Workspace>(), task.Id, "main", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("squash-sha");

        var taskRepo = new InMemoryTaskRepository();
        await taskRepo.CreateAsync(task);
        var workspaceRepo = new InMemoryWorkspaceRepository();
        await workspaceRepo.CreateAsync(_workspace);

        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = workspaceRepo,
            GitWorkspaceManager = gitManagerMock.Object
        }.Build();

        var result = await orchestrator.CompleteTaskAndCascadeAsync(task.Id, "msg");

        Assert.Equal(CompleteTaskStatus.Completed, result.Status);
        var updated = await taskRepo.GetByIdAsync(task.Id);
        Assert.Equal(TaskOverallStatus.Closed, updated!.Status);
        Assert.Equal("squash-sha", updated.SquashCommitSha);
        var updatedBag = PipelineDataBag.FromJson(updated.PipelineDataJson);
        Assert.Null(updatedBag.Get<bool?>(PipelineDataBagKeys.HasResolvedConflict));
        Assert.Null(updatedBag.Get<int?>(PipelineDataBagKeys.ConflictResolutionAttempts));
    }

    [Fact]
    public async Task Gate3_PushRejectedAfterRetry_DoesNotRecordSquashCommitSha()
    {
        var workspace = new Workspace
        {
            Id = 2,
            Name = "Mirror Workspace",
            RepositoryPath = "/test/repo",
            Ext4MirrorPath = "/ext4/mirror",
            DefaultBaseBranch = "main"
        };
        var task = new ForgeTask
        {
            Id = 211,
            WorkspaceId = workspace.Id,
            BaseBranch = "main",
            Status = TaskOverallStatus.Ready
        };

        var gitManagerMock = new Mock<IGitWorkspaceManager>();
        gitManagerMock.Setup(m => m.GetBranchShaAsync(It.IsAny<Workspace>(), "main", It.IsAny<CancellationToken>())).ReturnsAsync("pre-sha");
        gitManagerMock.Setup(m => m.SquashAndMergeAsync(It.IsAny<Workspace>(), task.Id, "main", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("squash-sha");

        var mirrorMock = new Mock<IGitMirrorSyncService>();
        mirrorMock.Setup(m => m.PushBaseBranchToHostAsync("/ext4/mirror", "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitSyncResult(false, false, "main", "rejected", IsNonFastForward: true));

        var taskRepo = new InMemoryTaskRepository();
        await taskRepo.CreateAsync(task);
        var workspaceRepo = new InMemoryWorkspaceRepository();
        await workspaceRepo.CreateAsync(workspace);

        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = workspaceRepo,
            GitWorkspaceManager = gitManagerMock.Object,
            GitMirrorSyncService = mirrorMock.Object
        }.Build();

        await Assert.ThrowsAsync<MirrorPushRejectedException>(() => orchestrator.CompleteTaskAndCascadeAsync(task.Id, "msg"));

        Assert.Null(task.SquashCommitSha);
        Assert.NotEqual(TaskOverallStatus.Closed, task.Status);
    }

    [Fact]
    public void PromptBuilder_WhenConflictFilesPresent_InjectsConflictDirective()
    {
        var task = new ForgeTask
        {
            Id = 301,
            WorkspaceId = _workspace.Id,
            BaseBranch = "main",
            Title = "Conflict Task"
        };
        var stage = new TestWorkflowStage(StandardStageIds.Implementation, WorkflowStageKind.Agentic)
        {
            PromptTemplate = "Implement task {{ task.id }}"
        };
        var bag = new PipelineDataBag();
        bag.Set(PipelineDataBagKeys.ConflictFiles, new List<string> { "Services/Foo.cs", "Services/Bar.cs" });
        bag.Set(PipelineDataBagKeys.ConflictBaseBranch, "main");
        var context = new PipelineContext(task, _workspace, bag);

        var prompt = DefaultBootstrapPromptBuilder.Build(stage, task, context);

        Assert.Contains(ConflictResolutionConstants.ConflictDirectiveHeader, prompt);
        Assert.Contains("- Services/Foo.cs", prompt);
        Assert.Contains("- Services/Bar.cs", prompt);
        Assert.Contains("target branch 'main'", prompt);
        Assert.Contains("You MUST execute 'dotnet build' and 'dotnet test --no-build' via 'bash'", prompt);
    }

    [Fact]
    public void PromptBuilder_WhenCustomBuildAndTestCommandsInBag_InjectsConfiguredCommandsIntoDirective()
    {
        var task = new ForgeTask
        {
            Id = 302,
            WorkspaceId = _workspace.Id,
            BaseBranch = "develop",
            Title = "Node Conflict Task"
        };
        var stage = new TestWorkflowStage(StandardStageIds.Implementation, WorkflowStageKind.Agentic)
        {
            PromptTemplate = "Implement task {{ task.id }}"
        };
        var bag = new PipelineDataBag();
        bag.Set(PipelineDataBagKeys.ConflictFiles, new List<string> { "src/index.ts" });
        bag.Set(PipelineDataBagKeys.ConflictBaseBranch, "develop");
        bag.Set(PipelineDataBagKeys.BuildCommand, "npm run build");
        bag.Set(PipelineDataBagKeys.TestCommand, "npm test");
        var context = new PipelineContext(task, _workspace, bag);

        var prompt = DefaultBootstrapPromptBuilder.Build(stage, task, context);

        Assert.Contains(ConflictResolutionConstants.ConflictDirectiveHeader, prompt);
        Assert.Contains("target branch 'develop'", prompt);
        Assert.Contains("You MUST execute 'npm run build' and 'npm test' via 'bash'", prompt);
        Assert.DoesNotContain("dotnet build", prompt);
    }

    [Fact]
    public async Task PromptBuilder_WhenConfigFilePresentInWorkspace_InjectsWorkspaceConfigCommandsIntoDirective()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var configDir = Path.Combine(tempDir, ".nightcodeforge");
        Directory.CreateDirectory(configDir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(configDir, "config.json"), "{\"buildCommand\": \"cargo build\", \"testCommand\": \"cargo test\"}");

            var workspace = new Workspace
            {
                Id = 10,
                Name = "Rust Repo",
                RepositoryPath = tempDir
            };
            var task = new ForgeTask
            {
                Id = 303,
                WorkspaceId = workspace.Id,
                BaseBranch = "main",
                Title = "Rust Conflict Task"
            };
            var stage = new TestWorkflowStage(StandardStageIds.Implementation, WorkflowStageKind.Agentic)
            {
                PromptTemplate = "Implement task {{ task.id }}"
            };
            var bag = new PipelineDataBag();
            bag.Set(PipelineDataBagKeys.ConflictFiles, new List<string> { "src/main.rs" });
            bag.Set(PipelineDataBagKeys.ConflictBaseBranch, "main");
            var context = new PipelineContext(task, workspace, bag);

            var prompt = DefaultBootstrapPromptBuilder.Build(stage, task, context);

            Assert.Contains(ConflictResolutionConstants.ConflictDirectiveHeader, prompt);
            Assert.Contains("You MUST execute 'cargo build' and 'cargo test' via 'bash'", prompt);
            Assert.DoesNotContain("dotnet build", prompt);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task Verifier_ResolvedButUnstagedFile_IsStagedByVerifierAndPasses()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var filePath = Path.Combine(tempDir, "resolved.cs");
            await File.WriteAllTextAsync(filePath, "public class Resolved { public int Value = 42; }");

            var gitRunner = new Mock<IGitCliRunner>();
            gitRunner.Setup(r => r.ExecuteAsync(tempDir, It.Is<IEnumerable<string>>(args => args.Contains("add")), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Ok());
            gitRunner.Setup(r => r.ExecuteAsync(tempDir, It.Is<IEnumerable<string>>(args => args.Contains("ls-files")), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Ok(""));
            gitRunner.Setup(r => r.ExecuteAsync(tempDir, It.Is<IEnumerable<string>>(args => args.Contains("rev-parse")), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Ok(""));

            var gitWorkspaceManager = new Mock<IGitWorkspaceManager>();
            gitWorkspaceManager.Setup(m => m.GetWorktreePath(It.IsAny<Workspace>(), It.IsAny<int>())).Returns(tempDir);

            var verifier = new ConflictResolutionVerifier(gitRunner.Object, gitWorkspaceManager.Object, CreateMockEnvironmentProvider(), CreateMockPathResolver(), NullLogger<ConflictResolutionVerifier>.Instance);

            var result = await verifier.VerifyResolutionAsync(new Workspace { Id = 1, RepositoryPath = tempDir }, 10, "main", new[] { "resolved.cs" });

            Assert.True(result.IsSuccess);
            gitRunner.Verify(r => r.ExecuteAsync(tempDir, It.Is<IEnumerable<string>>(args => args.Contains("add") && args.Contains("resolved.cs")), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task Verifier_DeletedFile_SkippedFromMarkerScanAndRemovedFromIndex()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var gitRunner = new Mock<IGitCliRunner>();
            gitRunner.Setup(r => r.ExecuteAsync(tempDir, It.Is<IEnumerable<string>>(args => args.Contains("rm")), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Ok());
            gitRunner.Setup(r => r.ExecuteAsync(tempDir, It.Is<IEnumerable<string>>(args => args.Contains("ls-files")), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Ok(""));
            gitRunner.Setup(r => r.ExecuteAsync(tempDir, It.Is<IEnumerable<string>>(args => args.Contains("rev-parse")), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Ok(""));

            var gitWorkspaceManager = new Mock<IGitWorkspaceManager>();
            gitWorkspaceManager.Setup(m => m.GetWorktreePath(It.IsAny<Workspace>(), It.IsAny<int>())).Returns(tempDir);

            var verifier = new ConflictResolutionVerifier(gitRunner.Object, gitWorkspaceManager.Object, CreateMockEnvironmentProvider(), CreateMockPathResolver(), NullLogger<ConflictResolutionVerifier>.Instance);

            var result = await verifier.VerifyResolutionAsync(new Workspace { Id = 1, RepositoryPath = tempDir }, 10, "main", new[] { "deleted.cs" });

            Assert.True(result.IsSuccess);
            gitRunner.Verify(r => r.ExecuteAsync(tempDir, It.Is<IEnumerable<string>>(args => args.Contains("rm") && args.Contains("deleted.cs")), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task Verifier_BinaryFileConflict_UnstagedFailsAsUnmerged()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var binaryFile = Path.Combine(tempDir, "image.png");
            await File.WriteAllBytesAsync(binaryFile, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x00, 0x00, 0x00 });

            var unmerged = "100644 aaa 1\timage.png\n100644 bbb 2\timage.png\n100644 ccc 3\timage.png\n";
            var gitRunner = new Mock<IGitCliRunner>();
            gitRunner.Setup(r => r.ExecuteAsync(tempDir, It.IsAny<IEnumerable<string>>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Ok());
            gitRunner.Setup(r => r.ExecuteAsync(tempDir, It.Is<IEnumerable<string>>(args => args.Contains("ls-files")), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Ok(unmerged));

            var gitWorkspaceManager = new Mock<IGitWorkspaceManager>();
            gitWorkspaceManager.Setup(m => m.GetWorktreePath(It.IsAny<Workspace>(), It.IsAny<int>())).Returns(tempDir);

            var verifier = new ConflictResolutionVerifier(gitRunner.Object, gitWorkspaceManager.Object, CreateMockEnvironmentProvider(), CreateMockPathResolver(), NullLogger<ConflictResolutionVerifier>.Instance);

            var result = await verifier.VerifyResolutionAsync(new Workspace { Id = 1, RepositoryPath = tempDir }, 10, "main", new[] { "image.png" });

            Assert.False(result.IsSuccess);
            Assert.Contains("Unmerged index entries", result.ErrorMessage);
            gitRunner.Verify(r => r.ExecuteAsync(tempDir, It.Is<IEnumerable<string>>(args => args.Contains("add")), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task Verifier_OrchestratorCommit_CreatesMergeCommitWhenMergeHeadExists()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var filePath = Path.Combine(tempDir, "file.cs");
            await File.WriteAllTextAsync(filePath, "public class Resolved {}");

            var gitRunner = new Mock<IGitCliRunner>();
            gitRunner.Setup(r => r.ExecuteAsync(tempDir, It.Is<IEnumerable<string>>(args => args.Contains("add")), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Ok());
            gitRunner.Setup(r => r.ExecuteAsync(tempDir, It.Is<IEnumerable<string>>(args => args.Contains("ls-files")), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Ok(""));
            gitRunner.Setup(r => r.ExecuteAsync(tempDir, It.Is<IEnumerable<string>>(args => args.Contains("rev-parse") && args.Contains("MERGE_HEAD")), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Ok("merge_head_sha"));
            gitRunner.Setup(r => r.ExecuteAsync(tempDir, It.Is<IEnumerable<string>>(args => args.Contains("commit")), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Ok());

            var gitWorkspaceManager = new Mock<IGitWorkspaceManager>();
            gitWorkspaceManager.Setup(m => m.GetWorktreePath(It.IsAny<Workspace>(), It.IsAny<int>())).Returns(tempDir);

            var verifier = new ConflictResolutionVerifier(gitRunner.Object, gitWorkspaceManager.Object, CreateMockEnvironmentProvider(), CreateMockPathResolver(), NullLogger<ConflictResolutionVerifier>.Instance);

            var result = await verifier.VerifyResolutionAsync(new Workspace { Id = 1, RepositoryPath = tempDir }, 10, "main", new[] { "file.cs" });

            Assert.True(result.IsSuccess);
            gitRunner.Verify(r => r.ExecuteAsync(tempDir, It.Is<IEnumerable<string>>(args => args.Contains("commit") && args.Contains("--no-edit") && args.Contains("Resolve merge conflicts with main")), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ConflictResolution_AttemptsCounterBoundary_LoopsBackBeforeBudgetExhaustion()
    {
        var task = new ForgeTask
        {
            Id = 302,
            WorkspaceId = _workspace.Id,
            BaseBranch = "main",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.Implementation
        };
        var bag = new PipelineDataBag();
        bag.Set(PipelineDataBagKeys.ConflictFiles, new List<string> { "Conflicted.cs" });
        bag.Set(PipelineDataBagKeys.ConflictResolutionAttempts, 1);
        task.PipelineDataJson = bag.ToJson();

        var taskRepo = new InMemoryTaskRepository();
        await taskRepo.CreateAsync(task);
        var workspaceRepo = new InMemoryWorkspaceRepository();
        await workspaceRepo.CreateAsync(_workspace);
        var depStore = new InMemoryTaskDependencyStore();
        depStore.AddTask(task.Id, task.WorkspaceId, task.Status);

        var gitManagerMock = new Mock<IGitWorkspaceManager>();
        gitManagerMock.Setup(m => m.SyncFromBaseBranchAsync(It.IsAny<Workspace>(), task.Id, "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MergeSyncResult(false, true, new[] { "Conflicted.cs" }, null));

        var verifierMock = new Mock<IConflictResolutionVerifier>();
        verifierMock.Setup(v => v.VerifyResolutionAsync(It.IsAny<Workspace>(), task.Id, "main", It.IsAny<IReadOnlyList<string>>(), It.IsAny<PipelineDataBag?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConflictResolutionVerificationResult(false, "Unmerged index entries remain", new[] { "Conflicted.cs" }));

        var pipeline = new WorkflowPipeline(new IWorkflowStage[]
        {
            new TestWorkflowStage(StandardStageIds.Implementation, WorkflowStageKind.Agentic)
            {
                Handler = (ctx, ct) => Task.FromResult(StageExecutionResult.Success())
            }
        });

        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = workspaceRepo,
            DependencyStore = depStore,
            GitWorkspaceManager = gitManagerMock.Object,
            ConflictResolutionVerifier = verifierMock.Object,
            PipelineProvider = new DefaultWorkflowPipelineProvider(pipeline)
        }.Build();

        await orchestrator.ProcessTaskAsync(task.Id);

        var updated = await taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(updated);
        Assert.False(updated.IsWaitingForHuman);
        Assert.Equal(StandardStageIds.Implementation, updated.CurrentStageId);

        var updatedBag = PipelineDataBag.FromJson(updated.PipelineDataJson);
        Assert.Equal(2, updatedBag.Get<int?>(PipelineDataBagKeys.ConflictResolutionAttempts));
        var errors = updatedBag.Get<List<string>>(PipelineDataBagKeys.CompilerErrors);
        Assert.NotNull(errors);
        Assert.Contains(errors, e => e.Contains("Unmerged index entries"));
    }

    [Fact]
    public async Task ConflictResolution_VerifiedPass_RecordsAgentResolutionInTaskHistory()
    {
        var task = new ForgeTask
        {
            Id = 303,
            WorkspaceId = _workspace.Id,
            BaseBranch = "main",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.Implementation
        };
        var bag = new PipelineDataBag();
        bag.Set(PipelineDataBagKeys.ConflictFiles, new List<string> { "Resolved.cs" });
        task.PipelineDataJson = bag.ToJson();

        var taskRepo = new InMemoryTaskRepository();
        await taskRepo.CreateAsync(task);
        var workspaceRepo = new InMemoryWorkspaceRepository();
        await workspaceRepo.CreateAsync(_workspace);
        var depStore = new InMemoryTaskDependencyStore();
        depStore.AddTask(task.Id, task.WorkspaceId, task.Status);

        var gitManagerMock = new Mock<IGitWorkspaceManager>();
        gitManagerMock.Setup(m => m.SyncFromBaseBranchAsync(It.IsAny<Workspace>(), task.Id, "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MergeSyncResult(false, true, new[] { "Resolved.cs" }, null));

        var verifierMock = new Mock<IConflictResolutionVerifier>();
        verifierMock.Setup(v => v.VerifyResolutionAsync(It.IsAny<Workspace>(), task.Id, "main", It.IsAny<IReadOnlyList<string>>(), It.IsAny<PipelineDataBag?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConflictResolutionVerificationResult(true));

        var pipeline = new WorkflowPipeline(new IWorkflowStage[]
        {
            new TestWorkflowStage(StandardStageIds.Implementation, WorkflowStageKind.Agentic)
            {
                Handler = (ctx, ct) => Task.FromResult(StageExecutionResult.Success())
            },
            new TestWorkflowStage(StandardStageIds.PostImplementationGates, WorkflowStageKind.Deterministic)
        });

        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = workspaceRepo,
            DependencyStore = depStore,
            GitWorkspaceManager = gitManagerMock.Object,
            ConflictResolutionVerifier = verifierMock.Object,
            PipelineProvider = new DefaultWorkflowPipelineProvider(pipeline)
        }.Build();

        await orchestrator.ProcessTaskAsync(task.Id);

        var history = await taskRepo.GetHistoryAsync(task.Id);
        Assert.Contains(history, h => h.ChangeReason?.Contains("Implementation Agent resolved merge conflicts", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public async Task Gate2_CleanNoAdvance_TransitionsDirectlyToReady()
    {
        var task = new ForgeTask
        {
            Id = 304,
            WorkspaceId = _workspace.Id,
            BaseBranch = "main",
            Status = TaskOverallStatus.Reviewed,
            CurrentStageId = StandardStageIds.PostReviewVerification
        };

        var taskRepo = new InMemoryTaskRepository();
        await taskRepo.CreateAsync(task);
        var workspaceRepo = new InMemoryWorkspaceRepository();
        await workspaceRepo.CreateAsync(_workspace);
        var depStore = new InMemoryTaskDependencyStore();
        depStore.AddTask(task.Id, task.WorkspaceId, task.Status);

        var gitManagerMock = new Mock<IGitWorkspaceManager>();
        gitManagerMock.Setup(m => m.CheckMergeableAsync(It.IsAny<Workspace>(), task.Id, "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MergeabilityCheckResult(true, Array.Empty<string>(), "base_sha", "feat_sha"));
        gitManagerMock.Setup(m => m.SyncFromBaseBranchAsync(It.IsAny<Workspace>(), task.Id, "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MergeSyncResult(true, false, Array.Empty<string>(), "feat_sha"));

        int stage6ExecutionCount = 0;
        var pipeline = new WorkflowPipeline(new IWorkflowStage[]
        {
            new TestWorkflowStage(StandardStageIds.PostReviewVerification, WorkflowStageKind.Deterministic)
            {
                Handler = (ctx, ct) =>
                {
                    stage6ExecutionCount++;
                    return Task.FromResult(StageExecutionResult.Success());
                }
            }
        });

        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = workspaceRepo,
            DependencyStore = depStore,
            GitWorkspaceManager = gitManagerMock.Object,
            PipelineProvider = new DefaultWorkflowPipelineProvider(pipeline)
        }.Build();

        await orchestrator.ProcessTaskAsync(task.Id);

        var updated = await taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(updated);
        Assert.Equal(TaskOverallStatus.Ready, updated.Status);
        Assert.Equal(1, stage6ExecutionCount);
    }

    [Fact]
    public async Task Gate2_CleanWithAdvance_ReverifiesAndTransitionsToReady()
    {
        var task = new ForgeTask
        {
            Id = 305,
            WorkspaceId = _workspace.Id,
            BaseBranch = "main",
            Status = TaskOverallStatus.Reviewed,
            CurrentStageId = StandardStageIds.PostReviewVerification
        };
        var bag = new PipelineDataBag();
        bag.Set(PipelineDataBagKeys.HasResolvedConflict, true);
        task.PipelineDataJson = bag.ToJson();

        var taskRepo = new InMemoryTaskRepository();
        await taskRepo.CreateAsync(task);
        var workspaceRepo = new InMemoryWorkspaceRepository();
        await workspaceRepo.CreateAsync(_workspace);
        var depStore = new InMemoryTaskDependencyStore();
        depStore.AddTask(task.Id, task.WorkspaceId, task.Status);

        var gitManagerMock = new Mock<IGitWorkspaceManager>();
        gitManagerMock.Setup(m => m.CheckMergeableAsync(It.IsAny<Workspace>(), task.Id, "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MergeabilityCheckResult(true, Array.Empty<string>(), "base_sha", "feat_sha"));
        gitManagerMock.Setup(m => m.SyncFromBaseBranchAsync(It.IsAny<Workspace>(), task.Id, "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MergeSyncResult(true, false, Array.Empty<string>(), "new_merge_commit_sha"));

        int stage6ExecutionCount = 0;
        var pipeline = new WorkflowPipeline(new IWorkflowStage[]
        {
            new TestWorkflowStage(StandardStageIds.PostReviewVerification, WorkflowStageKind.Deterministic)
            {
                Handler = (ctx, ct) =>
                {
                    stage6ExecutionCount++;
                    return Task.FromResult(StageExecutionResult.Success());
                }
            }
        });

        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = workspaceRepo,
            DependencyStore = depStore,
            GitWorkspaceManager = gitManagerMock.Object,
            PipelineProvider = new DefaultWorkflowPipelineProvider(pipeline)
        }.Build();

        await orchestrator.ProcessTaskAsync(task.Id);

        var updated = await taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(updated);
        Assert.Equal(TaskOverallStatus.Ready, updated.Status);
        Assert.Equal(2, stage6ExecutionCount);

        var history = await taskRepo.GetHistoryAsync(task.Id);
        Assert.Contains(history, h => h.ChangeReason == ConflictResolutionConstants.AuditPostReviewVerificationPassedReopened);
    }

    [Fact]
    public async Task Gate2_CleanWithAdvance_ReverificationFails_LoopsBackToImplementation()
    {
        var task = new ForgeTask
        {
            Id = 306,
            WorkspaceId = _workspace.Id,
            BaseBranch = "main",
            Status = TaskOverallStatus.Reviewed,
            CurrentStageId = StandardStageIds.PostReviewVerification
        };

        var taskRepo = new InMemoryTaskRepository();
        await taskRepo.CreateAsync(task);
        var workspaceRepo = new InMemoryWorkspaceRepository();
        await workspaceRepo.CreateAsync(_workspace);
        var depStore = new InMemoryTaskDependencyStore();
        depStore.AddTask(task.Id, task.WorkspaceId, task.Status);

        var gitManagerMock = new Mock<IGitWorkspaceManager>();
        gitManagerMock.Setup(m => m.CheckMergeableAsync(It.IsAny<Workspace>(), task.Id, "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MergeabilityCheckResult(true, Array.Empty<string>(), "base_sha", "feat_sha"));
        gitManagerMock.Setup(m => m.SyncFromBaseBranchAsync(It.IsAny<Workspace>(), task.Id, "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MergeSyncResult(true, false, Array.Empty<string>(), "new_merge_commit_sha"));

        int executionCount = 0;
        var pipeline = new WorkflowPipeline(new IWorkflowStage[]
        {
            new TestWorkflowStage(StandardStageIds.Implementation, WorkflowStageKind.Agentic),
            new TestWorkflowStage(StandardStageIds.PostReviewVerification, WorkflowStageKind.Deterministic)
            {
                Handler = (ctx, ct) =>
                {
                    executionCount++;
                    return Task.FromResult(executionCount == 1
                        ? StageExecutionResult.Success()
                        : StageExecutionResult.Failure("Semantic build breakage on merged tree"));
                }
            }
        });

        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = workspaceRepo,
            DependencyStore = depStore,
            GitWorkspaceManager = gitManagerMock.Object,
            PipelineProvider = new DefaultWorkflowPipelineProvider(pipeline)
        }.Build();

        await orchestrator.ProcessTaskAsync(task.Id);

        var updated = await taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(updated);
        Assert.Equal(StandardStageIds.Implementation, updated.CurrentStageId);
        Assert.NotEqual(TaskOverallStatus.Ready, updated.Status);
    }

    [Fact]
    public async Task Gate2_UncommittedWorktreeChanges_CheckpointsBeforeMergeabilityCheck()
    {
        var task = new ForgeTask
        {
            Id = 307,
            WorkspaceId = _workspace.Id,
            BaseBranch = "main",
            Status = TaskOverallStatus.Reviewed,
            CurrentStageId = StandardStageIds.PostReviewVerification
        };

        var taskRepo = new InMemoryTaskRepository();
        await taskRepo.CreateAsync(task);
        var workspaceRepo = new InMemoryWorkspaceRepository();
        await workspaceRepo.CreateAsync(_workspace);
        var depStore = new InMemoryTaskDependencyStore();
        depStore.AddTask(task.Id, task.WorkspaceId, task.Status);

        var callOrder = new List<string>();
        var gitManagerMock = new Mock<IGitWorkspaceManager>();
        gitManagerMock.Setup(m => m.CreateWipCheckpointAsync(It.IsAny<Workspace>(), task.Id, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<Workspace, int, string, string, CancellationToken>((ws, id, sId, msg, ct) => callOrder.Add($"checkpoint:{msg}"))
            .ReturnsAsync("wip_sha");
        gitManagerMock.Setup(m => m.CheckMergeableAsync(It.IsAny<Workspace>(), task.Id, "main", It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("check_mergeable"))
            .ReturnsAsync(new MergeabilityCheckResult(true, Array.Empty<string>(), "base_sha", "feat_sha"));
        gitManagerMock.Setup(m => m.SyncFromBaseBranchAsync(It.IsAny<Workspace>(), task.Id, "main", It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("sync"))
            .ReturnsAsync(new MergeSyncResult(true, false, Array.Empty<string>(), "feat_sha"));

        var pipeline = new WorkflowPipeline(new IWorkflowStage[]
        {
            new TestWorkflowStage(StandardStageIds.PostReviewVerification, WorkflowStageKind.Deterministic)
            {
                Handler = (ctx, ct) => Task.FromResult(StageExecutionResult.Success())
            }
        });

        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = workspaceRepo,
            DependencyStore = depStore,
            GitWorkspaceManager = gitManagerMock.Object,
            PipelineProvider = new DefaultWorkflowPipelineProvider(pipeline)
        }.Build();

        await orchestrator.ProcessTaskAsync(task.Id);

        Assert.Equal(new[]
        {
            $"checkpoint:Completed stage '{StandardStageIds.PostReviewVerification}'",
            "checkpoint:WIP checkpoint before Gate 2 mergeability check",
            "check_mergeable",
            "sync"
        }, callOrder);
    }

    [Fact]
    public async Task Gate3_AutomaticMode_SquashConflict_DispatchesConflictResolutionWithoutApprovalDiscardHistory()
    {
        var task = new ForgeTask
        {
            Id = 308,
            WorkspaceId = _workspace.Id,
            BaseBranch = "main",
            Status = TaskOverallStatus.Ready,
            MergeMode = MergeMode.Automatic
        };

        var gitManagerMock = new Mock<IGitWorkspaceManager>();
        gitManagerMock.Setup(m => m.SquashAndMergeAsync(It.IsAny<Workspace>(), task.Id, "main", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new MergeConflictException(task.Id, "main", new[] { "AutoConflicted.cs" }, "auto conflict"));

        var taskRepo = new InMemoryTaskRepository();
        await taskRepo.CreateAsync(task);
        var workspaceRepo = new InMemoryWorkspaceRepository();
        await workspaceRepo.CreateAsync(_workspace);

        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = workspaceRepo,
            GitWorkspaceManager = gitManagerMock.Object
        }.Build();

        var result = await orchestrator.CompleteTaskAndCascadeAsync(task.Id, "Auto squash commit");

        Assert.Equal(CompleteTaskStatus.ConflictResolutionDispatched, result.Status);
        var history = await taskRepo.GetHistoryAsync(task.Id);
        Assert.DoesNotContain(history, h => h.ChangeReason?.Contains("approval discarded", StringComparison.OrdinalIgnoreCase) == true);
        Assert.Contains(history, h => h.ChangeReason?.Contains("Routed to Implementation Agent", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public async Task Gate3_Conflict_DependentsStayBlocked()
    {
        var parentTask = new ForgeTask
        {
            Id = 309,
            WorkspaceId = _workspace.Id,
            BaseBranch = "main",
            Status = TaskOverallStatus.Ready
        };

        var childTask = new ForgeTask
        {
            Id = 310,
            WorkspaceId = _workspace.Id,
            BaseBranch = "main",
            Status = TaskOverallStatus.Open
        };

        var gitManagerMock = new Mock<IGitWorkspaceManager>();
        gitManagerMock.Setup(m => m.SquashAndMergeAsync(It.IsAny<Workspace>(), parentTask.Id, "main", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new MergeConflictException(parentTask.Id, "main", new[] { "ParentConflict.cs" }, "conflict"));

        var taskRepo = new InMemoryTaskRepository();
        await taskRepo.CreateAsync(parentTask);
        await taskRepo.CreateAsync(childTask);

        var workspaceRepo = new InMemoryWorkspaceRepository();
        await workspaceRepo.CreateAsync(_workspace);

        var depStore = new InMemoryTaskDependencyStore();
        depStore.AddTask(parentTask.Id, _workspace.Id, parentTask.Status);
        depStore.AddTask(childTask.Id, _workspace.Id, childTask.Status);
        depStore.AddDependency(_workspace.Id, childTask.Id, parentTask.Id);

        var depEngine = new Mock<IDependencyResolutionEngine>();
        var jobQueue = new FakeTaskJobQueue();

        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = workspaceRepo,
            DependencyStore = depStore,
            DependencyEngine = depEngine.Object,
            GitWorkspaceManager = gitManagerMock.Object,
            JobQueue = jobQueue
        }.Build();

        var result = await orchestrator.CompleteTaskAndCascadeAsync(parentTask.Id, "Squash parent");

        Assert.Equal(CompleteTaskStatus.ConflictResolutionDispatched, result.Status);
        depEngine.Verify(e => e.ResolveCascadeUnblockedTasksAsync(It.IsAny<int>()), Times.Never);
        Assert.DoesNotContain(childTask.Id, jobQueue.InferenceQueue);
        Assert.DoesNotContain(childTask.Id, jobQueue.UrgentQueue);
    }

    [Fact]
    public async Task Gate1_SyncConflictThenRetryLeavesCleanWorktree()
    {
        var task = new ForgeTask
        {
            Id = 311,
            WorkspaceId = _workspace.Id,
            BaseBranch = "main",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.PreImplementationBaseline
        };

        var taskRepo = new InMemoryTaskRepository();
        await taskRepo.CreateAsync(task);
        var workspaceRepo = new InMemoryWorkspaceRepository();
        await workspaceRepo.CreateAsync(_workspace);
        var depStore = new InMemoryTaskDependencyStore();
        depStore.AddTask(task.Id, task.WorkspaceId, task.Status);

        int syncAttempts = 0;
        var gitManagerMock = new Mock<IGitWorkspaceManager>();
        gitManagerMock.Setup(m => m.SyncFromBaseBranchAsync(It.IsAny<Workspace>(), task.Id, "main", It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                syncAttempts++;
                return Task.FromResult(syncAttempts == 1
                    ? new MergeSyncResult(false, true, new[] { "docs/tasks/task-311.md" }, null)
                    : new MergeSyncResult(true, false, Array.Empty<string>(), "clean_sha"));
            });

        bool abortCalled = false;
        gitManagerMock.Setup(m => m.AbortMergeAsync(It.IsAny<Workspace>(), task.Id, It.IsAny<CancellationToken>()))
            .Callback(() => abortCalled = true)
            .Returns(Task.CompletedTask);

        var pipeline = new WorkflowPipeline(new IWorkflowStage[]
        {
            new TestWorkflowStage(StandardStageIds.PreImplementationBaseline, WorkflowStageKind.Deterministic),
            new TestWorkflowStage(StandardStageIds.Implementation, WorkflowStageKind.Agentic)
        });

        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = workspaceRepo,
            DependencyStore = depStore,
            GitWorkspaceManager = gitManagerMock.Object,
            PipelineProvider = new DefaultWorkflowPipelineProvider(pipeline)
        }.Build();

        await Assert.ThrowsAsync<MergeConflictException>(() => orchestrator.ProcessTaskAsync(task.Id));
        Assert.True(abortCalled);

        task.Status = TaskOverallStatus.Open;
        await taskRepo.UpdateAsync(task);
        await depStore.UpdateTaskStatusAsync(task.Id, TaskOverallStatus.Open);

        await orchestrator.ProcessTaskAsync(task.Id);
        Assert.Equal(2, syncAttempts);
    }

    [Fact]
    public async Task Verifier_DeterministicBuildFails_ReturnsFailureWithTruncatedTailErrorsAndNoCommit()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var resolvedFile = Path.Combine(tempDir, "file.cs");
            await File.WriteAllTextAsync(resolvedFile, "public class Resolved { }");

            var gitRunner = new Mock<IGitCliRunner>();
            gitRunner.Setup(r => r.ExecuteAsync(It.IsAny<string>(), It.Is<IEnumerable<string>>(args => args.Contains("add")), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GitCommandResult(0, "", "", TimeSpan.Zero));
            gitRunner.Setup(r => r.ExecuteAsync(It.IsAny<string>(), It.Is<IEnumerable<string>>(args => args.Contains("ls-files")), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GitCommandResult(0, "", "", TimeSpan.Zero));
            gitRunner.Setup(r => r.ExecuteAsync(It.IsAny<string>(), It.Is<IEnumerable<string>>(args => args.Contains("status")), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GitCommandResult(0, "", "", TimeSpan.Zero));

            var gitWorkspaceManager = new Mock<IGitWorkspaceManager>();
            gitWorkspaceManager.Setup(m => m.GetWorktreePath(It.IsAny<Workspace>(), It.IsAny<int>())).Returns(tempDir);

            // Generate compiler output > 4000 characters
            var longDiagnostics = new string('A', 3000) + "\nCS1002: ; expected at line 42\n" + new string('B', 2000);
            var env = new Mock<IExecutionEnvironment>();
            env.Setup(e => e.ExecuteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new CommandResult(1, longDiagnostics, "", TimeSpan.FromSeconds(2)));

            var envProvider = new Mock<IExecutionEnvironmentProvider>();
            envProvider.Setup(p => p.GetEnvironment(It.IsAny<Workspace>(), It.IsAny<SandboxSecurityTier>(), It.IsAny<int?>()))
                .Returns(env.Object);

            var verifier = new ConflictResolutionVerifier(gitRunner.Object, gitWorkspaceManager.Object, envProvider.Object, CreateMockPathResolver(), NullLogger<ConflictResolutionVerifier>.Instance);

            var workspace = new Workspace
            {
                Id = 1,
                RepositoryPath = tempDir,
                DefaultBaseBranch = "main"
            };

            var result = await verifier.VerifyResolutionAsync(workspace, 10, "main", new[] { "file.cs" });

            Assert.False(result.IsSuccess);
            Assert.Contains("Deterministic build failed", result.ErrorMessage);
            Assert.NotNull(result.CompilerErrors);
            Assert.True(result.CompilerErrors.Length <= ConflictResolutionConstants.MaxCompilerErrorsLength);
            Assert.Contains("truncated", result.CompilerErrors);
            // Verify tail is preserved: ending with 'B' characters
            Assert.EndsWith(new string('B', 100), result.CompilerErrors.TrimEnd());

            // Verify no merge commit was created
            gitRunner.Verify(r => r.ExecuteAsync(It.IsAny<string>(), It.Is<IEnumerable<string>>(args => args.Contains("commit")), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task Verifier_DeterministicBuildTimesOut_ReturnsHardFailureAndNoCommit()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var resolvedFile = Path.Combine(tempDir, "file.cs");
            await File.WriteAllTextAsync(resolvedFile, "public class Resolved { }");

            var gitRunner = new Mock<IGitCliRunner>();
            gitRunner.Setup(r => r.ExecuteAsync(It.IsAny<string>(), It.Is<IEnumerable<string>>(args => args.Contains("add")), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GitCommandResult(0, "", "", TimeSpan.Zero));
            gitRunner.Setup(r => r.ExecuteAsync(It.IsAny<string>(), It.Is<IEnumerable<string>>(args => args.Contains("ls-files")), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GitCommandResult(0, "", "", TimeSpan.Zero));
            gitRunner.Setup(r => r.ExecuteAsync(It.IsAny<string>(), It.Is<IEnumerable<string>>(args => args.Contains("status")), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GitCommandResult(0, "", "", TimeSpan.Zero));

            var gitWorkspaceManager = new Mock<IGitWorkspaceManager>();
            gitWorkspaceManager.Setup(m => m.GetWorktreePath(It.IsAny<Workspace>(), It.IsAny<int>())).Returns(tempDir);

            var env = new Mock<IExecutionEnvironment>();
            env.Setup(e => e.ExecuteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new TimeoutException("Operation timed out after 30 seconds"));

            var envProvider = new Mock<IExecutionEnvironmentProvider>();
            envProvider.Setup(p => p.GetEnvironment(It.IsAny<Workspace>(), It.IsAny<SandboxSecurityTier>(), It.IsAny<int?>()))
                .Returns(env.Object);

            var verifier = new ConflictResolutionVerifier(gitRunner.Object, gitWorkspaceManager.Object, envProvider.Object, CreateMockPathResolver(), NullLogger<ConflictResolutionVerifier>.Instance);

            var workspace = new Workspace
            {
                Id = 1,
                RepositoryPath = tempDir,
                DefaultBaseBranch = "main",
                BuildTimeoutSeconds = 45
            };

            var result = await verifier.VerifyResolutionAsync(workspace, 10, "main", new[] { "file.cs" });

            Assert.False(result.IsSuccess);
            Assert.Contains("timed out after 45 seconds", result.ErrorMessage);
            Assert.Contains("Deterministic build timed out", result.CompilerErrors);

            // Verify no merge commit was created
            gitRunner.Verify(r => r.ExecuteAsync(It.IsAny<string>(), It.Is<IEnumerable<string>>(args => args.Contains("commit")), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task Verifier_DeterministicBuildPasses_CreatesMergeCommitAndReturnsSuccess()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var resolvedFile = Path.Combine(tempDir, "file.cs");
            await File.WriteAllTextAsync(resolvedFile, "public class Resolved { }");

            var gitRunner = new Mock<IGitCliRunner>();
            gitRunner.Setup(r => r.ExecuteAsync(It.IsAny<string>(), It.Is<IEnumerable<string>>(args => args.Contains("add")), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GitCommandResult(0, "", "", TimeSpan.Zero));
            gitRunner.Setup(r => r.ExecuteAsync(It.IsAny<string>(), It.Is<IEnumerable<string>>(args => args.Contains("ls-files")), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GitCommandResult(0, "", "", TimeSpan.Zero));
            gitRunner.Setup(r => r.ExecuteAsync(It.IsAny<string>(), It.Is<IEnumerable<string>>(args => args.Contains("status")), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GitCommandResult(0, "", "", TimeSpan.Zero));
            gitRunner.Setup(r => r.ExecuteAsync(It.IsAny<string>(), It.Is<IEnumerable<string>>(args => args.Contains("rev-parse")), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GitCommandResult(0, "mock-sha", "", TimeSpan.Zero));
            gitRunner.Setup(r => r.ExecuteAsync(It.IsAny<string>(), It.Is<IEnumerable<string>>(args => args.Contains("commit")), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GitCommandResult(0, "", "", TimeSpan.Zero));

            var gitWorkspaceManager = new Mock<IGitWorkspaceManager>();
            gitWorkspaceManager.Setup(m => m.GetWorktreePath(It.IsAny<Workspace>(), It.IsAny<int>())).Returns(tempDir);

            var env = new Mock<IExecutionEnvironment>();
            env.Setup(e => e.ExecuteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new CommandResult(0, "Build succeeded. 0 Warning(s) 0 Error(s)", "", TimeSpan.FromSeconds(3)));

            var envProvider = new Mock<IExecutionEnvironmentProvider>();
            envProvider.Setup(p => p.GetEnvironment(It.IsAny<Workspace>(), It.IsAny<SandboxSecurityTier>(), It.IsAny<int?>()))
                .Returns(env.Object);

            var verifier = new ConflictResolutionVerifier(gitRunner.Object, gitWorkspaceManager.Object, envProvider.Object, CreateMockPathResolver(), NullLogger<ConflictResolutionVerifier>.Instance);

            var workspace = new Workspace
            {
                Id = 1,
                RepositoryPath = tempDir,
                DefaultBaseBranch = "main"
            };

            var result = await verifier.VerifyResolutionAsync(workspace, 10, "main", new[] { "file.cs" });

            Assert.True(result.IsSuccess);
            Assert.Null(result.ErrorMessage);
            Assert.Null(result.CompilerErrors);

            // Verify merge commit was created with proper message
            gitRunner.Verify(r => r.ExecuteAsync(It.IsAny<string>(), It.Is<IEnumerable<string>>(args => args.Contains("commit") && args.Contains("Resolve merge conflicts with main")), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task Verifier_StagingBeforeBuild_StagesUntrackedSourceFilesAndIgnoresBuildArtifacts()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var resolvedFile = Path.Combine(tempDir, "file.cs");
            await File.WriteAllTextAsync(resolvedFile, "public class Resolved { }");

            var stagedFiles = new List<string>();

            var gitRunner = new Mock<IGitCliRunner>();
            gitRunner.Setup(r => r.ExecuteAsync(It.IsAny<string>(), It.Is<IEnumerable<string>>(args => args.Contains("add")), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .Callback<string, IEnumerable<string>, int?, CancellationToken>((_, args, _, _) =>
                {
                    var file = args.LastOrDefault();
                    if (file != null) stagedFiles.Add(file);
                })
                .ReturnsAsync(new GitCommandResult(0, "", "", TimeSpan.Zero));

            gitRunner.Setup(r => r.ExecuteAsync(It.IsAny<string>(), It.Is<IEnumerable<string>>(args => args.Contains("ls-files")), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GitCommandResult(0, "", "", TimeSpan.Zero));

            // Status shows newly created source file and a build artifact in bin/
            gitRunner.Setup(r => r.ExecuteAsync(It.IsAny<string>(), It.Is<IEnumerable<string>>(args => args.Contains("status")), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GitCommandResult(0, "?? src/NewHelper.cs\n?? bin/Debug/app.dll\n?? obj/Debug/app.pdb", "", TimeSpan.Zero));

            gitRunner.Setup(r => r.ExecuteAsync(It.IsAny<string>(), It.Is<IEnumerable<string>>(args => args.Contains("rev-parse")), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GitCommandResult(0, "mock-sha", "", TimeSpan.Zero));
            gitRunner.Setup(r => r.ExecuteAsync(It.IsAny<string>(), It.Is<IEnumerable<string>>(args => args.Contains("commit")), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GitCommandResult(0, "", "", TimeSpan.Zero));

            var gitWorkspaceManager = new Mock<IGitWorkspaceManager>();
            gitWorkspaceManager.Setup(m => m.GetWorktreePath(It.IsAny<Workspace>(), It.IsAny<int>())).Returns(tempDir);

            var env = new Mock<IExecutionEnvironment>();
            env.Setup(e => e.ExecuteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new CommandResult(0, "Build succeeded", "", TimeSpan.FromSeconds(1)));

            var envProvider = new Mock<IExecutionEnvironmentProvider>();
            envProvider.Setup(p => p.GetEnvironment(It.IsAny<Workspace>(), It.IsAny<SandboxSecurityTier>(), It.IsAny<int?>()))
                .Returns(env.Object);

            var verifier = new ConflictResolutionVerifier(gitRunner.Object, gitWorkspaceManager.Object, envProvider.Object, CreateMockPathResolver(), NullLogger<ConflictResolutionVerifier>.Instance);

            var workspace = new Workspace
            {
                Id = 1,
                RepositoryPath = tempDir,
                DefaultBaseBranch = "main"
            };

            var result = await verifier.VerifyResolutionAsync(workspace, 10, "main", new[] { "file.cs" });

            Assert.True(result.IsSuccess);
            Assert.Contains("file.cs", stagedFiles);
            Assert.Contains("src/NewHelper.cs", stagedFiles);
            Assert.DoesNotContain("bin/Debug/app.dll", stagedFiles);
            Assert.DoesNotContain("obj/Debug/app.pdb", stagedFiles);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task Verifier_NullBuildTimeout_ResolvesToWorkflowDefault()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var resolvedFile = Path.Combine(tempDir, "file.cs");
            await File.WriteAllTextAsync(resolvedFile, "public class Resolved { }");

            var gitRunner = new Mock<IGitCliRunner>();
            gitRunner.Setup(r => r.ExecuteAsync(It.IsAny<string>(), It.Is<IEnumerable<string>>(args => args.Contains("add")), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GitCommandResult(0, "", "", TimeSpan.Zero));
            gitRunner.Setup(r => r.ExecuteAsync(It.IsAny<string>(), It.Is<IEnumerable<string>>(args => args.Contains("ls-files")), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GitCommandResult(0, "", "", TimeSpan.Zero));
            gitRunner.Setup(r => r.ExecuteAsync(It.IsAny<string>(), It.Is<IEnumerable<string>>(args => args.Contains("status")), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GitCommandResult(0, "", "", TimeSpan.Zero));
            gitRunner.Setup(r => r.ExecuteAsync(It.IsAny<string>(), It.Is<IEnumerable<string>>(args => args.Contains("rev-parse")), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GitCommandResult(0, "mock-merge-head", "", TimeSpan.Zero));
            gitRunner.Setup(r => r.ExecuteAsync(It.IsAny<string>(), It.Is<IEnumerable<string>>(args => args.Contains("commit")), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GitCommandResult(0, "", "", TimeSpan.Zero));

            var gitWorkspaceManager = new Mock<IGitWorkspaceManager>();
            gitWorkspaceManager.Setup(m => m.GetWorktreePath(It.IsAny<Workspace>(), It.IsAny<int>())).Returns(tempDir);

            int? capturedTimeout = null;
            var env = new Mock<IExecutionEnvironment>();
            env.Setup(e => e.ExecuteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
                .Callback<string, string, int?, IReadOnlyDictionary<string, string>, CancellationToken>((_, _, timeout, _, _) =>
                {
                    capturedTimeout = timeout;
                })
                .ReturnsAsync(new CommandResult(0, "Build succeeded", "", TimeSpan.FromSeconds(1)));

            var envProvider = new Mock<IExecutionEnvironmentProvider>();
            envProvider.Setup(p => p.GetEnvironment(It.IsAny<Workspace>(), It.IsAny<SandboxSecurityTier>(), It.IsAny<int?>()))
                .Returns(env.Object);

            var verifier = new ConflictResolutionVerifier(gitRunner.Object, gitWorkspaceManager.Object, envProvider.Object, CreateMockPathResolver(), NullLogger<ConflictResolutionVerifier>.Instance);

            var workspace = new Workspace
            {
                Id = 1,
                RepositoryPath = tempDir,
                DefaultBaseBranch = "main",
                BuildTimeoutSeconds = null // Null should resolve to WorkflowDefaults.DefaultBuildTimeoutSeconds
            };

            var result = await verifier.VerifyResolutionAsync(workspace, 10, "main", new[] { "file.cs" });

            Assert.True(result.IsSuccess);
            Assert.Equal(WorkflowDefaults.DefaultBuildTimeoutSeconds, capturedTimeout);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public void PromptBuilder_ConflictMandate_PrependedAtTopAndIncludesInspectionHints()
    {
        var task = new ForgeTask
        {
            Id = 310,
            WorkspaceId = _workspace.Id,
            BaseBranch = "main",
            Title = "Mandate Placement Task"
        };
        var stage = new TestWorkflowStage(StandardStageIds.Implementation, WorkflowStageKind.Agentic)
        {
            PromptTemplate = "Persona: You are a senior engineer."
        };
        var bag = new PipelineDataBag();
        bag.Set(PipelineDataBagKeys.ConflictFiles, new List<string> { "Services/OrderService.cs" });
        bag.Set(PipelineDataBagKeys.ConflictBaseBranch, "main");
        var context = new PipelineContext(task, _workspace, bag);

        var prompt = DefaultBootstrapPromptBuilder.Build(stage, task, context);

        Assert.StartsWith(ConflictResolutionConstants.ConflictDirectiveHeader, prompt);
        Assert.Contains("git diff --cached", prompt);
        Assert.Contains("git status", prompt);
        Assert.Contains("Persona: You are a senior engineer.", prompt);
    }

    [Fact]
    public void PromptBuilder_WithCompilerErrors_RendersDiagnosticsBlock()
    {
        var task = new ForgeTask
        {
            Id = 311,
            WorkspaceId = _workspace.Id,
            BaseBranch = "main",
            Title = "Compiler Diagnostics Prompt Task"
        };
        var stage = new TestWorkflowStage(StandardStageIds.Implementation, WorkflowStageKind.Agentic)
        {
            PromptTemplate = "Implement task {{ task.id }}"
        };
        var bag = new PipelineDataBag();
        bag.Set(PipelineDataBagKeys.ConflictFiles, new List<string> { "Services/OrderService.cs" });
        bag.Set(PipelineDataBagKeys.CompilerErrors, new List<string> { "Error CS1002: ; expected in OrderService.cs", "Error CS0246: The type or namespace 'IOrder' could not be found" });
        var context = new PipelineContext(task, _workspace, bag);

        var prompt = DefaultBootstrapPromptBuilder.Build(stage, task, context);

        Assert.Contains("PREVIOUS COMPILER DIAGNOSTICS (Resolve these compilation errors):", prompt);
        Assert.Contains("Error CS1002: ; expected", prompt);
        Assert.Contains("Error CS0246: The type or namespace 'IOrder' could not be found", prompt);
    }

    [Fact]
    public void PromptBuilder_WithoutCompilerErrors_OmitsDiagnosticsBlock()
    {
        var task = new ForgeTask
        {
            Id = 312,
            WorkspaceId = _workspace.Id,
            BaseBranch = "main",
            Title = "Clean Diagnostics Prompt Task"
        };
        var stage = new TestWorkflowStage(StandardStageIds.Implementation, WorkflowStageKind.Agentic)
        {
            PromptTemplate = "Implement task {{ task.id }}"
        };
        var bag = new PipelineDataBag();
        bag.Set(PipelineDataBagKeys.ConflictFiles, new List<string> { "Services/OrderService.cs" });
        var context = new PipelineContext(task, _workspace, bag);

        var prompt = DefaultBootstrapPromptBuilder.Build(stage, task, context);

        Assert.DoesNotContain("PREVIOUS COMPILER DIAGNOSTICS", prompt);
    }

    [Fact]
    public async Task ConflictResolution_LifetimeDetectionsCeiling_EscalatesToHumanMaintainer()
    {
        var task = new ForgeTask
        {
            Id = 313,
            WorkspaceId = _workspace.Id,
            BaseBranch = "main",
            Status = TaskOverallStatus.Reviewed,
            CurrentStageId = StandardStageIds.PostReviewVerification
        };
        var bag = new PipelineDataBag();
        bag.Set(PipelineDataBagKeys.LifetimeConflictDetections, ConflictResolutionConstants.MaxLifetimeConflictDetections - 1); // 2 previous detections
        task.PipelineDataJson = bag.ToJson();

        var taskRepo = new InMemoryTaskRepository();
        await taskRepo.CreateAsync(task);
        var workspaceRepo = new InMemoryWorkspaceRepository();
        await workspaceRepo.CreateAsync(_workspace);
        var depStore = new InMemoryTaskDependencyStore();
        depStore.AddTask(task.Id, task.WorkspaceId, task.Status);

        var gitManagerMock = new Mock<IGitWorkspaceManager>();
        gitManagerMock.Setup(m => m.CheckMergeableAsync(It.IsAny<Workspace>(), task.Id, "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MergeabilityCheckResult(IsMergeable: false, ConflictedFiles: new[] { "src/Order.cs" }, BaseBranchSha: "b1", FeatureBranchSha: "f1"));

        var jobQueue = new FakeTaskJobQueue();

        var pipeline = new WorkflowPipeline(new IWorkflowStage[]
        {
            new TestWorkflowStage(StandardStageIds.Implementation, WorkflowStageKind.Agentic),
            new TestWorkflowStage(StandardStageIds.PostReviewVerification, WorkflowStageKind.Deterministic)
            {
                Handler = (ctx, ct) => Task.FromResult(StageExecutionResult.Success())
            }
        });

        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = workspaceRepo,
            DependencyStore = depStore,
            GitWorkspaceManager = gitManagerMock.Object,
            JobQueue = jobQueue,
            PipelineProvider = new DefaultWorkflowPipelineProvider(pipeline)
        }.Build();

        await orchestrator.ProcessTaskAsync(task.Id);

        var updated = await taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(updated);
        Assert.True(updated.IsWaitingForHuman);
        Assert.Equal(StandardStageIds.Implementation, updated.InterruptedStageId);

        var updatedBag = PipelineDataBag.FromJson(updated.PipelineDataJson);
        Assert.Equal(ConflictResolutionConstants.MaxLifetimeConflictDetections, updatedBag.Get<int?>(PipelineDataBagKeys.LifetimeConflictDetections));

        var history = await taskRepo.GetHistoryAsync(task.Id);
        Assert.Contains(history, h => h.ChangeReason != null && h.ChangeReason.Contains("distinct merge conflicts against advancing base branch. Escalated to maintainer"));
        Assert.Empty(jobQueue.ImplementationQueue);
    }

    [Fact]
    public async Task ConflictResolution_ExhaustsTotalAttemptsBudget_EscalatesToHumanMaintainer()
    {
        var task = new ForgeTask
        {
            Id = 314,
            WorkspaceId = _workspace.Id,
            BaseBranch = "main",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.Implementation
        };
        var bag = new PipelineDataBag();
        bag.Set(PipelineDataBagKeys.ConflictFiles, new List<string> { "src/Order.cs" });
        bag.Set(PipelineDataBagKeys.ConflictResolutionAttempts, 0); // 0 in this conflict
        bag.Set(PipelineDataBagKeys.TotalConflictResolutionAttempts, ConflictResolutionConstants.MaxTotalConflictResolutionAttempts - 1); // 5 total across task lifetime
        task.PipelineDataJson = bag.ToJson();

        var taskRepo = new InMemoryTaskRepository();
        await taskRepo.CreateAsync(task);
        var workspaceRepo = new InMemoryWorkspaceRepository();
        await workspaceRepo.CreateAsync(_workspace);
        var depStore = new InMemoryTaskDependencyStore();
        depStore.AddTask(task.Id, task.WorkspaceId, task.Status);

        var verifierMock = new Mock<IConflictResolutionVerifier>();
        verifierMock.Setup(v => v.VerifyResolutionAsync(It.IsAny<Workspace>(), task.Id, "main", It.IsAny<IReadOnlyList<string>>(), It.IsAny<PipelineDataBag?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConflictResolutionVerificationResult(false, "Build failed", CompilerErrors: "error CS0001"));

        var pipeline = new WorkflowPipeline(new IWorkflowStage[]
        {
            new TestWorkflowStage(StandardStageIds.Implementation, WorkflowStageKind.Agentic)
            {
                Handler = (ctx, ct) => Task.FromResult(StageExecutionResult.Success())
            }
        });

        var gitManagerMock = new Mock<IGitWorkspaceManager>();
        gitManagerMock.Setup(m => m.SyncFromBaseBranchAsync(It.IsAny<Workspace>(), task.Id, "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MergeSyncResult(false, true, new[] { "src/Order.cs" }, null));

        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = workspaceRepo,
            DependencyStore = depStore,
            GitWorkspaceManager = gitManagerMock.Object,
            ConflictResolutionVerifier = verifierMock.Object,
            PipelineProvider = new DefaultWorkflowPipelineProvider(pipeline)
        }.Build();

        await orchestrator.ProcessTaskAsync(task.Id);

        var updated = await taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(updated);
        Assert.True(updated.IsWaitingForHuman);
        Assert.Equal(StandardStageIds.Implementation, updated.InterruptedStageId);

        var history = await taskRepo.GetHistoryAsync(task.Id);
        Assert.Contains(history, h => h.ChangeReason?.Contains(ConflictResolutionConstants.AuditConflictResolutionBudgetExhausted, StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public async Task ConflictResolution_VerificationFailure_ClearsCompilerErrorsOnMarkerScanFailureAndLoopsBack()
    {
        var task = new ForgeTask
        {
            Id = 315,
            WorkspaceId = _workspace.Id,
            BaseBranch = "main",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.Implementation
        };
        var bag = new PipelineDataBag();
        bag.Set(PipelineDataBagKeys.ConflictFiles, new List<string> { "src/Order.cs" });
        bag.Set(PipelineDataBagKeys.ConflictResolutionAttempts, 0);
        bag.Set(PipelineDataBagKeys.CompilerErrors, new List<string> { "Old compiler error" });
        task.PipelineDataJson = bag.ToJson();

        var taskRepo = new InMemoryTaskRepository();
        await taskRepo.CreateAsync(task);
        var workspaceRepo = new InMemoryWorkspaceRepository();
        await workspaceRepo.CreateAsync(_workspace);
        var depStore = new InMemoryTaskDependencyStore();
        depStore.AddTask(task.Id, task.WorkspaceId, task.Status);

        var jobQueue = new FakeTaskJobQueue();

        var gitManagerMock = new Mock<IGitWorkspaceManager>();
        gitManagerMock.Setup(m => m.SyncFromBaseBranchAsync(It.IsAny<Workspace>(), task.Id, "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MergeSyncResult(false, true, new[] { "src/Order.cs" }, null));

        var verifierMock = new Mock<IConflictResolutionVerifier>();
        verifierMock.Setup(v => v.VerifyResolutionAsync(It.IsAny<Workspace>(), task.Id, "main", It.IsAny<IReadOnlyList<string>>(), It.IsAny<PipelineDataBag?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConflictResolutionVerificationResult(false, "Conflict markers remain in src/Order.cs", CompilerErrors: null));

        var pipeline = new WorkflowPipeline(new IWorkflowStage[]
        {
            new TestWorkflowStage(StandardStageIds.Implementation, WorkflowStageKind.Agentic)
            {
                QueueTier = StageQueueTier.Implementation,
                Handler = (ctx, ct) => Task.FromResult(StageExecutionResult.Success())
            }
        });

        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = workspaceRepo,
            DependencyStore = depStore,
            GitWorkspaceManager = gitManagerMock.Object,
            JobQueue = jobQueue,
            ConflictResolutionVerifier = verifierMock.Object,
            PipelineProvider = new DefaultWorkflowPipelineProvider(pipeline)
        }.Build();

        await orchestrator.ProcessTaskAsync(task.Id);

        var updated = await taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(updated);
        Assert.False(updated.IsWaitingForHuman);
        Assert.Equal(StandardStageIds.Implementation, updated.CurrentStageId);
        Assert.Equal(TaskOverallStatus.Open, updated.Status);

        var updatedBag = PipelineDataBag.FromJson(updated.PipelineDataJson);
        Assert.Equal(1, updatedBag.Get<int?>(PipelineDataBagKeys.ConflictResolutionAttempts));
        Assert.Equal(1, updatedBag.Get<int?>(PipelineDataBagKeys.TotalConflictResolutionAttempts));
        var errors = updatedBag.Get<List<string>>(PipelineDataBagKeys.CompilerErrors);
        Assert.NotNull(errors);
        Assert.Contains(errors, e => e.Contains("Conflict markers remain"));

        Assert.Single(jobQueue.ImplementationQueue);
        Assert.Equal(task.Id, jobQueue.ImplementationQueue[0]);
    }

    [Fact]
    public async Task ConflictResolution_VerifiedPass_TraversesDownstreamGatesToReady()
    {
        var task = new ForgeTask
        {
            Id = 316,
            WorkspaceId = _workspace.Id,
            BaseBranch = "main",
            Status = TaskOverallStatus.Open,
            CurrentStageId = StandardStageIds.Implementation
        };
        var bag = new PipelineDataBag();
        bag.Set(PipelineDataBagKeys.ConflictFiles, new List<string> { "src/Resolved.cs" });
        task.PipelineDataJson = bag.ToJson();

        var taskRepo = new InMemoryTaskRepository();
        await taskRepo.CreateAsync(task);
        var workspaceRepo = new InMemoryWorkspaceRepository();
        await workspaceRepo.CreateAsync(_workspace);
        var depStore = new InMemoryTaskDependencyStore();
        depStore.AddTask(task.Id, task.WorkspaceId, task.Status);

        var gitManagerMock = new Mock<IGitWorkspaceManager>();
        gitManagerMock.Setup(m => m.CheckMergeableAsync(It.IsAny<Workspace>(), task.Id, "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MergeabilityCheckResult(true, Array.Empty<string>(), "base-sha", "feat-sha"));
        var syncCount = 0;
        gitManagerMock.Setup(m => m.SyncFromBaseBranchAsync(It.IsAny<Workspace>(), task.Id, "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => syncCount++ == 0
                ? new MergeSyncResult(false, true, new[] { "src/Resolved.cs" }, "feat-sha")
                : new MergeSyncResult(true, false, Array.Empty<string>(), "feat-sha"));

        var verifierMock = new Mock<IConflictResolutionVerifier>();
        verifierMock.Setup(v => v.VerifyResolutionAsync(It.IsAny<Workspace>(), task.Id, "main", It.IsAny<IReadOnlyList<string>>(), It.IsAny<PipelineDataBag?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConflictResolutionVerificationResult(true));

        var jobQueue = new FakeTaskJobQueue();

        var stagesVisited = new List<string>();

        var pipeline = new WorkflowPipeline(new IWorkflowStage[]
        {
            new TestWorkflowStage(StandardStageIds.Implementation, WorkflowStageKind.Agentic)
            {
                QueueTier = StageQueueTier.Implementation,
                Handler = (ctx, ct) => { stagesVisited.Add(StandardStageIds.Implementation); return Task.FromResult(StageExecutionResult.Success()); }
            },
            new TestWorkflowStage(StandardStageIds.PostImplementationGates, WorkflowStageKind.Deterministic)
            {
                QueueTier = StageQueueTier.Implementation,
                Handler = (ctx, ct) => { stagesVisited.Add(StandardStageIds.PostImplementationGates); return Task.FromResult(StageExecutionResult.Success()); }
            },
            new TestWorkflowStage(StandardStageIds.CodeReviewAndFix, WorkflowStageKind.Agentic)
            {
                QueueTier = StageQueueTier.CodeReview,
                Handler = (ctx, ct) => { stagesVisited.Add(StandardStageIds.CodeReviewAndFix); return Task.FromResult(StageExecutionResult.Success()); }
            },
            new TestWorkflowStage(StandardStageIds.PostReviewVerification, WorkflowStageKind.Deterministic)
            {
                QueueTier = StageQueueTier.CodeReview,
                Handler = (ctx, ct) => { stagesVisited.Add(StandardStageIds.PostReviewVerification); return Task.FromResult(StageExecutionResult.Success()); }
            }
        });

        var orchestrator = new TestOrchestratorBuilder
        {
            TaskRepository = taskRepo,
            WorkspaceRepository = workspaceRepo,
            DependencyStore = depStore,
            GitWorkspaceManager = gitManagerMock.Object,
            JobQueue = jobQueue,
            ConflictResolutionVerifier = verifierMock.Object,
            PipelineProvider = new DefaultWorkflowPipelineProvider(pipeline)
        }.Build();

        // Step 1: Implementation agent turn verifies conflict resolution
        await orchestrator.ProcessTaskAsync(task.Id);
        var taskAfterImpl = await taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(taskAfterImpl);
        Assert.Equal(StandardStageIds.PostImplementationGates, taskAfterImpl.CurrentStageId);
        Assert.Equal(TaskOverallStatus.Open, taskAfterImpl.Status);

        var bagAfterImpl = PipelineDataBag.FromJson(taskAfterImpl.PipelineDataJson);
        Assert.True(bagAfterImpl.Get<bool?>(PipelineDataBagKeys.HasResolvedConflict));
        Assert.Null(bagAfterImpl.Get<List<string>>(PipelineDataBagKeys.ConflictFiles));

        // Step 2: PostImplementationGates
        await orchestrator.ProcessTaskAsync(task.Id);
        var taskAfterPostImpl = await taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(taskAfterPostImpl);
        Assert.Equal(StandardStageIds.CodeReviewAndFix, taskAfterPostImpl.CurrentStageId);
        Assert.Equal(TaskOverallStatus.Open, taskAfterPostImpl.Status);

        // Step 3: CodeReviewAndFix
        await orchestrator.ProcessTaskAsync(task.Id);
        var taskAfterCR = await taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(taskAfterCR);
        Assert.Equal(StandardStageIds.PostReviewVerification, taskAfterCR.CurrentStageId);
        Assert.Equal(TaskOverallStatus.Open, taskAfterCR.Status);

        // Step 4: PostReviewVerification and Gate 2
        await orchestrator.ProcessTaskAsync(task.Id);
        var taskAfterAll = await taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(taskAfterAll);
        Assert.Equal(TaskOverallStatus.Ready, taskAfterAll.Status);

        var finalBag = PipelineDataBag.FromJson(taskAfterAll.PipelineDataJson);
        Assert.True(finalBag.Get<bool?>(PipelineDataBagKeys.HasResolvedConflict));

        Assert.Equal(new[]
        {
            StandardStageIds.Implementation,
            StandardStageIds.PostImplementationGates,
            StandardStageIds.CodeReviewAndFix,
            StandardStageIds.PostReviewVerification
        }, stagesVisited);
    }

    [Fact]
    public async Task Verifier_WithWslLinuxWorktree_ResolvesHostPathForMarkerScanAndSelectiveStaging()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var resolvedFile = Path.Combine(tempDir, "OllamaRoutingMiddleware.cs");
            await File.WriteAllTextAsync(resolvedFile, "public class OllamaRoutingMiddleware { }");

            var stagedFiles = new List<string>();

            var gitRunner = new Mock<IGitCliRunner>();
            gitRunner.Setup(r => r.ExecuteAsync(It.IsAny<string>(), It.Is<IEnumerable<string>>(args => args.Contains("add")), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .Callback<string, IEnumerable<string>, int?, CancellationToken>((_, args, _, _) =>
                {
                    var file = args.LastOrDefault();
                    if (file != null) stagedFiles.Add(file);
                })
                .ReturnsAsync(new GitCommandResult(0, "", "", TimeSpan.Zero));

            gitRunner.Setup(r => r.ExecuteAsync(It.IsAny<string>(), It.Is<IEnumerable<string>>(args => args.Contains("ls-files")), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GitCommandResult(0, "", "", TimeSpan.Zero));

            gitRunner.Setup(r => r.ExecuteAsync(It.IsAny<string>(), It.Is<IEnumerable<string>>(args => args.Contains("status")), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GitCommandResult(0, "", "", TimeSpan.Zero));

            gitRunner.Setup(r => r.ExecuteAsync(It.IsAny<string>(), It.Is<IEnumerable<string>>(args => args.Contains("rev-parse")), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GitCommandResult(0, "mock-sha", "", TimeSpan.Zero));

            gitRunner.Setup(r => r.ExecuteAsync(It.IsAny<string>(), It.Is<IEnumerable<string>>(args => args.Contains("commit")), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GitCommandResult(0, "", "", TimeSpan.Zero));

            var linuxWorktreePath = "/home/gruearbre/.nightcodeforge/wks/1/task-8";

            var gitWorkspaceManager = new Mock<IGitWorkspaceManager>();
            gitWorkspaceManager.Setup(m => m.GetWorktreePath(It.IsAny<Workspace>(), It.IsAny<int>())).Returns(linuxWorktreePath);

            var pathResolver = new Mock<IWorkspacePathResolver>();
            pathResolver.Setup(p => p.ToHostPath(linuxWorktreePath)).Returns(tempDir);

            var env = new Mock<IExecutionEnvironment>();
            env.Setup(e => e.ExecuteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new CommandResult(0, "Build succeeded", "", TimeSpan.FromSeconds(1)));

            var envProvider = new Mock<IExecutionEnvironmentProvider>();
            envProvider.Setup(p => p.GetEnvironment(It.IsAny<Workspace>(), It.IsAny<SandboxSecurityTier>(), It.IsAny<int?>()))
                .Returns(env.Object);

            var verifier = new ConflictResolutionVerifier(gitRunner.Object, gitWorkspaceManager.Object, envProvider.Object, pathResolver.Object, NullLogger<ConflictResolutionVerifier>.Instance);

            var workspace = new Workspace
            {
                Id = 1,
                RepositoryPath = "/test/repo",
                DefaultBaseBranch = "main"
            };

            var result = await verifier.VerifyResolutionAsync(workspace, 8, "main", new[] { "OllamaRoutingMiddleware.cs" });

            Assert.True(result.IsSuccess);
            Assert.Contains("OllamaRoutingMiddleware.cs", stagedFiles);
            // Verify git add was called with linuxWorktreePath for execution, not host path
            gitRunner.Verify(r => r.ExecuteAsync(linuxWorktreePath, It.Is<IEnumerable<string>>(args => args.Contains("add") && args.Contains("OllamaRoutingMiddleware.cs")), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Once);
            // Verify git rm was NEVER called
            gitRunner.Verify(r => r.ExecuteAsync(It.IsAny<string>(), It.Is<IEnumerable<string>>(args => args.Contains("rm")), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }
}


