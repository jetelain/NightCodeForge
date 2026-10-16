using System.Collections.Concurrent;
using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Repositories;

/// <summary>
/// Thread-safe in-memory implementation of IWorkflowRepository for unit and integration testing.
/// </summary>
public class InMemoryWorkflowRepository : IWorkflowRepository
{
    private readonly ConcurrentDictionary<int, WorkflowDefinition> _workflows = new();
    private int _nextId = 1;

    public InMemoryWorkflowRepository(IEnumerable<WorkflowDefinition>? initialWorkflows = null)
    {
        if (initialWorkflows != null)
        {
            foreach (var wf in initialWorkflows)
            {
                if (wf.Id <= 0)
                {
                    wf.Id = _nextId++;
                }
                else if (wf.Id >= _nextId)
                {
                    _nextId = wf.Id + 1;
                }
                _workflows[wf.Id] = CloneWorkflow(wf);
            }
        }
    }

    public Task<WorkflowDefinition?> GetByIdWithStagesAsync(int workflowId, CancellationToken cancellationToken = default)
    {
        if (_workflows.TryGetValue(workflowId, out var wf))
        {
            return Task.FromResult<WorkflowDefinition?>(CloneWorkflow(wf));
        }

        return Task.FromResult<WorkflowDefinition?>(null);
    }

    public Task<WorkflowDefinition?> GetDefaultWorkflowWithStagesAsync(CancellationToken cancellationToken = default)
    {
        var def = _workflows.Values.FirstOrDefault(w => w.IsDefault)
            ?? _workflows.Values.FirstOrDefault(w => string.Equals(w.Code, "standard-3-agent", StringComparison.OrdinalIgnoreCase))
            ?? _workflows.Values.FirstOrDefault();

        return Task.FromResult(def != null ? CloneWorkflow(def) : null);
    }

    public Task<IReadOnlyList<WorkflowDefinition>> GetWorkflowsAsync(int? workspaceId = null, CancellationToken cancellationToken = default)
    {
        var list = _workflows.Values
            .Where(w => !workspaceId.HasValue || w.WorkspaceId == workspaceId.Value || w.WorkspaceId == null)
            .OrderBy(w => w.Id)
            .Select(CloneWorkflow)
            .ToList();

        return Task.FromResult<IReadOnlyList<WorkflowDefinition>>(list);
    }

    public Task<WorkflowDefinition> CreateWorkflowAsync(WorkflowDefinition workflow, CancellationToken cancellationToken = default)
    {
        if (workflow == null) throw new ArgumentNullException(nameof(workflow));

        if (workflow.Id <= 0)
        {
            workflow.Id = _nextId++;
        }
        else if (workflow.Id >= _nextId)
        {
            _nextId = workflow.Id + 1;
        }

        var cloned = CloneWorkflow(workflow);
        _workflows[cloned.Id] = cloned;
        return Task.FromResult(CloneWorkflow(cloned));
    }

    public Task UpdateWorkflowAsync(WorkflowDefinition workflow, CancellationToken cancellationToken = default)
    {
        if (workflow == null) throw new ArgumentNullException(nameof(workflow));

        var cloned = CloneWorkflow(workflow);
        _workflows[cloned.Id] = cloned;
        return Task.CompletedTask;
    }

    public Task DeleteWorkflowAsync(int workflowId, CancellationToken cancellationToken = default)
    {
        _workflows.TryRemove(workflowId, out _);
        return Task.CompletedTask;
    }

    private static WorkflowDefinition CloneWorkflow(WorkflowDefinition wf)
    {
        var clone = new WorkflowDefinition
        {
            Id = wf.Id,
            Name = wf.Name,
            Code = wf.Code,
            Icon = wf.Icon,
            Description = wf.Description,
            IsDefault = wf.IsDefault,
            WorkspaceId = wf.WorkspaceId,
            CreatedAt = wf.CreatedAt,
            UpdatedAt = wf.UpdatedAt,
            Stages = wf.Stages
                .OrderBy(s => s.SequenceOrder)
                .Select(s => new StageDefinition
                {
                    Id = s.Id,
                    WorkflowId = wf.Id,
                    StageId = s.StageId,
                    DisplayName = s.DisplayName,
                    Icon = s.Icon,
                    SequenceOrder = s.SequenceOrder,
                    Kind = s.Kind,
                    CsharpImplementationType = s.CsharpImplementationType,
                    ConfigurationJson = s.ConfigurationJson,
                    CreatedAt = s.CreatedAt
                })
                .ToList()
        };

        foreach (var stage in clone.Stages)
        {
            stage.Workflow = clone;
        }

        return clone;
    }
}
