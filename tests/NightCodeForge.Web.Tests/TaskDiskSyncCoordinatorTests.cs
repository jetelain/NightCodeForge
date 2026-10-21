using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Services;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class TaskDiskSyncCoordinatorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly Mock<ITaskMarkdownSyncService> _mockMarkdownSync;
    private readonly Mock<IWorkspacePathResolver> _mockPathResolver;
    private readonly TaskDiskSyncCoordinator _coordinator;

    public TaskDiskSyncCoordinatorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ncf-sync-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _mockMarkdownSync = new Mock<ITaskMarkdownSyncService>();
        _mockPathResolver = new Mock<IWorkspacePathResolver>();
        _coordinator = new TaskDiskSyncCoordinator(
            _mockMarkdownSync.Object,
            _mockPathResolver.Object,
            NullLogger<TaskDiskSyncCoordinator>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try
            {
                Directory.Delete(_tempDir, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    [Fact]
    public async Task SyncWorkspaceDocsTasksAsync_NullWorkspace_ThrowsArgumentNullException()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _coordinator.SyncWorkspaceDocsTasksAsync(null!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SyncWorkspaceDocsTasksAsync_InvalidRepositoryPath_ThrowsDirectoryNotFoundException(string? invalidPath)
    {
        var workspace = new Workspace
        {
            Id = 1,
            Name = "Invalid Repo",
            RepositoryPath = invalidPath!
        };

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            _coordinator.SyncWorkspaceDocsTasksAsync(workspace));
    }

    [Fact]
    public async Task SyncWorkspaceDocsTasksAsync_NonExistentDirectory_ThrowsDirectoryNotFoundException()
    {
        var workspace = new Workspace
        {
            Id = 2,
            Name = "Missing Dir Repo",
            RepositoryPath = Path.Combine(_tempDir, "does-not-exist")
        };

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            _coordinator.SyncWorkspaceDocsTasksAsync(workspace));
    }

    [Fact]
    public async Task SyncWorkspaceDocsTasksAsync_ValidWorkspace_ExecutesBidirectionalSyncInOrder()
    {
        var workspace = new Workspace
        {
            Id = 3,
            Name = "Sync Repo",
            RepositoryPath = _tempDir
        };

        var refreshedTasks = new List<ForgeTask>
        {
            new() { Id = 10, WorkspaceId = 3, Title = "Disk Task 1", Status = TaskOverallStatus.Suggested },
            new() { Id = 20, WorkspaceId = 3, Title = "Disk Task 2", Status = TaskOverallStatus.Open }
        };

        var writtenPaths = new Dictionary<int, string>
        {
            { 10, Path.Combine(_tempDir, "docs", "tasks", "task-10.md") },
            { 20, Path.Combine(_tempDir, "docs", "tasks", "task-20.md") }
        };

        var toDiskResult = new SyncAllToDiskResult(
            writtenPaths,
            new Dictionary<int, TaskBaselineEntry>());

        var callOrder = new List<string>();

        _mockMarkdownSync
            .Setup(s => s.SyncAllTasksFromDiskAsync(
                workspace,
                _tempDir,
                _tempDir,
                null,
                AuditActors.User,
                TaskSyncConstants.DiskModificationsSync,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(refreshedTasks)
            .Callback(() => callOrder.Add("FromDisk"));

        _mockMarkdownSync
            .Setup(s => s.SyncAllTasksToDiskAsync(
                workspace,
                _tempDir,
                _tempDir,
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(toDiskResult)
            .Callback(() => callOrder.Add("ToDisk"));

        var result = await _coordinator.SyncWorkspaceDocsTasksAsync(workspace);

        Assert.Equal(2, callOrder.Count);
        Assert.Equal("FromDisk", callOrder[0]);
        Assert.Equal("ToDisk", callOrder[1]);

        Assert.Equal(2, result.TasksIngestedOrUpdatedFromDisk);
        Assert.Equal(2, result.TasksWrittenToDisk);
        Assert.Equal(refreshedTasks, result.RefreshedTasks);
        Assert.Equal(writtenPaths, result.WrittenPaths);
    }

    [Fact]
    public async Task SyncWorkspaceDocsTasksAsync_WhenMarkdownSyncThrows_PropagatesException()
    {
        var workspace = new Workspace
        {
            Id = 4,
            Name = "Error Repo",
            RepositoryPath = _tempDir
        };

        _mockMarkdownSync
            .Setup(s => s.SyncAllTasksFromDiskAsync(
                workspace,
                _tempDir,
                _tempDir,
                null,
                AuditActors.User,
                TaskSyncConstants.DiskModificationsSync,
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TaskMarkdownSyncException("Syntax error in task-10.md", "task-10.md"));

        var ex = await Assert.ThrowsAsync<TaskMarkdownSyncException>(() =>
            _coordinator.SyncWorkspaceDocsTasksAsync(workspace));

        Assert.Contains("Syntax error in task-10.md", ex.Message);
    }

    [Fact]
    public async Task SyncWorkspaceDocsTasksAsync_WithExistingCloneDirectory_MirrorsToClone_AndDoesNotIngestFromClone()
    {
        var cloneDir = Path.Combine(_tempDir, "clone");
        Directory.CreateDirectory(cloneDir);

        var workspace = new Workspace
        {
            Id = 5,
            Name = "Clone WS",
            RepositoryPath = _tempDir,
            Ext4MirrorPath = cloneDir
        };

        _mockPathResolver
            .Setup(r => r.ToHostPath(cloneDir))
            .Returns(cloneDir);

        var refreshedTasks = new List<ForgeTask>
        {
            new() { Id = 50, WorkspaceId = 5, Title = "Host Edited Task", Status = TaskOverallStatus.Open }
        };

        var writtenPaths = new Dictionary<int, string>
        {
            { 50, Path.Combine(_tempDir, "docs", "tasks", "task-50.md") }
        };

        _mockMarkdownSync
            .Setup(s => s.SyncAllTasksFromDiskAsync(
                workspace,
                _tempDir,
                _tempDir,
                null,
                AuditActors.User,
                TaskSyncConstants.DiskModificationsSync,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(refreshedTasks);

        _mockMarkdownSync
            .Setup(s => s.SyncAllTasksToDiskAsync(
                workspace,
                _tempDir,
                _tempDir,
                cloneDir,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SyncAllToDiskResult(writtenPaths, new Dictionary<int, TaskBaselineEntry>()));

        var result = await _coordinator.SyncWorkspaceDocsTasksAsync(workspace);

        // Verify SyncAllTasksFromDiskAsync was called ONLY for the host repo (_tempDir), never for cloneDir
        _mockMarkdownSync.Verify(s => s.SyncAllTasksFromDiskAsync(
            workspace,
            _tempDir,
            _tempDir,
            null,
            AuditActors.User,
            TaskSyncConstants.DiskModificationsSync,
            It.IsAny<CancellationToken>()), Times.Once);

        _mockMarkdownSync.Verify(s => s.SyncAllTasksFromDiskAsync(
            workspace,
            cloneDir,
            It.IsAny<string?>(),
            It.IsAny<IReadOnlyDictionary<int, TaskBaselineEntry>?>(),
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);

        // Verify SyncAllTasksToDiskAsync was called with cloneDir
        _mockMarkdownSync.Verify(s => s.SyncAllTasksToDiskAsync(
            workspace,
            _tempDir,
            _tempDir,
            cloneDir,
            It.IsAny<CancellationToken>()), Times.Once);

        Assert.Equal(1, result.TasksIngestedOrUpdatedFromDisk);
        Assert.Equal(1, result.TasksWrittenToDisk);
        Assert.Single(result.RefreshedTasks);
        Assert.Equal("Host Edited Task", result.RefreshedTasks[0].Title);
    }
}
