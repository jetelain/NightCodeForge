using NightCodeForge.Core.Domain;
using NightCodeForge.Web.Services;

namespace NightCodeForge.Web.Tests;

public class MockTaskDiskSyncCoordinator : ITaskDiskSyncCoordinator
{
    public int SyncCount { get; private set; }
    public Workspace? LastSyncedWorkspace { get; private set; }
    public TaskDiskSyncResult ResultToReturn { get; set; } = new(0, 0, Array.Empty<ForgeTask>(), new Dictionary<int, string>());
    public Exception? ExceptionToThrow { get; set; }

    public Task<TaskDiskSyncResult> SyncWorkspaceDocsTasksAsync(Workspace workspace, CancellationToken cancellationToken = default)
    {
        SyncCount++;
        LastSyncedWorkspace = workspace;
        if (ExceptionToThrow != null)
        {
            throw ExceptionToThrow;
        }
        return Task.FromResult(ResultToReturn);
    }
}
