using Microsoft.EntityFrameworkCore;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Repositories;

namespace NightCodeForge.Infrastructure.Data.Repositories;

/// <summary>
/// EF Core SQLite implementation of <see cref="ITaskAttachmentRepository"/> creating short-lived <see cref="AppDbContext"/> instances on demand.
/// </summary>
public class EfCoreTaskAttachmentRepository : ITaskAttachmentRepository
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;

    public EfCoreTaskAttachmentRepository(IDbContextFactory<AppDbContext> contextFactory)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
    }

    public async Task<TaskAttachment?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.TaskAttachments
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
    }

    public async Task<IReadOnlyList<TaskAttachment>> GetByIdsAsync(IEnumerable<int> ids, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var idList = ids.Distinct().ToList();
        if (idList.Count == 0)
        {
            return Array.Empty<TaskAttachment>();
        }

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.TaskAttachments
            .AsNoTracking()
            .Where(a => idList.Contains(a.Id))
            .OrderBy(a => a.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TaskAttachment>> GetByWorkspaceIdAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.TaskAttachments
            .AsNoTracking()
            .Where(a => a.WorkspaceId == workspaceId)
            .OrderBy(a => a.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<TaskAttachment?> GetByWorkspaceAndSha256Async(int workspaceId, string sha256, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        var normalizedHash = NormalizeSha256(sha256);

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.TaskAttachments
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.WorkspaceId == workspaceId && a.Sha256 == normalizedHash, cancellationToken);
    }

    public async Task<TaskAttachment> CreateAsync(TaskAttachment attachment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        attachment.Sha256 = NormalizeSha256(attachment.Sha256);

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        context.TaskAttachments.Add(attachment);
        await context.SaveChangesAsync(cancellationToken);
        return attachment;
    }

    public async Task UpdateAsync(TaskAttachment attachment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        attachment.Sha256 = NormalizeSha256(attachment.Sha256);

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await context.TaskAttachments.FindAsync(new object[] { attachment.Id }, cancellationToken)
            ?? throw new AttachmentNotFoundException(attachment.Id, attachment.WorkspaceId);

        context.Entry(existing).CurrentValues.SetValues(attachment);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await context.TaskAttachments.FindAsync(new object[] { id }, cancellationToken);
        if (existing != null)
        {
            context.TaskAttachments.Remove(existing);
            await context.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<IReadOnlyList<TaskAttachment>> GetByTaskIdAsync(int taskId, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.TaskAttachmentLinks
            .AsNoTracking()
            .Where(l => l.TaskId == taskId && l.TaskAttachment != null)
            .Select(l => l.TaskAttachment!)
            .OrderBy(a => a.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<int>> GetLinkedTaskIdsAsync(int attachmentId, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.TaskAttachmentLinks
            .AsNoTracking()
            .Where(l => l.TaskAttachmentId == attachmentId)
            .Select(l => l.TaskId)
            .Distinct()
            .OrderBy(id => id)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ForgeTask>> GetLinkedTasksAsync(int attachmentId, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.TaskAttachmentLinks
            .AsNoTracking()
            .Where(l => l.TaskAttachmentId == attachmentId && l.ForgeTask != null)
            .Select(l => l.ForgeTask!)
            .OrderBy(t => t.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task SetTaskAttachmentLinksAsync(int taskId, IEnumerable<int> attachmentIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attachmentIds);
        var distinctIds = attachmentIds.Distinct().ToList();

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var task = await context.Tasks
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken)
            ?? throw new TaskNotFoundException(taskId);

        await ValidateAttachmentsExistInWorkspaceAsync(context, task.WorkspaceId, distinctIds, cancellationToken);

        var existingLinks = await context.TaskAttachmentLinks
            .Where(l => l.TaskId == taskId)
            .ToListAsync(cancellationToken);

        context.TaskAttachmentLinks.RemoveRange(existingLinks);

        foreach (var attachmentId in distinctIds)
        {
            context.TaskAttachmentLinks.Add(new TaskAttachmentLink
            {
                TaskId = taskId,
                TaskAttachmentId = attachmentId
            });
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task AddTaskAttachmentLinksAsync(int taskId, IEnumerable<int> attachmentIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attachmentIds);
        var distinctIds = attachmentIds.Distinct().ToList();
        if (distinctIds.Count == 0)
        {
            return;
        }

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var task = await context.Tasks
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken)
            ?? throw new TaskNotFoundException(taskId);

        await ValidateAttachmentsExistInWorkspaceAsync(context, task.WorkspaceId, distinctIds, cancellationToken);

        var existingIds = await context.TaskAttachmentLinks
            .Where(l => l.TaskId == taskId)
            .Select(l => l.TaskAttachmentId)
            .ToListAsync(cancellationToken);
        var existingSet = existingIds.ToHashSet();

        var added = false;
        foreach (var attachmentId in distinctIds)
        {
            if (existingSet.Add(attachmentId))
            {
                context.TaskAttachmentLinks.Add(new TaskAttachmentLink
                {
                    TaskId = taskId,
                    TaskAttachmentId = attachmentId
                });
                added = true;
            }
        }

        if (added)
        {
            await context.SaveChangesAsync(cancellationToken);
        }
    }

    private static async Task ValidateAttachmentsExistInWorkspaceAsync(
        AppDbContext context,
        int workspaceId,
        IReadOnlyList<int> attachmentIds,
        CancellationToken cancellationToken)
    {
        if (attachmentIds.Count == 0)
        {
            return;
        }

        var found = await context.TaskAttachments
            .AsNoTracking()
            .Where(a => attachmentIds.Contains(a.Id))
            .Select(a => new { a.Id, a.WorkspaceId })
            .ToListAsync(cancellationToken);

        var byId = found.ToDictionary(a => a.Id, a => a.WorkspaceId);
        foreach (var id in attachmentIds)
        {
            if (!byId.TryGetValue(id, out var attachmentWorkspaceId) || attachmentWorkspaceId != workspaceId)
            {
                throw new AttachmentNotFoundException(id, workspaceId);
            }
        }
    }

    private static string NormalizeSha256(string? sha256) =>
        (sha256 ?? string.Empty).Trim().ToLowerInvariant();
}
