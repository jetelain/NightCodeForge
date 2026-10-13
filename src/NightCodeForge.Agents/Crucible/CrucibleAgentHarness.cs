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
using NightCodeForge.Agents.Optimizations;
using NightCodeForge.Agents.Skills;
using NightCodeForge.Agents.Tools;
using NightCodeForge.Agents.Tools.EditorConfig;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Streaming;
using NightCodeForge.Core.Workflow;

namespace NightCodeForge.Agents.Crucible;

public partial class CrucibleAgentHarness : IAgentHarness
{
    private readonly IChatClient _baseChatClient;
    private readonly IExecutionEnvironment? _executionEnv;
    private readonly IEditorConfigService _editorConfigService;
    private readonly IEditorConfigFileApplier _editorConfigFileApplier;
    private readonly IWebSearchService _webSearchService;
    private readonly IWorkspaceSymbolService _symbolService;
    private readonly SkillsService _skillsService;
    private readonly AgentsMdService _agentsMdService;
    private readonly IAgentActivityStreamer? _activityStreamer;
    private readonly ILogger<CrucibleAgentHarness> _logger;

    public string HarnessId => "crucible";

    [GeneratedRegex(@"<tool_call>\s*({.*?})\s*</tool_call>", RegexOptions.Singleline)]
    private static partial Regex ToolCallTagRegex();

    public CrucibleAgentHarness(
        IChatClient baseChatClient,
        IExecutionEnvironment? executionEnv = null,
        IEditorConfigService? editorConfigService = null,
        IEditorConfigFileApplier? editorConfigFileApplier = null,
        IWebSearchService? webSearchService = null,
        IWorkspaceSymbolService? symbolService = null,
        SkillsService? skillsService = null,
        AgentsMdService? agentsMdService = null,
        IAgentActivityStreamer? activityStreamer = null,
        ILogger<CrucibleAgentHarness>? logger = null)
    {
        _baseChatClient = baseChatClient ?? throw new ArgumentNullException(nameof(baseChatClient));
        _executionEnv = executionEnv;
        _editorConfigService = editorConfigService ?? new EditorConfigService();
        _editorConfigFileApplier = editorConfigFileApplier ?? new EditorConfigFileApplier();
        _webSearchService = webSearchService ?? new MockWebSearchService();
        _symbolService = symbolService ?? new RoslynSymbolIndexProvider();
        _skillsService = skillsService ?? new SkillsService();
        _agentsMdService = agentsMdService ?? new AgentsMdService();
        _activityStreamer = activityStreamer;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<CrucibleAgentHarness>.Instance;
    }

