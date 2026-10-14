using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Dependencies;

/// <summary>
/// Data abstraction providing tasks and dependency relationships for DAG operations.
/// </summary>
public interface ITaskDependencyStore
{
    Task<IReadOnlyList<(int TaskId, int DependsOnTaskId)>> GetDependenciesForWorkspaceAsync(int workspaceId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<int>> GetPrerequisiteTaskIdsAsync(int taskId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<int>> GetDependentTaskIdsAsync(int prerequisiteTaskId, CancellationToken cancellationToken = default);
    Task<TaskOverallStatus?> GetTaskStatusAsync(int taskId, CancellationToken cancellationToken = default);
    Task UpdateTaskStatusAsync(int taskId, TaskOverallStatus newStatus, CancellationToken cancellationToken = default);
    Task<bool> IsTaskWaitingForHumanAsync(int taskId, CancellationToken cancellationToken = default);
    Task SetWaitingForHumanAsync(int taskId, bool isWaiting, CancellationToken cancellationToken = default);
}

/// <summary>
/// Thread-safe in-memory dependency store primarily used for unit testing and detached processing.
/// </summary>
public class InMemoryTaskDependencyStore : ITaskDependencyStore
{
    private readonly object _lock = new();
    private readonly Dictionary<int, TaskOverallStatus> _taskStatuses = new();
    private readonly Dictionary<int, int> _taskWorkspaces = new();
    private readonly Dictionary<int, bool> _waitingForHuman = new();
    private readonly List<(int WorkspaceId, int TaskId, int DependsOnTaskId)> _dependencies = new();

    public void AddTask(int taskId, int workspaceId, TaskOverallStatus status = TaskOverallStatus.Open, bool isWaitingForHuman = false)
    {
        lock (_lock)
        {
            _taskStatuses[taskId] = status;
            _taskWorkspaces[taskId] = workspaceId;
            _waitingForHuman[taskId] = isWaitingForHuman;
        }
    }

    public void SetWaitingForHuman(int taskId, bool isWaiting)
    {
        lock (_lock)
        {
            _waitingForHuman[taskId] = isWaiting;
        }
    }

    public Task SetWaitingForHumanAsync(int taskId, bool isWaiting, CancellationToken cancellationToken = default)
    {
        SetWaitingForHuman(taskId, isWaiting);
        return Task.CompletedTask;
    }

    public void AddDependency(int workspaceId, int taskId, int dependsOnTaskId)
    {
        lock (_lock)
        {
            if (!_dependencies.Any(d => d.WorkspaceId == workspaceId && d.TaskId == taskId && d.DependsOnTaskId == dependsOnTaskId))
            {
                _dependencies.Add((workspaceId, taskId, dependsOnTaskId));
            }
        }
    }

    public void RemoveDependency(int workspaceId, int taskId, int dependsOnTaskId)
    {
        lock (_lock)
        {
            _dependencies.RemoveAll(d => d.WorkspaceId == workspaceId && d.TaskId == taskId && d.DependsOnTaskId == dependsOnTaskId);
        }
    }

    public Task<IReadOnlyList<(int TaskId, int DependsOnTaskId)>> GetDependenciesForWorkspaceAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            IReadOnlyList<(int TaskId, int DependsOnTaskId)> result = _dependencies
                .Where(d => d.WorkspaceId == workspaceId)
                .Select(d => (d.TaskId, d.DependsOnTaskId))
                .ToList();
            return Task.FromResult(result);
        }
    }

    public Task<IReadOnlyList<int>> GetPrerequisiteTaskIdsAsync(int taskId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            IReadOnlyList<int> result = _dependencies
                .Where(d => d.TaskId == taskId)
                .Select(d => d.DependsOnTaskId)
                .Distinct()
                .ToList();
            return Task.FromResult(result);
        }
    }

    public Task<IReadOnlyList<int>> GetDependentTaskIdsAsync(int prerequisiteTaskId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            IReadOnlyList<int> result = _dependencies
                .Where(d => d.DependsOnTaskId == prerequisiteTaskId)
                .Select(d => d.TaskId)
                .Distinct()
                .ToList();
            return Task.FromResult(result);
        }
    }

    public Task<TaskOverallStatus?> GetTaskStatusAsync(int taskId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_taskStatuses.TryGetValue(taskId, out var status))
            {
                return Task.FromResult<TaskOverallStatus?>(status);
            }
            return Task.FromResult<TaskOverallStatus?>(null);
        }
    }

    public Task UpdateTaskStatusAsync(int taskId, TaskOverallStatus newStatus, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _taskStatuses[taskId] = newStatus;
            return Task.CompletedTask;
        }
    }

    public Task<bool> IsTaskWaitingForHumanAsync(int taskId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            return Task.FromResult(_waitingForHuman.TryGetValue(taskId, out var val) && val);
        }
    }
}
