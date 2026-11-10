using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Services;
using NightCodeForge.Web.Services;
using Xunit;

namespace NightCodeForge.Web.Tests;

public class TaskDiskSyncCoordinatorStartupTests : IDisposable
{
    private readonly string _tempDir;

    public TaskDiskSyncCoordinatorStartupTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ncf-startup-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch (Exception)
        {
            // Justification: Best-effort cleanup of temporary test directory during test disposal.
        }
    }

    [Fact]
    public async Task SynchronizeWorkspacesOnStartupAsync_RunsInParallelWithIsolatedScopes_AndCatchesPerWorkspaceExceptions()
    {
        var ws1Dir = Path.Combine(_tempDir, "ws1");
        var ws2Dir = Path.Combine(_tempDir, "ws2");
        var ws3Dir = Path.Combine(_tempDir, "ws3");
        var missingDir = Path.Combine(_tempDir, "non-existent");
        Directory.CreateDirectory(ws1Dir);
        Directory.CreateDirectory(ws2Dir);
        Directory.CreateDirectory(ws3Dir);

        var ws1 = new Workspace { Id = 1, Name = "Workspace 1", RepositoryPath = ws1Dir };
        var ws2 = new Workspace { Id = 2, Name = "Workspace 2", RepositoryPath = ws2Dir };
        var ws3 = new Workspace { Id = 3, Name = "Workspace 3 (Failing)", RepositoryPath = ws3Dir };
        var ws4Missing = new Workspace { Id = 4, Name = "Workspace 4 (Missing Path)", RepositoryPath = missingDir };
        var workspaces = new List<Workspace> { ws1, ws2, ws3, ws4Missing };

        int concurrentCallers = 0;
        int maxConcurrentCallers = 0;
        var completedWorkspaces = new List<int>();
        var resolvedCoordinatorInstances = new HashSet<ITaskDiskSyncCoordinator>(ReferenceEqualityComparer.Instance);
        using var allEnteredBarrier = new SemaphoreSlim(0, 3);
        using var releaseGate = new SemaphoreSlim(0, 3);

        var services = new ServiceCollection();
        services.AddScoped<ITaskDiskSyncCoordinator>(_ =>
        {
            var scopedMock = new Mock<ITaskDiskSyncCoordinator>();
            scopedMock
                .Setup(c => c.SyncWorkspaceDocsTasksAsync(It.IsAny<Workspace>(), It.IsAny<CancellationToken>()))
                .Returns<Workspace, CancellationToken>(async (ws, ct) =>
                {
                    lock (completedWorkspaces)
                    {
                        resolvedCoordinatorInstances.Add(scopedMock.Object);
                    }

                    var current = Interlocked.Increment(ref concurrentCallers);
                    lock (completedWorkspaces)
                    {
                        if (current > maxConcurrentCallers) maxConcurrentCallers = current;
                    }

                    allEnteredBarrier.Release();
                    await releaseGate.WaitAsync(TimeSpan.FromSeconds(5), ct);

                    Interlocked.Decrement(ref concurrentCallers);

                    if (ws.Id == 3)
                    {
                        throw new InvalidOperationException("Simulated disk error on workspace 3");
                    }

                    lock (completedWorkspaces)
                    {
                        completedWorkspaces.Add(ws.Id);
                    }

                    return new TaskDiskSyncResult(1, 1, Array.Empty<ForgeTask>(), new Dictionary<int, string>());
                });
            return scopedMock.Object;
        });

        using var serviceProvider = services.BuildServiceProvider();
        var mockMarkdownSync = new Mock<ITaskMarkdownSyncService>();
        var mockLogger = new Mock<ILogger>();

        var syncTask = TaskDiskSyncCoordinator.SynchronizeWorkspacesOnStartupAsync(
            workspaces,
            serviceProvider,
            mockMarkdownSync.Object,
            mockLogger.Object);

        // Wait until all 3 existing workspaces have entered SyncWorkspaceDocsTasksAsync concurrently
        for (int i = 0; i < 3; i++)
        {
            Assert.True(await allEnteredBarrier.WaitAsync(TimeSpan.FromSeconds(5)), "Expected all 3 valid workspaces to enter sync concurrently.");
        }

        releaseGate.Release(3);
        await syncTask;

        // Assert true concurrency across the 3 valid workspaces and distinct scoped instances per workspace
        Assert.Equal(3, maxConcurrentCallers);
        Assert.Equal(3, resolvedCoordinatorInstances.Count);

        // Assert watchers were started only for existing directories
        mockMarkdownSync.Verify(m => m.StartWatching(1, ws1Dir), Times.Once);
        mockMarkdownSync.Verify(m => m.StartWatching(2, ws2Dir), Times.Once);
        mockMarkdownSync.Verify(m => m.StartWatching(3, ws3Dir), Times.Once);
        mockMarkdownSync.Verify(m => m.StartWatching(4, It.IsAny<string>()), Times.Never);

        // Assert successful workspaces completed
        Assert.Contains(1, completedWorkspaces);
        Assert.Contains(2, completedWorkspaces);
        Assert.DoesNotContain(3, completedWorkspaces);
        Assert.DoesNotContain(4, completedWorkspaces);

        // Assert failing workspace error was isolated and logged as Warning
        mockLogger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("workspace #3", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<InvalidOperationException>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }
}

