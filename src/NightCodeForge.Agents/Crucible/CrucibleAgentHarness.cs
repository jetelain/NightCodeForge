using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NightCodeForge.Agents.Context;
using NightCodeForge.Agents.Crucible.ChatClients;
using NightCodeForge.Agents.Dispatcher;
using NightCodeForge.Agents.Inference;
using NightCodeForge.Agents.Mcp;
using NightCodeForge.Agents.Optimizations;
using NightCodeForge.Agents.Skills;
using NightCodeForge.Agents.Tools;
using NightCodeForge.Agents.Tools.EditorConfig;
using NightCodeForge.Agents.Transcripts;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Streaming;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Agents.Crucible;

public partial class CrucibleAgentHarness : IAgentHarness
{
    private readonly IOllamaChatClientFactory _chatClientFactory;
    private readonly IExecutionEnvironmentProvider _envProvider;
    private readonly IMcpClientManager _mcpManager;
    private readonly IEditorConfigService _editorConfigService;
    private readonly IEditorConfigFileApplier _editorConfigFileApplier;
    private readonly IWebSearchService _webSearchService;
    private readonly IWorkspaceSymbolService _symbolService;
    private readonly SkillsService _skillsService;
    private readonly AgentsMdService _agentsMdService;
    private readonly IAgentActivityStreamer _activityStreamer;
    private readonly SessionTranscriptService _transcriptService;
    private readonly NightCodeForge.Core.Repositories.ITaskRepository _taskRepository;
    private readonly IWorkspacePathResolver _pathResolver;
    private readonly IGlobalOptimizationSettingsService _globalOptimizationSettings;
    private readonly ILogger<CrucibleAgentHarness> _logger;
    private readonly ILogger<CanonicalTools> _canonicalToolsLogger;
    private readonly LlmProviderOptions _llmOptions;
    private readonly TimeProvider _timeProvider;

    public string HarnessId => StandardHarnessIds.Crucible;

