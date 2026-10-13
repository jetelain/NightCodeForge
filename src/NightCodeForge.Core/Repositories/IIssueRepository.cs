using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Repositories;

/// <summary>
/// Repository interface for issue persistence and dependency querying.
/// </summary>
public interface IIssueRepository
{
    Task<Issue?> GetByIdAsync(int id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Issue>> GetByWorkspaceIdAsync(int workspaceId, CancellationToken cancellationToken = default);
    Task<Issue> CreateAsync(Issue issue, IEnumerable<int>? dependsOn = null, CancellationToken cancellationToken = default);
    Task UpdateAsync(Issue issue, IEnumerable<int>? dependsOn = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<int>> GetDependenciesAsync(int issueId, CancellationToken cancellationToken = default);
    Task DeleteAsync(int id, CancellationToken cancellationToken = default);
    Task<Question?> GetQuestionByIdAsync(int questionId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Issue>> GetByStatusAsync(IssueOverallStatus status, CancellationToken cancellationToken = default);
    Task AddExecutionRecordAsync(StageExecutionRecord record, CancellationToken cancellationToken = default);
    Task AddCheckpointAsync(StageCheckpoint checkpoint, CancellationToken cancellationToken = default);
    Task AddExecutionLogAsync(ExecutionLog log, CancellationToken cancellationToken = default);
}

/// <summary>
/// Thread-safe in-memory issue repository for unit testing and detached execution.
/// </summary>
public class InMemoryIssueRepository : IIssueRepository
{
    private readonly object _lock = new();
    private readonly Dictionary<int, Issue> _issues = new();
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

    public Task<Issue?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_issues.TryGetValue(id, out var issue))
            {
                return Task.FromResult<Issue?>(issue);
            }
            return Task.FromResult<Issue?>(null);
        }
    }

    public Task<IReadOnlyList<Issue>> GetByWorkspaceIdAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            IReadOnlyList<Issue> list = _issues.Values.Where(i => i.WorkspaceId == workspaceId).ToList();
            return Task.FromResult(list);
        }
    }

    public Task<Issue> CreateAsync(Issue issue, IEnumerable<int>? dependsOn = null, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (issue.Id <= 0)
            {
                issue.Id = _nextId++;
            }
            else if (issue.Id >= _nextId)
            {
                _nextId = issue.Id + 1;
            }

            _issues[issue.Id] = issue;
            _dependencies[issue.Id] = dependsOn != null ? dependsOn.ToList() : new List<int>();

            foreach (var q in issue.Questions)
            {
                if (q.Id <= 0) q.Id = _nextQuestionId++;
                _questions[q.Id] = q;
            }

            return Task.FromResult(issue);
        }
    }

    public Task UpdateAsync(Issue issue, IEnumerable<int>? dependsOn = null, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _issues[issue.Id] = issue;
            if (dependsOn != null)
            {
                _dependencies[issue.Id] = dependsOn.ToList();
            }

            foreach (var q in issue.Questions)
            {
                if (q.Id <= 0) q.Id = _nextQuestionId++;
                _questions[q.Id] = q;
            }

            return Task.CompletedTask;
        }
    }

    public Task<IReadOnlyList<int>> GetDependenciesAsync(int issueId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_dependencies.TryGetValue(issueId, out var deps))
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
            _issues.Remove(id);
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

    public Task<IReadOnlyList<Issue>> GetByStatusAsync(IssueOverallStatus status, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            IReadOnlyList<Issue> list = _issues.Values.Where(i => i.Status == status).ToList();
            return Task.FromResult(list);
        }
    }

    public Task AddExecutionRecordAsync(StageExecutionRecord record, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (record.Id <= 0) record.Id = _nextRecordId++;
            _records.Add(record);
            if (_issues.TryGetValue(record.IssueId, out var issue))
            {
                issue.ExecutionRecords.Add(record);
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
            if (_issues.TryGetValue(checkpoint.IssueId, out var issue))
            {
                issue.Checkpoints.Add(checkpoint);
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
            if (log.IssueId > 0 && _issues.TryGetValue(log.IssueId, out var issue))
            {
                issue.ExecutionLogs.Add(log);
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
