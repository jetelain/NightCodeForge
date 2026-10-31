using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;

namespace NightCodeForge.Core.Repositories;

/// <summary>
/// Repository interface for task persistence and dependency querying.
/// </summary>
public interface ITaskRepository
{
    Task<ForgeTask?> GetByIdAsync(int id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ForgeTask>> GetByIdsAsync(IEnumerable<int> ids, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the persisted overall status of a task directly from storage, bypassing any change-tracker cache.
    /// Returns null if the task does not exist.
    /// </summary>
    Task<TaskOverallStatus?> GetStatusAsync(int id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ForgeTask>> GetByWorkspaceIdAsync(int workspaceId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ForgeTask>> GetExecutedTasksByWorkspaceIdAsync(int workspaceId, CancellationToken cancellationToken = default);
    Task<ForgeTask> CreateAsync(ForgeTask task, IEnumerable<int>? dependsOn = null, CancellationToken cancellationToken = default);
    Task<ForgeTask> CreateWithBaselineAsync(ForgeTask task, IEnumerable<int>? dependsOn, string createdBy, string? reason, CancellationToken cancellationToken = default);
    Task UpdateAsync(ForgeTask task, IEnumerable<int>? dependsOn = null, CancellationToken cancellationToken = default);
    Task UpdateWithHistoryAsync(ForgeTask task, IEnumerable<int>? dependsOn, string changedBy, string? reason, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically updates only the LastHangfireJobId of a task without rewriting or overwriting other entity fields.
    /// </summary>
    Task UpdateLastHangfireJobIdAsync(int taskId, string? jobId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<int>> GetDependenciesAsync(int taskId, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<int, IReadOnlyList<int>>> GetWorkspaceDependenciesAsync(int workspaceId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ForgeTaskHistory>> GetHistoryAsync(int taskId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ForgeTaskHistory>> GetHistoryByTaskIdAsync(int taskId, CancellationToken cancellationToken = default)
        => GetHistoryAsync(taskId, cancellationToken);
    Task<int> GetMaxHistoryVersionAsync(int taskId, CancellationToken cancellationToken = default);
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

    Task<Conversation?> GetWaitingConversationAsync(
        int taskId,
        string stageId,
        CancellationToken cancellationToken = default);

    Task<Conversation?> GetResumableConversationAsync(
        int taskId,
        string stageId,
        CancellationToken cancellationToken = default);

    Task ResumeConversationAsync(
        int conversationId,
        CancellationToken cancellationToken = default);

    Task SanitizeTrailingInterruptedTurnsAsync(
        int conversationId,
        CancellationToken cancellationToken = default);

    Task<Conversation> StartIdeationConversationAsync(
        int workspaceId,
        string? title = null,
        string? modelName = null,
        string? agentProfileId = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Conversation>> GetIdeationConversationsAsync(
        int workspaceId,
        CancellationToken cancellationToken = default);

    Task<Conversation?> GetActiveIdeationConversationAsync(
        int workspaceId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Thread-safe in-memory task repository for unit testing and detached execution.
/// </summary>
public class InMemoryTaskRepository : ITaskRepository
{
    private readonly object _lock = new();
    private readonly Dictionary<int, ForgeTask> _tasks = new();
    private readonly Dictionary<int, List<int>> _dependencies = new();
    private readonly Dictionary<int, List<ForgeTaskHistory>> _taskHistories = new();
    private readonly Dictionary<int, Question> _questions = new();
    private readonly List<StageExecutionRecord> _records = new();
    private readonly List<StageCheckpoint> _checkpoints = new();
    private readonly List<ExecutionLog> _logs = new();
    private readonly Dictionary<int, Conversation> _conversations = new();
    private readonly List<ConversationTurn> _turns = new();
    private int _nextId = 1;
    private int _nextHistoryId = 1;
    private int _nextQuestionId = 1;
    private int _nextCheckpointId = 1;
    private int _nextRecordId = 1;
    private int _nextLogId = 1;
    private int _nextConversationId = 1;
    private int _nextTurnId = 1;

    public Task<TaskOverallStatus?> GetStatusAsync(int id, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            return Task.FromResult<TaskOverallStatus?>(_tasks.TryGetValue(id, out var task) ? task.Status : null);
        }
    }

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

    public Task<IReadOnlyList<ForgeTask>> GetByIdsAsync(IEnumerable<int> ids, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var idSet = ids.ToHashSet();
            var list = _tasks.Values.Where(t => idSet.Contains(t.Id)).ToList();
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

    public Task<IReadOnlyList<ForgeTask>> GetExecutedTasksByWorkspaceIdAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var list = _tasks.Values
                .Where(t => t.WorkspaceId == workspaceId && t.HasExecutionRun)
                .OrderByDescending(t => t.UpdatedAt)
                .ToList();
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
        return CreateWithBaselineAsync(task, dependsOn, AuditActors.System, TaskSyncConstants.InitialTaskCreation, cancellationToken);
    }

    public Task<ForgeTask> CreateWithBaselineAsync(ForgeTask task, IEnumerable<int>? dependsOn, string createdBy, string? reason, CancellationToken cancellationToken = default)
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

            if (task.Version <= 0)
            {
                task.Version = 1;
            }

            if (string.IsNullOrWhiteSpace(task.FeatureBranch))
            {
                task.FeatureBranch = $"feature/task-{task.Id}";
            }

            var depList = dependsOn != null
                ? dependsOn.Distinct().ToList()
                : (task.Dependencies?.Select(d => d.DependsOnTaskId).Distinct().ToList() ?? new List<int>());

            _tasks[task.Id] = task;
            _dependencies[task.Id] = depList;
            task.Dependencies = depList.Select(d => new TaskDependency { TaskId = task.Id, DependsOnTaskId = d }).ToList();

            var baselineHistory = new ForgeTaskHistory
            {
                Id = _nextHistoryId++,
                TaskId = task.Id,
                Version = task.Version,
                Title = task.Title,
                Description = task.Description,
                Status = task.Status,
                Priority = task.Priority,
                MergeMode = task.MergeMode,
                BaseBranch = task.BaseBranch,
                FeatureBranch = task.FeatureBranch,
                CurrentStageId = task.CurrentStageId,
                SerializedDependencies = System.Text.Json.JsonSerializer.Serialize(depList),
                ChangedAt = task.CreatedAt != default ? task.CreatedAt : DateTime.UtcNow,
                ChangedBy = createdBy,
                ChangeReason = reason,
                ForgeTask = task
            };

            if (!_taskHistories.TryGetValue(task.Id, out var historyList))
            {
                historyList = new List<ForgeTaskHistory>();
                _taskHistories[task.Id] = historyList;
            }
            historyList.Add(baselineHistory);
            task.History.Add(baselineHistory);

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
            if (_tasks.TryGetValue(task.Id, out var existing) && !ReferenceEquals(existing, task))
            {
                if (existing.Version > task.Version)
                {
                    task.Version = existing.Version;
                    task.MergeMode = existing.MergeMode;
                    task.Priority = existing.Priority;
                    task.Title = existing.Title;
                    task.Description = existing.Description;
                }
                else
                {
                    task.Version = existing.Version;
                }
            }

            _tasks[task.Id] = task;
            if (dependsOn != null)
            {
                var depList = dependsOn.Distinct().ToList();
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

    public Task UpdateLastHangfireJobIdAsync(int taskId, string? jobId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (!_tasks.TryGetValue(taskId, out var task))
            {
                throw new TaskNotFoundException(taskId, $"ForgeTask #{taskId} was not found when updating Hangfire job ID.");
            }

            task.LastHangfireJobId = jobId;
            return Task.CompletedTask;
        }
    }

    public Task UpdateWithHistoryAsync(ForgeTask task, IEnumerable<int>? dependsOn, string changedBy, string? reason, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var maxVersion = _taskHistories.TryGetValue(task.Id, out var histories) && histories.Count > 0
                ? histories.Max(h => h.Version)
                : 0;

            task.Version = Math.Max(task.Version + 1, maxVersion + 1);
            _tasks[task.Id] = task;

            List<int> depList;
            if (dependsOn != null)
            {
                depList = dependsOn.Distinct().ToList();
                _dependencies[task.Id] = depList;
                task.Dependencies = depList.Select(d => new TaskDependency { TaskId = task.Id, DependsOnTaskId = d }).ToList();
            }
            else
            {
                depList = _dependencies.TryGetValue(task.Id, out var existingDeps) ? existingDeps.ToList() : new List<int>();
            }

            var history = new ForgeTaskHistory
            {
                Id = _nextHistoryId++,
                TaskId = task.Id,
                Version = task.Version,
                Title = task.Title,
                Description = task.Description,
                Status = task.Status,
                Priority = task.Priority,
                MergeMode = task.MergeMode,
                BaseBranch = task.BaseBranch,
                FeatureBranch = task.FeatureBranch,
                CurrentStageId = task.CurrentStageId,
                SerializedDependencies = System.Text.Json.JsonSerializer.Serialize(depList),
                ChangedAt = DateTime.UtcNow,
                ChangedBy = changedBy,
                ChangeReason = reason,
                ForgeTask = task
            };

            if (!_taskHistories.TryGetValue(task.Id, out var list))
            {
                list = new List<ForgeTaskHistory>();
                _taskHistories[task.Id] = list;
            }
            list.Add(history);
            task.History.Add(history);

            foreach (var q in task.Questions)
            {
                if (q.Id <= 0) q.Id = _nextQuestionId++;
                _questions[q.Id] = q;
            }

            return Task.CompletedTask;
        }
    }

    public Task<IReadOnlyList<ForgeTaskHistory>> GetHistoryAsync(int taskId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_taskHistories.TryGetValue(taskId, out var histories))
            {
                return Task.FromResult<IReadOnlyList<ForgeTaskHistory>>(histories.OrderBy(h => h.Version).ToList());
            }
            return Task.FromResult<IReadOnlyList<ForgeTaskHistory>>(Array.Empty<ForgeTaskHistory>());
        }
    }

    public Task<IReadOnlyList<ForgeTaskHistory>> GetHistoryByTaskIdAsync(int taskId, CancellationToken cancellationToken = default)
        => GetHistoryAsync(taskId, cancellationToken);

    public Task<int> GetMaxHistoryVersionAsync(int taskId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_taskHistories.TryGetValue(taskId, out var histories) && histories.Count > 0)
            {
                return Task.FromResult(histories.Max(h => h.Version));
            }
            return Task.FromResult(0);
        }
    }

    public Task<IReadOnlyDictionary<int, IReadOnlyList<int>>> GetWorkspaceDependenciesAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var result = new Dictionary<int, IReadOnlyList<int>>();
            var workspaceTasks = _tasks.Values.Where(t => t.WorkspaceId == workspaceId);
            foreach (var t in workspaceTasks)
            {
                if (_dependencies.TryGetValue(t.Id, out var deps))
                {
                    result[t.Id] = deps.ToList();
                }
                else
                {
                    result[t.Id] = Array.Empty<int>();
                }
            }
            return Task.FromResult<IReadOnlyDictionary<int, IReadOnlyList<int>>>(result);
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
            _taskHistories.Remove(id);
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
        cancellationToken.ThrowIfCancellationRequested();
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
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            if (_conversations.TryGetValue(conversationId, out var conversation))
            {
                conversation.Status = status;
                conversation.CompletedAt = status is StageExecutionStatus.WaitingForHuman or StageExecutionStatus.Interrupted
                    ? null
                    : DateTime.UtcNow;
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
        cancellationToken.ThrowIfCancellationRequested();
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
                turn.TurnIndex = conversation.Turns.Count > 0
                    ? conversation.Turns.Max(t => t.TurnIndex) + 1
                    : 0;
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

    public Task<Conversation?> GetWaitingConversationAsync(
        int taskId,
        string stageId,
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var conv = _conversations.Values
                .Where(c => c.TaskId == taskId && string.Equals(c.StageId, stageId, StringComparison.OrdinalIgnoreCase) && c.Status == StageExecutionStatus.WaitingForHuman)
                .OrderByDescending(c => c.StartedAt)
                .ThenByDescending(c => c.Id)
                .FirstOrDefault();

            if (conv != null)
            {
                conv.Turns = conv.Turns.OrderBy(t => t.TurnIndex).ToList();
                return Task.FromResult<Conversation?>(conv);
            }

            return Task.FromResult<Conversation?>(null);
        }
    }

    public Task<Conversation?> GetResumableConversationAsync(
        int taskId,
        string stageId,
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var conv = _conversations.Values
                .Where(c => c.TaskId == taskId
                         && string.Equals(c.StageId, stageId, StringComparison.OrdinalIgnoreCase)
                         && (c.Status == StageExecutionStatus.WaitingForHuman || c.Status == StageExecutionStatus.Interrupted))
                .OrderByDescending(c => c.AttemptIndex)
                .ThenByDescending(c => c.StartedAt)
                .ThenByDescending(c => c.Id)
                .FirstOrDefault();

            if (conv != null)
            {
                conv.Turns = conv.Turns.OrderBy(t => t.TurnIndex).ToList();
                return Task.FromResult<Conversation?>(conv);
            }

            return Task.FromResult<Conversation?>(null);
        }
    }

    public Task ResumeConversationAsync(
        int conversationId,
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (!_conversations.TryGetValue(conversationId, out var conversation))
            {
                throw new KeyNotFoundException($"Conversation #{conversationId} not found.");
            }

            conversation.Status = StageExecutionStatus.Running;
            conversation.CompletedAt = null;
            conversation.Summary = null;
            return Task.CompletedTask;
        }
    }

    public Task SanitizeTrailingInterruptedTurnsAsync(
        int conversationId,
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_conversations.TryGetValue(conversationId, out var conversation) && conversation.Turns.Count > 0)
            {
                var toRemove = NightCodeForge.Core.Workflow.ConversationHistorySerializer.GetTrailingInterruptedTurns(conversation.Turns);
                foreach (var turn in toRemove)
                {
                    conversation.Turns.Remove(turn);
                    _turns.Remove(turn);
                }
            }
            return Task.CompletedTask;
        }
    }

    public Task<Conversation> StartIdeationConversationAsync(
        int workspaceId,
        string? title = null,
        string? modelName = null,
        string? agentProfileId = null,
        CancellationToken cancellationToken = default)
    {
        return StartConversationAsync(
            taskId: null,
            stageId: StandardStageIds.Ideation,
            stageKind: WorkflowStageKind.Agentic,
            attemptIndex: 1,
            agentProfileId: agentProfileId,
            harnessId: StandardHarnessIds.Crucible,
            modelName: modelName,
            workspaceId: workspaceId,
            conversationType: ConversationType.Ideation,
            title: string.IsNullOrWhiteSpace(title) ? "Ideation Session" : title,
            cancellationToken: cancellationToken);
    }

    public Task<IReadOnlyList<Conversation>> GetIdeationConversationsAsync(
        int workspaceId,
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var list = _conversations.Values
                .Where(c => c.WorkspaceId == workspaceId && c.ConversationType == ConversationType.Ideation)
                .OrderByDescending(c => c.StartedAt)
                .ThenByDescending(c => c.Id)
                .ToList();
            return Task.FromResult<IReadOnlyList<Conversation>>(list);
        }
    }

    public Task<Conversation?> GetActiveIdeationConversationAsync(
        int workspaceId,
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var conv = _conversations.Values
                .Where(c => c.WorkspaceId == workspaceId
                         && c.ConversationType == ConversationType.Ideation
                         && c.Status == StageExecutionStatus.Running)
                .OrderByDescending(c => c.StartedAt)
                .ThenByDescending(c => c.Id)
                .FirstOrDefault();

            if (conv != null)
            {
                conv.Turns = conv.Turns.OrderBy(t => t.TurnIndex).ToList();
                return Task.FromResult<Conversation?>(conv);
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
