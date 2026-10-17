namespace NightCodeForge.Core.Workflow;

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
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
        RegisterHandler(StageImplementationTypes.Agentic, (sp, def) =>
        {
            var config = !string.IsNullOrWhiteSpace(def.ConfigurationJson)
                ? JsonSerializer.Deserialize<AgenticStageConfig>(def.ConfigurationJson, JsonOptions) ?? new AgenticStageConfig()
                : new AgenticStageConfig();
            var renderer = sp.GetRequiredService<IPromptTemplateRenderer>();
            return new GenericAgenticStage(def.StageId, def.DisplayName, config, renderer);
        });

        // 2. Deterministic discrete quality gate stages
        RegisterHandler(StageImplementationTypes.DeterministicBuild, (sp, def) => ActivatorUtilities.CreateInstance<DeterministicBuildStage>(sp));
        RegisterHandler(StageImplementationTypes.DeterministicTest, (sp, def) =>
        {
            var env = sp.GetRequiredService<IExecutionEnvironmentProvider>();
            var logger = sp.GetService<Microsoft.Extensions.Logging.ILogger<DeterministicTestStage>>()
                ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<DeterministicTestStage>.Instance;
            return new DeterministicTestStage(env, logger);
        });
        RegisterHandler(StageImplementationTypes.DeterministicCoverage, (sp, def) => ActivatorUtilities.CreateInstance<DeterministicCoverageGate>(sp));
        RegisterHandler(StageImplementationTypes.DeterministicSast, (sp, def) => ActivatorUtilities.CreateInstance<DeterministicSastStage>(sp));
        RegisterHandler(StageImplementationTypes.DeterministicRatchet, (sp, def) => ActivatorUtilities.CreateInstance<DeterministicWarningRatchetStage>(sp));
        RegisterHandler(StageImplementationTypes.DeterministicComposite, (sp, def) => new CompositeDeterministicStage(def, sp, this));
        RegisterHandler(StageImplementationTypes.DeterministicScript, (sp, def) => new DeterministicScriptStage(def, sp));
    }
}
