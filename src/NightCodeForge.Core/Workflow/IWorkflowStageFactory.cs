namespace NightCodeForge.Core.Workflow;

using NightCodeForge.Core.Domain;

/// <summary>
/// Factory resolving concrete IWorkflowStage instances from StageDefinition metadata and DI.
/// </summary>
public interface IWorkflowStageFactory
{
    /// <summary>
    /// Creates an executable IWorkflowStage instance for the given StageDefinition.
    /// </summary>
    /// <param name="definition">Database-driven stage definition entity.</param>
    /// <returns>Instantiated and configured workflow stage.</returns>
    /// <exception cref="InvalidOperationException">Thrown if implementation type is not registered.</exception>
    IWorkflowStage CreateStage(StageDefinition definition);
}
