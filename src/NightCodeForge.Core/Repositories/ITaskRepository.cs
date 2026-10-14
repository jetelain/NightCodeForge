using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Repositories;

/// <summary>
/// Repository interface for task persistence and dependency querying.
/// </summary>
public interface ITaskRepository
{
    Task<ForgeTask?> GetByIdAsync(int id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ForgeTask>> GetByWorkspaceIdAsync(int workspaceId, CancellationToken cancellationToken = default);
    Task<ForgeTask> CreateAsync(ForgeTask task, IEnumerable<int>? dependsOn = null, CancellationToken cancellationToken = default);
    Task UpdateAsync(ForgeTask task, IEnumerable<int>? dependsOn = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<int>> GetDependenciesAsync(int taskId, CancellationToken cancellationToken = default);
    Task DeleteAsync(int id, CancellationToken cancellationToken = default);
    Task<Question?> GetQuestionByIdAsync(int questionId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ForgeTask>> GetByStatusAsync(TaskOverallStatus status, CancellationToken cancellationToken = default);
    Task AddExecutionRecordAsync(StageExecutionRecord record, CancellationToken cancellationToken = default);
    Task AddCheckpointAsync(StageCheckpoint checkpoint, CancellationToken cancellationToken = default);
    Task AddExecutionLogAsync(ExecutionLog log, CancellationToken cancellationToken = default);
}

/// <summary>
/// Thread-safe in-memory task repository for unit testing and detached execution.
/// </summary>
public class InMemoryTaskRepository : ITaskRepository
{
    private readonly object _lock = new();
    private readonly Dictionary<int, ForgeTask> _tasks = new();
    private readonly Dictionary<int, List<int>> _dependencies = new();
    private readonly Dictionary<int, Question> _questions = new();
    private readonly List<StageExecutionRecord> _records = new();
    private readonly List<StageCheckpoint> _checkpoints = new();
    private readonly List<ExecutionLog> _logs = new();
    private int _nextId = 1;
    private int _nextQuestionId = 1;
    private int _nextCheckpointId = 1;
    private int _nextRecordId = 1;
    private int _nextLogId = 1;

    public Task<ForgeTask?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_tasks.TryGetValue(id, out var task))
            {
                return Task.FromResult<ForgeTask?>(task);
            }
            return Task.FromResult<ForgeTask?>(null);
        }
    }

    public Task<IReadOnlyList<ForgeTask>> GetByWorkspaceIdAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            IReadOnlyList<ForgeTask> list = _tasks.Values.Where(i => i.WorkspaceId == workspaceId).ToList();
            return Task.FromResult(list);
        }
    }

    public Task<ForgeTask> CreateAsync(ForgeTask task, IEnumerable<int>? dependsOn = null, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (task.Id <= 0)
            {
                task.Id = _nextId++;
            }
            else if (task.Id >= _nextId)
            {
                _nextId = task.Id + 1;
            }

            _tasks[task.Id] = task;
            _dependencies[task.Id] = dependsOn != null ? dependsOn.ToList() : new List<int>();

            foreach (var q in task.Questions)
            {
                if (q.Id <= 0) q.Id = _nextQuestionId++;
                _questions[q.Id] = q;
            }

            return Task.FromResult(task);
        }
    }

    public Task UpdateAsync(ForgeTask task, IEnumerable<int>? dependsOn = null, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _tasks[task.Id] = task;
            if (dependsOn != null)
            {
                _dependencies[task.Id] = dependsOn.ToList();
            }

            foreach (var q in task.Questions)
            {
                if (q.Id <= 0) q.Id = _nextQuestionId++;
                _questions[q.Id] = q;
            }

            return Task.CompletedTask;
        }
    }

    public Task<IReadOnlyList<int>> GetDependenciesAsync(int taskId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_dependencies.TryGetValue(taskId, out var deps))
            {
                return Task.FromResult<IReadOnlyList<int>>(deps.ToList());
            }
            return Task.FromResult<IReadOnlyList<int>>(Array.Empty<int>());
        }
    }

    public Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _tasks.Remove(id);
            _dependencies.Remove(id);
            return Task.CompletedTask;
        }
    }

    public Task<Question?> GetQuestionByIdAsync(int questionId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_questions.TryGetValue(questionId, out var question))
            {
                return Task.FromResult<Question?>(question);
            }
            return Task.FromResult<Question?>(null);
        }
    }

    public Task<IReadOnlyList<ForgeTask>> GetByStatusAsync(TaskOverallStatus status, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            IReadOnlyList<ForgeTask> list = _tasks.Values.Where(i => i.Status == status).ToList();
            return Task.FromResult(list);
        }
    }

    public Task AddExecutionRecordAsync(StageExecutionRecord record, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (record.Id <= 0) record.Id = _nextRecordId++;
            _records.Add(record);
            if (_tasks.TryGetValue(record.TaskId, out var task))
            {
                task.ExecutionRecords.Add(record);
            }
            return Task.CompletedTask;
        }
    }

    public Task AddCheckpointAsync(StageCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (checkpoint.Id <= 0) checkpoint.Id = _nextCheckpointId++;
            _checkpoints.Add(checkpoint);
            if (_tasks.TryGetValue(checkpoint.TaskId, out var task))
            {
                task.Checkpoints.Add(checkpoint);
            }
            return Task.CompletedTask;
        }
    }

    public Task AddExecutionLogAsync(ExecutionLog log, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (log.Id <= 0) log.Id = _nextLogId++;
            _logs.Add(log);
            if (log.TaskId > 0 && _tasks.TryGetValue(log.TaskId, out var task))
            {
                task.ExecutionLogs.Add(log);
            }
            return Task.CompletedTask;
        }
    }

    public IReadOnlyList<ExecutionLog> GetAllLogs()
    {
        lock (_lock)
        {
            return _logs.ToList();
        }
    }

    public IReadOnlyList<StageCheckpoint> GetAllCheckpoints()
    {
        lock (_lock)
        {
            return _checkpoints.ToList();
        }
    }

    public IReadOnlyList<StageExecutionRecord> GetAllExecutionRecords()
    {
        lock (_lock)
        {
            return _records.ToList();
        }
    }
}
