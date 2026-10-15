using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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

    public string HarnessId => "crucible";

    [GeneratedRegex(@"<tool_call>\s*({.*?})\s*</tool_call>", RegexOptions.Singleline)]
    private static partial Regex ToolCallTagRegex();

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
        ILogger<CrucibleAgentHarness>? logger = null)
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

        // Discover skills and format catalog using minimalist Pi syntax (~25 tokens/skill)
        var skills = await _skillsService.DiscoverSkillsAsync(workspacePath, cancellationToken: cancellationToken);
        var skillsCatalog = _skillsService.FormatSkillsCatalogPrompt(skills);

        // Register framework skill tools (load_skill, read_skill_resource, run_skill_script) into dispatcher
        var skillsContext = await _skillsService.GetSkillsContextAsync(workspacePath, cancellationToken: cancellationToken);
        if (skillsContext?.Tools != null && skillsContext.Tools.Any())
        {
            dispatcher.RegisterAITools(skillsContext.Tools);
        }

        string discoveredSymbols = string.Empty;
        if (context.OptimizationFlags.DeterministicSymbolMap)
        {
            var taskText = $"{context.ForgeTask.Title} {context.ForgeTask.Description}";
            discoveredSymbols = await _symbolService.GetMatchingSymbolsPromptAsync(workspacePath, taskText, context.OptimizationFlags, cancellationToken);
        }

        // 4. Resolve Active Stage Permitted Tools (Database/Profile Configurable)
        var permittedTools = ResolvePermittedTools(harnessConfig, context.Profile, context.RequiredMode);

        // 5. Build dynamic tool descriptions prompt
        var customToolsPrompt = BuildDynamicToolsPrompt(dispatcher, permittedTools, harnessConfig.ToolDirectives);

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
            customToolsPrompt: customToolsPrompt,
            customRulesPrompt: harnessConfig.CustomSystemRules,
            customRolePersona: customPersona,
            workingDirectory: agentWorkingDir);

        // 7. Restore or Initialize Conversation History
        List<ChatMessage> chatHistory;
        if (!string.IsNullOrEmpty(context.SerializedStateJson))
        {
            chatHistory = DeserializeSession(context.SerializedStateJson);
        }
        else
        {
            chatHistory = new List<ChatMessage>
            {
                new(ChatRole.System, stabilizedPrefix),
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

        var persistingClient = new PerServiceCallChatHistoryPersistingChatClient(
            baseClient,
            history =>
            {
                latestSerializedState = SerializeSession(history.ToList());
                return Task.CompletedTask;
            });

        IChatClient activeClient = new ThinkingTraceCompactionChatClient(persistingClient, context.OptimizationFlags);

        // 10. Main Agentic Reasoning Loop with Real-Time Streaming
        int maxTurns = harnessConfig.MaxTurns > 0
            ? harnessConfig.MaxTurns
            : (context.Profile.MaxTurns.HasValue && context.Profile.MaxTurns.Value > 0
                ? context.Profile.MaxTurns.Value
                : CrucibleHarnessConfig.DefaultMaxTurns);
        int currentTurn = 0;
        string? targetRouting = null;

        while (currentTurn < maxTurns && !cancellationToken.IsCancellationRequested)
        {
            currentTurn++;
            toolResultCompactor.Compact(chatHistory);

            var chatOptions = new ChatOptions
            {
                ModelId = !string.IsNullOrWhiteSpace(effectiveModel) ? effectiveModel : null,
                Temperature = (float?)context.Profile.Temperature,
                Tools = BuildAIFunctionList(permittedTools)
            };

            if (context.OptimizationFlags.GuidedGrammarDecoding)
            {
                GuidedGrammarHelper.ApplyGrammarConstraints(chatOptions, context.OptimizationFlags);
            }

            var accumulatedText = new StringBuilder();
            var functionCalls = new List<FunctionCallContent>();
            bool isThinking = false;

            try
            {
                await foreach (var update in activeClient.GetStreamingResponseAsync(chatHistory, chatOptions, cancellationToken))
                {
                    if (update.Contents != null)
                    {
                        foreach (var content in update.Contents)
                        {
                            if (content is FunctionCallContent fc)
                            {
                                functionCalls.Add(fc);
                            }
                            else if (content is UsageContent uc && uc.Details != null)
                            {
                                if (uc.Details.InputTokenCount.HasValue) promptTokens = Math.Max(promptTokens, uc.Details.InputTokenCount.Value);
                                if (uc.Details.OutputTokenCount.HasValue) completionTokens = Math.Max(completionTokens, uc.Details.OutputTokenCount.Value);
                            }
                        }
                    }

                    if (update.AdditionalProperties != null && update.AdditionalProperties.TryGetValue("usage", out var rawUsage) && rawUsage is JsonElement usageJe)
                    {
                        if (usageJe.TryGetProperty("prompt_tokens", out var pt) && pt.TryGetInt64(out var ptVal)) promptTokens = Math.Max(promptTokens, ptVal);
                        if (usageJe.TryGetProperty("completion_tokens", out var ct) && ct.TryGetInt64(out var ctVal)) completionTokens = Math.Max(completionTokens, ctVal);
                    }

                    if (update.AdditionalProperties != null && update.AdditionalProperties.TryGetValue("target_routing", out var trObj) && trObj is string trStr)
                    {
                        targetRouting = trStr;
                    }

                    if (!string.IsNullOrEmpty(update.Text))
                    {
                        accumulatedText.Append(update.Text);

                        // Detect thinking tag transitions for streaming activity
                        if (update.Text.Contains("<think>")) isThinking = true;
                        
                        var kind = isThinking ? AgentActivityKind.ThinkingDelta : AgentActivityKind.MessageDelta;
                        await BroadcastAsync(sessionId, context.ForgeTask.Id, context.StageId, kind, update.Text, cancellationToken);

                        if (update.Text.Contains("</think>")) isThinking = false;

                        // Check circuit breaker repetition in streaming buffer
                        if (context.OptimizationFlags.LoopCircuitBreaker && circuitBreaker.DetectStreamingRepetition(accumulatedText.ToString()))
                        {
                            _logger.LogWarning("Circuit breaker detected repeating n-gram monologue in streaming output. Aborting turn.");
                            break;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error during Crucible streaming LLM invocation");
                stopwatch.Stop();
                await BroadcastAsync(sessionId, context.ForgeTask.Id, context.StageId, AgentActivityKind.Error, ex.Message, cancellationToken);

                await SaveTranscriptAsync(context, workspacePath, chatHistory, stopwatch.ElapsedMilliseconds, promptTokens, completionTokens, AgentRunStatus.Failed, cancellationToken);

                return AgentRunResult.Failed(
                    ex.Message,
                    new AgentRunMetrics(stopwatch.ElapsedMilliseconds, promptTokens, completionTokens, promptTokens + completionTokens),
                    latestSerializedState);
            }

            var textResult = accumulatedText.ToString();
            var assistantMsg = new ChatMessage(ChatRole.Assistant, textResult);
            foreach (var fc in functionCalls)
            {
                assistantMsg.Contents.Add(fc);
            }

            chatHistory.Add(assistantMsg);
            latestSerializedState = SerializeSession(chatHistory);

            // Also check for raw text fallback tool call emitted by models without native tool tokens
            if (functionCalls.Count == 0 && !string.IsNullOrEmpty(textResult))
            {
                var fallbackCall = TryExtractTextToolCall(textResult);
                if (fallbackCall != null)
                {
                    functionCalls.Add(fallbackCall);
                }
            }

            if (functionCalls.Count == 0)
            {
                // Agent finished its reasoning turn without requesting any further tool calls
                break;
            }

            // Process tool calls
            foreach (var call in functionCalls)
            {
                var canonicalName = dispatcher.ResolveToolName(call.Name);
                var argsJson = call.Arguments != null ? JsonSerializer.Serialize(call.Arguments) : "{}";

                await BroadcastAsync(sessionId, context.ForgeTask.Id, context.StageId, AgentActivityKind.ToolStarted, $"{canonicalName}: {argsJson}", cancellationToken);

                // Enforce Tool Whitelist Security Gate
                if (!permittedTools.Contains(canonicalName, StringComparer.OrdinalIgnoreCase))
                {
                    var securityReject = $"Security Error: Tool '{canonicalName}' is prohibited in stage '{context.StageId}'. Permitted tools: {string.Join(", ", permittedTools)}";
                    chatHistory.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(call.CallId ?? Guid.NewGuid().ToString("N"), securityReject)]));
                    await BroadcastAsync(sessionId, context.ForgeTask.Id, context.StageId, AgentActivityKind.ToolCompleted, securityReject, cancellationToken);
                    continue;
                }

                // Check Circuit Breaker for duplicate invocations
                var loopCheck = circuitBreaker.EvaluateToolInvocation(canonicalName, call.Arguments, null);
                if (loopCheck.IsBlocked)
                {
                    chatHistory.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(call.CallId ?? Guid.NewGuid().ToString("N"), loopCheck.CorrectiveMessage!)]));
                    await BroadcastAsync(sessionId, context.ForgeTask.Id, context.StageId, AgentActivityKind.ToolCompleted, loopCheck.CorrectiveMessage!, cancellationToken);
                    continue;
                }

                // Execute via LenientToolDispatcher
                string toolResult;
                var toolSw = Stopwatch.StartNew();
                try
                {
                    toolResult = await dispatcher.DispatchAsync(canonicalName, argsJson, cancellationToken);
                    toolSw.Stop();
                }
                catch (RequestChangesException reqChange)
                {
                    toolSw.Stop();
                    stopwatch.Stop();
                    latestSerializedState = SerializeSession(chatHistory);

                    if (context.PipelineContext != null)
                    {
                        context.PipelineContext.DataBag.Set("ReviewerFeedback", reqChange.Feedback);
                    }

                    await BroadcastAsync(sessionId, context.ForgeTask.Id, context.StageId, AgentActivityKind.StageCompleted, $"Changes requested: {reqChange.Feedback}", cancellationToken);

                    await SaveTranscriptAsync(context, workspacePath, chatHistory, stopwatch.ElapsedMilliseconds, promptTokens, completionTokens, AgentRunStatus.Completed, cancellationToken);

                    return AgentRunResult.Completed(
                        new AgentRunMetrics(stopwatch.ElapsedMilliseconds, promptTokens, completionTokens, promptTokens + completionTokens),
                        commitHash: null,
                        updatedState: latestSerializedState,
                        summary: $"Reviewer requested changes: {reqChange.Feedback}",
                        targetRouting: reqChange.TargetStageId);
                }
                catch (HumanInterruptionException interruption)
                {
                    toolSw.Stop();
                    stopwatch.Stop();
                    latestSerializedState = SerializeSession(chatHistory);

                    await BroadcastAsync(sessionId, context.ForgeTask.Id, context.StageId, AgentActivityKind.Interrupted, interruption.Question.Prompt, cancellationToken);

                    await SaveTranscriptAsync(context, workspacePath, chatHistory, stopwatch.ElapsedMilliseconds, promptTokens, completionTokens, AgentRunStatus.WaitingForHuman, cancellationToken);

                    return AgentRunResult.WaitingForHuman(
                        interruption.Question.Prompt,
                        new AgentRunMetrics(stopwatch.ElapsedMilliseconds, promptTokens, completionTokens, promptTokens + completionTokens),
                        updatedState: latestSerializedState,
                        summary: "Crucible paused awaiting human clarification");
                }
                catch (Exception ex)
                {
                    toolSw.Stop();
                    toolResult = $"Error executing tool '{canonicalName}': {ex.Message}";
                }

                chatHistory.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(call.CallId ?? Guid.NewGuid().ToString("N"), toolResult)]));
                latestSerializedState = SerializeSession(chatHistory);

                await BroadcastAsync(sessionId, context.ForgeTask.Id, context.StageId, AgentActivityKind.ToolCompleted, $"Completed in {toolSw.ElapsedMilliseconds}ms: {toolResult}", cancellationToken);
            }
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

        await SaveTranscriptAsync(context, workspacePath, chatHistory, stopwatch.ElapsedMilliseconds, promptTokens, completionTokens, AgentRunStatus.Completed, cancellationToken);

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
        CancellationToken cancellationToken)
    {
        if (_transcriptService == null) return;

        try
        {
            var entries = BuildTranscriptEntries(chatHistory);
            var meta = new TranscriptMetadata(
                context.ForgeTask.Id,
                context.StageId,
                context.Profile.Name,
                context.Profile.ModelName,
                elapsedMs,
                new AgentRunMetrics(elapsedMs, promptTokens, completionTokens, promptTokens + completionTokens),
                context.OptimizationFlags ?? new LocalOptimizationFlags(),
                status);

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

    private static string BuildDynamicToolsPrompt(
        LenientToolDispatcher dispatcher,
        IReadOnlyList<string> permittedTools,
        Dictionary<string, string> customDirectives)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<tools>");

        foreach (var tool in dispatcher.RegisteredTools)
        {
            if (permittedTools.Contains(tool.Name, StringComparer.OrdinalIgnoreCase))
            {
                var desc = customDirectives.TryGetValue(tool.Name, out var customDesc)
                    ? customDesc
                    : tool.Description;

                sb.AppendLine($"- {tool.Name}: {desc}");
            }
        }

        sb.AppendLine("</tools>");
        return sb.ToString().TrimEnd();
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

    private static IList<AITool> BuildAIFunctionList(IReadOnlyList<string> permittedTools)
    {
        var tools = new List<AITool>();
        foreach (var tool in permittedTools)
        {
            tools.Add(AIFunctionFactory.Create(() => "ok", tool));
        }
        return tools;
    }

    private static FunctionCallContent? TryExtractTextToolCall(string text)
    {
        var match = ToolCallTagRegex().Match(text);
        if (!match.Success) return null;

        try
        {
            var node = JsonNode.Parse(match.Groups[1].Value);
            var name = node?["name"]?.ToString();
            if (string.IsNullOrEmpty(name)) return null;

            var args = node?["arguments"] as JsonObject ?? new JsonObject();
            var dict = new Dictionary<string, object?>();
            foreach (var (k, v) in args) dict[k] = v?.ToString();

            return new FunctionCallContent(Guid.NewGuid().ToString("N"), name, dict);
        }
        catch
        {
            return null;
        }
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

    private record SerializedMessageItem(
        string Role,
        string? Text,
        List<SerializedFunctionCall>? FunctionCalls,
        List<SerializedFunctionResult>? FunctionResults);

    private record SerializedFunctionCall(string? CallId, string Name, object? Arguments);
    private record SerializedFunctionResult(string? CallId, string? Result);
}
