# 🔌 Step 8.3: Extensible C# Stage Registry, Generic Agentic Engine & Scriban Templating

> **Technical Specification & Implementation Guide**  
> *Parent Plan:* [docs/plan/08_DATABASE_DRIVEN_WORKFLOWS_AND_STAGES.md](../08_DATABASE_DRIVEN_WORKFLOWS_AND_STAGES.md)  
> *Status:* Ready for Implementation

---

## 1. Objective & Architectural Rationale

Build the extensible stage registry and factory engine that maps string identifiers stored in `StageDefinition.CsharpImplementationType` to concrete C# `IWorkflowStage` instances, resolving services from DI and hydrating strongly-typed configurations (`AgenticStageConfig` and `DeterministicStageConfig`).

### Single `"agentic"` Type + Generic Agentic Stage + Scriban Templating
Rather than maintaining separate C# classes for every agentic stage or inventing arbitrary `"agentic-*"` subtypes:
1. **Deterministic Stages** remain discrete C# classes (or composite/script runners) because they execute CLI commands, parse tool XML/TRX outputs, evaluate metric thresholds, and ratchet warnings.
2. **Agentic Stages** are 100% data-driven and unified under the single implementation type **`"agentic"`**, instantiated via **`GenericAgenticStage`**. Their persona instructions, tool modes, loopback targets, and input prompts are defined entirely in [AgenticStageConfig](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Workflow/Configuration/AgenticStageConfig.cs).
3. **Input Prompts** are rendered via **Scriban**, allowing whitespace-trimmed conditionals (`{{- if context.reviewer_feedback }}`) that avoid empty sections and trailing newlines, without hardcoded C# branching.

---

## 2. Package Dependency

Add the lightweight, zero-dependency templating engine to `NightCodeForge.Core`:
```xml
<PackageReference Include="Scriban" Version="5.12.0" />
```

---

## 3. Core Components & Implementations

### 3.1 `IPromptTemplateRenderer` & `ScribanPromptTemplateRenderer`
New templating service in `src/NightCodeForge.Core/Workflow/`:

```csharp
namespace NightCodeForge.Core.Workflow;

public interface IPromptTemplateRenderer
{
    string Render(string templateText, object model);
}

public class ScribanPromptTemplateRenderer : IPromptTemplateRenderer
{
    public string Render(string templateText, object model)
    {
        if (string.IsNullOrWhiteSpace(templateText))
        {
            return string.Empty;
        }

        var template = Scriban.Template.Parse(templateText);
        if (template.HasErrors)
        {
            var errors = string.Join("; ", template.Messages.Select(m => m.Message));
            throw new InvalidOperationException($"Failed parsing Scriban prompt template: {errors}");
        }

        return template.Render(model, member => member.Name);
    }
}
```

---

### 3.2 `GenericAgenticStage.cs`
Universal agentic stage in `src/NightCodeForge.Core/Workflow/Stages/GenericAgenticStage.cs`:

```csharp
namespace NightCodeForge.Core.Workflow.Stages;

using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Workflow.Configuration;

/// <summary>
/// Universal data-driven agentic stage executing an LLM persona configured via AgenticStageConfig.
/// </summary>
public class GenericAgenticStage : IWorkflowStage
{
    public string StageId { get; }
    public string DisplayName { get; }
    public WorkflowStageKind Kind => WorkflowStageKind.Agentic;
    public AgentMode RequiredMode { get; }
    public string StageDirective { get; set; }
    public string? PromptTemplate { get; set; }
    public string? AgentProfileId { get; }
    public string? LoopBackStageId { get; }
    public int MaxRetries { get; }
    public int? TimeoutMinutes { get; }

    public Func<PipelineContext, CancellationToken, Task<StageExecutionResult>>? Handler { get; set; }

    public GenericAgenticStage(
        string stageId,
        string displayName,
        AgenticStageConfig config)
    {
        StageId = stageId;
        DisplayName = displayName;
        RequiredMode = config.RequiredMode;
        StageDirective = config.StageDirective ?? string.Empty;
        PromptTemplate = config.PromptTemplate;
        AgentProfileId = config.AgentProfileId;
        LoopBackStageId = config.LoopBackStageId;
        MaxRetries = config.MaxRetries;
        TimeoutMinutes = config.TimeoutMinutes;
    }

    public async Task<StageExecutionResult> ExecuteAsync(PipelineContext context, CancellationToken cancellationToken = default)
    {
        if (Handler != null)
        {
            return await Handler(context, cancellationToken);
        }
        return StageExecutionResult.Success();
    }
}
```

---

### 3.3 `IWorkflowStageRegistry` & `WorkflowStageRegistry.cs`
Defines and registers stage factories:

