using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Dependencies;

/// <summary>
/// Data abstraction providing issues and dependency relationships for DAG operations.
/// </summary>
public interface IIssueDependencyStore
{
    Task<IReadOnlyList<(int IssueId, int DependsOnIssueId)>> GetDependenciesForWorkspaceAsync(int workspaceId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<int>> GetPrerequisiteIssueIdsAsync(int issueId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<int>> GetDependentIssueIdsAsync(int prerequisiteIssueId, CancellationToken cancellationToken = default);
    Task<IssueOverallStatus?> GetIssueStatusAsync(int issueId, CancellationToken cancellationToken = default);
    Task UpdateIssueStatusAsync(int issueId, IssueOverallStatus newStatus, CancellationToken cancellationToken = default);
    Task<bool> IsIssueWaitingForHumanAsync(int issueId, CancellationToken cancellationToken = default);
    Task SetWaitingForHumanAsync(int issueId, bool isWaiting, CancellationToken cancellationToken = default);
}

/// <summary>
/// Thread-safe in-memory dependency store primarily used for unit testing and detached processing.
/// </summary>
public class InMemoryIssueDependencyStore : IIssueDependencyStore
{
    private readonly object _lock = new();
    private readonly Dictionary<int, IssueOverallStatus> _issueStatuses = new();
    private readonly Dictionary<int, int> _issueWorkspaces = new();
    private readonly Dictionary<int, bool> _waitingForHuman = new();
    private readonly List<(int WorkspaceId, int IssueId, int DependsOnIssueId)> _dependencies = new();

    public void AddIssue(int issueId, int workspaceId, IssueOverallStatus status = IssueOverallStatus.Open, bool isWaitingForHuman = false)
    {
        lock (_lock)
        {
            _issueStatuses[issueId] = status;
            _issueWorkspaces[issueId] = workspaceId;
            _waitingForHuman[issueId] = isWaitingForHuman;
        }
    }

    public void SetWaitingForHuman(int issueId, bool isWaiting)
    {
        lock (_lock)
        {
            _waitingForHuman[issueId] = isWaiting;
        }
    }

    public Task SetWaitingForHumanAsync(int issueId, bool isWaiting, CancellationToken cancellationToken = default)
    {
        SetWaitingForHuman(issueId, isWaiting);
        return Task.CompletedTask;
    }

    public void AddDependency(int workspaceId, int issueId, int dependsOnIssueId)
    {
        lock (_lock)
        {
            if (!_dependencies.Any(d => d.WorkspaceId == workspaceId && d.IssueId == issueId && d.DependsOnIssueId == dependsOnIssueId))
            {
                _dependencies.Add((workspaceId, issueId, dependsOnIssueId));
            }
        }
    }

    public void RemoveDependency(int workspaceId, int issueId, int dependsOnIssueId)
    {
        lock (_lock)
        {
            _dependencies.RemoveAll(d => d.WorkspaceId == workspaceId && d.IssueId == issueId && d.DependsOnIssueId == dependsOnIssueId);
        }
    }

    public Task<IReadOnlyList<(int IssueId, int DependsOnIssueId)>> GetDependenciesForWorkspaceAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            IReadOnlyList<(int IssueId, int DependsOnIssueId)> result = _dependencies
                .Where(d => d.WorkspaceId == workspaceId)
                .Select(d => (d.IssueId, d.DependsOnIssueId))
                .ToList();
            return Task.FromResult(result);
        }
    }

    public Task<IReadOnlyList<int>> GetPrerequisiteIssueIdsAsync(int issueId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            IReadOnlyList<int> result = _dependencies
                .Where(d => d.IssueId == issueId)
                .Select(d => d.DependsOnIssueId)
                .Distinct()
                .ToList();
            return Task.FromResult(result);
        }
    }

    public Task<IReadOnlyList<int>> GetDependentIssueIdsAsync(int prerequisiteIssueId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            IReadOnlyList<int> result = _dependencies
                .Where(d => d.DependsOnIssueId == prerequisiteIssueId)
                .Select(d => d.IssueId)
                .Distinct()
                .ToList();
            return Task.FromResult(result);
        }
    }

    public Task<IssueOverallStatus?> GetIssueStatusAsync(int issueId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_issueStatuses.TryGetValue(issueId, out var status))
            {
                return Task.FromResult<IssueOverallStatus?>(status);
            }
            return Task.FromResult<IssueOverallStatus?>(null);
        }
    }

    public Task UpdateIssueStatusAsync(int issueId, IssueOverallStatus newStatus, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _issueStatuses[issueId] = newStatus;
            return Task.CompletedTask;
        }
    }

    public Task<bool> IsIssueWaitingForHumanAsync(int issueId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            return Task.FromResult(_waitingForHuman.TryGetValue(issueId, out var val) && val);
        }
    }
}
