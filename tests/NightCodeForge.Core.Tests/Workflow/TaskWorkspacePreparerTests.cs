using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Core.Concurrency;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Tests.Fixtures;
using NightCodeForge.Core.Workflow;
using Xunit;

namespace NightCodeForge.Core.Tests.Workflow;

public class TaskWorkspacePreparerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly InMemoryWorkspaceRepository _workspaceRepo = new();
    private readonly WorkspaceLockProvider _lockProvider = new();
    private readonly Mock<IGitWorkspaceManager> _gitManagerMock = new();
    private readonly Mock<IGitMirrorSyncService> _mirrorSyncMock = new();
    private readonly Mock<ITaskMarkdownSyncService> _markdownSyncMock = new();
    private readonly Mock<IWorkspacePathResolver> _pathResolverMock = new();
    private readonly Mock<IExecutionEnvironmentProvider> _envProviderMock = new();

    public TaskWorkspacePreparerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ncf_preparer_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _pathResolverMock.Setup(p => p.ToHostPath(It.IsAny<string>())).Returns<string>(p => p);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors in tests
        }
    }

    private TaskWorkspacePreparer CreateSut() => new(
        _workspaceRepo,
        _lockProvider,
        _gitManagerMock.Object,
        _mirrorSyncMock.Object,
        _markdownSyncMock.Object,
        _pathResolverMock.Object,
        _envProviderMock.Object,
        NullLogger<TaskWorkspacePreparer>.Instance);

    [Fact]
    public void Constructor_WhenAnyDependencyIsNull_ThrowsArgumentNullException()
    {
        var logger = NullLogger<TaskWorkspacePreparer>.Instance;
        Assert.Throws<ArgumentNullException>(() => new TaskWorkspacePreparer(null!, _lockProvider, _gitManagerMock.Object, _mirrorSyncMock.Object, _markdownSyncMock.Object, _pathResolverMock.Object, _envProviderMock.Object, logger));
        Assert.Throws<ArgumentNullException>(() => new TaskWorkspacePreparer(_workspaceRepo, null!, _gitManagerMock.Object, _mirrorSyncMock.Object, _markdownSyncMock.Object, _pathResolverMock.Object, _envProviderMock.Object, logger));
        Assert.Throws<ArgumentNullException>(() => new TaskWorkspacePreparer(_workspaceRepo, _lockProvider, null!, _mirrorSyncMock.Object, _markdownSyncMock.Object, _pathResolverMock.Object, _envProviderMock.Object, logger));
        Assert.Throws<ArgumentNullException>(() => new TaskWorkspacePreparer(_workspaceRepo, _lockProvider, _gitManagerMock.Object, null!, _markdownSyncMock.Object, _pathResolverMock.Object, _envProviderMock.Object, logger));
        Assert.Throws<ArgumentNullException>(() => new TaskWorkspacePreparer(_workspaceRepo, _lockProvider, _gitManagerMock.Object, _mirrorSyncMock.Object, null!, _pathResolverMock.Object, _envProviderMock.Object, logger));
        Assert.Throws<ArgumentNullException>(() => new TaskWorkspacePreparer(_workspaceRepo, _lockProvider, _gitManagerMock.Object, _mirrorSyncMock.Object, _markdownSyncMock.Object, null!, _envProviderMock.Object, logger));
        Assert.Throws<ArgumentNullException>(() => new TaskWorkspacePreparer(_workspaceRepo, _lockProvider, _gitManagerMock.Object, _mirrorSyncMock.Object, _markdownSyncMock.Object, _pathResolverMock.Object, null!, logger));
        Assert.Throws<ArgumentNullException>(() => new TaskWorkspacePreparer(_workspaceRepo, _lockProvider, _gitManagerMock.Object, _mirrorSyncMock.Object, _markdownSyncMock.Object, _pathResolverMock.Object, _envProviderMock.Object, null!));
    }

    [Fact]
    public async Task AcquireWorkspaceLockAsync_And_AcquireTaskLockAsync_ReturnValidReleasers()
    {
        var sut = CreateSut();
        using var wsLock = await sut.AcquireWorkspaceLockAsync(1);
        using var taskLock = await sut.AcquireTaskLockAsync(1, 42);
        Assert.NotNull(wsLock);
        Assert.NotNull(taskLock);
    }

    [Fact]
    public async Task GetWorkspaceAsync_PrefersNavigationPropertyThenFallsBackToRepository()
    {
        var sut = CreateSut();
        var repoWorkspace = await _workspaceRepo.CreateAsync(new Workspace { Id = 7, Name = "Repo WS", RepositoryPath = _tempDir });
        var navWorkspace = new Workspace { Id = 8, Name = "Nav WS", RepositoryPath = _tempDir };

        var taskWithNav = new ForgeTask { Id = 1, WorkspaceId = 7, Workspace = navWorkspace };
        var taskWithoutNav = new ForgeTask { Id = 2, WorkspaceId = 7 };

        Assert.Same(navWorkspace, await sut.GetWorkspaceAsync(taskWithNav));
        Assert.Same(repoWorkspace, await sut.GetWorkspaceAsync(taskWithoutNav));
    }

    [Fact]
    public async Task ResolveAgentProfileAsync_PrioritizesInterruptedAgentProfileAndClearsField()
    {
        var sut = CreateSut();
        var interruptedProfile = new AgentProfile { Id = "interrupted-profile", Name = "Interrupted" };
        var defaultProfile = new AgentProfile { Id = StandardStageIds.Implementation, Name = "Default" };
        var workspace = new Workspace
        {
            Id = 1,
            Name = "Profile WS",
            AgentProfiles = [interruptedProfile, defaultProfile]
        };

        var task = new ForgeTask
        {
            Id = 10,
            WorkspaceId = 1,
            InterruptedAgentProfileId = "interrupted-profile"
        };

        var resolved = await sut.ResolveAgentProfileAsync(workspace, task, StandardStageIds.Implementation);

        Assert.Same(interruptedProfile, resolved);
        Assert.Null(task.InterruptedAgentProfileId);
    }

    [Fact]
    public async Task ResolveAgentProfileAsync_FallsBackToStageDirectiveMatch()
    {
        var sut = CreateSut();
        var directiveProfile = new AgentProfile
        {
            Id = "custom-coder",
            Name = "Custom Coder",
            StageDirectives = new Dictionary<string, string> { [StandardStageIds.Implementation] = "Write clean code" }
        };
        var workspace = new Workspace
        {
            Id = 1,
            Name = "Directive WS",
            AgentProfiles = [directiveProfile]
        };

        var task = new ForgeTask { Id = 11, WorkspaceId = 1 };
        var resolved = await sut.ResolveAgentProfileAsync(workspace, task, StandardStageIds.Implementation);

        Assert.Same(directiveProfile, resolved);
    }

    [Fact]
    public async Task PrepareTaskBranchAndMirrorAsync_ContainerMode_InitializesMirrorPullsAndChecksOutBranch()
    {
        var sut = CreateSut();
        var workspace = await _workspaceRepo.CreateAsync(new Workspace
        {
            Id = 1,
            Name = "Container WS",
            RepositoryPath = _tempDir,
            ExecutionMode = ExecutionMode.ContainerSandbox,
            DefaultBaseBranch = "main"
        });

        _pathResolverMock.Setup(p => p.GetAgentWorkingClonePath(1)).Returns("/home/user/.ncf/wks/1/clone");
        _mirrorSyncMock.Setup(m => m.InitializeMirrorAsync(workspace, "/home/user/.ncf/wks/1/clone", It.IsAny<CancellationToken>()))
            .ReturnsAsync("/home/user/.ncf/wks/1/clone");
        _gitManagerMock.Setup(g => g.ResolveEffectiveBaseBranchAsync(workspace, 42, "master", It.IsAny<CancellationToken>()))
            .ReturnsAsync("main");

        var task = new ForgeTask
        {
            Id = 42,
            WorkspaceId = 1,
            BaseBranch = "master"
        };

        string? callbackOldBranch = null;
        string? callbackNewBranch = null;
        await sut.PrepareTaskBranchAndMirrorAsync(
            workspace,
            task,
            (oldBranch, newBranch, _) =>
            {
                callbackOldBranch = oldBranch;
                callbackNewBranch = newBranch;
                return Task.CompletedTask;
            });

        Assert.Equal("feature/task-42", task.FeatureBranch);
        Assert.Equal("main", task.BaseBranch);
        Assert.Equal("master", callbackOldBranch);
        Assert.Equal("main", callbackNewBranch);
        Assert.Equal("/home/user/.ncf/wks/1/clone", workspace.Ext4MirrorPath);

        _mirrorSyncMock.Verify(m => m.ConfigureHostRepositoryAsync(_tempDir, It.IsAny<CancellationToken>()), Times.Once);
        _gitManagerMock.Verify(g => g.ResolveEffectiveBaseBranchAsync(workspace, 42, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        _mirrorSyncMock.Verify(m => m.PullFromHostAsync("/home/user/.ncf/wks/1/clone", "main", It.IsAny<CancellationToken>()), Times.Once);
        _gitManagerMock.Verify(g => g.CheckoutTaskBranchAsync(workspace, 42, "main", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void ResolveWorkspacePaths_SelectsExistingWorktreeDirectoryAsTargetSyncPath()
    {
        var sut = CreateSut();
        var worktreeDir = Path.Combine(_tempDir, "task-50");
        Directory.CreateDirectory(worktreeDir);

        var workspace = new Workspace { Id = 1, RepositoryPath = _tempDir };
        _gitManagerMock.Setup(g => g.GetWorktreePath(workspace, 50)).Returns(worktreeDir);

        var resolved = sut.ResolveWorkspacePaths(workspace, 50);

        Assert.Equal(worktreeDir, resolved.TaskWorkingPath);
        Assert.Equal(worktreeDir, resolved.HostWorkingPath);
        Assert.Equal(_tempDir, resolved.HostRepoPath);
        Assert.Equal(worktreeDir, resolved.TargetSyncPath);
    }

    [Fact]
    public async Task SyncPreStageMarkdownAsync_WhenResumedRunDetectsVersionIncrease_SetsModifiedFlagAndTaskMarkdownPath()
    {
        var sut = CreateSut();
        var workspace = new Workspace { Id = 1, RepositoryPath = _tempDir };
        var task = new ForgeTask { Id = 15, WorkspaceId = 1, Title = "Old Title", Description = "Old Desc", Version = 1 };
        var pipelineContext = new PipelineContext(task, workspace);
        var stage = TestWorkflowPipelines.CreateReviewStage();

        var updatedTaskFromDisk = new ForgeTask
        {
            Id = 15,
            WorkspaceId = 1,
            Title = "New Title",
            Description = "New Desc",
            Priority = 1,
            Version = 2
        };
        _markdownSyncMock.Setup(m => m.SyncAllTasksFromDiskAsync(
                workspace,
                _tempDir,
                _tempDir,
                null,
                It.IsAny<string>(),
                TaskSyncConstants.PreserveEditsBeforeResumeReason,
                15,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([updatedTaskFromDisk]);

        var taskMdFile = Path.Combine(_tempDir, "docs", "tasks", "0015-task.md");
        var baseline = new Dictionary<int, TaskBaselineEntry>();
        _markdownSyncMock.Setup(m => m.SyncAllTasksToDiskAsync(workspace, _tempDir, _tempDir, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SyncAllToDiskResult(new Dictionary<int, string> { [15] = taskMdFile }, baseline));

        var outcome = await sut.SyncPreStageMarkdownAsync(
            workspace,
            task,
            pipelineContext,
            stage,
            _tempDir,
            _tempDir,
            isResumedRun: true);

        Assert.True(outcome.ModifiedDuringPreResumeSync);
        Assert.Same(baseline, outcome.BaselineSnapshot);
        Assert.Equal("docs/tasks/0015-task.md", pipelineContext.DataBag.Get<string>(PipelineDataBagKeys.TaskMarkdownPath));
        Assert.Equal("New Title", task.Title);
        Assert.Equal("New Desc", task.Description);
        Assert.Equal(1, task.Priority);
        Assert.Equal(2, task.Version);
    }

    [Fact]
    public async Task SyncPreStageMarkdownAsync_WhenUnexpectedExceptionOccurs_WrapsInTaskMarkdownSyncException()
    {
        var sut = CreateSut();
        var workspace = new Workspace { Id = 1, RepositoryPath = _tempDir };
        var task = new ForgeTask { Id = 16, WorkspaceId = 1 };
        var pipelineContext = new PipelineContext(task, workspace);
        var stage = TestWorkflowPipelines.CreateReviewStage();

        _markdownSyncMock.Setup(m => m.SyncAllTasksToDiskAsync(workspace, _tempDir, _tempDir, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Disk full"));

        var ex = await Assert.ThrowsAsync<TaskMarkdownSyncException>(() =>
            sut.SyncPreStageMarkdownAsync(workspace, task, pipelineContext, stage, _tempDir, _tempDir, isResumedRun: false));
        Assert.Equal(16, ex.TaskId);
    }

    [Fact]
    public async Task SyncAllTasksFromDiskAsync_DelegatesToMarkdownSyncService()
    {
        var sut = CreateSut();
        var workspace = new Workspace { Id = 1, RepositoryPath = _tempDir };
        var expectedTasks = new List<ForgeTask> { new() { Id = 1, WorkspaceId = 1 } };
        _markdownSyncMock.Setup(m => m.SyncAllTasksFromDiskAsync(
                workspace,
                _tempDir,
                _tempDir,
                null,
                AuditActors.System,
                "sync",
                1,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedTasks);

        var actual = await sut.SyncAllTasksFromDiskAsync(workspace, _tempDir, _tempDir, null, AuditActors.System, "sync", 1);
        Assert.Same(expectedTasks, actual);
    }

    [Fact]
    public async Task GitAndMirrorPassthroughMethods_DelegateToUnderlyingServices()
    {
        var sut = CreateSut();
        var workspace = new Workspace { Id = 1, RepositoryPath = _tempDir, Ext4MirrorPath = "/mirror/path" };

        _gitManagerMock.Setup(g => g.CreateWipCheckpointAsync(workspace, 10, "impl", "msg", It.IsAny<CancellationToken>()))
            .ReturnsAsync("wip-sha");
        _gitManagerMock.Setup(g => g.SyncFromBaseBranchAsync(workspace, 10, "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MergeSyncResult(true, false, [], "merge-sha"));
        _gitManagerMock.Setup(g => g.CheckMergeableAsync(workspace, 10, "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MergeabilityCheckResult(true, [], "b", "f"));
        _gitManagerMock.Setup(g => g.GetBranchShaAsync(workspace, "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync("branch-sha");
        _gitManagerMock.Setup(g => g.SquashAndMergeAsync(workspace, 10, "main", "commit msg", It.IsAny<CancellationToken>()))
            .ReturnsAsync("squash-sha");
        _mirrorSyncMock.Setup(m => m.PushBaseBranchToHostAsync("/mirror/path", "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitSyncResult(true, false, "push", null, false));

        Assert.Equal("wip-sha", await sut.CreateWipCheckpointAsync(workspace, 10, "impl", "msg"));
        Assert.True((await sut.SyncFromBaseBranchAsync(workspace, 10, "main")).Success);
        await sut.AbortMergeAsync(workspace, 10);
        _gitManagerMock.Verify(g => g.AbortMergeAsync(workspace, 10, It.IsAny<CancellationToken>()), Times.Once);

        Assert.True((await sut.CheckMergeableAsync(workspace, 10, "main"))!.IsMergeable);
        await sut.PullMirrorFromHostLockedAsync(workspace, "main");
        _mirrorSyncMock.Verify(m => m.PullFromHostAsync("/mirror/path", "main", It.IsAny<CancellationToken>()), Times.Once);

        await sut.PullMirrorFromHostUnlockedAsync("/mirror/path", "develop");
        _mirrorSyncMock.Verify(m => m.PullFromHostAsync("/mirror/path", "develop", It.IsAny<CancellationToken>()), Times.Once);

        Assert.Equal("branch-sha", await sut.GetBranchShaAsync(workspace, "main"));
        Assert.Equal("squash-sha", await sut.SquashAndMergeAsync(workspace, 10, "main", "commit msg"));

        await sut.ResetCanonicalBranchAsync(workspace, "main", "target-sha");
        _gitManagerMock.Verify(g => g.ResetCanonicalBranchAsync(workspace, "main", "target-sha", It.IsAny<CancellationToken>()), Times.Once);

        Assert.True((await sut.PushBaseBranchToHostAsync("/mirror/path", "main")).Success);

        await sut.ResetTaskWorkspaceToBaseAsync(workspace, 10, "main");
        _envProviderMock.Verify(e => e.CleanupTaskContainerAsync(workspace, 10, It.IsAny<CancellationToken>()), Times.Once);
        _gitManagerMock.Verify(g => g.ResetTaskBranchToBaseAsync(workspace, 10, "main", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MaterializeConflictMarkersForImplementationAsync_UpdatesOrClearsConflictKeysBasedOnSyncResult()
    {
        var sut = CreateSut();
        var workspace = new Workspace { Id = 1, RepositoryPath = _tempDir };
        var task = new ForgeTask { Id = 20, WorkspaceId = 1, BaseBranch = "main" };
        var pipelineContext = new PipelineContext(task, workspace);
        pipelineContext.DataBag.Set(PipelineDataBagKeys.ConflictFiles, new List<string> { "a.cs" });
        pipelineContext.DataBag.Set(PipelineDataBagKeys.ConflictBaseBranch, "main");
        pipelineContext.DataBag.Set(PipelineDataBagKeys.ConflictDetectedAt, DateTime.UtcNow);

        // Case 1: Conflict still present with updated file list
        _gitManagerMock.Setup(g => g.SyncFromBaseBranchAsync(workspace, 20, "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MergeSyncResult(Success: false, HasConflict: true, ConflictedFiles: ["a.cs", "b.cs"], MergeCommitSha: null));

        await sut.MaterializeConflictMarkersForImplementationAsync(workspace, task, pipelineContext);
        Assert.Equal(["a.cs", "b.cs"], pipelineContext.DataBag.Get<List<string>>(PipelineDataBagKeys.ConflictFiles));

        // Case 2: Conflict resolved externally -> clears conflict keys
        _gitManagerMock.Setup(g => g.SyncFromBaseBranchAsync(workspace, 20, "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MergeSyncResult(Success: true, HasConflict: false, ConflictedFiles: [], MergeCommitSha: "sha-clean"));

        await sut.MaterializeConflictMarkersForImplementationAsync(workspace, task, pipelineContext);
        Assert.False(pipelineContext.DataBag.ContainsKey(PipelineDataBagKeys.ConflictFiles));
        Assert.False(pipelineContext.DataBag.ContainsKey(PipelineDataBagKeys.ConflictBaseBranch));
        Assert.False(pipelineContext.DataBag.ContainsKey(PipelineDataBagKeys.ConflictDetectedAt));
    }

    [Fact]
    public async Task PopulateReviewDiffsAsync_PopulatesDiffStatAndTrimsOrOmitsOversizedLiveDiff()
    {
        var sut = CreateSut();
        var workspace = new Workspace { Id = 1, RepositoryPath = _tempDir };
        var task = new ForgeTask { Id = 21, WorkspaceId = 1, BaseBranch = "main" };
        var pipelineContext = new PipelineContext(task, workspace);

        _gitManagerMock.Setup(g => g.GenerateDiffStatAsync(workspace, 21, "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync("1 file changed, 3 insertions(+)");
        _gitManagerMock.Setup(g => g.GenerateLiveDiffAsync(workspace, 21, "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync("  diff --git a/Foo.cs b/Foo.cs  \n");

        await sut.PopulateReviewDiffsAsync(workspace, task, pipelineContext);

        Assert.Equal("1 file changed, 3 insertions(+)", pipelineContext.DataBag.Get<string>(PipelineDataBagKeys.ReviewDiffStat));
        Assert.Equal("diff --git a/Foo.cs b/Foo.cs", pipelineContext.DataBag.Get<string>(PipelineDataBagKeys.ReviewDiff));

        // Oversized diff is removed from DataBag
        _gitManagerMock.Setup(g => g.GenerateLiveDiffAsync(workspace, 21, "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new string('x', WorkflowDefaults.MaxInlineReviewDiffChars + 10));

        await sut.PopulateReviewDiffsAsync(workspace, task, pipelineContext);
        Assert.False(pipelineContext.DataBag.ContainsKey(PipelineDataBagKeys.ReviewDiff));
    }

    [Fact]
    public async Task GenerateDiffStatSafeAsync_ReturnsDiffStatOrEmptyOnException()
    {
        var sut = CreateSut();
        var workspace = new Workspace { Id = 1, RepositoryPath = _tempDir };

        _gitManagerMock.Setup(g => g.GenerateDiffStatAsync(workspace, 22, "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync("2 files changed");
        Assert.Equal("2 files changed", await sut.GenerateDiffStatSafeAsync(workspace, 22, "main"));

        _gitManagerMock.Setup(g => g.GenerateDiffStatAsync(workspace, 22, "main", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Git error"));
        Assert.Equal(string.Empty, await sut.GenerateDiffStatSafeAsync(workspace, 22, "main"));
    }

    [Fact]
    public async Task HasImplementationChangesAsync_FallsBackToBranchDiffWhenWorktreeNotFound_AndReturnsTrueWhenDiffStatNull()
    {
        var sut = CreateSut();
        var workspace = new Workspace { Id = 1, RepositoryPath = _tempDir };
        var task = new ForgeTask { Id = 22, WorkspaceId = 1, BaseBranch = "main" };

        _gitManagerMock.Setup(g => g.GenerateLiveDiffStatAsync(workspace, 22, "main", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new WorktreeNotFoundException(22, "/missing/wt"));
        _gitManagerMock.Setup(g => g.GenerateDiffStatAsync(workspace, 22, "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync(" src/Service.cs | 10 ++++++++++\n 1 file changed, 10 insertions(+)");

        Assert.True(await sut.HasImplementationChangesAsync(workspace, task));

        _gitManagerMock.Setup(g => g.GenerateDiffStatAsync(workspace, 22, "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null!);
        Assert.True(await sut.HasImplementationChangesAsync(workspace, task));
    }

    [Fact]
    public async Task SafeCleanupAndSyncHelpers_SwallowNonCancellationExceptions()
    {
        var sut = CreateSut();
        var workspace = new Workspace { Id = 1, RepositoryPath = _tempDir, Ext4MirrorPath = "/mirror/path" };
        var task = new ForgeTask { Id = 25, WorkspaceId = 1, BaseBranch = "main" };

        _markdownSyncMock.Setup(m => m.SyncTaskToDiskAsync(task, It.IsAny<string>(), It.IsAny<IReadOnlyList<int>?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Markdown write error"));
        _mirrorSyncMock.Setup(m => m.PushToHostAsync("/mirror/path", 25, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Mirror push error"));
        _markdownSyncMock.Setup(m => m.ArchiveOrCleanupTaskFileAsync(task, _tempDir, true, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Archive error"));
        _gitManagerMock.Setup(g => g.RemoveWorktreeAsync(workspace, 25, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Worktree busy"));
        _envProviderMock.Setup(e => e.CleanupTaskContainerAsync(workspace, 25, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Docker error"));

        await sut.SyncTaskToWorktreeSafeAsync(task, _tempDir);
        await sut.SyncTaskMarkdownAndMirrorAsync(task, workspace);
        await sut.ArchiveTaskMarkdownSafeAsync(workspace, task);
        await sut.RemoveWorktreeSafeAsync(workspace, 25, "test");
        await sut.CleanupTaskContainerSafeAsync(workspace, 25, "test");
    }

    [Fact]
    public async Task SafeCleanupAndSyncHelpers_WhenCancelled_PropagateOperationCanceledException()
    {
        var sut = CreateSut();
        var workspace = new Workspace { Id = 1, RepositoryPath = _tempDir, Ext4MirrorPath = "/mirror/path" };
        var task = new ForgeTask { Id = 26, WorkspaceId = 1, BaseBranch = "main" };
        var pipelineContext = new PipelineContext(task, workspace);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        _markdownSyncMock.Setup(m => m.SyncTaskToDiskAsync(task, It.IsAny<string>(), It.IsAny<IReadOnlyList<int>?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cts.Token));
        _markdownSyncMock.Setup(m => m.ArchiveOrCleanupTaskFileAsync(task, _tempDir, true, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cts.Token));
        _gitManagerMock.Setup(g => g.GenerateDiffStatAsync(workspace, 26, "main", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.SyncTaskToWorktreeSafeAsync(task, _tempDir, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.SyncMarkdownTaskSafeAsync(workspace, task, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.SyncTaskMarkdownAndMirrorAsync(task, workspace, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.ArchiveTaskMarkdownSafeAsync(workspace, task, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.PopulateReviewDiffsAsync(workspace, task, pipelineContext, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.GenerateDiffStatSafeAsync(workspace, 26, "main", cts.Token));
    }
}