    public CrucibleAgentHarness(
        IWorkspacePathResolver pathResolver,
        IOllamaChatClientFactory chatClientFactory,
        IExecutionEnvironmentProvider envProvider,
        IMcpClientManager mcpManager,
        IEditorConfigService editorConfigService,
        IEditorConfigFileApplier editorConfigFileApplier,
        IWebSearchService webSearchService,
        IWorkspaceSymbolService symbolService,
        SkillsService skillsService,
        AgentsMdService agentsMdService,
        IAgentActivityStreamer activityStreamer,
        SessionTranscriptService transcriptService,
        IGlobalOptimizationSettingsService globalOptimizationSettings,
        NightCodeForge.Core.Repositories.ITaskRepository taskRepository,
        Microsoft.Extensions.Options.IOptions<LlmProviderOptions> llmOptions,
        ILogger<CrucibleAgentHarness> logger,
        ILogger<CanonicalTools> canonicalToolsLogger,
        TimeProvider timeProvider)
    {
        _pathResolver = pathResolver ?? throw new ArgumentNullException(nameof(pathResolver));
        _chatClientFactory = chatClientFactory ?? throw new ArgumentNullException(nameof(chatClientFactory));
        _envProvider = envProvider ?? throw new ArgumentNullException(nameof(envProvider));
        _mcpManager = mcpManager ?? throw new ArgumentNullException(nameof(mcpManager));
        _editorConfigService = editorConfigService ?? throw new ArgumentNullException(nameof(editorConfigService));
        _editorConfigFileApplier = editorConfigFileApplier ?? throw new ArgumentNullException(nameof(editorConfigFileApplier));
        _webSearchService = webSearchService ?? throw new ArgumentNullException(nameof(webSearchService));
        _symbolService = symbolService ?? throw new ArgumentNullException(nameof(symbolService));
        _skillsService = skillsService ?? throw new ArgumentNullException(nameof(skillsService));
        _agentsMdService = agentsMdService ?? throw new ArgumentNullException(nameof(agentsMdService));
        _activityStreamer = activityStreamer ?? throw new ArgumentNullException(nameof(activityStreamer));
        _transcriptService = transcriptService ?? throw new ArgumentNullException(nameof(transcriptService));
        _globalOptimizationSettings = globalOptimizationSettings ?? throw new ArgumentNullException(nameof(globalOptimizationSettings));
        _taskRepository = taskRepository ?? throw new ArgumentNullException(nameof(taskRepository));
        ArgumentNullException.ThrowIfNull(llmOptions);
        _llmOptions = llmOptions.Value ?? throw new ArgumentNullException(nameof(llmOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _canonicalToolsLogger = canonicalToolsLogger ?? throw new ArgumentNullException(nameof(canonicalToolsLogger));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<AgentRunResult> RunStageAsync(AgentSessionContext context, CancellationToken cancellationToken = default)
    {
        context.OptimizationFlags = context.OptimizationFlags ?? context.Profile.OptimizationFlags ?? _globalOptimizationSettings?.GetFlags() ?? new LocalOptimizationFlags();
        var stopwatch = Stopwatch.StartNew();
        long promptTokens = 0;
        long completionTokens = 0;
        var sessionId = Guid.NewGuid().ToString("N");

        int attemptIndex = (context.PipelineContext?.StageRetries.GetValueOrDefault(context.StageId, 0) ?? 0) + 1;
        Conversation? conversation = null;
        if (_taskRepository != null)
        {
            try
            {
                conversation = await _taskRepository.StartConversationAsync(
                    context.ForgeTask.Id,
                    context.StageId,
                    WorkflowStageKind.Agentic,
                    attemptIndex,
                    context.Profile.Id,
                    HarnessId,
                    context.Profile.ModelName,
                    cancellationToken: cancellationToken);

                if (!string.IsNullOrWhiteSpace(context.BootstrapPrompt))
                {
                    await _taskRepository.AddConversationTurnAsync(
                        conversation.Id,
                        StageTurnRole.User,
                        content: context.BootstrapPrompt,
                        cancellationToken: cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to start stage conversation record for task #{TaskId}", context.ForgeTask.Id);
            }
        }

        // Announced once the conversation record exists so a live UI can find and follow it.
        await BroadcastAsync(sessionId, context.ForgeTask.Id, context.StageId, AgentActivityKind.StageStarted, $"Starting stage '{context.StageId}'", cancellationToken);

        var workspacePath = !string.IsNullOrEmpty(context.WorkspacePath)
            ? context.WorkspacePath
            : (context.Workspace?.RepositoryPath ?? Directory.GetCurrentDirectory());

        // 1. Parse database-driven HarnessConfigurationJson if provided
        var harnessConfig = ParseHarnessConfig(context.Profile.HarnessConfigurationJson);

        var workspace = context.Workspace ?? throw new InvalidOperationException("Workspace is required for agent stage execution.");
        var effectiveEnv = _envProvider.GetEnvironment(workspace, context.Profile.SecurityTier, context.ForgeTask.Id)
            ?? throw new InvalidOperationException($"No execution environment could be resolved for workspace #{workspace.Id}.");

        var projectConfig = CodeStackPresets.ResolveConfig(workspacePath, null);
        var buildTimeout = workspace.BuildTimeoutSeconds ?? projectConfig.BuildTimeoutSeconds;
        var testTimeout = workspace.TestTimeoutSeconds ?? projectConfig.TestTimeoutSeconds;

        // 2. Initialize Tools & Lenient Dispatcher
        var canonicalTools = new CanonicalTools(
            workspacePath,
            _pathResolver,
            effectiveEnv,
            _editorConfigService,
            _editorConfigFileApplier,
            _webSearchService,
            _canonicalToolsLogger,
            buildTimeoutSeconds: buildTimeout,
            testTimeoutSeconds: testTimeout,
            workspaceId: workspace.Id,
            taskId: context.ForgeTask.Id > 0 ? context.ForgeTask.Id : (int?)null,
            isReadOnly: context.Profile.Mode == AgentMode.ReadOnly || context.Profile.SecurityTier == SandboxSecurityTier.RestrictedReadOnly);

        var dispatcher = new LenientToolDispatcher(canonicalTools);

        using var interruptionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        NightCodeForge.Core.Workflow.HumanInterruptionException? capturedInterruption = null;
        canonicalTools.AskHandler = (questions, ct) =>
        {
            capturedInterruption = new NightCodeForge.Core.Workflow.HumanInterruptionException(questions);
            interruptionCts.Cancel();
            throw new OperationCanceledException(interruptionCts.Token);
        };

        NightCodeForge.Core.Workflow.RequestChangesException? capturedRequestChanges = null;
        canonicalTools.RequestChangesHandler = (feedback, reason) =>
        {
            capturedRequestChanges = new NightCodeForge.Core.Workflow.RequestChangesException(feedback, reason);
            interruptionCts.Cancel();
            throw new OperationCanceledException(interruptionCts.Token);
        };

        // Rehydrate any pending proposals previously recorded in DataBag
        var existingProposals = context.PipelineContext?.DataBag.Get<List<TaskProposal>>(PipelineDataBagKeys.PendingProposals);
        if (existingProposals is { Count: > 0 })
        {
            canonicalTools.PendingProposals.AddRange(existingProposals);
        }

        // Register any custom tools defined in the database configuration
        if (harnessConfig.CustomTools.Count > 0)
        {
            foreach (var customTool in harnessConfig.CustomTools)
            {
                var toolDef = new ToolDefinition
                {
                    Name = customTool.Name,
                    Description = customTool.Description,
                    Aliases = customTool.Aliases,
                    ParameterAliases = new Dictionary<string, string>(customTool.ParameterAliases, StringComparer.OrdinalIgnoreCase),
                    // Database-defined custom tools carry no executable definition: report that honestly
                    // instead of pretending the call succeeded.
                    Handler = (args, ct) => Task.FromResult($"Error: custom tool '{customTool.Name}' has no executable handler configured; the call was not executed.")
                };
                dispatcher.RegisterTool(toolDef);
            }
        }

        // Register external MCP tools if configured
        var mcpServers = await _mcpManager.GetConfiguredServersAsync(workspacePath, cancellationToken);
        if (mcpServers != null)
        {
            foreach (var (serverName, def) in mcpServers)
            {
                var toolDef = new ToolDefinition
                {
                    Name = $"mcp_{serverName}",
                    Description = $"External MCP server tool '{serverName}'. Command: {def.Command} {string.Join(" ", def.Args)}",
                    // There is no MCP protocol client yet. Launching the server command once and returning its
                    // stdout is not a tool call (the arguments were ignored and a stdio server never exits),
                    // so report clearly that the call was not executed.
                    Handler = (args, ct) => Task.FromResult($"Error: MCP server '{serverName}' is configured but MCP tool calls are not supported yet; the call was not executed.")
                };
                dispatcher.RegisterTool(toolDef);
            }
        }

        var circuitBreaker = new ToolLoopCircuitBreaker(context.OptimizationFlags);
        var toolResultCompactor = new ToolResultCompactor(context.OptimizationFlags);

        // 3. Discover context: AGENTS.md, Skills (Microsoft Agent Framework), Symbols
        var agentsMdRules = await _agentsMdService.IngestRulesAsync(workspacePath, cancellationToken);
        var projectContext = _agentsMdService.FormatProjectContextSection(agentsMdRules, projectConfig, context.Workspace);

        string discoveredSymbols = string.Empty;
        if (context.OptimizationFlags.DeterministicSymbolMap)
        {
            var taskText = $"{context.ForgeTask.Title} {context.ForgeTask.Description}";
            discoveredSymbols = await _symbolService.GetMatchingSymbolsPromptAsync(workspacePath, taskText, context.OptimizationFlags, cancellationToken);
        }

        // 4. Resolve Active Stage Permitted Tools (Database/Profile Configurable)
        var permittedTools = ResolvePermittedTools(harnessConfig, context.Profile, context.RequiredMode);

        // Discover skills and format catalog using minimalist Pi syntax (~25 tokens/skill).
        // The catalog is only useful (and only injected) when the agent can call load_skill;
        // skill resources/scripts are reached through the canonical read and bash tools.
        IReadOnlyList<SkillDescriptor> skills = Array.Empty<SkillDescriptor>();
        if (permittedTools.Contains(CanonicalToolNames.LoadSkill, StringComparer.OrdinalIgnoreCase))
        {
            skills = await _skillsService.DiscoverSkillsAsync(workspacePath, cancellationToken: cancellationToken);
        }
        var skillsCatalog = _skillsService.FormatSkillsCatalogPrompt(skills);
        var activeSkills = skills.Select(s => s.Name).ToList();

        // 5. Tool schemas are provided natively via typed AIFunctions (see BuildAIFunctionList);
        //    no hand-written <tools> prompt is injected.

        // 6. Build 4-Layer Stabilized Prompt Prefix
        var prefixStabilizer = new PromptPrefixStabilizer();
        var stageDirective = !string.IsNullOrWhiteSpace(context.StageDirective)
            ? context.StageDirective
            : WorkspacePromptResolver.ResolveStageDirective(workspacePath, context.StageId, GetStageDirective(harnessConfig, context.StageId));
        var taskDocPath = context.TaskMarkdownPath ?? context.PipelineContext?.DataBag.Get<string>(PipelineDataBagKeys.TaskMarkdownPath);
        var taskDate = _timeProvider.GetUtcNow().ToString("yyyy-MM-dd");
        var taskScope = BuildTaskScope(context.ForgeTask, taskDocPath, taskDate);
        var workspaceSystemPrompt = WorkspacePromptResolver.ResolveSystemPrompt(workspacePath);
        var customPersona = !string.IsNullOrWhiteSpace(workspaceSystemPrompt)
            ? workspaceSystemPrompt
            : (!string.IsNullOrWhiteSpace(context.Profile.SystemPrompt)
                ? context.Profile.SystemPrompt
                : harnessConfig.CustomPersona);

        var isContainer = context.Workspace?.ExecutionMode == ExecutionMode.ContainerSandbox;
        var agentWorkingDir = isContainer
            ? (context.ForgeTask.Id > 0
                ? $"/workspace/task-{context.ForgeTask.Id}"
                : "/workspace/clone")
            : workspacePath;

        var stabilizedPrefix = prefixStabilizer.BuildStabilizedPrefix(
            stageDirective,
            taskScope,
            projectContext,
            skillsCatalog,
            discoveredSymbols,
            context.OptimizationFlags,
            customRulesPrompt: harnessConfig.CustomSystemRules,
            customRolePersona: customPersona,
            workingDirectory: agentWorkingDir);

        // 7. Restore or Initialize Conversation History.
        // The system prompt is NOT part of the history: it is supplied to HarnessAgent as HarnessInstructions
        // (ChatOptions.Instructions), so adding it as a System message too would send it to the model twice.
        List<ChatMessage> chatHistory;
        if (!string.IsNullOrEmpty(context.SerializedStateJson))
        {
            chatHistory = DeserializeSession(context.SerializedStateJson, _logger);
            // Legacy checkpoints stored the system prompt as the first message; strip it while preserving system notices
            chatHistory.RemoveAll(m => m.Role == ChatRole.System && (m.Text == null || !m.Text.Contains("[System Notice", StringComparison.OrdinalIgnoreCase)));
        }
        else
        {
            chatHistory = new List<ChatMessage>
            {
                new(ChatRole.User, !string.IsNullOrWhiteSpace(context.BootstrapPrompt) ? context.BootstrapPrompt : "Please inspect the requirements and begin execution.")
            };
        }

        // A checkpoint that cannot be restored (corrupt JSON) or only contained system notices would leave the
        // agent with no prompt at all: ensure the bootstrap prompt is present.
        if (chatHistory.Count == 0 || chatHistory.All(m => m.Role == ChatRole.System || (m.Text != null && m.Text.Contains("[System Notice", StringComparison.OrdinalIgnoreCase))))
        {
            var initialPrompt = !string.IsNullOrWhiteSpace(context.BootstrapPrompt) ? context.BootstrapPrompt : "Please inspect the requirements and begin execution.";
            if (chatHistory.Count == 0)
            {
                chatHistory.Add(new ChatMessage(ChatRole.User, initialPrompt));
            }
            else
            {
                chatHistory.Insert(0, new ChatMessage(ChatRole.User, initialPrompt));
            }
        }

        // 8. Preemptive Resumption: Complete pending 'ask' tool call with human answers as tool result
        if (!string.IsNullOrWhiteSpace(context.PendingHumanAnswer))
        {
            var pendingCallIds = new HashSet<string>();
            foreach (var msg in chatHistory)
            {
                foreach (var content in msg.Contents)
                {
                    if (content is FunctionCallContent fcc && !string.IsNullOrEmpty(fcc.CallId))
                    {
                        pendingCallIds.Add(fcc.CallId);
                    }
                    else if (content is FunctionResultContent frc && !string.IsNullOrEmpty(frc.CallId))
                    {
                        pendingCallIds.Remove(frc.CallId);
                    }
                }
            }

            if (pendingCallIds.Count > 0)
            {
                foreach (var pendingId in pendingCallIds)
                {
                    chatHistory.Add(new ChatMessage(ChatRole.Tool, new List<AIContent>
                    {
                        new FunctionResultContent(pendingId, context.PendingHumanAnswer)
                    }));
                }
                chatHistory.Add(new ChatMessage(ChatRole.User, "Please proceed with your plan based on the clarified answers."));
            }
            else if (!chatHistory.Any(m => (m.Text != null && m.Text.Contains(context.PendingHumanAnswer, StringComparison.Ordinal))
                     || m.Contents.OfType<FunctionResultContent>().Any(frc => frc.Result?.ToString()?.Contains(context.PendingHumanAnswer, StringComparison.Ordinal) == true)))
            {
                chatHistory.Add(new ChatMessage(ChatRole.User, $"[Human Clarification]:\n{context.PendingHumanAnswer}\n\nPlease proceed with your plan."));
            }
        }

        // 9. Resolve effective ChatClient for the profile and assemble middlewares
        var baseClient = _chatClientFactory.CreateClient(context.Profile);

        var effectiveModel = !string.IsNullOrWhiteSpace(context.Profile.ModelName)
            ? context.Profile.ModelName
            : baseClient.GetService<ChatClientMetadata>()?.DefaultModelId;

        string latestSerializedState = SerializeSession(chatHistory);

        // Conversation turns are written one at a time and in order: the repository (typically a scoped DbContext)
        // does not support concurrent operations, and a fire-and-forget write would hide its failures.
        var turnWriter = new TurnWriter(_logger);

        // The tracer sits closest to the model (below persistence and compaction) so that it records the exact
        // requests sent to the LLM: everything MAF injects, plus the history after compaction.
        var tracingClient = new ThinkingAndActivityTracingChatClient(
            baseClient,
            onEntry: entry =>
            {
                if (conversation != null && _taskRepository != null)
                {
                    try
                    {
                        var role = entry.Role switch
                        {
                            "THINKING" => StageTurnRole.Thinking,
                            "TOOL" => StageTurnRole.Tool,
                            "TOOL_RESULT" or "TOOL_ERROR" => StageTurnRole.ToolResult,
                            "OPTIMIZATION" => StageTurnRole.Optimization,
                            "USER" or "User" => StageTurnRole.User,
                            _ => StageTurnRole.Assistant
                        };

                        var repository = _taskRepository;
                        var conversationId = conversation.Id;
                        var metadataJson = !string.IsNullOrEmpty(entry.CallId)
                            ? JsonSerializer.Serialize(new { CallId = entry.CallId })
                            : null;

                        turnWriter.Enqueue(
                            () => repository.AddConversationTurnAsync(
                                conversationId,
                                role,
                                content: entry.Content,
                                thinkingTrace: entry.Role == "THINKING" ? entry.Content : null,
                                toolName: entry.ToolName,
                                toolArguments: entry.ToolArguments,
                                toolResult: entry.ToolResult,
                                isError: string.Equals(entry.Role, "TOOL_ERROR", StringComparison.OrdinalIgnoreCase),
                                metadataJson: metadataJson,
                                cancellationToken: cancellationToken),
                            $"Failed to record turn for conversation #{conversationId}");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to record turn for conversation #{ConversationId}", conversation.Id);
                    }
                }
            },
            activityStreamer: _activityStreamer,
            sessionId: sessionId,
            taskId: context.ForgeTask.Id,
            stageId: context.StageId);

        var persistingClient = new PerServiceCallChatHistoryPersistingChatClient(
            tracingClient,
            async history =>
            {
                chatHistory.Clear();
                chatHistory.AddRange(history);
                var compResult = toolResultCompactor.Compact(chatHistory);
                if (compResult.ItemsCompacted > 0)
                {
                    var optText = $"Truncated {compResult.ItemsCompacted} older tool result(s), saving approx ~{compResult.CharactersSaved / 4} tokens.";
                    await BroadcastAsync(
                        sessionId,
                        context.ForgeTask.Id,
                        context.StageId,
                        AgentActivityKind.OptimizationAction,
                        optText,
                        cancellationToken,
                        metadata: new Dictionary<string, object?>
                        {
                            ["ActionType"] = "ToolResultCompaction",
                            ["TokensSaved"] = compResult.CharactersSaved / 4
                        });

                    if (conversation != null && _taskRepository != null)
                    {
                        var repository = _taskRepository;
                        var conversationId = conversation.Id;
                        var metadataJson = System.Text.Json.JsonSerializer.Serialize(new { ActionType = "ToolResultCompaction", TokensSaved = compResult.CharactersSaved / 4 });
                        turnWriter.Enqueue(
                            () => repository.AddConversationTurnAsync(
                                conversationId,
                                StageTurnRole.Optimization,
                                content: optText,
                                metadataJson: metadataJson,
                                cancellationToken: cancellationToken),
                            $"Failed to record optimization turn for conversation #{conversationId}");
                    }
                }
                latestSerializedState = SerializeSession(chatHistory);
            },
            _logger);

        var compactedClient = new ThinkingTraceCompactionChatClient(persistingClient, context.OptimizationFlags);
        var activeClient = new LenientToolCallChatClient(compactedClient);

        // 10. Main Agentic Reasoning Loop with HarnessAgent & Real-Time Streaming
        int maxTurns = ResolveMaxTurns(harnessConfig, context.Profile);

        var aiTools = BuildAIFunctionList(canonicalTools, permittedTools, dispatcher, harnessConfig.CustomTools, harnessConfig.ToolDirectives);

        var chatOptions = new ChatOptions
        {
            ModelId = !string.IsNullOrWhiteSpace(effectiveModel) ? effectiveModel : null,
            Temperature = (float?)context.Profile.Temperature,
            Tools = aiTools,
            AdditionalProperties = ModelCapabilityResolver.BuildReasoningProperties(_llmOptions, effectiveModel ?? _llmOptions?.ModelId)
        };

        if (context.OptimizationFlags.GuidedGrammarDecoding)
        {
            GuidedGrammarHelper.ApplyGrammarConstraints(chatOptions, context.OptimizationFlags);
        }

        var harnessOptions = new HarnessAgentOptions
        {
            Id = context.StageId,
            Name = context.Profile.Name ?? context.Profile.Id,
            HarnessInstructions = stabilizedPrefix,
            ChatOptions = chatOptions,
            MaximumIterationsPerRequest = maxTurns,
            CompactionStrategy = ModelCapabilityResolver.BuildCompactionStrategy(_llmOptions, effectiveModel ?? _llmOptions?.ModelId),
            DisableTodoProvider = true,
            DisableAgentModeProvider = true,
            DisableFileMemory = true,
            DisableAgentSkillsProvider = true,
            DisableWebSearch = true,
            DisableToolAutoApproval = false
        };

        var agent = new HarnessAgent(activeClient, harnessOptions);
        var session = await agent.CreateSessionAsync(cancellationToken);

        string? targetRouting = null;
        var accumulatedText = new StringBuilder();
        var accumulatedReasoning = new StringBuilder();
        bool streamingFailed = false;

        AgentRunMetrics CurrentMetrics() =>
            new(stopwatch.ElapsedMilliseconds, promptTokens, completionTokens, promptTokens + completionTokens);

        async Task<string?> SaveAsync(AgentRunStatus status, string? summary, CancellationToken ct)
        {
            // Every queued conversation turn must be persisted before the conversation is closed.
            await turnWriter.DrainAsync();
            return await SaveTranscriptAsync(conversation, context, workspacePath, chatHistory, stopwatch.ElapsedMilliseconds, promptTokens, completionTokens, status, summary, ct, tracingClient, harnessOptions, activeSkills, attemptIndex);
        }

        async Task<AgentRunResult> CompleteWithRequestChangesAsync(RequestChangesException reqChange)
        {
            stopwatch.Stop();
            latestSerializedState = SerializeSession(chatHistory);

            var summary = $"Reviewer requested changes: {reqChange.Feedback}";
            await SaveAsync(AgentRunStatus.Completed, summary, cancellationToken);
            await BroadcastAsync(sessionId, context.ForgeTask.Id, context.StageId, AgentActivityKind.StageCompleted, $"Changes requested: {reqChange.Feedback}", cancellationToken);

            var proposals = canonicalTools.PendingProposals.Count > 0
                ? (IReadOnlyList<TaskProposal>)canonicalTools.PendingProposals.ToList()
                : null;

            return AgentRunResult.Completed(
                CurrentMetrics(),
                commitHash: null,
                updatedState: latestSerializedState,
                summary: summary,
                targetRouting: reqChange.TargetStageId,
                reviewerFeedback: reqChange.Feedback,
                proposals: proposals);
        }

        async Task<AgentRunResult> CompleteWithInterruptionAsync(HumanInterruptionException interruption)
        {
            stopwatch.Stop();

            var answeredCallIds = new HashSet<string>(
                chatHistory.SelectMany(m => m.Contents)
                    .OfType<FunctionResultContent>()
                    .Where(frc => !string.IsNullOrEmpty(frc.CallId))
                    .Select(frc => frc.CallId!));

            var hasPendingAsk = chatHistory.Any(m => m.Role == ChatRole.Assistant &&
                m.Contents.OfType<FunctionCallContent>().Any(f => f.Name == "ask" && !answeredCallIds.Contains(f.CallId)));
            if (!hasPendingAsk)
            {
                var callId = Guid.NewGuid().ToString("N");
                var askArgs = new Dictionary<string, object?> { ["questions"] = interruption.Questions };
                chatHistory.Add(new ChatMessage(ChatRole.Assistant, new List<AIContent>
                {
                    new FunctionCallContent(callId, "ask", askArgs)
                }));
            }

            latestSerializedState = SerializeSession(chatHistory);

            var summaryPrompt = string.Join("; ", interruption.Questions.Select(q => q.Prompt));
            await BroadcastAsync(sessionId, context.ForgeTask.Id, context.StageId, AgentActivityKind.Interrupted, summaryPrompt, cancellationToken);
            await SaveAsync(AgentRunStatus.WaitingForHuman, summaryPrompt, cancellationToken);

            return AgentRunResult.WaitingForHuman(
                interruption.Questions,
                CurrentMetrics(),
                updatedState: latestSerializedState,
                summary: "Crucible paused awaiting human clarification");
        }

        async Task<AgentRunResult> FailAsync(string errorMsg)
        {
            stopwatch.Stop();
            await BroadcastAsync(sessionId, context.ForgeTask.Id, context.StageId, AgentActivityKind.Error, errorMsg, cancellationToken);
            await SaveAsync(AgentRunStatus.Failed, errorMsg, cancellationToken);

            return AgentRunResult.Failed(errorMsg, CurrentMetrics(), latestSerializedState);
        }

        async Task AbortOnCancellationAsync()
        {
            // The caller's token is already cancelled: close the conversation with a token that still works,
            // otherwise the stage conversation would stay "Running" forever.
            stopwatch.Stop();
            latestSerializedState = SerializeSession(chatHistory);
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await BroadcastAsync(sessionId, context.ForgeTask.Id, context.StageId, AgentActivityKind.Error, "Stage cancelled", timeoutCts.Token);
            await SaveAsync(AgentRunStatus.Cancelled, "Stage cancelled", timeoutCts.Token);
        }

        var toolNamesByCallId = new Dictionary<string, string>(StringComparer.Ordinal);

        void FlushTurnToHistory()
        {
            if (accumulatedText.Length > 0 || accumulatedReasoning.Length > 0)
            {
                var text = accumulatedText.ToString();
                var reasoning = accumulatedReasoning.ToString();

                var lastMsg = chatHistory.LastOrDefault();
                if (lastMsg == null || lastMsg.Role != ChatRole.Assistant)
                {
                    var assistantMsg = new ChatMessage(ChatRole.Assistant, text);
                    if (!string.IsNullOrEmpty(reasoning))
                    {
                        assistantMsg.Contents.Add(new TextReasoningContent(reasoning));
                    }
                    chatHistory.Add(assistantMsg);
                }
                else
                {
                    if (string.IsNullOrEmpty(lastMsg.Text) && !string.IsNullOrEmpty(text))
                    {
                        lastMsg.Contents.Insert(0, new TextContent(text));
                    }
                    if (!string.IsNullOrEmpty(reasoning) && !lastMsg.Contents.OfType<TextReasoningContent>().Any())
                    {
                        lastMsg.Contents.Add(new TextReasoningContent(reasoning));
                    }
                }

                accumulatedText.Clear();
                accumulatedReasoning.Clear();
                latestSerializedState = SerializeSession(chatHistory);
            }
        }

        // Returns true when the loop circuit breaker asks to abort the current turn.
        async Task<bool> ProcessUpdateAsync(AgentResponseUpdate update, bool streaming)
        {
            // While streaming, ThinkingAndActivityTracingChatClient already broadcasts reasoning and message
            // deltas; broadcasting them again here would duplicate every delta in the activity feed.
            var broadcastDeltas = !streaming;
            var sawUsageContent = false;

            if (update.Contents != null)
            {
                foreach (var content in update.Contents)
                {
                    if (content is FunctionCallContent fc)
                    {
                        FlushTurnToHistory();
                        var canonicalName = dispatcher.ResolveToolName(fc.Name);
                        if (!string.IsNullOrEmpty(fc.CallId))
                        {
                            toolNamesByCallId[fc.CallId] = fc.Name;
                        }
                        var argsJson = fc.Arguments != null ? JsonSerializer.Serialize(fc.Arguments) : "{}";
                        await BroadcastAsync(sessionId, context.ForgeTask.Id, context.StageId, AgentActivityKind.ToolStarted, $"{canonicalName}: {argsJson}", cancellationToken, new Dictionary<string, object?> { ["ToolName"] = canonicalName });
                    }
                    else if (content is FunctionResultContent frc)
                    {
                        var resultStr = frc.Result?.ToString() ?? string.Empty;
                        var resultToolName = frc.CallId != null && toolNamesByCallId.TryGetValue(frc.CallId, out var calledName) ? calledName : null;
                        if (conversation != null && _taskRepository != null)
                        {
                            var repository = _taskRepository;
                            var conversationId = conversation.Id;
                            var resultIsError = resultStr.StartsWith("Error", StringComparison.OrdinalIgnoreCase);
                            var metadataObj = new
                            {
                                CallId = frc.CallId,
                                ToolName = resultToolName,
                                OptimizationFlags = context.OptimizationFlags,
                                PendingProposalsCount = canonicalTools.PendingProposals.Count
                            };
                            var metadataJson = JsonSerializer.Serialize(metadataObj);

                            turnWriter.Enqueue(
                                () => repository.AddConversationTurnAsync(
                                    conversationId,
                                    StageTurnRole.ToolResult,
                                    toolName: resultToolName,
                                    toolResult: resultStr,
                                    isError: resultIsError,
                                    metadataJson: metadataJson,
                                    cancellationToken: cancellationToken),
                                $"Failed to record tool result turn for conversation #{conversationId}");
                        }

                        // Sync in-flight DataBag state and task persistence across tool execution milestones
                        if (context.PipelineContext?.DataBag != null)
                        {
                            // Always mirror the proposals (an emptied list must overwrite a stale persisted one).
                            context.PipelineContext.DataBag.Set(PipelineDataBagKeys.PendingProposals, canonicalTools.PendingProposals.ToList());
                            if (context.OptimizationFlags != null)
                            {
                                context.PipelineContext.DataBag.Set(PipelineDataBagKeys.OptimizationFlags, context.OptimizationFlags);
                            }
                            if (context.DiscoveredSymbols != null)
                            {
                                context.PipelineContext.DataBag.Set(PipelineDataBagKeys.DiscoveredSymbols, context.DiscoveredSymbols);
                            }
                            // The conversation history itself is NOT mirrored here: it is already persisted turn by turn
                            // in ConversationTurns and rebuilt from there by StartupRecoveryService.

                            context.ForgeTask.PipelineDataJson = context.PipelineContext.DataBag.ToJson();
                            if (_taskRepository != null)
                            {
                                var currentTask = context.ForgeTask;
                                turnWriter.Enqueue(
                                    () => _taskRepository.UpdateAsync(currentTask, cancellationToken: cancellationToken),
                                    $"Failed to sync pipeline data bag for task #{currentTask.Id}");
                            }
                        }

                        await BroadcastAsync(
                            sessionId,
                            context.ForgeTask.Id,
                            context.StageId,
                            AgentActivityKind.ToolCompleted,
                            resultStr,
                            cancellationToken,
                            new Dictionary<string, object?> { ["ToolName"] = resultToolName ?? "tool" });
                    }
                    else if (content is TextReasoningContent reasoning && !string.IsNullOrEmpty(reasoning.Text))
                    {
                        accumulatedReasoning.Append(reasoning.Text);
                        if (broadcastDeltas)
                        {
                            await BroadcastAsync(sessionId, context.ForgeTask.Id, context.StageId, AgentActivityKind.ThinkingDelta, reasoning.Text, cancellationToken);
                        }
                    }
                    else if (content is UsageContent uc && uc.Details != null)
                    {
                        // Each model call reports its own usage: accumulate across the iterations of the run.
                        sawUsageContent = true;
                        if (uc.Details.InputTokenCount.HasValue) promptTokens += uc.Details.InputTokenCount.Value;
                        if (uc.Details.OutputTokenCount.HasValue) completionTokens += uc.Details.OutputTokenCount.Value;
                    }
                }
            }

            if (update.RawRepresentation is ChatResponseUpdate chatUpdate && chatUpdate.AdditionalProperties != null)
            {
                // The raw "usage" property describes the same call as UsageContent: only use it as a fallback.
                if (!sawUsageContent
                    && chatUpdate.AdditionalProperties.TryGetValue("usage", out var rawUsage)
                    && rawUsage is JsonElement je)
                {
                    if (je.TryGetProperty("prompt_tokens", out var pt) && pt.TryGetInt64(out var ptVal)) promptTokens += ptVal;
                    if (je.TryGetProperty("completion_tokens", out var ct) && ct.TryGetInt64(out var ctVal)) completionTokens += ctVal;
                }

                if (chatUpdate.AdditionalProperties.TryGetValue("target_routing", out var trObj) && trObj is string strVal)
                {
                    targetRouting = strVal;
                }
            }

            if (!string.IsNullOrEmpty(update.Text))
            {
                accumulatedText.Append(update.Text);

                if (broadcastDeltas)
                {
                    await BroadcastAsync(sessionId, context.ForgeTask.Id, context.StageId, AgentActivityKind.MessageDelta, update.Text, cancellationToken);
                }

                if (streaming && context.OptimizationFlags?.LoopCircuitBreaker == true && circuitBreaker.DetectStreamingRepetition(accumulatedText.ToString()))
                {
                    _logger.LogWarning("Circuit breaker detected repeating n-gram monologue in streaming output. Aborting turn.");
                    return true;
                }
            }

            return false;
        }

        // The agent receives a copy: the persisting middleware rewrites chatHistory in place on every model call.
        var historySnapshot = chatHistory.ToList();
        var enumerator = agent.RunStreamingAsync(historySnapshot.ToList(), session, cancellationToken: interruptionCts.Token)
            .GetAsyncEnumerator(interruptionCts.Token);
        try
        {
            while (true)
            {
                bool hasMore;
                try
                {
                    hasMore = await enumerator.MoveNextAsync();
                }
                catch (RequestChangesException reqChange)
                {
                    return await CompleteWithRequestChangesAsync(reqChange);
                }
                catch (HumanInterruptionException interruption)
                {
                    return await CompleteWithInterruptionAsync(interruption);
                }
                catch (Exception ex) when (ex is NotImplementedException or NotSupportedException)
                {
                    streamingFailed = true;
                    break;
                }
                catch (OperationCanceledException)
                {
                    if (capturedInterruption != null)
                    {
                        return await CompleteWithInterruptionAsync(capturedInterruption);
                    }
                    if (capturedRequestChanges != null)
                    {
                        return await CompleteWithRequestChangesAsync(capturedRequestChanges);
                    }
                    await AbortOnCancellationAsync();
                    throw;
                }
                catch (Exception ex)
                {
                    var errorMsg = ex.InnerException != null ? $"{ex.Message} (Inner: {ex.InnerException.Message})" : ex.Message;
                    _logger.LogError(ex, "Error during Crucible streaming LLM invocation: {ErrorMessage}", errorMsg);
                    return await FailAsync(errorMsg);
                }

                if (!hasMore) break;

                if (await ProcessUpdateAsync(enumerator.Current, streaming: true))
                {
                    break;
                }
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
        }

        if (capturedInterruption != null)
        {
            return await CompleteWithInterruptionAsync(capturedInterruption);
        }
        if (capturedRequestChanges != null)
        {
            return await CompleteWithRequestChangesAsync(capturedRequestChanges);
        }

        if (streamingFailed)
        {
            // Discard what a partially consumed stream produced so the non-streaming retry does not duplicate it.
            accumulatedText.Clear();
            accumulatedReasoning.Clear();
            chatHistory.Clear();
            chatHistory.AddRange(historySnapshot);

            try
            {
                var response = await agent.RunAsync(historySnapshot.ToList(), session, cancellationToken: interruptionCts.Token);
                foreach (var update in response.ToAgentResponseUpdates())
                {
                    await ProcessUpdateAsync(update, streaming: false);
                }
            }
            catch (RequestChangesException reqChange)
            {
                return await CompleteWithRequestChangesAsync(reqChange);
            }
            catch (HumanInterruptionException interruption)
            {
                return await CompleteWithInterruptionAsync(interruption);
            }
            catch (OperationCanceledException)
            {
                if (capturedInterruption != null)
                {
                    return await CompleteWithInterruptionAsync(capturedInterruption);
                }
                if (capturedRequestChanges != null)
                {
                    return await CompleteWithRequestChangesAsync(capturedRequestChanges);
                }
                await AbortOnCancellationAsync();
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during Crucible non-streaming LLM invocation");
                return await FailAsync(ex.Message);
            }

            if (capturedInterruption != null)
            {
                return await CompleteWithInterruptionAsync(capturedInterruption);
            }
            if (capturedRequestChanges != null)
            {
                return await CompleteWithRequestChangesAsync(capturedRequestChanges);
            }
        }

        FlushTurnToHistory();

        stopwatch.Stop();
        var concludingText = StageSummaryExtractor.GetConcludingText(chatHistory);
        var extraction = StageSummaryExtractor.Extract(concludingText, fallbackSummary: "Stage completed successfully.", explicitTargetRouting: targetRouting);

        var proposals = canonicalTools.PendingProposals.Count > 0
            ? (IReadOnlyList<TaskProposal>)canonicalTools.PendingProposals.ToList()
            : null;

        await SaveAsync(AgentRunStatus.Completed, extraction.Summary, cancellationToken);

        await BroadcastAsync(sessionId, context.ForgeTask.Id, context.StageId, AgentActivityKind.StageCompleted, extraction.Summary, cancellationToken);

        return AgentRunResult.Completed(
            new AgentRunMetrics(stopwatch.ElapsedMilliseconds, promptTokens, completionTokens, promptTokens + completionTokens),
            commitHash: null,
            updatedState: latestSerializedState,
            summary: extraction.Summary,
            targetRouting: extraction.TargetRouting,
            reviewerFeedback: extraction.ReviewerFeedback,
            proposals: proposals);
    }

    private async Task BroadcastAsync(string sessionId, int taskId, string stageId, AgentActivityKind kind, string content, CancellationToken ct, Dictionary<string, object?>? metadata = null)
    {
        if (_activityStreamer != null)
        {
            var evt = new AgentActivityEvent(sessionId, taskId, stageId, kind, content, DateTime.UtcNow, metadata);
            try
            {
                await _activityStreamer.BroadcastAsync(evt, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A failing live feed must never abort the stage or prevent its transcript from being saved.
                _logger.LogWarning(ex, "Failed to broadcast {Kind} activity for task #{TaskId}", kind, taskId);
            }
        }
    }

    /// <summary>
    /// Builds the &lt;task&gt; scope block of the prompt, neutralising characters that would break out of the
    /// attribute values or close the block early.
    /// </summary>
    public static string BuildTaskScope(ForgeTask task, string? taskDocPath, string? date = null)
    {
        ArgumentNullException.ThrowIfNull(task);

        static string EscapeAttribute(string value) =>
            value.Replace("\"", "&quot;").Replace("<", "&lt;").Replace(">", "&gt;");

        var docAttr = !string.IsNullOrWhiteSpace(taskDocPath) ? $" path=\"{EscapeAttribute(taskDocPath)}\"" : string.Empty;
        var dateAttr = !string.IsNullOrWhiteSpace(date) ? $" date=\"{EscapeAttribute(date)}\"" : string.Empty;
        var description = (task.Description ?? string.Empty)
            .Replace("</task>", "&lt;/task&gt;", StringComparison.OrdinalIgnoreCase);

        return $"<task id=\"{task.Id}\" title=\"{EscapeAttribute(task.Title ?? string.Empty)}\"{docAttr}{dateAttr}>\n{description}\n</task>";
    }

    /// <summary>Runs persistence work sequentially, in submission order, logging (never throwing) failures.</summary>
    private sealed class TurnWriter(ILogger logger)
    {
        private readonly object _gate = new();
        private Task _tail = Task.CompletedTask;

        public void Enqueue(Func<Task> write, string failureMessage)
        {
            lock (_gate)
            {
                _tail = RunAfterAsync(_tail, write, failureMessage);
            }
        }

        public Task DrainAsync()
        {
            lock (_gate)
            {
                return _tail;
            }
        }

        private async Task RunAfterAsync(Task previous, Func<Task> write, string failureMessage)
        {
            await previous.ConfigureAwait(false);
            try
            {
                await write().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "{Message}", failureMessage);
            }
        }
    }

    private async Task<string?> SaveTranscriptAsync(
        Conversation? conversation,
        AgentSessionContext context,
        string workspacePath,
        IReadOnlyList<ChatMessage> chatHistory,
        long elapsedMs,
        long promptTokens,
        long completionTokens,
        AgentRunStatus status,
        string? summary,
        CancellationToken cancellationToken,
        ThinkingAndActivityTracingChatClient? tracingClient = null,
        HarnessAgentOptions? harnessOptions = null,
        IReadOnlyList<string>? activeSkills = null,
        int attemptIndex = 1)
    {
        string? transcriptFileName = null;
        if (_transcriptService != null)
        {
            try
            {
                var systemPrompt = tracingClient?.LastCapturedSystemPrompt
                    ?? chatHistory.FirstOrDefault(m => m.Role == ChatRole.System)?.Text
                    ?? context.Profile.SystemPrompt;
                IReadOnlyList<string>? availableTools = tracingClient is { LastCapturedTools.Count: > 0 }
                    ? tracingClient.LastCapturedTools
                    : context.Profile.PermittedTools;
                var mafConfig = harnessOptions != null
                    ? MafConfigurationSummary.Build(harnessOptions, tracingClient?.DetectedMafConfiguration)
                    : null;
                var entries = BuildTranscriptEntries(chatHistory);

                var meta = new TranscriptMetadata(
                    context.ForgeTask.Id,
                    context.StageId,
                    context.Profile.Name,
                    context.Profile.ModelName,
                    elapsedMs,
                    new AgentRunMetrics(elapsedMs, promptTokens, completionTokens, promptTokens + completionTokens),
                    context.OptimizationFlags ?? new LocalOptimizationFlags(),
                    status,
                    KvCacheHitRate: null,
                    SystemPrompt: systemPrompt,
                    AvailableTools: availableTools,
                    ActiveSkills: activeSkills,
                    MafConfiguration: mafConfig,
                    ModelCalls: tracingClient?.ModelCalls,
                    AttemptIndex: attemptIndex);

                var transcriptDir = Path.Combine(workspacePath, "docs", "transcripts");
                var savedPath = await _transcriptService.SaveTranscriptAsync(meta, entries, transcriptDir, cancellationToken);
                transcriptFileName = Path.GetFileName(savedPath);
            }
            catch (Exception tex)
            {
                _logger.LogWarning(tex, "Failed to persist session transcript for task #{TaskId} with status {Status}", context.ForgeTask.Id, status);
            }
        }

        if (conversation != null && _taskRepository != null)
        {
            try
            {
                var stageStatus = status switch
                {
                    AgentRunStatus.Completed => StageExecutionStatus.Completed,
                    AgentRunStatus.WaitingForHuman => StageExecutionStatus.Running,
                    AgentRunStatus.Cancelled => StageExecutionStatus.Cancelled,
                    _ => StageExecutionStatus.Failed
                };

                await _taskRepository.CompleteConversationAsync(
                    conversation.Id,
                    stageStatus,
                    summary: summary,
                    transcriptFileName: transcriptFileName,
                    cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to complete Conversation #{ConversationId}", conversation.Id);
            }
        }

        return transcriptFileName;
    }

    public static List<TranscriptEntry> BuildTranscriptEntries(IReadOnlyList<ChatMessage> chatHistory)
    {
        var entries = new List<TranscriptEntry>();
        var now = DateTime.UtcNow;
        // Tool result messages only carry the call id: resolve it back to the name of the tool that was called.
        var toolNamesByCallId = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var msg in chatHistory)
        {
            var role = msg.Role.Value;
            var text = msg.Text ?? string.Empty;

            // Structured reasoning (TextReasoningContent) is not part of msg.Text; emit it as its own step.
            if (msg.Role == ChatRole.Assistant)
            {
                var reasoning = string.Concat(msg.Contents.OfType<TextReasoningContent>().Select(r => r.Text));
                if (!string.IsNullOrWhiteSpace(reasoning))
                {
                    entries.Add(new TranscriptEntry(now, "THINKING", reasoning));
                }
            }

            var functionCalls = msg.Contents.OfType<FunctionCallContent>().ToList();
            var functionResults = msg.Contents.OfType<FunctionResultContent>().ToList();

            if (functionCalls.Count > 0)
            {
                for (int i = 0; i < functionCalls.Count; i++)
                {
                    var fc = functionCalls[i];
                    if (!string.IsNullOrEmpty(fc.CallId))
                    {
                        toolNamesByCallId[fc.CallId] = fc.Name;
                    }
                    var argsJson = fc.Arguments != null
                        ? JsonSerializer.Serialize(fc.Arguments, new JsonSerializerOptions { WriteIndented = true })
                        : null;

                    entries.Add(new TranscriptEntry(
                        now,
                        role,
                        Content: i == 0 ? text : string.Empty,
                        ToolName: fc.Name,
                        ToolArguments: argsJson,
                        ToolResult: null));
                }
            }
            else if (functionResults.Count > 0)
            {
                foreach (var fr in functionResults)
                {
                    var resultStr = fr.Result?.ToString() ?? string.Empty;
                    entries.Add(new TranscriptEntry(
                        now,
                        role,
                        Content: string.Empty,
                        ToolName: fr.CallId != null && toolNamesByCallId.TryGetValue(fr.CallId, out var calledTool) ? calledTool : fr.CallId,
                        ToolArguments: null,
                        ToolResult: resultStr));
                }
            }
            else
            {
                entries.Add(new TranscriptEntry(
                    now,
                    role,
                    Content: text,
                    ToolName: null,
                    ToolArguments: null,
                    ToolResult: null));
            }
        }

        return entries;
    }

    private CrucibleHarnessConfig ParseHarnessConfig(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new CrucibleHarnessConfig();
        }

        try
        {
            return JsonSerializer.Deserialize<CrucibleHarnessConfig>(json) ?? new CrucibleHarnessConfig();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Ignoring invalid HarnessConfigurationJson; using default Crucible configuration");
            return new CrucibleHarnessConfig();
        }
    }

    private static IReadOnlyList<string> ResolvePermittedTools(CrucibleHarnessConfig config, AgentProfile profile, AgentMode requiredMode)
    {
        // 1. Config override from database HarnessConfigurationJson
        if (config.AllowedTools != null && config.AllowedTools.Count > 0)
        {
            return config.AllowedTools;
        }

        // 2. Profile whitelist
        if (profile.PermittedTools != null && profile.PermittedTools.Count > 0)
        {
            return profile.PermittedTools;
        }

        // 3. Modes: ReadOnly, Review, Full
        return DefaultAgentTools.GetTools(profile.Mode == AgentMode.ReadOnly ? AgentMode.ReadOnly : requiredMode);
    }

    private static string GetStageDirective(CrucibleHarnessConfig config, string stageId)
    {
        if (config.StageDirectives.TryGetValue(stageId, out var customDirective) && !string.IsNullOrWhiteSpace(customDirective))
        {
            return customDirective;
        }

        return stageId.ToLowerInvariant() switch
        {
            StandardStageIds.Triage or "stage-0" =>
                "Analyze the reported task against the repository base branch. Determine affected files and acceptance criteria. Call propose_tasks to recommend structured backlog items. You have read-only access and cannot modify code or access external networks.",
            StandardStageIds.TaskReview or "stage-1" =>
                "You are a Staff Software Architect & Requirements Investigator. 1. Thoroughly investigate the task requirements and inspect the codebase using 'read' and 'bash'. 2. MANDATORY FILE EDIT: Edit the task specification document on disk in docs/tasks/ using 'edit' or 'write' with your approved architecture, affected files, technical plan, and acceptance criteria. Your disk changes will be read back upon stage completion to update the task specification. 3. Gather all questions and ambiguities into a single 'ask' call using the question list, expecting all answers at once; follow-up asks are permitted only for newly discovered blockers. Do not guess. 4. Do not write production code.",
            StandardStageIds.Implementation or "stage-2" =>
                "You are a Senior Software Engineer & TDD Practitioner. Implement clean, robust code according to the task specification in docs/tasks/ and the approved plan. Keep blast radius minimal. Run builds and tests incrementally with bash. Fix all compiler errors and test failures. If breaking blockers arise, call 'ask'.",
            StandardStageIds.CodeReviewAndFix or "stage-3" =>
                "You are a Principal Code Reviewer and Quality Gatekeeper. Review the git diff against the base branch. Compare against the specification in docs/tasks/ and the implementation claims. Apply precision polish directly using 'edit'. Rejection is strictly reserved for major tasks or missing features: invoke 'request_changes' or conclude with <request_changes>[feedback]</request_changes>.",
            _ => "Execute assigned engineering tasks diligently using canonical tools."
        };
    }

    public static IList<AITool> BuildAIFunctionList(
        CanonicalTools tools,
        IReadOnlyList<string> permittedTools,
        LenientToolDispatcher dispatcher,
        List<CustomToolConfig>? customTools = null,
        IReadOnlyDictionary<string, string>? toolDirectives = null)
    {
        var aiFunctions = CanonicalAIFunctionFactory.CreateTools(tools, permittedTools, dispatcher).Cast<AIFunction>().ToList();
        var knownNames = new HashSet<string>(aiFunctions.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var tool in permittedTools)
        {
            // Only expose non-canonical tools that are actually registered (custom DB tools, MCP servers);
            // canonical tools are already handled by CanonicalAIFunctionFactory above.
            if (CanonicalToolNames.All.Contains(tool, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!knownNames.Contains(tool) && dispatcher.TryGetTool(tool, out _))
            {
                var custom = customTools?.FirstOrDefault(c => string.Equals(c.Name, tool, StringComparison.OrdinalIgnoreCase));
                var desc = custom?.Description ?? $"Execute tool '{tool}'";
                var toolName = tool;
                aiFunctions.Add(new DispatcherAIFunction(toolName, desc, dispatcher));
            }
        }

        if (toolDirectives is { Count: > 0 })
        {
            for (int i = 0; i < aiFunctions.Count; i++)
            {
                if (toolDirectives.TryGetValue(aiFunctions[i].Name, out var directive) && !string.IsNullOrWhiteSpace(directive))
                {
                    aiFunctions[i] = new DescriptionOverrideAIFunction(aiFunctions[i], directive);
                }
            }
        }

        return aiFunctions.Cast<AITool>().ToList();
    }

    /// <summary>
    /// Exposes a dispatcher-registered tool (custom DB tool, MCP server) with a free-form object schema so the
    /// model's arguments reach the dispatcher as-is, instead of being wrapped under a synthetic parameter.
    /// </summary>
    private sealed class DispatcherAIFunction(string name, string description, LenientToolDispatcher dispatcher) : AIFunction
    {
        private static readonly JsonElement FreeFormObjectSchema =
            JsonDocument.Parse("""{"type":"object","additionalProperties":true}""").RootElement.Clone();

        public override string Name => name;
        public override string Description => description;
        public override JsonElement JsonSchema => FreeFormObjectSchema;

        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            try
            {
                var argsJson = JsonSerializer.Serialize(arguments.ToDictionary(kv => kv.Key, kv => kv.Value));
                return await dispatcher.DispatchAsync(name, argsJson, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (NightCodeForge.Core.Workflow.HumanInterruptionException)
            {
                throw;
            }
            catch (NightCodeForge.Core.Workflow.RequestChangesException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var msg = ex.InnerException != null ? $"{ex.Message} (Inner: {ex.InnerException.Message})" : ex.Message;
                return $"Error: {msg}";
            }
        }
    }

    /// <summary>Overrides the description exposed to the model (database-configured tool directives).</summary>
    private sealed class DescriptionOverrideAIFunction(AIFunction inner, string description) : DelegatingAIFunction(inner)
    {
        public override string Description => description;
    }

    public static string SerializeSession(List<ChatMessage> messages)
    {
        // Contents are stored in their original order (reasoning, text, tool calls and results) so that a
        // resumed session is identical to the one that was checkpointed.
        var simpleList = messages.Select(m => new SerializedMessageItem(
            m.Role.Value,
            Text: null,
            FunctionCalls: null,
            FunctionResults: null,
            AuthorName: m.AuthorName,
            Contents: m.Contents.Select(ToSerializedContent).OfType<SerializedContent>().ToList()
        )).ToList();

        return JsonSerializer.Serialize(simpleList, new JsonSerializerOptions { WriteIndented = false });
    }

    private static SerializedContent? ToSerializedContent(AIContent content) => content switch
    {
        TextReasoningContent r => new SerializedContent("reasoning", Text: r.Text, ProtectedData: r.ProtectedData),
        TextContent t => new SerializedContent("text", Text: t.Text),
        FunctionCallContent f => new SerializedContent("call", CallId: f.CallId, Name: f.Name, Arguments: f.Arguments),
        FunctionResultContent r => new SerializedContent("result", CallId: r.CallId, Result: r.Result?.ToString()),
        _ => null
    };

    private static AIContent? FromSerializedContent(SerializedContent c)
    {
        switch (c.Type)
        {
            case "reasoning":
                return new TextReasoningContent(c.Text) { ProtectedData = c.ProtectedData };
            case "text":
                return new TextContent(c.Text);
            case "call":
                var args = c.Arguments != null
                    ? JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(c.Arguments))
                    : null;
                return new FunctionCallContent(c.CallId ?? Guid.NewGuid().ToString("N"), c.Name ?? string.Empty, args);
            case "result":
                return new FunctionResultContent(c.CallId ?? Guid.NewGuid().ToString("N"), c.Result);
            default:
                return null;
        }
    }

    public static List<ChatMessage> DeserializeSession(string json, ILogger? logger = null)
    {
        var result = new List<ChatMessage>();
        if (string.IsNullOrWhiteSpace(json)) return result;

        try
        {
            var items = JsonSerializer.Deserialize<List<SerializedMessageItem>>(json);
            if (items == null) return result;

            foreach (var item in items)
            {
                var role = new ChatRole(item.Role);
                var contents = new List<AIContent>();

                if (item.Contents != null)
                {
                    foreach (var serialized in item.Contents)
                    {
                        var restored = FromSerializedContent(serialized);
                        if (restored != null)
                        {
                            contents.Add(restored);
                        }
                    }

                    result.Add(new ChatMessage(role, contents) { AuthorName = item.AuthorName });
                    continue;
                }

                // Legacy checkpoint format (text, then calls, then results).
                if (!string.IsNullOrEmpty(item.Text))
                {
                    contents.Add(new TextContent(item.Text));
                }

                if (item.FunctionCalls != null)
                {
                    foreach (var fc in item.FunctionCalls)
                    {
                        var args = fc.Arguments != null ? JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(fc.Arguments)) : null;
                        contents.Add(new FunctionCallContent(fc.CallId ?? Guid.NewGuid().ToString("N"), fc.Name, args));
                    }
                }

                if (item.FunctionResults != null)
                {
                    foreach (var fr in item.FunctionResults)
                    {
                        contents.Add(new FunctionResultContent(fr.CallId ?? Guid.NewGuid().ToString("N"), fr.Result));
                    }
                }

                result.Add(new ChatMessage(role, contents));
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to deserialize agent session JSON");
        }

        return result;
    }

    public static void CompactOlderToolResults(IList<ChatMessage> messages, int? keepVerbatimCount = null, LocalOptimizationFlags? flags = null)
    {
        ToolResultCompactor.Compact(messages, flags, keepVerbatimCount);
    }

    /// <summary>
    /// Resolves the maximum iterations per request, giving absolute precedence to profile.MaxTurns
    /// over harnessConfig.MaxTurns to avoid premature agent stalls.
    /// </summary>
    public static int ResolveMaxTurns(CrucibleHarnessConfig harnessConfig, AgentProfile profile)
    {
        ArgumentNullException.ThrowIfNull(harnessConfig);
        ArgumentNullException.ThrowIfNull(profile);

        return (profile.MaxTurns.HasValue && profile.MaxTurns.Value > 0)
            ? profile.MaxTurns.Value
            : (harnessConfig.MaxTurns > 0 ? harnessConfig.MaxTurns : CrucibleHarnessConfig.DefaultMaxTurns);
    }

    private record SerializedMessageItem(
        string Role,
        string? Text,
        List<SerializedFunctionCall>? FunctionCalls,
        List<SerializedFunctionResult>? FunctionResults,
        string? AuthorName = null,
        List<SerializedContent>? Contents = null);

    private record SerializedContent(
        string Type,
        string? Text = null,
        string? CallId = null,
        string? Name = null,
        object? Arguments = null,
        string? Result = null,
        string? ProtectedData = null);

    private record SerializedFunctionCall(string? CallId, string Name, object? Arguments);
    private record SerializedFunctionResult(string? CallId, string? Result);
}
