# 💉 Step 9.6: Required Constructor DI & Fail-Fast Architecture

> **Phase 9 Implementation Step**  
> *Parent Plan:* [Phase 9 Master Plan](../09_CODEBASE_REMEDIATION_AND_ROBUSTNESS.md)  
> *Source Policy:* [AGENTS.md](../../../AGENTS.md)  
> *Status:* Completed | *Target Framework:* .NET 10 (C# 13)

---

## 1. Objective

Enforce strict dependency injection across all service constructors. Eliminate optional parameters (`= null`, `= default`), silent fallback object instantiations (`?? new FallbackService()`), and fallback loggers (`?? NullLogger...`). Make all dependencies required, add `ArgumentNullException.ThrowIfNull(...)`, fix the silent-success bug in deterministic quality gates when execution environments are missing, and introduce a centralized `TestOrchestratorBuilder` fixture helper so unit tests remain clean and maintainable.

---

## 2. Technical Scope & Specifications

### 2.1 Core Services & Quality Gates Refactoring
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

2. **Deterministic Quality Gates (`DeterministicBuildStage`, `DeterministicTestStage`, `DeterministicCoverageGate`, `DeterministicSastStage`, `DeterministicScriptStage`):**
   * Require `IExecutionEnvironmentProvider` explicitly in stage constructors.
   * Eliminate silent success fallbacks: if `effectiveEnv` cannot be resolved for a workspace, throw `InvalidOperationException` rather than returning a synthetic success.

3. **`StartupRecoveryService` & `TaskMarkdownSyncService` (`NightCodeForge.Core.Services`):**
   * Require all dependencies explicitly.

### 2.2 Agent Harnesses & Services Refactoring
1. **`CrucibleAgentHarness` (`NightCodeForge.Agents.Crucible`):**
   * Require all core services: `IWorkspacePathResolver`, `IOllamaChatClientFactory`, `IExecutionEnvironmentProvider`, `IMcpClientManager`, `IEditorConfigFileApplier`, `IWebSearchService`, `IWorkspaceSymbolService`, `SkillsService`, `AgentsMdService`, `IAgentActivityStreamer`, `SessionTranscriptService`, `IGlobalOptimizationSettingsService`, `ITaskRepository`, `IOptions<LlmProviderOptions>`, `ILogger<CrucibleAgentHarness>`, `IOllamaModelPreloadService`.
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
  * Inject non-null dependencies and fail fast.

---

## 3. Test Fixture Enhancement: Dedicated Test Builders

Centralized test builder fixtures were implemented across test assemblies:
- `TestOrchestratorBuilder` (`NightCodeForge.Core.Tests.Fixtures`)
- `TestExecutionEnvironmentProviderBuilder` (`NightCodeForge.Infrastructure.Tests.Fixtures`)
- `TestCrucibleHarnessBuilder`, `TestAcpHarnessBuilder`, `TestPiRpcHarnessBuilder`, `TestIdeationServiceBuilder`, `TestTriageServiceBuilder`, `TestCanonicalToolsBuilder` (`NightCodeForge.Agents.Tests.Fixtures`)
- `FakePreloadService` (`NightCodeForge.Core.Tests.Fixtures`)

---

## 4. Verification & Testing

* **Static Constructor Scan:** Verified 0 optional service parameters and 0 silent fallback instantiations across all service classes.
* **Unit & Integration Suite (`dotnet test --filter "Category!=Acceptance&Category!=E2E"`):**
  * `NightCodeForge.Core.Tests`: 186 passed, 0 failed
  * `NightCodeForge.Agents.Tests`: 268 passed, 0 failed
  * `NightCodeForge.Infrastructure.Tests`: 179 passed, 0 failed
  * `NightCodeForge.Web.Tests`: 261 passed, 0 failed
* **Full End-to-End Suite (`dotnet test`):**
  * 896 tests total passed across all 4 test projects (including Playwright E2E browser tests), 0 failed, 0 skipped.
