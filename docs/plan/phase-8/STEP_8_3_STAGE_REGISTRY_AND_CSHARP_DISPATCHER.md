# 🔌 Step 8.3: Extensible C# Stage Registry & Factory Engine

> **Technical Specification & Implementation Guide**  
> *Parent Plan:* [docs/plan/08_DATABASE_DRIVEN_WORKFLOWS_AND_STAGES.md](../08_DATABASE_DRIVEN_WORKFLOWS_AND_STAGES.md)  
> *Status:* Ready for Implementation

---

## 1. Objective

Build the extensible stage registry and factory engine that maps string identifiers stored in `StageDefinition.CsharpImplementationType` to concrete C# `IWorkflowStage` instances, resolving services from DI and hydrating strongly-typed configurations (`AgenticStageConfig` and `DeterministicStageConfig`).

---

## 2. Core Interfaces & Implementations

### 2.1 `IWorkflowStageRegistry` & `IWorkflowStageFactory`
```csharp
namespace NightCodeForge.Core.Workflow;

public interface IWorkflowStageFactory
{
    IWorkflowStage CreateStage(StageDefinition definition);
}

public interface IWorkflowStageRegistry
{
    void RegisterHandler(string implementationType, Func<IServiceProvider, StageDefinition, IWorkflowStage> factory);
    bool TryGetFactory(string implementationType, out Func<IServiceProvider, StageDefinition, IWorkflowStage>? factory);
    IReadOnlyCollection<string> RegisteredTypes { get; }
}
```

### 2.2 `WorkflowStageRegistry.cs` (Implementation)
- Maintains a thread-safe dictionary: `ConcurrentDictionary<string, Func<IServiceProvider, StageDefinition, IWorkflowStage>>`.
- Default registrations:
  - `"agentic-review"` -> `TaskReviewStage`
  - `"agentic-impl"` -> `ImplementationStage`
  - `"agentic-review-fix"` -> `CodeReviewAndFixStage`
  - `"agentic-custom"` -> `GenericAgenticStage`
  - `"deterministic-build"` -> `DeterministicBuildStage`
  - `"deterministic-test"` -> `DeterministicTestStage`
  - `"deterministic-coverage"` -> `DeterministicCoverageGate`
  - `"deterministic-sast"` -> `DeterministicSastStage`
  - `"deterministic-ratchet"` -> `DeterministicWarningRatchetStage`
  - `"deterministic-composite"` -> `CompositeDeterministicStage`
  - `"deterministic-script"` -> `DeterministicScriptStage`

### 2.3 `WorkflowStageFactory.cs` (Implementation)
- Takes `StageDefinition`.
- Looks up the factory in `IWorkflowStageRegistry`.
- Deserializes `definition.ConfigurationJson` into the expected config type.
- Binds parameters (agent profile ID, stage directive override, required mode, loopback stage ID, CLI commands, timeouts).
- Emits friendly, actionable error messages if an unknown implementation type is provided.

### 2.4 New Generic & Composite Stages
- **`GenericAgenticStage.cs`**: Allows any arbitrary agent role/prompt to be executed as an agentic stage without writing a dedicated C# class.
- **`CompositeDeterministicStage.cs`**: Executes a pipeline of child deterministic stages (e.g. Build + Test + Coverage) sequentially.
- **`DeterministicScriptStage.cs`**: Executes a host shell script or container binary with arguments and environment variables specified in `ExtraParameters`.

---

## 3. Verification Criteria
1. Factory successfully instantiates all registered stage types from test `StageDefinition` records.
2. Deserialization handles empty, minimal, and fully populated JSON payloads gracefully.
3. Unknown implementation types throw a descriptive `InvalidOperationException` detailing the missing key and available registered keys.
