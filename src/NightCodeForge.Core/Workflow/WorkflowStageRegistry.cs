namespace NightCodeForge.Core.Workflow;

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Workflow.Configuration;
using NightCodeForge.Core.Workflow.Stages;

/// <summary>
/// Default implementation of IWorkflowStageRegistry supporting unified 'agentic' and discrete deterministic stages.
/// </summary>
public class WorkflowStageRegistry : IWorkflowStageRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly ConcurrentDictionary<string, Func<IServiceProvider, StageDefinition, IWorkflowStage>> _factories =
        new(StringComparer.OrdinalIgnoreCase);

    public WorkflowStageRegistry()
    {
        RegisterDefaults();
    }

    public void RegisterHandler(string implementationType, Func<IServiceProvider, StageDefinition, IWorkflowStage> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(implementationType);
        ArgumentNullException.ThrowIfNull(factory);
        _factories[implementationType] = factory;
    }

    public bool TryGetFactory(string implementationType, out Func<IServiceProvider, StageDefinition, IWorkflowStage>? factory)
    {
        if (string.IsNullOrWhiteSpace(implementationType))
        {
            factory = null;
            return false;
        }

        return _factories.TryGetValue(implementationType, out factory);
    }

    public IReadOnlyCollection<string> RegisteredTypes => _factories.Keys.ToList();

    private void RegisterDefaults()
    {
        // 1. Unified data-driven agentic stage type
        RegisterHandler("agentic", (sp, def) =>
        {
            var config = !string.IsNullOrWhiteSpace(def.ConfigurationJson)
                ? JsonSerializer.Deserialize<AgenticStageConfig>(def.ConfigurationJson, JsonOptions) ?? new AgenticStageConfig()
                : new AgenticStageConfig();
            var renderer = sp.GetService<IPromptTemplateRenderer>();
            return new GenericAgenticStage(def.StageId, def.DisplayName, config, renderer);
        });

        // 2. Deterministic discrete quality gate stages
        RegisterHandler("deterministic-build", (sp, def) => ActivatorUtilities.CreateInstance<DeterministicBuildStage>(sp));
        RegisterHandler("deterministic-test", (sp, def) => ActivatorUtilities.CreateInstance<DeterministicTestStage>(sp));
        RegisterHandler("deterministic-coverage", (sp, def) => ActivatorUtilities.CreateInstance<DeterministicCoverageGate>(sp));
        RegisterHandler("deterministic-sast", (sp, def) => ActivatorUtilities.CreateInstance<DeterministicSastStage>(sp));
        RegisterHandler("deterministic-ratchet", (sp, def) => ActivatorUtilities.CreateInstance<DeterministicWarningRatchetStage>(sp));
        RegisterHandler("deterministic-composite", (sp, def) => new CompositeDeterministicStage(def, sp, this));
        RegisterHandler("deterministic-script", (sp, def) => new DeterministicScriptStage(def, sp));
    }
}
