using Microsoft.EntityFrameworkCore;
using NightCodeForge.Core.Domain;
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

    public async Task<ForgeTask?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        return await _context.Tasks
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
            .FirstOrDefaultAsync(i => i.Id == id, cancellationToken);
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

    public async Task<ForgeTask> CreateAsync(ForgeTask task, IEnumerable<int>? dependsOn = null, CancellationToken cancellationToken = default)
    {
        _context.Tasks.Add(task);
        await _context.SaveChangesAsync(cancellationToken);

        if (dependsOn != null)
        {
            foreach (var depId in dependsOn.Distinct())
            {
                _context.TaskDependencies.Add(new TaskDependency
                {
                    TaskId = task.Id,
                    DependsOnTaskId = depId
                });
            }
            await _context.SaveChangesAsync(cancellationToken);
        }

        return task;
    }

    public async Task UpdateAsync(ForgeTask task, IEnumerable<int>? dependsOn = null, CancellationToken cancellationToken = default)
    {
        var existing = _context.Tasks.Local.FirstOrDefault(entry => entry.Id == task.Id)
                       ?? await _context.Tasks.FindAsync(new object[] { task.Id }, cancellationToken);

        if (existing != null)
        {
            if (!ReferenceEquals(existing, task))
            {
                _context.Entry(existing).CurrentValues.SetValues(task);
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

            foreach (var depId in dependsOn.Distinct())
            {
                _context.TaskDependencies.Add(new TaskDependency
                {
                    TaskId = task.Id,
                    DependsOnTaskId = depId
                });
            }
        }

        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<int>> GetDependenciesAsync(int taskId, CancellationToken cancellationToken = default)
    {
        return await _context.TaskDependencies
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
            conversation.CompletedAt = DateTime.UtcNow;
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
        var nextIndex = await _context.ConversationTurns
            .Where(t => t.ConversationId == conversationId)
            .CountAsync(cancellationToken);

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

    public Task<Conversation> StartIdeationConversationAsync(
        int workspaceId,
        string? title = null,
        string? modelName = null,
        string? agentProfileId = null,
        CancellationToken cancellationToken = default)
    {
        return StartConversationAsync(
            taskId: null,
            stageId: "ideation",
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