    public async Task<AgentRunResult> RunStageAsync(AgentSessionContext context, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        long promptTokens = 0;
        long completionTokens = 0;
        var sessionId = Guid.NewGuid().ToString("N");

        await BroadcastAsync(sessionId, context.Issue.Id, context.StageId, AgentActivityKind.StageStarted, $"Starting stage '{context.StageId}'", cancellationToken);

        var workspacePath = !string.IsNullOrEmpty(context.WorkspacePath)
            ? context.WorkspacePath
            : (context.Workspace?.RepositoryPath ?? Directory.GetCurrentDirectory());

        // 1. Parse database-driven HarnessConfigurationJson if provided
        var harnessConfig = ParseHarnessConfig(context.Profile.HarnessConfigurationJson);

        // 2. Initialize Tools & Lenient Dispatcher
        var canonicalTools = new CanonicalTools(
            workspacePath,
            _executionEnv,
            _editorConfigService,
            _editorConfigFileApplier,
            _webSearchService);

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

        var circuitBreaker = new ToolLoopCircuitBreaker(context.OptimizationFlags);

        // 3. Discover context: AGENTS.md, Skills, Symbols
        var agentsMdRules = await _agentsMdService.IngestRulesAsync(workspacePath, cancellationToken);
        var projectContext = _agentsMdService.FormatProjectContextSection(agentsMdRules);

        var skills = await _skillsService.DiscoverSkillsAsync(workspacePath, cancellationToken: cancellationToken);
        var skillsCatalog = _skillsService.FormatSkillsCatalogPrompt(skills);

        string discoveredSymbols = string.Empty;
        if (context.OptimizationFlags.DeterministicSymbolMap)
        {
            var issueText = $"{context.Issue.Title} {context.Issue.Description}";
            discoveredSymbols = await _symbolService.GetMatchingSymbolsPromptAsync(workspacePath, issueText, context.OptimizationFlags, cancellationToken);
        }

        // 4. Resolve Active Stage Permitted Tools (Database/Profile Configurable)
        var permittedTools = ResolvePermittedTools(harnessConfig, context.Profile, context.StageId);

        // 5. Build dynamic tool descriptions prompt
        var customToolsPrompt = BuildDynamicToolsPrompt(dispatcher, permittedTools, harnessConfig.ToolDirectives);

        // 6. Build 4-Layer Stabilized Prompt Prefix
        var prefixStabilizer = new PromptPrefixStabilizer();
        var stageDirective = GetStageDirective(harnessConfig, context.StageId);
        var issueScope = $"<issue id=\"{context.Issue.Id}\" title=\"{context.Issue.Title}\">\n{context.Issue.Description}\n</issue>";

        var stabilizedPrefix = prefixStabilizer.BuildStabilizedPrefix(
            stageDirective,
            issueScope,
            projectContext,
            skillsCatalog,
            discoveredSymbols,
            context.OptimizationFlags,
            customToolsPrompt: customToolsPrompt,
            customRulesPrompt: harnessConfig.CustomSystemRules);

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

        // 9. Assemble ChatClient with Zero Data Loss & Token Compaction middlewares
        string latestSerializedState = SerializeSession(chatHistory);

        var persistingClient = new PerServiceCallChatHistoryPersistingChatClient(
            _baseChatClient,
            history =>
            {
                latestSerializedState = SerializeSession(history.ToList());
                return Task.CompletedTask;
            });

        IChatClient activeClient = new ThinkingTraceCompactionChatClient(persistingClient, context.OptimizationFlags);

        // 10. Main Agentic Reasoning Loop with Real-Time Streaming
        int maxTurns = harnessConfig.MaxTurns > 0 ? harnessConfig.MaxTurns : 20;
        int currentTurn = 0;

        while (currentTurn < maxTurns && !cancellationToken.IsCancellationRequested)
        {
            currentTurn++;
            var chatOptions = new ChatOptions
            {
                Temperature = (float?)context.Profile.Temperature,
                Tools = BuildAIFunctionList(permittedTools)
            };

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
                        }
                    }