```csharp
namespace NightCodeForge.Core.Workflow;

public interface IWorkflowStageRegistry
{
    void RegisterHandler(string implementationType, Func<IServiceProvider, StageDefinition, IWorkflowStage> factory);
    bool TryGetFactory(string implementationType, out Func<IServiceProvider, StageDefinition, IWorkflowStage>? factory);
    IReadOnlyCollection<string> RegisteredTypes { get; }
}

public class WorkflowStageRegistry : IWorkflowStageRegistry
{
    private readonly ConcurrentDictionary<string, Func<IServiceProvider, StageDefinition, IWorkflowStage>> _factories =
        new(StringComparer.OrdinalIgnoreCase);

    public WorkflowStageRegistry()
    {
        RegisterDefaults();
    }

    public void RegisterHandler(string implementationType, Func<IServiceProvider, StageDefinition, IWorkflowStage> factory)
    {
        _factories[implementationType] = factory;
    }

    public bool TryGetFactory(string implementationType, out Func<IServiceProvider, StageDefinition, IWorkflowStage>? factory)
    {
        return _factories.TryGetValue(implementationType, out factory);
    }

    public IReadOnlyCollection<string> RegisteredTypes => _factories.Keys.ToList();

    private void RegisterDefaults()
    {
        // Single unified agentic stage type
        RegisterHandler("agentic", (sp, def) =>
        {
            var config = JsonSerializer.Deserialize<AgenticStageConfig>(def.ConfigurationJson, JsonOptions.Default)
                         ?? new AgenticStageConfig();
            return new GenericAgenticStage(def.StageId, def.DisplayName, config);
        });

        // Deterministic stages
        RegisterHandler("deterministic-build", (sp, def) => new DeterministicBuildStage());
        RegisterHandler("deterministic-test", (sp, def) => new DeterministicTestStage());
        RegisterHandler("deterministic-coverage", (sp, def) => new DeterministicCoverageGate());
        RegisterHandler("deterministic-sast", (sp, def) => new DeterministicSastStage());
        RegisterHandler("deterministic-ratchet", (sp, def) => new DeterministicWarningRatchetStage());
        RegisterHandler("deterministic-composite", (sp, def) => ActivatorUtilities.CreateInstance<CompositeDeterministicStage>(sp, def));
        RegisterHandler("deterministic-script", (sp, def) => ActivatorUtilities.CreateInstance<DeterministicScriptStage>(sp, def));
    }
}
```

---

### 3.4 `IWorkflowStageFactory` & `WorkflowStageFactory.cs`
Resolves stages from `StageDefinition`:

```csharp
namespace NightCodeForge.Core.Workflow;

public interface IWorkflowStageFactory
{
    IWorkflowStage CreateStage(StageDefinition definition);
}

public class WorkflowStageFactory : IWorkflowStageFactory
{
    private readonly IWorkflowStageRegistry _registry;
    private readonly IServiceProvider _serviceProvider;

    public WorkflowStageFactory(IWorkflowStageRegistry registry, IServiceProvider serviceProvider)
    {
        _registry = registry;
        _serviceProvider = serviceProvider;
    }

    public IWorkflowStage CreateStage(StageDefinition definition)
    {
        if (!_registry.TryGetFactory(definition.CsharpImplementationType, out var factory) || factory == null)
        {
            var available = string.Join(", ", _registry.RegisteredTypes);
            throw new InvalidOperationException(
                $"Unknown stage C# implementation type '{definition.CsharpImplementationType}' for stage '{definition.StageId}'. " +
                $"Available registered types: [{available}]");
        }

        return factory(_serviceProvider, definition);
    }
}
```

---

### 3.5 `TaskOrchestrator.cs` Prompt Rendering Integration
In `TaskOrchestrator.BuildBootstrapPrompt`:
1. Check if the stage has a `PromptTemplate` (e.g. from `GenericAgenticStage`).
2. If present, pass a structured model to `IPromptTemplateRenderer`:
   ```csharp
   var model = new
   {
       task = new { id = task.Id, title = task.Title, description = task.Description },
       stage = new { id = stage.StageId, display_name = stage.DisplayName },
       context = new
       {
           task_markdown_path = context.DataBag.Get<string>("TaskMarkdownPath"),
           approved_plan = context.DataBag.Get<string>("Stage1.ApprovedPlan") ?? context.DataBag.Get<string>("StageSummary_task-review"),
           reviewer_feedback = context.DataBag.Get<string>("ReviewerFeedback"),
           implementation_summary = context.DataBag.Get<string>("StageSummary_implementation"),
           compiler_errors = context.DataBag.Get<List<string>>("diagnostics.compiler_errors"),
           answered_questions = task.Questions?
               .Where(q => q.Status == QuestionStatus.Answered && !string.IsNullOrWhiteSpace(q.Answer))
               .Select(q => new { prompt = q.Prompt, answer = q.Answer })
               .ToList()
       }
   };
   ```
3. If no `PromptTemplate` is defined, fallback to the legacy concatenation format for backward compatibility.

---

## 4. Verification Criteria
1. **Scriban Rendering Tests**:
   - Verify prompt sections with whitespace trimming (`{{-` and `-}}`) omit empty sections without leftover newlines.
   - Verify loops (`for qa in answered_questions`) render properly.
2. **Factory & Registry Tests**:
   - Successfully instantiate `"agentic"` and deterministic stages.
   - Unknown types throw a descriptive `InvalidOperationException` with registered alternatives.
3. **End-to-End Compatibility**:
   - The seeded default workflow stages (`task-review`, `implementation`, and `code-review-fix`) resolve via `"agentic"` into `GenericAgenticStage` and render the exact expected bootstrap prompts.
