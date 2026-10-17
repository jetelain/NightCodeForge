# 💉 Step 9.13: Infrastructure Constructor DI & Options Remediation

> **Phase 9 Implementation Step**  
> *Parent Plan:* [Phase 9 Master Plan](../09_CODEBASE_REMEDIATION_AND_ROBUSTNESS.md)  
> *Source Policy:* [AGENTS.md](../../../AGENTS.md)  
> *Status:* Planned | *Target Framework:* .NET 10 (C# 13)

---

## 1. Problem

While Step 9.6 eliminated constructor parameter defaults (`= null`) in Core and deterministic stages, an audit of `src/NightCodeForge.Infrastructure/` and `src/NightCodeForge.Agents/` revealed residual optional parameters, fallback instantiations (`?? new ...`, `?? NullLogger...`), and overloaded constructors chaining to internal defaults.

Per [AGENTS.md](../../../AGENTS.md):
> "In service constructors, avoid using optional parameters. You should require all services you need in order to work properly. Avoid trying to have a fallback strategy if a service is not available. If a service is not available, it's better to let the application fail fast rather than silently using a default value that might not be appropriate for the use case."

---

## 2. Findings

| # | Location | Issue | Severity |
| :-- | :--- | :--- | :--- |
| C1 | [`WslMetadataService.cs`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Infrastructure/Execution/WslMetadataService.cs#L28-L38) | Constructor has optional parameters `options = null`, `isWindowsOverride = null`, `logger = null`, and silent fallback `?? new ExecutionEnvironmentProviderOptions()`, `?? NullLogger...`. | High |
| C2 | [`WslExecutionEnvironment.cs`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Infrastructure/Execution/WslExecutionEnvironment.cs#L24-L39) | Constructor has fallback `_processRunner = processRunner ?? new SystemProcessRunner()` and secondary constructor chaining to `new SystemProcessRunner()`. | High |
| C3 | [`WorkspacePathResolver.cs`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Infrastructure/Execution/WorkspacePathResolver.cs#L23-L35) | All 5 constructor parameters default to `= null`, with fallback `?? new ExecutionEnvironmentProviderOptions()`. | High |
| C4 | [`NightCodeForgeContainerExecutor.cs`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Infrastructure/Execution/NightCodeForgeContainerExecutor.cs#L217-L225) | `options = null`, `processRunner = null`, `logger = null` with silent `?? new ContainerExecutorOptions()`, `?? new SystemProcessRunner()`, `?? NullLogger...`. | High |
| C5 | [`HostSdkDiscoveryService.cs`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Infrastructure/Execution/HostSdkDiscoveryService.cs#L78-L99) | Overloaded constructors with `wslMetadataService = null`, `options = null`, `logger = null`, and `?? new ExecutionEnvironmentProviderOptions()`. | High |
| C6 | [`GenericAgenticStage.cs`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Workflow/Stages/GenericAgenticStage.cs#L30-L42) | Constructor parameter `IPromptTemplateRenderer? renderer = null` with fallback `?? new ScribanPromptTemplateRenderer()`. | Medium |
| C7 | [`CompositeDeterministicStage.cs`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Workflow/Stages/CompositeDeterministicStage.cs#L28-L51) | `IWorkflowStageRegistry? registry = null` with fallback `?? serviceProvider.GetService<IWorkflowStageRegistry>() ?? new WorkflowStageRegistry()`. | Medium |
| C8 | [`GlobalOptimizationSettingsService.cs`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Infrastructure/Data/GlobalOptimizationSettingsService.cs#L24-L30) & [`WebSearchSettingsService.cs`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Infrastructure/Data/WebSearchSettingsService.cs#L25-L35) | `ILogger? logger = null` and `IConfiguration? configuration = null` with silent null guards. | Medium |
| C9 | [`OllamaWebSearchService.cs`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Agents/Tools/OllamaWebSearchService.cs#L25-L38) | Optional `settingsService = null`, `logger = null`, `explicitApiKey = null`, `explicitEndpoint = null`. | Medium |
| C10 | [`OllamaRouterGatewayService.cs`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Infrastructure/Inference/OllamaRouterGatewayService.cs), `CatalogService`, `PreloadService` | `IOptions<OllamaRouterOptions>? options = null` and `logger = null` across router infrastructure services. | Medium |
| C11 | [`ThinkingTraceCompactionChatClient.cs`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Agents/Optimizations/ThinkingTraceCompactionChatClient.cs#L20-L28) & [`ToolResultCompactor.cs`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Agents/Optimizations/ToolResultCompactor.cs#L25-L33) | `flags = null`, `keepVerbatimCount = null` with `?? new LocalOptimizationFlags()`. | Low |
| C12 | [`TaskMarkdownSyncService.cs`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Services/TaskMarkdownSyncService.cs#L32-L71) | 4 constructor overloads maintaining dual `_fallbackTaskRepository`, `_fallbackDependencyEngine`, `_scopeFactory` with internal `ResolveDependencies` fallback branch. | Medium |
| C13 | [`ServiceCollectionExtensions.cs`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Infrastructure/ServiceCollectionExtensions.cs#L25-L75) | Does not register `ExecutionEnvironmentProviderOptions` or `ContainerExecutorOptions` in DI (`services.AddOptions<...>()`), forcing constructors to use optional fallbacks. | High |

---

## 3. Deliverables

1. **Service Registration Hygiene (`ServiceCollectionExtensions.cs`):**
   - Register and configure default options: `services.AddOptions<ExecutionEnvironmentProviderOptions>()` and `services.AddOptions<ContainerExecutorOptions>()`.
   - Ensure all injected options and loggers are provided by the container.
2. **Infrastructure Execution Services:**
   - [`WslMetadataService`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Infrastructure/Execution/WslMetadataService.cs): Require `IProcessRunner`, `IOptions<ExecutionEnvironmentProviderOptions>`, and `ILogger<WslMetadataService>`. Remove `= null` defaults and `?? new ...` / `?? NullLogger...`.
   - [`WslExecutionEnvironment`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Infrastructure/Execution/WslExecutionEnvironment.cs): Require `IProcessRunner`. Remove `?? new SystemProcessRunner()` and delete default constructor overload.
   - [`WorkspacePathResolver`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Infrastructure/Execution/WorkspacePathResolver.cs): Require `IWslMetadataService` and `IOptions<ExecutionEnvironmentProviderOptions>`. Remove all `= null` defaults.
   - [`NightCodeForgeContainerExecutor`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Infrastructure/Execution/NightCodeForgeContainerExecutor.cs): Require `IOptions<ContainerExecutorOptions>`, `IProcessRunner`, and `ILogger<NightCodeForgeContainerExecutor>`. Remove optional fallbacks.
   - [`HostSdkDiscoveryService`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Infrastructure/Execution/HostSdkDiscoveryService.cs): Require `IProcessRunner`, `IWslMetadataService`, `IOptions<ExecutionEnvironmentProviderOptions>`, and `ILogger<HostSdkDiscoveryService>`.
3. **Workflow Stages:**
   - [`GenericAgenticStage`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Workflow/Stages/GenericAgenticStage.cs): Require `IPromptTemplateRenderer`. Remove optional parameter and `?? new ScribanPromptTemplateRenderer()`.
   - [`CompositeDeterministicStage`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Workflow/Stages/CompositeDeterministicStage.cs): Require `IWorkflowStageRegistry`. Remove `?? serviceProvider.GetService...`.
4. **Agents & Settings Services:**
   - Require non-null dependencies in `GlobalOptimizationSettingsService`, `WebSearchSettingsService`, `OllamaWebSearchService`, `OllamaRouterGatewayService`, `OllamaModelCatalogService`, `OllamaModelPreloadService`.
5. **Clean Architecture in [`TaskMarkdownSyncService`](file:///c:/Users/Julien/source/repos/jetelain/NightCodeForge/src/NightCodeForge.Core/Services/TaskMarkdownSyncService.cs):**
   - Eliminate redundant fallback constructors. Consolidate to single required constructor accepting `IServiceScopeFactory` (for worker/singleton contexts) and a dedicated constructor for unit testing.
6. **Tests Update:**
   - Update tests that called defaulted constructors to pass required mocks/options via `Options.Create(...)` or test builders.

---

## 4. Definition of Done (DoD) Checklist

- [ ] Zero optional parameters (`= null`, `= default`) in service constructors across `NightCodeForge.Infrastructure` and `NightCodeForge.Agents`.
- [ ] Zero fallback initializations (`?? new ...`, `?? NullLogger...`) in service constructors.
- [ ] `ExecutionEnvironmentProviderOptions` and `ContainerExecutorOptions` configured in DI.
- [ ] `TaskMarkdownSyncService` has zero fallback shims.
- [ ] `dotnet test --filter "Category!=Acceptance&Category!=E2E"` passes with zero failures.
