using Microsoft.EntityFrameworkCore;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Repositories;

namespace NightCodeForge.Infrastructure.Data.Repositories;

/// <summary>
/// EF Core SQLite implementation of IIssueRepository.
/// </summary>
public class EfCoreIssueRepository : IIssueRepository
{
    private readonly AppDbContext _context;

    public EfCoreIssueRepository(AppDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public async Task<Issue?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        return await _context.Issues
            .Include(i => i.Workspace)
            .Include(i => i.Dependencies)
            .Include(i => i.Questions)
            .Include(i => i.Checkpoints)
            .Include(i => i.ExecutionRecords)
            .Include(i => i.ExecutionLogs)
            .FirstOrDefaultAsync(i => i.Id == id, cancellationToken);
    }

    public async Task<IReadOnlyList<Issue>> GetByWorkspaceIdAsync(int workspaceId, CancellationToken cancellationToken = default)
    {
        return await _context.Issues
            .Where(i => i.WorkspaceId == workspaceId)
            .OrderBy(i => i.Priority)
            .ThenBy(i => i.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<Issue> CreateAsync(Issue issue, IEnumerable<int>? dependsOn = null, CancellationToken cancellationToken = default)
    {
        _context.Issues.Add(issue);
        await _context.SaveChangesAsync(cancellationToken);

        if (dependsOn != null)
        {
            foreach (var depId in dependsOn.Distinct())
            {
                _context.IssueDependencies.Add(new IssueDependency
                {
                    IssueId = issue.Id,
                    DependsOnIssueId = depId
                });
            }
            await _context.SaveChangesAsync(cancellationToken);
        }

        return issue;
    }

    public async Task UpdateAsync(Issue issue, IEnumerable<int>? dependsOn = null, CancellationToken cancellationToken = default)
    {
        _context.Issues.Update(issue);

        if (dependsOn != null)
        {
            var existingDeps = await _context.IssueDependencies
                .Where(d => d.IssueId == issue.Id)
                .ToListAsync(cancellationToken);

            _context.IssueDependencies.RemoveRange(existingDeps);

            foreach (var depId in dependsOn.Distinct())
            {
                _context.IssueDependencies.Add(new IssueDependency
                {
                    IssueId = issue.Id,
                    DependsOnIssueId = depId
                });
            }
        }

        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<int>> GetDependenciesAsync(int issueId, CancellationToken cancellationToken = default)
    {
        return await _context.IssueDependencies
            .Where(d => d.IssueId == issueId)
            .Select(d => d.DependsOnIssueId)
            .ToListAsync(cancellationToken);
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        var issue = await _context.Issues.FindAsync(new object[] { id }, cancellationToken);
        if (issue != null)
        {
            _context.Issues.Remove(issue);
            await _context.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<Question?> GetQuestionByIdAsync(int questionId, CancellationToken cancellationToken = default)
    {
        return await _context.Questions
            .Include(q => q.Issue)
            .FirstOrDefaultAsync(q => q.Id == questionId, cancellationToken);
    }

    public async Task<IReadOnlyList<Issue>> GetByStatusAsync(IssueOverallStatus status, CancellationToken cancellationToken = default)
    {
        return await _context.Issues
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
