using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Repositories;
using NightCodeForge.Core.Services;
using NightCodeForge.Core.Tests.Fixtures;
using Xunit;

namespace NightCodeForge.Core.Tests;

/// <summary>
/// Step 11.26: sandbox containers are torn down when a task reaches a terminal state,
/// and orphaned containers are reaped by the reconciliation sweeps.
/// </summary>
public class TaskContainerLifecycleTests
{
    private readonly InMemoryTaskRepository _taskRepo = new();
    private readonly InMemoryWorkspaceRepository _workspaceRepo = new();
    private readonly InMemoryTaskDependencyStore _depStore = new();
    private readonly FakeGitWorkspaceManager _gitManager = new();
    private readonly Workspace _workspace = new()
    {
        Id = 1,
        Name = "Container Lifecycle Workspace",
        RepositoryPath = "C:/test/repo",
        DefaultBaseBranch = "main",
        ExecutionMode = ExecutionMode.ContainerSandbox
    };

    public TaskContainerLifecycleTests()
    {
        _workspaceRepo.CreateAsync(_workspace).GetAwaiter().GetResult();
    }

    private TestOrchestratorBuilder CreateBuilder() => new()
    {
        TaskRepository = _taskRepo,
        WorkspaceRepository = _workspaceRepo,
        DependencyEngine = new DependencyResolutionEngine(_depStore),
        GitWorkspaceManager = _gitManager,
        DependencyStore = _depStore
    };

