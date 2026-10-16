using Microsoft.EntityFrameworkCore;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Repositories;

namespace NightCodeForge.Infrastructure.Data.Repositories;

/// <summary>
/// EF Core SQLite implementation of IWorkflowRepository.
/// </summary>
public class EfCoreWorkflowRepository : IWorkflowRepository
{
    private readonly AppDbContext _context;

    public EfCoreWorkflowRepository(AppDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public async Task<WorkflowDefinition?> GetByIdWithStagesAsync(int workflowId, CancellationToken cancellationToken = default)
    {
        return await _context.WorkflowDefinitions
            .Include(w => w.Stages.OrderBy(s => s.SequenceOrder))
            .FirstOrDefaultAsync(w => w.Id == workflowId, cancellationToken);
    }

    public async Task<WorkflowDefinition?> GetDefaultWorkflowWithStagesAsync(CancellationToken cancellationToken = default)
    {
        var def = await _context.WorkflowDefinitions
            .Include(w => w.Stages.OrderBy(s => s.SequenceOrder))
            .FirstOrDefaultAsync(w => w.IsDefault, cancellationToken);

        if (def == null)
        {
            def = await _context.WorkflowDefinitions
                .Include(w => w.Stages.OrderBy(s => s.SequenceOrder))
                .FirstOrDefaultAsync(w => w.Code == "standard-3-agent", cancellationToken);
        }

        return def;
    }

    public async Task<IReadOnlyList<WorkflowDefinition>> GetWorkflowsAsync(int? workspaceId = null, CancellationToken cancellationToken = default)
    {
        IQueryable<WorkflowDefinition> query = _context.WorkflowDefinitions
            .Include(w => w.Stages.OrderBy(s => s.SequenceOrder));

        if (workspaceId.HasValue)
        {
            query = query.Where(w => w.WorkspaceId == workspaceId.Value || w.WorkspaceId == null);
        }

        return await query.OrderBy(w => w.Id).ToListAsync(cancellationToken);
    }

    public async Task<WorkflowDefinition> CreateWorkflowAsync(WorkflowDefinition workflow, CancellationToken cancellationToken = default)
    {
        if (workflow == null) throw new ArgumentNullException(nameof(workflow));

        _context.WorkflowDefinitions.Add(workflow);
        await _context.SaveChangesAsync(cancellationToken);
        return workflow;
    }

    public async Task UpdateWorkflowAsync(WorkflowDefinition workflow, CancellationToken cancellationToken = default)
    {
        if (workflow == null) throw new ArgumentNullException(nameof(workflow));

        var existing = _context.WorkflowDefinitions.Local.FirstOrDefault(entry => entry.Id == workflow.Id)
            ?? await _context.WorkflowDefinitions
                .Include(w => w.Stages)
                .FirstOrDefaultAsync(w => w.Id == workflow.Id, cancellationToken);

        if (existing != null)
        {
            if (!ReferenceEquals(existing, workflow))
            {
                _context.Entry(existing).CurrentValues.SetValues(workflow);
            }
        }
        else
        {
            _context.WorkflowDefinitions.Update(workflow);
        }

        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteWorkflowAsync(int workflowId, CancellationToken cancellationToken = default)
    {
        var workflow = await _context.WorkflowDefinitions
            .Include(w => w.Stages)
            .FirstOrDefaultAsync(w => w.Id == workflowId, cancellationToken);

        if (workflow != null)
        {
            _context.WorkflowDefinitions.Remove(workflow);
            await _context.SaveChangesAsync(cancellationToken);
        }
    }
}
