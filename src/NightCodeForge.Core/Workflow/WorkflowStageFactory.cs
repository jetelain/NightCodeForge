namespace NightCodeForge.Core.Workflow;

using NightCodeForge.Core.Domain;

/// <summary>
/// Resolves and instantiates IWorkflowStage instances using the configured IWorkflowStageRegistry.
/// </summary>
public class WorkflowStageFactory : IWorkflowStageFactory
{
    private readonly IWorkflowStageRegistry _registry;
    private readonly IServiceProvider _serviceProvider;

    public WorkflowStageFactory(IWorkflowStageRegistry registry, IServiceProvider serviceProvider)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    }

    public IWorkflowStage CreateStage(StageDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (!Enum.IsDefined(typeof(StageQueueTier), definition.QueueTier) || (int)definition.QueueTier == 0)
        {
            throw new Exceptions.WorkflowValidationException(
                $"Stage '{definition.StageId}' does not have a designated QueueTier. Workflow stages must define a valid QueueTier.");
        }

        if (!_registry.TryGetFactory(definition.CsharpImplementationType, out var factory) || factory == null)
        {
            var available = string.Join(", ", _registry.RegisteredTypes);
            throw new InvalidOperationException(
                $"Unknown stage C# implementation type '{definition.CsharpImplementationType}' for stage '{definition.StageId}'. " +
                $"Available registered types: [{available}]");
        }

        var stage = factory(_serviceProvider, definition);
        stage.QueueTier = definition.QueueTier;
        return stage;
    }
}