    [Fact]
    public async Task CompleteTaskAndCascadeAsync_OnTerminalSuccess_InvokesContainerCleanup()
    {
        await _taskRepo.CreateAsync(new ForgeTask { Id = 301, WorkspaceId = 1, Status = TaskOverallStatus.Ready, BaseBranch = "main" });
        _depStore.AddTask(301, 1, TaskOverallStatus.Ready);
        var builder = CreateBuilder();

        await builder.Build().CompleteTaskAndCascadeAsync(301, "Merge task 301");

        builder.EnvironmentProviderMock.Verify(
            p => p.CleanupTaskContainerAsync(It.Is<Workspace>(w => w.Id == 1), 301, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RejectTaskAsync_InvokesWorktreeAndContainerCleanup()
    {
        var task = await _taskRepo.CreateWithBaselineAsync(new ForgeTask
        {
            WorkspaceId = 1,
            Title = "Task to reject",
            Status = TaskOverallStatus.Open,
            Priority = TaskPriority.Normal
        }, null, AuditActors.User, "baseline");
        _depStore.AddTask(task.Id, 1, TaskOverallStatus.Open);
        var builder = CreateBuilder();

        await builder.Build().RejectTaskAsync(task.Id, "No longer needed");

        builder.EnvironmentProviderMock.Verify(
            p => p.CleanupTaskContainerAsync(It.Is<Workspace>(w => w.Id == 1), task.Id, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task TaskContainerReaper_ReapsContainersForClosedRejectedCancelledAndMissingTasks_PreservesActiveOnes()
    {
        var closed = await _taskRepo.CreateAsync(new ForgeTask { WorkspaceId = 1, Status = TaskOverallStatus.Closed });
        var rejected = await _taskRepo.CreateAsync(new ForgeTask { WorkspaceId = 1, Status = TaskOverallStatus.Rejected });
        var cancelled = await _taskRepo.CreateAsync(new ForgeTask { WorkspaceId = 1, Status = TaskOverallStatus.Cancelled });
        var open = await _taskRepo.CreateAsync(new ForgeTask { WorkspaceId = 1, Status = TaskOverallStatus.Open });
        var ready = await _taskRepo.CreateAsync(new ForgeTask { WorkspaceId = 1, Status = TaskOverallStatus.Ready });
        const int missingTaskId = 99999;

        var provider = new Mock<IExecutionEnvironmentProvider>();
        provider.Setup(p => p.ListTaskContainerIdsAsync(It.IsAny<Workspace>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { closed.Id, rejected.Id, cancelled.Id, open.Id, ready.Id, missingTaskId });

        var reaped = await TaskContainerReaper.ReapAsync(
            provider.Object, _taskRepo, _workspace, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(4, reaped);
        provider.Verify(p => p.CleanupTaskContainerAsync(_workspace, closed.Id, It.IsAny<CancellationToken>()), Times.Once);
        provider.Verify(p => p.CleanupTaskContainerAsync(_workspace, rejected.Id, It.IsAny<CancellationToken>()), Times.Once);
        provider.Verify(p => p.CleanupTaskContainerAsync(_workspace, cancelled.Id, It.IsAny<CancellationToken>()), Times.Once);
        provider.Verify(p => p.CleanupTaskContainerAsync(_workspace, missingTaskId, It.IsAny<CancellationToken>()), Times.Once);
        provider.Verify(p => p.CleanupTaskContainerAsync(_workspace, open.Id, It.IsAny<CancellationToken>()), Times.Never);
        provider.Verify(p => p.CleanupTaskContainerAsync(_workspace, ready.Id, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StartupRecoveryService_ReapsContainersForClosedAndMissingTasks()
    {
        var closed = await _taskRepo.CreateAsync(new ForgeTask { WorkspaceId = 1, Status = TaskOverallStatus.Closed });
        var open = await _taskRepo.CreateAsync(new ForgeTask { WorkspaceId = 1, Status = TaskOverallStatus.Open });

        var provider = new Mock<IExecutionEnvironmentProvider>();
        provider.Setup(p => p.ListTaskContainerIdsAsync(It.IsAny<Workspace>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { closed.Id, open.Id, 424242 });

        var gitRunner = new Mock<IGitCliRunner>();
        gitRunner.Setup(g => g.ExecuteAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitCommandResult(0, string.Empty, string.Empty, TimeSpan.Zero));

        var mockOps = new Mock<IOperationsControlService>();
        mockOps.Setup(m => m.IsPaused).Returns(false);

        var service = new StartupRecoveryService(
            _workspaceRepo,
            _taskRepo,
            gitRunner.Object,
            _gitManager,
            new NightCodeForge.Core.Workflow.DefaultWorkflowPipelineProvider(),
            new FakeTaskJobQueue(),
            new Mock<IWorkspacePathResolver>().Object,
            new Mock<IProcessRunner>().Object,
            provider.Object,
            mockOps.Object,
            NullLogger<StartupRecoveryService>.Instance);

        await service.ReconcileWorkspacesAsync();

        provider.Verify(p => p.CleanupTaskContainerAsync(It.IsAny<Workspace>(), closed.Id, It.IsAny<CancellationToken>()), Times.Once);
        provider.Verify(p => p.CleanupTaskContainerAsync(It.IsAny<Workspace>(), 424242, It.IsAny<CancellationToken>()), Times.Once);
        provider.Verify(p => p.CleanupTaskContainerAsync(It.IsAny<Workspace>(), open.Id, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StartupRecoveryService_WhenOneWorkspaceReapFails_ContinuesReconcilingOtherWorkspaces()
    {
        var workspace2 = new Workspace
        {
            Id = 2,
            Name = "Second Workspace",
            RepositoryPath = "C:/test/repo2",
            DefaultBaseBranch = "main",
            ExecutionMode = ExecutionMode.ContainerSandbox
        };
        await _workspaceRepo.CreateAsync(workspace2);

        var closed1 = await _taskRepo.CreateAsync(new ForgeTask { WorkspaceId = 1, Status = TaskOverallStatus.Closed });
        var closed2 = await _taskRepo.CreateAsync(new ForgeTask { WorkspaceId = 2, Status = TaskOverallStatus.Closed });

        var provider = new Mock<IExecutionEnvironmentProvider>();
        provider.Setup(p => p.ListTaskContainerIdsAsync(It.Is<Workspace>(w => w.Id == 1), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Docker daemon unavailable for workspace 1"));
        provider.Setup(p => p.ListTaskContainerIdsAsync(It.Is<Workspace>(w => w.Id == 2), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { closed2.Id });

        var gitRunner = new Mock<IGitCliRunner>();
        gitRunner.Setup(g => g.ExecuteAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitCommandResult(0, string.Empty, string.Empty, TimeSpan.Zero));

        var mockOps = new Mock<IOperationsControlService>();
        mockOps.Setup(m => m.IsPaused).Returns(false);

        var service = new StartupRecoveryService(
            _workspaceRepo,
            _taskRepo,
            gitRunner.Object,
            _gitManager,
            new NightCodeForge.Core.Workflow.DefaultWorkflowPipelineProvider(),
            new FakeTaskJobQueue(),
            new Mock<IWorkspacePathResolver>().Object,
            new Mock<IProcessRunner>().Object,
            provider.Object,
            mockOps.Object,
            NullLogger<StartupRecoveryService>.Instance);

        // Does not throw even though workspace 1 fails
        await service.ReconcileWorkspacesAsync();

        provider.Verify(p => p.CleanupTaskContainerAsync(It.Is<Workspace>(w => w.Id == 2), closed2.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CompleteTaskAndCascadeAsync_WhenContainerCleanupFails_DoesNotThrowAndCompletesTask()
    {
        await _taskRepo.CreateAsync(new ForgeTask { Id = 302, WorkspaceId = 1, Status = TaskOverallStatus.Ready, BaseBranch = "main" });
        _depStore.AddTask(302, 1, TaskOverallStatus.Ready);
        var builder = CreateBuilder();
        builder.EnvironmentProviderMock
            .Setup(p => p.CleanupTaskContainerAsync(It.IsAny<Workspace>(), 302, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Docker daemon error"));

        var result = await builder.Build().CompleteTaskAndCascadeAsync(302, "Merge task 302");

        Assert.Equal(CompleteTaskStatus.Completed, result.Status);
        var updated = await _taskRepo.GetByIdAsync(302);
        Assert.NotNull(updated);
        Assert.Equal(TaskOverallStatus.Closed, updated.Status);
    }

    [Fact]
    public async Task RejectTaskAsync_WhenContainerCleanupFails_DoesNotThrowAndRejectsTask()
    {
        var task = await _taskRepo.CreateWithBaselineAsync(new ForgeTask
        {
            WorkspaceId = 1,
            Title = "Task to reject with failing docker",
            Status = TaskOverallStatus.Open,
            Priority = TaskPriority.Normal
        }, null, AuditActors.User, "baseline");
        _depStore.AddTask(task.Id, 1, TaskOverallStatus.Open);
        var builder = CreateBuilder();
        builder.EnvironmentProviderMock
            .Setup(p => p.CleanupTaskContainerAsync(It.IsAny<Workspace>(), task.Id, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Docker daemon error"));

        await builder.Build().RejectTaskAsync(task.Id, "Rejected despite container error");

        var updated = await _taskRepo.GetByIdAsync(task.Id);
        Assert.NotNull(updated);
        Assert.Equal(TaskOverallStatus.Rejected, updated.Status);
    }
}
