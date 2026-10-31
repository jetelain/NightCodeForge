using Microsoft.EntityFrameworkCore;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;

namespace NightCodeForge.Infrastructure.Data.Repositories;

/// <summary>
/// EF Core SQLite implementation of ITaskDependencyStore for DAG dependency resolution,
/// creating short-lived AppDbContext instances on demand.
/// </summary>
public class EfCoreTaskDependencyStore : ITaskDependencyStore
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;

    public EfCoreTaskDependencyStore(IDbContextFactory<AppDbContext> contextFactory)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
    }

    public async Task<IReadOnlyList<(int TaskId, int DependsOnTaskId)>> GetDependenciesForWorkspaceAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var items = await context.TaskDependencies
            .AsNoTracking()
            .Where(d => d.ForgeTask != null && d.ForgeTask.WorkspaceId == workspaceId)
            .Select(d => new { d.TaskId, d.DependsOnTaskId })
            .ToListAsync(cancellationToken);

        return items.Select(x => (x.TaskId, x.DependsOnTaskId)).ToList();
    }

    public async Task<IReadOnlyList<int>> GetPrerequisiteTaskIdsAsync(int taskId, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.TaskDependencies
            .AsNoTracking()
            .Where(d => d.TaskId == taskId)
            .Select(d => d.DependsOnTaskId)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<int>> GetDependentTaskIdsAsync(int prerequisiteTaskId, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.TaskDependencies
            .AsNoTracking()
            .Where(d => d.DependsOnTaskId == prerequisiteTaskId)
            .Select(d => d.TaskId)
            .ToListAsync(cancellationToken);
    }

    public async Task<TaskOverallStatus?> GetTaskStatusAsync(int taskId, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var task = await context.Tasks
            .AsNoTracking()
            .Where(i => i.Id == taskId)
            .Select(i => new { i.Status })
            .FirstOrDefaultAsync(cancellationToken);

        return task?.Status;
    }

    public async Task UpdateTaskStatusAsync(int taskId, TaskOverallStatus newStatus, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var now = DateTime.UtcNow;
        await context.Tasks
            .Where(t => t.Id == taskId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, newStatus)
                .SetProperty(t => t.UpdatedAt, now),
                cancellationToken);
    }

    public async Task<bool> IsTaskWaitingForHumanAsync(int taskId, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var task = await context.Tasks
            .AsNoTracking()
            .Where(i => i.Id == taskId)
            .Select(i => new { i.IsWaitingForHuman })
            .FirstOrDefaultAsync(cancellationToken);

        return task?.IsWaitingForHuman ?? false;
    }

    public async Task SetWaitingForHumanAsync(int taskId, bool isWaiting, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var now = DateTime.UtcNow;
        await context.Tasks
            .Where(t => t.Id == taskId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.IsWaitingForHuman, isWaiting)
                .SetProperty(t => t.UpdatedAt, now),
                cancellationToken);
    }
}