                    if (!string.IsNullOrEmpty(update.Text))
                    {
                        accumulatedText.Append(update.Text);

                        // Detect thinking tag transitions for streaming activity
                        if (update.Text.Contains("<think>")) isThinking = true;
                        
                        var kind = isThinking ? AgentActivityKind.ThinkingDelta : AgentActivityKind.MessageDelta;
                        await BroadcastAsync(sessionId, context.Issue.Id, context.StageId, kind, update.Text, cancellationToken);

                        if (update.Text.Contains("</think>")) isThinking = false;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error during Crucible streaming LLM invocation");
                stopwatch.Stop();
                await BroadcastAsync(sessionId, context.Issue.Id, context.StageId, AgentActivityKind.Error, ex.Message, cancellationToken);

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

                await BroadcastAsync(sessionId, context.Issue.Id, context.StageId, AgentActivityKind.ToolStarted, $"{canonicalName}: {argsJson}", cancellationToken);

                // Enforce Tool Whitelist Security Gate
                if (!permittedTools.Contains(canonicalName, StringComparer.OrdinalIgnoreCase))
                {
                    var securityReject = $"Security Error: Tool '{canonicalName}' is prohibited in stage '{context.StageId}'. Permitted tools: {string.Join(", ", permittedTools)}";
                    chatHistory.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(call.CallId ?? Guid.NewGuid().ToString("N"), securityReject)]));
                    await BroadcastAsync(sessionId, context.Issue.Id, context.StageId, AgentActivityKind.ToolCompleted, securityReject, cancellationToken);
                    continue;
                }

                // Check Circuit Breaker for duplicate invocations
                var loopCheck = circuitBreaker.EvaluateToolInvocation(canonicalName, call.Arguments, null);
                if (loopCheck.IsBlocked)
                {
                    chatHistory.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(call.CallId ?? Guid.NewGuid().ToString("N"), loopCheck.CorrectiveMessage!)]));
                    await BroadcastAsync(sessionId, context.Issue.Id, context.StageId, AgentActivityKind.ToolCompleted, loopCheck.CorrectiveMessage!, cancellationToken);
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
                catch (HumanInterruptionException interruption)
                {
                    toolSw.Stop();
                    stopwatch.Stop();
                    latestSerializedState = SerializeSession(chatHistory);

                    await BroadcastAsync(sessionId, context.Issue.Id, context.StageId, AgentActivityKind.Interrupted, interruption.Question.Prompt, cancellationToken);

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

                await BroadcastAsync(sessionId, context.Issue.Id, context.StageId, AgentActivityKind.ToolCompleted, $"Completed in {toolSw.ElapsedMilliseconds}ms: {toolResult}", cancellationToken);
            }
        }

        stopwatch.Stop();
        var lastAssistantMessage = chatHistory.LastOrDefault(m => m.Role == ChatRole.Assistant)?.Text;

        await BroadcastAsync(sessionId, context.Issue.Id, context.StageId, AgentActivityKind.StageCompleted, lastAssistantMessage ?? "Stage finished", cancellationToken);

        return AgentRunResult.Completed(
            new AgentRunMetrics(stopwatch.ElapsedMilliseconds, promptTokens, completionTokens, promptTokens + completionTokens),
            commitHash: null,
            updatedState: latestSerializedState,
            summary: lastAssistantMessage ?? "Stage completed successfully.");
    }

    private async Task BroadcastAsync(string sessionId, int issueId, string stageId, AgentActivityKind kind, string content, CancellationToken ct)
    {
        if (_activityStreamer != null)
        {
            var evt = new AgentActivityEvent(sessionId, issueId, stageId, kind, content, DateTime.UtcNow);
            await _activityStreamer.BroadcastAsync(evt, ct);
        }
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

    private static IReadOnlyList<string> ResolvePermittedTools(CrucibleHarnessConfig config, AgentProfile profile, string stageId)
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

        // 3. Stage defaults
        return stageId.ToLowerInvariant() switch
        {
            "triage" or "stage-0" => new[] { "read", "propose_issues" },
            "issue-review" or "stage-1" => new[] { "read", "bash", "web_search", "ask" },
            "implementation" or "stage-2" => new[] { "read", "edit", "write", "bash", "web_search", "ask" },
            "code-review-fix" or "stage-3" => new[] { "read", "edit", "bash", "ask" },
            _ => new[] { "read", "edit", "write", "bash", "web_search", "ask", "propose_issues" }
        };
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
                "Analyze the reported issue against the repository base branch. Determine affected files and acceptance criteria. Call propose_issues to recommend structured backlog items. You have read-only access and cannot modify code or access external networks.",
            "issue-review" or "stage-1" =>
                "Investigate requirements and codebase architecture. Proactively uncover ambiguities. If any design decision is ambiguous, invoke 'ask' immediately. Do not guess.",
            "implementation" or "stage-2" =>
                "Implement clean code according to the approved plan. Run builds and tests incrementally with bash. Fix all compiler errors and test failures. If breaking blockers arise, call 'ask'.",
            "code-review-fix" or "stage-3" =>
                "Review the git diff against the base branch. Identify regressions or style violations and apply precision edits. Ensure builds and tests pass cleanly.",
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

    private record SerializedMessageItem(
        string Role,
        string? Text,
        List<SerializedFunctionCall>? FunctionCalls,
        List<SerializedFunctionResult>? FunctionResults);

    private record SerializedFunctionCall(string? CallId, string Name, object? Arguments);
    private record SerializedFunctionResult(string? CallId, string? Result);
}
