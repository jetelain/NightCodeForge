using Microsoft.EntityFrameworkCore;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Repositories;

namespace NightCodeForge.Infrastructure.Data.Repositories;

/// <summary>
/// EF Core SQLite implementation of IWorkspaceRepository creating short-lived AppDbContext instances on demand.
/// </summary>
public class EfCoreWorkspaceRepository : IWorkspaceRepository
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;

    public EfCoreWorkspaceRepository(IDbContextFactory<AppDbContext> contextFactory)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
    }

    public async Task<Workspace?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Workspaces
            .AsNoTracking()
            .AsSplitQuery()
            .Include(w => w.AgentProfiles)
            .Include(w => w.Tasks)
            .FirstOrDefaultAsync(w => w.Id == id, cancellationToken);
    }

    public async Task<IReadOnlyList<Workspace>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Workspaces
            .AsNoTracking()
            .Include(w => w.AgentProfiles)
            .ToListAsync(cancellationToken);
    }

    public async Task<Workspace> CreateAsync(Workspace workspace, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        context.Workspaces.Add(workspace);
        await context.SaveChangesAsync(cancellationToken);
        return workspace;
    }

    public async Task UpdateAsync(Workspace workspace, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var existing = await context.Workspaces.FindAsync(new object[] { workspace.Id }, cancellationToken);
        if (existing != null)
        {
            context.Entry(existing).CurrentValues.SetValues(workspace);
            await SyncAgentProfilesAsync(context, workspace, existing, cancellationToken);
        }
        else
        {
            context.Workspaces.Update(workspace);
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    private static async Task SyncAgentProfilesAsync(
        AppDbContext context,
        Workspace workspace,
        Workspace trackedWorkspace,
        CancellationToken cancellationToken)
    {
        if (workspace.AgentProfiles == null || workspace.AgentProfiles.Count == 0)
        {
            return;
        }

        var profileIds = workspace.AgentProfiles
            .Where(p => !string.IsNullOrWhiteSpace(p.Id))
            .Select(p => p.Id)
            .ToHashSet(StringComparer.Ordinal);

        if (profileIds.Count > 0)
        {
            var existingProfiles = await context.AgentProfiles
                .Where(p => p.WorkspaceId == workspace.Id || profileIds.Contains(p.Id))
                .ToListAsync(cancellationToken);

            var profilesToRemove = existingProfiles
                .Where(p => p.WorkspaceId == workspace.Id && !profileIds.Contains(p.Id))
                .ToList();

            if (profilesToRemove.Count > 0)
            {
                context.AgentProfiles.RemoveRange(profilesToRemove);
            }
        }

        foreach (var profile in workspace.AgentProfiles.ToList())
        {
            if (string.IsNullOrWhiteSpace(profile.Id))
            {
                continue;
            }

            profile.WorkspaceId ??= workspace.Id;
            var existingProfile = context.AgentProfiles.Local.FirstOrDefault(p =>
                string.Equals(p.Id, profile.Id, StringComparison.Ordinal));

            if (existingProfile != null)
            {
                if (!ReferenceEquals(existingProfile, profile))
                {
                    context.Entry(existingProfile).CurrentValues.SetValues(profile);
                }
                existingProfile.Workspace = trackedWorkspace;
            }
            else
            {
                profile.Workspace = trackedWorkspace;
                context.AgentProfiles.Add(profile);
            }
        }
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var workspace = await context.Workspaces.FindAsync(new object[] { id }, cancellationToken);
        if (workspace != null)
        {
            context.Workspaces.Remove(workspace);
            await context.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<AgentProfile?> GetEffectiveAgentProfileAsync(int? workspaceId, string stageId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stageId);

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        if (workspaceId.HasValue)
        {
            var dedicatedList = await context.AgentProfiles
                .AsNoTracking()
                .Where(p => p.WorkspaceId == workspaceId.Value)
                .ToListAsync(cancellationToken);

            var dedicated = dedicatedList.FirstOrDefault(p =>
                string.Equals(p.Id, stageId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(p.Id, $"ws-{workspaceId.Value}:{stageId}", StringComparison.OrdinalIgnoreCase) ||
                (p.StageDirectives != null && p.StageDirectives.ContainsKey(stageId)));

            if (dedicated != null)
            {
                return dedicated;
            }
        }

        var globalList = await context.AgentProfiles
            .AsNoTracking()
            .Where(p => p.WorkspaceId == null)
            .ToListAsync(cancellationToken);

        return globalList.FirstOrDefault(p =>
            string.Equals(p.Id, stageId, StringComparison.OrdinalIgnoreCase) ||
            (p.StageDirectives != null && p.StageDirectives.ContainsKey(stageId)));
    }

    public async Task<IReadOnlyList<AgentProfile>> GetGlobalAgentProfilesAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.AgentProfiles
            .AsNoTracking()
            .Where(p => p.WorkspaceId == null)
            .ToListAsync(cancellationToken);
    }
}
