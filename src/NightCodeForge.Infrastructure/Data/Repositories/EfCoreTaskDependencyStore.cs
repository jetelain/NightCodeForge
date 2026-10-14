using Microsoft.EntityFrameworkCore;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;

namespace NightCodeForge.Infrastructure.Data.Repositories;

/// <summary>
/// EF Core SQLite implementation of ITaskDependencyStore for DAG dependency resolution.
/// </summary>
public class EfCoreTaskDependencyStore : ITaskDependencyStore
{
    private readonly AppDbContext _context;

    public EfCoreTaskDependencyStore(AppDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public async Task<IReadOnlyList<(int TaskId, int DependsOnTaskId)>> GetDependenciesForWorkspaceAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        var items = await _context.TaskDependencies
            .Where(d => d.ForgeTask != null && d.ForgeTask.WorkspaceId == workspaceId)
            .Select(d => new { d.TaskId, d.DependsOnTaskId })
            .ToListAsync(cancellationToken);

        return items.Select(x => (x.TaskId, x.DependsOnTaskId)).ToList();
    }

    public async Task<IReadOnlyList<int>> GetPrerequisiteTaskIdsAsync(int taskId, CancellationToken cancellationToken = default)
    {
        return await _context.TaskDependencies
            .Where(d => d.TaskId == taskId)
            .Select(d => d.DependsOnTaskId)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<int>> GetDependentTaskIdsAsync(int prerequisiteTaskId, CancellationToken cancellationToken = default)
    {
        return await _context.TaskDependencies
            .Where(d => d.DependsOnTaskId == prerequisiteTaskId)
            .Select(d => d.TaskId)
            .ToListAsync(cancellationToken);
    }

    public async Task<TaskOverallStatus?> GetTaskStatusAsync(int taskId, CancellationToken cancellationToken = default)
    {
        var task = await _context.Tasks
            .AsNoTracking()
            .Where(i => i.Id == taskId)
            .Select(i => new { i.Status })
            .FirstOrDefaultAsync(cancellationToken);

        return task?.Status;
    }

    public async Task UpdateTaskStatusAsync(int taskId, TaskOverallStatus newStatus, CancellationToken cancellationToken = default)
    {
        var task = await _context.Tasks.FindAsync(new object[] { taskId }, cancellationToken);
        if (task != null)
        {
            task.Status = newStatus;
            task.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<bool> IsTaskWaitingForHumanAsync(int taskId, CancellationToken cancellationToken = default)
    {
        var task = await _context.Tasks
            .AsNoTracking()
            .Where(i => i.Id == taskId)
            .Select(i => new { i.IsWaitingForHuman })
            .FirstOrDefaultAsync(cancellationToken);

        return task?.IsWaitingForHuman ?? false;
    }

    public async Task SetWaitingForHumanAsync(int taskId, bool isWaiting, CancellationToken cancellationToken = default)
    {
        var task = await _context.Tasks.FindAsync(new object[] { taskId }, cancellationToken);
        if (task != null)
        {
            task.IsWaitingForHuman = isWaiting;
            task.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);
        }
    }
}
