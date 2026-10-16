using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Repositories;

/// <summary>
/// Repository interface for persisting and loading WorkflowDefinition and StageDefinition entities.
/// </summary>
public interface IWorkflowRepository
{
    Task<WorkflowDefinition?> GetByIdWithStagesAsync(int workflowId, CancellationToken cancellationToken = default);
    Task<WorkflowDefinition?> GetDefaultWorkflowWithStagesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WorkflowDefinition>> GetWorkflowsAsync(int? workspaceId = null, CancellationToken cancellationToken = default);
    Task<WorkflowDefinition> CreateWorkflowAsync(WorkflowDefinition workflow, CancellationToken cancellationToken = default);
    Task UpdateWorkflowAsync(WorkflowDefinition workflow, CancellationToken cancellationToken = default);
    Task DeleteWorkflowAsync(int workflowId, CancellationToken cancellationToken = default);
}
