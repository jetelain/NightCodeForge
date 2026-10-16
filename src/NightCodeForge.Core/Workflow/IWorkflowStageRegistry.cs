namespace NightCodeForge.Core.Workflow;

using NightCodeForge.Core.Domain;

/// <summary>
/// Registry mapping C# implementation type keys to stage factory delegates.
/// </summary>
public interface IWorkflowStageRegistry
{
    /// <summary>
    /// Registers a custom stage factory delegate for a given implementation type identifier.
    /// </summary>
    void RegisterHandler(string implementationType, Func<IServiceProvider, StageDefinition, IWorkflowStage> factory);

    /// <summary>
    /// Attempts to retrieve the factory delegate registered for the specified implementation type identifier.
    /// </summary>
    bool TryGetFactory(string implementationType, out Func<IServiceProvider, StageDefinition, IWorkflowStage>? factory);

    /// <summary>
    /// Gets all registered implementation type identifiers.
    /// </summary>
    IReadOnlyCollection<string> RegisteredTypes { get; }
}
