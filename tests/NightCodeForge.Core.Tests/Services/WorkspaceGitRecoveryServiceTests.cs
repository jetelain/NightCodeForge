using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;
using Xunit;

namespace NightCodeForge.Core.Tests.Services;

public class WorkspaceGitRecoveryServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FakeGitCliRunner _gitCliRunner = new();
    private readonly FakeGitWorkspaceManager _gitWorkspaceManager = new();
    private readonly Mock<IWorkspacePathResolver> _pathResolverMock = new();
    private readonly Mock<IProcessRunner> _processRunnerMock = new();
    private readonly InMemoryTaskRepository _taskRepo = new();

    public WorkspaceGitRecoveryServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ncf_git_recovery_tests_" + Guid.NewGuid().ToString("N"));
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
            // Ignore cleanup errors
        }
    }

    private WorkspaceGitRecoveryService CreateSut() => new(
        _gitCliRunner,
        _gitWorkspaceManager,
        _pathResolverMock.Object,
        _processRunnerMock.Object,
        _taskRepo,
        NullLogger<WorkspaceGitRecoveryService>.Instance);

    [Fact]
    public void Constructor_WhenAnyDependencyIsNull_ThrowsArgumentNullException()
    {
        var logger = NullLogger<WorkspaceGitRecoveryService>.Instance;
        Assert.Throws<ArgumentNullException>(() => new WorkspaceGitRecoveryService(null!, _gitWorkspaceManager, _pathResolverMock.Object, _processRunnerMock.Object, _taskRepo, logger));
        Assert.Throws<ArgumentNullException>(() => new WorkspaceGitRecoveryService(_gitCliRunner, null!, _pathResolverMock.Object, _processRunnerMock.Object, _taskRepo, logger));
        Assert.Throws<ArgumentNullException>(() => new WorkspaceGitRecoveryService(_gitCliRunner, _gitWorkspaceManager, null!, _processRunnerMock.Object, _taskRepo, logger));
        Assert.Throws<ArgumentNullException>(() => new WorkspaceGitRecoveryService(_gitCliRunner, _gitWorkspaceManager, _pathResolverMock.Object, null!, _taskRepo, logger));
        Assert.Throws<ArgumentNullException>(() => new WorkspaceGitRecoveryService(_gitCliRunner, _gitWorkspaceManager, _pathResolverMock.Object, _processRunnerMock.Object, null!, logger));
        Assert.Throws<ArgumentNullException>(() => new WorkspaceGitRecoveryService(_gitCliRunner, _gitWorkspaceManager, _pathResolverMock.Object, _processRunnerMock.Object, _taskRepo, null!));
    }

    [Fact]
    public void PurgeStaleGitLocks_DeletesDanglingIndexAndRefsLocks()
    {
        var gitDir = Path.Combine(_tempDir, ".git");
        var refsHeadsDir = Path.Combine(gitDir, "refs", "heads");
        Directory.CreateDirectory(refsHeadsDir);

        var indexLock = Path.Combine(gitDir, "index.lock");
        var branchLock = Path.Combine(refsHeadsDir, "main.lock");
        File.WriteAllText(indexLock, "lock");
        File.WriteAllText(branchLock, "lock");

        var sut = CreateSut();
        sut.PurgeStaleGitLocks(_tempDir, workspaceId: 1);

        Assert.False(File.Exists(indexLock));
        Assert.False(File.Exists(branchLock));
    }

    [Fact]
    public async Task AlignMirrorWithHostOriginAsync_WhenFastForwardFails_ExecutesHardResetToHostOrigin()
    {
        var sut = CreateSut();
        var workspace = new Workspace
        {
            Id = 1,
            Name = "Mirror WS",
            Ext4MirrorPath = "/home/user/.ncf/wks/1/clone",
            DefaultBaseBranch = "develop"
        };

        _gitCliRunner.Handler = (_, args) =>
        {
            if (args.Contains("merge --ff-only host-origin/develop"))
            {
                return new GitCommandResult(1, string.Empty, "fatal: Not possible to fast-forward, aborting.", TimeSpan.Zero);
            }
            return new GitCommandResult(0, string.Empty, string.Empty, TimeSpan.Zero);
        };

        await sut.AlignMirrorWithHostOriginAsync(workspace);

        Assert.Contains(_gitCliRunner.ExecutedCommands, c => c.Arguments == "fetch host-origin develop");
        Assert.Contains(_gitCliRunner.ExecutedCommands, c => c.Arguments == "checkout develop");
        Assert.Contains(_gitCliRunner.ExecutedCommands, c => c.Arguments == "merge --ff-only host-origin/develop");
        Assert.Contains(_gitCliRunner.ExecutedCommands, c => c.Arguments == "reset --hard host-origin/develop");
    }

    [Fact]
    public async Task AlignMirrorWithHostOriginAsync_WhenMirrorPathEmpty_DoesNothing()
    {
        var sut = CreateSut();
        var workspace = new Workspace { Id = 1, Name = "No Mirror", Ext4MirrorPath = null };

        await sut.AlignMirrorWithHostOriginAsync(workspace);
        Assert.Empty(_gitCliRunner.ExecutedCommands);
    }

    [Fact]
    public async Task GetWorktreesToRescueAsync_SkipsPrimaryCloneAndIncludesRegisteredAndActiveTaskWorktrees()
    {
        var sut = CreateSut();
        var workspace = new Workspace
        {
            Id = 3,
            Name = "Worktrees WS",
            RepositoryPath = _tempDir,
            Ext4MirrorPath = "/wks/3/clone"
        };

        await _taskRepo.CreateAsync(new ForgeTask { Id = 101, WorkspaceId = 3, Status = TaskOverallStatus.Open });
        await _taskRepo.CreateAsync(new ForgeTask { Id = 102, WorkspaceId = 3, Status = TaskOverallStatus.Closed });

        _pathResolverMock.Setup(p => p.GetAgentWorktreePath(3, 101)).Returns("/wks/3/task-101");

        _gitCliRunner.Handler = (_, args) =>
        {
            if (args.Contains("worktree list --porcelain"))
            {
                var porcelain = """
                    worktree /wks/3/clone
                    HEAD abcdef123456
                    branch refs/heads/main

                    worktree /wks/3/task-99
                    HEAD 123456abcdef
                    branch refs/heads/feature/task-99
                    """;
                return new GitCommandResult(0, porcelain, string.Empty, TimeSpan.Zero);
            }
            return new GitCommandResult(0, string.Empty, string.Empty, TimeSpan.Zero);
        };

        var worktrees = await sut.GetWorktreesToRescueAsync(workspace);

        Assert.DoesNotContain("/wks/3/clone", worktrees);
        Assert.Contains("/wks/3/task-99", worktrees);
        Assert.Contains("/wks/3/task-101", worktrees);
    }

    [Fact]
    public async Task GetWorktreesToRescueAsync_WithoutMirrorOrRegisteredWorktrees_FallsBackToRepositoryPath()
    {
        var sut = CreateSut();
        var workspace = new Workspace { Id = 4, Name = "Single Repo", RepositoryPath = _tempDir };

        var worktrees = await sut.GetWorktreesToRescueAsync(workspace);
        Assert.Equal([_tempDir], worktrees);
    }

    [Fact]
    public async Task RescueUncommittedChangesAsync_WhenPorcelainStatusDirty_StagesAllAndCommitsWipRecovery()
    {
        var sut = CreateSut();
        _gitCliRunner.Handler = (_, args) =>
        {
            if (args == "status --porcelain")
            {
                return new GitCommandResult(0, " M src/Dirty.cs\n", string.Empty, TimeSpan.Zero);
            }
            return new GitCommandResult(0, string.Empty, string.Empty, TimeSpan.Zero);
        };

        await sut.RescueUncommittedChangesAsync(_tempDir);

        Assert.Contains(_gitCliRunner.ExecutedCommands, c => c.WorkingDirectory == _tempDir && c.Arguments == "add -A");
        Assert.Contains(_gitCliRunner.ExecutedCommands, c => c.WorkingDirectory == _tempDir && c.Arguments.Contains("WIP-RECOVERY"));
    }

    [Fact]
    public async Task RescueUncommittedChangesAsync_WhenWorkingTreeClean_DoesNotStageOrCommit()
    {
        var sut = CreateSut();
        _gitCliRunner.Handler = (_, _) => new GitCommandResult(0, string.Empty, string.Empty, TimeSpan.Zero);

        await sut.RescueUncommittedChangesAsync(_tempDir);

        Assert.Single(_gitCliRunner.ExecutedCommands);
        Assert.Equal("status --porcelain", _gitCliRunner.ExecutedCommands[0].Arguments);
    }

    [Fact]
    public async Task RecoverWorkspaceAsync_NormalizesBothRescuedWorktreeTaskIdsAndActiveDatabaseTasks()
    {
        var sut = CreateSut();
        var workspace = new Workspace
        {
            Id = 5,
            Name = "Full Recovery WS",
            RepositoryPath = _tempDir,
            Ext4MirrorPath = "/wks/5/clone"
        };

        await _taskRepo.CreateAsync(new ForgeTask { Id = 202, WorkspaceId = 5, Status = TaskOverallStatus.Ready });

        _gitCliRunner.Handler = (_, args) =>
        {
            if (args.Contains("worktree list --porcelain"))
            {
                var porcelain = """
                    worktree /wks/5/clone
                    HEAD abcdef

                    worktree /wks/5/task-201
                    HEAD 123456
                    """;
                return new GitCommandResult(0, porcelain, string.Empty, TimeSpan.Zero);
            }
            return new GitCommandResult(0, string.Empty, string.Empty, TimeSpan.Zero);
        };

        await sut.RecoverWorkspaceAsync(workspace);

        Assert.Contains(201, _gitWorkspaceManager.NormalizedTaskIds);
        Assert.Contains(202, _gitWorkspaceManager.NormalizedTaskIds);
    }

    [Fact]
    public void VerifyWslAvailability_HandlesSuccessFailureAndTimeoutGracefully()
    {
        var sut = CreateSut();

        _processRunnerMock.Setup(p => p.Run(It.IsAny<ProcessStartSpec>()))
            .Returns(new ProcessExecutionResult(0, "WSL2 OK", string.Empty));
        Assert.Null(Record.Exception(() => sut.VerifyWslAvailability()));

        _processRunnerMock.Setup(p => p.Run(It.IsAny<ProcessStartSpec>()))
            .Returns(new ProcessExecutionResult(1, string.Empty, "WSL not installed"));
        Assert.Null(Record.Exception(() => sut.VerifyWslAvailability()));

        _processRunnerMock.Setup(p => p.Run(It.IsAny<ProcessStartSpec>()))
            .Throws(new TimeoutException("WSL timed out"));
        Assert.Null(Record.Exception(() => sut.VerifyWslAvailability()));
    }

    [Fact]
    public async Task RecoveryMethods_WhenCancelled_PropagateOperationCanceledException()
    {
        var sut = CreateSut();
        var workspace = new Workspace
        {
            Id = 6,
            Name = "Cancel WS",
            RepositoryPath = _tempDir,
            Ext4MirrorPath = "/wks/6/clone"
        };

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        _gitCliRunner.Handler = (_, _) => throw new OperationCanceledException(cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.AlignMirrorWithHostOriginAsync(workspace, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.GetWorktreesToRescueAsync(workspace, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.RescueUncommittedChangesAsync(_tempDir, cts.Token));
    }
}
