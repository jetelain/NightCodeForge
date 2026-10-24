using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Workflow;

/// <summary>
/// Provider interface for obtaining the workflow pipeline appropriate for a given task or workspace.
/// </summary>
public interface IWorkflowPipelineProvider
{
    Task<IWorkflowPipeline> GetPipelineAsync(ForgeTask task, CancellationToken cancellationToken = default);
}

/// <summary>
/// Pipeline provider returning an in-memory configured pipeline (used in tests and fallback scenarios).
/// </summary>
public class DefaultWorkflowPipelineProvider : IWorkflowPipelineProvider
{
    private readonly IWorkflowPipeline _pipeline;

    public DefaultWorkflowPipelineProvider()
        : this(CreateDefaultPipeline())
    {
    }

    public DefaultWorkflowPipelineProvider(IWorkflowPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        _pipeline = pipeline;
    }

    public Task<IWorkflowPipeline> GetPipelineAsync(ForgeTask task, CancellationToken cancellationToken = default)
        => Task.FromResult(_pipeline);

    private static IWorkflowPipeline CreateDefaultPipeline()
    {
        var standardWorkflow = StandardWorkflowDefinitions.CreateStandard3AgentWorkflow();
        var stages = standardWorkflow.Stages
            .OrderBy(s => s.SequenceOrder)
            .Select(s => (IWorkflowStage)new DefaultStage
            {
                StageId = s.StageId,
                DisplayName = s.DisplayName,
                Kind = s.Kind,
                QueueTier = s.QueueTier
            });

        return new WorkflowPipeline(stages);
    }

    private sealed class DefaultStage : IWorkflowStage
    {
        public string StageId { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public WorkflowStageKind Kind { get; init; } = WorkflowStageKind.Agentic;
        public StageQueueTier QueueTier { get; set; } = StageQueueTier.Implementation;

        public Task<StageExecutionResult> ExecuteAsync(PipelineContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(StageExecutionResult.Success());
    }
}
