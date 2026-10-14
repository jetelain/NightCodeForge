using Microsoft.EntityFrameworkCore;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Repositories;

namespace NightCodeForge.Infrastructure.Data.Repositories;

/// <summary>
/// EF Core SQLite implementation of IWorkspaceRepository.
/// </summary>
public class EfCoreWorkspaceRepository : IWorkspaceRepository
{
    private readonly AppDbContext _context;

    public EfCoreWorkspaceRepository(AppDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public async Task<Workspace?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        return await _context.Workspaces
            .Include(w => w.AgentProfiles)
            .Include(w => w.Tasks)
            .FirstOrDefaultAsync(w => w.Id == id, cancellationToken);
    }

    public async Task<IReadOnlyList<Workspace>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await _context.Workspaces
            .Include(w => w.AgentProfiles)
            .ToListAsync(cancellationToken);
    }

    public async Task<Workspace> CreateAsync(Workspace workspace, CancellationToken cancellationToken = default)
    {
        _context.Workspaces.Add(workspace);
        await _context.SaveChangesAsync(cancellationToken);
        return workspace;
    }

    public async Task UpdateAsync(Workspace workspace, CancellationToken cancellationToken = default)
    {
        _context.Workspaces.Update(workspace);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        var workspace = await _context.Workspaces.FindAsync(new object[] { id }, cancellationToken);
        if (workspace != null)
        {
            _context.Workspaces.Remove(workspace);
            await _context.SaveChangesAsync(cancellationToken);
        }
    }
}
