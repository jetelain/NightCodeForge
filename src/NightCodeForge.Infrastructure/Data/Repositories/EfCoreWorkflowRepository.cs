using Microsoft.EntityFrameworkCore;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Repositories;

namespace NightCodeForge.Infrastructure.Data.Repositories;

/// <summary>
/// EF Core SQLite implementation of IWorkflowRepository creating short-lived AppDbContext instances on demand.
/// </summary>
public class EfCoreWorkflowRepository : IWorkflowRepository
{
    private readonly IDbContextFactory<AppDbContext> _contextFactory;

    public EfCoreWorkflowRepository(IDbContextFactory<AppDbContext> contextFactory)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
    }

    public async Task<WorkflowDefinition?> GetByIdWithStagesAsync(int workflowId, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.WorkflowDefinitions
            .AsNoTracking()
            .Include(w => w.Stages.OrderBy(s => s.SequenceOrder))
            .FirstOrDefaultAsync(w => w.Id == workflowId, cancellationToken);
    }

    public async Task<WorkflowDefinition?> GetDefaultWorkflowWithStagesAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var def = await context.WorkflowDefinitions
            .AsNoTracking()
            .Include(w => w.Stages.OrderBy(s => s.SequenceOrder))
            .FirstOrDefaultAsync(w => w.IsDefault, cancellationToken);

        if (def == null)
        {
            def = await context.WorkflowDefinitions
                .AsNoTracking()
                .Include(w => w.Stages.OrderBy(s => s.SequenceOrder))
                .FirstOrDefaultAsync(w => w.Code == StandardWorkflowDefinitions.Standard3AgentCode, cancellationToken);
        }

        return def;
    }

    public async Task<IReadOnlyList<WorkflowDefinition>> GetWorkflowsAsync(int? workspaceId = null, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        IQueryable<WorkflowDefinition> query = context.WorkflowDefinitions
            .AsNoTracking()
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

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        context.WorkflowDefinitions.Add(workflow);
        await context.SaveChangesAsync(cancellationToken);
        return workflow;
    }

    public async Task UpdateWorkflowAsync(WorkflowDefinition workflow, CancellationToken cancellationToken = default)
    {
        if (workflow == null) throw new ArgumentNullException(nameof(workflow));

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var existing = await context.WorkflowDefinitions
            .Include(w => w.Stages)
            .FirstOrDefaultAsync(w => w.Id == workflow.Id, cancellationToken);

        if (existing != null)
        {
            context.Entry(existing).CurrentValues.SetValues(workflow);
            if (workflow.Stages != null)
            {
                var incomingStages = workflow.Stages.ToList();
                var matchedExistingIds = new HashSet<int>();

                foreach (var stage in incomingStages)
                {
                    var existingStage = existing.Stages.FirstOrDefault(s =>
                        (stage.Id > 0 && s.Id == stage.Id) ||
                        string.Equals(s.StageId, stage.StageId, StringComparison.OrdinalIgnoreCase));
                    if (existingStage != null)
                    {
                        matchedExistingIds.Add(existingStage.Id);
                        stage.Id = existingStage.Id;
                        stage.WorkflowId = existing.Id;
                        context.Entry(existingStage).CurrentValues.SetValues(stage);
                    }
                    else
                    {
                        stage.WorkflowId = existing.Id;
                        stage.Workflow = existing;
                        context.StageDefinitions.Add(stage);
                    }
                }

                if (incomingStages.Count > 0)
                {
                    var stagesToRemove = existing.Stages
                        .Where(s => s.Id > 0 && !matchedExistingIds.Contains(s.Id))
                        .ToList();
                    if (stagesToRemove.Count > 0)
                    {
                        context.StageDefinitions.RemoveRange(stagesToRemove);
                    }
                }
            }
        }
        else
        {
            context.WorkflowDefinitions.Update(workflow);
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteWorkflowAsync(int workflowId, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var workflow = await context.WorkflowDefinitions
            .Include(w => w.Stages)
            .FirstOrDefaultAsync(w => w.Id == workflowId, cancellationToken);

        if (workflow != null)
        {
            context.WorkflowDefinitions.Remove(workflow);
            await context.SaveChangesAsync(cancellationToken);
        }
    }
}
