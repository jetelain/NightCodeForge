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
            .AsNoTracking()
            .Where(d => d.ForgeTask != null && d.ForgeTask.WorkspaceId == workspaceId)
            .Select(d => new { d.TaskId, d.DependsOnTaskId })
            .ToListAsync(cancellationToken);

        return items.Select(x => (x.TaskId, x.DependsOnTaskId)).ToList();
    }

    public async Task<IReadOnlyList<int>> GetPrerequisiteTaskIdsAsync(int taskId, CancellationToken cancellationToken = default)
    {
        return await _context.TaskDependencies
            .AsNoTracking()
            .Where(d => d.TaskId == taskId)
            .Select(d => d.DependsOnTaskId)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<int>> GetDependentTaskIdsAsync(int prerequisiteTaskId, CancellationToken cancellationToken = default)
    {
        return await _context.TaskDependencies
            .AsNoTracking()
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
        var now = DateTime.UtcNow;
        await _context.Tasks
            .Where(t => t.Id == taskId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, newStatus)
                .SetProperty(t => t.UpdatedAt, now),
                cancellationToken);

        var local = _context.Tasks.Local.FirstOrDefault(t => t.Id == taskId);
        if (local != null)
        {
            var entry = _context.Entry(local);
            SyncCleanValue(entry.Property(t => t.Status), newStatus);
            SyncCleanValue(entry.Property(t => t.UpdatedAt), now);
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
        var now = DateTime.UtcNow;
        await _context.Tasks
            .Where(t => t.Id == taskId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.IsWaitingForHuman, isWaiting)
                .SetProperty(t => t.UpdatedAt, now),
                cancellationToken);

        var local = _context.Tasks.Local.FirstOrDefault(t => t.Id == taskId);
        if (local != null)
        {
            var entry = _context.Entry(local);
            SyncCleanValue(entry.Property(t => t.IsWaitingForHuman), isWaiting);
            SyncCleanValue(entry.Property(t => t.UpdatedAt), now);
        }
    }

    /// <summary>
    /// Aligns a tracked property with a value already written to the database by ExecuteUpdate,
    /// without leaving the property flagged as modified (avoids a redundant second write).
    /// </summary>
    private static void SyncCleanValue<TProp>(Microsoft.EntityFrameworkCore.ChangeTracking.PropertyEntry<ForgeTask, TProp> property, TProp value)
    {
        property.CurrentValue = value;
        property.OriginalValue = value;
        property.IsModified = false;
    }
}
