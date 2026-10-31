# 🔄 Step 8.4: Database-Driven Pipeline Provider & Orchestrator Integration

> **Technical Specification & Implementation Guide**  
> *Parent Plan:* [docs/plan/08_DATABASE_DRIVEN_WORKFLOWS_AND_STAGES.md](../08_DATABASE_DRIVEN_WORKFLOWS_AND_STAGES.md)  
> *Status:* Completed

---

## 1. Objective

Integrate the dynamic database workflow system into the core execution engine: update `IWorkflowPipelineProvider` to build pipelines dynamically from database definitions, update `TaskOrchestrator` to execute them, and support dynamic loopback and retry targets.

---

## 2. Key Architecture Components

### 2.1 `IWorkflowRepository` & Implementation
Create repository contract and implementations:
- `IWorkflowRepository.cs` in `NightCodeForge.Core.Repositories`
- `EfCoreWorkflowRepository.cs` in `NightCodeForge.Infrastructure.Data.Repositories`
- `InMemoryWorkflowRepository.cs` in `NightCodeForge.Core.Repositories` (for testing)

Methods:
- `Task<WorkflowDefinition?> GetByIdWithStagesAsync(int workflowId, CancellationToken cancellationToken = default);`
- `Task<WorkflowDefinition?> GetDefaultWorkflowWithStagesAsync(CancellationToken cancellationToken = default);`
- `Task<IReadOnlyList<WorkflowDefinition>> GetWorkflowsAsync(int? workspaceId = null, CancellationToken cancellationToken = default);`
- `Task<WorkflowDefinition> CreateWorkflowAsync(WorkflowDefinition workflow, CancellationToken cancellationToken = default);`
- `Task UpdateWorkflowAsync(WorkflowDefinition workflow, CancellationToken cancellationToken = default);`
- `Task DeleteWorkflowAsync(int workflowId, CancellationToken cancellationToken = default);`

### 2.2 `DatabaseWorkflowPipelineProvider`
Replace `DefaultWorkflowPipelineProvider` with a dynamic provider:
```csharp
public class DatabaseWorkflowPipelineProvider : IWorkflowPipelineProvider
{
    private readonly IWorkflowRepository _workflowRepo;
    private readonly IWorkflowStageFactory _stageFactory;

    public DatabaseWorkflowPipelineProvider(IWorkflowRepository workflowRepo, IWorkflowStageFactory stageFactory)
    {
        _workflowRepo = workflowRepo;
        _stageFactory = stageFactory;
    }

    public async Task<IWorkflowPipeline> GetPipelineAsync(ForgeTask task, CancellationToken cancellationToken = default)
    {
        WorkflowDefinition? workflow = null;
        if (task.WorkflowId > 0)
        {
            workflow = await _workflowRepo.GetByIdWithStagesAsync(task.WorkflowId, cancellationToken);
        }

        if (workflow == null)
        {
            workflow = await _workflowRepo.GetDefaultWorkflowWithStagesAsync(cancellationToken);
        }

        if (workflow == null || workflow.Stages.Count == 0)
        {
            throw new InvalidOperationException($"No workflow pipeline available for task #{task.Id}.");
        }

        var stages = workflow.Stages
            .OrderBy(s => s.SequenceOrder)
            .Select(_stageFactory.CreateStage)
            .ToList();

        return new WorkflowPipeline(stages);
    }
}
```

### 2.3 `TaskOrchestrator` Alignment
1. **Pipeline Resolution**:
   - Resolve pipeline via `await _pipelineProvider.GetPipelineAsync(task, cancellationToken)`.
   - Update `task.WorkflowId` if it was previously unset.
2. **Dynamic Stage Progression**:
   - Use `pipeline.GetNextStage(task.CurrentStageId)`.
3. **Dynamic Loopbacks**:
   - Instead of hardcoded fallback to `"implementation"`, read `stageDefinition.ConfigurationJson` -> `LoopBackStageId`.
   - On change requests or quality gate failure, orchestrator routes the task directly to the configured loopback target.

---

## 3. Verification Criteria
1. Tasks assigned to a 3-stage custom workflow execute only those 3 stages in order.
2. Tasks assigned to the standard 6-stage workflow execute the full autonomous pipeline.
3. Code review rejection loops back to the dynamically configured `LoopBackStageId`.
4. Unit and integration tests in `TaskOrchestratorDynamicWorkflowTests` verify execution flow and state transitions.
