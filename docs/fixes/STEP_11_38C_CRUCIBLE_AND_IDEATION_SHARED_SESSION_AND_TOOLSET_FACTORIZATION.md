# Step 11.38C — Shared Agent Session & Toolset Factorization for `CrucibleAgentHarness`, `IdeationService` & `CanonicalTools`

**Status:** Pending (Fix / Code Factorization & Constructor Diet)
**Prerequisite:** Step 11.38B

---

## 1. Problem Statement

[`CrucibleAgentHarness`](../../src/NightCodeForge.Agents/Crucible/CrucibleAgentHarness.cs) (2,022 lines, **23 constructor parameters**), [`IdeationService`](../../src/NightCodeForge.Agents/Ideation/IdeationService.cs) (919 lines, **19 constructor parameters**), and [`CanonicalTools`](../../src/NightCodeForge.Agents/Tools/CanonicalTools.cs) (931 lines, **13 constructor parameters**) suffer from severe constructor bloat and duplicated session-assembly logic:

1. **Manual `CanonicalTools` Construction Forces 6–7 Pass-Through Dependencies:**
   - Both `CrucibleAgentHarness` and `IdeationService` manually instantiate `new CanonicalTools(...)`.
   - Simply to call that constructor, both classes must inject `IWorkspacePathResolver`, `IExecutionEnvironmentProvider`, `IEditorConfigService`, `IEditorConfigFileApplier`, `IWebSearchService`, `IWorkspaceSkillsServiceFactory`, and `ILogger<CanonicalTools>`, even though neither class uses `IEditorConfigService`, `IEditorConfigFileApplier`, `IWebSearchService`, or `ILogger<CanonicalTools>` directly.
   - `CanonicalTools` itself has a 13-parameter constructor mixing 6 DI services with 7 runtime scope values (`workspaceRoot`, `buildTimeoutSeconds`, `testTimeoutSeconds`, `workspaceId`, `taskId`, `isReadOnly`).
2. **Duplicated Workspace Context & Skills Discovery Across `CrucibleAgentHarness`, `IdeationService` & `TriageService`:**
   - `CrucibleAgentHarness` and `IdeationService` duplicate `AgentsMdService.IngestRulesAsync` + `FormatProjectContextSection` (`CodeStackPresets.ResolveConfig`) and conditional `load_skill` catalog discovery (`IWorkspaceSkillsService.GetActiveSkillsAsync` / `GetSkillsCatalogPromptAsync` when `permittedTools` contains `load_skill`).
   - `IdeationService` and `TriageService` duplicate working-clone / repository path resolution (`Ext4MirrorPath`, `GetAgentWorkingClonePath`) and `ITaskMarkdownSyncService.SyncAllTasksToDiskAsync` pre-syncing.
3. **Duplicated `HarnessAgent` & Compaction Pipeline Assembly:**
   - Both `CrucibleAgentHarness` and `IdeationService` duplicate model resolution (`ILlmChatClientFactory`, `ChatClientMetadata.DefaultModelId`), `ModelCapabilityResolver.CreateChatOptions`, dynamic context window discovery via `ILlmGatewayService.ResolveContextWindowAsync`, `ModelCapabilityResolver.BuildCompactionStrategy`, `HarnessAgentOptions` construction (with identical provider disable flags), and `ThinkingAndActivityTracingChatClient` + `LenientToolCallChatClient` middleware wrapping.
4. **Monolithic Conversation Resumption, Priming & Transcript Persistence in `CrucibleAgentHarness`:**
   - Over 550 lines of `CrucibleAgentHarness` are dedicated to database `Conversation` initialization/resumption, sequential `TurnWriter` persistence, synthetic priming turn injection, `PendingHumanAnswer` `ask` tool-call completion, and `SaveTranscriptAsync` / `BuildTranscriptEntries`.

---

## 2. Architectural Goals

1. **Slim `CanonicalTools` (`13 -> <= 8` Constructor Parameters) & Introduce `ICanonicalToolsetFactory`:**
   - Encapsulate runtime scope values into a `CanonicalToolScope` record and introduce an `ICanonicalToolsetFactory` (`CanonicalToolsetFactory`) that resolves the execution environment, skills service, `CanonicalTools`, `LenientToolDispatcher`, custom/MCP tool registrations, and `IList<AITool>`.
2. **Extract `IAgentWorkspaceContextService` (`AgentWorkspaceContextService`):**
   - Factorize workspace repository/clone path resolution, backlog markdown pre-syncing, `AGENTS.md` + `CodeStackPresets` project context formatting, conditional `load_skill` catalog loading, and deterministic symbol map discovery across `CrucibleAgentHarness`, `IdeationService`, and `TriageService`.
3. **Extract `IHarnessAgentFactory` (`HarnessAgentFactory`):**
   - Factorize `IChatClient` resolution, `ChatOptions` creation, dynamic context window discovery (`ILlmGatewayService`), `PiStyleCrucibleCompactionStrategy` construction, and `HarnessAgentOptions` assembly shared between `CrucibleAgentHarness` and `IdeationService`.
4. **Extract `ICrucibleConversationCoordinator` (`CrucibleConversationCoordinator`):**
   - Move database conversation initialization/resumption, `TurnWriter`, synthetic conversation priming, `PendingHumanAnswer` `ask` tool-call completion, and transcript persistence out of `CrucibleAgentHarness`.
