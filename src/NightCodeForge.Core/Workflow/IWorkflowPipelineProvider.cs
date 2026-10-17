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
        : this(new WorkflowPipeline(Array.Empty<IWorkflowStage>()))
    {
    }

    public DefaultWorkflowPipelineProvider(IWorkflowPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        _pipeline = pipeline;
    }

    public Task<IWorkflowPipeline> GetPipelineAsync(ForgeTask task, CancellationToken cancellationToken = default)
        => Task.FromResult(_pipeline);
}
