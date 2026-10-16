# 💉 Step 9.4: Required Constructor DI & Fail-Fast Architecture

> **Phase 9 Implementation Step**  
> *Parent Plan:* [Phase 9 Master Plan](../09_CODEBASE_REMEDIATION_AND_ROBUSTNESS.md)  
> *Source Policy:* [AGENTS.md](../../../AGENTS.md)  
> *Status:* Planned | *Target Framework:* .NET 10 (C# 13)

---

## 1. Objective

Enforce strict dependency injection across all service constructors. Eliminate optional parameters (`= null`, `= default`), silent fallback object instantiations (`?? new FallbackService()`), and fallback loggers (`?? NullLogger...`). Make all dependencies required, add `ArgumentNullException.ThrowIfNull(...)`, and introduce a centralized `TestOrchestratorBuilder` fixture helper so unit tests remain clean and maintainable.

---

## 2. Technical Scope & Specifications

### 2.1 Core Services Refactoring
1. **`TaskOrchestrator` (`NightCodeForge.Core.Workflow`):**
   * Make all 14 parameters required:
     * `ITaskRepository`
     * `IWorkspaceRepository`
     * `IDependencyResolutionEngine`
     * `IWorkspaceLockProvider`
     * `IGitWorkspaceManager`
     * `IWorkflowPipelineProvider`
     * `ITaskJobQueue`
     * `ITaskDependencyStore`
     * `IHarnessRegistry`
     * `IGitMirrorSyncService`
     * `ITaskMarkdownSyncService`
     * `IWorkspacePathResolver`
     * `IAgentActivityStreamer`
     * `ILogger<TaskOrchestrator>`
   * Remove `= null` defaults and `?? NullLogger...`.
   * Add `ArgumentNullException.ThrowIfNull` for all parameters.
   * Remove silent `if (_harnessRegistry != null)` null-guards.

2. **`StartupRecoveryService` & `TaskMarkdownSyncService` (`NightCodeForge.Core.Services`):**
   * Require all dependencies explicitly.

### 2.2 Agent Harnesses & Services Refactoring
1. **`CrucibleAgentHarness` (`NightCodeForge.Agents.Crucible`):**
   * Require all core services: `IWorkspacePathResolver`, `IOllamaChatClientFactory`, `IExecutionEnvironmentProvider`, `IMcpClientManager`, `IEditorConfigFileApplier`, `IWebSearchService`, `IWorkspaceSymbolService`, `SkillsService`, `AgentsMdService`, `IAgentActivityStreamer`, `SessionTranscriptService`, `IGlobalOptimizationSettingsService`, `ITaskRepository`, `IOptions<LlmProviderOptions>`, `ILogger<CrucibleAgentHarness>`.
   * Remove silent fallbacks: `new MockWebSearchService()`, `new SkillsService()`, etc.
2. **`AcpAgentHarness` & `PiRpcAgentHarness` (`NightCodeForge.Agents`):**
   * Require options, execution environment, streamer, transcripts, path resolver, process runner, and logger explicitly without `= null`.
3. **`IdeationService` & `TriageService` (`NightCodeForge.Agents`):**
   * Require all services explicitly.
4. **`CanonicalTools` (`NightCodeForge.Agents.Tools`):**
   * Require `IWorkspacePathResolver`, `IExecutionEnvironment`, `IEditorConfigService`, `IEditorConfigFileApplier`, `IWebSearchService`, and `ILogger<CanonicalTools>` without fallback mock instantiation.

### 2.3 Infrastructure Services Refactoring
1. **`ExecutionEnvironmentProvider` (`NightCodeForge.Infrastructure.Execution`):**
   * Consolidate into a single required constructor: `BareHostExecutionEnvironment`, `IProcessRunner`, `IOptions<ExecutionEnvironmentProviderOptions>`, `IWorkspacePathResolver`, `IHostSdkDiscoveryService`, `IGitWorkspaceManager`, `ILogger<NightCodeForgeContainerExecutor>`.
   * Eliminate fallback `_processRunner = processRunner ?? new SystemProcessRunner();`.
2. **`GitCliRunner`, `GitWorkspaceManager`, `GitMirrorSyncService` (`NightCodeForge.Infrastructure.Git`):**
   * Require path resolver, WSL metadata service, alert notifier, and logger explicitly.

### 2.4 Web Layer Services Refactoring
* In `DevQueueService`, `GitSyncService`, `TaskReviewService`, `TranscriptBrowserService`, `WorkspaceProvisioningService`:
  * Remove all optional constructor parameters and null guards.

---

## 3. Test Fixture Enhancement: `TestOrchestratorBuilder`

To prevent repetitive 14-parameter wiring across tests, create a dedicated test builder in `NightCodeForge.Core.Tests`:

```csharp
namespace NightCodeForge.Core.Tests.Fixtures;

public class TestOrchestratorBuilder
{
    public ITaskRepository TaskRepository { get; set; } = new InMemoryTaskRepository();
    public IWorkspaceRepository WorkspaceRepository { get; set; } = new InMemoryWorkspaceRepository();
    public IDependencyResolutionEngine DependencyEngine { get; set; } = new DependencyResolutionEngine();
    public IWorkspaceLockProvider LockProvider { get; set; } = new WorkspaceLockProvider();
    public IGitWorkspaceManager GitWorkspaceManager { get; set; } = new Mock<IGitWorkspaceManager>().Object;
    public IWorkflowPipelineProvider PipelineProvider { get; set; } = new TestWorkflowPipelineProvider();
    public ITaskJobQueue JobQueue { get; set; } = new InMemoryTaskJobQueue();
    public ITaskDependencyStore DependencyStore { get; set; } = new InMemoryTaskDependencyStore();
    public IHarnessRegistry HarnessRegistry { get; set; } = new HarnessRegistry();
    public IGitMirrorSyncService GitMirrorSyncService { get; set; } = new Mock<IGitMirrorSyncService>().Object;
    public ITaskMarkdownSyncService MarkdownSyncService { get; set; } = new Mock<ITaskMarkdownSyncService>().Object;
    public IWorkspacePathResolver PathResolver { get; set; } = new Mock<IWorkspacePathResolver>().Object;
    public IAgentActivityStreamer Streamer { get; set; } = new InMemoryAgentActivityStreamer();
    public ILogger<TaskOrchestrator> Logger { get; set; } = NullLogger<TaskOrchestrator>.Instance;

    public TaskOrchestrator Build() => new(
        TaskRepository,
        WorkspaceRepository,
        DependencyEngine,
        LockProvider,
        GitWorkspaceManager,
        PipelineProvider,
        JobQueue,
        DependencyStore,
        HarnessRegistry,
        GitMirrorSyncService,
        MarkdownSyncService,
        PathResolver,
        Streamer,
        Logger);
}
```

---

## 4. Verification & Testing

* Execute static constructor scan to verify 0 optional service parameters.
* Execute unit and integration tests: `dotnet test --filter "Category!=Acceptance&Category!=E2E"`.
* Run full test suite including end-to-end acceptance tests: `dotnet test`.
