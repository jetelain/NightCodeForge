using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;

namespace NightCodeForge.Core.Workflow;

/// <summary>
/// Provider interface for obtaining the workflow pipeline appropriate for a given task or workspace.
/// </summary>
public interface IWorkflowPipelineProvider
{
    Task<IWorkflowPipeline> GetPipelineAsync(ForgeTask task, CancellationToken cancellationToken = default);
    IWorkflowPipeline GetPipeline(ForgeTask task) => GetPipelineAsync(task).GetAwaiter().GetResult();
}

/// <summary>
/// Pipeline provider returning an in-memory configured pipeline (used in tests and fallback scenarios).
/// </summary>
public class DefaultWorkflowPipelineProvider : IWorkflowPipelineProvider
{
    private readonly IWorkflowPipeline _pipeline;

    public DefaultWorkflowPipelineProvider(
        IWorkflowPipeline? pipeline = null,
        IExecutionEnvironmentProvider? envProvider = null,
        IExecutionEnvironment? executionEnv = null,
        IOllamaModelPreloadService? preloadService = null)
    {
        _pipeline = pipeline ?? Default3AgentPipeline.Create(
            envProvider: envProvider,
            executionEnv: executionEnv,
            preloadService: preloadService);
    }

    public Task<IWorkflowPipeline> GetPipelineAsync(ForgeTask task, CancellationToken cancellationToken = default)
        => Task.FromResult(_pipeline);

    public IWorkflowPipeline GetPipeline(ForgeTask task) => _pipeline;
}
