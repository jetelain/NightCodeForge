using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Services;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class TaskDiskSyncCoordinatorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly Mock<ITaskMarkdownSyncService> _mockMarkdownSync;
    private readonly TaskDiskSyncCoordinator _coordinator;

    public TaskDiskSyncCoordinatorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ncf-sync-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _mockMarkdownSync = new Mock<ITaskMarkdownSyncService>();
        _coordinator = new TaskDiskSyncCoordinator(
            _mockMarkdownSync.Object,
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
}
