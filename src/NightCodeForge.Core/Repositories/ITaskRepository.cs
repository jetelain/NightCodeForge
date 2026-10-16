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
    Task<IReadOnlyList<Question>> GetQuestionsByStatusAsync(QuestionStatus status, int? workspaceId = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ForgeTask>> GetByStatusAsync(TaskOverallStatus status, CancellationToken cancellationToken = default);
    Task AddExecutionRecordAsync(StageExecutionRecord record, CancellationToken cancellationToken = default);
    Task AddCheckpointAsync(StageCheckpoint checkpoint, CancellationToken cancellationToken = default);
    Task AddExecutionLogAsync(ExecutionLog log, CancellationToken cancellationToken = default);

    Task<Conversation> StartConversationAsync(
        int? taskId,
        string stageId,
        WorkflowStageKind stageKind,
        int attemptIndex,
        string? agentProfileId = null,
        string? harnessId = null,
        string? modelName = null,
        int? workspaceId = null,
        ConversationType conversationType = ConversationType.StageRun,
        string? title = null,
        CancellationToken cancellationToken = default);

    Task CompleteConversationAsync(
        int conversationId,
        StageExecutionStatus status,
        string? summary = null,
        string? transcriptFileName = null,
        CancellationToken cancellationToken = default);

    Task<ConversationTurn> AddConversationTurnAsync(
        int conversationId,
        StageTurnRole role,
        string? content = null,
        string? thinkingTrace = null,
        string? toolName = null,
        string? toolArguments = null,
        string? toolResult = null,
        long? durationMs = null,
        bool isError = false,
        string? metadataJson = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Conversation>> GetConversationsAsync(
        int taskId,
        CancellationToken cancellationToken = default);

    Task<Conversation?> GetConversationWithTurnsAsync(
        int conversationId,
        CancellationToken cancellationToken = default);

    // Backward-compatibility aliases
    Task<Conversation> StartStageConversationAsync(
        int taskId,
        string stageId,
        WorkflowStageKind stageKind,
        int attemptIndex,
        string? agentProfileId = null,
        string? harnessId = null,
        string? modelName = null,
        CancellationToken cancellationToken = default) =>
        StartConversationAsync(taskId, stageId, stageKind, attemptIndex, agentProfileId, harnessId, modelName, null, ConversationType.StageRun, null, cancellationToken);

    Task CompleteStageConversationAsync(
        int conversationId,
        StageExecutionStatus status,
        string? summary = null,
        string? transcriptFileName = null,
        CancellationToken cancellationToken = default) =>
        CompleteConversationAsync(conversationId, status, summary, transcriptFileName, cancellationToken);

    Task<IReadOnlyList<Conversation>> GetStageConversationsAsync(
        int taskId,
        CancellationToken cancellationToken = default) =>
        GetConversationsAsync(taskId, cancellationToken);

    Task<Conversation?> GetStageConversationWithTurnsAsync(
        int conversationId,
        CancellationToken cancellationToken = default) =>
        GetConversationWithTurnsAsync(conversationId, cancellationToken);
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
    private readonly Dictionary<int, Conversation> _conversations = new();
    private readonly List<ConversationTurn> _turns = new();
    private int _nextId = 1;
    private int _nextQuestionId = 1;
    private int _nextCheckpointId = 1;
    private int _nextRecordId = 1;
    private int _nextLogId = 1;
    private int _nextConversationId = 1;
    private int _nextTurnId = 1;

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
            var list = _tasks.Values.Where(i => i.WorkspaceId == workspaceId).ToList();
            foreach (var task in list)
            {
                if (_dependencies.TryGetValue(task.Id, out var deps))
                {
                    task.Dependencies = deps.Select(d => new TaskDependency { TaskId = task.Id, DependsOnTaskId = d }).ToList();
                }
            }
            return Task.FromResult<IReadOnlyList<ForgeTask>>(list);
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

            var depList = dependsOn != null ? dependsOn.ToList() : new List<int>();
            _tasks[task.Id] = task;
            _dependencies[task.Id] = depList;
            task.Dependencies = depList.Select(d => new TaskDependency { TaskId = task.Id, DependsOnTaskId = d }).ToList();

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
                var depList = dependsOn.ToList();
                _dependencies[task.Id] = depList;
                task.Dependencies = depList.Select(d => new TaskDependency { TaskId = task.Id, DependsOnTaskId = d }).ToList();
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
            var convsToRemove = _conversations.Values.Where(c => c.TaskId == id).Select(c => c.Id).ToList();
            foreach (var convId in convsToRemove)
            {
                _conversations.Remove(convId);
                _turns.RemoveAll(t => t.ConversationId == convId);
            }
            return Task.CompletedTask;
        }
    }

    public Task<Question?> GetQuestionByIdAsync(int questionId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_questions.TryGetValue(questionId, out var question))
            {
                if (question.ForgeTask == null && _tasks.TryGetValue(question.TaskId, out var t))
                {
                    question.ForgeTask = t;
                }
                return Task.FromResult<Question?>(question);
            }
            return Task.FromResult<Question?>(null);
        }
    }

    public Task<IReadOnlyList<Question>> GetQuestionsByStatusAsync(QuestionStatus status, int? workspaceId = null, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var list = _questions.Values
                .Where(q => q.Status == status)
                .Where(q => !workspaceId.HasValue || (q.ForgeTask != null && q.ForgeTask.WorkspaceId == workspaceId.Value) || (_tasks.TryGetValue(q.TaskId, out var t) && t.WorkspaceId == workspaceId.Value))
                .OrderByDescending(q => q.AskedAt)
                .ToList();

            foreach (var q in list)
            {
                if (q.ForgeTask == null && _tasks.TryGetValue(q.TaskId, out var t))
                {
                    q.ForgeTask = t;
                }
            }

            return Task.FromResult<IReadOnlyList<Question>>(list);
        }
    }

    public void AddQuestion(Question question)
    {
        lock (_lock)
        {
            if (question.Id <= 0) question.Id = _nextQuestionId++;
            if (question.ForgeTask != null && question.TaskId <= 0)
            {
                question.TaskId = question.ForgeTask.Id;
            }
            _questions[question.Id] = question;
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

    public Task<Conversation> StartConversationAsync(
        int? taskId,
        string stageId,
        WorkflowStageKind stageKind,
        int attemptIndex,
        string? agentProfileId = null,
        string? harnessId = null,
        string? modelName = null,
        int? workspaceId = null,
        ConversationType conversationType = ConversationType.StageRun,
        string? title = null,
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var conversation = new Conversation
            {
                Id = _nextConversationId++,
                TaskId = taskId,
                WorkspaceId = workspaceId,
                ConversationType = conversationType,
                Title = title,
                StageId = stageId,
                StageKind = stageKind,
                AttemptIndex = attemptIndex,
                AgentProfileId = agentProfileId,
                HarnessId = harnessId,
                ModelName = modelName,
                StartedAt = DateTime.UtcNow,
                Status = StageExecutionStatus.Running,
                Turns = new List<ConversationTurn>()
            };

            _conversations[conversation.Id] = conversation;
            if (taskId.HasValue && _tasks.TryGetValue(taskId.Value, out var task))
            {
                task.Conversations.Add(conversation);
                conversation.ForgeTask = task;
            }

            return Task.FromResult(conversation);
        }
    }

    public Task CompleteConversationAsync(
        int conversationId,
        StageExecutionStatus status,
        string? summary = null,
        string? transcriptFileName = null,
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_conversations.TryGetValue(conversationId, out var conversation))
            {
                conversation.Status = status;
                conversation.CompletedAt = DateTime.UtcNow;
                if (summary != null) conversation.Summary = summary;
                if (transcriptFileName != null) conversation.TranscriptFileName = transcriptFileName;
            }
            return Task.CompletedTask;
        }
    }

    public Task<ConversationTurn> AddConversationTurnAsync(
        int conversationId,
        StageTurnRole role,
        string? content = null,
        string? thinkingTrace = null,
        string? toolName = null,
        string? toolArguments = null,
        string? toolResult = null,
        long? durationMs = null,
        bool isError = false,
        string? metadataJson = null,
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var turn = new ConversationTurn
            {
                Id = _nextTurnId++,
                ConversationId = conversationId,
                Role = role,
                Content = content,
                ThinkingTrace = thinkingTrace,
                ToolName = toolName,
                ToolArguments = toolArguments,
                ToolResult = toolResult,
                DurationMs = durationMs,
                IsError = isError,
                MetadataJson = metadataJson,
                Timestamp = DateTime.UtcNow
            };

            if (_conversations.TryGetValue(conversationId, out var conversation))
            {
                turn.TurnIndex = conversation.Turns.Count;
                turn.Conversation = conversation;
                conversation.Turns.Add(turn);
            }
            _turns.Add(turn);

            return Task.FromResult(turn);
        }
    }

    public Task<IReadOnlyList<Conversation>> GetConversationsAsync(
        int taskId,
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var list = _conversations.Values
                .Where(c => c.TaskId == taskId)
                .OrderBy(c => c.StartedAt)
                .ThenBy(c => c.AttemptIndex)
                .ToList();
            return Task.FromResult<IReadOnlyList<Conversation>>(list);
        }
    }

    public Task<Conversation?> GetConversationWithTurnsAsync(
        int conversationId,
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_conversations.TryGetValue(conversationId, out var conversation))
            {
                conversation.Turns = conversation.Turns.OrderBy(t => t.TurnIndex).ToList();
                return Task.FromResult<Conversation?>(conversation);
            }
            return Task.FromResult<Conversation?>(null);
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

    public IReadOnlyList<Conversation> GetAllConversations()
    {
        lock (_lock)
        {
            return _conversations.Values.ToList();
        }
    }

    public IReadOnlyList<ConversationTurn> GetAllConversationTurns()
    {
        lock (_lock)
        {
            return _turns.ToList();
        }
    }
}
