using Microsoft.EntityFrameworkCore;
using NightCodeForge.Core.Dependencies;
using NightCodeForge.Core.Domain;

namespace NightCodeForge.Infrastructure.Data.Repositories;

/// <summary>
/// EF Core SQLite implementation of IIssueDependencyStore for DAG dependency resolution.
/// </summary>
public class EfCoreIssueDependencyStore : IIssueDependencyStore
{
    private readonly AppDbContext _context;

    public EfCoreIssueDependencyStore(AppDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public async Task<IReadOnlyList<(int IssueId, int DependsOnIssueId)>> GetDependenciesForWorkspaceAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        var items = await _context.IssueDependencies
            .Where(d => d.Issue != null && d.Issue.WorkspaceId == workspaceId)
            .Select(d => new { d.IssueId, d.DependsOnIssueId })
            .ToListAsync(cancellationToken);

        return items.Select(x => (x.IssueId, x.DependsOnIssueId)).ToList();
    }

    public async Task<IReadOnlyList<int>> GetPrerequisiteIssueIdsAsync(int issueId, CancellationToken cancellationToken = default)
    {
        return await _context.IssueDependencies
            .Where(d => d.IssueId == issueId)
            .Select(d => d.DependsOnIssueId)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<int>> GetDependentIssueIdsAsync(int prerequisiteIssueId, CancellationToken cancellationToken = default)
    {
        return await _context.IssueDependencies
            .Where(d => d.DependsOnIssueId == prerequisiteIssueId)
            .Select(d => d.IssueId)
            .ToListAsync(cancellationToken);
    }

    public async Task<IssueOverallStatus?> GetIssueStatusAsync(int issueId, CancellationToken cancellationToken = default)
    {
        var issue = await _context.Issues
            .AsNoTracking()
            .Where(i => i.Id == issueId)
            .Select(i => new { i.Status })
            .FirstOrDefaultAsync(cancellationToken);

        return issue?.Status;
    }

    public async Task UpdateIssueStatusAsync(int issueId, IssueOverallStatus newStatus, CancellationToken cancellationToken = default)
    {
        var issue = await _context.Issues.FindAsync(new object[] { issueId }, cancellationToken);
        if (issue != null)
        {
            issue.Status = newStatus;
            issue.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<bool> IsIssueWaitingForHumanAsync(int issueId, CancellationToken cancellationToken = default)
    {
        var issue = await _context.Issues
            .AsNoTracking()
            .Where(i => i.Id == issueId)
            .Select(i => new { i.IsWaitingForHuman })
            .FirstOrDefaultAsync(cancellationToken);

        return issue?.IsWaitingForHuman ?? false;
    }

    public async Task SetWaitingForHumanAsync(int issueId, bool isWaiting, CancellationToken cancellationToken = default)
    {
        var issue = await _context.Issues.FindAsync(new object[] { issueId }, cancellationToken);
        if (issue != null)
        {
            issue.IsWaitingForHuman = isWaiting;
            issue.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);
        }
    }
}
