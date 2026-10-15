using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    private readonly IChatClient? _baseChatClient;
    private readonly IOllamaChatClientFactory? _chatClientFactory;
    private readonly IExecutionEnvironment? _executionEnv;
    private readonly IExecutionEnvironmentProvider? _envProvider;
    private readonly IMcpClientManager? _mcpManager;
    private readonly IEditorConfigService _editorConfigService;
    private readonly IEditorConfigFileApplier _editorConfigFileApplier;
    private readonly IWebSearchService _webSearchService;
    private readonly IWorkspaceSymbolService _symbolService;
    private readonly SkillsService _skillsService;
    private readonly AgentsMdService _agentsMdService;
    private readonly IAgentActivityStreamer? _activityStreamer;
    private readonly SessionTranscriptService? _transcriptService;
    private readonly IWorkspacePathResolver _pathResolver;
    private readonly IGlobalOptimizationSettingsService? _globalOptimizationSettings;
    private readonly ILogger<CrucibleAgentHarness> _logger;
    private readonly LlmProviderOptions? _llmOptions;

    public string HarnessId => "crucible";

    public CrucibleAgentHarness(
        IWorkspacePathResolver pathResolver,
        IChatClient? baseChatClient = null,
        IOllamaChatClientFactory? chatClientFactory = null,
        IExecutionEnvironment? executionEnv = null,
        IExecutionEnvironmentProvider? envProvider = null,
        IMcpClientManager? mcpManager = null,
        IEditorConfigService? editorConfigService = null,
        IEditorConfigFileApplier? editorConfigFileApplier = null,
        IWebSearchService? webSearchService = null,
        IWorkspaceSymbolService? symbolService = null,
        SkillsService? skillsService = null,
        AgentsMdService? agentsMdService = null,
        IAgentActivityStreamer? activityStreamer = null,
        SessionTranscriptService? transcriptService = null,
        IGlobalOptimizationSettingsService? globalOptimizationSettings = null,
        ILogger<CrucibleAgentHarness>? logger = null,
        Microsoft.Extensions.Options.IOptions<LlmProviderOptions>? llmOptions = null)
    {
        ArgumentNullException.ThrowIfNull(pathResolver);
        if (baseChatClient == null && chatClientFactory == null)
        {
            throw new ArgumentNullException(nameof(baseChatClient), "Either baseChatClient or chatClientFactory must be provided.");
        }

        _baseChatClient = baseChatClient;
        _chatClientFactory = chatClientFactory;
        _executionEnv = executionEnv;
        _envProvider = envProvider;
        _mcpManager = mcpManager;
        _editorConfigService = editorConfigService ?? new EditorConfigService();
        _editorConfigFileApplier = editorConfigFileApplier ?? new EditorConfigFileApplier();
        _webSearchService = webSearchService ?? new MockWebSearchService();
        _symbolService = symbolService ?? new RoslynSymbolIndexProvider();
        _skillsService = skillsService ?? new SkillsService();
        _agentsMdService = agentsMdService ?? new AgentsMdService();
        _activityStreamer = activityStreamer;
        _transcriptService = transcriptService;
        _pathResolver = pathResolver;
        _globalOptimizationSettings = globalOptimizationSettings;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<CrucibleAgentHarness>.Instance;
        _llmOptions = llmOptions?.Value;
    }

    public CrucibleAgentHarness(
        IChatClient baseChatClient,
        IWorkspacePathResolver pathResolver)
        : this(pathResolver, baseChatClient: baseChatClient)
    {
    }

    public async Task<AgentRunResult> RunStageAsync(AgentSessionContext context, CancellationToken cancellationToken = default)
    {
        context.OptimizationFlags = context.OptimizationFlags ?? context.Profile.OptimizationFlags ?? _globalOptimizationSettings?.GetFlags() ?? new LocalOptimizationFlags();
        var stopwatch = Stopwatch.StartNew();
        long promptTokens = 0;
        long completionTokens = 0;
        var sessionId = Guid.NewGuid().ToString("N");

        await BroadcastAsync(sessionId, context.ForgeTask.Id, context.StageId, AgentActivityKind.StageStarted, $"Starting stage '{context.StageId}'", cancellationToken);

        var workspacePath = !string.IsNullOrEmpty(context.WorkspacePath)
            ? context.WorkspacePath
            : (context.Workspace?.RepositoryPath ?? Directory.GetCurrentDirectory());

        // 1. Parse database-driven HarnessConfigurationJson if provided
        var harnessConfig = ParseHarnessConfig(context.Profile.HarnessConfigurationJson);

        var effectiveEnv = (context.Workspace != null && _envProvider != null)
            ? _envProvider.GetEnvironment(context.Workspace, context.Profile.SecurityTier, context.ForgeTask.Id)
            : _executionEnv;

        var projectConfig = CodeStackPresets.ResolveConfig(workspacePath, null);
        var buildTimeout = context.Workspace?.BuildTimeoutSeconds ?? projectConfig.BuildTimeoutSeconds;
        var testTimeout = context.Workspace?.TestTimeoutSeconds ?? projectConfig.TestTimeoutSeconds;

        // 2. Initialize Tools & Lenient Dispatcher
        var canonicalTools = new CanonicalTools(
            workspacePath,
            _pathResolver,
            effectiveEnv,
            _editorConfigService,
            _editorConfigFileApplier,
            _webSearchService,
            buildTimeoutSeconds: buildTimeout,
            testTimeoutSeconds: testTimeout,
            workspaceId: context.Workspace?.Id,
            taskId: context.ForgeTask.Id,
            isReadOnly: context.Profile.Mode == AgentMode.ReadOnly || context.Profile.SecurityTier == SandboxSecurityTier.RestrictedReadOnly);

        var dispatcher = new LenientToolDispatcher(canonicalTools);

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
                    Handler = (args, ct) => Task.FromResult($"Custom tool '{customTool.Name}' executed with {args.Count} parameter(s).")
                };
                dispatcher.RegisterTool(toolDef);
            }
        }

        // Register external MCP tools if configured
        if (_mcpManager != null)
        {
            var mcpServers = await _mcpManager.GetConfiguredServersAsync(workspacePath, cancellationToken);
            foreach (var (serverName, def) in mcpServers)
            {
                var toolDef = new ToolDefinition
                {
                    Name = $"mcp_{serverName}",
                    Description = $"External MCP server tool '{serverName}'. Command: {def.Command} {string.Join(" ", def.Args)}",
                    Handler = async (args, ct) =>
                    {
                        if (effectiveEnv != null && !string.IsNullOrWhiteSpace(def.Command))
                        {
                            var cmd = $"{def.Command} {string.Join(" ", def.Args)}";
                            var res = await effectiveEnv.ExecuteAsync(cmd, workspacePath, cancellationToken: ct);
                            return string.IsNullOrEmpty(res.StandardError)
                                ? res.StandardOutput
                                : $"{res.StandardOutput}\n{res.StandardError}";
                        }
                        return $"MCP server '{serverName}' invoked with {args.Count} parameter(s).";
                    }
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
        var taskDocPath = context.PipelineContext?.DataBag.Get<string>("TaskMarkdownPath");
        var taskDocAttr = !string.IsNullOrWhiteSpace(taskDocPath) ? $" path=\"{taskDocPath}\"" : string.Empty;
        var taskScope = $"<task id=\"{context.ForgeTask.Id}\" title=\"{context.ForgeTask.Title}\"{taskDocAttr}>\n{context.ForgeTask.Description}\n</task>";
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
            chatHistory = DeserializeSession(context.SerializedStateJson);
            // Legacy checkpoints stored the system prompt as the first message; it is rebuilt each run.
            chatHistory.RemoveAll(m => m.Role == ChatRole.System);
        }
        else
        {
            chatHistory = new List<ChatMessage>
            {
                new(ChatRole.User, !string.IsNullOrWhiteSpace(context.BootstrapPrompt) ? context.BootstrapPrompt : "Please inspect the requirements and begin execution.")
            };
        }

        // 8. Preemptive Resumption: Inject user answer if resuming from 'ask'
        if (!string.IsNullOrWhiteSpace(context.PendingHumanAnswer))
        {
            chatHistory.Add(new ChatMessage(ChatRole.User, $"[Human Answer]: {context.PendingHumanAnswer}"));
        }

        // 9. Resolve effective ChatClient for the profile and assemble middlewares
        var baseClient = _chatClientFactory != null
            ? _chatClientFactory.CreateClient(context.Profile)
            : (_baseChatClient ?? throw new InvalidOperationException("No IChatClient or IOllamaChatClientFactory configured for CrucibleAgentHarness."));

        var effectiveModel = !string.IsNullOrWhiteSpace(context.Profile.ModelName)
            ? context.Profile.ModelName
            : baseClient.GetService<ChatClientMetadata>()?.DefaultModelId;

        string latestSerializedState = SerializeSession(chatHistory);

        // The tracer sits closest to the model (below persistence and compaction) so that it records the exact
        // requests sent to the LLM: everything MAF injects, plus the history after compaction.
        var tracingClient = new ThinkingAndActivityTracingChatClient(baseClient);

        var persistingClient = new PerServiceCallChatHistoryPersistingChatClient(
            tracingClient,
            history =>
            {
                chatHistory.Clear();
                chatHistory.AddRange(history);
                latestSerializedState = SerializeSession(chatHistory);
                return Task.CompletedTask;
            });

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

        IAsyncEnumerator<AgentResponseUpdate>? enumerator = null;
        try
        {
            var stream = agent.RunStreamingAsync(chatHistory, session, cancellationToken: cancellationToken);
            enumerator = stream.GetAsyncEnumerator(cancellationToken);
        }
        catch (RequestChangesException reqChange)
        {
            stopwatch.Stop();
            latestSerializedState = SerializeSession(chatHistory);

            if (context.PipelineContext != null)
            {
                context.PipelineContext.DataBag.Set("ReviewerFeedback", reqChange.Feedback);
            }

            await BroadcastAsync(sessionId, context.ForgeTask.Id, context.StageId, AgentActivityKind.StageCompleted, $"Changes requested: {reqChange.Feedback}", cancellationToken);
            await SaveTranscriptAsync(context, workspacePath, chatHistory, stopwatch.ElapsedMilliseconds, promptTokens, completionTokens, AgentRunStatus.Completed, cancellationToken, tracingClient, harnessOptions, activeSkills);

            return AgentRunResult.Completed(
                new AgentRunMetrics(stopwatch.ElapsedMilliseconds, promptTokens, completionTokens, promptTokens + completionTokens),
                commitHash: null,
                updatedState: latestSerializedState,
                summary: $"Reviewer requested changes: {reqChange.Feedback}",
                targetRouting: reqChange.TargetStageId);
        }
        catch (HumanInterruptionException interruption)
        {
            stopwatch.Stop();
            latestSerializedState = SerializeSession(chatHistory);

            await BroadcastAsync(sessionId, context.ForgeTask.Id, context.StageId, AgentActivityKind.Interrupted, interruption.Question.Prompt, cancellationToken);
            await SaveTranscriptAsync(context, workspacePath, chatHistory, stopwatch.ElapsedMilliseconds, promptTokens, completionTokens, AgentRunStatus.WaitingForHuman, cancellationToken, tracingClient, harnessOptions, activeSkills);

            return AgentRunResult.WaitingForHuman(
                interruption.Question.Prompt,
                new AgentRunMetrics(stopwatch.ElapsedMilliseconds, promptTokens, completionTokens, promptTokens + completionTokens),
                updatedState: latestSerializedState,
                summary: "Crucible paused awaiting human clarification");
        }
        catch (Exception ex) when (ex is NotImplementedException or NotSupportedException)
        {
            streamingFailed = true;
        }

        if (!streamingFailed && enumerator != null)
        {
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
                        stopwatch.Stop();
                        latestSerializedState = SerializeSession(chatHistory);

                        if (context.PipelineContext != null)
                        {
                            context.PipelineContext.DataBag.Set("ReviewerFeedback", reqChange.Feedback);
                        }

                        await BroadcastAsync(sessionId, context.ForgeTask.Id, context.StageId, AgentActivityKind.StageCompleted, $"Changes requested: {reqChange.Feedback}", cancellationToken);
                        await SaveTranscriptAsync(context, workspacePath, chatHistory, stopwatch.ElapsedMilliseconds, promptTokens, completionTokens, AgentRunStatus.Completed, cancellationToken, tracingClient, harnessOptions, activeSkills);

                        return AgentRunResult.Completed(
                            new AgentRunMetrics(stopwatch.ElapsedMilliseconds, promptTokens, completionTokens, promptTokens + completionTokens),
                            commitHash: null,
                            updatedState: latestSerializedState,
                            summary: $"Reviewer requested changes: {reqChange.Feedback}",
                            targetRouting: reqChange.TargetStageId);
                    }
                    catch (HumanInterruptionException interruption)
                    {
                        stopwatch.Stop();
                        latestSerializedState = SerializeSession(chatHistory);

                        await BroadcastAsync(sessionId, context.ForgeTask.Id, context.StageId, AgentActivityKind.Interrupted, interruption.Question.Prompt, cancellationToken);
                        await SaveTranscriptAsync(context, workspacePath, chatHistory, stopwatch.ElapsedMilliseconds, promptTokens, completionTokens, AgentRunStatus.WaitingForHuman, cancellationToken, tracingClient, harnessOptions, activeSkills);

                        return AgentRunResult.WaitingForHuman(
                            interruption.Question.Prompt,
                            new AgentRunMetrics(stopwatch.ElapsedMilliseconds, promptTokens, completionTokens, promptTokens + completionTokens),
                            updatedState: latestSerializedState,
                            summary: "Crucible paused awaiting human clarification");
                    }
                    catch (Exception ex) when (ex is NotImplementedException or NotSupportedException)
                    {
                        streamingFailed = true;
                        break;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogError(ex, "Error during Crucible streaming LLM invocation");
                        stopwatch.Stop();
                        await BroadcastAsync(sessionId, context.ForgeTask.Id, context.StageId, AgentActivityKind.Error, ex.Message, cancellationToken);
                        await SaveTranscriptAsync(context, workspacePath, chatHistory, stopwatch.ElapsedMilliseconds, promptTokens, completionTokens, AgentRunStatus.Failed, cancellationToken, tracingClient, harnessOptions, activeSkills);

                        return AgentRunResult.Failed(
                            ex.Message,
                            new AgentRunMetrics(stopwatch.ElapsedMilliseconds, promptTokens, completionTokens, promptTokens + completionTokens),
                            latestSerializedState);
                    }

                    if (!hasMore) break;

                    var update = enumerator.Current;

                    if (update.Contents != null)
                    {
                        foreach (var content in update.Contents)
                        {
                            if (content is FunctionCallContent fc)
                            {
                                var canonicalName = dispatcher.ResolveToolName(fc.Name);
                                var argsJson = fc.Arguments != null ? JsonSerializer.Serialize(fc.Arguments) : "{}";
                                await BroadcastAsync(sessionId, context.ForgeTask.Id, context.StageId, AgentActivityKind.ToolStarted, $"{canonicalName}: {argsJson}", cancellationToken);
                            }
                            else if (content is FunctionResultContent frc)
                            {
                                var resultStr = frc.Result?.ToString() ?? string.Empty;
                                await BroadcastAsync(sessionId, context.ForgeTask.Id, context.StageId, AgentActivityKind.ToolCompleted, resultStr, cancellationToken);
                            }
                            else if (content is TextReasoningContent reasoning && !string.IsNullOrEmpty(reasoning.Text))
                            {
                                accumulatedReasoning.Append(reasoning.Text);
                                await BroadcastAsync(sessionId, context.ForgeTask.Id, context.StageId, AgentActivityKind.ThinkingDelta, reasoning.Text, cancellationToken);
                            }
                            else if (content is UsageContent uc && uc.Details != null)
                            {
                                if (uc.Details.InputTokenCount.HasValue) promptTokens = Math.Max(promptTokens, uc.Details.InputTokenCount.Value);
                                if (uc.Details.OutputTokenCount.HasValue) completionTokens = Math.Max(completionTokens, uc.Details.OutputTokenCount.Value);
                            }
                        }
                    }

                    if (update.RawRepresentation is ChatResponseUpdate chatUpdate && chatUpdate.AdditionalProperties != null)
                    {
                        if (chatUpdate.AdditionalProperties.TryGetValue("usage", out var rawUsage) && rawUsage is JsonElement je)
                        {
                            if (je.TryGetProperty("prompt_tokens", out var pt) && pt.TryGetInt64(out var ptVal)) promptTokens = Math.Max(promptTokens, ptVal);
                            if (je.TryGetProperty("completion_tokens", out var ct) && ct.TryGetInt64(out var ctVal)) completionTokens = Math.Max(completionTokens, ctVal);
                        }

                        if (chatUpdate.AdditionalProperties.TryGetValue("target_routing", out var trObj) && trObj is string strVal)
                        {
                            targetRouting = strVal;
                        }
                    }

                    if (!string.IsNullOrEmpty(update.Text))
                    {
                        accumulatedText.Append(update.Text);

                        await BroadcastAsync(sessionId, context.ForgeTask.Id, context.StageId, AgentActivityKind.MessageDelta, update.Text, cancellationToken);

                        if (context.OptimizationFlags.LoopCircuitBreaker && circuitBreaker.DetectStreamingRepetition(accumulatedText.ToString()))
                        {
                            _logger.LogWarning("Circuit breaker detected repeating n-gram monologue in streaming output. Aborting turn.");
                            break;
                        }
                    }
                }
            }
            finally
            {
                await enumerator.DisposeAsync();
            }
        }

        if (streamingFailed)
        {
            try
            {
                var response = await agent.RunAsync(chatHistory, session, cancellationToken: cancellationToken);
                foreach (var update in response.ToAgentResponseUpdates())
                {
                    if (update.Contents != null)
                    {
                        foreach (var content in update.Contents)
                        {
                            if (content is FunctionCallContent fc)
                            {
                                var canonicalName = dispatcher.ResolveToolName(fc.Name);
                                var argsJson = fc.Arguments != null ? JsonSerializer.Serialize(fc.Arguments) : "{}";
                                await BroadcastAsync(sessionId, context.ForgeTask.Id, context.StageId, AgentActivityKind.ToolStarted, $"{canonicalName}: {argsJson}", cancellationToken);
                            }
                            else if (content is FunctionResultContent frc)
                            {
                                var resultStr = frc.Result?.ToString() ?? string.Empty;
                                await BroadcastAsync(sessionId, context.ForgeTask.Id, context.StageId, AgentActivityKind.ToolCompleted, resultStr, cancellationToken);
                            }
                            else if (content is TextReasoningContent reasoning && !string.IsNullOrEmpty(reasoning.Text))
                            {
                                accumulatedReasoning.Append(reasoning.Text);
                                await BroadcastAsync(sessionId, context.ForgeTask.Id, context.StageId, AgentActivityKind.ThinkingDelta, reasoning.Text, cancellationToken);
                            }
                            else if (content is UsageContent uc && uc.Details != null)
                            {
                                if (uc.Details.InputTokenCount.HasValue) promptTokens = Math.Max(promptTokens, uc.Details.InputTokenCount.Value);
                                if (uc.Details.OutputTokenCount.HasValue) completionTokens = Math.Max(completionTokens, uc.Details.OutputTokenCount.Value);
                            }
                        }
                    }

                    if (!string.IsNullOrEmpty(update.Text))
                    {
                        accumulatedText.Append(update.Text);
                        await BroadcastAsync(sessionId, context.ForgeTask.Id, context.StageId, AgentActivityKind.MessageDelta, update.Text, cancellationToken);
                    }
                }
            }
            catch (RequestChangesException reqChange)
            {
                stopwatch.Stop();
                latestSerializedState = SerializeSession(chatHistory);

                if (context.PipelineContext != null)
                {
                    context.PipelineContext.DataBag.Set("ReviewerFeedback", reqChange.Feedback);
                }

                await BroadcastAsync(sessionId, context.ForgeTask.Id, context.StageId, AgentActivityKind.StageCompleted, $"Changes requested: {reqChange.Feedback}", cancellationToken);
                await SaveTranscriptAsync(context, workspacePath, chatHistory, stopwatch.ElapsedMilliseconds, promptTokens, completionTokens, AgentRunStatus.Completed, cancellationToken, tracingClient, harnessOptions, activeSkills);

                return AgentRunResult.Completed(
                    new AgentRunMetrics(stopwatch.ElapsedMilliseconds, promptTokens, completionTokens, promptTokens + completionTokens),
                    commitHash: null,
                    updatedState: latestSerializedState,
                    summary: $"Reviewer requested changes: {reqChange.Feedback}",
                    targetRouting: reqChange.TargetStageId);
            }
            catch (HumanInterruptionException interruption)
            {
                stopwatch.Stop();
                latestSerializedState = SerializeSession(chatHistory);

                await BroadcastAsync(sessionId, context.ForgeTask.Id, context.StageId, AgentActivityKind.Interrupted, interruption.Question.Prompt, cancellationToken);
                await SaveTranscriptAsync(context, workspacePath, chatHistory, stopwatch.ElapsedMilliseconds, promptTokens, completionTokens, AgentRunStatus.WaitingForHuman, cancellationToken, tracingClient, harnessOptions, activeSkills);

                return AgentRunResult.WaitingForHuman(
                    interruption.Question.Prompt,
                    new AgentRunMetrics(stopwatch.ElapsedMilliseconds, promptTokens, completionTokens, promptTokens + completionTokens),
                    updatedState: latestSerializedState,
                    summary: "Crucible paused awaiting human clarification");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error during Crucible non-streaming LLM invocation");
                stopwatch.Stop();
                await BroadcastAsync(sessionId, context.ForgeTask.Id, context.StageId, AgentActivityKind.Error, ex.Message, cancellationToken);
                await SaveTranscriptAsync(context, workspacePath, chatHistory, stopwatch.ElapsedMilliseconds, promptTokens, completionTokens, AgentRunStatus.Failed, cancellationToken, tracingClient, harnessOptions, activeSkills);

                return AgentRunResult.Failed(
                    ex.Message,
                    new AgentRunMetrics(stopwatch.ElapsedMilliseconds, promptTokens, completionTokens, promptTokens + completionTokens),
                    latestSerializedState);
            }
        }

        if (accumulatedText.Length > 0 && chatHistory.LastOrDefault()?.Role != ChatRole.Assistant)
        {
            var assistantMsg = new ChatMessage(ChatRole.Assistant, accumulatedText.ToString());
            if (accumulatedReasoning.Length > 0)
            {
                assistantMsg.Contents.Add(new TextReasoningContent(accumulatedReasoning.ToString()));
            }
            chatHistory.Add(assistantMsg);
            latestSerializedState = SerializeSession(chatHistory);
        }

        stopwatch.Stop();
        var lastAssistantMessage = chatHistory.LastOrDefault(m => m.Role == ChatRole.Assistant)?.Text;

        if (string.IsNullOrEmpty(targetRouting) && !string.IsNullOrEmpty(lastAssistantMessage))
        {
            var match = Regex.Match(lastAssistantMessage, @"<request_changes>(.*?)</request_changes>", RegexOptions.Singleline);
            if (match.Success)
            {
                var feedback = match.Groups[1].Value.Trim();
                targetRouting = "implementation";
                if (context.PipelineContext != null)
                {
                    context.PipelineContext.DataBag.Set("ReviewerFeedback", feedback);
                }
            }
        }

        if (canonicalTools.PendingProposals.Count > 0 && context.PipelineContext != null)
        {
            var existingProposals = context.PipelineContext.DataBag.Get<List<TaskProposal>>("PendingProposals") ?? new List<TaskProposal>();
            existingProposals.AddRange(canonicalTools.PendingProposals);
            context.PipelineContext.DataBag.Set("PendingProposals", existingProposals);
        }

        await BroadcastAsync(sessionId, context.ForgeTask.Id, context.StageId, AgentActivityKind.StageCompleted, lastAssistantMessage ?? "Stage finished", cancellationToken);

        await SaveTranscriptAsync(context, workspacePath, chatHistory, stopwatch.ElapsedMilliseconds, promptTokens, completionTokens, AgentRunStatus.Completed, cancellationToken, tracingClient, harnessOptions, activeSkills);

        return AgentRunResult.Completed(
            new AgentRunMetrics(stopwatch.ElapsedMilliseconds, promptTokens, completionTokens, promptTokens + completionTokens),
            commitHash: null,
            updatedState: latestSerializedState,
            summary: lastAssistantMessage ?? "Stage completed successfully.",
            targetRouting: targetRouting);
    }

    private async Task BroadcastAsync(string sessionId, int taskId, string stageId, AgentActivityKind kind, string content, CancellationToken ct)
    {
        if (_activityStreamer != null)
        {
            var evt = new AgentActivityEvent(sessionId, taskId, stageId, kind, content, DateTime.UtcNow);
            await _activityStreamer.BroadcastAsync(evt, ct);
        }
    }

    private async Task SaveTranscriptAsync(
        AgentSessionContext context,
        string workspacePath,
        IReadOnlyList<ChatMessage> chatHistory,
        long elapsedMs,
        long promptTokens,
        long completionTokens,
        AgentRunStatus status,
        CancellationToken cancellationToken,
        ThinkingAndActivityTracingChatClient? tracingClient = null,
        HarnessAgentOptions? harnessOptions = null,
        IReadOnlyList<string>? activeSkills = null)
    {
        if (_transcriptService == null) return;

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
                ModelCalls: tracingClient?.ModelCalls);

            var transcriptDir = Path.Combine(workspacePath, "docs", "transcripts");
            await _transcriptService.SaveTranscriptAsync(meta, entries, transcriptDir, cancellationToken);
        }
        catch (Exception tex)
        {
            _logger.LogWarning(tex, "Failed to persist session transcript for task #{TaskId} with status {Status}", context.ForgeTask.Id, status);
        }
    }

    public static List<TranscriptEntry> BuildTranscriptEntries(IReadOnlyList<ChatMessage> chatHistory)
    {
        var entries = new List<TranscriptEntry>();
        var now = DateTime.UtcNow;

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
                        ToolName: fr.CallId,
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

    private static CrucibleHarnessConfig ParseHarnessConfig(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new CrucibleHarnessConfig();
        }

        try
        {
            return JsonSerializer.Deserialize<CrucibleHarnessConfig>(json) ?? new CrucibleHarnessConfig();
        }
        catch
        {
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
            "triage" or "stage-0" =>
                "Analyze the reported task against the repository base branch. Determine affected files and acceptance criteria. Call propose_tasks to recommend structured backlog items. You have read-only access and cannot modify code or access external networks.",
            "task-review" or "stage-1" =>
                "You are a Staff Software Architect & Requirements Investigator. Thoroughly investigate requirements and inspect the codebase using 'read' and 'bash'. MANDATORY FILE EDIT: Edit the task specification document on disk in docs/tasks/ using 'edit' or 'write' with your approved architecture, technical plan, and acceptance criteria. Your disk changes will be read back upon stage completion to update the task specification. Proactively uncover ambiguities: invoke 'ask' immediately if needed. Do not guess. Do not write production code.",
            "implementation" or "stage-2" =>
                "You are a Senior Software Engineer & TDD Practitioner. Implement clean, robust code according to the task specification in docs/tasks/ and the approved plan. Keep blast radius minimal. Run builds and tests incrementally with bash. Fix all compiler errors and test failures. If breaking blockers arise, call 'ask'.",
            "code-review-fix" or "stage-3" =>
                "You are a Principal Code Reviewer and Quality Gatekeeper. Review the git diff against the base branch. Compare against the specification in docs/tasks/ and the implementation claims. Apply precision polish directly using 'edit'. Rejection is strictly reserved for major tasks or missing features: invoke 'request_changes' or conclude with <request_changes>[feedback]</request_changes>.",
            _ => "Execute assigned engineering tasks diligently using canonical tools."
        };
    }

    private static IList<AITool> BuildAIFunctionList(
        CanonicalTools tools,
        IReadOnlyList<string> permittedTools,
        LenientToolDispatcher dispatcher,
        List<CustomToolConfig>? customTools = null,
        IReadOnlyDictionary<string, string>? toolDirectives = null)
    {
        var aiFunctions = CanonicalAIFunctionFactory.CreateTools(tools, permittedTools).Cast<AIFunction>().ToList();
        var knownNames = new HashSet<string>(aiFunctions.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var tool in permittedTools)
        {
            // Only expose non-canonical tools that are actually registered (custom DB tools, MCP servers);
            // names such as the legacy read_skill_resource/run_skill_script have no handler and are skipped.
            if (!knownNames.Contains(tool) && dispatcher.TryGetTool(tool, out _))
            {
                var custom = customTools?.FirstOrDefault(c => string.Equals(c.Name, tool, StringComparison.OrdinalIgnoreCase));
                var desc = custom?.Description ?? $"Execute tool '{tool}'";
                var toolName = tool;
                aiFunctions.Add(AIFunctionFactory.Create(
                    async (object? args, CancellationToken ct) =>
                    {
                        var argsJson = args != null ? JsonSerializer.Serialize(args) : "{}";
                        return await dispatcher.DispatchAsync(toolName, argsJson, ct);
                    },
                    toolName,
                    desc));
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

    /// <summary>Overrides the description exposed to the model (database-configured tool directives).</summary>
    private sealed class DescriptionOverrideAIFunction(AIFunction inner, string description) : DelegatingAIFunction(inner)
    {
        public override string Description => description;
    }

    public static string SerializeSession(List<ChatMessage> messages)
    {
        var simpleList = messages.Select(m => new SerializedMessageItem(
            m.Role.Value,
            m.Text,
            m.Contents.OfType<FunctionCallContent>().Select(f => new SerializedFunctionCall(f.CallId, f.Name, f.Arguments)).ToList(),
            m.Contents.OfType<FunctionResultContent>().Select(r => new SerializedFunctionResult(r.CallId, r.Result?.ToString())).ToList()
        )).ToList();

        return JsonSerializer.Serialize(simpleList, new JsonSerializerOptions { WriteIndented = false });
    }

    public static List<ChatMessage> DeserializeSession(string json)
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
        catch { }

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
        List<SerializedFunctionResult>? FunctionResults);

    private record SerializedFunctionCall(string? CallId, string Name, object? Arguments);
    private record SerializedFunctionResult(string? CallId, string? Result);
}
