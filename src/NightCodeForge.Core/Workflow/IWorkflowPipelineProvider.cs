using NightCodeForge.Core.Domain;

namespace NightCodeForge.Core.Workflow;

/// <summary>
/// Provider interface for obtaining the workflow pipeline appropriate for a given issue or workspace.
/// </summary>
public interface IWorkflowPipelineProvider
{
    IWorkflowPipeline GetPipeline(Issue issue);
}

/// <summary>
/// Default pipeline provider returning the standard 3-agent pipeline.
/// </summary>
public class DefaultWorkflowPipelineProvider : IWorkflowPipelineProvider
{
    private readonly IWorkflowPipeline _pipeline;

    public DefaultWorkflowPipelineProvider(IWorkflowPipeline? pipeline = null)
    {
        _pipeline = pipeline ?? Default3AgentPipeline.Create();
    }

    public IWorkflowPipeline GetPipeline(Issue issue) => _pipeline;
}
