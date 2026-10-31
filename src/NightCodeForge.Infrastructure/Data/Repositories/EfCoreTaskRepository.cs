using Microsoft.EntityFrameworkCore;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Repositories;

namespace NightCodeForge.Infrastructure.Data.Repositories;

/// <summary>
/// EF Core SQLite implementation of ITaskRepository.
/// </summary>
public class EfCoreTaskRepository : ITaskRepository
{
    private readonly AppDbContext _context;

    public EfCoreTaskRepository(AppDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public async Task<TaskOverallStatus?> GetStatusAsync(int id, CancellationToken cancellationToken = default)
    {
        return await _context.Tasks
            .AsNoTracking()
            .Where(t => t.Id == id)
            .Select(t => (TaskOverallStatus?)t.Status)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<ForgeTask?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        return await _context.Tasks
            .AsNoTracking()
            .AsSplitQuery()
            .Include(i => i.Workspace)
            .Include(i => i.Workflow)
                .ThenInclude(w => w!.Stages)
            .Include(i => i.Dependencies)
            .Include(i => i.Questions)
            .Include(i => i.Checkpoints)
            .Include(i => i.ExecutionRecords)
            .Include(i => i.ExecutionLogs)
            .Include(i => i.Conversations)
            .Include(i => i.History)
            .FirstOrDefaultAsync(i => i.Id == id, cancellationToken);
    }

    public async Task<IReadOnlyList<ForgeTask>> GetByIdsAsync(IEnumerable<int> ids, CancellationToken cancellationToken = default)
    {
        var idList = ids.Distinct().ToList();
        if (idList.Count == 0) return Array.Empty<ForgeTask>();

        return await _context.Tasks
            .AsNoTracking()
            .AsSplitQuery()
            .Include(i => i.Workflow)
            .Include(i => i.Dependencies)
            .Include(i => i.DependentTasks)
            .Include(i => i.Questions)
            .Include(i => i.ExecutionRecords)
            .Include(i => i.ExecutionLogs)
            .Where(i => idList.Contains(i.Id))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ForgeTask>> GetByWorkspaceIdAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        return await _context.Tasks
            .AsNoTracking()
            .AsSplitQuery()
            .Include(i => i.Workflow)
            .Include(i => i.Dependencies)
            .Include(i => i.DependentTasks)
            .Include(i => i.Questions)
            .Include(i => i.ExecutionRecords)
            .Include(i => i.ExecutionLogs)
            .Where(i => i.WorkspaceId == workspaceId)
            .OrderBy(i => i.Priority)
            .ThenBy(i => i.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ForgeTask>> GetExecutedTasksByWorkspaceIdAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        return await _context.Tasks
            .AsNoTracking()
            .AsSplitQuery()
            .Include(i => i.Workflow)
            .Include(i => i.Dependencies)
            .Include(i => i.DependentTasks)
            .Include(i => i.Questions)
            .Include(i => i.ExecutionRecords)
            .Include(i => i.ExecutionLogs)
            .Where(t => t.WorkspaceId == workspaceId && t.HasExecutionRun)
            .OrderByDescending(t => t.UpdatedAt)
            .ToListAsync(cancellationToken);
    }

    public Task<ForgeTask> CreateAsync(ForgeTask task, IEnumerable<int>? dependsOn = null, CancellationToken cancellationToken = default)
    {
        return CreateWithBaselineAsync(task, dependsOn, AuditActors.System, TaskSyncConstants.InitialTaskCreation, cancellationToken);
    }

    public async Task<ForgeTask> CreateWithBaselineAsync(ForgeTask task, IEnumerable<int>? dependsOn, string createdBy, string? reason, CancellationToken cancellationToken = default)
    {
        if (task.Version <= 0)
        {
            task.Version = 1;
        }

        // Task, dependencies and Version 1 baseline must be one atomic unit.
        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

        _context.Tasks.Add(task);
        await _context.SaveChangesAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(task.FeatureBranch))
        {
            task.FeatureBranch = $"feature/task-{task.Id}";
        }

        var depList = dependsOn != null
            ? dependsOn.Distinct().ToList()
            : (task.Dependencies?.Select(d => d.DependsOnTaskId).Distinct().ToList() ?? new List<int>());

        if (depList.Count > 0)
        {
            foreach (var depId in depList)
            {
                _context.TaskDependencies.Add(new TaskDependency
                {
                    TaskId = task.Id,
                    DependsOnTaskId = depId
                });
            }
            await _context.SaveChangesAsync(cancellationToken);
        }

        task.Dependencies = depList.Select(d => new TaskDependency { TaskId = task.Id, DependsOnTaskId = d }).ToList();

        var baselineHistory = new ForgeTaskHistory
        {
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
            ChangeReason = reason
        };

        _context.TaskHistories.Add(baselineHistory);
        task.History?.Add(baselineHistory);
        await _context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return task;
    }

    public async Task UpdateAsync(ForgeTask task, IEnumerable<int>? dependsOn = null, CancellationToken cancellationToken = default)
    {
        var existing = _context.Tasks.Local.FirstOrDefault(entry => entry.Id == task.Id);
        if (existing != null)
        {
            if (!ReferenceEquals(existing, task))
            {
                await _context.Entry(existing).ReloadAsync(cancellationToken);
            }
        }
        else
        {
            existing = await _context.Tasks.FindAsync(new object[] { task.Id }, cancellationToken);
        }

        if (existing != null)
        {
            if (!ReferenceEquals(existing, task))
            {
                // The version token and maintainer-controlled fields are owned by UpdateWithHistoryAsync:
                // a background execution state update must never regress them.
                var authoritativeVersion = existing.Version;
                var authoritativeMergeMode = existing.MergeMode;
                var authoritativePriority = existing.Priority;
                var authoritativeTitle = existing.Title;
                var authoritativeDescription = existing.Description;

                _context.Entry(existing).CurrentValues.SetValues(task);

                // If the database has a newer version than the incoming in-memory task,
                // maintainer properties owned by UpdateWithHistoryAsync were modified concurrently.
                // A background execution state update must not regress them.
                if (authoritativeVersion > task.Version)
                {
                    existing.Version = authoritativeVersion;
                    existing.MergeMode = authoritativeMergeMode;
                    existing.Priority = authoritativePriority;
                    existing.Title = authoritativeTitle;
                    existing.Description = authoritativeDescription;

                    // Sync latest maintainer values back to caller's in-memory instance
                    task.Version = authoritativeVersion;
                    task.MergeMode = authoritativeMergeMode;
                    task.Priority = authoritativePriority;
                    task.Title = authoritativeTitle;
                    task.Description = authoritativeDescription;
                }
                else
                {
                    // Version token is strictly owned by UpdateWithHistoryAsync
                    existing.Version = authoritativeVersion;
                }

                _context.Entry(existing).State = EntityState.Modified;
            }
        }
        else
        {
            _context.Tasks.Update(task);
        }

        if (dependsOn != null)
        {
            var existingDeps = await _context.TaskDependencies
                .Where(d => d.TaskId == task.Id)
                .ToListAsync(cancellationToken);

            _context.TaskDependencies.RemoveRange(existingDeps);

            var newDeps = dependsOn.Distinct().Select(depId => new TaskDependency
            {
                TaskId = task.Id,
                DependsOnTaskId = depId
            }).ToList();

            foreach (var dep in newDeps)
            {
                _context.TaskDependencies.Add(dep);
            }

            task.Dependencies = newDeps;
            if (existing != null && !ReferenceEquals(existing, task))
            {
                existing.Dependencies = task.Dependencies;
            }
        }

        await SyncQuestionsAsync(task, existing ?? task, cancellationToken);
        await SyncCheckpointsAsync(task, existing ?? task, cancellationToken);

        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateLastHangfireJobIdAsync(int taskId, string? jobId, CancellationToken cancellationToken = default)
    {
        var existing = _context.Tasks.Local.FirstOrDefault(entry => entry.Id == taskId)
                       ?? await _context.Tasks.FindAsync(new object[] { taskId }, cancellationToken);

        if (existing == null)
        {
            throw new TaskNotFoundException(taskId, $"ForgeTask #{taskId} was not found when updating Hangfire job ID.");
        }

        existing.LastHangfireJobId = jobId;
        _context.Entry(existing).Property(t => t.LastHangfireJobId).IsModified = true;
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateWithHistoryAsync(ForgeTask task, IEnumerable<int>? dependsOn, string changedBy, string? reason, CancellationToken cancellationToken = default)
    {
        var currentMaxVersion = await _context.TaskHistories
            .Where(h => h.TaskId == task.Id)
            .Select(h => (int?)h.Version)
            .MaxAsync(cancellationToken) ?? 0;

        task.Version = Math.Max(task.Version + 1, currentMaxVersion + 1);

        var existing = _context.Tasks.Local.FirstOrDefault(entry => entry.Id == task.Id);
        if (existing != null)
        {
            if (!ReferenceEquals(existing, task))
            {
                await _context.Entry(existing).ReloadAsync(cancellationToken);
            }
        }
        else
        {
            existing = await _context.Tasks.FindAsync(new object[] { task.Id }, cancellationToken);
        }

        if (existing != null)
        {
            if (!ReferenceEquals(existing, task))
            {
                _context.Entry(existing).CurrentValues.SetValues(task);
                _context.Entry(existing).State = EntityState.Modified;
            }
        }
        else
        {
            _context.Tasks.Update(task);
        }

        List<int> depList;
        if (dependsOn != null)
        {
            depList = dependsOn.Distinct().ToList();
            var existingDeps = await _context.TaskDependencies
                .Where(d => d.TaskId == task.Id)
                .ToListAsync(cancellationToken);

            _context.TaskDependencies.RemoveRange(existingDeps);

            var newDeps = depList.Select(depId => new TaskDependency
            {
                TaskId = task.Id,
                DependsOnTaskId = depId
            }).ToList();

            foreach (var dep in newDeps)
            {
                _context.TaskDependencies.Add(dep);
            }

            task.Dependencies = newDeps;
            if (existing != null && !ReferenceEquals(existing, task))
            {
                existing.Dependencies = task.Dependencies;
            }
        }
        else
        {
            depList = await _context.TaskDependencies
                .Where(d => d.TaskId == task.Id)
                .Select(d => d.DependsOnTaskId)
                .ToListAsync(cancellationToken);
        }

        var history = new ForgeTaskHistory
        {
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
            ChangeReason = reason
        };

        _context.TaskHistories.Add(history);
        task.History?.Add(history);
        if (existing != null && !ReferenceEquals(existing, task))
        {
            existing.History?.Add(history);
        }
        await SyncQuestionsAsync(task, existing ?? task, cancellationToken);
        await SyncCheckpointsAsync(task, existing ?? task, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
    }

    private async Task SyncCheckpointsAsync(ForgeTask task, ForgeTask trackedTask, CancellationToken cancellationToken = default)
    {
        if (task.Checkpoints == null || task.Checkpoints.Count == 0) return;

        var existingIds = task.Checkpoints.Where(c => c.Id > 0).Select(c => c.Id).Distinct().ToList();
        if (existingIds.Count > 0)
        {
            var missingIds = existingIds.Where(id => _context.StageCheckpoints.Local.All(x => x.Id != id)).ToList();
            if (missingIds.Count > 0)
            {
                await _context.StageCheckpoints
                    .Where(c => missingIds.Contains(c.Id))
                    .ToListAsync(cancellationToken);
            }
        }

        foreach (var c in task.Checkpoints.ToList())
        {
            if (c.Id <= 0)
            {
                if (c.TaskId <= 0)
                {
                    c.TaskId = task.Id;
                }
                c.ForgeTask = trackedTask;
                var tracked = _context.StageCheckpoints.Local.FirstOrDefault(x => ReferenceEquals(x, c));
                if (tracked == null)
                {
                    _context.StageCheckpoints.Add(c);
                }
            }
            else
            {
                var existingC = _context.StageCheckpoints.Local.FirstOrDefault(x => x.Id == c.Id);
                if (existingC != null)
                {
                    if (!ReferenceEquals(existingC, c))
                    {
                        _context.Entry(existingC).CurrentValues.SetValues(c);
                    }
                    existingC.ForgeTask = trackedTask;
                }
                else
                {
                    c.ForgeTask = trackedTask;
                    _context.StageCheckpoints.Attach(c);
                    _context.Entry(c).State = EntityState.Modified;
                }
            }
        }
    }

    private async Task SyncQuestionsAsync(ForgeTask task, ForgeTask trackedTask, CancellationToken cancellationToken = default)
    {
        if (task.Questions == null || task.Questions.Count == 0) return;

        var existingIds = task.Questions.Where(q => q.Id > 0).Select(q => q.Id).Distinct().ToList();
        if (existingIds.Count > 0)
        {
            var missingIds = existingIds.Where(id => _context.Questions.Local.All(x => x.Id != id)).ToList();
            if (missingIds.Count > 0)
            {
                await _context.Questions
                    .Where(q => missingIds.Contains(q.Id))
                    .ToListAsync(cancellationToken);
            }
        }

        foreach (var q in task.Questions.ToList())
        {
            if (q.Id <= 0)
            {
                if (q.TaskId <= 0)
                {
                    q.TaskId = task.Id;
                }
                q.ForgeTask = trackedTask;
                var tracked = _context.Questions.Local.FirstOrDefault(x => ReferenceEquals(x, q));
                if (tracked == null)
                {
                    _context.Questions.Add(q);
                }
            }
            else
            {
                var existingQ = _context.Questions.Local.FirstOrDefault(x => x.Id == q.Id);
                if (existingQ != null)
                {
                    if (!ReferenceEquals(existingQ, q))
                    {
                        _context.Entry(existingQ).CurrentValues.SetValues(q);
                    }
                    existingQ.ForgeTask = trackedTask;
                }
                else
                {
                    q.ForgeTask = trackedTask;
                    _context.Questions.Attach(q);
                    _context.Entry(q).State = EntityState.Modified;
                }
            }
        }
    }

    public async Task<IReadOnlyList<ForgeTaskHistory>> GetHistoryAsync(int taskId, CancellationToken cancellationToken = default)
    {
        return await _context.TaskHistories
            .AsNoTracking()
            .Where(h => h.TaskId == taskId)
            .OrderBy(h => h.Version)
            .ToListAsync(cancellationToken);
    }

    public Task<IReadOnlyList<ForgeTaskHistory>> GetHistoryByTaskIdAsync(int taskId, CancellationToken cancellationToken = default)
    {
        return GetHistoryAsync(taskId, cancellationToken);
    }

    public async Task<int> GetMaxHistoryVersionAsync(int taskId, CancellationToken cancellationToken = default)
    {
        return await _context.TaskHistories
            .Where(h => h.TaskId == taskId)
            .Select(h => (int?)h.Version)
            .MaxAsync(cancellationToken) ?? 0;
    }

    public async Task<IReadOnlyDictionary<int, IReadOnlyList<int>>> GetWorkspaceDependenciesAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        var taskIds = await _context.Tasks
            .AsNoTracking()
            .Where(t => t.WorkspaceId == workspaceId)
            .Select(t => t.Id)
            .ToListAsync(cancellationToken);

        var deps = await _context.TaskDependencies
            .AsNoTracking()
            .Where(d => taskIds.Contains(d.TaskId))
            .Select(d => new { d.TaskId, d.DependsOnTaskId })
            .ToListAsync(cancellationToken);

        var dict = taskIds.ToDictionary(id => id, id => (IReadOnlyList<int>)new List<int>());
        foreach (var dep in deps)
        {
            if (dict.TryGetValue(dep.TaskId, out var list) && list is List<int> mutableList)
            {
                mutableList.Add(dep.DependsOnTaskId);
            }
        }
        return dict;
    }

    public async Task<IReadOnlyList<int>> GetDependenciesAsync(int taskId, CancellationToken cancellationToken = default)
    {
        return await _context.TaskDependencies
            .AsNoTracking()
            .Where(d => d.TaskId == taskId)
            .Select(d => d.DependsOnTaskId)
            .ToListAsync(cancellationToken);
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        var task = await _context.Tasks.FindAsync(new object[] { id }, cancellationToken);
        if (task != null)
        {
            _context.Tasks.Remove(task);
            await _context.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<Question?> GetQuestionByIdAsync(int questionId, CancellationToken cancellationToken = default)
    {
        return await _context.Questions
            .Include(q => q.ForgeTask)
            .FirstOrDefaultAsync(q => q.Id == questionId, cancellationToken);
    }

    public async Task<IReadOnlyList<Question>> GetQuestionsByStatusAsync(QuestionStatus status, int? workspaceId = null, CancellationToken cancellationToken = default)
    {
        var query = _context.Questions
            .Include(q => q.ForgeTask)
            .Where(q => q.Status == status);

        if (workspaceId.HasValue)
        {
            query = query.Where(q => q.ForgeTask != null && q.ForgeTask.WorkspaceId == workspaceId.Value);
        }

        return await query
            .OrderByDescending(q => q.AskedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ForgeTask>> GetByStatusAsync(TaskOverallStatus status, CancellationToken cancellationToken = default)
    {
        return await _context.Tasks
            .AsNoTracking()
            .AsSplitQuery()
            .Include(i => i.Workspace)
            .Include(i => i.Checkpoints)
            .Include(i => i.ExecutionRecords)
            .Where(i => i.Status == status)
            .ToListAsync(cancellationToken);
    }

    public async Task AddExecutionRecordAsync(StageExecutionRecord record, CancellationToken cancellationToken = default)
    {
        _context.StageExecutionRecords.Add(record);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task AddCheckpointAsync(StageCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        _context.StageCheckpoints.Add(checkpoint);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task AddExecutionLogAsync(ExecutionLog log, CancellationToken cancellationToken = default)
    {
        _context.ExecutionLogs.Add(log);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<Conversation> StartConversationAsync(
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
        var conversation = new Conversation
        {
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
            Status = StageExecutionStatus.Running
        };

        _context.Conversations.Add(conversation);
        await _context.SaveChangesAsync(cancellationToken);
        return conversation;
    }

    public async Task CompleteConversationAsync(
        int conversationId,
        StageExecutionStatus status,
        string? summary = null,
        string? transcriptFileName = null,
        CancellationToken cancellationToken = default)
    {
        var conversation = await _context.Conversations.FindAsync(new object[] { conversationId }, cancellationToken);
        if (conversation != null)
        {
            conversation.Status = status;
            conversation.CompletedAt = status is StageExecutionStatus.WaitingForHuman or StageExecutionStatus.Interrupted
                ? null
                : DateTime.UtcNow;
            if (summary != null) conversation.Summary = summary;
            if (transcriptFileName != null) conversation.TranscriptFileName = transcriptFileName;
            await _context.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<ConversationTurn> AddConversationTurnAsync(
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
        var maxIndex = await _context.ConversationTurns
            .Where(t => t.ConversationId == conversationId)
            .MaxAsync(t => (int?)t.TurnIndex, cancellationToken);
        var nextIndex = (maxIndex ?? -1) + 1;

        var turn = new ConversationTurn
        {
            ConversationId = conversationId,
            TurnIndex = nextIndex,
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

        _context.ConversationTurns.Add(turn);
        await _context.SaveChangesAsync(cancellationToken);
        return turn;
    }

    public async Task<IReadOnlyList<Conversation>> GetConversationsAsync(
        int taskId,
        CancellationToken cancellationToken = default)
    {
        return await _context.Conversations
            .AsNoTracking()
            .Where(c => c.TaskId == taskId)
            .OrderBy(c => c.StartedAt)
            .ThenBy(c => c.AttemptIndex)
            .ToListAsync(cancellationToken);
    }

    public async Task<Conversation?> GetConversationWithTurnsAsync(
        int conversationId,
        CancellationToken cancellationToken = default)
    {
        return await _context.Conversations
            .AsNoTracking()
            .Include(c => c.Turns.OrderBy(t => t.TurnIndex))
            .FirstOrDefaultAsync(c => c.Id == conversationId, cancellationToken);
    }

    public async Task<Conversation?> GetWaitingConversationAsync(
        int taskId,
        string stageId,
        CancellationToken cancellationToken = default)
    {
        return await _context.Conversations
            .Include(c => c.Turns.OrderBy(t => t.TurnIndex))
            .Where(c => c.TaskId == taskId && c.StageId == stageId && c.Status == StageExecutionStatus.WaitingForHuman)
            .OrderByDescending(c => c.StartedAt)
            .ThenByDescending(c => c.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<Conversation?> GetResumableConversationAsync(
        int taskId,
        string stageId,
        CancellationToken cancellationToken = default)
    {
        return await _context.Conversations
            .Include(c => c.Turns.OrderBy(t => t.TurnIndex))
            .Where(c => c.TaskId == taskId
                     && c.StageId == stageId
                     && (c.Status == StageExecutionStatus.WaitingForHuman || c.Status == StageExecutionStatus.Interrupted))
            .OrderByDescending(c => c.AttemptIndex)
            .ThenByDescending(c => c.StartedAt)
            .ThenByDescending(c => c.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task ResumeConversationAsync(
        int conversationId,
        CancellationToken cancellationToken = default)
    {
        var conversation = await _context.Conversations.FindAsync(new object[] { conversationId }, cancellationToken)
            ?? throw new KeyNotFoundException($"Conversation #{conversationId} not found.");

        conversation.Status = StageExecutionStatus.Running;
        conversation.CompletedAt = null;
        conversation.Summary = null;
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task SanitizeTrailingInterruptedTurnsAsync(
        int conversationId,
        CancellationToken cancellationToken = default)
    {
        var turns = await _context.ConversationTurns
            .Where(t => t.ConversationId == conversationId)
            .OrderBy(t => t.TurnIndex)
            .ToListAsync(cancellationToken);

        var toRemove = NightCodeForge.Core.Workflow.ConversationHistorySerializer.GetTrailingInterruptedTurns(turns);
        if (toRemove.Count > 0)
        {
            _context.ConversationTurns.RemoveRange(toRemove);
            await _context.SaveChangesAsync(cancellationToken);
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

    public async Task<IReadOnlyList<Conversation>> GetIdeationConversationsAsync(
        int workspaceId,
        CancellationToken cancellationToken = default)
    {
        return await _context.Conversations
            .AsNoTracking()
            .Where(c => c.WorkspaceId == workspaceId && c.ConversationType == ConversationType.Ideation)
            .OrderByDescending(c => c.StartedAt)
            .ThenByDescending(c => c.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<Conversation?> GetActiveIdeationConversationAsync(
        int workspaceId,
        CancellationToken cancellationToken = default)
    {
        return await _context.Conversations
            .AsNoTracking()
            .Include(c => c.Turns.OrderBy(t => t.TurnIndex))
            .Where(c => c.WorkspaceId == workspaceId
                     && c.ConversationType == ConversationType.Ideation
                     && c.Status == StageExecutionStatus.Running)
            .OrderByDescending(c => c.StartedAt)
            .ThenByDescending(c => c.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
