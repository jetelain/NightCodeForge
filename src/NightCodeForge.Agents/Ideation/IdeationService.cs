using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NightCodeForge.Agents.Context;
using NightCodeForge.Agents.Dispatcher;
using NightCodeForge.Agents.Inference;
using NightCodeForge.Agents.Optimizations;
using NightCodeForge.Agents.Tools;
using NightCodeForge.Core.Domain;
using NightCodeForge.Core.Interfaces;
using NightCodeForge.Core.Streaming;

namespace NightCodeForge.Agents.Ideation;

/// <summary>
/// Interactive Ideation and Discovery Agent conversing with developers, exploring codebase structure,
/// and proposing backlog tasks with cross-dependencies.
/// Relies on an AgentProfile (not scheduled as a background pipeline workload).
/// Provides live feedback via IAgentActivityStreamer and streaming updates.
/// </summary>
public partial class IdeationService : IIdeationService
{
    [GeneratedRegex(@"```(?:json)?\s*\[\s*\{.*?\}\s*\]\s*```", RegexOptions.Singleline)]
    private static partial Regex TaskJsonArrayRegex();

    [GeneratedRegex(@"<tool_call>\s*({.*?})\s*</tool_call>", RegexOptions.Singleline)]
    private static partial Regex ToolCallTagRegex();

    private readonly IChatClient? _defaultChatClient;
    private readonly IOllamaChatClientFactory? _chatClientFactory;
    private readonly IExecutionEnvironmentProvider? _envProvider;
    private readonly IExecutionEnvironment? _executionEnv;
    private readonly IWebSearchService _webSearchService;
    private readonly AgentsMdService _agentsMdService;
    private readonly IAgentActivityStreamer? _activityStreamer;
    private readonly IWorkspacePathResolver? _pathResolver;
    private readonly ILogger<IdeationService> _logger;

    public IdeationService(
        IChatClient? defaultChatClient = null,
        IOllamaChatClientFactory? chatClientFactory = null,
        IExecutionEnvironmentProvider? envProvider = null,
        IExecutionEnvironment? executionEnv = null,
        IWebSearchService? webSearchService = null,
        AgentsMdService? agentsMdService = null,
        IAgentActivityStreamer? activityStreamer = null,
        IWorkspacePathResolver? pathResolver = null,
        ILogger<IdeationService>? logger = null)
    {
        if (defaultChatClient == null && chatClientFactory == null)
        {
            throw new ArgumentNullException(nameof(defaultChatClient), "Either defaultChatClient or chatClientFactory must be provided.");
        }

        _defaultChatClient = defaultChatClient;
        _chatClientFactory = chatClientFactory;
        _envProvider = envProvider;
        _executionEnv = executionEnv;
        _webSearchService = webSearchService ?? new MockWebSearchService();
        _agentsMdService = agentsMdService ?? new AgentsMdService();
        _activityStreamer = activityStreamer;
        _pathResolver = pathResolver;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<IdeationService>.Instance;
    }

    /// <summary>
    /// Resolves the effective AgentProfile for ideation, prioritizing explicit profile,
    /// then workspace profile named/id 'ideation', or falling back to CreateDefaultIdeationProfile.
    /// </summary>
    public static AgentProfile ResolveIdeationProfile(Workspace? workspace, AgentProfile? explicitProfile = null)
    {
        if (explicitProfile != null) return explicitProfile;

        var profileFromWorkspace = workspace?.AgentProfiles?.FirstOrDefault(p =>
            string.Equals(p.Id, "ideation", StringComparison.OrdinalIgnoreCase) ||
            p.Name.Contains("ideation", StringComparison.OrdinalIgnoreCase));

        return profileFromWorkspace ?? AgentProfile.CreateDefaultIdeationProfile(workspace?.Id);
    }

    public Task<IdeationResult> ConverseAsync(
        Workspace workspace,
        string userPrompt,
        IReadOnlyList<(string Role, string Content)>? conversationHistory,
        string? customSystemPrompt,
        CancellationToken cancellationToken = default)
    {
        return ConverseAsync(workspace, userPrompt, conversationHistory, null, customSystemPrompt, cancellationToken);
    }

