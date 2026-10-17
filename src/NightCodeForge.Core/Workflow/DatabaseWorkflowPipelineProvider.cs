using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Exceptions;
using NightCodeForge.Core.Repositories;

namespace NightCodeForge.Core.Workflow;

/// <summary>
/// Database-driven pipeline provider constructing dynamic IWorkflowPipeline sequences
/// from persisted WorkflowDefinition and StageDefinition metadata.
/// </summary>
public class DatabaseWorkflowPipelineProvider : IWorkflowPipelineProvider
{
    private readonly IWorkflowRepository _workflowRepo;
    private readonly IWorkflowStageFactory _stageFactory;

    public DatabaseWorkflowPipelineProvider(IWorkflowRepository workflowRepo, IWorkflowStageFactory stageFactory)
    {
        _workflowRepo = workflowRepo ?? throw new ArgumentNullException(nameof(workflowRepo));
        _stageFactory = stageFactory ?? throw new ArgumentNullException(nameof(stageFactory));
    }

    public async Task<IWorkflowPipeline> GetPipelineAsync(ForgeTask task, CancellationToken cancellationToken = default)
    {
        if (task == null) throw new ArgumentNullException(nameof(task));

        WorkflowDefinition? workflow = null;
        if (task.WorkflowId.HasValue && task.WorkflowId.Value > 0)
        {
            workflow = await _workflowRepo.GetByIdWithStagesAsync(task.WorkflowId.Value, cancellationToken);
            if (workflow == null)
            {
                throw new WorkflowNotFoundException(task.WorkflowId.Value);
            }
        }
        else
        {
            workflow = await _workflowRepo.GetDefaultWorkflowWithStagesAsync(cancellationToken);
        }

        if (workflow == null || workflow.Stages.Count == 0)
        {
            throw new WorkflowNotFoundException("Default workflow is not seeded or contains no stages.");
        }

        if (!task.WorkflowId.HasValue || task.WorkflowId.Value <= 0)
        {
            task.WorkflowId = workflow.Id;
        }

        var stages = workflow.Stages
            .OrderBy(s => s.SequenceOrder)
            .Select(_stageFactory.CreateStage)
            .ToList();

        return new WorkflowPipeline(stages);
    }
}
