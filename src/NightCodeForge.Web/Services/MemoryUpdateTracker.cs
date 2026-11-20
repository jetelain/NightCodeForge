using NightCodeForge.Core.Memory;

namespace NightCodeForge.Web.Services;

/// <summary>A memory update pushed by the hub (already parsed for UI consumption).</summary>
public sealed record MemoryUpdateNotice(
    int WorkspaceId,
    int? TaskId,
    string StageId,
    AgentMemoryScope Scope,
    string RelativePath,
    AgentMemoryChangeKind? ChangeKind,
    string Summary);

/// <summary>
/// Scoped tracker of live memory updates: counts updates per workspace (for the Ideation header indicator)
/// and republishes them to the Workspace Memory drawer so that it can refresh its list.
/// </summary>
public class MemoryUpdateTracker : IDisposable
{
    private readonly OrchestratorHubClientConnection _hubConnection;
    private readonly Dictionary<int, int> _counts = new();
    private readonly object _lock = new();
    private bool _disposed;

    public MemoryUpdateTracker(OrchestratorHubClientConnection hubConnection)
    {
        _hubConnection = hubConnection ?? throw new ArgumentNullException(nameof(hubConnection));
        _hubConnection.OnMemoryUpdatedReceived += HandleMemoryUpdated;
    }

    public event Action<MemoryUpdateNotice>? OnMemoryUpdated;

    public int GetCount(int workspaceId)
    {
        lock (_lock)
        {
            return _counts.GetValueOrDefault(workspaceId);
        }
    }

    public void Reset(int workspaceId)
    {
        lock (_lock)
        {
            _counts.Remove(workspaceId);
        }
    }

    private void HandleMemoryUpdated(int workspaceId, int? taskId, string stageId, string memoryScope, string relativePath, string changeKind, string summary)
    {
        if (_disposed)
        {
            return;
        }

        lock (_lock)
        {
            _counts[workspaceId] = _counts.GetValueOrDefault(workspaceId) + 1;
        }

        var scope = MemoryPresentationHelper.ParseScope(memoryScope);
        var notice = new MemoryUpdateNotice(
            workspaceId,
            taskId,
            stageId,
            scope,
            scope == AgentMemoryScope.None ? relativePath : MemoryPresentationHelper.ToDrawerRelativePath(scope, relativePath),
            MemoryPresentationHelper.ParseChangeKind(changeKind),
            summary);
        OnMemoryUpdated?.Invoke(notice);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _hubConnection.OnMemoryUpdatedReceived -= HandleMemoryUpdated;
    }
}