    public async Task<IdeationResult> ConverseAsync(
        Workspace workspace,
        string userPrompt,
        IReadOnlyList<(string Role, string Content)>? conversationHistory = null,
        AgentProfile? agentProfile = null,
        string? customSystemPrompt = null,
        CancellationToken cancellationToken = default)
    {
        IdeationResult? finalResult = null;
        await foreach (var update in ConverseStreamingAsync(
            workspace,
            userPrompt,
            conversationHistory,
            agentProfile,
            customSystemPrompt,
            cancellationToken).ConfigureAwait(false))
        {
            if (update.Kind == IdeationStreamUpdateKind.Completed && update.FinalResult != null)
            {
                finalResult = update.FinalResult;
            }
        }

        return finalResult ?? new IdeationResult(string.Empty, Array.Empty<IdeationProposal>());
    }

    public IAsyncEnumerable<IdeationStreamUpdate> ConverseStreamingAsync(
        Workspace workspace,
        string userPrompt,
        IReadOnlyList<(string Role, string Content)>? conversationHistory,
        string? customSystemPrompt,
        CancellationToken cancellationToken = default)
    {
        return ConverseStreamingAsync(workspace, userPrompt, conversationHistory, null, customSystemPrompt, cancellationToken);
    }

    public async IAsyncEnumerable<IdeationStreamUpdate> ConverseStreamingAsync(
        Workspace workspace,
        string userPrompt,
        IReadOnlyList<(string Role, string Content)>? conversationHistory = null,
        AgentProfile? agentProfile = null,
        string? customSystemPrompt = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var profile = ResolveIdeationProfile(workspace, agentProfile);

        // Resolve client dynamically for the agent profile
        var client = _chatClientFactory != null
            ? _chatClientFactory.CreateClient(profile)
            : (_defaultChatClient ?? throw new InvalidOperationException("No IChatClient or IOllamaChatClientFactory configured."));

        var sessionId = $"ideation-{workspace.Id}-{Guid.NewGuid():N}";
        var metadata = new Dictionary<string, object?>
        {
            ["workspaceId"] = workspace.Id,
            ["agentProfile"] = profile.Id,
            ["model"] = profile.ModelName
        };

        await BroadcastAsync(sessionId, 0, "ideation", AgentActivityKind.StageStarted, "Starting ideation discovery", cancellationToken, metadata);

        var repoPath = !string.IsNullOrEmpty(workspace.RepositoryPath) ? workspace.RepositoryPath : Directory.GetCurrentDirectory();
        var agentsRules = await _agentsMdService.IngestRulesAsync(repoPath, cancellationToken);
        var projectConfig = CodeStackPresets.ResolveConfig(repoPath, null);
        var projectContext = _agentsMdService.FormatProjectContextSection(agentsRules, projectConfig, workspace);

        var basePersona = !string.IsNullOrWhiteSpace(customSystemPrompt)
            ? customSystemPrompt
            : (!string.IsNullOrWhiteSpace(profile.SystemPrompt)
                ? profile.SystemPrompt
                : "You are the NightCodeForge Ideation & Codebase Discovery Agent.\nYour objective is to explore the codebase, discuss architectural features, and help the developer plan new tasks.");

        var systemPrompt = $@"{basePersona}
{projectContext}

When you recommend one or more implementation tasks, format them as a structured JSON array at the end of your response:
```json
[
  {{
    ""title"": ""Descriptive ForgeTask Title"",
    ""description"": ""Detailed technical specification and acceptance criteria"",
    ""priority"": 1,
    ""dependsOnTitles"": []
  }}
]
```";

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt)
        };

        if (conversationHistory != null)
        {
            foreach (var (role, content) in conversationHistory)
            {
                var chatRole = role.Equals("user", StringComparison.OrdinalIgnoreCase) ? ChatRole.User : ChatRole.Assistant;
                messages.Add(new ChatMessage(chatRole, content));
            }
        }

        messages.Add(new ChatMessage(ChatRole.User, userPrompt));

        var permittedTools = profile.PermittedTools?.Count > 0
            ? profile.PermittedTools
            : (profile.Mode == AgentMode.ReadOnly
                ? new[] { "read", "propose_tasks", "web_search" }
                : new[] { "read", "bash", "web_search", "propose_tasks" });

        var effectiveEnv = (workspace != null && _envProvider != null)
            ? _envProvider.GetEnvironment(workspace, profile.SecurityTier)
            : _executionEnv;

        var buildTimeout = workspace?.BuildTimeoutSeconds ?? projectConfig.BuildTimeoutSeconds;
        var testTimeout = workspace?.TestTimeoutSeconds ?? projectConfig.TestTimeoutSeconds;

        var canonicalTools = new CanonicalTools(
            repoPath,
            effectiveEnv,
            webSearchService: _webSearchService,
            buildTimeoutSeconds: buildTimeout,
            testTimeoutSeconds: testTimeout,
            pathResolver: _pathResolver,
            workspaceId: workspace?.Id);

        var dispatcher = new LenientToolDispatcher(canonicalTools);

        var aiTools = new List<AITool>();
        foreach (var t in permittedTools)
        {
            aiTools.Add(AIFunctionFactory.Create(() => "ok", t));
        }

        var chatOptions = new ChatOptions
        {
            ModelId = !string.IsNullOrWhiteSpace(profile.ModelName) ? profile.ModelName : null,
            Temperature = (float?)profile.Temperature ?? 0.3f,
            Tools = aiTools
        };

        var toolResultCompactor = new ToolResultCompactor(profile.OptimizationFlags);
        var fullTextBuilder = new StringBuilder();
        int maxTurns = profile.MaxTurns.HasValue && profile.MaxTurns.Value > 0 ? profile.MaxTurns.Value : 5;
        int currentTurn = 0;

        while (currentTurn < maxTurns && !cancellationToken.IsCancellationRequested)
        {
            currentTurn++;
            toolResultCompactor.Compact(messages);
            var inThinking = false;
            bool streamingFailed = false;
            var functionCalls = new List<FunctionCallContent>();
            var turnTextBuilder = new StringBuilder();

            IAsyncEnumerator<ChatResponseUpdate>? enumerator = null;
            try
            {
                var stream = client.GetStreamingResponseAsync(messages, chatOptions, cancellationToken);
                enumerator = stream.GetAsyncEnumerator(cancellationToken);
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
                        ChatResponseUpdate? update = null;
                        bool hasMore = false;
                        try
                        {
                            hasMore = await enumerator.MoveNextAsync();
                            if (hasMore)
                            {
                                update = enumerator.Current;
                            }
                        }
                        catch (Exception ex) when (ex is NotImplementedException or NotSupportedException)
                        {
                            streamingFailed = true;
                            break;
                        }
                        catch (Exception ex)
                        {
                            await BroadcastAsync(sessionId, 0, "ideation", AgentActivityKind.Error, ex.Message, cancellationToken, metadata);
                            throw;
                        }

                        if (!hasMore || update == null) break;

                        if (update.Contents != null)
                        {
                            foreach (var c in update.Contents)
                            {
                                if (c is FunctionCallContent fc) functionCalls.Add(fc);
                            }
                        }

                        var text = update.Text;
                        if (string.IsNullOrEmpty(text)) continue;

                        turnTextBuilder.Append(text);

                        foreach (var (isThinkingDelta, piece) in ParseStreamDeltas(text, ref inThinking))
                        {
                            if (string.IsNullOrEmpty(piece)) continue;

                            var activityKind = isThinkingDelta ? AgentActivityKind.ThinkingDelta : AgentActivityKind.MessageDelta;
                            await BroadcastAsync(sessionId, 0, "ideation", activityKind, piece, cancellationToken, metadata);

                            var streamKind = isThinkingDelta ? IdeationStreamUpdateKind.ThinkingDelta : IdeationStreamUpdateKind.MessageDelta;
                            yield return new IdeationStreamUpdate(streamKind, TextDelta: piece);
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
                ChatResponse response;
                try
                {
                    response = await client.GetResponseAsync(messages, chatOptions, cancellationToken: cancellationToken);
                }
                catch (Exception ex)
                {
                    await BroadcastAsync(sessionId, 0, "ideation", AgentActivityKind.Error, ex.Message, cancellationToken, metadata);
                    throw;
                }

                if (response.Messages != null)
                {
                    foreach (var msg in response.Messages)
                    {
                        if (msg.Contents != null)
                        {
                            foreach (var c in msg.Contents)
                            {
                                if (c is FunctionCallContent fc) functionCalls.Add(fc);
                            }
                        }
                    }
                }

                var text = response.Text ?? string.Empty;
                turnTextBuilder.Clear();
                turnTextBuilder.Append(text);

                inThinking = false;
                foreach (var (isThinkingDelta, piece) in ParseStreamDeltas(text, ref inThinking))
                {
                    if (string.IsNullOrEmpty(piece)) continue;

                    var activityKind = isThinkingDelta ? AgentActivityKind.ThinkingDelta : AgentActivityKind.MessageDelta;
                    await BroadcastAsync(sessionId, 0, "ideation", activityKind, piece, cancellationToken, metadata);

                    var streamKind = isThinkingDelta ? IdeationStreamUpdateKind.ThinkingDelta : IdeationStreamUpdateKind.MessageDelta;
                    yield return new IdeationStreamUpdate(streamKind, TextDelta: piece);
                }
            }

            var turnText = turnTextBuilder.ToString();
            fullTextBuilder.Append(turnText);

            var assistantMsg = new ChatMessage(ChatRole.Assistant, turnText);
            foreach (var fc in functionCalls)
            {
                assistantMsg.Contents.Add(fc);
            }
            messages.Add(assistantMsg);

            // Also check for raw text fallback tool call emitted by models without native tool tokens
            if (functionCalls.Count == 0 && !string.IsNullOrEmpty(turnText))
            {
                var fallbackCall = TryExtractTextToolCall(turnText);
                if (fallbackCall != null)
                {
                    functionCalls.Add(fallbackCall);
                }
            }

            if (functionCalls.Count == 0)
            {
                break;
            }

            // Process tool calls
            foreach (var call in functionCalls)
            {
                var canonicalName = dispatcher.ResolveToolName(call.Name);
                var argsJson = call.Arguments != null ? JsonSerializer.Serialize(call.Arguments) : "{}";

                await BroadcastAsync(sessionId, 0, "ideation", AgentActivityKind.ToolStarted, $"{canonicalName}: {argsJson}", cancellationToken, metadata);

                string toolResult;
                try
                {
                    toolResult = await dispatcher.DispatchAsync(canonicalName, argsJson, cancellationToken);
                }
                catch (Exception ex)
                {
                    toolResult = $"Error executing tool '{canonicalName}': {ex.Message}";
                }

                messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(call.CallId ?? Guid.NewGuid().ToString("N"), toolResult)]));
                await BroadcastAsync(sessionId, 0, "ideation", AgentActivityKind.ToolCompleted, toolResult, cancellationToken, metadata);
            }
        }

        var fullText = fullTextBuilder.ToString();
        var proposals = ParseProposals(fullText);

        if (canonicalTools.PendingProposals.Count > 0)
        {
            foreach (var p in canonicalTools.PendingProposals)
            {
                if (!proposals.Any(existing => string.Equals(existing.Title, p.Title, StringComparison.OrdinalIgnoreCase)))
                {
                    proposals.Add(new IdeationProposal(p.Title, p.Description, 1, p.AffectedFiles));
                }
            }
        }

        foreach (var proposal in proposals)
        {
            yield return new IdeationStreamUpdate(IdeationStreamUpdateKind.ProposalDiscovered, Proposal: proposal);
        }

        var finalResult = new IdeationResult(fullText, proposals);
        await BroadcastAsync(sessionId, 0, "ideation", AgentActivityKind.StageCompleted, $"Ideation completed with {proposals.Count} proposal(s).", cancellationToken, metadata);

        yield return new IdeationStreamUpdate(IdeationStreamUpdateKind.Completed, FinalResult: finalResult);
    }

    private static List<(bool IsThinking, string Text)> ParseStreamDeltas(string text, ref bool inThinking)
    {
        var results = new List<(bool IsThinking, string Text)>();
        var remaining = text;
        while (!string.IsNullOrEmpty(remaining))
        {
            if (!inThinking)
            {
                var thinkIndex = remaining.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
                if (thinkIndex < 0)
                {
                    results.Add((false, remaining));
                    break;
                }

                if (thinkIndex > 0)
                {
                    results.Add((false, remaining[..thinkIndex]));
                }

                inThinking = true;
                remaining = remaining[(thinkIndex + 7)..];
            }
            else
            {
                var endThinkIndex = remaining.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
                if (endThinkIndex < 0)
                {
                    results.Add((true, remaining));
                    break;
                }

                if (endThinkIndex > 0)
                {
                    results.Add((true, remaining[..endThinkIndex]));
                }

                inThinking = false;
                remaining = remaining[(endThinkIndex + 8)..];
            }
        }

        return results;
    }

    private List<IdeationProposal> ParseProposals(string text)
    {
        var proposals = new List<IdeationProposal>();
        var match = TaskJsonArrayRegex().Match(text);
        if (!match.Success) return proposals;

        var rawJson = match.Value.Trim();
        if (rawJson.StartsWith("```"))
        {
            var firstLine = rawJson.IndexOf('\n');
            var lastTicks = rawJson.LastIndexOf("```");
            if (firstLine > 0 && lastTicks > firstLine)
            {
                rawJson = rawJson.Substring(firstLine + 1, lastTicks - firstLine - 1).Trim();
            }
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<List<IdeationProposalDto>>(rawJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (parsed != null)
            {
                proposals.AddRange(parsed.Select(p => new IdeationProposal(
                    p.Title ?? "Untitled Proposal",
                    p.Description ?? string.Empty,
                    p.Priority <= 0 ? 1 : p.Priority,
                    p.DependsOnTitles)));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse proposed tasks JSON from ideation response.");
        }

        return proposals;
    }

    private async Task BroadcastAsync(
        string sessionId,
        int taskId,
        string stageId,
        AgentActivityKind kind,
        string content,
        CancellationToken ct,
        IReadOnlyDictionary<string, object?>? metadata = null)
    {
        if (_activityStreamer != null)
        {
            var evt = new AgentActivityEvent(sessionId, taskId, stageId, kind, content, DateTime.UtcNow, metadata);
            await _activityStreamer.BroadcastAsync(evt, ct);
        }
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

            var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            if (node?["arguments"] is JsonObject args)
            {
                foreach (var (k, v) in args)
                {
                    dict[k] = ParseJsonNodeValue(v);
                }
            }

            return new FunctionCallContent(Guid.NewGuid().ToString("N"), name, dict);
        }
        catch
        {
            return null;
        }
    }

    private static object? ParseJsonNodeValue(JsonNode? node)
    {
        if (node == null) return null;
        if (node is JsonValue val)
        {
            if (val.TryGetValue<long>(out var l)) return l;
            if (val.TryGetValue<double>(out var d)) return d;
            if (val.TryGetValue<bool>(out var b)) return b;
            if (val.TryGetValue<string>(out var s)) return s;
            return val.ToString();
        }
        if (node is JsonArray arr)
        {
            return arr.Select(ParseJsonNodeValue).ToList();
        }
        if (node is JsonObject obj)
        {
            var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in obj)
            {
                dict[k] = ParseJsonNodeValue(v);
            }
            return dict;
        }
        return node.ToString();
    }

    private class IdeationProposalDto
    {
        public string? Title { get; set; }
        public string? Description { get; set; }
        public int Priority { get; set; }
        public List<string>? DependsOnTitles { get; set; }
    }
}