5. **Enforce `<= 10` Constructor Parameters on All Three Classes:**
   - Reduce `CrucibleAgentHarness` from **23 to `<= 8` parameters**, `IdeationService` from **19 to `<= 8` parameters**, and `CanonicalTools` from **13 to `<= 8` parameters**, and remove all three from `LegacyConstructorDebtAllowlist` in `ConstructorParameterCeilingTests`.

---

## 3. Required Specification & Behavioral Contracts

### 3.1 `CanonicalToolScope` & `ICanonicalToolsetFactory`

1. **`CanonicalToolScope`:**
   - Immutable record holding per-session scope values: `string WorkspaceRoot`, `int? WorkspaceId = null`, `int? TaskId = null`, `int? BuildTimeoutSeconds = null`, `int? TestTimeoutSeconds = null`, `bool IsReadOnly = false`.
2. **`CanonicalTools` Constructor Diet (`<= 8` Parameters):**
   - Replace the 13-parameter constructor with a constructor accepting `CanonicalToolScope` plus required collaborator services (or group file-formatting dependencies into `IEditorConfigFileApplier`), ensuring `CanonicalTools` has at most 8 constructor parameters.
3. **`ICanonicalToolsetFactory` (`CanonicalToolsetFactory`):**
   - Constructor has `<= 8` required dependencies (`IWorkspacePathResolver`, `IExecutionEnvironmentProvider`, `IEditorConfigService`, `IEditorConfigFileApplier`, `IWebSearchService`, `IWorkspaceSkillsServiceFactory`, `IMcpServerManager`, `ILoggerFactory`).
   - Builds a `CanonicalToolsetSession` containing the configured `CanonicalTools` instance, `LenientToolDispatcher`, `IWorkspaceSkillsService`, and `IList<AITool>` for a given `Workspace`, `AgentProfile`, `CanonicalToolScope`, and permitted/custom tools.

### 3.2 `IAgentWorkspaceContextService` (`AgentWorkspaceContextService`)

- Centralizes:
  1. Resolving `(string RepoPath, string? CloneDir, string AgentWorkingDir)` for a `Workspace` and optional task ID across `BareHost`, `WslDirect`, and `ContainerSandbox` modes.
  2. Pre-syncing backlog task markdown files to disk (`ITaskMarkdownSyncService.SyncAllTasksToDiskAsync`) for ideation and triage sessions.
  3. Building the project context section (`AgentsMdService.IngestRulesAsync` + `FormatProjectContextSection` + `CodeStackPresets.ResolveConfig`), conditional skills catalog (`load_skill` check), and optional deterministic symbol map (`ISymbolDiscoveryService`).
- Constructor has `<= 7` required parameters.

### 3.3 `IHarnessAgentFactory` (`HarnessAgentFactory`)

- Centralizes:
  1. Resolving the base `IChatClient` (`ILlmChatClientFactory`) and effective model ID.
  2. Creating `ChatOptions` (`ModelCapabilityResolver.CreateChatOptions`) and applying optional grammar constraints or default temperature.
  3. Discovering the effective context window via `ILlmGatewayService.ResolveContextWindowAsync` when `profile.ContextWindowTokens` is not explicitly set.
  4. Building the compaction strategy (`ModelCapabilityResolver.BuildCompactionStrategy`) and `HarnessAgentOptions` with standard provider disable flags.
- Constructor has `<= 6` required parameters.

### 3.4 `ICrucibleConversationCoordinator` (`CrucibleConversationCoordinator`)

- Encapsulates Crucible stage conversation lifecycle and turn persistence:
  1. Resolving or starting the database `Conversation` (`WaitingForHuman`, `Interrupted`, or new attempt) and restoring `List<ChatMessage>` history.
  2. Managing ordered, non-concurrent `ConversationTurn` writes (`TurnWriter`) and deduplicating tool call IDs (`writtenToolCallIds`).
  3. Applying synthetic conversation priming (`SyntheticPriming`) and completing pending `ask` tool calls on human answer resumption (`PendingHumanAnswer`).
  4. Persisting session transcripts (`SessionTranscriptService`) and completing the database `Conversation` (`CompleteConversationAsync`).
- Constructor has `<= 5` required parameters.

### 3.5 Strict Constructor Rules (`AGENTS.md`)

- Every new and refactored service constructor in `src/` must require all its dependencies (no optional parameters or `= null` defaults; fail fast with `ArgumentNullException.ThrowIfNull`).
- Update `TestCrucibleHarnessBuilder`, `TestIdeationServiceBuilder`, and `TestCanonicalToolsBuilder` in `tests/NightCodeForge.Agents.Tests/Fixtures/` so existing tests remain clean and concise.
- Remove `CrucibleAgentHarness`, `IdeationService`, and `CanonicalTools` from `LegacyConstructorDebtAllowlist` in `ConstructorParameterCeilingTests`.

---

## 4. Verification Criteria

- `CrucibleAgentHarness`, `IdeationService`, `CanonicalTools`, and all newly extracted services (`CanonicalToolsetFactory`, `AgentWorkspaceContextService`, `HarnessAgentFactory`, `CrucibleConversationCoordinator`) each have **`<= 10` constructor parameters** (target `<= 8`).
- `CrucibleAgentHarness`, `IdeationService`, and `CanonicalTools` are removed from `LegacyConstructorDebtAllowlist` in `ConstructorParameterCeilingTests`.
- Dedicated unit tests for `CanonicalToolsetFactory`, `AgentWorkspaceContextService`, `HarnessAgentFactory`, and `CrucibleConversationCoordinator` pass.
- Full test suite passes.

