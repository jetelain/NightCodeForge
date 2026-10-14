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
            .Include(i => i.Workspace)
            .Include(i => i.Dependencies)
            .Include(i => i.Questions)
            .Include(i => i.Checkpoints)
            .Include(i => i.ExecutionRecords)
            .Include(i => i.ExecutionLogs)
            .FirstOrDefaultAsync(i => i.Id == id, cancellationToken);
    }

    public async Task<IReadOnlyList<ForgeTask>> GetByWorkspaceIdAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        return await _context.Tasks
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
        _context.Tasks.Update(task);

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

    public async Task<IReadOnlyList<ForgeTask>> GetByStatusAsync(TaskOverallStatus status, CancellationToken cancellationToken = default)
    {
        return await _context.Tasks
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
}
